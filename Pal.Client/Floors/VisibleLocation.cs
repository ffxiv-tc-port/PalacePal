using System.Numerics;
using Pal.Client.Database;
using Pal.Common;

namespace Pal.Client.Floors
{
    /// <summary>
    /// 這一幀在物件表裡看到的一個陷阱／寶藏／寶箱。只是值；要記下來的時候才建立對應的
    /// <see cref="PersistentLocation"/> 或 <see cref="EphemeralLocation"/>。
    /// </summary>
    /// <param name="Source">陷阱與寶藏是怎麼被看到的；寶箱用不到，一律 <c>Unknown</c>。</param>
    internal readonly record struct VisibleLocation(
        MemoryLocation.EType Type,
        Vector3 Position,
        ClientLocation.ESource Source = ClientLocation.ESource.Unknown)
    {
        /// <summary>與點位之間的相等判斷同義：同種類、同一格。</summary>
        public bool Matches(MemoryLocation location)
            => Type == location.Type && PalaceMath.IsNearlySamePosition(Position, location.Position);
    }
}
