using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using vibrance.GUI.AMD.vendor;
using vibrance.GUI.AMD.vendor.adl32;
using vibrance.GUI.NVIDIA;

namespace vibrance.GUI.common
{
    public enum GraphicsAdapter
    {
        Unknown = 0,
        Nvidia = 1,
        Amd = 2,
        Ambiguous = 3
    }

    /// <summary>
    /// One graphics adapter, as Windows reports it through EnumDisplayDevices. Windows emits an
    /// adapter entry per display head, so several entries share a Name - they are folded into one
    /// instance here and DisplayNames lists every head the adapter owns.
    /// Only ever produced by GraphicsAdapterHelper.GetDisplayAdapters().
    /// </summary>
    public class DisplayAdapterInfo
    {
        public string Name { get; set; }                // DeviceString, e.g. "NVIDIA GeForce RTX 5070 Ti"
        public GraphicsAdapter Vendor { get; set; }     // Nvidia, Amd, or Unknown for anything else
        public bool IsAttachedToDesktop { get; set; }   // drives at least one display of the desktop
        public bool IsPrimary { get; set; }             // owns the primary display
        public List<string> DisplayNames { get; set; }  // "\\.\DISPLAY1", ...; diagnostics only

        public DisplayAdapterInfo()
        {
            DisplayNames = new List<string>();
        }
    }

    public class GraphicsAdapterHelper
    {

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr LoadLibrary(string dllToLoad);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplayDevices(string lpDevice, uint iDevNum, ref DisplayDevice lpDisplayDevice, uint dwFlags);

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

        private const uint DisplayDeviceAttachedToDesktop = 0x00000001;
        private const uint DisplayDevicePrimaryDevice = 0x00000004;

        // Nothing real comes close to this. The bound only exists so that a display driver which
        // never fails the enumeration cannot spin the loop forever before the window opens.
        private const uint MaxEnumeratedDisplayDevices = 64;

        // Matched case-insensitively as whole words in the adapter's DeviceString - see
        // ContainsAnyToken for why the word boundary is not optional. Anything that matches
        // neither list - Intel above all, and every virtual display driver - is Unknown and must
        // never be reported as one of the two supported vendors.
        private static readonly string[] NvidiaAdapterNameTokens = { "NVIDIA" };
        private static readonly string[] AmdAdapterNameTokens = { "AMD", "Radeon", "ATI" };

        // nvapi.dll is the 32-bit NvAPI entry point and on a 64-bit Windows it exists only in
        // SysWOW64; the 64-bit driver ships nvapi64.dll in System32. A 64-bit process that asks
        // the loader for "nvapi.dll" therefore gets ERROR_MOD_NOT_FOUND (126) - SysWOW64 is not on
        // its search path - and IsAdapterAvailable below reported "no NVIDIA driver present" on
        // every x64 machine, whatever card was in it. The whole app then died in the "failed to
        // determine your graphics adapter" dialog, which blamed the driver for a name this side
        // chose.
        //
        // This is the same process-bitness rule the native side has always applied - see
        // initializeLibrary in native/vibranceDLL/vibrance/vibrance.cpp, which picks nvapi64.dll
        // under _WIN64 - and the one _amdDllName below applies. It was missed here because this
        // was a const, so the x64 port had no per-platform expression to revisit.
        private static readonly string _nvidiaDllName = Environment.Is64BitProcess
            ? "nvapi64.dll"
            : "nvapi.dll";
        // NOTE: the adl32/adl64 namespace names are inverted relative to the file each one loads -
        // adl64.AdlImport binds "atiadlxy.dll", a 32-bit-only bridge binary that exists nowhere but
        // SysWOW64 (no 64-bit build exists at all), while adl32.AdlImport binds "atiadlxx.dll",
        // which System32/SysWOW64 file redirection resolves to the 64-bit or 32-bit copy depending
        // on the *calling process's* bitness. An x64 process therefore has exactly one option that
        // can load at all: adl32 -> "atiadlxx.dll". A 32-bit process keeps picking whichever file it
        // always has (still gated on OS bitness, unchanged from before) since the two ADL binding
        // sets are otherwise byte-identical (no 64-bit-shaped structs) and both already work for a
        // 32-bit process. Previously this whole selection was keyed off
        // Environment.Is64BitOperatingSystem, so an x64 process on a 64-bit OS picked adl64 ->
        // "atiadlxy.dll", which has no 64-bit build and fails to load outright.
        private static readonly string _amdDllName = Environment.Is64BitProcess
            ? AMD.vendor.adl32.AdlImport.AtiadlFileName
            : (Environment.Is64BitOperatingSystem
                ? AMD.vendor.adl64.AdlImport.AtiadlFileName
                : AMD.vendor.adl32.AdlImport.AtiadlFileName);

