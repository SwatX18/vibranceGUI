using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Windows.Forms;
using vibrance.GUI.AMD;
using vibrance.GUI.AMD.vendor;
using vibrance.GUI.NVIDIA;

namespace vibrance.GUI.common
{
    /// <summary>
    /// Regression coverage for the toggle hotkey feature (upstream #143, per-game suppression):
    /// HotkeyBindingParser's parse/format round trip, HotkeyRegistration's release-then-register
    /// lifecycle (R3 in particular - pins the release-then-register ordering across a handle
    /// change, see HotkeyRegistration.Release's own comment for why the handle is cached rather
    /// than read fresh), ProfileToggleHelper.Decide's pure decision, the per-game suppression gate both
    /// vendor proxies' OnWinEventHook now open with, ToggleForegroundProfile's actual write, and
    /// the settings round trip. No GUI, no live GPU driver, and - unlike every check here that
    /// reflects into a real OnWinEventHook/ToggleForegroundProfile - this fixture NEVER calls the
    /// real RegisterHotKey and must never grow a hardware variant: IHotkeyRegistrar is driven
    /// exclusively through FakeHotkeyRegistrar below, exactly as VibranceRestoreFixture never
    /// touches a real GPU and ResolutionChangeFixture never touches a real display. Run by
    /// vibrance.GUI.exe --selftest-profiletoggle.
    /// </summary>
    public static class ProfileToggleFixture
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetDesktopWindow();

        public static List<string> Run()
        {
            Checklist checklist = new Checklist();
            checklist.Lines.Add("vibranceGUI toggle hotkey self test");
            checklist.Lines.Add(string.Empty);

            RunParseFormatChecks(checklist);
            RunRegistrationChecks(checklist);
            RunDecideChecks(checklist);
            RunToggleEffectChecks(checklist);
            RunSuppressionGateChecks(checklist);
            RunSuppressionCleanupChecks(checklist);
            RunSettingsChecks(checklist);

            checklist.Lines.Add(string.Empty);
            checklist.Lines.Add(string.Format("PASSED {0}/{1}", checklist.Passed, checklist.Total));
            return checklist.Lines;
        }

        // ------------------------------------------------------------------
        // HotkeyBindingParser - pure, no registrar, no proxy. Unchanged from the discarded global-
        // pause design; kept verbatim per the architect's instruction that this half survives.
        // ------------------------------------------------------------------

        private static void RunParseFormatChecks(Checklist checklist)
        {
            checklist.Lines.Add("HotkeyBindingParser.TryParse/Format (pure):");

            CheckParseBasic(checklist);
            CheckRoundTrip(checklist);
            CheckInvalidInputsRejected(checklist);
            CheckUnrecognisedTokenRejected(checklist);
            CheckNumericTokenRejected(checklist);
            CheckFormatNone(checklist);
            CheckCaseInsensitiveParseNormalisedFormat(checklist);
            CheckNoRepeatNeverFormatted(checklist);

            checklist.Lines.Add(string.Empty);
        }

        // H1. Mutation this guards: drop "| ModNoRepeat" from TryParse.
        private static void CheckParseBasic(Checklist checklist)
        {
            HotkeyBinding binding;
            bool ok = HotkeyBindingParser.TryParse("Ctrl+Alt+F9", out binding);
            const uint expectedModifiers = 0x0002 /* MOD_CONTROL */ | 0x0001 /* MOD_ALT */ | 0x4000 /* MOD_NOREPEAT */;
            const uint expectedVirtualKey = 0x78; // VK_F9

            checklist.Check(ok && binding.IsSet && binding.Modifiers == expectedModifiers && binding.VirtualKey == expectedVirtualKey,
                string.Format("H1: \"Ctrl+Alt+F9\" parses to Modifiers=0x{0:X} (MOD_CONTROL|MOD_ALT|MOD_NOREPEAT), VirtualKey=0x{1:X} (VK_F9), got ok={2} Modifiers=0x{3:X} VirtualKey=0x{4:X}",
                    expectedModifiers, expectedVirtualKey, ok, binding.Modifiers, binding.VirtualKey));
        }

        // H2. Mutation this guards: emit the modifiers in TryParse's own read order instead of a
        // fixed Ctrl/Alt/Shift/Win order in Format.
        private static readonly string[] CanonicalStrings =
        {
            "F9",
            "Ctrl+F9",
            "Alt+F5",
            "Shift+F12",
            "Win+D1",
            "Ctrl+Alt+Shift+Win+F9"
        };

        private static void CheckRoundTrip(Checklist checklist)
        {
            bool allRoundTrip = true;
            string firstMismatch = null;
            foreach (string canonical in CanonicalStrings)
            {
                HotkeyBinding binding;
                bool parsed = HotkeyBindingParser.TryParse(canonical, out binding);
                string formatted = HotkeyBindingParser.Format(binding);
                if (!parsed || formatted != canonical)
                {
                    allRoundTrip = false;
                    firstMismatch = string.Format("\"{0}\" -> parsed={1}, formatted=\"{2}\"", canonical, parsed, formatted);
                    break;
                }
            }

            checklist.Check(allRoundTrip, allRoundTrip
                ? "H2: Format(TryParse(s)) == s over all 6 canonical strings"
                : "H2: Format(TryParse(s)) == s over all 6 canonical strings - first mismatch: " + firstMismatch);
        }

        // H3. Mutation this guards: drop the "no key at all" guard, letting "Ctrl" parse as a
        // legal binding.
        private static void CheckInvalidInputsRejected(Checklist checklist)
        {
            HotkeyBinding binding;
            bool emptyOk = HotkeyBindingParser.TryParse("", out binding);
            bool nullOk = HotkeyBindingParser.TryParse(null, out binding);
            bool ctrlOnlyOk = HotkeyBindingParser.TryParse("Ctrl", out binding);
            bool trailingSeparatorOk = HotkeyBindingParser.TryParse("Ctrl+", out binding);

            checklist.Check(!emptyOk && !nullOk && !ctrlOnlyOk && !trailingSeparatorOk,
                string.Format("H3: \"\", null, \"Ctrl\", \"Ctrl+\" all fail to parse, got empty={0} null={1} ctrlOnly={2} trailingSeparator={3}",
                    emptyOk, nullOk, ctrlOnlyOk, trailingSeparatorOk));
        }

        // H4. Mutation this guards: silently drop an unrecognised token instead of failing.
        private static void CheckUnrecognisedTokenRejected(Checklist checklist)
        {
            HotkeyBinding binding;
            bool ok = HotkeyBindingParser.TryParse("Ctrl+Zzz+F9", out binding);

            checklist.Check(!ok, string.Format(
                "H4: \"Ctrl+Zzz+F9\" fails to parse - an unrecognised token is an error, never silently dropped, got {0}", ok));
        }

        // H5. Mutation this guards: drop the numeric-token guard, letting Enum.TryParse<Keys>
        // turn "1" into Keys.LButton.
        private static void CheckNumericTokenRejected(Checklist checklist)
        {
            HotkeyBinding numericBinding;
            bool numericOk = HotkeyBindingParser.TryParse("Ctrl+1", out numericBinding);

            HotkeyBinding namedDigitBinding;
            bool namedDigitOk = HotkeyBindingParser.TryParse("Ctrl+D1", out namedDigitBinding);

            checklist.Check(!numericOk,
                string.Format("H5a: \"Ctrl+1\" fails to parse - a purely numeric token is never a valid key name on its own, got {0}", numericOk));
            checklist.Check(namedDigitOk && namedDigitBinding.VirtualKey == 0x31,
                string.Format("H5b: \"Ctrl+D1\" parses with VirtualKey=0x31 (VK_1), got ok={0} VirtualKey=0x{1:X}", namedDigitOk, namedDigitBinding.VirtualKey));
        }

        // H6. Mutation this guards: return a non-empty placeholder for an unset binding instead
        // of "".
        private static void CheckFormatNone(Checklist checklist)
        {
            string formatted = HotkeyBindingParser.Format(HotkeyBinding.None);
            checklist.Check(formatted == string.Empty, string.Format("H6: Format(HotkeyBinding.None) == \"\", got \"{0}\"", formatted));
        }

        // H7. Mutation this guards: compare tokens with StringComparison.Ordinal (case-sensitive)
        // instead of OrdinalIgnoreCase.
        private static void CheckCaseInsensitiveParseNormalisedFormat(Checklist checklist)
        {
            HotkeyBinding binding;
            bool ok = HotkeyBindingParser.TryParse("ctrl+alt+f9", out binding);
            string formatted = HotkeyBindingParser.Format(binding);

            checklist.Check(ok && formatted == "Ctrl+Alt+F9",
                string.Format("H7: \"ctrl+alt+f9\" parses and formats back as the normalised \"Ctrl+Alt+F9\", got ok={0} formatted=\"{1}\"", ok, formatted));
        }

        // H8. Mutation this guards: add MOD_NOREPEAT to the list of modifier parts Format emits.
        private static void CheckNoRepeatNeverFormatted(Checklist checklist)
        {
            HotkeyBinding binding;
            HotkeyBindingParser.TryParse("Ctrl+F9", out binding);
            bool noRepeatSetInternally = (binding.Modifiers & HotkeyBindingParser.ModNoRepeat) != 0;
            string formatted = HotkeyBindingParser.Format(binding);

            checklist.Check(noRepeatSetInternally && formatted == "Ctrl+F9",
                string.Format("H8: MOD_NOREPEAT is set internally (Modifiers=0x{0:X}) but the formatted text is exactly \"Ctrl+F9\", got \"{1}\"",
                    binding.Modifiers, formatted));
        }

        // ------------------------------------------------------------------
        // HotkeyRegistration - against FakeHotkeyRegistrar, never RegisterHotKey. R1-R8 unchanged
        // from the discarded design; R9/R10 are new, covering the checkbox-gated registration
        // expression VibranceGUI.ApplyToggleHotkey now uses.
        // ------------------------------------------------------------------

        private static void RunRegistrationChecks(Checklist checklist)
        {
            checklist.Lines.Add("HotkeyRegistration.Apply/Release (via FakeHotkeyRegistrar, never the real RegisterHotKey):");

            CheckApplyNoneMakesNoRegisterCall(checklist);
            CheckApplyValidBindingRegistersOnce(checklist);
            CheckApplyOnNewHandleReleasesThePriorOne(checklist);
            CheckApplyPropagatesAlreadyOwned(checklist);
            CheckReleaseAfterFailedApplyMakesNoUnregisterCall(checklist);
            CheckReleaseOnFreshInstanceMakesNoCalls(checklist);
            CheckApplyNoneAfterSuccessReleasesWithoutRegistering(checklist);
            CheckModNoRepeatCompatibilityRetry(checklist);
            CheckCheckboxOffSuppressesRegistrationOfAValidBinding(checklist);
            CheckCheckboxOnWithNoBindingMakesNoCall(checklist);
            CheckShouldReleaseHotkeyOnFocusTransition(checklist);

            checklist.Lines.Add(string.Empty);
        }

