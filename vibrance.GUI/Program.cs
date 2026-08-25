using System.ComponentModel;
using System.Runtime.InteropServices;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using vibrance.GUI.AMD;
using vibrance.GUI.AMD.vendor;
using vibrance.GUI.AMD.vendor.utils;
using vibrance.GUI.common;
using vibrance.GUI.common.gamefinder;
using vibrance.GUI.NVIDIA;

namespace vibrance.GUI
{
    static class Program
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        private const string ErrorGraphicsAdapterUnknown = "Failed to determine your Graphic GraphicsAdapter type (NVIDIA/AMD). Make sure you have installed a proper GPU driver. Intel laptops are not supported as stated on the website. When installing your GPU driver did not work, please contact @juvlarN at twitter. Press Yes to open twitter in your browser now. Error: ";
        private const string ErrorGraphicsAdapterAmbiguous = "Both NVIDIA and AMD graphic drivers have been found on your system. This can happen when you recently switched your graphic card and did not uninstall the old drivers. Make sure to uninstall unused graphic drivers to keep your system safe and stable. Use the program \"Display Driver Uninstaller\" to uninstall your old drivers!\n\nIn case you want to do it manually: The related files are located in your Windows folder and are called \"nvapi.dll\" (NVIDIA) and \"atiadlxx.dll\" (AMD) and \"atiadlxy.dll\" (AMD). You are free to rename/delete the files that you no longer need but proceed with caution!\n\nPress Yes to open \"Display Driver Uninstaller\" download website in your Browser now.\nPress No to quit vibranceGUI.";
        private const string MessageBoxCaption = "vibranceGUI Error";
        private const string SelfTestMessageBoxCaption = "vibranceGUI game finder self test";
        private const string GpuSelfTestMessageBoxCaption = "vibranceGUI graphics adapter self test";
        private const string MatchingSelfTestMessageBoxCaption = "vibranceGUI foreground matching self test";
        private const string DisplayDriverUninstallerUrl = "http://www.guru3d.com/files-details/display-driver-uninstaller-download.html";

