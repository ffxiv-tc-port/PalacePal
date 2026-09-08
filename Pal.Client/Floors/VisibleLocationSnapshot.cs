using System;
using System.Collections.Generic;
using System.Numerics;

namespace Pal.Client.Floors
{
    /// <summary>
    /// 「上一個 framework 幀，客戶端物件表裡真的存在的深宮實體」的不可變快照。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 🔑 <b>「看得到」的精確定義</b>（消費端的圖例必須照這個意思寫）：
    /// 這份快照的內容 ＝ <c>FrameworkService.GetRelevantGameObjects()</c> 在上一幀掃描
    /// <c>IObjectTable</c> 的事件物件區段（索引 246 起）時，<c>BaseId</c> 命中陷阱／
    /// 受詛咒藏寶箱／銀寶箱／金寶箱清單的那些物件的 <c>Position</c>。也就是說：
    /// </para>
    /// <list type="bullet">
    /// <item>不是「在畫面上」—— 沒有做視錐、遮擋或朝向判斷。玩家背對它、隔著一道牆，
    /// 只要物件還在物件表裡就算數。</item>
    /// <item>沒有外掛自己加的距離門檻 —— 範圍完全由<b>遊戲自己的物件串流距離</b>決定，
    /// 走遠到遊戲把物件收掉，它下一幀就從這份清單消失。</item>
    /// <item>陷阱要<b>已現形</b>才有事件物件（用了全景魔陶器，或已經被踩過）。沒現形的陷阱
    /// 在物件表裡根本不存在，所以永遠不會出現在這裡 —— 這也是它與
    /// <c>GetTrapLocations</c>／<c>GetConfirmedTrapLocations</c> 最大的差別。</item>
    /// <item>受詛咒藏寶箱同理：要用了感知寶藏（或已被挖出）才會有實體。</item>
    /// <item>剛被踩爆的陷阱（<c>GameHooks</c> 送進 <c>NextUpdateObjects</c> 的那些）在被取出的
    /// 那一幀也算「看得到」—— 這是刻意的：快照直接沿用外掛自己拿去畫圖的同一份清單，
    /// 不另外算一份會分岔的。</item>
    /// </list>
    /// <para>
    /// ⚠️ 這份是<b>一幀的快照</b>，不是累積值，也不是資料庫。它回答的是「這一趟這一層現在
    /// 真的看得到什麼」，而 <c>GetTrapLocations</c> 那一組回答的是「這個 territory 的十層
    /// 累積下來有哪些候選生成點」。兩者不可互相取代。
    /// </para>
    /// <para>
    /// 🔴 <b>執行緒</b>：只在 framework 執行緒建立，建立完才用單次整體參考指派公開出去
    /// （<see cref="FloorService.VisibleLocations"/> 是 <c>volatile</c> 欄位）。
    /// 公開之後所有欄位與陣列內容都不再變動，所以 IPC 執行緒讀它是安全的。
    /// 🔴 但陣列本身仍然是可變型別 —— <b>絕對不要把 <see cref="Traps"/> 這些陣列直接交給
    /// 別的外掛</b>，IPC 端點一律複製成新的 <c>List</c> 再回傳。
    /// </para>
    /// </remarks>
    internal sealed class VisibleLocationSnapshot
    {
        private VisibleLocationSnapshot(
            uint territoryType,
            byte floor,
            long capturedAtTicks,
            Vector3[] traps,
            Vector3[] hoards,
            Vector3[] silverCoffers,
            Vector3[] goldCoffers)
        {
            TerritoryType = territoryType;
            Floor = floor;
            CapturedAtTicks = capturedAtTicks;
            Traps = traps;
            Hoards = hoards;
            SilverCoffers = silverCoffers;
            GoldCoffers = goldCoffers;
        }

        /// <summary>拍下這份快照時所在的 territory。查詢別的 territory 時一律當作「不知道」。</summary>
        public uint TerritoryType { get; }

        /// <summary>
        /// 拍下這份快照時的深宮樓層，取自 <c>PomanderSensor.CurrentFloor</c>。
        /// <c>0</c> ＝ 樓層本身還沒讀到（不是「第 0 層」）。
        /// </summary>
        public byte Floor { get; }

        /// <summary><see cref="Environment.TickCount64"/>，用來算「這份快照多舊了」。</summary>
        public long CapturedAtTicks { get; }

        public Vector3[] Traps { get; }
        public Vector3[] Hoards { get; }
        public Vector3[] SilverCoffers { get; }
        public Vector3[] GoldCoffers { get; }

        /// <summary>
        /// 認得的四種點位各回一份陣列；認不得的種類回空陣列（不擲例外 —— 這條路徑的終點是
        /// 別的外掛的 IPC 呼叫，寧可回空也不要把例外送過去）。
        /// </summary>
        public Vector3[] ForType(MemoryLocation.EType type)
            => type switch
            {
                MemoryLocation.EType.Trap => Traps,
                MemoryLocation.EType.Hoard => Hoards,
                MemoryLocation.EType.SilverCoffer => SilverCoffers,
                MemoryLocation.EType.GoldCoffer => GoldCoffers,
                _ => Array.Empty<Vector3>(),
            };

        /// <summary>
        /// 從這一幀算出來的可見清單拍一份快照。🔴 只准在 framework 執行緒上呼叫。
        /// </summary>
        /// <remarks>
        /// 只抄 <see cref="Vector3"/>（實值型別，複製即脫鉤），不保存任何
        /// <see cref="PersistentLocation"/> 參考 —— 那些物件會被
        /// <c>FloorService.MergePersistentLocations</c> 原地改 <c>Seen</c>，
        /// 存參考等於讓 IPC 讀到會變的東西。
        /// </remarks>
        public static VisibleLocationSnapshot Capture(
            uint territoryType,
            byte floor,
            IReadOnlyList<PersistentLocation> persistentLocations,
            IReadOnlyList<EphemeralLocation> ephemeralLocations)
        {
            return new VisibleLocationSnapshot(
                territoryType,
                floor,
                Environment.TickCount64,
                Extract(persistentLocations, MemoryLocation.EType.Trap),
                Extract(persistentLocations, MemoryLocation.EType.Hoard),
                Extract(ephemeralLocations, MemoryLocation.EType.SilverCoffer),
                Extract(ephemeralLocations, MemoryLocation.EType.GoldCoffer));
        }

        /// <summary>
        /// 兩趟走訪：先數再填，沒有命中時完全不配置（回共用的空陣列）。
        /// 深宮大多數幀四種都是 0 或個位數，所以這裡每幀的配置量趨近於零。
        /// </summary>
        private static Vector3[] Extract<T>(IReadOnlyList<T> source, MemoryLocation.EType type)
            where T : MemoryLocation
        {
            int count = 0;
            for (int i = 0; i < source.Count; i++)
            {
                if (source[i].Type == type)
                    count++;
            }

            if (count == 0)
                return Array.Empty<Vector3>();

            Vector3[] result = new Vector3[count];
            int n = 0;
            for (int i = 0; i < source.Count && n < result.Length; i++)
            {
                if (source[i].Type == type)
                    result[n++] = source[i].Position;
            }

            // 兩趟之間清單不會變（同一個執行緒、同一個呼叫），但真的少填時寧可截短也不要
            // 回傳一段預設的 Vector3.Zero —— 那會被消費端畫成地圖原點的假標記。
            if (n != result.Length)
                Array.Resize(ref result, n);

            return result;
        }
    }
}