        private static HotkeyBinding ParseOrThrow(string canonicalText)
        {
            HotkeyBinding binding;
            if (!HotkeyBindingParser.TryParse(canonicalText, out binding))
            {
                throw new InvalidOperationException("Fixture setup error: \"" + canonicalText + "\" failed to parse");
            }
            return binding;
        }

        // R1. Mutation this guards: call _registrar.Register before checking binding.IsSet.
        private static void CheckApplyNoneMakesNoRegisterCall(Checklist checklist)
        {
            FakeHotkeyRegistrar registrar = new FakeHotkeyRegistrar();
            HotkeyRegistration registration = new HotkeyRegistration(registrar);

            HotkeyRegistrationResult result = registration.Apply((IntPtr)1, HotkeyBinding.None);

            checklist.Check(result == HotkeyRegistrationResult.NotConfigured && registrar.RegisterCalls.Count == 0,
                string.Format("R1: Apply(h, None) returns NotConfigured with zero Register calls, got result={0} registerCalls={1}",
                    result, registrar.RegisterCalls.Count));
        }

        // R2 (PIN - not regression evidence, the ordinary successful path).
        private static void CheckApplyValidBindingRegistersOnce(Checklist checklist)
        {
            FakeHotkeyRegistrar registrar = new FakeHotkeyRegistrar();
            HotkeyRegistration registration = new HotkeyRegistration(registrar);
            HotkeyBinding binding = ParseOrThrow("Ctrl+Alt+F9");

            HotkeyRegistrationResult result = registration.Apply((IntPtr)1, binding);

            checklist.Check(result == HotkeyRegistrationResult.Registered && registrar.RegisterCalls.Count == 1 && registration.IsRegistered,
                string.Format("R2 (pin): Apply(h, validBinding) makes exactly one Register call and returns Registered, got result={0} registerCalls={1} isRegistered={2}",
                    result, registrar.RegisterCalls.Count, registration.IsRegistered));
        }

        // R3, the highest-value check in this file. Mutation this guards: have Release() (called
        // from inside Apply) read a fresh "hWnd" parameter instead of the handle cached at
        // registration time. Pins the release-then-register ordering across a handle change -
        // see HotkeyRegistration.Release's own comment for why caching it is the defensive
        // choice regardless of whether anything in this codebase currently recreates the handle.
        private static void CheckApplyOnNewHandleReleasesThePriorOne(Checklist checklist)
        {
            FakeHotkeyRegistrar registrar = new FakeHotkeyRegistrar();
            HotkeyRegistration registration = new HotkeyRegistration(registrar);
            IntPtr h1 = (IntPtr)111;
            IntPtr h2 = (IntPtr)222;
            HotkeyBinding a = ParseOrThrow("Ctrl+F9");
            HotkeyBinding b = ParseOrThrow("Alt+F10");

            registration.Apply(h1, a);
            registration.Apply(h2, b);

            bool sequenceMatches = registrar.RegisterCalls.Count == 2 &&
                registrar.RegisterCalls[0].HWnd == h1 &&
                registrar.RegisterCalls[1].HWnd == h2 &&
                registrar.UnregisterCalls.Count == 1 &&
                registrar.UnregisterCalls[0] == h1;

            checklist.Check(sequenceMatches, string.Format(
                "R3: Apply(h1,a) then Apply(h2,b) records Register(h1), Unregister(h1), Register(h2) in that order - the release-then-register ordering across a handle change - got {0} Register call(s) on [{1}], {2} Unregister call(s) on [{3}]",
                registrar.RegisterCalls.Count, DescribeHandles(registrar.RegisterCalls),
                registrar.UnregisterCalls.Count, DescribeHandles(registrar.UnregisterCalls)));
        }

        private static string DescribeHandles(List<FakeHotkeyRegistrar.RegisterCall> calls)
        {
            List<string> parts = new List<string>();
            foreach (FakeHotkeyRegistrar.RegisterCall call in calls)
            {
                parts.Add(call.HWnd.ToString());
            }
            return string.Join(",", parts.ToArray());
        }

        private static string DescribeHandles(List<IntPtr> handles)
        {
            List<string> parts = new List<string>();
            foreach (IntPtr handle in handles)
            {
                parts.Add(handle.ToString());
            }
            return string.Join(",", parts.ToArray());
        }

        // R4. Mutation this guards: treat AlreadyOwnedByAnotherApplication the same as Registered.
        private static void CheckApplyPropagatesAlreadyOwned(Checklist checklist)
        {
            FakeHotkeyRegistrar registrar = new FakeHotkeyRegistrar();
            registrar.QueueResult(HotkeyRegistrationResult.AlreadyOwnedByAnotherApplication);
            HotkeyRegistration registration = new HotkeyRegistration(registrar);
            HotkeyBinding binding = ParseOrThrow("Ctrl+F9");

            HotkeyRegistrationResult result = registration.Apply((IntPtr)1, binding);

            checklist.Check(result == HotkeyRegistrationResult.AlreadyOwnedByAnotherApplication && !registration.IsRegistered,
                string.Format("R4: AlreadyOwnedByAnotherApplication propagates and IsRegistered stays false, got result={0} isRegistered={1}",
                    result, registration.IsRegistered));
        }

        // R5. Mutation this guards: drop Release's "if (!_isRegistered) return;" guard.
        private static void CheckReleaseAfterFailedApplyMakesNoUnregisterCall(Checklist checklist)
        {
            FakeHotkeyRegistrar registrar = new FakeHotkeyRegistrar();
            registrar.QueueResult(HotkeyRegistrationResult.AlreadyOwnedByAnotherApplication);
            HotkeyRegistration registration = new HotkeyRegistration(registrar);
            registration.Apply((IntPtr)1, ParseOrThrow("Ctrl+F9"));

            registration.Release();

            checklist.Check(registrar.UnregisterCalls.Count == 0,
                string.Format("R5: Release() after a failed Apply makes zero Unregister calls, got {0}", registrar.UnregisterCalls.Count));
        }

        // R6. Same guard as R5, exercised on a completely untouched instance.
        private static void CheckReleaseOnFreshInstanceMakesNoCalls(Checklist checklist)
        {
            FakeHotkeyRegistrar registrar = new FakeHotkeyRegistrar();
            HotkeyRegistration registration = new HotkeyRegistration(registrar);

            registration.Release();

            checklist.Check(registrar.RegisterCalls.Count == 0 && registrar.UnregisterCalls.Count == 0,
                string.Format("R6: Release() on a fresh instance makes zero calls, got register={0} unregister={1}",
                    registrar.RegisterCalls.Count, registrar.UnregisterCalls.Count));
        }

        // R7. Mutation this guards: still call Register with a zeroed binding when applying None.
        private static void CheckApplyNoneAfterSuccessReleasesWithoutRegistering(Checklist checklist)
        {
            FakeHotkeyRegistrar registrar = new FakeHotkeyRegistrar();
            HotkeyRegistration registration = new HotkeyRegistration(registrar);
            IntPtr h = (IntPtr)1;
            registration.Apply(h, ParseOrThrow("Ctrl+F9"));
            int registerCallsBefore = registrar.RegisterCalls.Count;

            HotkeyRegistrationResult result = registration.Apply(h, HotkeyBinding.None);

            checklist.Check(result == HotkeyRegistrationResult.NotConfigured &&
                registrar.UnregisterCalls.Count == 1 &&
                registrar.RegisterCalls.Count == registerCallsBefore,
                string.Format("R7: Apply(h, None) after a success makes one Unregister call and zero new Register calls, got result={0} unregisterCalls={1} newRegisterCalls={2}",
                    result, registrar.UnregisterCalls.Count, registrar.RegisterCalls.Count - registerCallsBefore));
        }

        // R8. Mutation this guards: drop the MOD_NOREPEAT compatibility retry entirely.
        private static void CheckModNoRepeatCompatibilityRetry(Checklist checklist)
        {
            FakeHotkeyRegistrar registrar = new FakeHotkeyRegistrar();
            registrar.QueueResult(HotkeyRegistrationResult.Failed);
            HotkeyRegistration registration = new HotkeyRegistration(registrar);
            HotkeyBinding binding = ParseOrThrow("Ctrl+F9");

            HotkeyRegistrationResult result = registration.Apply((IntPtr)1, binding);

            bool secondCallDroppedNoRepeat = registrar.RegisterCalls.Count == 2 &&
                (registrar.RegisterCalls[0].Modifiers & HotkeyBindingParser.ModNoRepeat) != 0 &&
                (registrar.RegisterCalls[1].Modifiers & HotkeyBindingParser.ModNoRepeat) == 0;

            checklist.Check(result == HotkeyRegistrationResult.Registered && secondCallDroppedNoRepeat,
                string.Format("R8: a Failed first Register call (MOD_NOREPEAT set) retries exactly once more without MOD_NOREPEAT and succeeds, got result={0} registerCalls={1}",
                    result, registrar.RegisterCalls.Count));
        }

        // R9. Mutation this guards: gate registration on binding.IsSet alone, ignoring "enabled" -
        // the checkbox-off state VibranceGUI.ApplyToggleHotkey must respect. Drives the REAL
        // production gate, HotkeyRegistration.EffectiveBinding - not a fixture-local copy of it;
        // VibranceGUI.ApplyToggleHotkey calls this exact static method, so a mutation here is
        // visible to the actual code path, not just to a mirror of it.
        private static void CheckCheckboxOffSuppressesRegistrationOfAValidBinding(Checklist checklist)
        {
            FakeHotkeyRegistrar registrar = new FakeHotkeyRegistrar();
            HotkeyRegistration registration = new HotkeyRegistration(registrar);
            HotkeyBinding validBinding = ParseOrThrow("Ctrl+Alt+F9");

            HotkeyBinding effective = HotkeyRegistration.EffectiveBinding(false, validBinding);
            HotkeyRegistrationResult result = registration.Apply((IntPtr)1, effective);

            checklist.Check(result == HotkeyRegistrationResult.NotConfigured && registrar.RegisterCalls.Count == 0,
                string.Format("R9: checkbox off + a valid saved combination makes zero Register calls, got result={0} registerCalls={1}",
                    result, registrar.RegisterCalls.Count));
        }

