using System;
using System.Collections.Generic;

namespace vibrance.GUI.common
{
    /// <summary>
    /// The on-disk shape ResolutionRestoreStore round-trips through XmlSerializer at
    /// %APPDATA%\vibranceGUI\resolutionRestore.xml - D4's resolution half, alongside
    /// VibranceRestoreRecord (vibranceRestore.xml) for the vibrance half. A SEPARATE file,
    /// deliberately: ResolutionHelper is a shared static both vendor proxies drive (unlike
    /// vibrance, which is NVIDIA-only because INvidiaVibranceDevice is), so this record's write
    /// cadence has nothing to do with vibrance's own - combining the two into one file would mean
    /// every vibrance journal write also rewrites whatever resolution entries happen to be
    /// pending (and vice versa), exactly issue #156's per-event-I/O mistake reintroduced one layer
    /// further down, this time between two features that otherwise never touch each other.
    ///
    /// Public with a public parameterless constructor and plain public auto-properties throughout,
    /// exactly as VibranceRestoreRecord is shaped - XmlSerializer requires both.
    /// </summary>
    public class ResolutionRestoreRecord
    {
        // Bumped only if this shape ever needs an incompatible change. Nothing in this codebase
        // currently branches on it - written from day one so a future version has something to
        // check without having to guess about every file an earlier version ever wrote. See
        // VibranceRestoreRecord.SchemaVersion for the identical reasoning.
        public int SchemaVersion { get; set; }

        // DIAGNOSTIC ONLY - never a gate, and this is the one field that genuinely differs in kind
        // from VibranceRestoreRecord.Vendor, not just in value. Vibrance needs a hard discard gate
        // on this field because NVIDIA's 0-63 vibrance range overlaps AMD's 0-300, so a foreign
        // AppliedLevel can coincidentally collide with a live level on the WRONG vendor's display
        // and be misread as a match. There is no equivalent collision risk here: AppliedMode/
        // WindowsMode are Devmode-derived width/height/bits-per-pel/refresh values, which mean the
        // same thing on every vendor's driver - a mode is either the one the live display is
        // showing or it is not, regardless of which proxy wrote the record. Discarding a record
        // here just because it happened to be written under a different vendor would only throw
        // away a perfectly readable restore obligation for no safety gained. Do NOT copy
        // ReplayPersistedVibranceRestore's wholesale-discard-on-Vendor-mismatch branch across to
        // this record's own replay - that guard exists for a reason that does not apply here.
        // Stamped from the same VibranceRestoreHelper.VendorTag vibrance already uses (there is
        // deliberately no second VendorTag) - see ResolutionRestoreHelper's own header for why
        // sharing that one field is safe even though this class's replay never reads it back.
        public string Vendor { get; set; }

        public DateTime WrittenUtc { get; set; }

        public List<ResolutionRestoreEntry> Displays { get; set; }

        public ResolutionRestoreRecord()
        {
            Displays = new List<ResolutionRestoreEntry>();
        }
    }

    /// <summary>
    /// One display's entry in ResolutionRestoreRecord.Displays.
    ///
    /// MonitorId is the key: a monitor device interface path from MonitorIdentity.TryGetMonitorId
    /// - see that class's own header for why "\\.\DISPLAYn" cannot be used here instead, and
    /// VibranceRestoreEntry's own header for the identical reasoning this class shares.
    ///
    /// DeviceNameAtWrite is diagnostic ONLY - whatever "\\.\DISPLAYn" MonitorId happened to resolve
    /// to at the moment this entry was written, and is very often stale after a reboot. Nothing may
    /// ever read DeviceNameAtWrite back to decide anything on replay;
    /// MonitorIdentity.TryResolveDeviceName(MonitorId) is the only sanctioned way to find the
    /// CURRENT "\\.\DISPLAYn" for an entry.
    ///
    /// AppliedMode is the GAME mode this application actually set on this display before the
    /// process went away - it is what the replay's verify gate
    /// (ResolutionHelper.TryRestorePersistedMode) compares a live read-back against, to tell
    /// "still ours" apart from "the user already changed this by hand". WindowsMode is the desktop
    /// mode owed back to the display - the restore TARGET, not something the gate verifies against
    /// (see TryRestorePersistedMode's own header for the full decision table).
    ///
    /// ResolutionModeWrapper needs no changes to live here: it is already a public class with a
    /// public parameterless constructor and public auto-properties, and already round-trips
    /// through XmlSerializer as a nested type inside applicationData.xml (ApplicationSetting.
    /// ResolutionSettings) - nested directly here rather than flattened into five loose fields for
    /// the same reason.
    /// </summary>
    public class ResolutionRestoreEntry
    {
        public string MonitorId { get; set; }
        public string DeviceNameAtWrite { get; set; }
        public ResolutionModeWrapper AppliedMode { get; set; }
        public ResolutionModeWrapper WindowsMode { get; set; }

        public ResolutionRestoreEntry()
        {
        }
    }
}