        [STAThread]
        static void Main(string[] args)
        {
            bool result = false;
            Mutex mutex = new Mutex(true, "vibranceGUI~Mutex", out result);
            if (!result)
            {
                MessageBox.Show("You can run vibranceGUI only once at a time!", MessageBoxCaption, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (Environment.OSVersion.Version.Major >= 6)
            {
                SetProcessDPIAware();
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Runs before the GPU vendor detection below on purpose: the picker is pure, so the
            // self test must stay runnable on a build agent or a reviewer's machine that has
            // neither an NVIDIA nor an AMD driver, where GetAdapter() shows an error and exits.
            if (args.Contains("--selftest-gamefinder"))
            {
                MessageBox.Show(string.Join(Environment.NewLine, ExecutablePickerFixture.Run().ToArray()),
                    SelfTestMessageBoxCaption, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // Same placement, and for the same reason: both fixtures are pure, so they have to
            // stay runnable on a machine that GetAdapter() cannot resolve and would exit from.
            // That is exactly the machine whose adapter name is worth checking.
            if (args.Contains("--selftest-gpu"))
            {
                MessageBox.Show(string.Join(Environment.NewLine, GraphicsAdapterFixture.Run().ToArray()),
                    GpuSelfTestMessageBoxCaption, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // Same placement again: matching is pure string work over settings built in the
            // fixture, so it needs no driver, no running game and no settings file.
            if (args.Contains("--selftest-matching"))
            {
                MessageBox.Show(string.Join(Environment.NewLine, MatchingFixture.Run().ToArray()),
                    MatchingSelfTestMessageBoxCaption, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            NativeMethods.SetDllDirectory(CommonUtils.GetVibrance_GUI_AppDataPath());

            GraphicsAdapter adapter = GraphicsAdapterHelper.GetAdapter();

            // Captured here, while it still means something. Everything below - the File.Exists
            // inside AreBothVendorDriversInstalled() above all - overwrites the thread's last
            // error, degrading 126 "the specified module could not be found", which tells a user
            // their driver DLL is missing, into 2 "the system cannot find the file specified".
            // That message is what users are asked to paste into a bug report.
            int adapterDetectionWin32Error = Marshal.GetLastWin32Error();

            Form vibranceGui = null;

            bool isForcedAmdAdapterExecution = args.Contains("--force-amd");
            bool isForcedNvidiaAdapterExecution = args.Contains("--force-nvidia");

            // Both drivers installed is the one case the driver files cannot settle. Precedence,
            // highest first: an explicit --force flag, the stored choice, what the attached
            // displays say, and only then the chooser. A machine that resolves to a single vendor
            // never reaches any of this and keeps resolving exactly as it did.
            if (GraphicsAdapterHelper.AreBothVendorDriversInstalled() &&
                !isForcedAmdAdapterExecution && !isForcedNvidiaAdapterExecution)
            {
                adapter = ResolveInstalledDriverConflict(adapter);
                if (adapter != GraphicsAdapter.Amd && adapter != GraphicsAdapter.Nvidia)
                {
                    // Cancelled, or the fallback dialog has already had its say.
                    return;
                }
            }

            GraphicsAdapter effectiveAdapter = GraphicsAdapterHelper.ApplyForcedAdapter(adapter,
                isForcedAmdAdapterExecution, isForcedNvidiaAdapterExecution);

            if (effectiveAdapter == GraphicsAdapter.Amd)
            {
                Func<List<ApplicationSetting>, Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>>, IVibranceProxy> getProxy = (x, y) => new AmdDynamicVibranceProxy(Environment.Is64BitOperatingSystem
                    ? new AmdAdapter64()
                    : (IAmdAdapter)new AmdAdapter32(), x, y);
                vibranceGui = new VibranceGUI(getProxy,
                    GraphicsAdapter.Amd,
                    AmdDynamicVibranceProxy.AmdDefaultLevel,
                    AmdDynamicVibranceProxy.AmdMinLevel,
                    AmdDynamicVibranceProxy.AmdMaxLevel,
                    AmdDynamicVibranceProxy.AmdDefaultLevel,
                    isForcedAmdAdapterExecution);
            }
            else if (effectiveAdapter == GraphicsAdapter.Nvidia)
            {
                const string nvidiaAdapterName = "vibranceDLL.dll";
                string resourceName = $"{typeof(Program).Namespace}.NVIDIA.{nvidiaAdapterName}";
                CommonUtils.LoadUnmanagedLibraryFromResource(
                    Assembly.GetExecutingAssembly(),
                    resourceName,
                    nvidiaAdapterName);
                Marshal.PrelinkAll(typeof(NvidiaDynamicVibranceProxy));

                vibranceGui = new VibranceGUI(
                    (x, y) => new NvidiaDynamicVibranceProxy(x, y),
                    GraphicsAdapter.Nvidia,
                    NvidiaDynamicVibranceProxy.NvapiDefaultLevel,
                    NvidiaDynamicVibranceProxy.NvapiDefaultLevel,
                    NvidiaDynamicVibranceProxy.NvapiMaxLevel,
                    NvidiaDynamicVibranceProxy.NvapiDefaultLevel,
                    isForcedNvidiaAdapterExecution);
            }
            else if (effectiveAdapter == GraphicsAdapter.Unknown)
            {
                string errorMessage = new Win32Exception(adapterDetectionWin32Error).Message;
                if (MessageBox.Show(ErrorGraphicsAdapterUnknown + errorMessage,
                    MessageBoxCaption, MessageBoxButtons.YesNo, MessageBoxIcon.Error) == DialogResult.Yes)
                {
                    System.Diagnostics.Process.Start("https://twitter.com/juvlarN");
                }
                return;
            }
            else if (effectiveAdapter == GraphicsAdapter.Ambiguous)
            {
                // Not reachable through ResolveInstalledDriverConflict, which never lets an
                // unresolved Ambiguous past it. Kept so the vendor cannot silently go unhandled.
                ShowLegacyAmbiguousDriverDialog();
                return;
            }
            if (args.Contains("-minimized"))
            {
                vibranceGui.WindowState = FormWindowState.Minimized;
                ((VibranceGUI)vibranceGui).SetAllowVisible(false);
            }
            vibranceGui.Text += buildFormTitleText(effectiveAdapter, isForcedAmdAdapterExecution, isForcedNvidiaAdapterExecution);
            Application.Run(vibranceGui);

            GC.KeepAlive(mutex);
        }

        /// <summary>
        /// Settles the case the driver files alone cannot: both vendors' DLLs are installed, so
        /// the old code gave up here and told the user to uninstall a driver. A stored choice wins
        /// first, then whatever adapter actually drives an attached display, and only when both of
        /// those come up empty is the user asked.
        /// Returns Ambiguous when the user declined to choose, which means "quit".
        /// </summary>
        static GraphicsAdapter ResolveInstalledDriverConflict(GraphicsAdapter detectedAdapter)
        {
            GraphicsAdapter storedAdapter = GraphicsAdapter.Unknown;
            try
            {
                storedAdapter = new SettingsController().ReadGraphicsAdapterPreference();
            }
            catch (Exception ex)
            {
                LogSafely(ex.ToString());
            }

            // A stored choice is honoured only while the hardware it names is still around,
            // otherwise a user who swapped cards would be stuck on last year's answer.
            if (storedAdapter != GraphicsAdapter.Unknown && GraphicsAdapterHelper.IsVendorDriverInstalled(storedAdapter))
            {
                LogAdapterResolution("the stored preference", storedAdapter);
                return storedAdapter;
            }

            if (detectedAdapter == GraphicsAdapter.Amd || detectedAdapter == GraphicsAdapter.Nvidia)
            {
                LogAdapterResolution("the attached display devices", detectedAdapter);
                return detectedAdapter;
            }

            return AskUserForGraphicsAdapter();
        }

        static GraphicsAdapter AskUserForGraphicsAdapter()
        {
            try
            {
                using (GraphicsAdapterChooser chooser = new GraphicsAdapterChooser(GraphicsAdapterHelper.GetDisplayAdapters()))
                {
                    if (chooser.ShowDialog() != DialogResult.OK ||
                        (chooser.SelectedAdapter != GraphicsAdapter.Amd && chooser.SelectedAdapter != GraphicsAdapter.Nvidia))
                    {
                        return GraphicsAdapter.Ambiguous;
                    }

                    if (chooser.ShouldRememberChoice)
                    {
                        try
                        {
                            new SettingsController().SetGraphicsAdapterPreference(chooser.SelectedAdapter);
                        }
                        catch (Exception ex)
                        {
                            // Not being able to remember the answer is no reason not to act on it.
                            LogSafely(ex.ToString());
                        }
                    }

                    LogAdapterResolution("the chooser dialog", chooser.SelectedAdapter);
                    return chooser.SelectedAdapter;
                }
            }
            catch (Exception ex)
            {
                LogSafely(ex.ToString());
                return ShowLegacyAmbiguousDriverDialog();
            }
        }

        /// <summary>
        /// The pre-existing dialog, now only the fallback for when the chooser itself cannot be
        /// shown. Its DDU advice is right for a leftover driver and wrong for hybrid hardware,
        /// which is why it is no longer the first thing a user meets.
        /// </summary>
        static GraphicsAdapter ShowLegacyAmbiguousDriverDialog()
        {
            if (MessageBox.Show(ErrorGraphicsAdapterAmbiguous, MessageBoxCaption, MessageBoxButtons.YesNo,
                MessageBoxIcon.Error) == DialogResult.Yes)
            {
                System.Diagnostics.Process.Start(DisplayDriverUninstallerUrl);
            }
            return GraphicsAdapter.Ambiguous;
        }

        /// <summary>
        /// The line to ask a user for when they report that vibranceGUI picked the wrong GPU.
        /// </summary>
        static void LogAdapterResolution(string source, GraphicsAdapter adapter)
        {
            LogSafely(string.Format("Both GPU drivers are installed. Resolved to {0} from {1}.{2}{3}",
                adapter, source, Environment.NewLine, GraphicsAdapterHelper.DescribeDisplayAdapters()));
        }

        static void LogSafely(string message)
        {
            try
            {
                VibranceGUI.Log(message);
            }
            catch (Exception)
            {
                // Logging must never be the reason startup fails.
            }
        }

        static string buildFormTitleText(GraphicsAdapter adapter, bool isForcedAmdAdapterExecution, bool isForcedNvidiaAdapterExecution)
        {
            string forcedExecution = "";
            if (isForcedAmdAdapterExecution)
            {
                forcedExecution = "*AMD forced*";
            }
            else if (isForcedNvidiaAdapterExecution)
            {
                forcedExecution = "*NVIDIA forced*";
            }
            return String.Format(" ({0}, {1}) {2}", adapter.ToString().ToUpper(), Application.ProductVersion, forcedExecution);
        }
    }
}