        /// <summary>
        /// The ADL file name resolved above, exposed read-only so GraphicsAdapterFixture can assert
        /// the process-bitness selection without touching any driver file.
        /// </summary>
        public static string AmdDllName
        {
            get { return _amdDllName; }
        }

        /// <summary>
        /// The NvAPI file name resolved above, exposed read-only so GraphicsAdapterFixture can
        /// assert the process-bitness selection without touching any driver file.
        /// </summary>
        public static string NvidiaDllName
        {
            get { return _nvidiaDllName; }
        }


        public static GraphicsAdapter GetAdapter()
        {
            if (AreBothVendorDriversInstalled())
            {
                // A driver DLL sitting in the system folder says nothing about whether that GPU is
                // in use, so the file system alone cannot settle this. Ask Windows which adapter
                // actually drives a display first: on an AMD CPU with integrated graphics plus a
                // discrete NVIDIA card - both DLLs present, one GPU driving the monitors - there
                // is an unambiguous answer, and it is the difference between the application
                // starting and refusing to start at all.
                return GetAdapterFromAttachedDisplays();
            }
            if (IsAdapterAvailable(_amdDllName))
            {
                // Mirrors the _amdDllName selection above (see the comment there for why this is
                // process bitness, not OS bitness, and why AmdAdapter64/adl64 is only reachable for
                // a 32-bit process).
                IAmdAdapter amdAdapter = Environment.Is64BitProcess
                    ? (IAmdAdapter)new AmdAdapter32()
                    : (Environment.Is64BitOperatingSystem ? (IAmdAdapter)new AmdAdapter64() : new AmdAdapter32());
                if (amdAdapter.IsAvailable())
                {
                    return GraphicsAdapter.Amd;
                }
            }
            if (IsAdapterAvailable(_nvidiaDllName))
            {
                return GraphicsAdapter.Nvidia;
            }
            return GraphicsAdapter.Unknown;
        }

        /// <summary>
        /// Applies the --force-amd / --force-nvidia overrides on top of whatever was detected,
        /// stored or chosen. An explicit instruction from the user outranks all of those.
        /// This is a function rather than a pair of inline conditions because the order used to be
        /// wrong: testing "detected as AMD OR forced to AMD" before the NVIDIA case meant
        /// --force-nvidia was silently swallowed on any system that detected as AMD, which is the
        /// system its users were most likely to be on.
        /// Both flags at once keeps resolving to AMD, exactly as it did before.
        /// </summary>
        public static GraphicsAdapter ApplyForcedAdapter(GraphicsAdapter detectedAdapter, bool isForcedAmdAdapterExecution, bool isForcedNvidiaAdapterExecution)
        {
            if (isForcedAmdAdapterExecution)
            {
                return GraphicsAdapter.Amd;
            }
            if (isForcedNvidiaAdapterExecution)
            {
                return GraphicsAdapter.Nvidia;
            }
            return detectedAdapter;
        }

        /// <summary>
        /// True when both vendors' driver DLLs are installed. That is the only case GetAdapter()
        /// cannot decide from the file system, and therefore the only case in which the display
        /// device detection, a stored preference or the chooser are allowed to have a say - a
        /// machine that resolves to a single vendor today keeps resolving exactly as it did.
        /// </summary>
        public static bool AreBothVendorDriversInstalled()
        {
            return IsVendorDriverInstalled(GraphicsAdapter.Amd) && IsVendorDriverInstalled(GraphicsAdapter.Nvidia);
        }

