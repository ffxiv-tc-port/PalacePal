using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Pal.Client.Floors
{
    // 本檔假設與契約：
    // - 我方確認資料庫，與 PalacePal 自己的 sqlite（含下載、匯入）完全分開，只收本機親眼確認的點。
    // - Observe／FlushIfDirty／FlushIfDeferred／Dispose 只在 framework 執行緒呼叫；IPC 只讀 GetSnapshot() 回的不可變快照。
    // - 寫檔在背景執行緒，只碰 FlushIfDirty 當下深拷貝的快照；同一時間最多一個寫入（.tmp 名稱固定）。
    // - 不依賴 Dalamud，方便離線測試。

    internal enum EConfirmedKind
    {
        Trap = 1,
        Hoard = 2,
    }

    [Flags]
    internal enum EConfirmMethod
    {
        None = 0,
        Triggered = 1,
        Sight = 2,
        Intuition = 4,
        Dug = 8,
    }

    /// <summary>觀測從哪裡來。<see cref="Other"/> 不會被記錄。</summary>
    internal enum EObservationSource
    {
        ObjectTable,
        TrapVfx,
        Other,
        ActionEffect,
    }

    internal static class ConfirmationRules
    {
        /// <summary>
        /// 一次觀測算不算「我方確認」。<see cref="EConfirmMethod.None"/> ＝不記。
        /// 陷阱實體只在全景生效時採信（沒用全景時物件表會不會出現陷阱實體離線證不了）；
        /// 寶藏實體只在感知寶藏生效或本層已挖到時採信。
        /// </summary>
        public static EConfirmMethod Classify(EConfirmedKind kind, EObservationSource source,
            bool sightActive, bool intuitionActive, bool hoardFoundThisFloor)
        {
            switch (kind)
            {
                case EConfirmedKind.Trap when source is EObservationSource.TrapVfx or EObservationSource.ActionEffect:
                    return EConfirmMethod.Triggered;
                case EConfirmedKind.Trap when source == EObservationSource.ObjectTable && sightActive:
                    return EConfirmMethod.Sight;
                case EConfirmedKind.Hoard when source == EObservationSource.ObjectTable && hoardFoundThisFloor:
                    return EConfirmMethod.Dug;
                case EConfirmedKind.Hoard when source == EObservationSource.ObjectTable && intuitionActive:
                    return EConfirmMethod.Intuition;
                default:
                    return EConfirmMethod.None;
            }
        }

        /// <summary>把 ABGR 顏色的 alpha 乘上 <paramref name="factor"/>（0～1）。0 色（不顯示）原樣回傳。</summary>
        public static uint ScaleAlpha(uint color, float factor)
        {
            if (color == 0)
                return 0;

            factor = Math.Clamp(factor, 0f, 1f);
            uint alpha = (uint)MathF.Round((color >> 24) * factor);
            return (color & 0x00FFFFFF) | (alpha << 24);
        }
    }

    internal sealed class ConfirmedLocation
    {
        public EConfirmedKind Type { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public EConfirmMethod Methods { get; set; }
        public DateTime FirstConfirmedUtc { get; set; }
        public DateTime LastConfirmedUtc { get; set; }
        public int Count { get; set; }

        [JsonIgnore] public Vector3 Position => new(X, Y, Z);
    }

    internal sealed class ConfirmedLocationFile
    {
        public int Version { get; set; } = 1;
        public Dictionary<ushort, List<ConfirmedLocation>> Territories { get; set; } = new();
    }

    internal enum EObserveResult
    {
        None,
        Updated,
        Added,
    }

    /// <summary>IPC 用的不可變快照：每個區域各一組陷阱／寶藏座標。</summary>
    internal sealed class ConfirmedLocationSnapshot
    {
        public static readonly ConfirmedLocationSnapshot Empty = new(new Dictionary<ushort, (Vector3[], Vector3[])>());

        private readonly Dictionary<ushort, (Vector3[] Traps, Vector3[] Hoards)> _byTerritory;

        public ConfirmedLocationSnapshot(Dictionary<ushort, (Vector3[] Traps, Vector3[] Hoards)> byTerritory)
            => _byTerritory = byTerritory;

        public IReadOnlyList<Vector3> Get(ushort territory, EConfirmedKind kind)
        {
            if (!_byTerritory.TryGetValue(territory, out var entry))
                return Array.Empty<Vector3>();
            return kind == EConfirmedKind.Trap ? entry.Traps : entry.Hoards;
        }
    }

    internal sealed class ConfirmedLocationStore : IDisposable
    {
        public const string FileName = "confirmed-locations.json";

        /// <summary>
        /// 同區域、同類型在這個距離（y）內視為同一點。依本機資料定：同一生成點的抖動最大約 1.1y，
        /// 相異生成點最近約 2.2y。
        /// </summary>
        public const float MergeDistance = 1.5f;

        /// <summary>卸載時最多等進行中的背景寫入這麼久；逾時就不再寫。</summary>
        public static readonly TimeSpan DisposeWait = TimeSpan.FromSeconds(2);

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };

        private readonly string _path;
        private readonly Action<string> _logInfo;
        private readonly Action<string> _logWarning;
        private readonly Func<DateTime> _utcNow;
        private readonly ConfirmedLocationFile _data;
        private readonly HashSet<ConfirmedLocation> _touchedThisVisit = new();

        private ushort _visitTerritory;
        private int _visitFloor = -1;

        // _version 每次改動 +1（主執行緒）；_persistedVersion 只在寫入成功後更新（背景）。兩者不同＝還有沒落地的改動，
        // 寫入失敗時不更新 ⇒ 下一次 FlushIfDirty 自然重寫。
        private int _version;
        private volatile int _persistedVersion;
        private Task? _pendingWrite;
        private bool _flushDeferred;
        private bool _disposed;
        private volatile ConfirmedLocationSnapshot _snapshot = ConfirmedLocationSnapshot.Empty;

        public ConfirmedLocationStore(string path, Action<string> logInfo, Action<string> logWarning,
            Func<DateTime>? utcNow = null)
        {
            _path = path;
            _logInfo = logInfo;
            _logWarning = logWarning;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
            _data = Load();
            RebuildSnapshot();
        }

        public string FilePath => _path;

        /// <summary>任何執行緒都可讀；內容不可變。</summary>
        public ConfirmedLocationSnapshot GetSnapshot() => _snapshot;

        public int CountFor(ushort territory)
            => _data.Territories.TryGetValue(territory, out var list) ? list.Count : 0;

        public IReadOnlyList<ConfirmedLocation> GetLocations(ushort territory)
            => _data.Territories.TryGetValue(territory, out var list) ? list : Array.Empty<ConfirmedLocation>();

        public bool IsConfirmed(ushort territory, EConfirmedKind kind, Vector3 position)
            => Find(territory, kind, position) != null;

        /// <summary>
        /// 記一次觀測。同一次樓層造訪內同一點只算一次；本身不寫檔。
        /// 回 <see cref="EObserveResult.Added"/> 時呼叫端要在同一幀呼叫 <see cref="FlushIfDirty"/>（新點當幀交給背景寫入，
        /// 同幀多點只寫一次）；次數／時間的更新等換區、卸載時一起寫。
        /// </summary>
        public EObserveResult Observe(ushort territory, int floor, EConfirmedKind kind, Vector3 position,
            EConfirmMethod method)
        {
            if (method == EConfirmMethod.None)
                return EObserveResult.None;

            if (territory != _visitTerritory || floor != _visitFloor)
            {
                _visitTerritory = territory;
                _visitFloor = floor;
                _touchedThisVisit.Clear();
            }

            DateTime now = _utcNow();
            ConfirmedLocation? existing = Find(territory, kind, position);
            if (existing != null)
            {
                bool changed = false;
                if ((existing.Methods & method) == 0)
                {
                    existing.Methods |= method;
                    changed = true;
                }

                if (_touchedThisVisit.Add(existing))
                {
                    existing.Count++;
                    existing.LastConfirmedUtc = now;
                    changed = true;
                }

                if (!changed)
                    return EObserveResult.None;

                _version++;
                return EObserveResult.Updated;
            }

            var created = new ConfirmedLocation
            {
                Type = kind,
                X = position.X,
                Y = position.Y,
                Z = position.Z,
                Methods = method,
                FirstConfirmedUtc = now,
                LastConfirmedUtc = now,
                Count = 1,
            };

            if (!_data.Territories.TryGetValue(territory, out var list))
                _data.Territories[territory] = list = new List<ConfirmedLocation>();
            list.Add(created);
            _touchedThisVisit.Add(created);
            _version++;
            RebuildSnapshot();

            _logInfo($"PalacePal:我方確認資料庫新增 1 點（區域 {territory}、第 {floor} 層、{kind}、方式 {method}、"
                     + $"座標 {position.X:F1},{position.Y:F1},{position.Z:F1}），本區共 {list.Count} 點。");
            return EObserveResult.Added;
        }

        /// <summary>
        /// 有沒落地的改動就深拷貝一份交給背景寫（主執行緒不序列化、不碰檔案）。
        /// 前一次背景寫還沒完成就不另開，記下延後，由 <see cref="FlushIfDeferred"/> 在它完成後補送最新內容。
        /// </summary>
        public void FlushIfDirty()
        {
            if (_disposed || _version == _persistedVersion)
                return;

            if (_pendingWrite is { IsCompleted: false })
            {
                _flushDeferred = true;
                return;
            }

            _flushDeferred = false;
            int version = _version;
            ConfirmedLocationFile snapshot = CloneData();
            _pendingWrite = Task.Run(() =>
            {
                if (TrySave(snapshot))
                    _persistedVersion = version;
            });
        }

        /// <summary>每幀呼叫：只補送因背景寫未完成而延後的那一次；寫入失敗不在這裡重試（等下一個新點或換區）。</summary>
        public void FlushIfDeferred()
        {
            if (_flushDeferred && _pendingWrite is { IsCompleted: true })
                FlushIfDirty();
        }

        /// <summary>有界地等進行中的背景寫；回 true＝已經沒有進行中的寫入。</summary>
        public bool WaitForPendingWrite(TimeSpan timeout)
        {
            Task? pending = _pendingWrite;
            if (pending == null)
                return true;
            try
            {
                return pending.Wait(timeout);
            }
            catch (AggregateException)
            {
                return true;
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            // 逾時仍在寫的那份還握著 .tmp，再同步寫會互相踩 ⇒ 放棄這次，原檔維持最後一次完整寫入的內容。
            if (!WaitForPendingWrite(DisposeWait))
            {
                _logWarning($"PalacePal:我方確認資料庫的背景寫入 {DisposeWait.TotalSeconds:F0} 秒內沒有完成，卸載時不再等、也不再寫；"
                            + "最後一批變動可能沒有存進檔案。");
                return;
            }

            if (_version != _persistedVersion && TrySave(CloneData()))
                _persistedVersion = _version;
        }

        private ConfirmedLocationFile CloneData()
        {
            var clone = new ConfirmedLocationFile { Version = _data.Version };
            foreach (var (territory, list) in _data.Territories)
            {
                var copy = new List<ConfirmedLocation>(list.Count);
                foreach (var l in list)
                {
                    copy.Add(new ConfirmedLocation
                    {
                        Type = l.Type,
                        X = l.X,
                        Y = l.Y,
                        Z = l.Z,
                        Methods = l.Methods,
                        FirstConfirmedUtc = l.FirstConfirmedUtc,
                        LastConfirmedUtc = l.LastConfirmedUtc,
                        Count = l.Count,
                    });
                }

                clone.Territories[territory] = copy;
            }

            return clone;
        }

        private ConfirmedLocation? Find(ushort territory, EConfirmedKind kind, Vector3 position)
        {
            if (!_data.Territories.TryGetValue(territory, out var list))
                return null;

            ConfirmedLocation? best = null;
            float bestDistance = MergeDistance;
            foreach (var location in list)
            {
                if (location.Type != kind)
                    continue;

                float distance = Vector3.Distance(location.Position, position);
                if (distance <= bestDistance)
                {
                    best = location;
                    bestDistance = distance;
                }
            }

            return best;
        }

        private void RebuildSnapshot()
        {
            var byTerritory = new Dictionary<ushort, (Vector3[], Vector3[])>(_data.Territories.Count);
            foreach (var (territory, list) in _data.Territories)
            {
                var traps = new List<Vector3>();
                var hoards = new List<Vector3>();
                foreach (var location in list)
                    (location.Type == EConfirmedKind.Trap ? traps : hoards).Add(location.Position);
                byTerritory[territory] = (traps.ToArray(), hoards.ToArray());
            }

            _snapshot = new ConfirmedLocationSnapshot(byTerritory);
        }

        private static int TotalCount(ConfirmedLocationFile data)
        {
            int total = 0;
            foreach (var list in data.Territories.Values)
                total += list.Count;
            return total;
        }

        private ConfirmedLocationFile Load()
        {
            if (!File.Exists(_path))
                return new ConfirmedLocationFile();

            try
            {
                var loaded = JsonSerializer.Deserialize<ConfirmedLocationFile>(File.ReadAllBytes(_path), JsonOptions);
                if (loaded?.Territories != null)
                    return loaded;
                throw new InvalidDataException("檔案內容是空的");
            }
            catch (Exception e)
            {
                // 讀不懂的檔案改名保留，不覆蓋使用者的資料。
                string aside = $"{_path}.unreadable-{DateTime.UtcNow:yyyyMMddHHmmss}";
                try
                {
                    File.Move(_path, aside);
                }
                catch (Exception moveError)
                {
                    aside = $"（改名失敗：{moveError.Message}）";
                }

                _logWarning($"PalacePal:我方確認資料庫讀取失敗（{e.Message}），原檔改名為 {aside}，從空的開始。");
                return new ConfirmedLocationFile();
            }
        }

        // 背景執行緒與 Dispose 都會呼叫；只讀參數 data（快照），不碰 _data。永不擲例外。
        private bool TrySave(ConfirmedLocationFile data)
        {
            string temp = _path + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(data, JsonOptions);
                using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    fs.Write(bytes);
                    fs.Flush(true);
                }

                var verify = JsonSerializer.Deserialize<ConfirmedLocationFile>(File.ReadAllBytes(temp), JsonOptions);
                int expected = TotalCount(data);
                int actual = verify?.Territories != null ? TotalCount(verify) : 0;
                if (actual != expected)
                    throw new InvalidDataException($"暫存檔讀回 {actual} 點，預期 {expected} 點");

                File.Move(temp, _path, overwrite: true);
                return true;
            }
            catch (Exception e)
            {
                _logWarning($"PalacePal:我方確認資料庫寫入失敗（{e.Message}），原檔不變，下次有新點時重試。");
                try
                {
                    File.Delete(temp);
                }
                catch
                {
                    // 暫存檔刪不掉不影響原檔。
                }

                return false;
            }
        }
    }
}
