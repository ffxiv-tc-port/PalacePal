using ECommons;
using ECommons.Configuration;
using Dalamud.Bindings.ImGui;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;

namespace Pal.Client.Configuration
{
    public class AdditionalConfiguration
    {
        public bool GoldText = true;
        public bool SilverText = true;
        public bool DisplayExit = false;
        public bool DisplayExitOnlyActive = false;
        public bool ExitText = true;
        public bool TrapColorFilled = false;
        public bool BronzeShow = false;
        public bool BronzeFill = false;
        public bool BronzeText = true;
        public Vector4 BronzeColor = 0xFF185AE1.ToVector4();
        public Vector4 MimicColor = 0xFF0000FF.ToVector4();
        public Vector4 TrapColor = 0xFF0000FF.ToVector4();
        public Vector4 ExitColor = 0xFFFF00C8.ToVector4();
        public float OverlayFScale = 1.3f;

        // 魔陶器感知:三個效果各自獨立的開關,預設全開(維持既有行為)。
        // 這些是既有總開關(Traps/HoardCoffers 的 OnlyVisibleAfterPomander)底下的細項,
        // 總開關關掉時這三個不生效。新欄位在既有使用者的 JSON 裡不存在,
        // 反序列化會保留此處的初始值,所以既有使用者也吃得到「預設開」。
        public bool HideTrapsOnSafety = true;
        public bool HideTrapsOnSight = true;
        public bool HideHoardOnIntuition = true;

        // 我方沒確認過的陷阱／寶藏標示的透明度倍率（0.05～1）；1＝跟確認過的一樣實色。
        public float UnconfirmedMarkerAlpha = 0.3f;

        // Palantir 下載點至少要幾次回報才顯示（0＝全部顯示）；預設值照 Palantir（陷阱 4、寶藏 2）。
        public int CommunityTrapThreshold = 4;
        public int CommunityHoardThreshold = 2;

        // 用了魔陶器：全景之後，在已現形的陷阱上方標出種類名稱。
        public bool TrapKindLabels = true;

        // 陷阱一觸發，觸發點 2 米內的陷阱標記本層不再畫（只影響繪製，不影響記錄與 IPC）。
        public bool HideSteppedTraps = true;

        // 再生石塚／再生燈籠／再生裝置的標示；顏色照 Palantir 的 Return（#99E699）。
        public bool DisplayReturn = false;
        public bool ReturnText = true;
        public Vector4 ReturnColor = new(0.60f, 0.90f, 0.60f, 1f);

        // 深層迷宮本層小地圖浮動視窗；預設關（現行行為，Palantir 的獨立小地圖視窗也是預設關），其餘照 Palantir 的預設。
        public bool MinimapShow = false;
        public float MinimapScale = 1f;
        public float MinimapOpacity = 0.8f;
        public bool MinimapLocked = false;
        public bool MinimapClickThrough = false;
        public bool MinimapParty = true;
        public bool MinimapActiveOnly = true;
    }
}
