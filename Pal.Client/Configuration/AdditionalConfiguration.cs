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
    }
}
