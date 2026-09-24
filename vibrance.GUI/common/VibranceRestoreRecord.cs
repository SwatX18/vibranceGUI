using System;
using System.Collections.Generic;

namespace vibrance.GUI.common
{
    /// <summary>
    /// The on-disk shape VibranceRestoreStore round-trips through XmlSerializer at
    /// %APPDATA%\vibranceGUI\vibranceRestore.xml - see that class's own header for the atomic-
    /// write contract, and VibranceRestoreHelper for when a write or delete actually happens.
    /// Public with a public parameterless constructor and plain public auto-properties throughout,
    /// exactly as ApplicationSetting is shaped - XmlSerializer requires both, the same reason
    /// SettingsController already serialises List&lt;ApplicationSetting&gt; the same way.
    /// </summary>
    public class VibranceRestoreRecord
    {
        // Bumped only if this shape ever needs an incompatible change. Nothing in this codebase
        // currently branches on it - a record from a future or unrecognised version is read the
        // same as any other, and simply reflects whatever XmlSerializer could deserialise from it
        // - but it is written from day one so a future version has something to check without
        // having to guess about every file an earlier version ever wrote.
        public int SchemaVersion { get; set; }

        // Which vendor's proxy wrote this record (see VibranceRestoreHelper.VendorTag) - a real
        // gate on replay, not merely diagnostic: NVIDIA's vibrance range is 0-63, AMD's is 0-300,
        // so a record written by the OTHER vendor is discarded wholesale by
        // NvidiaDynamicVibranceProxy.ReplayPersistedVibranceRestore before any entry is even
        // looked at, rather than having its AppliedLevel values compared against a live NVIDIA
        // display and potentially misread (an AMD-range level could coincidentally collide with a
        // valid NVIDIA one). Persistence itself is vendor-agnostic - both proxies journal through
        // VibranceRestoreHelper - so this field is what keeps a record AMD wrote inert on an NVIDIA
        // machine, rather than misinterpreted.
        public string Vendor { get; set; }

        public DateTime WrittenUtc { get; set; }

        public List<VibranceRestoreEntry> Displays { get; set; }

        public VibranceRestoreRecord()
        {
            Displays = new List<VibranceRestoreEntry>();
        }
    }

    /// <summary>
    /// One display's entry in VibranceRestoreRecord.Displays.
    ///
    /// MonitorId is the key: a monitor device interface path from MonitorIdentity.TryGetMonitorId
    /// - see that class's own header for why "\\.\DISPLAYn" cannot be used here instead.
    ///
    /// DeviceNameAtWrite is diagnostic ONLY - whatever "\\.\DISPLAYn" MonitorId happened to resolve
    /// to at the moment this entry was written, and is very often stale after a reboot (the entire
    /// reason MonitorId exists in the first place). Nothing may ever read DeviceNameAtWrite back to
    /// decide anything on replay; MonitorIdentity.TryResolveDeviceName(MonitorId) is the only
    /// sanctioned way to find the CURRENT "\\.\DISPLAYn" for an entry.
    ///
    /// AppliedLevel is the GAME level this application actually wrote to this display before the
    /// process went away - it is what the replay's verify gate (VibranceGUI.backgroundWorker_
    /// DoWork's call site, implemented in NvidiaDynamicVibranceProxy.ReplayPersistedVibranceRestore)
    /// compares a live IsAtLevel read-back against, to tell "still ours" apart from "the user
    /// already changed this by hand" - see that method's own header comment for the full decision
    /// table.
    /// </summary>
    public class VibranceRestoreEntry
    {
        public string MonitorId { get; set; }
        public string DeviceNameAtWrite { get; set; }
        public int AppliedLevel { get; set; }

        public VibranceRestoreEntry()
        {
        }
    }
}
