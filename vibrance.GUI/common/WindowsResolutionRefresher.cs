using System;
using System.Collections.Generic;

namespace vibrance.GUI.common
{
    // The extraction target of RebuildWindowsResolutionSettings (VibranceGUI.cs) - pulled out so
    // ResolutionChangeFixture can drive the refresh logic through a fake IDisplayModeDevice, with
    // no Screen, no Form and no real display anywhere in the call stack. No
    // "using System.Windows.Forms" here, for the same reason ResolutionHelper.cs has none: this is
    // refresh/capture bookkeeping, not UI, and has to stay reachable from a self test that runs
    // with no message loop at all.
    internal static class WindowsResolutionRefresher
    {
        // Delegates to the overload below against ResolutionHelper.RealDevice - the only
        // production IDisplayModeDevice. VibranceGUI itself only ever needs this overload;
        // ResolutionChangeFixture drives the IDisplayModeDevice overload directly instead, exactly
        // as ChangeResolutionEx/IsResolutionChangeNeeded already split in ResolutionHelper.cs.
        internal static void Refresh(
            Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>> windowsResolutionSettings,
            Dictionary<string, ResolutionModeWrapper> lastKnownWindowsModes,
            IList<string> attachedDeviceNames,
            bool preserveCapturedMode,
            bool reportUnreadableDevices,
            Action<string> onUnreadableDevice)
        {
            Refresh(ResolutionHelper.RealDevice, windowsResolutionSettings, lastKnownWindowsModes, attachedDeviceNames,
                preserveCapturedMode, reportUnreadableDevices, onUnreadableDevice);
        }

        // The seam ResolutionChangeFixture drives directly - see the public overload above for
        // why. Mutates windowsResolutionSettings IN PLACE (Clear() then re-add) rather than
        // replacing it - both vendor proxies hold a reference to this very instance (NVIDIA's is
        // static), so only in-place mutation is visible to them.
        internal static void Refresh(
            IDisplayModeDevice device,
            Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>> windowsResolutionSettings,
            Dictionary<string, ResolutionModeWrapper> lastKnownWindowsModes,
            IList<string> attachedDeviceNames,
            bool preserveCapturedMode,
            bool reportUnreadableDevices,
            Action<string> onUnreadableDevice)
        {
            // This is the single most dangerous line in the resolution-change fix: if a refresh
            // runs while a game's resolution change is currently applied, a live read of "the
            // current mode" for the game's own screen returns the GAME's mode, not the desktop's.
            // Overwriting the captured "Windows resolution" (Item1) with that would strand the
            // desktop at the game's resolution forever - the revert path compares against Item1, so
            // once it has silently become the game's own mode, "reverting" turns into a no-op that
            // still reports success. A game going fullscreen is exactly the kind of change that
            // fires DisplaySettingsChanged, so this is not a rare interleaving to guard against.
            //
            // While a resolution change is applied, every screen this dictionary already has an
            // entry for keeps its previously captured Item1 untouched, and only Item2 (the
            // device's supported-mode list, a property of the device rather than of whichever mode
            // happens to be active right now) is refreshed. A screen with no previous entry still
            // needs one captured fresh - it cannot be the screen the game is running on, since that
            // one is already recorded.
            Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>> previous =
                new Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>>(windowsResolutionSettings);

            windowsResolutionSettings.Clear();
            foreach (string deviceName in attachedDeviceNames)
            {
                Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>> existing;
                bool hasExisting = previous.TryGetValue(deviceName, out existing);

                // Item2 is a property of the device's capability, not of whichever mode happens to
                // be active right now (the comment above already relies on that to justify reusing
                // it while a game's own change is applied) - so a device this dictionary already
                // has an entry for reuses that SAME List<ResolutionModeWrapper> instance rather than
                // re-enumerating. Two reasons this matters beyond the obvious P/Invoke cost (up to
                // several hundred EnumDisplaySettings calls per screen, on the UI thread): first,
                // vibranceGUI's OWN resolution changes also fire DisplaySettingsChanged, so an
                // unconditional re-enumerate here would run twice per alt-tab cycle; second, reusing
                // the identical instance (not a fresh copy) is what keeps _supportedResolutionList -
                // captured once, in the constructor, and readonly - from silently going stale after
                // a refresh, since it then still points at the very list being kept up to date here.
                List<ResolutionModeWrapper> availableResolutions = hasExisting
                    ? existing.Item2
                    : ResolutionHelper.EnumerateSupportedResolutionModes(device, deviceName);

                ResolutionModeWrapper capturedMode = null;
                if (preserveCapturedMode)
                {
                    if (hasExisting)
                    {
                        capturedMode = existing.Item1;
                    }
                    else
                    {
                        // A device that dropped out of the dictionary during an earlier refresh -
                        // because it was unattached at that moment - but is attached again now:
                        // fall back to the mode last captured for it instead of a live read. A live
                        // read here is exactly as dangerous as the one the comment above guards
                        // against - if THIS device is the one currently running the game, it would
                        // capture the game's mode, not the desktop's. See
                        // CheckDetachedDeviceKeepsItsCapturedModeAcrossAReattach
                        // (ResolutionChangeFixture.cs) for the scenario this exists to cover, and
                        // CheckReattachedDeviceReenumeratesItsSupportedModes for why this is the
                        // ONLY thing carried across the gap - Item2 above still re-enumerates from
                        // scratch, since hasExisting is false here regardless of this fallback.
                        lastKnownWindowsModes.TryGetValue(deviceName, out capturedMode);
                    }
                }

                if (capturedMode == null)
                {
                    Devmode currentResolutionMode;
                    if (device.TryGetCurrentMode(deviceName, out currentResolutionMode))
                    {
                        capturedMode = new ResolutionModeWrapper(currentResolutionMode);
                    }
                    else
                    {
                        if (reportUnreadableDevices && onUnreadableDevice != null)
                        {
                            onUnreadableDevice(deviceName);
                        }
                        continue;
                    }
                }

                // .Add, not the indexer: attachedDeviceNames is projected from Screen.AllScreens,
                // which cannot report the same device name twice - a duplicate here would be a bug
                // worth an exception, not a silent overwrite.
                windowsResolutionSettings.Add(deviceName,
                    new Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>(capturedMode, availableResolutions));

                // Recorded for every device this refresh keeps or (re)captures an entry for -
                // including one whose mode was just read fresh - so the fallback above always has
                // the most recently known value available, not just whatever was captured the one
                // time a device first appeared. Deliberately never pruned for a device that drops
                // out of attachedDeviceNames - see _lastKnownWindowsModes's own comment
                // (VibranceGUI.cs) for why that is safe to leave unbounded in practice.
                lastKnownWindowsModes[deviceName] = capturedMode;
            }
        }
    }
}
