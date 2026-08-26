using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace vibrance.GUI.common
{
    // The seam between the capture/compose/restore logic below and the actual display hardware.
    // RealGammaDevice (bottom of this file) is the only production implementation; GammaRestoreFixture
    // supplies a fake backed by an in-memory dictionary so the session logic - the part that got B1
    // and a follow-up defect ("S1") past review and QA - can be driven through real apply/restore
    // cycles, including forced failures, without a display.
    internal interface IGammaDevice
    {
        bool TryRead(string deviceName, out DeviceGammaRampHelper.GammaRamp ramp);
        bool Write(string deviceName, DeviceGammaRampHelper.GammaRamp ramp);
    }

    class DeviceGammaRampHelper
    {
        // Keyed by Screen.DeviceName (e.g. "\\.\DISPLAY1") - the same string CreateDC consumes and
        // _windowsResolutionSettings is already keyed by. Holds the TRUE calibration baseline for
        // that device - the ramp that was on the monitor before this application ever touched it -
        // so a restore can put back what was really there instead of synthesising an identity ramp
        // from the user's brightness/contrast/gamma sliders (issue #128).
        //
        // This is durable across an entire run, not per-session: it is captured once and then kept
        // forever (see _lastWrittenGammaRamps below for how a re-capture is still triggered safely
        // when it is genuinely needed). A prior version of this fix dropped it on every restore,
        // which meant the NEXT ApplyGameGammaRamp re-captured whatever was currently on screen -
        // this application's own previous restore output - as the new "baseline". For a non-neutral
        // Windows slider LUT u, compose(compose(B, u), u) != compose(B, u), so that re-capture
        // compounded once per alt-tab out of a game: the sliders re-applied themselves session
        // after session, and once a channel value pinned to 0 or 65535 it could never recover
        // (issue found in review, before this shipped - "B1"). _devicesHoldingGameRamp below is
        // the dictionary restore actually iterates and clears; this one never shrinks on its own.
        //
        // Everything here runs on the UI thread - the proxy is constructed on it, WINEVENT_OUTOFCONTEXT
        // callbacks arrive on the thread that registered them, and HandleDvcExit comes from
        // Form1_FormClosing - so this is deliberately unsynchronised, same as the
        // _gameScreen/_vibranceInfo statics it sits alongside.
        private static readonly Dictionary<string, GammaRamp> _capturedGammaRamps = new Dictionary<string, GammaRamp>();

        // The exact ramp this application itself last wrote to that device, whether by an apply or
        // a restore. ApplyGameGammaRamp only trusts the existing _capturedGammaRamps entry - and so
        // only skips re-capturing - when the device's current ramp still matches this value; any
        // mismatch (a hot-plugged monitor now answering to the same DeviceName, an ICC profile or
        // f.lux change landing mid-session, or simply nothing captured yet) is treated as "we do
        // not know what is really on this device" and re-baselines from a fresh read.
        //
        // Deliberately NEVER removed on a failed write, including when nothing was written at all
        // (e.g. CreateDC itself failing) - a second defect found in review ("S1"). The entry is
        // consulted only as a content comparison at the point of use, so removing it cannot help:
        // if the device's content differs from the stale record, the comparison already re-baselines
        // on its own; if the content still matches, the record is still correct and removing it
        // forces a wrong re-baseline instead - the device's own game ramp gets adopted as the "true"
        // baseline, permanently, since a neutral Windows slider setting (the default) makes
        // compose(gameRamp, neutral) == gameRamp exactly, so nothing about a later restore would
        // ever reveal the mistake. A false *match* (stale record, content coincidentally equal) is
        // harmless and self-corrects at the next successful write; a false *negative* (record
        // dropped when it was still accurate) is not recoverable. Hence: only ever add or overwrite
        // this dictionary, never remove from it, anywhere.
        private static readonly Dictionary<string, GammaRamp> _lastWrittenGammaRamps = new Dictionary<string, GammaRamp>();

        // The restore work-list: devices that currently hold a ramp this application applied and
        // have not yet been restored. Separate from _capturedGammaRamps on purpose (S2) - that
        // dictionary is a durable calibration store for every device ever touched this run, and
        // iterating IT for restore meant every alt-tab out of a game re-wrote every device ever
        // baselined, including ones this session never applied anything to (an external change on
        // an untouched monitor - f.lux at sunset, say - would get silently stomped back to its old
        // captured baseline on someone else's game exit). Added on a successful apply, iterated and
        // cleared by RestoreCapturedGammaRamps regardless of that device's individual outcome - one
        // restore attempt per device per triggering event, matching how the proxies already treat
        // isColorSettingApplied.
        private static readonly HashSet<string> _devicesHoldingGameRamp = new HashSet<string>();

        // Suppresses repeat log calls for a device that is failing, on either the apply or the
        // restore path - without this, a foreground-change storm against a broken device would redo
        // the failing operation plus a synchronous file write on the UI thread on every single
        // event. One set per device, not one per failure mode: a device that fails a read (logged)
        // and later fails a write on the same device has that second failure suppressed too, not
        // just a repeat of the same failure. Cleared as soon as any operation against that device
        // succeeds, so a failure that starts again later - of any kind - is logged once more.
        private static readonly HashSet<string> _loggedDeviceFailures = new HashSet<string>();

        // The only production IGammaDevice. Everything above is exercised against this by the two
        // public, hardware-touching overloads below; GammaRestoreFixture drives the internal
        // overloads with its own fake instead.
        private static readonly IGammaDevice _realDevice = new RealGammaDevice();

        // All four collections above are static, so every call in this process shares them - real
        // hardware use and every fixture check alike. GammaRestoreFixture's checks currently stay
        // isolated from each other only because each one drains its own device(s) out of
        // _devicesHoldingGameRamp and uses DeviceName strings no other check reuses - an ordering
        // dependency, not a guarantee. Exists so each seam-based check can start from a clean slate
        // instead of relying on that.
        internal static void ResetForTests()
        {
            _capturedGammaRamps.Clear();
            _lastWrittenGammaRamps.Clear();
            _devicesHoldingGameRamp.Clear();
            _loggedDeviceFailures.Clear();
        }

        /// <summary>
        /// Captures screen's current gamma ramp as the undo baseline for its DeviceName the first
        /// time it is touched - or re-captures it if the device no longer holds what this
        /// application itself last wrote there - then composes brightness/contrast/gamma on top of
        /// that baseline (rather than on top of the identity ramp) and writes the result. Returns
        /// true only when a baseline is held and the write itself succeeded - i.e. only when this
        /// write is known to be undoable via <see cref="RestoreCapturedGammaRamps"/>. Never writes
        /// anything on a bail.
        /// </summary>
        public static bool ApplyGameGammaRamp(Screen screen, int brightness, int contrast, int gamma)
        {
            return ApplyGameGammaRamp(_realDevice, screen.DeviceName, brightness, contrast, gamma);
        }

        // The seam GammaRestoreFixture drives directly - same logic as the public overload above,
        // parameterised over IGammaDevice and a bare DeviceName instead of a live Screen so it can
        // run against a fake with no display at all.
        internal static bool ApplyGameGammaRamp(IGammaDevice device, string deviceName, int brightness, int contrast, int gamma)
        {
            // Tracks whether THIS call is the one that (re-)captured the baseline below, so a bail
            // further down can drop it again. Only what this call captured may be dropped here - a
            // baseline an earlier, successful ApplyGameGammaRamp stored must never be removed by a
            // later call that happens to fail, or a ramp that really is on the monitor would be
            // stranded with no way back. Defensive against a failure landing immediately after a
            // fresh capture - not reachable through the current callers in steady state, but
            // genuinely load-bearing whenever the branch below re-baselines (a hot-plugged monitor,
            // an ICC/f.lux change mid-session, or the very first capture), which is no longer a
            // one-time event now that re-baselining can happen more than once per DeviceName over a
            // run.
            bool capturedNow = false;

            // Always read the device's current ramp, even when a baseline is already held - this is
            // what lets the comparison below detect a device that no longer holds our last write.
            GammaRamp candidate;
            if (!device.TryRead(deviceName, out candidate))
            {
                if (_loggedDeviceFailures.Add(deviceName))
                {
                    Program.LogSafely(string.Format("Failed to read the current gamma ramp for screen {0}, refusing to apply a ramp that could not be undone", deviceName));
                }
                return false;
            }

            GammaRamp baseline;
            GammaRamp lastWritten;
            // Re-baseline whenever there is no retained baseline yet, no record of what this
            // application last wrote, or the device's current ramp no longer matches that record -
            // in every other case the retained baseline is still trustworthy and must be reused, or
            // a non-neutral Windows slider LUT would compound across sessions (see the class-level
            // comment on _capturedGammaRamps - "B1").
            bool needsRebaseline = !_capturedGammaRamps.TryGetValue(deviceName, out baseline) ||
                !_lastWrittenGammaRamps.TryGetValue(deviceName, out lastWritten) ||
                !candidate.Equals(lastWritten);

            if (needsRebaseline)
            {
                if (!IsPlausibleGammaRamp(candidate))
                {
                    if (_loggedDeviceFailures.Add(deviceName))
                    {
                        Program.LogSafely(string.Format("The gamma ramp read back from screen {0} does not look plausible, refusing to apply a ramp that could not be undone", deviceName));
                    }
                    return false;
                }
                _capturedGammaRamps[deviceName] = candidate;
                baseline = candidate;
                capturedNow = true;
            }

            GammaRamp composed = ComposeGammaRamp(baseline, CalculateLUT(brightness: (double)brightness / 100, contrast: (double)contrast / 100, gamma: (double)gamma / 100));
            if (!IsPlausibleGammaRamp(composed))
            {
                if (_loggedDeviceFailures.Add(deviceName))
                {
                    Program.LogSafely(string.Format("The gamma ramp composed for screen {0} does not look plausible, refusing to apply it", deviceName));
                }
                if (capturedNow)
                {
                    _capturedGammaRamps.Remove(deviceName);
                }
                return false;
            }

            bool applied = device.Write(deviceName, composed);
            if (applied)
            {
                _lastWrittenGammaRamps[deviceName] = composed;
                _devicesHoldingGameRamp.Add(deviceName);
                _loggedDeviceFailures.Remove(deviceName);
            }
            else
            {
                if (_loggedDeviceFailures.Add(deviceName))
                {
                    Program.LogSafely(string.Format("Failed to set device gamma ramp for screen {0}", deviceName));
                }
                if (capturedNow)
                {
                    // Nothing landed on the display, so do not leave behind an undo for a write
                    // that never happened - see the capturedNow comment above for why only this
                    // call's own capture is eligible to be dropped. _lastWrittenGammaRamps is left
                    // exactly as it was - see its own class-level comment ("S1") for why a failed
                    // write must never remove an existing record there.
                    _capturedGammaRamps.Remove(deviceName);
                }
            }
            return applied;
        }

        /// <summary>
        /// Writes brightness/contrast/gamma composed on top of the captured baseline back to every
        /// device this application currently holds a game ramp on (see
        /// <see cref="_devicesHoldingGameRamp"/> - "S2"), then clears that device from the work-list
        /// regardless of the individual outcome. The baseline itself is durable and is never removed
        /// here or anywhere else in this class (see <see cref="_capturedGammaRamps"/> - "B1"); what
        /// this call updates is the record of what it actually wrote, so the next
        /// <see cref="ApplyGameGammaRamp"/> can tell whether the device still holds it. No-op when
        /// nothing is currently held.
        /// </summary>
        public static void RestoreCapturedGammaRamps(int brightness, int contrast, int gamma)
        {
            RestoreCapturedGammaRamps(_realDevice, brightness, contrast, gamma);
        }

        // The seam GammaRestoreFixture drives directly - see ApplyGameGammaRamp's internal overload
        // above for why.
        internal static void RestoreCapturedGammaRamps(IGammaDevice device, int brightness, int contrast, int gamma)
        {
            if (_devicesHoldingGameRamp.Count == 0)
            {
                return;
            }

            ushort[] sourceLut = CalculateLUT(brightness: (double)brightness / 100, contrast: (double)contrast / 100, gamma: (double)gamma / 100);

            // Snapshotted because _devicesHoldingGameRamp is itself mutated (entries removed) while
            // iterating below.
            List<string> deviceNames = new List<string>(_devicesHoldingGameRamp);
            foreach (string deviceName in deviceNames)
            {
                try
                {
                    RestoreCapturedGammaRamp(device, deviceName, sourceLut);
                }
                catch (Exception ex)
                {
                    Program.LogSafely(string.Format("Failed to restore the gamma ramp for screen {0}: {1}", deviceName, ex));
                    // Deliberately does NOT touch _lastWrittenGammaRamps here - see that
                    // dictionary's own class-level comment ("S1") for why a write whose outcome is
                    // uncertain must never cause an existing record to be removed.
                }
                finally
                {
                    _devicesHoldingGameRamp.Remove(deviceName);
                }
            }
        }

        private static void RestoreCapturedGammaRamp(IGammaDevice device, string deviceName, ushort[] sourceLut)
        {
            // TryGetValue rather than the indexer: _devicesHoldingGameRamp and _capturedGammaRamps
            // are two separate collections kept in sync only by the invariant that nothing adds to
            // the former without a captured baseline already existing for the same DeviceName. Not
            // reachable through the two proxies, but reachable through the internal overload the
            // fixture drives directly (a successful apply, an external content change forcing a
            // re-baseline that then has ApplyGameGammaRamp's own write fail before
            // _capturedGammaRamps is repopulated, and the device is still in the work-list from the
            // earlier successful apply) - degrade safely instead of throwing out of a WinEvent
            // callback for what would otherwise be a caller-invariant violation.
            GammaRamp baseline;
            if (!_capturedGammaRamps.TryGetValue(deviceName, out baseline))
            {
                Program.LogSafely(string.Format("No captured baseline for screen {0}, skipping its restore", deviceName));
                return;
            }
            GammaRamp composed = ComposeGammaRamp(baseline, sourceLut);
            if (device.Write(deviceName, composed))
            {
                _lastWrittenGammaRamps[deviceName] = composed;
                _loggedDeviceFailures.Remove(deviceName);
            }
            else if (_loggedDeviceFailures.Add(deviceName))
            {
                Program.LogSafely(string.Format("Failed to restore device gamma ramp for screen {0}", deviceName));
            }
        }

        /// <summary>
        /// Reads the current gamma ramp for the selected screen. Surfaces GetDeviceGammaRamp's own
        /// success/failure instead of hiding it behind a struct that is indistinguishable from a
        /// real all-black ramp. The returned ramp's arrays are freshly allocated and owned
        /// exclusively by the caller.
        /// </summary>
        public static bool TryGetGammaRamp(Screen screen, out GammaRamp ramp)
        {
            return _realDevice.TryRead(screen.DeviceName, out ramp);
        }

        /// <summary>
        /// Rejects a ramp that cannot be a genuine capture: a null channel, a channel of the wrong
        /// length, or a channel that is entirely zero (a failed read reads back as all zeroes, and
        /// so does a genuinely dead/black channel - neither is safe to treat as an undo baseline).
        /// Deliberately does not require monotonicity - legitimate calibration and accessibility
        /// ramps are not always monotonic - and does not reject a ramp merely because it is dim.
        /// </summary>
        public static bool IsPlausibleGammaRamp(GammaRamp ramp)
        {
            return IsPlausibleChannel(ramp.Red) && IsPlausibleChannel(ramp.Green) && IsPlausibleChannel(ramp.Blue);
        }

        private static bool IsPlausibleChannel(UInt16[] channel)
        {
            if (channel == null || channel.Length != GAMMA_RAMP_SIZE)
            {
                return false;
            }
            for (int i = 0; i < channel.Length; i++)
            {
                if (channel[i] != 0)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Runs sourceLut first and the baseline (the monitor's own calibration) last, so
        /// composing on top of an identity baseline reproduces sourceLut unchanged, and composing
        /// an identity sourceLut on top of any baseline reproduces that baseline unchanged. Pure:
        /// allocates a new GammaRamp and never aliases baseline's arrays.
        /// </summary>
        public static GammaRamp ComposeGammaRamp(GammaRamp baseline, ushort[] sourceLut)
        {
            ushort[] red = new ushort[GAMMA_RAMP_SIZE];
            ushort[] green = new ushort[GAMMA_RAMP_SIZE];
            ushort[] blue = new ushort[GAMMA_RAMP_SIZE];
            for (int i = 0; i < GAMMA_RAMP_SIZE; i++)
            {
                red[i] = Sample(baseline.Red, sourceLut[i]);
                green[i] = Sample(baseline.Green, sourceLut[i]);
                blue[i] = Sample(baseline.Blue, sourceLut[i]);
            }
            return new GammaRamp(red, green, blue);
        }

        // The captured ramp is indexed 0..255 and 255 * 257 == 65535 exactly, so value / 257.0 is
        // that index directly. Interpolating rather than truncating to an 8-bit index is what makes
        // an identity baseline reproduce value unchanged - which is why composing on the apply path
        // is invisible to a user with no calibration.
        private static ushort Sample(ushort[] channel, ushort value)
        {
            double x = value / 257.0;
            int lo = (int)x;
            if (lo >= GAMMA_RAMP_SIZE - 1)
                return channel[GAMMA_RAMP_SIZE - 1];
            double t = x - lo;
            return (ushort)Math.Round(channel[lo] * (1.0 - t) + channel[lo + 1] * t);
        }

        private static IntPtr GetDeviceContext(string deviceName)
        {
            var hdc = NativeAPI.CreateDC(deviceName, null, null, IntPtr.Zero);
            if (hdc == IntPtr.Zero)
            {
                // Shares _loggedDeviceFailures with ApplyGameGammaRamp/RestoreCapturedGammaRamp -
                // this is reached from both paths, and a persistently unreachable device would
                // otherwise log on every foreground change no matter which path found it.
                if (_loggedDeviceFailures.Add(deviceName))
                {
                    Program.LogSafely(string.Format("Failed to create screen device context for screen {0}", deviceName));
                }
            }
            return hdc;
        }

        private static void ReleaseDeviceContext(IntPtr hdc)
        {
            //the device context was created with CreateDC, so it must be freed with DeleteDC (ReleaseDC does not work here)
            if (!NativeAPI.DeleteDC(hdc))
            {
                Program.LogSafely(string.Format("Failed to release device context handle {0}", hdc.ToString()));
            }
        }

        // The production IGammaDevice: CreateDC/GetDeviceGammaRamp/SetDeviceGammaRamp/DeleteDC
        // against a real display, exactly as ApplyGameGammaRamp/RestoreCapturedGammaRamp called
        // them directly before the device seam existed.
        private class RealGammaDevice : IGammaDevice
        {
            public bool TryRead(string deviceName, out GammaRamp ramp)
            {
                ramp = new GammaRamp(new ushort[GAMMA_RAMP_SIZE], new ushort[GAMMA_RAMP_SIZE], new ushort[GAMMA_RAMP_SIZE]);
                IntPtr hdc = GetDeviceContext(deviceName);
                if (hdc == IntPtr.Zero)
                {
                    return false;
                }
                try
                {
                    return NativeAPI.GetDeviceGammaRamp(hdc, ref ramp);
                }
                finally
                {
                    ReleaseDeviceContext(hdc);
                }
            }

            public bool Write(string deviceName, GammaRamp ramp)
            {
                IntPtr hdc = GetDeviceContext(deviceName);
                if (hdc == IntPtr.Zero)
                {
                    return false;
                }
                try
                {
                    return NativeAPI.SetDeviceGammaRamp(hdc, ref ramp);
                }
                finally
                {
                    ReleaseDeviceContext(hdc);
                }
            }
        }

        /// <summary>
        /// Calculate the device lookup table (LUT). Credits to https://github.com/falahati in https://github.com/falahati/NvAPIWrapper/issues/20#issuecomment-634551206
        /// </summary>
        /// <param name="brightness">The brightness value</param>
        /// <param name="contrast">The contrast value</param>
        /// <param name="gamma">The Gamma value</param>
        /// <returns></returns>
        public static ushort[] CalculateLUT(double brightness = 0.5, double contrast = 0.5, double gamma = 1)
        {

            const int dataPoints = 256;

            // Limit gamma in range [0.4-2.8]
            gamma = Math.Min(Math.Max(gamma, 0.4), 2.8);

            // Normalize contrast in range [-1,1]
            contrast = (Math.Min(Math.Max(contrast, 0), 1) - 0.5) * 2;

            // Normalize brightness in range [-1,1]
            brightness = (Math.Min(Math.Max(brightness, 0), 1) - 0.5) * 2;

            // Calculate curve offset resulted from contrast
            var offset = contrast > 0 ? contrast * -25.4 : contrast * -32;

            // Calculate the total range of curve
            var range = (dataPoints - 1) + offset * 2;

            // Add brightness to the curve offset
            offset += brightness * (range / 5);

            // Fill the gamma curve
            var result = new ushort[dataPoints];
            for (var i = 0; i < result.Length; i++)
            {
                var factor = (i + offset) / range;

                // Pre-existing, benign: a negative factor raised to a non-integral power is NaN
                // under Math.Pow - and 1/gamma is integral for more than just gamma == 1 (gamma ==
                // 0.5 is reachable via trackBarGamma's range and gives exponent exactly 2.0).
                // Math.Min/Math.Max below do NOT clamp NaN - they propagate it, confirmed directly -
                // so factor is still NaN when it reaches the (ushort)Math.Round(...) cast further
                // down. Confirmed directly too: (int)double.NaN is int.MinValue (0x80000000) in
                // .NET - the C# spec leaves unchecked float-to-integral of NaN unspecified, so this
                // is "in practice on x86", not guaranteed - and it is narrowing that int down to
                // ushort (keeping only the low 16 bits, which are 0 for 0x80000000) that produces
                // the 0 result[i] ends up with, not the clamp above. Flagged here now that this
                // method is public and directly fixture-exercised, not because it is wrong.
                factor = Math.Pow(factor, 1 / gamma);

                factor = Math.Min(Math.Max(factor, 0), 1);

                result[i] = (ushort)Math.Round(factor * ushort.MaxValue);
            }

            return result;
        }

        public static bool IsGammaRampEqualToWindowsValues(VibranceInfo vibranceInfo, ApplicationSetting applicationSetting)
        {
            return vibranceInfo.userColorSettings.brightness == applicationSetting.Brightness && vibranceInfo.userColorSettings.contrast == applicationSetting.Contrast && vibranceInfo.userColorSettings.gamma == applicationSetting.Gamma;
        }

        public static bool IsGammaRampDefault(VibranceInfo vibranceInfo)
        {
            return 50 == vibranceInfo.userColorSettings.brightness && 50 == vibranceInfo.userColorSettings.contrast && 100 == vibranceInfo.userColorSettings.gamma;
        }

        // constant data
        public const int GAMMA_RAMP_SIZE = 256;

        // types
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        public struct GammaRamp
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = GAMMA_RAMP_SIZE)]
            public UInt16[] Red;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = GAMMA_RAMP_SIZE)]
            public UInt16[] Green;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = GAMMA_RAMP_SIZE)]
            public UInt16[] Blue;

            // constructor
            /// <summary>
            /// Define red, blue and green arrays.
            /// </summary>
            /// <param name="r">Red array.</param>
            /// <param name="g">Green array.</param>
            /// <param name="b">Blue array.</param>
            public GammaRamp(UInt16[] r = null, UInt16[] g = null, UInt16[] b = null)
            {
                Red = r == null ? new UInt16[GAMMA_RAMP_SIZE] : r;
                Green = g == null ? new UInt16[GAMMA_RAMP_SIZE] : g;
                Blue = b == null ? new UInt16[GAMMA_RAMP_SIZE] : b;
            }

            public override bool Equals(object obj)
            {
                if (!(obj is GammaRamp))
                {
                    return false;
                }
                GammaRamp other = (GammaRamp)obj;
                return AreRampChannelsEqual(this.Red, other.Red) && AreRampChannelsEqual(this.Blue, other.Blue) && AreRampChannelsEqual(this.Green, other.Green);
            }

            private static bool AreRampChannelsEqual(UInt16[] left, UInt16[] right)
            {
                if (left == null || right == null)
                {
                    return left == right;
                }
                return left.SequenceEqual(right);
            }

            public override int GetHashCode()
            {
                //has to hash the channel contents, Equals compares them by value
                int hashCode = -1058441243;
                hashCode = hashCode * -1521134295 + GetRampChannelHashCode(Red);
                hashCode = hashCode * -1521134295 + GetRampChannelHashCode(Green);
                hashCode = hashCode * -1521134295 + GetRampChannelHashCode(Blue);
                return hashCode;
            }

            private static int GetRampChannelHashCode(UInt16[] channel)
            {
                if (channel == null)
                {
                    return 0;
                }
                int hashCode = 17;
                for (int i = 0; i < channel.Length; i++)
                {
                    hashCode = hashCode * 31 + channel[i];
                }
                return hashCode;
            }
        };


        // Windows Native API
        private class NativeAPI
        {
            [DllImport("gdi32.dll")]
            public static extern IntPtr CreateDC(string lpszDriver, string lpszDevice, string lpszOutput, IntPtr lpInitData);

            [DllImport("gdi32.dll", EntryPoint = "DeleteDC")]
            public static extern bool DeleteDC([In] IntPtr hdc);

            [DllImport("user32.dll", EntryPoint = "GetDC")]
            public static extern IntPtr GetDC([In] IntPtr hWnd);


            // extern methods
            [DllImport("gdi32.dll")]
            public static extern bool SetDeviceGammaRamp(IntPtr hDC, ref GammaRamp lpRamp);
            [DllImport("gdi32.dll")]
            public static extern bool GetDeviceGammaRamp(IntPtr hDC, ref GammaRamp lpRamp);
        }
    }
}
