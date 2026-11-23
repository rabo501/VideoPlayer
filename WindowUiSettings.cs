using System;
using System.Collections.Generic;

namespace VideoPlayer
{
    internal sealed class WindowUiSettings
    {
        public double Width { get; set; } = 1540;
        public double Height { get; set; } = 940;
        public double Left { get; set; } = 80;
        public double Top { get; set; } = 60;
        public bool IsMaximized { get; set; }
        public bool ToolsVisible { get; set; } = true;
        public double ToolsWidth { get; set; } = 420;
        public int SelectedTabIndex { get; set; }
        public int SpeedIndex { get; set; } = 2;
        public int ScaleIndex { get; set; }
    }
}
