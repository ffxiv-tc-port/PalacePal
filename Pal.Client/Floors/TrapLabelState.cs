namespace Pal.Client.Floors
{
    /// <summary>陷阱種類標籤要不要（重新）送給 Splatoon。只在 framework 執行緒用。</summary>
    internal sealed class TrapLabelState
    {
        public bool Shown { get; private set; }

        /// <summary>存過設定：下一次 <see cref="Next"/> 一定照新設定重送。</summary>
        public bool Dirty { get; set; }

        /// <returns>true＝要把 show 送給 Splatoon。</returns>
        public bool Next(bool show, bool splatoonReconnected)
        {
            bool refresh = Dirty || (show && splatoonReconnected);
            if (show == Shown && !refresh)
                return false;

            Dirty = false;
            Shown = show;
            return true;
        }

        /// <returns>true＝換區前有送過，要撤掉。</returns>
        public bool Leave()
        {
            bool had = Shown;
            Shown = false;
            return had;
        }
    }
}
