using Dalamud.Game.Text;
using ECommons;
using ECommons.DalamudServices;
using Pal.Client.Properties;

namespace Pal.Client.Rendering
{
    /// <summary>
    /// 所有疊字標籤共用的字級、字底與寶箱名稱。名稱的快取只在框架執行緒讀寫。
    /// </summary>
    internal static class OverlayStyle
    {
        /// <summary>實際字級＝設定的「文字大小」× 此倍率；改這裡就是對所有使用者整體放大，不必動他們存的值。</summary>
        public const float BaseScale = 1.3f;

        /// <summary>黑色、約 60% 不透明的字底（0xAABBGGRR）。</summary>
        public const uint LabelBackground = 0x99000000;

        public static float FontScale => P.Config.OverlayFScale * BaseScale;

        /// <summary>0xRRGGBB → ImGui／Splatoon 使用的不透明 0xAABBGGRR。</summary>
        public static uint FromRgb(uint rgb)
            => 0xFF000000u | ((rgb & 0xFFu) << 16) | (rgb & 0xFF00u) | ((rgb >> 16) & 0xFFu);

        private static string? _goldCoffer, _silverCoffer, _bronzeCoffer;

        // Addon 10420～10422 是深層迷宮地圖圖例的「金寶箱／銀寶箱／銅寶箱」；EObjName 的寶箱一律只叫「寶箱」，分不出種類。
        // 字前的遊戲圖示字元沿用原本各寶箱標籤的記號。
        public static string GoldCoffer => _goldCoffer ??= IconLabel(SeIconChar.HighQuality, AddonText(10420, Localization.Overlay_Gold_Coffer));
        public static string SilverCoffer => _silverCoffer ??= IconLabel(SeIconChar.Buff, AddonText(10421, Localization.Overlay_Silver_Coffer));
        public static string BronzeCoffer => _bronzeCoffer ??= IconLabel(SeIconChar.Collectible, AddonText(10422, Localization.Overlay_Bronze_Coffer));

        // 遊戲資料沒有「會變成擬態怪的寶箱」這個名稱。
        public static string MimicCoffer => IconLabel(SeIconChar.Cross, Localization.Overlay_Mimic_Coffer);

        private static string IconLabel(SeIconChar icon, string name) => $"{icon.ToIconChar()} {name}";

        private static string AddonText(uint rowId, string fallback)
        {
            string text = Svc.Data.GetExcelSheet<Lumina.Excel.Sheets.Addon>().GetRowOrDefault(rowId)?.Text.GetText() ?? string.Empty;
            return text.Length > 0 ? text : fallback;
        }
    }
}
