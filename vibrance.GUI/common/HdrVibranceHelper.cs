using System.Collections.Generic;

namespace vibrance.GUI.common
{
    /// <summary>
    /// The separate-SDR/HDR-vibrance decision itself (upstream #147), and nothing else - no
    /// device, no Screen, no OS call, no I/O. Mirrors ProfileToggleHelper's own shape: a pure
    /// function a caller turns into an actual write, kept testable without any display at all.
    ///
    /// This slice (PR 1) has no caller yet - ResolveIngameLevel and DescribeHdrStatus are only
    /// exercised by HdrVibranceFixture until the vendor proxies and the settings UI wire them in
    /// (PR 2). A Decide(...)-style overload that also owns a caller's side effect, the same shape
    /// ProfileToggleHelper.Decide has, belongs to that PR too - there is nothing here yet for it
    /// to decide between.
    /// </summary>
    internal static class HdrVibranceHelper
    {
        // ApplicationSetting.HdrIngameLevel's "no separate HDR level configured" sentinel. Cannot
        // collide with a real level - both vendors' minimum level is 0 - and is what a pre-v2.7
        // profile always reads as, because XmlSerializer runs the property initialiser and then
        // finds no <HdrIngameLevel> element in the file to overwrite it with.
        internal const int HdrLevelUnset = -1;

        /// <summary>
        /// Whether hdrIngameLevel names a real, separately-configured HDR level. Deliberately
        /// "&gt;= 0", not "&gt; 0": 0 IS a legal level on both vendors (fully desaturated), not a
        /// second spelling of "unset" - using "&gt;" here would silently lose level 0.
        /// </summary>
        internal static bool HasSeparateHdrLevel(int hdrIngameLevel)
        {
            return hdrIngameLevel >= 0;
        }

        /// <summary>
        /// The vibrance level a profile should use right now: setting.HdrIngameLevel only when the
        /// display is confirmed HDR AND a separate level is actually configured, setting.IngameLevel
        /// in every other case - including HdrDisplayState.Unknown, which must resolve exactly like
        /// Sdr so a detection that cannot answer reproduces today's behaviour rather than silently
        /// switching to an HDR level nobody asked for.
        ///
        /// PR 2 note: AMD's "affect all monitors" write path has no single device name to pass
        /// HdrStateTracker.GetState - the broadcast targets every attached display uniformly, not
        /// one screen. Passing null/empty resolves to Unknown here, which falls back to
        /// IngameLevel exactly like Sdr - safe, but silent: a user who enables "affect all
        /// monitors" and configures HdrIngameLevel would never see it apply, with nothing telling
        /// them why.
        /// </summary>
        internal static int ResolveIngameLevel(ApplicationSetting setting, HdrDisplayState state)
        {
            return state == HdrDisplayState.Hdr && HasSeparateHdrLevel(setting.HdrIngameLevel)
                ? setting.HdrIngameLevel
                : setting.IngameLevel;
        }

        /// <summary>
        /// The one-line HDR status string the settings UI will show next to the HDR slider (PR 2) -
        /// pure given the sweep an IHdrStateReader.ReadAll() call already produced and whether that
        /// reader itself is available, so this can be unit tested with a hand-built sweep and no
        /// display. Always returns exactly one of four messages, in priority order: the API being
        /// entirely unavailable outranks every other case; among the rest, "nothing capable"
        /// (which also covers an empty or null sweep) outranks "capable but off everywhere", which
        /// outranks "on for at least one display".
        /// </summary>
        internal static string DescribeHdrStatus(List<HdrDisplayInfo> sweep, bool isReaderAvailable)
        {
            if (!isReaderAvailable)
            {
                return "vibranceGUI cannot detect HDR on this version of Windows, so this level will never be used.";
            }

            bool anyCapable = false;
            List<HdrDisplayInfo> activeHdrDisplays = new List<HdrDisplayInfo>();
            if (sweep != null)
            {
                foreach (HdrDisplayInfo display in sweep)
                {
                    if (display.IsHdrCapable)
                    {
                        anyCapable = true;
                    }
                    if (display.State == HdrDisplayState.Hdr)
                    {
                        activeHdrDisplays.Add(display);
                    }
                }
            }

            // Both conditions, not just !anyCapable (review nitpick): a display genuinely active
            // in HDR (State == Hdr) is definitionally HDR-capable, so an active display must never
            // be reported as "no support" even if IsHdrCapable somehow said otherwise for it. That
            // inconsistency cannot arise today - every path into IsHdrCapable ties it to State -
            // but this keeps the message honest rather than relying on staying that way forever.
            if (!anyCapable && activeHdrDisplays.Count == 0)
            {
                return "No attached display reports HDR support.";
            }

            if (activeHdrDisplays.Count == 0)
            {
                return "Windows HDR is currently off on every attached display.";
            }

            string first = activeHdrDisplays[0].DeviceName;
            int more = activeHdrDisplays.Count - 1;
            return more > 0
                ? string.Format("Windows HDR is currently on for {0}, and {1} more.", first, more)
                : string.Format("Windows HDR is currently on for {0}.", first);
        }
    }
}
