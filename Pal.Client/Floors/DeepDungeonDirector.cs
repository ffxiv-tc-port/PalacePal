using System.Threading;
using ECommons.DalamudServices;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace Pal.Client.Floors
{
    /// <summary>
    /// 取深宮導演的唯一入口。契約：每次呼叫重查、不保存指標；不在深宮回 null；
    /// CS 解不到 <c>EventFramework.Instance</c> 或 <c>GetInstanceContentDirector</c> 也回 null（呼叫端把功能當停用），
    /// 而不是讓 CS 產生碼每幀擲 <see cref="System.InvalidOperationException"/>。
    /// </summary>
    internal static unsafe class DeepDungeonDirector
    {
        private static int _loggedUnresolved;

        public static bool IsResolved =>
            EventFramework.StaticAddressPointers.ppInstance != null &&
            EventFramework.MemberFunctionPointers.GetInstanceContentDirector != null;

        public static InstanceContentDeepDungeon* Get()
        {
            if (!IsResolved)
            {
                if (Interlocked.Exchange(ref _loggedUnresolved, 1) == 0)
                    Svc.Log.Information(
                        "PalacePal:遊戲函式對不上(EventFramework.Instance／GetInstanceContentDirector 解不到位址),魔陶器狀態偵測與小地圖停用。");
                return null;
            }

            var eventFramework = EventFramework.Instance();
            return eventFramework == null ? null : eventFramework->GetInstanceContentDeepDungeon();
        }
    }
}
