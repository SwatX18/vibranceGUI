using System;
using System.Runtime.InteropServices;

namespace vibrance.GUI.common
{
    /// <summary>
    /// Durable monitor identity for the persisted vibrance restore record (D4's abnormal-exit
    /// half). Screen.DeviceName ("\\.\DISPLAY3") CANNOT key that record: it is derived from
    /// \Device\VideoN in HKLM\HARDWARE\DEVICEMAP\VIDEO, and HKLM\HARDWARE is a volatile hive with
    /// no backing file (its own hivelist entry is empty, unlike every other machine hive, which
    /// does map to a real file on disk) - it is rebuilt every boot, numbered by whatever order the
    /// adapters happened to enumerate in that boot, so "\\.\DISPLAY3" today and "\\.\DISPLAY3"
    /// after the next restart are not guaranteed to be the same physical monitor. A record keyed
    /// on it would silently restore the WRONG display, or none at all, after almost any reboot.
    ///
    /// The durable key is the monitor's device interface path, e.g.
    /// "\\?\DISPLAY#BNQ78E5#5&amp;a2a2cd5&amp;1&amp;UID4354#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}",
    /// obtained from EnumDisplayDevices with EDD_GET_DEVICE_INTERFACE_NAME - it is backed by the
    /// on-disk SYSTEM hive and was confirmed stable across a month of reboots on the machine this
    /// fix was developed on. EDID alone (manufacturer + product code) is NOT sufficient: two
    /// identical monitors of the same model report identical EDID and are separable only by the
    /// connector UID this interface path carries.
    ///
    /// Both directions here are port-bound, not panel-bound: moving a monitor to a different port,
    /// or to a different GPU, changes its interface path, so TryResolveDeviceName treats it as a
    /// different, newly-attached monitor afterward. That is an accepted limitation of this
    /// identity, not a bug in either method below - see VibranceRestoreHelper's own replay-gate
    /// comment for how a monitor that no longer resolves is handled.
    ///
    /// Neither method ever throws: a missing or currently-detached monitor is an entirely ordinary
    /// answer on this path (a record written before a reboot, replayed after one, naming a
    /// monitor that is not plugged in this time), not an error condition.
    /// </summary>
    internal static class MonitorIdentity
    {
        // EDD_GET_DEVICE_INTERFACE_NAME - without this flag DISPLAY_DEVICE.DeviceID instead holds
        // a PnP hardware id ("MONITOR\BNQ78E5\..."), which is not unique across two identical
        // monitors either. With it, DeviceID holds the interface path this class is built around.
        private const uint EddGetDeviceInterfaceName = 0x00000001;

        // Nothing real comes close to this - mirrors GraphicsAdapterHelper.MaxEnumeratedDisplayDevices,
        // the same defensive bound for the same reason: a driver that never fails the enumeration
        // must not be able to spin TryResolveDeviceName's loop forever.
        private const uint MaxEnumeratedDisplayDevices = 64;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplayDevices(string lpDevice, uint iDevNum, ref DisplayDevice lpDisplayDevice, uint dwFlags);

        // Byte-identical layout to GraphicsAdapterHelper's own private DISPLAY_DEVICE - duplicated
        // here, not shared, because that struct is private to that class and this is meant to be
        // its own small, self-contained seam file (house style: one file per seam, e.g.
        // IDisplayModeDevice/ResolutionHelper, INvidiaVibranceDevice/NvidiaDynamicVibranceProxy).
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DisplayDevice
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceString;
            public uint StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceKey;
        }

        /// <summary>
        /// deviceName ("\\.\DISPLAYn") -> that display's monitor device interface path, or null
        /// when deviceName is null/empty, the call fails, or the display reports no interface
        /// name (a virtual/mirror driver, most likely). Reads the FIRST monitor
        /// (iMonitorNum = 0) EnumDisplayDevices reports for deviceName - an ordinary physical
        /// display reports exactly one; nothing in this codebase's model of a display (Screen,
        /// one entry per "\\.\DISPLAYn") anticipates more than that.
        /// </summary>
        internal static string TryGetMonitorId(string deviceName)
        {
            if (string.IsNullOrEmpty(deviceName))
            {
                return null;
            }

            try
            {
                DisplayDevice monitor = new DisplayDevice();
                monitor.cb = Marshal.SizeOf(typeof(DisplayDevice));
                if (!EnumDisplayDevices(deviceName, 0, ref monitor, EddGetDeviceInterfaceName))
                {
                    return null;
                }

                string interfaceName = monitor.DeviceID;
                return string.IsNullOrEmpty(interfaceName) ? null : interfaceName;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// The reverse lookup: monitorId (as TryGetMonitorId returned it) -> whichever
        /// "\\.\DISPLAYn" currently names that monitor, or null when no currently attached
        /// display resolves to it - an entirely ordinary answer for a monitor that has been
        /// unplugged, moved to a different port, or simply is not attached yet this boot. Walks
        /// every currently enumerated "\\.\DISPLAYn" (the same top-level EnumDisplayDevices(null,
        /// i, ...) enumeration GraphicsAdapterHelper.GetDisplayAdapters() uses) and resolves each
        /// one's own id via TryGetMonitorId, rather than trying to reverse the interface path
        /// string directly - there is no cheaper OS call that goes straight from an interface path
        /// to a "\\.\DISPLAYn" name.
        /// </summary>
        internal static string TryResolveDeviceName(string monitorId)
        {
            if (string.IsNullOrEmpty(monitorId))
            {
                return null;
            }

            try
            {
                for (uint deviceIndex = 0; deviceIndex < MaxEnumeratedDisplayDevices; deviceIndex++)
                {
                    DisplayDevice device = new DisplayDevice();
                    device.cb = Marshal.SizeOf(typeof(DisplayDevice));
                    if (!EnumDisplayDevices(null, deviceIndex, ref device, 0))
                    {
                        break;
                    }

                    string candidateDeviceName = device.DeviceName;
                    if (string.IsNullOrEmpty(candidateDeviceName))
                    {
                        continue;
                    }

                    string candidateMonitorId = TryGetMonitorId(candidateDeviceName);
                    if (string.Equals(candidateMonitorId, monitorId, StringComparison.OrdinalIgnoreCase))
                    {
                        return candidateDeviceName;
                    }
                }
            }
            catch (Exception)
            {
                return null;
            }

            return null;
        }
    }
}
