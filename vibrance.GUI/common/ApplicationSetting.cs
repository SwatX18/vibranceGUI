using System;
using System.ComponentModel;
using System.Xml.Serialization;

namespace vibrance.GUI.common
{
    public class ApplicationSetting
    {
        public string Name { get; set; }
        public string FileName { get; set; }
        public int IngameLevel { get; set; }
        public int Brightness { get; set; } = 50;
        public int Contrast { get; set; } = 50;
        public int Gamma { get; set; } = 100;
        public bool IsResolutionChangeNeeded { get; set; }
        [XmlElement(IsNullable = true)]
        public ResolutionModeWrapper ResolutionSettings { get; set; }
        // Install folder of the game as reported by the store. Null for manually added entries.
        // Used as the initial directory of the "Change executable..." picker.
        public string InstallDirectory { get; set; }
        // True while the executable was inferred by the game finder and has never been seen in the
        // foreground. Rendered as a "(?)" marker. Default false, so settings files written before
        // this feature load unchanged.
        public bool IsExecutableUnconfirmed { get; set; }
        // Separate vibrance level to use while Windows reports this display as HDR (upstream #147).
        // HdrVibranceHelper.HdrLevelUnset (-1) means "no separate HDR level configured" and is the
        // only value a pre-v2.7 profile can ever have: XmlSerializer runs this initialiser and then
        // finds no <HdrIngameLevel> element in the file to overwrite it with - the same mechanism
        // that already keeps Brightness = 50 working on older files. -1 can never collide with a
        // real level - both vendors' minimum level is 0.
        //
        // That mechanism is one-directional. Downgrading to a pre-v2.7 build and then saving -
        // which needs no deliberate action, since VibranceGUI's settingsBackgroundWorker fires a
        // full re-save 5 seconds after any trackbar scroll - re-serialises applicationData.xml from
        // a type that has never heard of this property, permanently losing every configured
        // HdrIngameLevel. Inherent to round-tripping the whole file through a type per version, not
        // something this property could fix alone - noted here so nobody assumes a downgrade is lossless.
        public int HdrIngameLevel { get; set; } = HdrVibranceHelper.HdrLevelUnset;

        public ApplicationSetting(){ }

        public ApplicationSetting(string name, string fileName, int ingameLevel, ResolutionModeWrapper resolutionSettings, bool isResolutionChangeNeeded, int brightness, int contrast, int gamma)
        {
            this.Name = name;
            this.FileName = fileName;
            this.IngameLevel = ingameLevel;
            this.ResolutionSettings = resolutionSettings;
            this.IsResolutionChangeNeeded = isResolutionChangeNeeded;
            this.Brightness = brightness;
            this.Contrast = contrast;
            this.Gamma = gamma;
        }

        public override bool Equals(object obj)
        {
            // Check for null values and compare run-time types.
            if (obj == null || GetType() != obj.GetType())
                return false;

            ApplicationSetting that = (ApplicationSetting)obj;
            return this.FileName.Equals(that.FileName);
        }

        public override int GetHashCode()
        {
            return this.FileName.GetHashCode();
        }
    }
}