        // R10. Mutation this guards: throw (e.g. dereferencing a field on HotkeyBinding.None
        // incorrectly) instead of cleanly returning NotConfigured when enabled but unbound. Same
        // real-gate note as R9 above.
        private static void CheckCheckboxOnWithNoBindingMakesNoCall(Checklist checklist)
        {
            FakeHotkeyRegistrar registrar = new FakeHotkeyRegistrar();
            HotkeyRegistration registration = new HotkeyRegistration(registrar);

            bool threw = false;
            HotkeyRegistrationResult result = HotkeyRegistrationResult.Failed;
            try
            {
                HotkeyBinding effective = HotkeyRegistration.EffectiveBinding(true, HotkeyBinding.None);
                result = registration.Apply((IntPtr)1, effective);
            }
            catch (Exception)
            {
                threw = true;
            }

            checklist.Check(!threw && result == HotkeyRegistrationResult.NotConfigured && registrar.RegisterCalls.Count == 0,
                string.Format("R10: checkbox on + no combination makes zero calls and never throws, got threw={0} result={1} registerCalls={2}",
                    threw, result, registrar.RegisterCalls.Count));
        }

        // B1. Drives VibranceGUI.ShouldReleaseHotkeyOnFocusTransition directly (same assembly,
        // internal - no reflection needed) rather than a real Form: VibranceGUI's own constructor
        // calls getProxy(...), touching a real vendor proxy, and FormatterServices.
        // GetUninitializedObject is not safe for a Form-derived type (WinForms/Control internal
        // state that only the real constructor sets up). This is the condition
        // OnDeactivate/OnActivated both call before touching the hotkey registration - a
        // real Form correctly wiring WM_ACTIVATE to those two overrides is a WinForms framework
        // fact, not something this harness can verify headlessly, but the DECISION they both make
        // is real production code and is what this pins.
        //
        // Mutation this guards: invert the comparison (or hardcode true/false) so OnDeactivate/
        // OnActivated stop distinguishing "the capture box has focus" from "anything else does".
        private static void CheckShouldReleaseHotkeyOnFocusTransition(Checklist checklist)
        {
            TextBox captureBox = new TextBox();
            TextBox otherControl = new TextBox();

            bool trueForTheCaptureBoxItself = VibranceGUI.ShouldReleaseHotkeyOnFocusTransition(captureBox, captureBox);
            bool falseForADifferentControl = !VibranceGUI.ShouldReleaseHotkeyOnFocusTransition(otherControl, captureBox);
            bool falseForNoActiveControlAtAll = !VibranceGUI.ShouldReleaseHotkeyOnFocusTransition(null, captureBox);

            checklist.Check(trueForTheCaptureBoxItself && falseForADifferentControl && falseForNoActiveControlAtAll,
                string.Format("B1: ShouldReleaseHotkeyOnFocusTransition is true only when the capture box itself is the active control - got captureBox={0}, otherControl={1}, null={2}",
                    trueForTheCaptureBoxItself, !falseForADifferentControl, !falseForNoActiveControlAtAll));
        }

        // ------------------------------------------------------------------
        // ProfileToggleHelper.Decide - pure, no device, no Screen, no OS call.
        // ------------------------------------------------------------------

        private static void RunDecideChecks(Checklist checklist)
        {
            checklist.Lines.Add("ProfileToggleHelper.Decide (pure):");

            CheckDecideMatchesByInstallDirectory(checklist);
            CheckDecideNoMatch(checklist);
            CheckDecideEmptyAndNullLists(checklist);
            CheckDecideEngineNotReady(checklist);
            CheckDecideDirectionBothWays(checklist);
            CheckDecideMutatesNothing(checklist);
            CheckDecideSuppressionIsCaseInsensitive(checklist);
            CheckDecideNameBeatsLongerDirectoryMatch(checklist);

            checklist.Lines.Add(string.Empty);
        }

        // D1. Mutation this guards: match on setting.Name alone, ignoring processImagePath - PR
        // #153's bug. A profile the game finder only ever confirmed by install directory (a
        // guessed executable, or a launcher/anti-cheat shim in the foreground instead of the game
        // itself) must be just as reachable by the hotkey as by the automatic apply path.
        private static void CheckDecideMatchesByInstallDirectory(Checklist checklist)
        {
            ApplicationSetting setting = new ApplicationSetting();
            setting.Name = "RealGameExeName";
            setting.InstallDirectory = "C:\\Games\\SomeGame";
            setting.IngameLevel = 40;
            List<ApplicationSetting> settings = new List<ApplicationSetting> { setting };

            ProfileToggleDecision decision = ProfileToggleHelper.Decide(
                settings, "launcher", "C:\\Games\\SomeGame\\bin\\launcher.exe", true);

            checklist.Check(decision.Action == ProfileToggleAction.RestoreWindowsLevel && decision.Setting == setting,
                string.Format("D1: a process whose name does not match but whose image path sits under the setting's InstallDirectory is still found, got Action={0}",
                    decision.Action));
        }

        // D2. Mutation this guards: return something other than None (e.g. throw, or default to
        // the first setting) when nothing matches at all.
        private static void CheckDecideNoMatch(Checklist checklist)
        {
            ApplicationSetting setting = new ApplicationSetting();
            setting.Name = "SomeGame";
            List<ApplicationSetting> settings = new List<ApplicationSetting> { setting };

            ProfileToggleDecision decision = ProfileToggleHelper.Decide(settings, "UnrelatedProcess", null, true);

            checklist.Check(decision.Action == ProfileToggleAction.None,
                string.Format("D2: no match at all -> None, got {0}", decision.Action));
        }

        // D3. Mutation this guards: dereference the list without a null check.
        private static void CheckDecideEmptyAndNullLists(Checklist checklist)
        {
            bool threw = false;
            ProfileToggleAction emptyAction = ProfileToggleAction.None;
            ProfileToggleAction nullAction = ProfileToggleAction.None;
            try
            {
                emptyAction = ProfileToggleHelper.Decide(new List<ApplicationSetting>(), "Anything", null, true).Action;
                nullAction = ProfileToggleHelper.Decide(null, "Anything", null, true).Action;
            }
            catch (Exception)
            {
                threw = true;
            }

            checklist.Check(!threw && emptyAction == ProfileToggleAction.None && nullAction == ProfileToggleAction.None,
                string.Format("D3: an empty list and a null list both yield None with no throw, got threw={0} emptyAction={1} nullAction={2}",
                    threw, emptyAction, nullAction));
        }

        // D4. Mutation this guards: skip the isWindowsLevelKnown check and fall through to a
        // real Action even when the Windows level is still the struct default 0 - reopens
        // issue #60/#36 through the toggle's own door.
        private static void CheckDecideEngineNotReady(Checklist checklist)
        {
            ApplicationSetting setting = new ApplicationSetting();
            setting.Name = "SomeGame";
            List<ApplicationSetting> settings = new List<ApplicationSetting> { setting };

            ProfileToggleDecision decision = ProfileToggleHelper.Decide(settings, "SomeGame", null, false);

            checklist.Check(decision.Action == ProfileToggleAction.EngineNotReady,
                string.Format("D4: a clean Name match with isWindowsLevelKnown false -> EngineNotReady, not a write action, got {0}", decision.Action));
        }

        // D5. Mutation this guards: invert (or hard-code) the direction rule.
        private static void CheckDecideDirectionBothWays(Checklist checklist)
        {
            ProfileToggleHelper.ResetForTests();
            ApplicationSetting setting = new ApplicationSetting();
            setting.Name = "SomeGameD5";
            List<ApplicationSetting> settings = new List<ApplicationSetting> { setting };

            ProfileToggleDecision notSuppressed = ProfileToggleHelper.Decide(settings, "SomeGameD5", null, true);
            ProfileToggleHelper.SetSuppressed("SomeGameD5", true);
            ProfileToggleDecision suppressed = ProfileToggleHelper.Decide(settings, "SomeGameD5", null, true);

            checklist.Check(notSuppressed.Action == ProfileToggleAction.RestoreWindowsLevel && suppressed.Action == ProfileToggleAction.ApplyGameLevel,
                string.Format("D5: not suppressed -> RestoreWindowsLevel, suppressed -> ApplyGameLevel, got notSuppressed={0} suppressed={1}",
                    notSuppressed.Action, suppressed.Action));

            ProfileToggleHelper.ResetForTests();
        }

        // D6. Mutation this guards: have Decide itself call SetSuppressed (or otherwise mutate
        // state) instead of leaving the flip to the caller, after a confirmed write.
        private static void CheckDecideMutatesNothing(Checklist checklist)
        {
            ProfileToggleHelper.ResetForTests();
            ApplicationSetting setting = new ApplicationSetting();
            setting.Name = "SomeGameD6";
            List<ApplicationSetting> settings = new List<ApplicationSetting> { setting };

            int countBefore = ProfileToggleHelper.SuppressedCount;
            ProfileToggleAction first = ProfileToggleHelper.Decide(settings, "SomeGameD6", null, true).Action;
            ProfileToggleAction second = ProfileToggleHelper.Decide(settings, "SomeGameD6", null, true).Action;
            ProfileToggleAction third = ProfileToggleHelper.Decide(settings, "SomeGameD6", null, true).Action;
            int countAfter = ProfileToggleHelper.SuppressedCount;

            checklist.Check(countBefore == countAfter && first == second && second == third,
                string.Format("D6: three identical Decide calls leave SuppressedCount unchanged ({0} -> {1}) and return the same Action every time ({2},{3},{4})",
                    countBefore, countAfter, first, second, third));
        }

        // D7. Mutation this guards: compare suppressed names with StringComparison.Ordinal.
        private static void CheckDecideSuppressionIsCaseInsensitive(Checklist checklist)
        {
            ProfileToggleHelper.ResetForTests();
            ApplicationSetting setting = new ApplicationSetting();
            setting.Name = "SomeGameD7";
            List<ApplicationSetting> settings = new List<ApplicationSetting> { setting };

            ProfileToggleHelper.SetSuppressed("somegamed7", true);
            ProfileToggleDecision decision = ProfileToggleHelper.Decide(settings, "SomeGameD7", null, true);

            checklist.Check(decision.Action == ProfileToggleAction.ApplyGameLevel,
                string.Format("D7: suppressing \"somegamed7\" (different case) still suppresses the setting named \"SomeGameD7\", got {0}", decision.Action));

            ProfileToggleHelper.ResetForTests();
        }