        /// <summary>
        /// True when the driver DLL of the given vendor is installed. Used to discard a stored
        /// preference that names hardware the user no longer has.
        /// </summary>
        public static bool IsVendorDriverInstalled(GraphicsAdapter graphicsAdapter)
        {
            string dllName;
            if (graphicsAdapter == GraphicsAdapter.Nvidia)
            {
                dllName = _nvidiaDllName;
            }
            else if (graphicsAdapter == GraphicsAdapter.Amd)
            {
                dllName = _amdDllName;
            }
            else
            {
                return false;
            }

            try
            {
                // SystemX86 is always SysWOW64 on a 64-bit OS, and SysWOW64 holds the 32-bit
                // driver files only. Now that both names above are chosen per process bitness, a
                // 64-bit process has to look in System32 for them - asking SysWOW64 for
                // nvapi64.dll or for the 64-bit atiadlxx.dll finds neither, and this method would
                // report that the user has no driver of that vendor installed at all. A 32-bit
                // process keeps naming SysWOW64 explicitly, exactly as it did before.
                string windowsFolder = Environment.GetFolderPath(Environment.Is64BitProcess
                    ? Environment.SpecialFolder.System
                    : Environment.SpecialFolder.SystemX86);
                return File.Exists(Path.Combine(windowsFolder, dllName));
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// The vendor of the adapter that actually drives a display attached to the desktop. This
        /// is the signal that discriminates a hybrid machine: Win32_VideoController reports an AMD
        /// iGPU and a discrete NVIDIA card as equally OK, but only one of them has a monitor on
        /// it. Returns Ambiguous when both vendors drive a display, and also when neither does -
        /// an Intel-only desktop, an RDP session, or an enumeration that told us nothing. In those
        /// cases asking is better than guessing.
        /// </summary>
        public static GraphicsAdapter GetAdapterFromAttachedDisplays()
        {
            bool isNvidiaAttached = false;
            bool isAmdAttached = false;
            foreach (DisplayAdapterInfo adapter in GetAttachedDisplayAdapters())
            {
                if (adapter.Vendor == GraphicsAdapter.Nvidia)
                {
                    isNvidiaAttached = true;
                }
                else if (adapter.Vendor == GraphicsAdapter.Amd)
                {
                    isAmdAttached = true;
                }
            }

            if (isNvidiaAttached && !isAmdAttached)
            {
                return GraphicsAdapter.Nvidia;
            }
            if (isAmdAttached && !isNvidiaAttached)
            {
                return GraphicsAdapter.Amd;
            }
            return GraphicsAdapter.Ambiguous;
        }

        /// <summary>
        /// The adapters that drive at least one display attached to the desktop.
        /// </summary>
        public static List<DisplayAdapterInfo> GetAttachedDisplayAdapters()
        {
            List<DisplayAdapterInfo> attachedAdapters = new List<DisplayAdapterInfo>();
            foreach (DisplayAdapterInfo adapter in GetDisplayAdapters())
            {
                if (adapter.IsAttachedToDesktop)
                {
                    attachedAdapters.Add(adapter);
                }
            }
            return attachedAdapters;
        }

        /// <summary>
        /// Every graphics adapter Windows knows about, folded to one entry per adapter name.
        /// Never throws and never returns null: this runs before the main window exists, and a
        /// virtual display driver, an RDP session or a headless machine can make EnumDisplayDevices
        /// behave in ways no caller should have to anticipate. Every caller treats an empty list
        /// as "could not tell", which falls back to the behaviour that was there before.
        /// </summary>
        public static List<DisplayAdapterInfo> GetDisplayAdapters()
        {
            List<DisplayAdapterInfo> adapters = new List<DisplayAdapterInfo>();
            bool isEnumerationComplete = false;
            try
            {
                for (uint deviceIndex = 0; deviceIndex < MaxEnumeratedDisplayDevices; deviceIndex++)
                {
                    DisplayDevice device = new DisplayDevice();
                    device.cb = Marshal.SizeOf(typeof(DisplayDevice));
                    if (!EnumDisplayDevices(null, deviceIndex, ref device, 0))
                    {
                        isEnumerationComplete = true;
                        break;
                    }

                    string adapterName = device.DeviceString == null ? string.Empty : device.DeviceString.Trim();
                    if (adapterName.Length == 0)
                    {
                        continue;
                    }

                    DisplayAdapterInfo adapter = FindAdapterByName(adapters, adapterName);
                    if (adapter == null)
                    {
                        adapter = new DisplayAdapterInfo();
                        adapter.Name = adapterName;
                        adapter.Vendor = GetVendorFromAdapterName(adapterName);
                        adapters.Add(adapter);
                    }

                    adapter.IsAttachedToDesktop |= (device.StateFlags & DisplayDeviceAttachedToDesktop) != 0;
                    adapter.IsPrimary |= (device.StateFlags & DisplayDevicePrimaryDevice) != 0;
                    if (device.DeviceName != null && device.DeviceName.Trim().Length > 0)
                    {
                        adapter.DisplayNames.Add(device.DeviceName.Trim());
                    }
                }
            }
            catch (Exception)
            {
                // A half-read enumeration is worse than none: it could show one vendor and hide
                // the other. Report "could not tell" instead.
                return new List<DisplayAdapterInfo>();
            }

            if (!isEnumerationComplete)
            {
                // Ran out at the bound rather than at the end of the list. Same half-read hazard
                // as an exception, so it gets the same answer.
                return new List<DisplayAdapterInfo>();
            }
            return adapters;
        }

        /// <summary>
        /// The vendor an adapter name belongs to, or Unknown when it is neither of the two
        /// supported ones.
        /// </summary>
        public static GraphicsAdapter GetVendorFromAdapterName(string adapterName)
        {
            if (string.IsNullOrEmpty(adapterName))
            {
                return GraphicsAdapter.Unknown;
            }
            if (ContainsAnyToken(adapterName, NvidiaAdapterNameTokens))
            {
                return GraphicsAdapter.Nvidia;
            }
            if (ContainsAnyToken(adapterName, AmdAdapterNameTokens))
            {
                return GraphicsAdapter.Amd;
            }
            return GraphicsAdapter.Unknown;
        }

        /// <summary>
        /// One line per adapter, for vibranceGUI.log. This is the first thing to ask a user for
        /// when they report that the wrong GPU was picked.
        /// </summary>
        public static string DescribeDisplayAdapters()
        {
            StringBuilder description = new StringBuilder();
            List<DisplayAdapterInfo> adapters = GetDisplayAdapters();
            if (adapters.Count == 0)
            {
                return "No display adapters could be enumerated.";
            }

            foreach (DisplayAdapterInfo adapter in adapters)
            {
                description.AppendFormat("  {0} [vendor={1}, attached={2}, primary={3}, displays={4}]",
                    adapter.Name,
                    adapter.Vendor,
                    adapter.IsAttachedToDesktop,
                    adapter.IsPrimary,
                    string.Join(", ", adapter.DisplayNames.ToArray()));
                description.AppendLine();
            }
            return description.ToString().TrimEnd();
        }

        private static DisplayAdapterInfo FindAdapterByName(List<DisplayAdapterInfo> adapters, string adapterName)
        {
            foreach (DisplayAdapterInfo adapter in adapters)
            {
                if (string.Equals(adapter.Name, adapterName, StringComparison.OrdinalIgnoreCase))
                {
                    return adapter;
                }
            }
            return null;
        }

        /// <summary>
        /// The URL the "could not determine your adapter" dialog offers to open. It used to be a
        /// Twitter profile, which asked a user hitting a startup crash to go compose a public
        /// message to a stranger. This fork has an issue tracker with a form that asks for the
        /// title bar string, so the report arrives with the build and architecture already in it.
        /// </summary>
        public const string BugReportUrl =
            "https://github.com/SwatX18/vibranceGUI/issues/new?template=bug_report.yml";

        /// <summary>
        /// Windows' ERROR_MOD_NOT_FOUND. Worth naming: it is the loader saying "a file I needed is
        /// not there", which is a very different thing from "your GPU driver is broken", and until
        /// v2.10.1 the dialog reported it as the latter.
        /// </summary>
        public const int ErrorModNotFound = 126;

        /// <summary>
        /// The "we could not work out which GPU you have" message.
        ///
        /// This is built rather than a const string because the two facts that actually diagnose
        /// it - the driver file name *this* process looked for, and the Win32 error the loader
        /// handed back - both depend on the running process's bitness. v2.10.0 is the argument for
        /// that: its x64 build searched for "nvapi.dll", a name no 64-bit process can ever load,
        /// and the fixed message told every NVIDIA user on the planet to go and reinstall a driver
        /// that was working perfectly. Naming the file it looked for turns that dialog from an
        /// accusation into a bug report.
        ///
        /// Every input is a parameter rather than read from the environment, so GraphicsAdapterFixture
        /// can render the x64 wording from an x86 process and vice versa.
        /// </summary>
        public static string BuildAdapterUnknownMessage(int win32Error, string win32ErrorText,
            bool is64BitProcess, string nvidiaDllName, string amdDllName)
        {
            StringBuilder message = new StringBuilder();
            message.Append("vibranceGUI could not determine whether this machine has an NVIDIA or an AMD GPU, ");
            message.AppendLine("so there is nothing for it to drive.");
            message.AppendLine();
            message.AppendFormat("This is the {0} build. It looked for \"{1}\" (NVIDIA) and \"{2}\" (AMD) and could load neither.",
                is64BitProcess ? "x64" : "x86", nvidiaDllName, amdDllName);
            message.AppendLine();
            message.AppendFormat("Windows reported: {0} (error {1})", win32ErrorText, win32Error);
            message.AppendLine();

            if (win32Error == ErrorModNotFound)
            {
                // The branch this whole method exists for. 126 is the loader failing to find a
                // file, and the file name is one vibranceGUI chose - so this is at least as likely
                // to be our bug as the user's driver, and the x86/x64 comparison is the one test
                // that tells the two apart without any tooling.
                message.AppendLine();
                message.Append("Error 126 means a file vibranceGUI asked for was not found. That is often ");
                message.Append("vibranceGUI's fault rather than your driver's - it can mean this build asked ");
                message.AppendLine("for the wrong file name for its own architecture.");
                if (is64BitProcess)
                {
                    message.Append("Please try the x86 download on this same machine. If x86 works and x64 does not, ");
                    message.AppendLine("that is a bug in vibranceGUI, not a problem with your driver - please report it.");
                }
            }

            message.AppendLine();
            message.Append("If you do have an NVIDIA or AMD card with its driver installed, please report this ");
            message.AppendLine("and include the build and error lines above exactly as they appear.");
            message.AppendLine("Intel graphics are not supported.");
            message.AppendLine();
            message.AppendLine("Press Yes to open the bug report form in your browser now.");
            message.Append("Press No to quit vibranceGUI.");
            return message.ToString();
        }

        /// <summary>
        /// The "both vendors' drivers are installed" message.
        ///
        /// Built rather than a const for one reason: the old text named "nvapi.dll" as the NVIDIA
        /// file to rename or delete, which on 64-bit Windows is the 32-bit one in SysWOW64 and not
        /// the file a 64-bit application loads at all. Advice to delete a system file should at
        /// least name the right file, so the NVIDIA pair and the folder each lives in are spelled
        /// out per OS bitness. OS bitness, not process bitness: which files exist on disk is a
        /// property of the Windows install, not of the build the user happens to be running.
        /// </summary>
        public static string BuildAdapterAmbiguousMessage(bool is64BitOperatingSystem)
        {
            StringBuilder message = new StringBuilder();
            message.Append("Both NVIDIA and AMD graphic drivers have been found on your system. This can happen ");
            message.Append("when you recently switched your graphic card and did not uninstall the old drivers. ");
            message.Append("Make sure to uninstall unused graphic drivers to keep your system safe and stable. ");
            message.AppendLine("Use the program \"Display Driver Uninstaller\" to uninstall your old drivers!");
            message.AppendLine();
            message.AppendLine("In case you want to do it manually, the files are:");
            if (is64BitOperatingSystem)
            {
                message.AppendLine("  NVIDIA - \"nvapi64.dll\" in System32, and \"nvapi.dll\" in SysWOW64");
                message.AppendLine("  AMD    - \"atiadlxx.dll\" in System32, and \"atiadlxx.dll\" / \"atiadlxy.dll\" in SysWOW64");
            }
            else
            {
                message.AppendLine("  NVIDIA - \"nvapi.dll\" in System32");
                message.AppendLine("  AMD    - \"atiadlxx.dll\" / \"atiadlxy.dll\" in System32");
            }
            message.Append("You are free to rename or delete the files you no longer need, but proceed with ");
            message.AppendLine("caution - these belong to the driver, not to vibranceGUI.");
            message.AppendLine();
            message.AppendLine("Press Yes to open the \"Display Driver Uninstaller\" download website in your browser now.");
            message.Append("Press No to quit vibranceGUI.");
            return message.ToString();
        }

        /// <summary>
        /// True when one of the tokens appears in the adapter name as a whole word.
        /// The boundary check is not cosmetic. "ATI" occurs inside ordinary English words -
        /// workstation, application, cinematic, innovation - and a bare substring match turns
        /// "Workstation Virtual Display" into an AMD adapter. That would make the one branch which
        /// is supposed to refuse to guess guess confidently and wrongly, building an AMD proxy on
        /// a machine with no usable AMD GPU instead of showing the chooser.
        /// Only letters break a match, never digits: "ATI2VGA" and "AMD780G Integrated Graphics"
        /// are real adapter names and have to keep matching.
        /// </summary>
        private static bool ContainsAnyToken(string adapterName, string[] tokens)
        {
            foreach (string token in tokens)
            {
                int index = adapterName.IndexOf(token, StringComparison.OrdinalIgnoreCase);
                while (index >= 0)
                {
                    if (IsWordBoundary(adapterName, index - 1) &&
                        IsWordBoundary(adapterName, index + token.Length))
                    {
                        return true;
                    }
                    // Keep looking: an earlier glued occurrence must not hide a later real one,
                    // as in "Innovation Radeon Display".
                    index = adapterName.IndexOf(token, index + 1, StringComparison.OrdinalIgnoreCase);
                }
            }
            return false;
        }

        private static bool IsWordBoundary(string adapterName, int index)
        {
            return index < 0 || index >= adapterName.Length || !char.IsLetter(adapterName[index]);
        }

        private static bool IsAdapterAvailable(string dllName)
        {
            try
            {
                return LoadLibrary(dllName) != IntPtr.Zero;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
