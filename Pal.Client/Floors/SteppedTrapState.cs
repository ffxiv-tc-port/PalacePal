using System.Collections.Generic;
using System.Numerics;

namespace Pal.Client.Floors
{
    /// <summary>
    /// 本層已觸發過的陷阱位置；只在 framework 執行緒用，只存座標、不存檔。
    /// 判準與 BossModReborn 的「踩過的陷阱本層不再閃避」相同：陷阱一層只觸發一次，觸發點水平 2 米內的標記都算同一個陷阱。
    /// </summary>
    internal sealed class SteppedTrapState
    {
        public const float Radius = 2f;

        private readonly List<Vector3> _positions = new();

        public int Count => _positions.Count;

        /// <summary>記錄時的樓層（0＝不知道）。</summary>
        public byte Floor { get; private set; }

        /// <returns>true＝新的觸發點；離既有觸發點 <see cref="Radius"/> 內的視為同一個陷阱，不重複記。</returns>
        public bool Add(Vector3 position, byte floor)
        {
            if (Floor != floor)
            {
                _positions.Clear();
                Floor = floor;
            }

            if (Covers(position))
                return false;

            _positions.Add(position);
            return true;
        }

        public bool Covers(Vector3 position)
        {
            foreach (var p in _positions)
            {
                float dx = p.X - position.X;
                float dz = p.Z - position.Z;
                if (dx * dx + dz * dz <= Radius * Radius)
                    return true;
            }

            return false;
        }

        /// <summary>樓層已知且與記錄時不同就清空；樓層暫時讀成 0（載入中）不算換層。</summary>
        /// <returns>清掉的觸發點數。</returns>
        public int OnFloor(byte floor)
        {
            if (floor == 0 || floor == Floor || _positions.Count == 0)
                return 0;

            return Clear();
        }

        /// <returns>清掉的觸發點數。</returns>
        public int Clear()
        {
            int n = _positions.Count;
            _positions.Clear();
            Floor = 0;
            return n;
        }
    }
}