        // D8 (PIN). A Name match beats a longer directory match belonging to a DIFFERENT setting -
        // proves Decide's pass-through of ApplicationSettingMatcher.FindMatch's own two-pass rule,
        // not a Decide-specific behaviour.
        private static void CheckDecideNameBeatsLongerDirectoryMatch(Checklist checklist)
        {
            ApplicationSetting nameMatch = new ApplicationSetting();
            nameMatch.Name = "TargetProcess";
            nameMatch.InstallDirectory = null;

            ApplicationSetting directoryMatch = new ApplicationSetting();
            directoryMatch.Name = "SomeOtherName";
            directoryMatch.InstallDirectory = "C:\\Games\\SomeGame\\A\\Much\\Longer\\Nested\\Directory";

            List<ApplicationSetting> settings = new List<ApplicationSetting> { directoryMatch, nameMatch };

            ProfileToggleDecision decision = ProfileToggleHelper.Decide(
                settings, "TargetProcess", "C:\\Games\\SomeGame\\A\\Much\\Longer\\Nested\\Directory\\TargetProcess.exe", true);

            checklist.Check(decision.Setting == nameMatch,
                "D8 (pin): an exact Name match wins over a longer InstallDirectory match belonging to a different setting");
        }

        // ------------------------------------------------------------------
        // ToggleForegroundProfile's actual write - both vendors, via ResetForTests/fake
        // adapters/GetUninitializedObject. NVIDIA's ToggleForegroundProfile is an instance method
        // purely because IVibranceProxy requires one; it (like OnWinEventHook) touches only this
        // class's own static state, so GetUninitializedObject hands back a target to invoke it
        // against without ever running the real constructor (which would call vibranceDLL.dll's
        // initializeLibrary()). AMD's own state is per-instance, so its checks construct a real
        // proxy around a fake IAmdAdapter instead (IAmdAdapter.IsAvailable() returning false keeps
        // the constructor from installing a real, process-lifetime SetWinEventHook).
        // ------------------------------------------------------------------

        private static void RunToggleEffectChecks(Checklist checklist)
        {
            checklist.Lines.Add("ToggleForegroundProfile's write (both vendors, via fakes):");

            CheckNvidiaToggleWritesOnlyTheGamesOwnDisplay(checklist);
            CheckAmdToggleNeverWidensToAllDisplays(checklist);
            CheckNvidiaToggleNoMatchMakesNoCallsOrStateChange(checklist);
            CheckAmdToggleNoMatchMakesNoCallsOrStateChange(checklist);
            CheckNvidiaToggleOnWriteFailureLeavesStateUnchanged(checklist);
            CheckAmdToggleOnWriteFailureLeavesStateUnchanged(checklist);
            CheckNvidiaToggleOffWriteFailureLeavesStateUnchanged(checklist);
            CheckAmdToggleOffWriteFailureLeavesStateUnchanged(checklist);
            CheckNvidiaToggleOffThenOnRoundTrip(checklist);
            CheckNvidiaToggleOffOfNeverAppliedGameStillSucceeds(checklist);
            CheckAmdToggleOffRespectsWideMode(checklist);
            CheckAmdToggleOnRespectsWideMode(checklist);

            checklist.Lines.Add(string.Empty);
        }

        private static object NewNvidiaInstance()
        {
            return FormatterServices.GetUninitializedObject(typeof(NvidiaDynamicVibranceProxy));
        }

        private static ProfileToggleResult InvokeNvidiaToggle(IntPtr hWnd, string processName, string processImagePath)
        {
            MethodInfo m = typeof(NvidiaDynamicVibranceProxy).GetMethod("ToggleForegroundProfile");
            return (ProfileToggleResult)m.Invoke(NewNvidiaInstance(), new object[] { hWnd, processName, processImagePath });
        }

        // T1, the check that matters most here. The work-list is seeded with BOTH dGame and
        // dOther as GENUINELY held (VibranceRestoreHelper.RecordGameLevelApplied for both) -
        // seeding dOther as a real work-list entry, not an unused decoy, is what makes the
        // widening mutation below actually produce a second write; a decoy never on the work-list
        // would leave that wrong implementation passing by accident.
        //
        // Mutation this guards: route the toggle's restore write through
        // RestoreWindowsVibranceLevel instead of RestoreOneDisplay - the former walks the WHOLE
        // work-list plus the primary, so it would write dOther too.
        private static void CheckNvidiaToggleWritesOnlyTheGamesOwnDisplay(Checklist checklist)
        {
            FakeNvidiaVibranceDevice device = new FakeNvidiaVibranceDevice();
            ApplicationSetting matchingSetting = new ApplicationSetting();
            matchingSetting.Name = "TestGameT1";
            matchingSetting.IngameLevel = 50;
            List<ApplicationSetting> settings = new List<ApplicationSetting> { matchingSetting };

            VibranceInfo vibranceInfo = new VibranceInfo();
            vibranceInfo.isWindowsLevelKnown = true;
            vibranceInfo.userVibranceSettingDefault = 30;
            NvidiaDynamicVibranceProxy.ResetForTests(device, vibranceInfo, settings);

            IntPtr desktop = GetDesktopWindow();
            string dGame = Screen.FromHandle(desktop).DeviceName;
            const string dOther = "\\\\.\\DISPLAY_TESTONLY_T1_OTHER";
            VibranceRestoreHelper.RecordGameLevelApplied(dGame);
            VibranceRestoreHelper.RecordGameLevelApplied(dOther);

            ProfileToggleResult result = InvokeNvidiaToggle(desktop, "TestGameT1", null);

            bool witness1 = device.SetLevelCalls.Count == 1 && device.SetLevelCalls[0] == device.HandleFor(dGame);
            bool witness2 = device.ResolvedDeviceNames.Count == 1 && device.ResolvedDeviceNames[0] == dGame;
            bool witness3 = VibranceRestoreHelper.HoldingCount == 1;

            checklist.Check(result == ProfileToggleResult.ToggledOff && witness1 && witness2 && witness3,
                string.Format("T1: toggling off writes ONLY the game's own display (1 SetLevel call on dGame, 1 resolved device, dOther left on the work-list -> HoldingCount 1), got result={0} SetLevelCalls={1} ResolvedDeviceNames={2} HoldingCount={3}",
                    result, device.SetLevelCalls.Count, device.ResolvedDeviceNames.Count, VibranceRestoreHelper.HoldingCount));
        }

        // T2, AMD's counterpart. Mutation this guards: route the toggle's restore write through
        // SetSaturationOnAllDisplays (the "likeliest wrong implementation") instead of
        // SetSaturationOnDisplay(vibranceLevel, deviceName).
        private static void CheckAmdToggleNeverWidensToAllDisplays(Checklist checklist)
        {
            FakeAmdAdapter adapter = new FakeAmdAdapter();
            ApplicationSetting matchingSetting = new ApplicationSetting();
            matchingSetting.Name = "TestGameT2";
            matchingSetting.IngameLevel = 200;
            List<ApplicationSetting> settings = new List<ApplicationSetting> { matchingSetting };
            AmdDynamicVibranceProxy proxy = BuildAmdProxy(adapter, settings);
            proxy.SetVibranceWindowsLevel(90);
            // Narrow mode - B2 (off)/(on) below cover the wide-mode branch this proxy also has to
            // respect; this check is specifically about the narrow, single-display overload.
            proxy.SetAffectPrimaryMonitorOnly(true);

            IntPtr desktop = GetDesktopWindow();
            string dGame = Screen.FromHandle(desktop).DeviceName;

            ProfileToggleResult result = proxy.ToggleForegroundProfile(desktop, "TestGameT2", null);

            checklist.Check(result == ProfileToggleResult.ToggledOff &&
                adapter.SetSaturationOnAllDisplaysCallCount == 0 &&
                adapter.SetSaturationOnDisplayNames.Count == 1 && adapter.SetSaturationOnDisplayNames[0] == dGame,
                string.Format("T2: toggling off an AMD profile never calls SetSaturationOnAllDisplays (got {0} call(s)) and writes SetSaturationOnDisplay exactly once, to the game's own display, got result={1} perDisplayCalls={2}",
                    adapter.SetSaturationOnAllDisplaysCallCount, result, adapter.SetSaturationOnDisplayNames.Count));
        }

        // T3n. Mutation this guards: fall through to some write even when Decide returned None.
        private static void CheckNvidiaToggleNoMatchMakesNoCallsOrStateChange(Checklist checklist)
        {
            FakeNvidiaVibranceDevice device = new FakeNvidiaVibranceDevice();
            VibranceInfo vibranceInfo = new VibranceInfo();
            vibranceInfo.isWindowsLevelKnown = true;
            vibranceInfo.userVibranceSettingDefault = 30;
            NvidiaDynamicVibranceProxy.ResetForTests(device, vibranceInfo, new List<ApplicationSetting>());

            ProfileToggleResult result = InvokeNvidiaToggle(GetDesktopWindow(), "SomeOtherProcessT3", null);

            checklist.Check(result == ProfileToggleResult.NoConfiguredGameInForeground && device.TotalCallCount == 0 && ProfileToggleHelper.SuppressedCount == 0,
                string.Format("T3n: no configured game in the foreground makes zero device calls and zero suppression-state change, got result={0} calls={1} suppressedCount={2}",
                    result, device.TotalCallCount, ProfileToggleHelper.SuppressedCount));
        }

        // T3a, AMD's counterpart.
        private static void CheckAmdToggleNoMatchMakesNoCallsOrStateChange(Checklist checklist)
        {
            ProfileToggleHelper.ResetForTests();
            FakeAmdAdapter adapter = new FakeAmdAdapter();
            AmdDynamicVibranceProxy proxy = BuildAmdProxy(adapter, new List<ApplicationSetting>());
            proxy.SetVibranceWindowsLevel(90);

            ProfileToggleResult result = proxy.ToggleForegroundProfile(GetDesktopWindow(), "SomeOtherProcessT3", null);

            checklist.Check(result == ProfileToggleResult.NoConfiguredGameInForeground &&
                adapter.SetSaturationOnAllDisplaysCallCount == 0 && adapter.SetSaturationOnDisplayNames.Count == 0 &&
                ProfileToggleHelper.SuppressedCount == 0,
                string.Format("T3a: no configured game in the foreground makes zero adapter calls and zero suppression-state change, got result={0} allCalls={1} perDisplayCalls={2} suppressedCount={3}",
                    result, adapter.SetSaturationOnAllDisplaysCallCount, adapter.SetSaturationOnDisplayNames.Count, ProfileToggleHelper.SuppressedCount));
        }

