using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace vibrance.GUI.common
{
    /// <summary>
    /// The cached, change-detecting front end onto IHdrStateReader.ReadAll() (upstream #147) -
    /// mirrors VibranceRestoreHelper/ProfileToggleHelper: a static, deliberately unsynchronised
    /// class (everything that will call it in PR 2 - WinEvent callbacks and the eventual poll
    /// timer - runs on the UI thread, exactly like those two), with a settable reader and a
    /// ResetForTests seam so HdrVibranceFixture can drive it against a fake with no real display.
    ///
    /// A real QueryDisplayConfig sweep is not free enough to run on every foreground-change event
    /// (PR 2's WinEventHookHandler), so this caches the last sweep for CacheTtlMs and only re-runs
    /// it when the cache is stale, explicitly invalidated, or the caller specifically asks for a
    /// fresh comparison via RefreshAndDetectChange (the poll timer's use case: "did HDR just turn
    /// on/off", which by definition cannot be answered from a stale cache).
    /// </summary>
    internal static class HdrStateTracker
    {
        internal const int CacheTtlMs = 1000;

        // Deliberately a Stopwatch, not Environment.TickCount - TickCount wraps every ~24.9 days
        // (int32 milliseconds), which a "has enough time passed" subtraction gets silently wrong
        // the moment a long-running process crosses that boundary. Stopwatch.ElapsedMilliseconds
        // is a long counting from this field's own initialisation, so this class never has to
        // reason about a wraparound at all.
        private static readonly Stopwatch _stopwatch = Stopwatch.StartNew();

        private static IHdrStateReader _reader = new RealHdrStateReader();
        private static Dictionary<string, HdrDisplayInfo> _lastSweep =
            new Dictionary<string, HdrDisplayInfo>(StringComparer.OrdinalIgnoreCase);
        // Named for what it means to a reader (review nitpick - the old name "_hasSweptOnce" lied
        // the moment Invalidate() cleared it back to false on a reader that plainly HAD swept
        // before): true only while _lastSweep is still within CacheTtlMs of _lastSweepElapsedMs
        // AND has not been explicitly invalidated.
        private static bool _cacheIsValid;
        private static long _lastSweepElapsedMs;

        /// <summary>
        /// deviceName's current HDR state, from a sweep no more than CacheTtlMs old. A name absent
        /// from the sweep - including null/empty, a display QueryDisplayConfig cannot resolve a
        /// name or a colour state for, or simply a name this process has never seen - reads as
        /// Unknown, which every caller must treat exactly like Sdr (see HdrDisplayState's own
        /// comment).
        /// </summary>
        internal static HdrDisplayState GetState(string deviceName)
        {
            if (string.IsNullOrEmpty(deviceName))
            {
                return HdrDisplayState.Unknown;
            }

            EnsureFresh();

            HdrDisplayInfo info;
            return _lastSweep.TryGetValue(deviceName, out info) ? info.State : HdrDisplayState.Unknown;
        }

        /// <summary>
        /// Whether any display in the last sweep (no more than CacheTtlMs old) reports HDR
        /// capability, regardless of whether HDR is currently on for it. Feeds
        /// HdrVibranceHelper.DescribeHdrStatus's "no attached display reports HDR support" case.
        /// </summary>
        internal static bool AnyDisplayIsHdrCapable()
        {
            EnsureFresh();

            foreach (HdrDisplayInfo info in _lastSweep.Values)
            {
                if (info.IsHdrCapable)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Forces the NEXT read (GetState, AnyDisplayIsHdrCapable or Snapshot) to re-sweep even if
        /// the TTL has not yet elapsed - does not sweep itself, so calling this alone never touches
        /// the API.
        /// </summary>
        internal static void Invalidate()
        {
            _cacheIsValid = false;
        }

        /// <summary>
        /// Sweeps unconditionally - ignoring the TTL entirely - and reports whether the result
        /// differs from whatever the previous sweep (cached or not) held: a device's HDR state
        /// flipped, or the set of resolvable devices changed (one appeared or disappeared). A
        /// display's IsHdrCapable is not part of this comparison - it is a static capability of
        /// the panel, not something that changes at runtime, so it carries no "something changed"
        /// signal a poll timer would need to act on.
        /// </summary>
        internal static bool RefreshAndDetectChange()
        {
            Dictionary<string, HdrDisplayInfo> previous = _lastSweep;
            Sweep();
            return HasChanged(previous, _lastSweep);
        }

        /// <summary>
        /// A defensive copy of the last sweep (no more than CacheTtlMs old) - safe for a caller to
        /// iterate or hold onto without this class's own dictionary changing underneath it on the
        /// next sweep.
        /// </summary>
        internal static List<HdrDisplayInfo> Snapshot()
        {
            EnsureFresh();
            return new List<HdrDisplayInfo>(_lastSweep.Values);
        }

        // Exists for HdrVibranceFixture only - production code never needs to swap the reader or
        // blank this out mid-run. Mirrors NvidiaDynamicVibranceProxy.ResetForTests' device swap and
        // VibranceRestoreHelper.ResetForTests' clean-slate reset.
        internal static void ResetForTests(IHdrStateReader reader)
        {
            _reader = reader ?? new RealHdrStateReader();
            _lastSweep = new Dictionary<string, HdrDisplayInfo>(StringComparer.OrdinalIgnoreCase);
            _cacheIsValid = false;
            _lastSweepElapsedMs = 0;
        }

        private static void EnsureFresh()
        {
            if (!_cacheIsValid || _stopwatch.ElapsedMilliseconds - _lastSweepElapsedMs >= CacheTtlMs)
            {
                Sweep();
            }
        }

        private static void Sweep()
        {
            List<HdrDisplayInfo> results;
            try
            {
                results = _reader.ReadAll();
            }
            catch (Exception)
            {
                // IHdrStateReader.ReadAll() must never throw by its own contract, but a fake built
                // for a test - or a future implementation that does not honour that contract -
                // must never be able to take this tracker down with it. See HdrVibranceFixture's
                // "a throwing fake reader" check.
                results = null;
            }

            Dictionary<string, HdrDisplayInfo> sweep = new Dictionary<string, HdrDisplayInfo>(StringComparer.OrdinalIgnoreCase);
            if (results != null)
            {
                foreach (HdrDisplayInfo info in results)
                {
                    if (!string.IsNullOrEmpty(info.DeviceName))
                    {
                        sweep[info.DeviceName] = info;
                    }
                }
            }

            _lastSweep = sweep;
            _lastSweepElapsedMs = _stopwatch.ElapsedMilliseconds;
            _cacheIsValid = true;
        }

        private static bool HasChanged(Dictionary<string, HdrDisplayInfo> previous, Dictionary<string, HdrDisplayInfo> current)
        {
            if (previous.Count != current.Count)
            {
                return true;
            }

            foreach (KeyValuePair<string, HdrDisplayInfo> entry in current)
            {
                HdrDisplayInfo previousInfo;
                if (!previous.TryGetValue(entry.Key, out previousInfo) || previousInfo.State != entry.Value.State)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
