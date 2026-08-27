using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using vibrance.GUI.AMD;
using vibrance.GUI.NVIDIA;

namespace vibrance.GUI.common
{

    class SettingsController : ISettingsController
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern uint GetPrivateProfileString(
           string lpAppName,
           string lpKeyName,
           string lpDefault,
           StringBuilder lpReturnedString,
           uint nSize,
           string lpFileName);


        [DllImport("kernel32.dll", EntryPoint = "WritePrivateProfileString")]
        private static extern bool WritePrivateProfileString(string lpAppName,
          string lpKeyName, string lpString, string lpFileName);

        const string SzSectionName = "Settings";
        const string SzKeyNameInactive = "inactiveValue";
        const string SzKeyNameRefreshRate = "refreshRate";
        const string SzKeyNameAffectPrimaryMonitorOnly = "affectPrimaryMonitorOnly";
        const string SzKeyNameNeverSwitchResolution = "neverSwitchResolution";
        const string SzKeyNameNeverChangeColorSettings = "neverChangeColorSettings";
        const string SzKeyNameBrightnessWindowsLevel = "brightnessWindowsLevel";
        const string SzKeyNameContrastWindowsLevel = "contrastWindowsLevel";
        const string SzKeyNameGammaWindowsLevel = "gammaWindowsLevel";
        const string SzKeyNameGraphicsAdapter = "graphicsAdapter";
        const string SzKeyNameToggleHotkey = "toggleHotkey";
        const string SzKeyNameToggleHotkeyEnabled = "toggleHotkeyEnabled";


