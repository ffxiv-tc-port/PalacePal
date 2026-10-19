using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Pal.Client.Floors
{
    /// <summary>
    /// 可以從任何執行緒新增／清空／列舉的點位容器，另外提供一份「內容沒變就重用」的快照，
    /// 給每一幀都要走訪整包的路徑用（直接列舉 ConcurrentBag 每次都會複製一份）。
    /// </summary>
    /// <remarks>
    /// 版本號在每次變動「完成之後」才加一，快照記的是「拍之前」讀到的版本，
    /// 所以快照的內容只可能比它標的版本新，不會把已經完成的變動漏掉。
    /// </remarks>
    internal sealed class LocationBag<T> : IReadOnlyCollection<T>
        where T : class
    {
        private readonly ConcurrentBag<T> _items = new();
        private readonly object _refreshLock = new();
        private int _version;
        private volatile Cached? _cached;

        public int Count => _items.Count;

        public void Add(T item)
        {
            _items.Add(item);
            Interlocked.Increment(ref _version);
        }

        public void Clear()
        {
            _items.Clear();
            Interlocked.Increment(ref _version);
            _cached = null;
        }

        /// <summary>
        /// 目前內容的唯讀快照，順序與列舉相同。內容沒變時回同一份、不配置；
        /// 回傳的範圍之後不會再被改寫，但也不會跟著容器更新。
        /// </summary>
        public ReadOnlySpan<T> Snapshot()
        {
            Cached? cached = _cached;
            if (cached == null || cached.Version != Volatile.Read(ref _version))
                cached = Refresh();

            return cached.Items;
        }

        // 重拍要排隊：兩條執行緒各拍各的再各自存回去時，較舊的那份可能蓋掉較新的。
        private Cached Refresh()
        {
            lock (_refreshLock)
            {
                int version = Volatile.Read(ref _version);
                Cached? cached = _cached;
                if (cached == null || cached.Version != version)
                    _cached = cached = new Cached(version, _items.ToArray());

                return cached;
            }
        }

        public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        private sealed class Cached
        {
            public Cached(int version, T[] items)
            {
                Version = version;
                Items = items;
            }

            public int Version { get; }
            public T[] Items { get; }
        }
    }
}
