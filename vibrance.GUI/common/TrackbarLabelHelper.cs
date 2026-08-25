using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using vibrance.GUI.NVIDIA;

namespace vibrance.GUI.common
{
    internal class TrackbarLabelHelper
    {
        public static string ResolveVibranceLabelLevel(GraphicsAdapter graphicsAdapter, int value)
        {
            switch (graphicsAdapter)
            {
                case GraphicsAdapter.Nvidia:
                    return NvidiaVibranceValueWrapper.Find(value).Percentage;
                case GraphicsAdapter.Amd:
                    return ResolvePercentageLabelLevel(value);
                case GraphicsAdapter.Ambiguous:
                case GraphicsAdapter.Unknown:
                default:
                    return "";
            }
        }

        public static string ResolveBrightnessLabelLevel(int value)
        {
            return ResolvePercentageLabelLevel(value);
        }

        public static string ResolveContrastLabelLevel(int value)
        {
            return ResolvePercentageLabelLevel(value);
        }

        public static string ResolveGammaLabelLevel(int value)
        {
            return string.Format("{0:F2}", (double)value / 100);
        }

        public static int ClampToTrackBarRange(TrackBar trackBar, int value)
        {
            //TrackBar.Value throws for values outside of its range and the settings file is not validated on load
            return Math.Min(Math.Max(value, trackBar.Minimum), trackBar.Maximum);
        }

        private static string ResolvePercentageLabelLevel(int value)
        {
            return string.Format("{0}%", value);
        }
    }
}