        // T4n (ON-path failure). The game is already suppressed; toggling it back on fails to
        // write - must report WriteFailed and leave it suppressed. Mutation this guards: flip
        // suppression before checking the write's own return value.
        private static void CheckNvidiaToggleOnWriteFailureLeavesStateUnchanged(Checklist checklist)
        {
            FakeNvidiaVibranceDevice device = new FakeNvidiaVibranceDevice();
            ApplicationSetting matchingSetting = new ApplicationSetting();
            matchingSetting.Name = "TestGameT4";
            matchingSetting.IngameLevel = 50;
            List<ApplicationSetting> settings = new List<ApplicationSetting> { matchingSetting };
            VibranceInfo vibranceInfo = new VibranceInfo();
            vibranceInfo.isWindowsLevelKnown = true;
            vibranceInfo.userVibranceSettingDefault = 30;
            NvidiaDynamicVibranceProxy.ResetForTests(device, vibranceInfo, settings);
            ProfileToggleHelper.SetSuppressed("TestGameT4", true);

            IntPtr desktop = GetDesktopWindow();
            string dGame = Screen.FromHandle(desktop).DeviceName;
            device.FailNextSetLevel(dGame);

            ProfileToggleResult result = InvokeNvidiaToggle(desktop, "TestGameT4", null);

            checklist.Check(result == ProfileToggleResult.WriteFailed && ProfileToggleHelper.IsSuppressed("TestGameT4"),
                string.Format("T4n: a failed game-level write on the ON path returns WriteFailed and leaves the profile suppressed, got result={0} isSuppressed={1}",
                    result, ProfileToggleHelper.IsSuppressed("TestGameT4")));

            ProfileToggleHelper.ResetForTests();
        }

        // T4a, AMD's counterpart.
        private static void CheckAmdToggleOnWriteFailureLeavesStateUnchanged(Checklist checklist)
        {
            ProfileToggleHelper.ResetForTests();
            FakeAmdAdapter adapter = new FakeAmdAdapter();
            ApplicationSetting matchingSetting = new ApplicationSetting();
            matchingSetting.Name = "TestGameT4a";
            matchingSetting.IngameLevel = 210;
            List<ApplicationSetting> settings = new List<ApplicationSetting> { matchingSetting };
            AmdDynamicVibranceProxy proxy = BuildAmdProxy(adapter, settings);
            proxy.SetVibranceWindowsLevel(90);
            // Narrow mode - B2 (on) already covers the wide-mode write; this check is about the
            // write-failure/state-preservation contract, not which overload gets called, so it is
            // pinned to the mode whose failure injection targets a specific display name.
            proxy.SetAffectPrimaryMonitorOnly(true);
            ProfileToggleHelper.SetSuppressed("TestGameT4a", true);

            IntPtr desktop = GetDesktopWindow();
            string dGame = Screen.FromHandle(desktop).DeviceName;
            adapter.FailNextSetSaturationOnDisplay(dGame);

            ProfileToggleResult result = proxy.ToggleForegroundProfile(desktop, "TestGameT4a", null);

            checklist.Check(result == ProfileToggleResult.WriteFailed && ProfileToggleHelper.IsSuppressed("TestGameT4a"),
                string.Format("T4a: a failed game-level write on the ON path returns WriteFailed and leaves the profile suppressed, got result={0} isSuppressed={1}",
                    result, ProfileToggleHelper.IsSuppressed("TestGameT4a")));

            ProfileToggleHelper.ResetForTests();
        }

        // T5n (OFF-path failure). The game is running normally; toggling it off fails to write -
        // must report WriteFailed and leave it un-suppressed.
        private static void CheckNvidiaToggleOffWriteFailureLeavesStateUnchanged(Checklist checklist)
        {
            FakeNvidiaVibranceDevice device = new FakeNvidiaVibranceDevice();
            ApplicationSetting matchingSetting = new ApplicationSetting();
            matchingSetting.Name = "TestGameT5";
            matchingSetting.IngameLevel = 50;
            List<ApplicationSetting> settings = new List<ApplicationSetting> { matchingSetting };
            VibranceInfo vibranceInfo = new VibranceInfo();
            vibranceInfo.isWindowsLevelKnown = true;
            vibranceInfo.userVibranceSettingDefault = 30;
            NvidiaDynamicVibranceProxy.ResetForTests(device, vibranceInfo, settings);

            IntPtr desktop = GetDesktopWindow();
            string dGame = Screen.FromHandle(desktop).DeviceName;
            device.FailNextSetLevel(dGame);

            ProfileToggleResult result = InvokeNvidiaToggle(desktop, "TestGameT5", null);

            checklist.Check(result == ProfileToggleResult.WriteFailed && !ProfileToggleHelper.IsSuppressed("TestGameT5"),
                string.Format("T5n: a failed Windows-level write on the OFF path returns WriteFailed and leaves the profile NOT suppressed, got result={0} isSuppressed={1}",
                    result, ProfileToggleHelper.IsSuppressed("TestGameT5")));
        }

        // T5a, AMD's counterpart.
        private static void CheckAmdToggleOffWriteFailureLeavesStateUnchanged(Checklist checklist)
        {
            ProfileToggleHelper.ResetForTests();
            FakeAmdAdapter adapter = new FakeAmdAdapter();
            ApplicationSetting matchingSetting = new ApplicationSetting();
            matchingSetting.Name = "TestGameT5a";
            matchingSetting.IngameLevel = 210;
            List<ApplicationSetting> settings = new List<ApplicationSetting> { matchingSetting };
            AmdDynamicVibranceProxy proxy = BuildAmdProxy(adapter, settings);
            proxy.SetVibranceWindowsLevel(90);
            // Narrow mode - see the matching comment in CheckAmdToggleOnWriteFailureLeavesStateUnchanged.
            proxy.SetAffectPrimaryMonitorOnly(true);

            IntPtr desktop = GetDesktopWindow();
            string dGame = Screen.FromHandle(desktop).DeviceName;
            adapter.FailNextSetSaturationOnDisplay(dGame);

            ProfileToggleResult result = proxy.ToggleForegroundProfile(desktop, "TestGameT5a", null);

            checklist.Check(result == ProfileToggleResult.WriteFailed && !ProfileToggleHelper.IsSuppressed("TestGameT5a"),
                string.Format("T5a: a failed Windows-level write on the OFF path returns WriteFailed and leaves the profile NOT suppressed, got result={0} isSuppressed={1}",
                    result, ProfileToggleHelper.IsSuppressed("TestGameT5a")));

            ProfileToggleHelper.ResetForTests();
        }

        // T6. A full off-then-on round trip: two writes, two different levels, ends un-suppressed.
        private static void CheckNvidiaToggleOffThenOnRoundTrip(Checklist checklist)
        {
            FakeNvidiaVibranceDevice device = new FakeNvidiaVibranceDevice();
            ApplicationSetting matchingSetting = new ApplicationSetting();
            matchingSetting.Name = "TestGameT6";
            matchingSetting.IngameLevel = 55;
            List<ApplicationSetting> settings = new List<ApplicationSetting> { matchingSetting };
            VibranceInfo vibranceInfo = new VibranceInfo();
            vibranceInfo.isWindowsLevelKnown = true;
            vibranceInfo.userVibranceSettingDefault = 20;
            NvidiaDynamicVibranceProxy.ResetForTests(device, vibranceInfo, settings);

            IntPtr desktop = GetDesktopWindow();

            ProfileToggleResult offResult = InvokeNvidiaToggle(desktop, "TestGameT6", null);
            ProfileToggleResult onResult = InvokeNvidiaToggle(desktop, "TestGameT6", null);

            checklist.Check(offResult == ProfileToggleResult.ToggledOff && onResult == ProfileToggleResult.ToggledOn && !ProfileToggleHelper.IsSuppressed("TestGameT6"),
                string.Format("T6: off-then-on returns ToggledOff then ToggledOn and ends un-suppressed, got off={0} on={1} isSuppressed={2}",
                    offResult, onResult, ProfileToggleHelper.IsSuppressed("TestGameT6")));

            bool sawBothLevels = device.SetLevelCalls.Count == 2;
            checklist.Check(sawBothLevels,
                string.Format("T6: exactly two SetLevel calls landed across the round trip (one per direction), got {0}", device.SetLevelCalls.Count));
        }

        // T8. A game that was suppressed WITHOUT ever having its game level actually applied
        // (SetLevel never called for it before) still toggles off successfully via the read-back
        // alone, when the display already happens to be at the Windows level.
        private static void CheckNvidiaToggleOffOfNeverAppliedGameStillSucceeds(Checklist checklist)
        {
            FakeNvidiaVibranceDevice device = new FakeNvidiaVibranceDevice();
            ApplicationSetting matchingSetting = new ApplicationSetting();
            matchingSetting.Name = "TestGameT8";
            matchingSetting.IngameLevel = 50;
            List<ApplicationSetting> settings = new List<ApplicationSetting> { matchingSetting };
            VibranceInfo vibranceInfo = new VibranceInfo();
            vibranceInfo.isWindowsLevelKnown = true;
            vibranceInfo.userVibranceSettingDefault = 30;
            NvidiaDynamicVibranceProxy.ResetForTests(device, vibranceInfo, settings);

            IntPtr desktop = GetDesktopWindow();
            string dGame = Screen.FromHandle(desktop).DeviceName;
            device.SeedLevel(dGame, 30); // already at the Windows level - as if never actually applied to.

            ProfileToggleResult result = InvokeNvidiaToggle(desktop, "TestGameT8", null);

            checklist.Check(result == ProfileToggleResult.ToggledOff && device.SetLevelCalls.Count == 0 && ProfileToggleHelper.IsSuppressed("TestGameT8"),
                string.Format("T8: toggling off a display already at the Windows level makes zero SetLevel calls (confirmed by read-back alone) but still reports ToggledOff and suppresses, got result={0} setLevelCalls={1} isSuppressed={2}",
                    result, device.SetLevelCalls.Count, ProfileToggleHelper.IsSuppressed("TestGameT8")));
        }