        private string _fileName = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData).ToString() + "\\vibranceGUI\\vibranceGUI.ini";
        private string _fileNameApplicationSettings = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData).ToString() + "\\vibranceGUI\\applicationData.xml";

        public SettingsController()
        {
        }

        // Lets ProfileToggleFixture round-trip SetToggleHotkey/ReadToggleHotkey against a temp
        // INI, never the user's real "%APPDATA%\vibranceGUI\vibranceGUI.ini" - see that fixture's
        // own header comment.
        internal SettingsController(string fileName, string applicationSettingsFileName)
        {
            _fileName = fileName;
            _fileNameApplicationSettings = applicationSettingsFileName;
        }


        public bool SetVibranceSettings(string windowsLevel, string affectPrimaryMonitorOnly, string neverSwitchResolution, string neverChangeColorSettings, List<ApplicationSetting> applicationSettings, 
            string brightnessWindowsLevel, string contrastWindowsLevel, string gammaWindowsLevel)
        {
            if (!PrepareFile())
            {
                return false;
            }

            WritePrivateProfileString(SzSectionName, SzKeyNameInactive, windowsLevel, _fileName);
            WritePrivateProfileString(SzSectionName, SzKeyNameAffectPrimaryMonitorOnly, affectPrimaryMonitorOnly, _fileName);
            WritePrivateProfileString(SzSectionName, SzKeyNameNeverSwitchResolution, neverSwitchResolution, _fileName);
            WritePrivateProfileString(SzSectionName, SzKeyNameNeverChangeColorSettings, neverChangeColorSettings, _fileName);
            WritePrivateProfileString(SzSectionName, SzKeyNameBrightnessWindowsLevel, brightnessWindowsLevel, _fileName);
            WritePrivateProfileString(SzSectionName, SzKeyNameContrastWindowsLevel, contrastWindowsLevel, _fileName);
            WritePrivateProfileString(SzSectionName, SzKeyNameGammaWindowsLevel, gammaWindowsLevel, _fileName);

            try
            {
                var writer = System.Xml.XmlWriter.Create(_fileNameApplicationSettings);
                if (writer.WriteState != WriteState.Start)
                    return false;
                XmlSerializer serializer = new XmlSerializer(typeof(List<ApplicationSetting>));
                serializer.Serialize(writer, applicationSettings);
                writer.Flush();
                writer.Close();
            }
            catch (Exception)
            {
                return false;
            }

            return (Marshal.GetLastWin32Error() == 0);
        }

        public bool SetVibranceSetting(string szKeyName, string value)
        {
            if (!PrepareFile())
            {
                return false;
            }

            WritePrivateProfileString(SzSectionName, szKeyName, value.ToString(), _fileName);

            return (Marshal.GetLastWin32Error() == 0);
        }

        /// <summary>
        /// The GPU vendor the user picked when both drivers were installed, or Unknown when the
        /// INI holds no preference - which is what every existing installation looks like, and
        /// what an INI written by an older version looks like too.
        /// Read on its own because it is needed before the main form and the application settings
        /// XML exist, so it must not go through ReadVibranceSettings.
        /// </summary>
        public GraphicsAdapter ReadGraphicsAdapterPreference()
        {
            if (!IsFileExisting(_fileName))
            {
                return GraphicsAdapter.Unknown;
            }

            StringBuilder szValueGraphicsAdapter = new StringBuilder(1024);
            GetPrivateProfileString(SzSectionName,
                SzKeyNameGraphicsAdapter,
                "",
                szValueGraphicsAdapter,
                Convert.ToUInt32(szValueGraphicsAdapter.Capacity),
                _fileName);

            string szGraphicsAdapter = szValueGraphicsAdapter.ToString().Trim();
            if (string.Equals(szGraphicsAdapter, GraphicsAdapter.Nvidia.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return GraphicsAdapter.Nvidia;
            }
            if (string.Equals(szGraphicsAdapter, GraphicsAdapter.Amd.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                return GraphicsAdapter.Amd;
            }
            return GraphicsAdapter.Unknown;
        }

        /// <summary>
        /// Stores the vendor the user picked. Only the two supported vendors are ever written, so
        /// that the key can never be turned into a value the reader would have to guess about.
        /// </summary>
        public bool SetGraphicsAdapterPreference(GraphicsAdapter graphicsAdapter)
        {
            if (graphicsAdapter != GraphicsAdapter.Nvidia && graphicsAdapter != GraphicsAdapter.Amd)
            {
                return false;
            }

            return SetVibranceSetting(SzKeyNameGraphicsAdapter, graphicsAdapter.ToString());
        }

        /// <summary>
        /// The toggle hotkey's canonical text (HotkeyBindingParser.Format's own output, e.g.
        /// "Ctrl+Alt+F9"), or "" when the INI holds no binding - which is what every existing
        /// installation looks like. Modelled exactly on ReadGraphicsAdapterPreference: read on
        /// its own, not folded into ReadVibranceSettings' 8-parameter signature, since that
        /// signature is shared by every existing call site and this feature has nothing to do
        /// with vibrance levels.
        /// </summary>
        public string ReadToggleHotkey()
        {
            if (!IsFileExisting(_fileName))
            {
                return string.Empty;
            }

            StringBuilder szValueToggleHotkey = new StringBuilder(1024);
            GetPrivateProfileString(SzSectionName,
                SzKeyNameToggleHotkey,
                "",
                szValueToggleHotkey,
                Convert.ToUInt32(szValueToggleHotkey.Capacity),
                _fileName);

            return szValueToggleHotkey.ToString().Trim();
        }

        /// <summary>
        /// Stores the toggle hotkey's canonical text. SetVibranceSetting is already a single-key
        /// writer, so this is a thin, named wrapper over it - the same shape as
        /// SetGraphicsAdapterPreference.
        /// </summary>
        public bool SetToggleHotkey(string canonicalText)
        {
            return SetVibranceSetting(SzKeyNameToggleHotkey, canonicalText ?? string.Empty);
        }

        /// <summary>
        /// Whether the toggle hotkey checkbox was checked, or false when the INI holds no
        /// preference - which is what every existing installation looks like. A missing/corrupt
        /// value defaults to false (disabled), the safer of the two: an unexpectedly-active
        /// global hotkey is a worse first impression than one the user has to turn on themselves.
        /// </summary>
        public bool ReadToggleHotkeyEnabled()
        {
            if (!IsFileExisting(_fileName))
            {
                return false;
            }

            StringBuilder szValueToggleHotkeyEnabled = new StringBuilder(1024);
            GetPrivateProfileString(SzSectionName,
                SzKeyNameToggleHotkeyEnabled,
                "False",
                szValueToggleHotkeyEnabled,
                Convert.ToUInt32(szValueToggleHotkeyEnabled.Capacity),
                _fileName);

            bool enabled;
            return bool.TryParse(szValueToggleHotkeyEnabled.ToString().Trim(), out enabled) && enabled;
        }

        /// <summary>
        /// Stores whether the toggle hotkey checkbox was checked - the same single-key writer
        /// shape as SetToggleHotkey beside it.
        /// </summary>
        public bool SetToggleHotkeyEnabled(bool enabled)
        {
            return SetVibranceSetting(SzKeyNameToggleHotkeyEnabled, enabled.ToString());
        }

        private bool PrepareFile()
        {
            if (!IsFileExisting(_fileName))
            {
                StreamWriter sw = new StreamWriter(_fileName);
                sw.Close();
                if (!IsFileExisting(_fileName))
                {
                    return false;
                }
            }

            return true;
        }

        public void ReadVibranceSettings(GraphicsAdapter graphicsAdapter, out int vibranceWindowsLevel, out bool affectPrimaryMonitorOnly, out bool neverSwitchResolution, 
            out bool neverChangeColorSettings, out List<ApplicationSetting> applicationSettings, out int brightnessWindowsLevel, out int contrastWindowsLevel, out int gammaWindowsLevel)
        {
            int defaultLevel = 0; 
            int maxLevel = 0;
            if (graphicsAdapter == GraphicsAdapter.Nvidia)
            {
                defaultLevel = NvidiaDynamicVibranceProxy.NvapiDefaultLevel;
                maxLevel = NvidiaDynamicVibranceProxy.NvapiMaxLevel;
            }
            if (graphicsAdapter == GraphicsAdapter.Amd)
            {
                defaultLevel = AmdDynamicVibranceProxy.AmdDefaultLevel;
                maxLevel = AmdDynamicVibranceProxy.AmdMaxLevel;
            }

            if (!IsFileExisting(_fileName) || !IsFileExisting(_fileNameApplicationSettings))
            {
                vibranceWindowsLevel = defaultLevel;
                affectPrimaryMonitorOnly = true;
                applicationSettings = new List<ApplicationSetting>();
                neverSwitchResolution = true;
                neverChangeColorSettings = true;
                brightnessWindowsLevel = 50;
                contrastWindowsLevel = 50;
                gammaWindowsLevel = 100;
                return;
            }

            string szDefault = "";

            StringBuilder szValueInactive = new StringBuilder(1024);
            GetPrivateProfileString(SzSectionName,
                SzKeyNameInactive,
                szDefault,
                szValueInactive,
                Convert.ToUInt32(szValueInactive.Capacity),
                _fileName);

            StringBuilder szValueRefreshRate = new StringBuilder(1024);
            GetPrivateProfileString(SzSectionName,
                SzKeyNameRefreshRate,
                szDefault,
                szValueRefreshRate,
                Convert.ToUInt32(szValueRefreshRate.Capacity),
                _fileName);

            StringBuilder szValueAffectPrimaryMonitorOnly = new StringBuilder(1024);
            GetPrivateProfileString(SzSectionName,
                SzKeyNameAffectPrimaryMonitorOnly,
                "true",
                szValueAffectPrimaryMonitorOnly,
                Convert.ToUInt32(szValueAffectPrimaryMonitorOnly.Capacity),
                _fileName);

            StringBuilder szValueNeverSwitchResolution = new StringBuilder(1024);
            GetPrivateProfileString(SzSectionName,
                SzKeyNameNeverSwitchResolution,
                "true",
                szValueNeverSwitchResolution,
                Convert.ToUInt32(szValueNeverSwitchResolution.Capacity),
                _fileName);

            StringBuilder szValueNeverChangeColorSettings = new StringBuilder(1024);
            GetPrivateProfileString(SzSectionName,
                SzKeyNameNeverChangeColorSettings,
                "true",
                szValueNeverChangeColorSettings,
                Convert.ToUInt32(szValueNeverChangeColorSettings.Capacity),
                _fileName);

            StringBuilder szValueBrightnessWindowsLevel = new StringBuilder(1024);
            GetPrivateProfileString(SzSectionName,
                SzKeyNameBrightnessWindowsLevel,
                "50",
                szValueBrightnessWindowsLevel,
                Convert.ToUInt32(szValueBrightnessWindowsLevel.Capacity),
                _fileName);

            StringBuilder szValueContrastWindowsLevel = new StringBuilder(1024);
            GetPrivateProfileString(SzSectionName,
                SzKeyNameContrastWindowsLevel,
                "50",
                szValueContrastWindowsLevel,
                Convert.ToUInt32(szValueContrastWindowsLevel.Capacity),
                _fileName);

            StringBuilder szValueGammaWindowsLevel = new StringBuilder(1024);
            GetPrivateProfileString(SzSectionName,
                SzKeyNameGammaWindowsLevel,
                "100",
                szValueGammaWindowsLevel,
                Convert.ToUInt32(szValueGammaWindowsLevel.Capacity),
                _fileName);

            try
            {
                vibranceWindowsLevel = int.Parse(szValueInactive.ToString());
                affectPrimaryMonitorOnly = bool.Parse(szValueAffectPrimaryMonitorOnly.ToString());
                neverSwitchResolution = bool.Parse(szValueNeverSwitchResolution.ToString());
                neverChangeColorSettings = bool.Parse(szValueNeverChangeColorSettings.ToString());
                brightnessWindowsLevel = int.Parse(szValueBrightnessWindowsLevel.ToString());
                contrastWindowsLevel = int.Parse(szValueContrastWindowsLevel.ToString());
                gammaWindowsLevel = int.Parse(szValueGammaWindowsLevel.ToString());
            }
            catch (Exception)
            {
                vibranceWindowsLevel = defaultLevel;
                affectPrimaryMonitorOnly = false;
                applicationSettings = new List<ApplicationSetting>();
                neverSwitchResolution = true;
                neverChangeColorSettings = true;
                brightnessWindowsLevel = 50;
                contrastWindowsLevel = 50;
                gammaWindowsLevel = 100;
                return;
            }

            if (vibranceWindowsLevel < defaultLevel || vibranceWindowsLevel > maxLevel)
                vibranceWindowsLevel = defaultLevel;

            try
            {
                var reader = System.Xml.XmlReader.Create(_fileNameApplicationSettings);
                XmlSerializer serializer = new XmlSerializer(typeof(List<ApplicationSetting>));
                applicationSettings = (List<ApplicationSetting>)serializer.Deserialize(reader);
                reader.Close();
            }
            catch (Exception)
            {
                applicationSettings = new List<ApplicationSetting>();
            }
        }

        private bool IsFileExisting(string szFilename)
        {
            return File.Exists(szFilename);
        }
    }
}
