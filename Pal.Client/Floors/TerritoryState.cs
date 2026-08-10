using Dalamud.Game.ClientState;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using Pal.Client.Configuration;
using Pal.Common;

namespace Pal.Client.Floors
{
    public sealed class TerritoryState
    {
        private readonly IClientState _clientState;
        private readonly ICondition _condition;

        public TerritoryState(IClientState clientState, ICondition condition)
        {
            _clientState = clientState;
            _condition = condition;
        }

        public uint LastTerritory { get; set; }
        public PomanderState PomanderOfSight { get; set; } = PomanderState.Inactive;
        public PomanderState PomanderOfIntuition { get; set; } = PomanderState.Inactive;

        // 以下三個由 PomanderSensor 每幀從 InstanceContentDeepDungeon 結構寫入,
        // 與上面兩個(來自系統訊息)是 OR 的關係:任一邊偵測到就算數。
        // 感知器停用時三個恆為 false,判斷式會完全退回原本只看系統訊息的行為。
        public bool SafetyActiveFromMemory { get; set; }
        public bool SightActiveFromMemory { get; set; }
        public bool IntuitionActiveFromMemory { get; set; }

        /// <summary>
        /// 本層是否該隱藏「潛在陷阱」標記。
        /// 咒印解除(陷阱已被清掉)與全景(陷阱已顯形為實體)各自有獨立開關,
        /// 兩者都受既有的總開關 Traps.OnlyVisibleAfterPomander 節制。
        /// </summary>
        public bool ShouldHideTraps(IPalacePalConfiguration configuration)
        {
            if (!configuration.DeepDungeons.Traps.OnlyVisibleAfterPomander)
                return false;

            bool safetyUsed = PomanderOfSight == PomanderState.PomanderOfSafetyUsed || SafetyActiveFromMemory;
            bool sightUsed = PomanderOfSight == PomanderState.Active || SightActiveFromMemory;

            return (safetyUsed && P.Config.HideTrapsOnSafety)
                   || (sightUsed && P.Config.HideTrapsOnSight);
        }

        /// <summary>
        /// 本層是否該隱藏「潛在受詛咒藏寶箱」標記。
        /// PomanderOfIntuition 為 FoundOnCurrentFloor 時同樣要隱藏(本層寶藏已經挖到了),
        /// 這是既有行為,原封不動。
        /// </summary>
        public bool ShouldHideHoardCoffers(IPalacePalConfiguration configuration)
        {
            if (!configuration.DeepDungeons.HoardCoffers.OnlyVisibleAfterPomander)
                return false;

            if (!P.Config.HideHoardOnIntuition)
                return false;

            return PomanderOfIntuition != PomanderState.Inactive || IntuitionActiveFromMemory;
        }

        public bool IsInDeepDungeon() =>
            _clientState.IsLoggedIn
            && _condition[ConditionFlag.InDeepDungeon]
            && typeof(ETerritoryType).IsEnumDefined((uint)_clientState.TerritoryType);

    }

    public enum PomanderState
    {
        Inactive,
        Active,
        FoundOnCurrentFloor,
        PomanderOfSafetyUsed,
    }
}
