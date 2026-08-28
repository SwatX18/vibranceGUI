using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace vibrance.GUI.common
{
    /// <summary>
    /// Regression coverage for the separate SDR/HDR vibrance level foundation (upstream #147, PR 1
    /// of 2): HdrVibranceHelper's pure resolver, HdrStateTracker's cache and change detection
    /// against a fake IHdrStateReader, the P/Invoke struct layout RealHdrStateReader depends on,
    /// and ApplicationSetting.HdrIngameLevel's on-disk round trip through SettingsController. Run
    /// by vibrance.GUI.exe --selftest-hdr.
    ///
    /// Unlike --selftest-vibrance, --selftest-resolution and --selftest-profiletoggle, the
    /// diagnostics section below deliberately DOES touch real hardware - see the header comment on
    /// --selftest-hdr in Program.cs for why that is safe here specifically: QueryDisplayConfig and
    /// DisplayConfigGetDeviceInfo only ever READ display configuration, so unlike a gamma or
    /// resolution write this cannot change any display's state, and it is the only way anyone
    /// learns what a given machine's real sweep reports. Precedent: VibranceRestoreFixture's AMD
    /// checks already read the real GetForegroundWindow() and Skip on a mismatch; this fixture's
    /// one counted hardware check (N1) follows the same Skip-on-unavailable convention.
    /// </summary>
    public static class HdrVibranceFixture
    {
        public static List<string> Run()
        {
            Checklist checklist = new Checklist();
            checklist.Lines.Add("vibranceGUI HDR vibrance foundation self test");
            checklist.Lines.Add(string.Empty);

            RunPureResolverChecks(checklist);
            RunTrackerChecks(checklist);
            RunStructLayoutChecks(checklist);
            RunColorInfoMappingChecks(checklist);
            RunSettingsChecks(checklist);
            RunDiagnosticsChecks(checklist);

            checklist.Lines.Add(string.Empty);
            checklist.Lines.Add(string.Format("PASSED {0}/{1}", checklist.Passed, checklist.Total));
            return checklist.Lines;
        }

        // ------------------------------------------------------------------
        // Pure - HdrVibranceHelper.ResolveIngameLevel/HasSeparateHdrLevel/DescribeHdrStatus, no
        // fakes, no device, no Screen at all.
        // ------------------------------------------------------------------

        private static void RunPureResolverChecks(Checklist checklist)
        {
            checklist.Lines.Add("HdrVibranceHelper (pure):");

            CheckResolveUnsetSdr(checklist);
            CheckResolveUnsetHdr(checklist);
            CheckResolveSetHdr(checklist);
            CheckResolveSetSdr(checklist);
            CheckResolveZeroLevelHdr(checklist);
            CheckResolveUnknown(checklist);
            CheckHasSeparateHdrLevel(checklist);

            CheckDescribeHdrStatusUnavailable(checklist);
            CheckDescribeHdrStatusNoCapableDisplay(checklist);
            CheckDescribeHdrStatusOffEverywhere(checklist);
            CheckDescribeHdrStatusOneActive(checklist);
            CheckDescribeHdrStatusMultipleActive(checklist);

            checklist.Lines.Add(string.Empty);
        }

        private static ApplicationSetting BuildSetting(int ingameLevel, int hdrIngameLevel)
        {
            ApplicationSetting setting = new ApplicationSetting("Game", "game.exe", ingameLevel, null, false, 50, 50, 100);
            setting.HdrIngameLevel = hdrIngameLevel;
            return setting;
        }

        // P1. Mutation this guards: ResolveIngameLevel returning HdrIngameLevel whenever it is
        // configured, regardless of display state.
        private static void CheckResolveUnsetSdr(Checklist checklist)
        {
            ApplicationSetting setting = BuildSetting(80, HdrVibranceHelper.HdrLevelUnset);
            int resolved = HdrVibranceHelper.ResolveIngameLevel(setting, HdrDisplayState.Sdr);
            checklist.Check(resolved == 80, string.Format("P1: unset HdrIngameLevel + Sdr resolves to IngameLevel, got {0}", resolved));
        }

        // P2. Mutation this guards: treating Unset as a real HDR level once the display IS Hdr.
        private static void CheckResolveUnsetHdr(Checklist checklist)
        {
            ApplicationSetting setting = BuildSetting(80, HdrVibranceHelper.HdrLevelUnset);
            int resolved = HdrVibranceHelper.ResolveIngameLevel(setting, HdrDisplayState.Hdr);
            checklist.Check(resolved == 80, string.Format("P2: unset HdrIngameLevel + Hdr still resolves to IngameLevel, got {0}", resolved));
        }

        // P3. Mutation this guards: ignoring a configured HdrIngameLevel even when the display IS Hdr.
        private static void CheckResolveSetHdr(Checklist checklist)
        {
            ApplicationSetting setting = BuildSetting(80, 40);
            int resolved = HdrVibranceHelper.ResolveIngameLevel(setting, HdrDisplayState.Hdr);
            checklist.Check(resolved == 40, string.Format("P3: a configured HdrIngameLevel + Hdr resolves to HdrIngameLevel, got {0}", resolved));
        }

        // P4. Mutation this guards: applying HdrIngameLevel regardless of display state (swapped branches).
        private static void CheckResolveSetSdr(Checklist checklist)
        {
            ApplicationSetting setting = BuildSetting(80, 40);
            int resolved = HdrVibranceHelper.ResolveIngameLevel(setting, HdrDisplayState.Sdr);
            checklist.Check(resolved == 80, string.Format("P4: a configured HdrIngameLevel + Sdr still resolves to IngameLevel, got {0}", resolved));
        }

        // P5. Mutation this guards: HasSeparateHdrLevel using "> 0" instead of ">= 0", which would
        // silently lose a configured level of exactly 0.
        private static void CheckResolveZeroLevelHdr(Checklist checklist)
        {
            ApplicationSetting setting = BuildSetting(80, 0);
            int resolved = HdrVibranceHelper.ResolveIngameLevel(setting, HdrDisplayState.Hdr);
            checklist.Check(resolved == 0, string.Format("P5: HdrIngameLevel == 0 + Hdr resolves to 0, not IngameLevel, got {0}", resolved));
        }

        // P6. Mutation this guards: treating HdrDisplayState.Unknown like Hdr instead of like Sdr.
        private static void CheckResolveUnknown(Checklist checklist)
        {
            ApplicationSetting setting = BuildSetting(80, 40);
            int resolved = HdrVibranceHelper.ResolveIngameLevel(setting, HdrDisplayState.Unknown);
            checklist.Check(resolved == 80, string.Format("P6: HdrDisplayState.Unknown resolves to IngameLevel exactly like Sdr, not like Hdr, got {0}", resolved));
        }

        private static void CheckHasSeparateHdrLevel(Checklist checklist)
        {
            checklist.Check(!HdrVibranceHelper.HasSeparateHdrLevel(-1), "P7a: HasSeparateHdrLevel(-1) is false");
            checklist.Check(HdrVibranceHelper.HasSeparateHdrLevel(0), "P7b: HasSeparateHdrLevel(0) is true - 0 is a legal level, not a second spelling of unset");
            checklist.Check(HdrVibranceHelper.HasSeparateHdrLevel(63), "P7c: HasSeparateHdrLevel(63) is true");
        }

        private static void CheckDescribeHdrStatusUnavailable(Checklist checklist)
        {
            string message = HdrVibranceHelper.DescribeHdrStatus(new List<HdrDisplayInfo>(), false);
            checklist.Check(message == "vibranceGUI cannot detect HDR on this version of Windows, so this level will never be used.",
                string.Format("P8: DescribeHdrStatus with isReaderAvailable=false, got \"{0}\"", message));
        }

        private static void CheckDescribeHdrStatusNoCapableDisplay(Checklist checklist)
        {
            List<HdrDisplayInfo> sweep = OneDisplay(@"\\.\DISPLAY1", HdrDisplayState.Sdr, false);
            string message = HdrVibranceHelper.DescribeHdrStatus(sweep, true);
            checklist.Check(message == "No attached display reports HDR support.",
                string.Format("P9: DescribeHdrStatus with no HDR-capable display, got \"{0}\"", message));
        }

        private static void CheckDescribeHdrStatusOffEverywhere(Checklist checklist)
        {
            List<HdrDisplayInfo> sweep = OneDisplay(@"\\.\DISPLAY1", HdrDisplayState.Sdr, true);
            string message = HdrVibranceHelper.DescribeHdrStatus(sweep, true);
            checklist.Check(message == "Windows HDR is currently off on every attached display.",
                string.Format("P10: DescribeHdrStatus with a capable display currently in Sdr, got \"{0}\"", message));
        }

        private static void CheckDescribeHdrStatusOneActive(Checklist checklist)
        {
            List<HdrDisplayInfo> sweep = OneDisplay(@"\\.\DISPLAY1", HdrDisplayState.Hdr, true);
            string message = HdrVibranceHelper.DescribeHdrStatus(sweep, true);
            checklist.Check(message == @"Windows HDR is currently on for \\.\DISPLAY1.",
                string.Format("P11: DescribeHdrStatus with exactly one active HDR display, got \"{0}\"", message));
        }

        private static void CheckDescribeHdrStatusMultipleActive(Checklist checklist)
        {
            List<HdrDisplayInfo> sweep = new List<HdrDisplayInfo>();
            sweep.Add(BuildInfo(@"\\.\DISPLAY1", HdrDisplayState.Hdr, true));
            sweep.Add(BuildInfo(@"\\.\DISPLAY2", HdrDisplayState.Hdr, true));
            sweep.Add(BuildInfo(@"\\.\DISPLAY3", HdrDisplayState.Sdr, true));
            string message = HdrVibranceHelper.DescribeHdrStatus(sweep, true);
            checklist.Check(message == @"Windows HDR is currently on for \\.\DISPLAY1, and 1 more.",
                string.Format("P12: DescribeHdrStatus with two active HDR displays, got \"{0}\"", message));
        }

        private static HdrDisplayInfo BuildInfo(string deviceName, HdrDisplayState state, bool isHdrCapable)
        {
            HdrDisplayInfo info = new HdrDisplayInfo();
            info.DeviceName = deviceName;
            info.State = state;
            info.IsHdrCapable = isHdrCapable;
            return info;
        }

        private static List<HdrDisplayInfo> OneDisplay(string deviceName, HdrDisplayState state, bool isHdrCapable)
        {
            List<HdrDisplayInfo> list = new List<HdrDisplayInfo>();
            list.Add(BuildInfo(deviceName, state, isHdrCapable));
            return list;
        }

        private static List<HdrDisplayInfo> TwoDisplays()
        {
            List<HdrDisplayInfo> list = OneDisplay(@"\\.\DISPLAY1", HdrDisplayState.Sdr, true);
            list.Add(BuildInfo(@"\\.\DISPLAY2", HdrDisplayState.Sdr, false));
            return list;
        }

        // ------------------------------------------------------------------
        // HdrStateTracker - driven entirely through ResetForTests(fake), no display at all.
        // ------------------------------------------------------------------

        private static void RunTrackerChecks(Checklist checklist)
        {
            checklist.Lines.Add("HdrStateTracker (fake IHdrStateReader, no display):");

            CheckTrackerCachesWithinTtl(checklist);
            CheckTrackerInvalidateForcesResweep(checklist);
            CheckTrackerUnknownDeviceName(checklist);
            CheckTrackerThrowingReaderYieldsUnknown(checklist);
            CheckTrackerChangeDetectionFlip(checklist);
            CheckTrackerChangeDetectionRepeat(checklist);
            CheckTrackerChangeDetectionAdd(checklist);
            CheckTrackerChangeDetectionRemove(checklist);

            HdrStateTracker.ResetForTests(null);
            checklist.Lines.Add(string.Empty);
        }

        // T1. Mutation this guards: dropping the TTL cache entirely and re-sweeping on every call.
        private static void CheckTrackerCachesWithinTtl(Checklist checklist)
        {
            FakeHdrStateReader fake = new FakeHdrStateReader();
            fake.NextResult = OneDisplay(@"\\.\DISPLAY1", HdrDisplayState.Sdr, true);
            HdrStateTracker.ResetForTests(fake);

            HdrStateTracker.GetState(@"\\.\DISPLAY1");
            HdrStateTracker.GetState(@"\\.\DISPLAY1");

            checklist.Check(fake.CallCount == 1,
                string.Format("T1: a second GetState call inside the TTL does not re-sweep, got callCount={0}", fake.CallCount));
        }

        // T2. Mutation this guards: Invalidate() being a no-op.
        private static void CheckTrackerInvalidateForcesResweep(Checklist checklist)
        {
            FakeHdrStateReader fake = new FakeHdrStateReader();
            fake.NextResult = OneDisplay(@"\\.\DISPLAY1", HdrDisplayState.Sdr, true);
            HdrStateTracker.ResetForTests(fake);

            HdrStateTracker.GetState(@"\\.\DISPLAY1");
            HdrStateTracker.Invalidate();
            HdrStateTracker.GetState(@"\\.\DISPLAY1");

            checklist.Check(fake.CallCount == 2,
                string.Format("T2: Invalidate() forces the next read to re-sweep, got callCount={0}", fake.CallCount));
        }

        // T3. Mutation this guards: an unrecognised device name falling through to Sdr (or Hdr)
        // instead of Unknown.
        private static void CheckTrackerUnknownDeviceName(Checklist checklist)
        {
            FakeHdrStateReader fake = new FakeHdrStateReader();
            fake.NextResult = OneDisplay(@"\\.\DISPLAY1", HdrDisplayState.Hdr, true);
            HdrStateTracker.ResetForTests(fake);

            HdrDisplayState state = HdrStateTracker.GetState(@"\\.\DISPLAY9");

            checklist.Check(state == HdrDisplayState.Unknown,
                string.Format("T3: a device name absent from the sweep reads back as Unknown, got {0}", state));
        }

        // T4. Mutation this guards: letting a throwing IHdrStateReader.ReadAll() escape past
        // HdrStateTracker - the exact hazard that matters once this runs inside a WinEvent
        // callback frame in PR 2.
        private static void CheckTrackerThrowingReaderYieldsUnknown(Checklist checklist)
        {
            FakeHdrStateReader fake = new FakeHdrStateReader();
            fake.ShouldThrow = true;
            HdrStateTracker.ResetForTests(fake);

            bool threw = false;
            HdrDisplayState state = HdrDisplayState.Hdr;
            try
            {
                state = HdrStateTracker.GetState(@"\\.\DISPLAY1");
            }
            catch (Exception)
            {
                threw = true;
            }

            checklist.Check(!threw && state == HdrDisplayState.Unknown,
                string.Format("T4: a throwing IHdrStateReader.ReadAll() never escapes HdrStateTracker and reads back as Unknown, got threw={0} state={1}", threw, state));
        }

        // T5. Mutation this guards: RefreshAndDetectChange() always returning false.
        private static void CheckTrackerChangeDetectionFlip(Checklist checklist)
        {
            FakeHdrStateReader fake = new FakeHdrStateReader();
            fake.NextResult = OneDisplay(@"\\.\DISPLAY1", HdrDisplayState.Sdr, true);
            HdrStateTracker.ResetForTests(fake);
            HdrStateTracker.RefreshAndDetectChange();

            fake.NextResult = OneDisplay(@"\\.\DISPLAY1", HdrDisplayState.Hdr, true);
            bool changed = HdrStateTracker.RefreshAndDetectChange();

            checklist.Check(changed, string.Format("T5: RefreshAndDetectChange() is true when a device's state flips, got {0}", changed));
        }

        // T6. Mutation this guards: RefreshAndDetectChange() always returning true.
        private static void CheckTrackerChangeDetectionRepeat(Checklist checklist)
        {
            FakeHdrStateReader fake = new FakeHdrStateReader();
            fake.NextResult = OneDisplay(@"\\.\DISPLAY1", HdrDisplayState.Sdr, true);
            HdrStateTracker.ResetForTests(fake);
            HdrStateTracker.RefreshAndDetectChange();

            bool changed = HdrStateTracker.RefreshAndDetectChange();

            checklist.Check(!changed, string.Format("T6: RefreshAndDetectChange() is false when the sweep repeats unchanged, got {0}", changed));
        }

        // T7. Mutation this guards: comparing only devices present in both sweeps, missing an
        // added device entirely.
        private static void CheckTrackerChangeDetectionAdd(Checklist checklist)
        {
            FakeHdrStateReader fake = new FakeHdrStateReader();
            fake.NextResult = OneDisplay(@"\\.\DISPLAY1", HdrDisplayState.Sdr, true);
            HdrStateTracker.ResetForTests(fake);
            HdrStateTracker.RefreshAndDetectChange();

            fake.NextResult = TwoDisplays();
            bool changed = HdrStateTracker.RefreshAndDetectChange();

            checklist.Check(changed, string.Format("T7: RefreshAndDetectChange() is true when a device is added, got {0}", changed));
        }

        // T8. Mutation this guards: the same blind spot as T7, but for a removed device.
        private static void CheckTrackerChangeDetectionRemove(Checklist checklist)
        {
            FakeHdrStateReader fake = new FakeHdrStateReader();
            fake.NextResult = TwoDisplays();
            HdrStateTracker.ResetForTests(fake);
            HdrStateTracker.RefreshAndDetectChange();

            fake.NextResult = OneDisplay(@"\\.\DISPLAY1", HdrDisplayState.Sdr, true);
            bool changed = HdrStateTracker.RefreshAndDetectChange();

            checklist.Check(changed, string.Format("T8: RefreshAndDetectChange() is true when a device is removed, got {0}", changed));
        }

        private class FakeHdrStateReader : IHdrStateReader
        {
            public List<HdrDisplayInfo> NextResult = new List<HdrDisplayInfo>();
            public bool ShouldThrow;
            public int CallCount;
            public bool IsAvailable { get; set; }

            public FakeHdrStateReader()
            {
                IsAvailable = true;
            }

            public List<HdrDisplayInfo> ReadAll()
            {
                CallCount++;
                if (ShouldThrow)
                {
                    throw new InvalidOperationException("FakeHdrStateReader forced failure");
                }
                return NextResult;
            }
        }

        // ------------------------------------------------------------------
        // P/Invoke struct layout - guards the single most likely defect in a P/Invoke nobody can
        // otherwise exercise: a dropped field, a wrong SizeConst, CharSet.Ansi on the source-name
        // struct.
        // ------------------------------------------------------------------

        private static void RunStructLayoutChecks(Checklist checklist)
        {
            checklist.Lines.Add("P/Invoke struct layout (Marshal.SizeOf, this process's real x86 layout):");

            CheckStructSize(checklist, "L1", typeof(DisplayConfigDeviceInfoHeader), 20);
            CheckStructSize(checklist, "L2", typeof(DisplayConfigPathInfo), 72);
            CheckStructSize(checklist, "L3", typeof(DisplayConfigModeInfo), 64);
            CheckStructSize(checklist, "L4", typeof(DisplayConfigSourceDeviceName), 84);
            CheckStructSize(checklist, "L5", typeof(DisplayConfigAdvancedColorInfo), 32);
            CheckStructSize(checklist, "L6", typeof(DisplayConfigAdvancedColorInfo2), 36);

            CheckHeaderSizeField(checklist, "L7", "SourceDeviceNameSize", typeof(DisplayConfigSourceDeviceName));
            CheckHeaderSizeField(checklist, "L8", "AdvancedColorInfoSize", typeof(DisplayConfigAdvancedColorInfo));
            CheckHeaderSizeField(checklist, "L9", "AdvancedColorInfo2Size", typeof(DisplayConfigAdvancedColorInfo2));
            CheckAdvancedColorInfo2TypeValue(checklist);
            CheckDeviceInfoTypeConstant(checklist, "L11", "DeviceInfoGetSourceName", 1,
                "DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME, wingdi.h:3042-3049");
            CheckDeviceInfoTypeConstant(checklist, "L12", "DeviceInfoGetAdvancedColorInfo", 9,
                "DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO, wingdi.h:3042-3049");

            checklist.Lines.Add(string.Empty);
        }

        private static void CheckStructSize(Checklist checklist, string id, Type structType, int expected)
        {
            int actual = Marshal.SizeOf(structType);
            checklist.Check(actual == expected,
                string.Format("{0}: Marshal.SizeOf({1}) == {2}, got {3}", id, structType.Name, expected, actual));
        }

        // Reads RealHdrStateReader's own private, precomputed header.size field via reflection and
        // compares it against an INDEPENDENT Marshal.SizeOf call - proves the value the real P/Invoke
        // call actually sends is not a stale or hand-typed literal that happens to match today.
        private static void CheckHeaderSizeField(Checklist checklist, string id, string privateFieldName, Type structType)
        {
            FieldInfo field = typeof(RealHdrStateReader).GetField(privateFieldName, BindingFlags.NonPublic | BindingFlags.Static);
            uint fieldValue = field == null ? uint.MaxValue : (uint)field.GetValue(null);
            int expected = Marshal.SizeOf(structType);
            checklist.Check(fieldValue == (uint)expected,
                string.Format("{0}: RealHdrStateReader.{1} == Marshal.SizeOf({2}) ({3}), got {4}",
                    id, privateFieldName, structType.Name, expected, fieldValue));
        }

        // L10. Pins the single most load-bearing number in this whole P/Invoke layer, because it
        // was already wrong once: the original spec for this reader said 14, and 14 is actually
        // DISPLAYCONFIG_DEVICE_INFO_SET_RESERVED1 - an undocumented SETTER, not this GETTER. The
        // real value, confirmed against the Windows SDK header itself (not this reader's own
        // comment, which could drift the same way the original spec did):
        //   C:\Program Files (x86)\Windows Kits\10\Include\10.0.26100.0\um\wingdi.h:3042-3049
        //     DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO    = 9,
        //     DISPLAYCONFIG_DEVICE_INFO_SET_RESERVED1              = 14,
        //     DISPLAYCONFIG_DEVICE_INFO_GET_ADVANCED_COLOR_INFO_2  = 15,
        //     DISPLAYCONFIG_DEVICE_INFO_SET_HDR_STATE              = 16,
        // Mutation this guards: aliasing the constant back to 14 - exactly the failure class that
        // produced two of the nine can't-fail tests already caught in this codebase (a test that
        // agrees with the implementation instead of an independent anchor). "15" here is typed
        // directly against the SDK citation above, not read back from RealHdrStateReader itself.
        private static void CheckAdvancedColorInfo2TypeValue(Checklist checklist)
        {
            FieldInfo field = typeof(RealHdrStateReader).GetField("DeviceInfoGetAdvancedColorInfo2", BindingFlags.NonPublic | BindingFlags.Static);
            uint actual = field == null ? uint.MaxValue : (uint)field.GetValue(null);
            checklist.Check(actual == 15,
                string.Format("L10: DeviceInfoGetAdvancedColorInfo2 == 15 (wingdi.h:3042-3049; 14 is the reserved SET_RESERVED1 setter, not this getter), got {0}", actual));
        }

        // L11/L12. Same guard as L10, same reasoning: pin the two other device-info type constants
        // against a literal matching the SDK header, not read back from any shared source, so an
        // implementation that aliased 1 or 9 to some other plausible small integer could not agree
        // with itself. L10 already had a citation; type 9 previously had none at all.
        private static void CheckDeviceInfoTypeConstant(Checklist checklist, string id, string fieldName, uint expected, string citation)
        {
            FieldInfo field = typeof(RealHdrStateReader).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static);
            uint actual = field == null ? uint.MaxValue : (uint)field.GetValue(null);
            checklist.Check(actual == expected,
                string.Format("{0}: RealHdrStateReader.{1} == {2} ({3}), got {4}", id, fieldName, expected, citation, actual));
        }

        // ------------------------------------------------------------------
        // The value -> (state, capability) decoding itself (RealHdrStateReader.MapAdvancedColorInfo2
        // / MapAdvancedColorInfo) - pure, no P/Invoke, no device. Added for review finding B3: the
        // reviewer mutated the capability bit from 0x10 to 0x01 (B1's exact regression) and got
        // 36/36 with no signal, then mutated type 9's constant from 9 to 11 and also got 36/36 -
        // this decoding step was completely uncovered by every prior check. Every literal below is
        // hand-typed against the SDK bit layout (see DisplayConfigAdvancedColorInfo2's own comment),
        // not derived from the implementation under test - two of the four type-15 cases (M1, M2)
        // are real values captured off this machine's three real displays.
        // ------------------------------------------------------------------

        private static void RunColorInfoMappingChecks(Checklist checklist)
        {
            checklist.Lines.Add("Advanced-colour bit decoding (RealHdrStateReader.MapAdvancedColorInfo2/MapAdvancedColorInfo, pure):");

            // M1/M2 are this machine's actual captured type-15 values (DISPLAY2/DISPLAY3 and
            // DISPLAY1 respectively) - see the diagnostics section below for the live sweep this
            // mirrors. M1 is also the one case that distinguishes bit0 from bit4: 0x45 has bit0 set
            // but bit4 clear, so the old (wrong) "value & 1" would have reported this display
            // HDR-capable when it is not.
            CheckMapAdvancedColorInfo2(checklist, "M1", 0x45, 0, HdrDisplayState.Sdr, false, "wide-colour capable only, not HDR-capable - DISPLAY2/DISPLAY3 on this machine");
            CheckMapAdvancedColorInfo2(checklist, "M2", 0x51, 0, HdrDisplayState.Sdr, true, "HDR-capable but currently SDR - DISPLAY1 on this machine");
            CheckMapAdvancedColorInfo2(checklist, "M3", 0x71, 2, HdrDisplayState.Hdr, true, "HDR-capable and currently active");
            CheckMapAdvancedColorInfo2(checklist, "M4", 0x51, 1, HdrDisplayState.Sdr, true, "WCG (activeColorMode 1) is NOT HDR - only activeColorMode 2 is");

            CheckMapAdvancedColorInfo(checklist, "M5", 0x01, HdrDisplayState.Sdr, false, "advancedColorSupported set, advancedColorEnabled clear");
            CheckMapAdvancedColorInfo(checklist, "M6", 0x03, HdrDisplayState.Hdr, true, "advancedColorSupported and advancedColorEnabled both set");

            checklist.Lines.Add(string.Empty);
        }

        private static void CheckMapAdvancedColorInfo2(Checklist checklist, string id, uint value, uint activeColorMode,
            HdrDisplayState expectedState, bool expectedCapable, string note)
        {
            HdrDisplayState state;
            bool isHdrCapable;
            RealHdrStateReader.MapAdvancedColorInfo2(value, activeColorMode, out state, out isHdrCapable);
            checklist.Check(state == expectedState && isHdrCapable == expectedCapable,
                string.Format("{0}: MapAdvancedColorInfo2(value=0x{1:X2}, activeColorMode={2}) expected ({3}, {4}) [{5}], got ({6}, {7})",
                    id, value, activeColorMode, expectedState, expectedCapable, note, state, isHdrCapable));
        }

        private static void CheckMapAdvancedColorInfo(Checklist checklist, string id, uint value,
            HdrDisplayState expectedState, bool expectedCapable, string note)
        {
            HdrDisplayState state;
            bool isHdrCapable;
            RealHdrStateReader.MapAdvancedColorInfo(value, out state, out isHdrCapable);
            checklist.Check(state == expectedState && isHdrCapable == expectedCapable,
                string.Format("{0}: MapAdvancedColorInfo(value=0x{1:X2}) expected ({2}, {3}) [{4}], got ({5}, {6})",
                    id, value, expectedState, expectedCapable, note, state, isHdrCapable));
        }

        // ------------------------------------------------------------------
        // ApplicationSetting.HdrIngameLevel's on-disk round trip - through SettingsController,
        // never a hand-built XmlSerializer copy of its read path.
        // ------------------------------------------------------------------

        private static void RunSettingsChecks(Checklist checklist)
        {
            checklist.Lines.Add("HdrIngameLevel settings round-trip (temp INI + XML, SettingsController.ReadVibranceSettings):");

            CheckSettingsRoundTripsHdrIngameLevel(checklist);
            CheckLegacyXmlWithoutHdrIngameLevelDefaultsToUnset(checklist);
            CheckXmlWithUnknownElementDoesNotThrow(checklist);

            checklist.Lines.Add(string.Empty);
        }

        private static string NewTempPath(string extension)
        {
            return Path.Combine(Path.GetTempPath(), "vibranceGUI-hdr-fixture-" + Guid.NewGuid().ToString("N") + extension);
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

        // ReadVibranceSettings only reaches the XML at all once int.Parse(inactiveValue) succeeds -
        // every other key already has a safe built-in default ("true"/"50"/"100"), so this is the
        // one key a hand-written INI for these checks must actually contain.
        private static void WriteMinimalIni(string path)
        {
            File.WriteAllText(path, "[Settings]\r\ninactiveValue=0\r\n");
        }

        private static string LegacyApplicationSettingXml()
        {
            return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
                "<ArrayOfApplicationSetting>\r\n" +
                "  <ApplicationSetting>\r\n" +
                "    <Name>Legacy Game</Name>\r\n" +
                "    <FileName>legacy.exe</FileName>\r\n" +
                "    <IngameLevel>75</IngameLevel>\r\n" +
                "    <Brightness>50</Brightness>\r\n" +
                "    <Contrast>50</Contrast>\r\n" +
                "    <Gamma>100</Gamma>\r\n" +
                "    <IsResolutionChangeNeeded>false</IsResolutionChangeNeeded>\r\n" +
                "    <IsExecutableUnconfirmed>false</IsExecutableUnconfirmed>\r\n" +
                "  </ApplicationSetting>\r\n" +
                "</ArrayOfApplicationSetting>\r\n";
        }

        private static string XmlWithUnknownElement()
        {
            return "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
                "<ArrayOfApplicationSetting>\r\n" +
                "  <ApplicationSetting>\r\n" +
                "    <Name>Future Game</Name>\r\n" +
                "    <FileName>future.exe</FileName>\r\n" +
                "    <IngameLevel>50</IngameLevel>\r\n" +
                "    <Brightness>50</Brightness>\r\n" +
                "    <Contrast>50</Contrast>\r\n" +
                "    <Gamma>100</Gamma>\r\n" +
                "    <IsResolutionChangeNeeded>false</IsResolutionChangeNeeded>\r\n" +
                "    <IsExecutableUnconfirmed>false</IsExecutableUnconfirmed>\r\n" +
                "    <HdrIngameLevel>12</HdrIngameLevel>\r\n" +
                "    <SomeFutureFieldThisTypeDoesNotHave>true</SomeFutureFieldThisTypeDoesNotHave>\r\n" +
                "  </ApplicationSetting>\r\n" +
                "</ArrayOfApplicationSetting>\r\n";
        }

        // D1. Mutation this guards: dropping HdrIngameLevel from the serialized/deserialized shape
        // entirely, or wiring it to the wrong XML element.
        private static void CheckSettingsRoundTripsHdrIngameLevel(Checklist checklist)
        {
            string tempIni = NewTempPath(".ini");
            string tempXml = NewTempPath(".xml");
            try
            {
                SettingsController controller = new SettingsController(tempIni, tempXml);
                List<ApplicationSetting> settings = new List<ApplicationSetting>();
                ApplicationSetting setting = new ApplicationSetting("Game", "game.exe", 50, null, false, 50, 50, 100);
                setting.HdrIngameLevel = 12;
                settings.Add(setting);

                bool wrote = controller.SetVibranceSettings("50", "true", "true", "true", settings, "50", "50", "100");

                int vibranceWindowsLevel;
                bool affectPrimaryMonitorOnly;
                bool neverSwitchResolution;
                bool neverChangeColorSettings;
                List<ApplicationSetting> readBack;
                int brightnessWindowsLevel;
                int contrastWindowsLevel;
                int gammaWindowsLevel;
                controller.ReadVibranceSettings(GraphicsAdapter.Nvidia, out vibranceWindowsLevel, out affectPrimaryMonitorOnly,
                    out neverSwitchResolution, out neverChangeColorSettings, out readBack, out brightnessWindowsLevel,
                    out contrastWindowsLevel, out gammaWindowsLevel);

                int gotLevel = readBack != null && readBack.Count == 1 ? readBack[0].HdrIngameLevel : int.MinValue;
                checklist.Check(wrote && readBack != null && readBack.Count == 1 && gotLevel == 12,
                    string.Format("D1: HdrIngameLevel round-trips through SettingsController, got wrote={0} count={1} hdrIngameLevel={2}",
                        wrote, readBack == null ? -1 : readBack.Count, gotLevel));
            }
            finally
            {
                DeleteFileIfExists(tempIni);
                DeleteFileIfExists(tempXml);
            }
        }

        // D2. Mutation this guards: defaulting a missing <HdrIngameLevel> to 0 (a real level)
        // instead of HdrLevelUnset, or having ResolveIngameLevel apply it anyway. Deliberately goes
        // through SettingsController.ReadVibranceSettings itself, not a hand-built XmlSerializer
        // call against a copy of its read path.
        private static void CheckLegacyXmlWithoutHdrIngameLevelDefaultsToUnset(Checklist checklist)
        {
            string tempIni = NewTempPath(".ini");
            string tempXml = NewTempPath(".xml");
            try
            {
                WriteMinimalIni(tempIni);
                File.WriteAllText(tempXml, LegacyApplicationSettingXml());

                SettingsController controller = new SettingsController(tempIni, tempXml);

                int vibranceWindowsLevel;
                bool affectPrimaryMonitorOnly;
                bool neverSwitchResolution;
                bool neverChangeColorSettings;
                List<ApplicationSetting> readBack;
                int brightnessWindowsLevel;
                int contrastWindowsLevel;
                int gammaWindowsLevel;
                controller.ReadVibranceSettings(GraphicsAdapter.Nvidia, out vibranceWindowsLevel, out affectPrimaryMonitorOnly,
                    out neverSwitchResolution, out neverChangeColorSettings, out readBack, out brightnessWindowsLevel,
                    out contrastWindowsLevel, out gammaWindowsLevel);

                bool loaded = readBack != null && readBack.Count == 1;
                int gotLevel = loaded ? readBack[0].HdrIngameLevel : int.MinValue;
                int resolved = loaded ? HdrVibranceHelper.ResolveIngameLevel(readBack[0], HdrDisplayState.Hdr) : int.MinValue;
                int expectedIngameLevel = loaded ? readBack[0].IngameLevel : int.MinValue;

                checklist.Check(loaded && gotLevel == HdrVibranceHelper.HdrLevelUnset && resolved == expectedIngameLevel,
                    string.Format("D2: a legacy XML with no <HdrIngameLevel> element reads back as HdrLevelUnset and resolves to IngameLevel even in HDR, got loaded={0} hdrIngameLevel={1} resolved={2} ingameLevel={3}",
                        loaded, gotLevel, resolved, expectedIngameLevel));
            }
            finally
            {
                DeleteFileIfExists(tempIni);
                DeleteFileIfExists(tempXml);
            }
        }

        // D3. Mutation this guards: a future element breaking deserialization for an older reader -
        // the exact mechanism a v2.6.0 downgrade (reading a v2.7-written file) relies on.
        private static void CheckXmlWithUnknownElementDoesNotThrow(Checklist checklist)
        {
            string tempIni = NewTempPath(".ini");
            string tempXml = NewTempPath(".xml");
            try
            {
                WriteMinimalIni(tempIni);
                File.WriteAllText(tempXml, XmlWithUnknownElement());

                SettingsController controller = new SettingsController(tempIni, tempXml);

                bool threw = false;
                List<ApplicationSetting> readBack = null;
                try
                {
                    int vibranceWindowsLevel;
                    bool affectPrimaryMonitorOnly;
                    bool neverSwitchResolution;
                    bool neverChangeColorSettings;
                    int brightnessWindowsLevel;
                    int contrastWindowsLevel;
                    int gammaWindowsLevel;
                    controller.ReadVibranceSettings(GraphicsAdapter.Nvidia, out vibranceWindowsLevel, out affectPrimaryMonitorOnly,
                        out neverSwitchResolution, out neverChangeColorSettings, out readBack, out brightnessWindowsLevel,
                        out contrastWindowsLevel, out gammaWindowsLevel);
                }
                catch (Exception)
                {
                    threw = true;
                }

                int gotLevel = readBack != null && readBack.Count == 1 ? readBack[0].HdrIngameLevel : int.MinValue;
                checklist.Check(!threw && readBack != null && readBack.Count == 1 && gotLevel == 12,
                    string.Format("D3: an XML element this type does not have is silently ignored, not thrown on, got threw={0} count={1} hdrIngameLevel={2}",
                        threw, readBack == null ? -1 : readBack.Count, gotLevel));
            }
            finally
            {
                DeleteFileIfExists(tempIni);
                DeleteFileIfExists(tempXml);
            }
        }

        // ------------------------------------------------------------------
        // Diagnostics - real hardware. See this class's own header comment for why this fixture,
        // unlike most others, is allowed to touch it.
        // ------------------------------------------------------------------

        private static void RunDiagnosticsChecks(Checklist checklist)
        {
            checklist.Lines.Add("Real hardware diagnostics (this machine's actual QueryDisplayConfig sweep):");

            RealHdrStateReader reader = new RealHdrStateReader();
            List<HdrDisplayInfo> sweep = reader.ReadAll();

            checklist.Skip(string.Format("IsAvailable={0}", reader.IsAvailable));

            if (sweep.Count == 0)
            {
                checklist.Skip("no displays resolved by the real sweep (see IsAvailable above, or QueryDisplayConfig/DisplayConfigGetDeviceInfo failed for every path)");
            }
            else
            {
                // Per-display, not a single instance-wide flag (review nitpick, second pass): type
                // 15 vs type 9 is decided fresh per target now that the latch is gone entirely, so
                // that is what this reports - which type actually answered THIS display, not a
                // process-wide guess.
                foreach (HdrDisplayInfo info in sweep)
                {
                    checklist.Skip(string.Format("display: DeviceName={0} State={1} IsHdrCapable={2} answeredBy=type{3}",
                        info.DeviceName, info.State, info.IsHdrCapable, info.AnsweredByType15 ? "15" : "9"));
                }
            }

            // HdrVibranceHelper.DescribeHdrStatus has no caller yet (PR 2 wires it into the
            // settings UI) - fed the real sweep here purely as a diagnostic, so a reviewer can see
            // what this machine's actual sweep would show a user today.
            checklist.Skip(string.Format("DescribeHdrStatus(real sweep, IsAvailable={0}) => \"{1}\"",
                reader.IsAvailable, HdrVibranceHelper.DescribeHdrStatus(sweep, reader.IsAvailable)));

            RunScreenCrossCheck(checklist, reader, sweep);
            CheckTypeFifteenIsPerTargetIndependent(checklist, reader, sweep);

            checklist.Lines.Add(string.Empty);
        }

        // N1. The one counted hardware check: every Screen.DeviceName this process can see must
        // appear in the real sweep - verifies path enumeration and the source-name -> Screen.
        // DeviceName match against this machine's real topology, HDR display or not. Skipped only
        // when the DisplayConfig API itself is unavailable.
        private static void RunScreenCrossCheck(Checklist checklist, RealHdrStateReader reader, List<HdrDisplayInfo> sweep)
        {
            if (!reader.IsAvailable)
            {
                checklist.Skip("N1: every Screen.DeviceName appears in the real QueryDisplayConfig sweep - the DisplayConfig API is unavailable on this machine/OS version");
                return;
            }

            HashSet<string> sweepNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (HdrDisplayInfo info in sweep)
            {
                sweepNames.Add(info.DeviceName);
            }

            List<string> missing = new List<string>();
            foreach (Screen screen in Screen.AllScreens)
            {
                if (!sweepNames.Contains(screen.DeviceName))
                {
                    missing.Add(screen.DeviceName);
                }
            }

            checklist.Check(missing.Count == 0,
                string.Format("N1: every Screen.DeviceName appears in the real QueryDisplayConfig sweep, missing: {0}",
                    missing.Count == 0 ? "(none)" : string.Join(", ", missing.ToArray())));
        }

        // N2. The property that actually matters now that the type-15 latch is gone entirely
        // (review follow-up): one target's type-15 attempt must never be influenced by what
        // happened for any OTHER target on the same reader instance. Reuses the SAME reader and
        // its already-computed sweep from RunDiagnosticsChecks. Baseline keys on
        // AnsweredByType15 ALONE, not IsHdrCapable && AnsweredByType15 (review follow-up #2): that
        // is the real discriminator for "type 15 answered for this target", needs no HDR-capable
        // panel at all, and gives every display on this machine as a baseline instead of just the
        // one that happens to be HDR-capable - stronger, and un-skips on essentially every Win11
        // box regardless of HDR hardware. Then reflects into the real private TryGetColorInfo to
        // drive one genuinely failing call - a target id no real display owns, so both type 15 and
        // type 9 fail against it through the actual P/Invoke, not a stubbed return - and sweeps
        // again on the SAME instance. A reinstated process-wide latch (of any shape - a bool field,
        // a counter, anything shared across targets) fails this: the interference call's type-15
        // failure would suppress type 15 for every other target too, so every previously-type-15
        // display would fall back to type 9 on the second sweep. Skipped only when this machine has
        // no display type 15 answers for at all.
        private static void CheckTypeFifteenIsPerTargetIndependent(Checklist checklist, RealHdrStateReader reader, List<HdrDisplayInfo> before)
        {
            List<string> answeredByType15Before = new List<string>();
            foreach (HdrDisplayInfo info in before)
            {
                if (info.AnsweredByType15)
                {
                    answeredByType15Before.Add(info.DeviceName);
                }
            }

            if (answeredByType15Before.Count == 0)
            {
                checklist.Skip("N2: type 15's per-target independence - no display on this machine gets a type-15 answer to discriminate from a type-9 fallback");
                return;
            }

            MethodInfo tryGetColorInfo = typeof(RealHdrStateReader).GetMethod("TryGetColorInfo", BindingFlags.NonPublic | BindingFlags.Instance);
            if (tryGetColorInfo == null)
            {
                checklist.Skip("N2: type 15's per-target independence - TryGetColorInfo not found via reflection");
                return;
            }

            // GetMethod alone only guards a RENAME - it returns null and this check skips
            // gracefully. A RESHAPE (a parameter added or removed, exactly what this task's own
            // AnsweredByType15 change just did) still finds the method, and Invoke below would
            // throw TargetParameterCountException straight out of this check and, unhandled, out of
            // Run() itself - crashing the ENTIRE fixture and losing every other check's result, not
            // just this one (review follow-up #1, reproduced against a real signature change).
            // Turning that into one red line naming the actual cause makes the rename and reshape
            // paths symmetric.
            ParameterInfo[] parameters = tryGetColorInfo.GetParameters();
            if (parameters.Length != 5)
            {
                checklist.Check(false, string.Format("N2: TryGetColorInfo now takes {0} parameters, not 5 - update this check", parameters.Length));
                return;
            }

            // Interferes on THIS SAME reader instance - the one "before" was already computed
            // from - and then sweeps it again. A fresh instance would prove nothing about state
            // leaking between targets on one reader.
            DisplayConfigLuid bogusAdapterId = new DisplayConfigLuid();
            bogusAdapterId.LowPart = 999999;
            bogusAdapterId.HighPart = 0;
            object[] callArgs = new object[] { bogusAdapterId, (uint)999999, null, null, null };
            // Return value and out params deliberately unchecked - a real, guaranteed-failing
            // type-15-then-type-9 P/Invoke against a target id no real display owns IS the
            // interference; this check has nothing to assert about its own result.
            tryGetColorInfo.Invoke(reader, callArgs);

            List<HdrDisplayInfo> after = reader.ReadAll();
            HashSet<string> answeredByType15After = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (HdrDisplayInfo info in after)
            {
                if (info.AnsweredByType15)
                {
                    answeredByType15After.Add(info.DeviceName);
                }
            }

            bool stillAnswers = true;
            foreach (string name in answeredByType15Before)
            {
                if (!answeredByType15After.Contains(name))
                {
                    stillAnswers = false;
                    break;
                }
            }

            checklist.Check(stillAnswers,
                string.Format("N2: type 15's per-target independence - a bogus target's real, failing type-15 call must not affect any other target, answeredByType15 before=[{0}] after=[{1}]",
                    string.Join(", ", answeredByType15Before.ToArray()), string.Join(", ", new List<string>(answeredByType15After).ToArray())));
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
            // the convention this follows.
            public void Skip(string description)
            {
                Lines.Add(string.Format("[SKIP] {0}", description));
            }
        }
    }
}
