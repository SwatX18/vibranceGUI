using System;
using System.Runtime.InteropServices;
using vibrance.GUI.common;

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

    /// <summary>
    /// The NV_SCALING values NVIDIA's own nvapi.h defines (its lines 508-528), classified by who does
    /// the scaling. "Balanced" modes (1, 6, 7) let the display scale; "Force GPU" modes (2, 5, 3, 8)
    /// make the GPU do it. 0 (DEFAULT, "no change") and 255 (CUSTOMIZED) are neither, so they, and any
    /// value a future driver adds, classify as null and leave the control unavailable rather than
    /// guessing.
    /// </summary>
    internal static class NvScalingMap
    {
        // nvapi.h NV_SCALING, verbatim.
        internal const int GpuScalingToClosest = 1;                 // Balanced  - Full Screen
        internal const int GpuScalingToNative = 2;                  // Force GPU - Full Screen
        internal const int GpuScanoutToNative = 3;                  // Force GPU - Centered\No Scaling
        internal const int GpuScalingToAspectScanoutToNative = 5;   // Force GPU - Aspect Ratio
        internal const int GpuScalingToAspectScanoutToClosest = 6;  // Balanced  - Aspect Ratio
        internal const int GpuScanoutToClosest = 7;                 // Balanced  - Centered\No Scaling
        internal const int GpuIntegerAspectScaling = 8;             // Force GPU - Integer Scaling

        internal static ScalingTarget? Classify(int nvScaling)
        {
            switch (nvScaling)
            {
                case GpuScalingToNative:
                case GpuScanoutToNative:
                case GpuScalingToAspectScanoutToNative:
                case GpuIntegerAspectScaling:
                    return ScalingTarget.Gpu;
                case GpuScalingToClosest:
                case GpuScalingToAspectScanoutToClosest:
                case GpuScanoutToClosest:
                    return ScalingTarget.Display;
                default:
                    return null;
            }
        }

        /// <summary>
        /// The value that keeps the scaling mode (full screen / aspect / no scaling) but moves who
        /// performs it. Returns currentNvScaling when it is already on the requested side, and null
        /// when there is no equivalent: unknown values, and integer scaling, which only the GPU can do.
        /// </summary>
        internal static int? Retarget(int currentNvScaling, ScalingTarget target)
        {
            ScalingTarget? side = Classify(currentNvScaling);
            if (side == null)
            {
                return null;
            }
            if (side.Value == target)
            {
                return currentNvScaling;
            }

            if (target == ScalingTarget.Display)
            {
                switch (currentNvScaling)
                {
                    case GpuScalingToNative: return GpuScalingToClosest;
                    case GpuScalingToAspectScanoutToNative: return GpuScalingToAspectScanoutToClosest;
                    case GpuScanoutToNative: return GpuScanoutToClosest;
                    default: return null; // integer scaling has no display-side counterpart
                }
            }

            switch (currentNvScaling)
            {
                case GpuScalingToClosest: return GpuScalingToNative;
                case GpuScalingToAspectScanoutToClosest: return GpuScalingToAspectScanoutToNative;
                case GpuScanoutToClosest: return GpuScanoutToNative;
                default: return null;
            }
        }
    }

    /// <summary>
    /// Production IDisplayScalingDevice over vibranceDLL's vibrance_get/setDisplayScaling. The DLL is
    /// already extracted and loaded by Program.cs, and NvAPI is initialised by the NVIDIA proxy, before
    /// anything constructs this. Both natives return an NvAPI status (0 = OK), not the 0/1 of the
    /// older exports.
    /// </summary>
    internal sealed class NvapiDisplayScalingDevice : IDisplayScalingDevice
    {
        [DllImport(
            "vibranceDLL.dll",
            EntryPoint = "vibrance_getDisplayScaling",
            CallingConvention = CallingConvention.Cdecl,
            CharSet = CharSet.Ansi)]
        internal static extern int getDisplayScaling(string gdiDisplayName, out int outScaling);

        [DllImport(
            "vibranceDLL.dll",
            EntryPoint = "vibrance_setDisplayScaling",
            CallingConvention = CallingConvention.Cdecl,
            CharSet = CharSet.Ansi)]
        internal static extern int setDisplayScaling(string gdiDisplayName, int scaling);

        public bool TryGetScaling(string gdiDeviceName, out int nvScaling)
        {
            nvScaling = 0;
            if (string.IsNullOrEmpty(gdiDeviceName))
            {
                return false;
            }
            int status;
            try
            {
                status = getDisplayScaling(gdiDeviceName, out nvScaling);
            }
            catch (Exception ex)
            {
                // DllNotFoundException / EntryPointNotFoundException (a stale DLL) and the like.
                DisplayScalingController.SafeLog("NvAPI display scaling read threw " + ex.GetType().Name + ": " + ex.Message);
                nvScaling = 0;
                return false;
            }
            if (status != 0)
            {
                DisplayScalingController.SafeLog(string.Format("NvAPI display scaling read for {0} failed with status {1}", gdiDeviceName, status));
                nvScaling = 0;
                return false;
            }
            return true;
        }

        public bool TrySetScaling(string gdiDeviceName, int nvScaling)
        {
            if (string.IsNullOrEmpty(gdiDeviceName))
            {
                return false;
            }
            try
            {
                int status = setDisplayScaling(gdiDeviceName, nvScaling);
                if (status != 0)
                {
                    DisplayScalingController.SafeLog(string.Format("NvAPI display scaling write ({0}) for {1} failed with status {2}", nvScaling, gdiDeviceName, status));
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                DisplayScalingController.SafeLog("NvAPI display scaling write threw " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }
    }

    internal enum ScalingControlState
    {
        Unavailable,
        Reading,
        Ready,
        Writing
    }

    /// <summary>
    /// State machine behind the "Perform scaling on: GPU / Display" toggle. Not thread-safe: drive it
    /// from the UI thread. It never throws; a device that throws, or reports a value this code cannot
    /// classify, leaves it Unavailable.
    /// </summary>
    internal sealed class DisplayScalingController
    {
        private readonly IDisplayScalingDevice _device;
        private readonly Func<string> _primaryDeviceName;
        private readonly Func<bool> _isGameResolutionApplied;

        private ScalingControlState _state = ScalingControlState.Unavailable;
        private ScalingTarget? _current;
        private int? _lastRawValue;

        internal DisplayScalingController(IDisplayScalingDevice device, Func<string> primaryDeviceName, Func<bool> isGameResolutionApplied)
        {
            _device = device;
            _primaryDeviceName = primaryDeviceName;
            _isGameResolutionApplied = isGameResolutionApplied;
        }

        internal ScalingControlState State
        {
            get { return _state; }
        }

        internal ScalingTarget? Current
        {
            get { return _current; }
        }

        /// <summary>
        /// The raw NV_SCALING value of the last successful read, or null when the last read failed.
        /// For a tooltip explaining why the control is unavailable (for example a value such as 0 or
        /// 255 that has no GPU/Display meaning).
        /// </summary>
        internal int? LastRawValue
        {
            get { return _lastRawValue; }
        }

        internal event EventHandler StateChanged;

        /// <summary>Re-reads the driver. True when the control ends up Ready.</summary>
        internal bool Refresh()
        {
            if (_state == ScalingControlState.Reading || _state == ScalingControlState.Writing)
            {
                return false; // a read or write is already in flight (device re-entered us)
            }
            return ReadFromDriver();
        }

        /// <summary>
        /// Asks the driver to move the primary display's scaling to the given side, keeping the scaling
        /// mode. False, and nothing written, unless the control is Ready, the target differs from
        /// Current, no game resolution is applied and an equivalent mode exists. Otherwise writes
        /// exactly once and re-reads, so Current always reflects what the driver reports afterwards;
        /// the result is whether the driver accepted the write.
        /// </summary>
        internal bool Request(ScalingTarget target)
        {
            try
            {
                if (_state != ScalingControlState.Ready || _current == null || _current.Value == target)
                {
                    return false;
                }
                if (_isGameResolutionApplied())
                {
                    return false;
                }
                if (!_lastRawValue.HasValue)
                {
                    return false;
                }
                int? nvTarget = NvScalingMap.Retarget(_lastRawValue.Value, target);
                if (nvTarget == null)
                {
                    return false;
                }
                string deviceName = _primaryDeviceName();
                if (string.IsNullOrEmpty(deviceName))
                {
                    return false;
                }

                SetState(ScalingControlState.Writing, _current);
                bool accepted = false;
                try
                {
                    accepted = _device.TrySetScaling(deviceName, nvTarget.Value);
                }
                catch (Exception ex)
                {
                    SafeLog("Display scaling write threw " + ex.GetType().Name + ": " + ex.Message);
                }

                // Always re-read, success or not: the driver is the source of truth.
                ReadFromDriver();
                return accepted;
            }
            catch (Exception ex)
            {
                SafeLog("Display scaling request failed: " + ex.Message);
                SetState(ScalingControlState.Unavailable, null);
                return false;
            }
        }

        private bool ReadFromDriver()
        {
            try
            {
                SetState(ScalingControlState.Reading, _current);

                int raw = 0;
                bool read = false;
                string deviceName = _primaryDeviceName();
                if (!string.IsNullOrEmpty(deviceName))
                {
                    try
                    {
                        read = _device.TryGetScaling(deviceName, out raw);
                    }
                    catch (Exception ex)
                    {
                        SafeLog("Display scaling read threw " + ex.GetType().Name + ": " + ex.Message);
                        read = false;
                    }
                }

                ScalingTarget? side = read ? NvScalingMap.Classify(raw) : null;
                _lastRawValue = read ? (int?)raw : null;
                if (side == null)
                {
                    SetState(ScalingControlState.Unavailable, null);
                    return false;
                }
                SetState(ScalingControlState.Ready, side);
                return true;
            }
            catch (Exception ex)
            {
                SafeLog("Display scaling refresh failed: " + ex.Message);
                _lastRawValue = null;
                SetState(ScalingControlState.Unavailable, null);
                return false;
            }
        }

        private void SetState(ScalingControlState state, ScalingTarget? current)
        {
            bool changed = _state != state || _current != current;
            _state = state;
            _current = current;
            if (!changed)
            {
                return;
            }
            EventHandler handler = StateChanged;
            if (handler == null)
            {
                return;
            }
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                SafeLog("Display scaling StateChanged handler threw " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        /// <summary>VibranceGUI.Log can throw when the log file is busy; a lost line must never break this.</summary>
        internal static void SafeLog(string message)
        {
            try
            {
                VibranceGUI.Log(message);
            }
            catch (Exception)
            {
            }
        }
    }
}