        // B2 (off). AMD's apply is NOT single-display with affectPrimaryMonitorOnly false (the
        // DEFAULT) - unlike NVIDIA, it writes every attached screen (OnWinEventHook's own apply
        // branch above). Mutation this guards: ignore the flag in the OFF direction and always
        // write/clear only the one named display. Witnessed two ways: the adapter call itself
        // must be the WIDE overload (SetSaturationOnDisplay(level, null), never
        // SetSaturationOnAllDisplays - so the bool return still gets checked), and a work-list
        // entry for a display OTHER than the resolved one must still be cleared - the narrow
        // ClearGameLevelRecord(deviceName) would leave it behind, only the wide
        // ClearAllGameLevelRecords() drains it.
        private static void CheckAmdToggleOffRespectsWideMode(Checklist checklist)
        {
            ProfileToggleHelper.ResetForTests();
            VibranceRestoreHelper.ResetForTests();
            FakeAmdAdapter adapter = new FakeAmdAdapter();
            ApplicationSetting matchingSetting = new ApplicationSetting();
            matchingSetting.Name = "TestGameB2Off";
            matchingSetting.IngameLevel = 220;
            List<ApplicationSetting> settings = new List<ApplicationSetting> { matchingSetting };
            AmdDynamicVibranceProxy proxy = BuildAmdProxy(adapter, settings);
            // affectPrimaryMonitorOnly left at the struct default (false) - AMD's actual default,
            // and the wide mode this check means to exercise.
            proxy.SetVibranceWindowsLevel(90);

            const string otherDevice = "\\\\.\\DISPLAY_TESTONLY_B2OFF_OTHER";
            VibranceRestoreHelper.RecordGameLevelApplied(otherDevice);

            IntPtr desktop = GetDesktopWindow();
            ProfileToggleResult result = proxy.ToggleForegroundProfile(desktop, "TestGameB2Off", null);

            bool usedWideOverload = adapter.SetSaturationOnAllDisplaysCallCount == 0 &&
                adapter.SetSaturationOnDisplayNames.Count == 1 && adapter.SetSaturationOnDisplayNames[0] == null &&
                adapter.SetSaturationOnDisplayLevels[0] == 90;

            checklist.Check(result == ProfileToggleResult.ToggledOff && usedWideOverload && VibranceRestoreHelper.HoldingCount == 0,
                string.Format("B2 (off): with affectPrimaryMonitorOnly false, toggling off calls SetSaturationOnDisplay(90, null) - never SetSaturationOnAllDisplays - and drains the WHOLE work-list (a display never named in the call), got result={0} allDisplaysCalls={1} perDisplayCalls={2} lastName={3} HoldingCount={4}",
                    result, adapter.SetSaturationOnAllDisplaysCallCount, adapter.SetSaturationOnDisplayNames.Count,
                    adapter.SetSaturationOnDisplayNames.Count > 0 ? (adapter.SetSaturationOnDisplayNames[0] ?? "null") : "(none)",
                    VibranceRestoreHelper.HoldingCount));

            ProfileToggleHelper.ResetForTests();
            VibranceRestoreHelper.ResetForTests();
        }

        // B2 (on). The mirror-image defect: toggling a suppressed game back ON with the flag
        // false must write (and record as owing a restore) every attached display, exactly like
        // the automatic apply branch does - not just the screen the toggle happened to resolve
        // foregroundWindow to. Witnessed via the real Screen.AllScreens count, so this passes
        // regardless of how many monitors are actually attached to the machine running it.
        private static void CheckAmdToggleOnRespectsWideMode(Checklist checklist)
        {
            ProfileToggleHelper.ResetForTests();
            VibranceRestoreHelper.ResetForTests();
            FakeAmdAdapter adapter = new FakeAmdAdapter();
            ApplicationSetting matchingSetting = new ApplicationSetting();
            matchingSetting.Name = "TestGameB2On";
            matchingSetting.IngameLevel = 230;
            List<ApplicationSetting> settings = new List<ApplicationSetting> { matchingSetting };
            AmdDynamicVibranceProxy proxy = BuildAmdProxy(adapter, settings);
            proxy.SetVibranceWindowsLevel(90);
            ProfileToggleHelper.SetSuppressed("TestGameB2On", true);

            IntPtr desktop = GetDesktopWindow();
            ProfileToggleResult result = proxy.ToggleForegroundProfile(desktop, "TestGameB2On", null);

            bool usedWideOverload = adapter.SetSaturationOnAllDisplaysCallCount == 0 &&
                adapter.SetSaturationOnDisplayNames.Count == 1 && adapter.SetSaturationOnDisplayNames[0] == null &&
                adapter.SetSaturationOnDisplayLevels[0] == 230;
            int expectedHoldingCount = Screen.AllScreens.Length;

            checklist.Check(result == ProfileToggleResult.ToggledOn && usedWideOverload && VibranceRestoreHelper.HoldingCount == expectedHoldingCount,
                string.Format("B2 (on): with affectPrimaryMonitorOnly false, toggling on calls SetSaturationOnDisplay(230, null) - never SetSaturationOnAllDisplays - and records EVERY attached screen as owing a restore ({0} on this machine), got result={1} allDisplaysCalls={2} perDisplayCalls={3} lastName={4} HoldingCount={5}",
                    expectedHoldingCount, result, adapter.SetSaturationOnAllDisplaysCallCount, adapter.SetSaturationOnDisplayNames.Count,
                    adapter.SetSaturationOnDisplayNames.Count > 0 ? (adapter.SetSaturationOnDisplayNames[0] ?? "null") : "(none)",
                    VibranceRestoreHelper.HoldingCount));

            ProfileToggleHelper.ResetForTests();
            VibranceRestoreHelper.ResetForTests();
        }

        private static AmdDynamicVibranceProxy BuildAmdProxy(FakeAmdAdapter adapter, List<ApplicationSetting> settings)
        {
            Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>> windowsResolutionSettings =
                new Dictionary<string, Tuple<ResolutionModeWrapper, List<ResolutionModeWrapper>>>();
            AmdDynamicVibranceProxy proxy = new AmdDynamicVibranceProxy(adapter, settings, windowsResolutionSettings);
            proxy.SetNeverChangeColorSettings(true);
            proxy.SetNeverSwitchResolution(true);
            return proxy;
        }

        // ------------------------------------------------------------------
        // Suppression gate - the real, private OnWinEventHook by reflection, both vendors.
        // ------------------------------------------------------------------

        private static void RunSuppressionGateChecks(Checklist checklist)
        {
            checklist.Lines.Add("Per-game suppression gate at the top of the apply branch (real OnWinEventHook via reflection, both vendors):");

            CheckNvidiaSuppressedGameMakesNoCalls(checklist);
            CheckNvidiaGameScreenUnchangedWhenSuppressed(checklist);
            CheckNvidiaDifferentUnsuppressedGameStillApplies(checklist);
            CheckNvidiaRestoreBranchStillRunsWhileSomethingIsSuppressed(checklist);
            CheckNvidiaNotSuppressedAppliesNormally(checklist);
            CheckAmdSuppressedGameMakesNoCalls(checklist);

            checklist.Lines.Add(string.Empty);
        }

        // ------------------------------------------------------------------
        // Stranded-suppression cleanup - VibranceGUI.ClearSuppressionIfNameChanged, the real
        // production method both the "remove program" and "Change executable..." call sites now
        // route through (same assembly, internal - no reflection needed). Repro this closes: a
        // game A gets suppressed, its profile is later removed (or edited so its Name changes),
        // and a brand new, UNRELATED profile that happens to resolve to the same Name would
        // otherwise silently start suppressed with no action from that user.
        // ------------------------------------------------------------------

        private static void RunSuppressionCleanupChecks(Checklist checklist)
        {
            checklist.Lines.Add("Stranded-suppression cleanup (VibranceGUI.ClearSuppressionIfNameChanged, real method, no reflection):");

            CheckClearSuppressionOnRemoval(checklist);
            CheckClearSuppressionOnRename(checklist);
            CheckClearSuppressionLeavesADifferentSuppressionAlone(checklist);

            checklist.Lines.Add(string.Empty);
        }

        // Mutation this guards: drop the removal call site's use of ClearSuppressionIfNameChanged
        // entirely (newName: null models "this profile no longer exists at all").
        private static void CheckClearSuppressionOnRemoval(Checklist checklist)
        {
            ProfileToggleHelper.ResetForTests();

            ApplicationSetting removedSetting = new ApplicationSetting();
            removedSetting.Name = "TestGameCleanupRemoved";
            ProfileToggleHelper.SetSuppressed("TestGameCleanupRemoved", true);

            VibranceGUI.ClearSuppressionIfNameChanged(removedSetting, null);

            checklist.Check(!ProfileToggleHelper.IsSuppressed("TestGameCleanupRemoved"),
                "Cleanup-1: removing a suppressed profile (newName: null) clears its suppression - a later, unrelated profile that resolves to the same Name must not silently inherit it");

            ProfileToggleHelper.ResetForTests();
        }

        // Mutation this guards: compare FileName instead of Name (or skip the comparison and
        // never clear at all) when deciding whether "Change executable..." actually moved this
        // profile off its old suppression key.
        private static void CheckClearSuppressionOnRename(Checklist checklist)
        {
            ProfileToggleHelper.ResetForTests();

            ApplicationSetting oldSetting = new ApplicationSetting();
            oldSetting.Name = "TestGameCleanupOldName";
            ProfileToggleHelper.SetSuppressed("TestGameCleanupOldName", true);

            VibranceGUI.ClearSuppressionIfNameChanged(oldSetting, "TestGameCleanupNewName");

            checklist.Check(!ProfileToggleHelper.IsSuppressed("TestGameCleanupOldName"),
                "Cleanup-2: \"Change executable...\" moving a suppressed profile to a new Name clears the suppression recorded under the OLD Name");

            ProfileToggleHelper.ResetForTests();
        }

        // Mutation this guards: clear suppression unconditionally (or key it on something other
        // than Name), wiping out an unrelated profile's own, legitimate suppression.
        private static void CheckClearSuppressionLeavesADifferentSuppressionAlone(Checklist checklist)
        {
            ProfileToggleHelper.ResetForTests();

            ApplicationSetting oldSetting = new ApplicationSetting();
            oldSetting.Name = "TestGameCleanupA";
            ProfileToggleHelper.SetSuppressed("TestGameCleanupA", true);
            ProfileToggleHelper.SetSuppressed("TestGameCleanupB", true);

            VibranceGUI.ClearSuppressionIfNameChanged(oldSetting, "TestGameCleanupANewName");

            checklist.Check(!ProfileToggleHelper.IsSuppressed("TestGameCleanupA") && ProfileToggleHelper.IsSuppressed("TestGameCleanupB"),
                "Cleanup-3: clearing profile A's suppression on rename leaves a DIFFERENT profile B's own suppression untouched");

            ProfileToggleHelper.ResetForTests();
        }

        private static void InvokeNvidiaOnWinEventHook(string processName, IntPtr handle)
        {
            MethodInfo onWinEventHook = typeof(NvidiaDynamicVibranceProxy).GetMethod(
                "OnWinEventHook", BindingFlags.NonPublic | BindingFlags.Static);
            WinEventHookEventArgs args = new WinEventHookEventArgs { Handle = handle, ProcessName = processName, ProcessImagePath = null };
            onWinEventHook.Invoke(null, new object[] { null, args });
        }

