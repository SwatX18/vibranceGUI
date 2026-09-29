using System;

namespace vibrance.GUI.NVIDIA
{
    public enum ScalingTarget
    {
        Gpu,
        Display
    }

    internal interface IDisplayScalingDevice
    {
        bool TryGetScaling(string gdiDeviceName, out int nvScaling);
        bool TrySetScaling(string gdiDeviceName, int nvScaling);
    }

    internal static class NvScalingMap
    {
        internal static ScalingTarget? Classify(int nvScaling)
        {
            return null;
        }

        internal static int? Retarget(int currentNvScaling, ScalingTarget target)
        {
            return null;
        }
    }

    internal enum ScalingControlState
    {
        Unavailable,
        Reading,
        Ready,
        Writing
    }

    internal sealed class DisplayScalingController
    {
        private readonly IDisplayScalingDevice _device;
        private readonly Func<string> _primaryDeviceName;
        private readonly Func<bool> _isGameResolutionApplied;

        internal DisplayScalingController(IDisplayScalingDevice device, Func<string> primaryDeviceName, Func<bool> isGameResolutionApplied)
        {
            _device = device;
            _primaryDeviceName = primaryDeviceName;
            _isGameResolutionApplied = isGameResolutionApplied;
        }

        internal ScalingControlState State
        {
            get { return ScalingControlState.Unavailable; }
        }

        internal ScalingTarget? Current
        {
            get { return null; }
        }

        internal event EventHandler StateChanged;

        internal bool Refresh()
        {
            return false;
        }

        internal bool Request(ScalingTarget target)
        {
            return false;
        }
    }
}
