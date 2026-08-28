using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace vibrance.GUI.common
{
    /// <summary>
    /// Whether a display currently is, or is not, running in HDR - as Windows itself reports it
    /// via QueryDisplayConfig/DisplayConfigGetDeviceInfo, never anything this application infers
    /// on its own. Unknown is what a detection failure produces (the API missing entirely, an
    /// unreadable source name, both the type 15 AND the type 9 advanced-colour query failing for
    /// one target) - see RealHdrStateReader.ReadAll's own comment for exactly which failure maps
    /// to which outcome. Every consumer of this enum must treat Unknown exactly like Sdr: a
    /// detection that cannot answer must reproduce today's behaviour (no separate HDR level ever
    /// applied), never HDR's - see HdrVibranceHelper.ResolveIngameLevel.
    /// </summary>
    internal enum HdrDisplayState
    {
        Sdr,
        Hdr,
        Unknown
    }

    /// <summary>
    /// One display's HDR state and capability, keyed by the same \\.\DISPLAYn name the rest of
    /// the application already uses (Screen.DeviceName, VibranceRestoreHelper's work-list, the
    /// _windowsResolutionSettings dictionary, ...).
    /// </summary>
    internal struct HdrDisplayInfo
    {
        internal string DeviceName;
        internal HdrDisplayState State;
        internal bool IsHdrCapable;

        // Diagnostic only - State/IsHdrCapable never depend on which one actually answered, and no
        // decision in this codebase branches on it. Exists so a sweep can be reported honestly
        // (see HdrVibranceFixture's diagnostics section) now that type 15 vs type 9 is decided
        // fresh per target, per sweep, with no persistent state to ask instead.
        internal bool AnsweredByType15;
    }

    /// <summary>
    /// The seam between HdrStateTracker and the real QueryDisplayConfig/DisplayConfigGetDeviceInfo
    /// sweep below - same shape as IForegroundWindowReader: RealHdrStateReader (below) is the only
    /// production implementation; HdrVibranceFixture supplies a fake so HdrStateTracker's TTL
    /// cache, change detection and Unknown fallback can all be exercised without any display,
    /// HDR-capable or not, ever being touched.
    /// </summary>
    internal interface IHdrStateReader
    {
        /// <summary>
        /// One entry per active display path this call can resolve both a source name and an HDR
        /// state for. NEVER throws and NEVER returns null - every failure, from the API being
        /// entirely absent on this version of Windows down to a single display's advanced-colour
        /// query failing, is reported either as an empty list (a whole-sweep failure) or as that
        /// one display simply being missing from an otherwise non-empty list (a single-display
        /// failure) - see RealHdrStateReader below for which is which. A display named here but
        /// missing from a later sweep, or never named at all, reads as HdrDisplayState.Unknown to
        /// every caller (see HdrStateTracker.GetState).
        /// </summary>
        List<HdrDisplayInfo> ReadAll();

        /// <summary>
        /// Latched false, permanently, the first time this reader learns the DisplayConfig API
        /// itself does not exist on this machine (pre-Windows-7 - QueryDisplayConfig,
        /// GetDisplayConfigBufferSizes and DisplayConfigGetDeviceInfo are Windows 7+ exports; 1709
        /// is when the type 9 constant arrived, not when these functions did) - true otherwise,
        /// including after a transient QueryDisplayConfig failure, so that one gets retried on the
        /// next sweep instead of being treated as "this machine can never answer".
        /// </summary>
        bool IsAvailable { get; }
    }

    /// <summary>
    /// The only production IHdrStateReader. Enumerates every active display path via
    /// QueryDisplayConfig, resolves each path's source device name (DISPLAYCONFIG_DEVICE_INFO_GET_
    /// SOURCE_NAME, type 1) and HDR state (type 15 first, falling back to type 9 - see
    /// TryGetColorInfo's own comment for why the order is load-bearing and must never be
    /// reversed), and never lets a native failure - missing entry point, missing DLL, or anything
    /// else - escape past this class. See ReadAll's own comment for exactly what each failure mode
    /// produces.
    /// </summary>
    internal class RealHdrStateReader : IHdrStateReader
    {
        private const uint QdcOnlyActivePaths = 2;
        private const int ErrorSuccess = 0;
        private const int ErrorInsufficientBuffer = 122;

        private const uint DeviceInfoGetSourceName = 1;
        private const uint DeviceInfoGetAdvancedColorInfo = 9;

        // DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO_2's real value - confirmed against the
        // Windows SDK header itself, not just this file's own earlier assumption of 14:
        // C:\Program Files (x86)\Windows Kits\10\Include\10.0.26100.0\um\wingdi.h:3042-3049
        //   DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO    = 9,
        //   DISPLAYCONFIG_DEVICE_INFO_SET_RESERVED1              = 14,
        //   DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO_2  = 15,
        //   DISPLAYCONFIG_DEVICE_INFO_SET_HDR_STATE              = 16,
        // 14 is DISPLAYCONFIG_DEVICE_INFO_SET_RESERVED1 - an undocumented SETTER, not this getter.
        // Passing 14 into DisplayConfigGetDeviceInfo is harmless (Get validates the type and
        // rejects it with ERROR_INVALID_PARAMETER) but is not the intended call - do NOT "fix"
        // this back to 14. HdrVibranceFixture's L10 pins this value against this same citation.
        private const uint DeviceInfoGetAdvancedColorInfo2 = 15;

        // header.size must be the size of the ENCLOSING struct DisplayConfigGetDeviceInfo is being
        // asked to fill in, not the header's own size - computed once via Marshal.SizeOf, never a
        // hardcoded literal, so it cannot silently drift from the real struct size after an edit.
        // HdrVibranceFixture's L1-L6 already assert Marshal.SizeOf on these three struct types
        // directly (84 / 32 / 36) - that is what catches a broken struct layout, not this comment.
        // L7-L9 exist for a narrower, different failure: this field itself being replaced with a
        // wrong-but-plausible hardcoded literal instead of staying computed, which L1-L6 alone
        // would not catch since they never read this field at all.
        private static readonly uint SourceDeviceNameSize = (uint)Marshal.SizeOf(typeof(DisplayConfigSourceDeviceName));
        private static readonly uint AdvancedColorInfoSize = (uint)Marshal.SizeOf(typeof(DisplayConfigAdvancedColorInfo));
        private static readonly uint AdvancedColorInfo2Size = (uint)Marshal.SizeOf(typeof(DisplayConfigAdvancedColorInfo2));

        [DllImport("user32.dll")]
        private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

        [DllImport("user32.dll")]
        private static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements,
            [In, Out] DisplayConfigPathInfo[] pathArray, ref uint numModeInfoArrayElements,
            [In, Out] DisplayConfigModeInfo[] modeInfoArray, IntPtr currentTopologyId);

        // Three overloads of the one native DisplayConfigGetDeviceInfo, disambiguated by C# on the
        // struct type of the single by-ref parameter - the native function itself takes only a
        // DISPLAYCONFIG_DEVICE_INFO_HEADER*, which every one of these structs starts with. There
        // is no A/W pair to worry about here (see the struct comments below for the one place that
        // DOES matter, DisplayConfigSourceDeviceName's CharSet).
        [DllImport("user32.dll")]
        private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigSourceDeviceName requestPacket);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigAdvancedColorInfo requestPacket);

        [DllImport("user32.dll")]
        private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigAdvancedColorInfo2 requestPacket);

        // Latched permanently false the first time EntryPointNotFoundException or
        // DllNotFoundException proves the whole API is absent (pre-Windows-7 - see IsAvailable's
        // own comment for why that is the real boundary, not 1709) - every ReadAll call after that
        // returns empty without paying another exception. Instance, not static: exactly one
        // RealHdrStateReader is ever constructed in production (HdrStateTracker's default reader),
        // so this behaves like a process-wide latch in practice without forcing every
        // fixture-constructed instance in HdrVibranceFixture's diagnostics section to share state
        // with each other. In practice this can only ever latch via TryGetSourceDeviceName, which
        // calls the same export on the same code path before TryGetColorInfo is ever reached - a
        // missing export fails there first, so type 15 vs type 9 never even gets a chance to matter
        // on a machine this old.
        private bool _isAvailable = true;

        // See ReadAll's catch (Exception) block - suppresses a full stack trace on every
        // occurrence of a non-specific failure, logging the full detail once and an abbreviated
        // one-liner after that.
        private bool _hasLoggedGenericFailure;

        public bool IsAvailable
        {
            get { return _isAvailable; }
        }

        public List<HdrDisplayInfo> ReadAll()
        {
            if (!_isAvailable)
            {
                return new List<HdrDisplayInfo>();
            }

            try
            {
                return ReadAllCore();
            }
            catch (EntryPointNotFoundException)
            {
                _isAvailable = false;
                Program.LogSafely("HDR state detection is unavailable on this version of Windows - QueryDisplayConfig/DisplayConfigGetDeviceInfo entry point missing from user32.dll.");
                return new List<HdrDisplayInfo>();
            }
            catch (DllNotFoundException)
            {
                _isAvailable = false;
                Program.LogSafely("HDR state detection is unavailable on this version of Windows - user32.dll could not be loaded.");
                return new List<HdrDisplayInfo>();
            }
            catch (Exception ex)
            {
                // Deliberately broad, and deliberately not a padding catch: in PR 2 this sweep
                // runs inside a native WinEvent callback frame - the exact hazard Program.LogSafely
                // itself exists for (see its own comment). Nothing here may ever throw back into
                // that frame, regardless of cause.
                //
                // Full ex.ToString() only on the first occurrence (review nitpick): PR 2 calls this
                // on every foreground change and every poll-timer tick, so a persistent failure
                // logging a full stack trace every time would write on the order of tens of
                // thousands of lines a day into vibranceGUI's one unbounded, unrotated log file. A
                // later occurrence still gets a one-line record, just not the full trace again.
                if (!_hasLoggedGenericFailure)
                {
                    _hasLoggedGenericFailure = true;
                    Program.LogSafely("HDR state detection failed: " + ex);
                }
                else
                {
                    Program.LogSafely("HDR state detection failed again: " + ex.GetType().Name + ": " + ex.Message);
                }
                return new List<HdrDisplayInfo>();
            }
        }

        private List<HdrDisplayInfo> ReadAllCore()
        {
            // At most one retry: QueryDisplayConfig can legitimately report ERROR_INSUFFICIENT_
            // BUFFER when the topology changed between the sizing call and the query call (a
            // monitor was plugged/unplugged in that window) - re-running the whole sequence once
            // picks up the new size. A second consecutive ERROR_INSUFFICIENT_BUFFER is treated as
            // a real failure, not chased forever.
            for (int attempt = 0; attempt < 2; attempt++)
            {
                uint numPaths;
                uint numModes;
                int sizeResult = GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out numPaths, out numModes);
                if (sizeResult != ErrorSuccess || numPaths == 0)
                {
                    return new List<HdrDisplayInfo>();
                }

                DisplayConfigPathInfo[] pathArray = new DisplayConfigPathInfo[numPaths];
                DisplayConfigModeInfo[] modeArray = new DisplayConfigModeInfo[numModes];
                uint numPathsForQuery = numPaths;
                uint numModesForQuery = numModes;
                int queryResult = QueryDisplayConfig(QdcOnlyActivePaths, ref numPathsForQuery, pathArray,
                    ref numModesForQuery, modeArray, IntPtr.Zero);

                if (queryResult == ErrorInsufficientBuffer)
                {
                    continue;
                }
                if (queryResult != ErrorSuccess)
                {
                    return new List<HdrDisplayInfo>();
                }

                return ReadPaths(pathArray, numPathsForQuery);
            }

            return new List<HdrDisplayInfo>();
        }

        private List<HdrDisplayInfo> ReadPaths(DisplayConfigPathInfo[] pathArray, uint numPaths)
        {
            List<HdrDisplayInfo> results = new List<HdrDisplayInfo>();
            HashSet<string> seenDeviceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < numPaths; i++)
            {
                DisplayConfigPathInfo path = pathArray[i];

                string deviceName;
                if (!TryGetSourceDeviceName(path.sourceInfo.adapterId, path.sourceInfo.id, out deviceName) ||
                    string.IsNullOrEmpty(deviceName))
                {
                    continue;
                }

                // Clone mode: several targets can share one source - the first target this loop
                // reaches for a given source wins, and every later one is skipped. Documented here
                // because it is otherwise invisible: nothing about DISPLAYCONFIG_PATH_INFO itself
                // flags a path as "clone", so there is nothing else to branch on.
                if (seenDeviceNames.Contains(deviceName))
                {
                    continue;
                }

                HdrDisplayState state;
                bool isHdrCapable;
                bool answeredByType15;
                if (!TryGetColorInfo(path.targetInfo.adapterId, path.targetInfo.id, out state, out isHdrCapable, out answeredByType15))
                {
                    // Neither type 15 nor type 9 could answer for this one display - omit it
                    // entirely, so HdrStateTracker.GetState reads it back as Unknown rather than a
                    // stale or fabricated Sdr/Hdr value.
                    continue;
                }

                seenDeviceNames.Add(deviceName);
                HdrDisplayInfo info = new HdrDisplayInfo();
                info.DeviceName = deviceName;
                info.State = state;
                info.IsHdrCapable = isHdrCapable;
                info.AnsweredByType15 = answeredByType15;
                results.Add(info);
            }

            return results;
        }

        private bool TryGetSourceDeviceName(DisplayConfigLuid adapterId, uint id, out string deviceName)
        {
            DisplayConfigSourceDeviceName packet = new DisplayConfigSourceDeviceName();
            packet.header.type = DeviceInfoGetSourceName;
            packet.header.size = SourceDeviceNameSize;
            packet.header.adapterId = adapterId;
            packet.header.id = id;

            int result = DisplayConfigGetDeviceInfo(ref packet);
            if (result != ErrorSuccess)
            {
                deviceName = null;
                return false;
            }

            deviceName = packet.viewGdiDeviceName;
            return true;
        }

        // Why type 15 (GET_ADVANCED_COLOR_INFO_2) is tried first, and must never be reversed to
        // type 9 first: on Windows 24H2 (this machine, build 26200), type 9's advancedColorEnabled
        // bit reads TRUE for Automatic Color Management on a display that is still SDR. A type-9-
        // only implementation would apply the configured HDR level while the display is in SDR on
        // the newest shipping Windows.
        //
        // Deliberately NOT latched (review finding, post-B2): DisplayConfigGetDeviceInfo returns an
        // int error code, it does not throw - a failed type-15 call on pre-1709 Windows, or on a
        // target that simply refuses it (a virtual display, an HMD target, a driver returning an
        // unexpected error), costs exactly one cheap P/Invoke and nothing else. A latch here only
        // ever bought "at most one wasted call per target per sweep", which on a TTL-capped ~1
        // sweep/second is a handful of calls a second at worst - and in exchange it made one
        // target's refusal capable of silently disabling type 15 for every OTHER target too,
        // including ones that would have answered correctly. Every target gets its own independent
        // attempt, every time - see HdrVibranceFixture's per-target-independence check for the
        // property this guarantees. (The genuinely permanent case - the API not existing on this
        // OS at all - is _isAvailable's job, via EntryPointNotFoundException/DllNotFoundException,
        // which really are permanent and really do only need paying once.)
        private bool TryGetColorInfo(DisplayConfigLuid adapterId, uint id, out HdrDisplayState state, out bool isHdrCapable, out bool answeredByType15)
        {
            DisplayConfigAdvancedColorInfo2 packet2 = new DisplayConfigAdvancedColorInfo2();
            packet2.header.type = DeviceInfoGetAdvancedColorInfo2;
            packet2.header.size = AdvancedColorInfo2Size;
            packet2.header.adapterId = adapterId;
            packet2.header.id = id;

            int result2 = DisplayConfigGetDeviceInfo(ref packet2);
            if (result2 == ErrorSuccess)
            {
                MapAdvancedColorInfo2(packet2.value, packet2.activeColorMode, out state, out isHdrCapable);
                answeredByType15 = true;
                return true;
            }

            DisplayConfigAdvancedColorInfo packet = new DisplayConfigAdvancedColorInfo();
            packet.header.type = DeviceInfoGetAdvancedColorInfo;
            packet.header.size = AdvancedColorInfoSize;
            packet.header.adapterId = adapterId;
            packet.header.id = id;

            int result = DisplayConfigGetDeviceInfo(ref packet);
            answeredByType15 = false;
            if (result != ErrorSuccess)
            {
                state = HdrDisplayState.Unknown;
                isHdrCapable = false;
                return false;
            }

            MapAdvancedColorInfo(packet.value, out state, out isHdrCapable);
            return true;
        }

        // Pure decode of a successful type 15 (GET_ADVANCED_COLOR_INFO_2) response - no P/Invoke,
        // no device, so HdrVibranceFixture can pin exact bit patterns with no display at all
        // (review finding B3). Two things a mutation here must not get away with:
        //   - activeColorMode is compared only to the literal 2 (HDR) - activeColorMode == 1 is
        //     WCG (wide colour gamut), which is NOT HDR and must resolve to Sdr like every other
        //     non-HDR mode, not "anything nonzero".
        //   - the capability bit is bit4 (0x10, highDynamicRangeSupported), NOT bit0. Type 9 has
        //     NO HDR-specific capability bit at all - its bit0 (advancedColorSupported) means wide
        //     colour gamut support, not HDR. Type 15 added highDynamicRangeSupported as a genuinely
        //     NEW bit at bit4 - it is not "type 9's bit0 moved", the two are unrelated bits that
        //     happen to share a field name "value" (review finding B1). Full layout, low to high
        //     bit: bit0 advancedColorSupported, bit1 advancedColorActive, bit2 reserved1,
        //     bit3 advancedColorLimitedByPolicy, bit4 highDynamicRangeSupported (this one),
        //     bit5 highDynamicRangeUserEnabled, bit6 wideColorSupported, bit7 wideColorUserEnabled.
        internal static void MapAdvancedColorInfo2(uint value, uint activeColorMode, out HdrDisplayState state, out bool isHdrCapable)
        {
            state = activeColorMode == 2 ? HdrDisplayState.Hdr : HdrDisplayState.Sdr;
            isHdrCapable = (value & 0x10) != 0;
        }

        // Pure decode of a successful type 9 (GET_ADVANCED_COLOR_INFO) response - no P/Invoke, no
        // device, same reasoning as MapAdvancedColorInfo2 above (review finding B3). Type 9 carries
        // no independent capability bit in this reader (only advancedColorEnabled at bit1) - a
        // display currently active in HDR is trivially capable of it; an SDR display answered only
        // by type 9 has no bit here that says whether it COULD do HDR, so this conservatively
        // reports false rather than guessing true.
        internal static void MapAdvancedColorInfo(uint value, out HdrDisplayState state, out bool isHdrCapable)
        {
            state = (value & 2) != 0 ? HdrDisplayState.Hdr : HdrDisplayState.Sdr;
            isHdrCapable = state == HdrDisplayState.Hdr;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DisplayConfigLuid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DisplayConfigDeviceInfoHeader
    {
        public uint type;
        public uint size;
        public DisplayConfigLuid adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DisplayConfigRational
    {
        public uint Numerator;
        public uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DisplayConfigPathSourceInfo
    {
        public DisplayConfigLuid adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DisplayConfigPathTargetInfo
    {
        public DisplayConfigLuid adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint outputTechnology;
        public uint rotation;
        public uint scaling;
        public DisplayConfigRational refreshRate;
        public uint scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DisplayConfigPathInfo
    {
        public DisplayConfigPathSourceInfo sourceInfo;
        public DisplayConfigPathTargetInfo targetInfo;
        public uint flags;
    }

    // The trailing 48 bytes (union0..union5) are never read anywhere in this file - this struct
    // exists purely so the array handed to QueryDisplayConfig is exactly the size the API demands
    // for numModeInfoArrayElements. Six ulong fields, not a ByValArray of bytes: the native union
    // contains a UINT64 pixelRate, so it is 8-byte aligned, and ulong reproduces that alignment
    // identically with no marshaller copy.
    [StructLayout(LayoutKind.Sequential)]
    public struct DisplayConfigModeInfo
    {
        public uint infoType;
        public uint id;
        public DisplayConfigLuid adapterId;
        public ulong union0;
        public ulong union1;
        public ulong union2;
        public ulong union3;
        public ulong union4;
        public ulong union5;
    }

    // CharSet.Unicode is mandatory here, unlike Devmode's CharSet.Ansi (ResolutionHelper.cs):
    // DisplayConfigGetDeviceInfo has no A/W pair, so the buffer is always WCHAR[32] = 64 bytes.
    // Copying Devmode's CharSet.Ansi would size this struct 52 bytes instead of 84, and every call
    // against it would fail with ERROR_INVALID_PARAMETER.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DisplayConfigSourceDeviceName
    {
        public DisplayConfigDeviceInfoHeader header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string viewGdiDeviceName;
    }

    // DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO (type 9). value bit0 is
    // advancedColorSupported; bit1, the only bit this reader consults, is advancedColorEnabled.
    [StructLayout(LayoutKind.Sequential)]
    public struct DisplayConfigAdvancedColorInfo
    {
        public DisplayConfigDeviceInfoHeader header;
        public uint value;
        public uint colorEncoding;
        public uint bitsPerColorChannel;
    }

    // DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO_2 (type 9's newer sibling, type 15 - see
    // RealHdrStateReader.DeviceInfoGetAdvancedColorInfo2 for the SDK citation; do not confuse it
    // with 14, which is the reserved SET_RESERVED1 setter). value is NOT type 9's bitfield under a
    // shared field name (review finding B1): type 9 has NO HDR-specific capability bit at all -
    // its bit0 (advancedColorSupported) means wide colour gamut support, not HDR. Type 15 added
    // highDynamicRangeSupported as a genuinely NEW bit at bit4, not "type 9's bit0 moved". Full
    // layout: bit0 advancedColorSupported, bit1 advancedColorActive, bit2 reserved1, bit3
    // advancedColorLimitedByPolicy, bit4 highDynamicRangeSupported (the capability bit this reader
    // actually consults - see RealHdrStateReader.MapAdvancedColorInfo2), bit5
    // highDynamicRangeUserEnabled, bit6 wideColorSupported, bit7 wideColorUserEnabled.
    // activeColorMode is 0 = SDR, 1 = WCG, 2 = HDR - only 2 counts as Hdr; WCG is not HDR. See
    // RealHdrStateReader.TryGetColorInfo for why this is queried before, and in preference to,
    // type 9 above.
    [StructLayout(LayoutKind.Sequential)]
    public struct DisplayConfigAdvancedColorInfo2
    {
        public DisplayConfigDeviceInfoHeader header;
        public uint value;
        public uint colorEncoding;
        public uint bitsPerColorChannel;
        public uint activeColorMode;
    }
}