        private static Screen GetNvidiaGameScreen()
        {
            FieldInfo f = typeof(NvidiaDynamicVibranceProxy).GetField("_gameScreen", BindingFlags.NonPublic | BindingFlags.Static);
            return (Screen)f.GetValue(null);
        }

        private static void SetNvidiaGameScreen(Screen value)
        {
            FieldInfo f = typeof(NvidiaDynamicVibranceProxy).GetField("_gameScreen", BindingFlags.NonPublic | BindingFlags.Static);
            f.SetValue(null, value);
        }

        // G1. Mutation this guards: remove the suppression gate entirely.
        private static void CheckNvidiaSuppressedGameMakesNoCalls(Checklist checklist)
        {
            FakeNvidiaVibranceDevice device = new FakeNvidiaVibranceDevice();
            ApplicationSetting matchingSetting = new ApplicationSetting();
            matchingSetting.Name = "TestGameG1";
            matchingSetting.IngameLevel = 40;
            List<ApplicationSetting> settings = new List<ApplicationSetting> { matchingSetting };
            VibranceInfo vibranceInfo = new VibranceInfo();
            vibranceInfo.neverChangeResolution = true;
            vibranceInfo.neverChangeColorSettings = true;
            NvidiaDynamicVibranceProxy.ResetForTests(device, vibranceInfo, settings);
            ProfileToggleHelper.SetSuppressed("TestGameG1", true);

            InvokeNvidiaOnWinEventHook("TestGameG1", GetDesktopWindow());

            checklist.Check(device.TotalCallCount == 0,
                string.Format("G1: a suppressed game's own foreground event makes zero device calls, got {0}", device.TotalCallCount));

            ProfileToggleHelper.ResetForTests();
        }

        // G2. Mutation this guards: place the gate AFTER "_gameScreen = screen;" instead of
        // before it - _gameScreen is forced to a known baseline (null) first, so this does not
        // depend on how many real monitors are attached.
        private static void CheckNvidiaGameScreenUnchangedWhenSuppressed(Checklist checklist)
        {
            FakeNvidiaVibranceDevice device = new FakeNvidiaVibranceDevice();
            ApplicationSetting matchingSetting = new ApplicationSetting();
            matchingSetting.Name = "TestGameG2";
            matchingSetting.IngameLevel = 40;
            List<ApplicationSetting> settings = new List<ApplicationSetting> { matchingSetting };
            VibranceInfo vibranceInfo = new VibranceInfo();
            vibranceInfo.neverChangeResolution = true;
            vibranceInfo.neverChangeColorSettings = true;
            NvidiaDynamicVibranceProxy.ResetForTests(device, vibranceInfo, settings);
            ProfileToggleHelper.SetSuppressed("TestGameG2", true);
            SetNvidiaGameScreen(null);

            InvokeNvidiaOnWinEventHook("TestGameG2", GetDesktopWindow());

            checklist.Check(GetNvidiaGameScreen() == null,
                "G2: _gameScreen is left exactly as it was (null) when the matched game is suppressed - a gate placed AFTER \"_gameScreen = screen\" would leave it non-null instead");

            ProfileToggleHelper.ResetForTests();
        }

        // G3. Mutation this guards: key the gate on "any suppression exists at all"
        // (ProfileToggleHelper.SuppressedCount > 0) instead of THIS setting's own name.
        private static void CheckNvidiaDifferentUnsuppressedGameStillApplies(Checklist checklist)
        {
            FakeNvidiaVibranceDevice device = new FakeNvidiaVibranceDevice();
            ApplicationSetting suppressedSetting = new ApplicationSetting();
            suppressedSetting.Name = "TestGameG3Suppressed";
            suppressedSetting.IngameLevel = 40;
            ApplicationSetting unsuppressedSetting = new ApplicationSetting();
            unsuppressedSetting.Name = "TestGameG3Unsuppressed";
            unsuppressedSetting.IngameLevel = 60;
            List<ApplicationSetting> settings = new List<ApplicationSetting> { suppressedSetting, unsuppressedSetting };
            VibranceInfo vibranceInfo = new VibranceInfo();
            vibranceInfo.neverChangeResolution = true;
            vibranceInfo.neverChangeColorSettings = true;
            NvidiaDynamicVibranceProxy.ResetForTests(device, vibranceInfo, settings);
            ProfileToggleHelper.SetSuppressed("TestGameG3Suppressed", true);

            InvokeNvidiaOnWinEventHook("TestGameG3Unsuppressed", GetDesktopWindow());

            checklist.Check(device.SetLevelCalls.Count == 1,
                string.Format("G3: a DIFFERENT, unsuppressed game still applies normally while another profile is suppressed, got {0} SetLevel call(s)", device.SetLevelCalls.Count));

            ProfileToggleHelper.ResetForTests();
        }

        // G4. Mutation this guards: gate the WHOLE handler (both branches) on any suppression
        // existing, the shape of the discarded global-pause design's gate.
        private static void CheckNvidiaRestoreBranchStillRunsWhileSomethingIsSuppressed(Checklist checklist)
        {
            FakeNvidiaVibranceDevice device = new FakeNvidiaVibranceDevice();
            ApplicationSetting suppressedSetting = new ApplicationSetting();
            suppressedSetting.Name = "TestGameG4";
            suppressedSetting.IngameLevel = 40;
            List<ApplicationSetting> settings = new List<ApplicationSetting> { suppressedSetting };
            VibranceInfo vibranceInfo = new VibranceInfo();
            vibranceInfo.neverChangeResolution = true;
            vibranceInfo.neverChangeColorSettings = true;
            vibranceInfo.isWindowsLevelKnown = true;
            vibranceInfo.userVibranceSettingDefault = 25;
            // Scopes RestoreWindowsVibranceLevel to the work-list + primary
            // (ComposeRestoreTargets/RestoreOneDisplay) - the branch this check actually means to
            // exercise. Left at the struct default (false), it takes the OTHER restore branch
            // instead, which only ever consults displayHandles (null here) and drains the whole
            // work-list via ClearAllGameLevelRecords() without writing anything at all - a false
            // "restore branch does nothing" that has nothing to do with the suppression gate.
            vibranceInfo.affectPrimaryMonitorOnly = true;
            NvidiaDynamicVibranceProxy.ResetForTests(device, vibranceInfo, settings);
            ProfileToggleHelper.SetSuppressed("TestGameG4", true);

            const string worklistDevice = "\\\\.\\DISPLAY_TESTONLY_G4";
            VibranceRestoreHelper.RecordGameLevelApplied(worklistDevice);

            // A non-matching process name (desktop, explorer, ...) drives the restore ("else")
            // branch, not the apply branch the suppression gate sits in.
            InvokeNvidiaOnWinEventHook("explorer", GetDesktopWindow());

            checklist.Check(device.SetLevelCalls.Count > 0 && VibranceRestoreHelper.HoldingCount == 0,
                string.Format("G4: the restore branch still runs (and drains the work-list) while a DIFFERENT profile is suppressed, got {0} SetLevel call(s), HoldingCount={1}",
                    device.SetLevelCalls.Count, VibranceRestoreHelper.HoldingCount));

            ProfileToggleHelper.ResetForTests();
        }

        // G5 (PIN). The ordinary, not-suppressed apply path still works once the gate exists.
        private static void CheckNvidiaNotSuppressedAppliesNormally(Checklist checklist)
        {
            FakeNvidiaVibranceDevice device = new FakeNvidiaVibranceDevice();
            ApplicationSetting matchingSetting = new ApplicationSetting();
            matchingSetting.Name = "TestGameG5";
            matchingSetting.IngameLevel = 40;
            List<ApplicationSetting> settings = new List<ApplicationSetting> { matchingSetting };
            VibranceInfo vibranceInfo = new VibranceInfo();
            vibranceInfo.neverChangeResolution = true;
            vibranceInfo.neverChangeColorSettings = true;
            NvidiaDynamicVibranceProxy.ResetForTests(device, vibranceInfo, settings);

            InvokeNvidiaOnWinEventHook("TestGameG5", GetDesktopWindow());

            checklist.Check(device.SetLevelCalls.Count == 1,
                string.Format("G5 (pin): not suppressed + a matched game applies the ingame level as today, got {0} SetLevel call(s)", device.SetLevelCalls.Count));
        }

        // G6, AMD's counterpart of G1.
        private static void CheckAmdSuppressedGameMakesNoCalls(Checklist checklist)
        {
            ProfileToggleHelper.ResetForTests();
            FakeAmdAdapter adapter = new FakeAmdAdapter();
            ApplicationSetting matchingSetting = new ApplicationSetting();
            matchingSetting.Name = "TestGameG6";
            matchingSetting.IngameLevel = 200;
            List<ApplicationSetting> settings = new List<ApplicationSetting> { matchingSetting };
            AmdDynamicVibranceProxy proxy = BuildAmdProxy(adapter, settings);
            proxy.SetVibranceWindowsLevel(90);
            ProfileToggleHelper.SetSuppressed("TestGameG6", true);

            MethodInfo onWinEventHook = typeof(AmdDynamicVibranceProxy).GetMethod("OnWinEventHook", BindingFlags.NonPublic | BindingFlags.Instance);
            WinEventHookEventArgs args = new WinEventHookEventArgs { Handle = GetDesktopWindow(), ProcessName = "TestGameG6", ProcessImagePath = null };
            onWinEventHook.Invoke(proxy, new object[] { null, args });

            checklist.Check(adapter.SetSaturationOnAllDisplaysCallCount == 0 && adapter.SetSaturationOnDisplayNames.Count == 0,
                string.Format("G6: a suppressed AMD game's own foreground event makes zero adapter calls, got allCalls={0} perDisplayCalls={1}",
                    adapter.SetSaturationOnAllDisplaysCallCount, adapter.SetSaturationOnDisplayNames.Count));

            ProfileToggleHelper.ResetForTests();
        }

        // ------------------------------------------------------------------
        // Settings round trip - a temp INI/XML pair, deleted in a finally, NEVER the user's real
        // "%APPDATA%\vibranceGUI\vibranceGUI.ini". Both keys: toggleHotkey (canonical text) and
        // toggleHotkeyEnabled (bool).
        // ------------------------------------------------------------------

        private static void RunSettingsChecks(Checklist checklist)
        {
            checklist.Lines.Add("SettingsController round trip, both keys (temp INI, never the user's real one):");

            CheckSettingsRoundTripBothKeys(checklist);
            CheckSettingsMissingKeysReadDefaults(checklist);
            CheckSettingsCorruptValuesDoNotThrow(checklist);

            checklist.Lines.Add(string.Empty);
        }

        private static string NewTempPath(string extension)
        {
            return Path.Combine(Path.GetTempPath(), "vibranceGUI-hotkey-fixture-" + Guid.NewGuid().ToString("N") + extension);
        }

        private static void DeleteFileIfExists(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
                // Best-effort temp file cleanup only - never let this mask a check's own result.
            }
        }

        // S1. Mutation this guards: have SetToggleHotkey/SetToggleHotkeyEnabled write under the
        // wrong key name, or the readers read a different one.
        private static void CheckSettingsRoundTripBothKeys(Checklist checklist)
        {
            string tempIni = NewTempPath(".ini");
            string tempXml = NewTempPath(".xml");
            try
            {
                SettingsController controller = new SettingsController(tempIni, tempXml);
                bool wroteBinding = controller.SetToggleHotkey("Ctrl+Alt+F9");
                bool wroteEnabled = controller.SetToggleHotkeyEnabled(true);
                string readBackBinding = controller.ReadToggleHotkey();
                bool readBackEnabled = controller.ReadToggleHotkeyEnabled();

                checklist.Check(wroteBinding && wroteEnabled && readBackBinding == "Ctrl+Alt+F9" && readBackEnabled,
                    string.Format("S1: toggleHotkey and toggleHotkeyEnabled both round-trip byte for byte against a temp INI, got wroteBinding={0} wroteEnabled={1} readBackBinding=\"{2}\" readBackEnabled={3}",
                        wroteBinding, wroteEnabled, readBackBinding, readBackEnabled));
            }
            finally
            {
                DeleteFileIfExists(tempIni);
                DeleteFileIfExists(tempXml);
            }
        }

        // S2. Mutation this guards: default either reader's missing-key value to something other
        // than ""/false.
        private static void CheckSettingsMissingKeysReadDefaults(Checklist checklist)
        {
            string tempIni = NewTempPath(".ini");
            string tempXml = NewTempPath(".xml");
            try
            {
                SettingsController controller = new SettingsController(tempIni, tempXml);
                // Writes a DIFFERENT key first, so the INI file exists but never contains either
                // toggle-hotkey key.
                controller.SetVibranceSetting("someOtherKey", "someOtherValue");

                string readBackBinding = controller.ReadToggleHotkey();
                bool readBackEnabled = controller.ReadToggleHotkeyEnabled();
                HotkeyBinding parsedBinding;
                bool parsed = HotkeyBindingParser.TryParse(readBackBinding, out parsedBinding);

                checklist.Check(readBackBinding == string.Empty && !parsed && !readBackEnabled,
                    string.Format("S2: missing keys read back as \"\"/false and never register, got readBackBinding=\"{0}\" parsed={1} readBackEnabled={2}",
                        readBackBinding, parsed, readBackEnabled));
            }
            finally
            {
                DeleteFileIfExists(tempIni);
                DeleteFileIfExists(tempXml);
            }
        }

        // S3. Mutation this guards: let either reader throw on a value the writers never
        // themselves produced.
        private static void CheckSettingsCorruptValuesDoNotThrow(Checklist checklist)
        {
            string tempIni = NewTempPath(".ini");
            string tempXml = NewTempPath(".xml");
            try
            {
                SettingsController controller = new SettingsController(tempIni, tempXml);
                controller.SetToggleHotkey("Ctrl+NotARealKeyName");
                controller.SetVibranceSetting("toggleHotkeyEnabled", "NotABool");

                bool threw = false;
                HotkeyBinding binding = HotkeyBinding.None;
                bool enabled = true;
                try
                {
                    string readBackBinding = controller.ReadToggleHotkey();
                    HotkeyBindingParser.TryParse(readBackBinding, out binding);
                    enabled = controller.ReadToggleHotkeyEnabled();
                }
                catch (Exception)
                {
                    threw = true;
                }

                checklist.Check(!threw && !binding.IsSet && !enabled,
                    string.Format("S3: a corrupt stored binding never throws and yields IsSet == false, and a corrupt bool string defaults to false, got threw={0} isSet={1} enabled={2}",
                        threw, binding.IsSet, enabled));
            }
            finally
            {
                DeleteFileIfExists(tempIni);
                DeleteFileIfExists(tempXml);
            }
        }

        // Records every (deviceName -> handle) resolution and every (handle -> level) write this
        // fake is asked to make, plus a combined call counter (TotalCallCount) the gate checks
        // above use for their "zero calls AT ALL" assertions. VibranceRestoreFixture's own
        // FakeNvidiaVibranceDevice is a private nested class there and not reachable from this
        // file, so this is a second, smaller copy of the same shape.
        private class FakeNvidiaVibranceDevice : INvidiaVibranceDevice
        {
            private readonly Dictionary<string, int> _handlesByDeviceName = new Dictionary<string, int>();
            private readonly Dictionary<int, int> _levelsByHandle = new Dictionary<int, int>();
            private readonly HashSet<int> _failNextSetLevel = new HashSet<int>();
            private int _nextHandle = 1;

            public readonly List<int> SetLevelCalls = new List<int>();
            public readonly List<string> ResolvedDeviceNames = new List<string>();
            public int TotalCallCount;

            public int HandleFor(string deviceName)
            {
                return ResolveOrAssign(deviceName);
            }

            public void SeedLevel(string deviceName, int level)
            {
                _levelsByHandle[ResolveOrAssign(deviceName)] = level;
            }

            public void FailNextSetLevel(string deviceName)
            {
                _failNextSetLevel.Add(ResolveOrAssign(deviceName));
            }

            private int ResolveOrAssign(string deviceName)
            {
                int handle;
                if (!_handlesByDeviceName.TryGetValue(deviceName, out handle))
                {
                    handle = _nextHandle++;
                    _handlesByDeviceName[deviceName] = handle;
                }
                return handle;
            }

            public bool IsWindowActive(ref IntPtr hWnd)
            {
                TotalCallCount++;
                return true;
            }

            public int TryResolveDisplayHandle(string deviceName)
            {
                TotalCallCount++;
                ResolvedDeviceNames.Add(deviceName);
                if (string.IsNullOrEmpty(deviceName))
                {
                    return -1;
                }
                return ResolveOrAssign(deviceName);
            }

            public bool IsAtLevel(int displayHandle, int level)
            {
                TotalCallCount++;
                int current;
                return _levelsByHandle.TryGetValue(displayHandle, out current) && current == level;
            }

            public bool SetLevel(int displayHandle, int level)
            {
                TotalCallCount++;
                SetLevelCalls.Add(displayHandle);
                if (_failNextSetLevel.Remove(displayHandle))
                {
                    return false;
                }
                _levelsByHandle[displayHandle] = level;
                return true;
            }
        }

        // Everything IAmdAdapter exposes, none of it touching real hardware - a second, smaller
        // copy of VibranceRestoreFixture's own private nested fake, extended with one-shot
        // per-display failure injection for T4a/T5a above.
        private class FakeAmdAdapter : IAmdAdapter
        {
            private readonly HashSet<string> _failNextSetSaturationOnDisplay = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public int SetSaturationOnAllDisplaysCallCount;

            public readonly List<int> SetSaturationOnDisplayLevels = new List<int>();
            public readonly List<string> SetSaturationOnDisplayNames = new List<string>();

            public void FailNextSetSaturationOnDisplay(string displayName)
            {
                _failNextSetSaturationOnDisplay.Add(displayName ?? string.Empty);
            }

            public void SetSaturationOnAllDisplays(int vibranceLevel)
            {
                SetSaturationOnAllDisplaysCallCount++;
            }

            public bool SetSaturationOnDisplay(int vibranceLevel, string displayName)
            {
                SetSaturationOnDisplayLevels.Add(vibranceLevel);
                SetSaturationOnDisplayNames.Add(displayName);
                return !_failNextSetSaturationOnDisplay.Remove(displayName ?? string.Empty);
            }

            public bool IsAvailable()
            {
                return false;
            }

            public void Init()
            {
            }

            public void Dispose()
            {
            }
        }

        // Records every Register/Unregister call this fake is asked to make, with a one-shot
        // FIFO queue of forced results - mirrors ResolutionChangeFixture.FakeDisplayModeDevice's
        // QueueResult shape. Never touches a real hotkey - see this file's own header comment.
        private class FakeHotkeyRegistrar : IHotkeyRegistrar
        {
            public struct RegisterCall
            {
                public readonly IntPtr HWnd;
                public readonly int Id;
                public readonly uint Modifiers;
                public readonly uint VirtualKey;

                public RegisterCall(IntPtr hWnd, int id, uint modifiers, uint virtualKey)
                {
                    HWnd = hWnd;
                    Id = id;
                    Modifiers = modifiers;
                    VirtualKey = virtualKey;
                }
            }

            private readonly Queue<HotkeyRegistrationResult> _queuedResults = new Queue<HotkeyRegistrationResult>();

            public readonly List<RegisterCall> RegisterCalls = new List<RegisterCall>();
            public readonly List<IntPtr> UnregisterCalls = new List<IntPtr>();

            public void QueueResult(HotkeyRegistrationResult result)
            {
                _queuedResults.Enqueue(result);
            }

            public HotkeyRegistrationResult Register(IntPtr hWnd, int id, uint modifiers, uint virtualKey)
            {
                RegisterCalls.Add(new RegisterCall(hWnd, id, modifiers, virtualKey));
                if (_queuedResults.Count > 0)
                {
                    return _queuedResults.Dequeue();
                }
                return HotkeyRegistrationResult.Registered;
            }

            public void Unregister(IntPtr hWnd, int id)
            {
                UnregisterCalls.Add(hWnd);
            }
        }

        private class Checklist
        {
            public readonly List<string> Lines = new List<string>();
            public int Passed;
            public int Total;

            public void Check(bool condition, string description)
            {
                Total++;
                if (condition)
                    Passed++;
                Lines.Add(string.Format("[{0}] {1}", condition ? "PASS" : "FAIL", description));
            }

            // Deliberately not counted in Total/Passed - see StabilityFixture.Checklist.Skip for
            // the convention this follows. None of the checks above currently need it (unlike the
            // pre-existing AMD checks in VibranceRestoreFixture, nothing here reads the real
            // GetForegroundWindow()), but it is kept for parity with every other fixture's
            // Checklist shape.
            public void Skip(string description)
            {
                Lines.Add(string.Format("[SKIP] {0}", description));
            }
        }
    }
}
