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
            RunWriteSiteChecks(checklist);
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
        // Write sites - the actual wiring from part 1's pure resolver into every place a vendor
        // proxy writes vibrance (upstream #147, part 2). Real OnWinEventHook/ToggleForegroundProfile/
        // RecheckForegroundHdrLevel, driven exactly like VibranceRestoreFixture/ProfileToggleFixture
        // already drive them - reflection where the method is private, a fake device/adapter always,
        // never a real GPU. HdrStateTracker.ResetForTests(fake) is what stands in for "a real display
        // reporting HDR" here; every check that installs one restores the real reader
        // (HdrStateTracker.ResetForTests(null)) before returning, so RunDiagnosticsChecks below still
        // sees this machine's actual sweep, not a check's leftover fake.
        // ------------------------------------------------------------------

        private static void RunWriteSiteChecks(Checklist checklist)
        {
            checklist.Lines.Add("Write sites use the resolved level, not raw IngameLevel (real OnWinEventHook/ToggleForegroundProfile/RecheckForegroundHdrLevel via reflection + fake devices):");

            CheckNvidiaApplyBranchUsesResolvedLevel(checklist);
            CheckNvidiaApplyBranchUnaffectedWhenNoSeparateLevel(checklist);
            CheckNvidiaToggleUsesResolvedLevel(checklist);
            CheckNvidiaRecheckAppliesOnDetectedTransition(checklist);
            CheckNvidiaRecheckSkipsSuppressedGame(checklist);

            CheckAmdGuardUsesResolvedLevelNotRawIngameLevel(checklist);
            CheckAmdNoSeparateLevelBehavesExactlyAsBefore(checklist);
            CheckAmdApplyBranchWritesResolvedLevel(checklist);
            CheckAmdToggleUsesResolvedLevel(checklist);
            CheckAmdRecheckAppliesOnDetectedTransition(checklist);

            checklist.Lines.Add(string.Empty);
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetDesktopWindow();

        // Installs a fake IHdrStateReader that reports deviceName as state (and, for Hdr, capable)
        // and nothing else - the seam every check below uses to stand in for "this display is
        // currently in HDR" with no real display involved.
        private static void SetHdrState(string deviceName, HdrDisplayState state)
        {
            FakeHdrStateReader fake = new FakeHdrStateReader();
            fake.NextResult = OneDisplay(deviceName, state, state == HdrDisplayState.Hdr);
            HdrStateTracker.ResetForTests(fake);
        }

        // W1. Mutation this guards: OnWinEventHook's apply branch passing
        // applicationSetting.IngameLevel (the raw SDR value) into ApplyGameVibranceLevel instead of
        // the level HdrVibranceHelper.ResolveIngameLevel actually resolved.
        private static void CheckNvidiaApplyBranchUsesResolvedLevel(Checklist checklist)
        {
            FakeNvidiaVibranceDevice device = new FakeNvidiaVibranceDevice();
            ApplicationSetting matchingSetting = new ApplicationSetting();
            matchingSetting.Name = "TestHdrW1";
            matchingSetting.IngameLevel = 60;
            matchingSetting.HdrIngameLevel = 20;
            List<ApplicationSetting> settings = new List<ApplicationSetting> { matchingSetting };

            VibranceInfo vibranceInfo = new VibranceInfo();
            vibranceInfo.neverChangeResolution = true;
            vibranceInfo.neverChangeColorSettings = true;
            NvidiaDynamicVibranceProxy.ResetForTests(device, vibranceInfo, settings);

            IntPtr desktop = GetDesktopWindow();
            string gameDeviceName = Screen.FromHandle(desktop).DeviceName;
            SetHdrState(gameDeviceName, HdrDisplayState.Hdr);

            InvokeNvidiaOnWinEventHook(desktop, "TestHdrW1");

            checklist.Check(device.LevelFor(gameDeviceName) == 20, string.Format(
                "W1 (NVIDIA apply): the apply branch writes HdrIngameLevel (20) while the display is Hdr, not the raw IngameLevel (60), got {0}",
                device.LevelFor(gameDeviceName)));

            HdrStateTracker.ResetForTests(null);
        }

        // W2. A profile with no separate HDR level configured (HdrLevelUnset) must behave EXACTLY
        // as it did before this PR, in BOTH display states - always the raw IngameLevel. Mutation
        // this guards: ResolveIngameLevel (or its wiring here) treating Unset as a real level, or
        // treating Hdr like Sdr's opposite instead of falling back identically in both.
        private static void CheckNvidiaApplyBranchUnaffectedWhenNoSeparateLevel(Checklist checklist)
        {
            foreach (HdrDisplayState state in new[] { HdrDisplayState.Sdr, HdrDisplayState.Hdr })
            {
                FakeNvidiaVibranceDevice device = new FakeNvidiaVibranceDevice();
                ApplicationSetting matchingSetting = new ApplicationSetting();
                matchingSetting.Name = "TestHdrW2";
                matchingSetting.IngameLevel = 45;
                // HdrIngameLevel left at its default (HdrLevelUnset) - no separate level configured.
                List<ApplicationSetting> settings = new List<ApplicationSetting> { matchingSetting };

                VibranceInfo vibranceInfo = new VibranceInfo();
                vibranceInfo.neverChangeResolution = true;
                vibranceInfo.neverChangeColorSettings = true;
                NvidiaDynamicVibranceProxy.ResetForTests(device, vibranceInfo, settings);

                IntPtr desktop = GetDesktopWindow();
                string gameDeviceName = Screen.FromHandle(desktop).DeviceName;
                SetHdrState(gameDeviceName, state);

                InvokeNvidiaOnWinEventHook(desktop, "TestHdrW2");

                checklist.Check(device.LevelFor(gameDeviceName) == 45, string.Format(
                    "W2 (NVIDIA apply, no separate HDR level, state={0}): still writes the raw IngameLevel (45) exactly as before this PR, got {1}",
                    state, device.LevelFor(gameDeviceName)));

                HdrStateTracker.ResetForTests(null);
            }
        }

        // W3. ToggleForegroundProfile's own write (line ~618) must resolve the level too, not just
        // OnWinEventHook's automatic apply branch.
        private static void CheckNvidiaToggleUsesResolvedLevel(Checklist checklist)
        {
            FakeNvidiaVibranceDevice device = new FakeNvidiaVibranceDevice();
            ApplicationSetting matchingSetting = new ApplicationSetting();
            matchingSetting.Name = "TestHdrW3";
            matchingSetting.IngameLevel = 60;
            matchingSetting.HdrIngameLevel = 25;
            List<ApplicationSetting> settings = new List<ApplicationSetting> { matchingSetting };

            VibranceInfo vibranceInfo = new VibranceInfo();
            vibranceInfo.isWindowsLevelKnown = true;
            vibranceInfo.userVibranceSettingDefault = 30;
            NvidiaDynamicVibranceProxy.ResetForTests(device, vibranceInfo, settings);
            ProfileToggleHelper.SetSuppressed("TestHdrW3", true);

            IntPtr desktop = GetDesktopWindow();
            string gameDeviceName = Screen.FromHandle(desktop).DeviceName;
            SetHdrState(gameDeviceName, HdrDisplayState.Hdr);

            ProfileToggleResult result = InvokeNvidiaToggle(desktop, "TestHdrW3", null);

            checklist.Check(result == ProfileToggleResult.ToggledOn && device.LevelFor(gameDeviceName) == 25, string.Format(
                "W3 (NVIDIA toggle): ToggleForegroundProfile's ApplyGameLevel write uses the resolved HDR level (25), not the raw IngameLevel (60), got result={0} level={1}",
                result, device.LevelFor(gameDeviceName)));

            HdrStateTracker.ResetForTests(null);
            ProfileToggleHelper.ResetForTests();
        }

        // W4. RecheckForegroundHdrLevel (the poll timer / DisplaySettingsChanged fast path's call)
        // re-applies against HdrStateTracker's CURRENT reading - proves a Sdr->Hdr transition,
        // detected via the exact same RefreshAndDetectChange() the real poll uses, actually reaches
        // a new write. Mutation this guards: RecheckForegroundHdrLevel resolving against a stale or
        // cached state, or not writing at all.
        private static void CheckNvidiaRecheckAppliesOnDetectedTransition(Checklist checklist)
        {
            FakeNvidiaVibranceDevice device = new FakeNvidiaVibranceDevice();
            ApplicationSetting matchingSetting = new ApplicationSetting();
            matchingSetting.Name = "TestHdrW4";
            matchingSetting.IngameLevel = 60;
            matchingSetting.HdrIngameLevel = 15;
            List<ApplicationSetting> settings = new List<ApplicationSetting> { matchingSetting };

            VibranceInfo vibranceInfo = new VibranceInfo();
            vibranceInfo.isWindowsLevelKnown = true;
            vibranceInfo.userVibranceSettingDefault = 5;
            NvidiaDynamicVibranceProxy.ResetForTests(device, vibranceInfo, settings);

            IntPtr desktop = GetDesktopWindow();
            string gameDeviceName = Screen.FromHandle(desktop).DeviceName;

            FakeHdrStateReader fake = new FakeHdrStateReader();
            fake.NextResult = OneDisplay(gameDeviceName, HdrDisplayState.Sdr, true);
            HdrStateTracker.ResetForTests(fake);
            HdrStateTracker.RefreshAndDetectChange();

            IVibranceProxy proxy = (IVibranceProxy)NewNvidiaInstance();
            proxy.RecheckForegroundHdrLevel(desktop, "TestHdrW4", null);
            int levelWhileSdr = device.LevelFor(gameDeviceName);

            // The transition RecheckForegroundHdrLevel is meant to notice - same
            // RefreshAndDetectChange() the real poll timer/fast path calls before ever reaching
            // RecheckForegroundHdrLevel (see VibranceGUI.OnHdrRecheckTick).
            fake.NextResult = OneDisplay(gameDeviceName, HdrDisplayState.Hdr, true);
            bool changed = HdrStateTracker.RefreshAndDetectChange();

            proxy.RecheckForegroundHdrLevel(desktop, "TestHdrW4", null);
            int levelWhileHdr = device.LevelFor(gameDeviceName);

            checklist.Check(changed && levelWhileSdr == 60 && levelWhileHdr == 15, string.Format(
                "W4 (NVIDIA recheck): a detected Sdr->Hdr transition re-applies from IngameLevel (60) to HdrIngameLevel (15), got changed={0} levelWhileSdr={1} levelWhileHdr={2}",
                changed, levelWhileSdr, levelWhileHdr));

            HdrStateTracker.ResetForTests(null);
        }

        // W5. A suppressed profile (toggled off by hotkey) owes RecheckForegroundHdrLevel nothing -
        // mirrors OnWinEventHook's own suppression gate. Mutation this guards: re-applying (and so
        // silently undoing the toggle hotkey's own effect) for a game the user explicitly forced to
        // the Windows level.
        private static void CheckNvidiaRecheckSkipsSuppressedGame(Checklist checklist)
        {
            FakeNvidiaVibranceDevice device = new FakeNvidiaVibranceDevice();
            ApplicationSetting matchingSetting = new ApplicationSetting();
            matchingSetting.Name = "TestHdrW5";
            matchingSetting.IngameLevel = 60;
            matchingSetting.HdrIngameLevel = 15;
            List<ApplicationSetting> settings = new List<ApplicationSetting> { matchingSetting };

            VibranceInfo vibranceInfo = new VibranceInfo();
            vibranceInfo.isWindowsLevelKnown = true;
            vibranceInfo.userVibranceSettingDefault = 5;
            NvidiaDynamicVibranceProxy.ResetForTests(device, vibranceInfo, settings);
            ProfileToggleHelper.SetSuppressed("TestHdrW5", true);

            IntPtr desktop = GetDesktopWindow();
            string gameDeviceName = Screen.FromHandle(desktop).DeviceName;
            SetHdrState(gameDeviceName, HdrDisplayState.Hdr);

            IVibranceProxy proxy = (IVibranceProxy)NewNvidiaInstance();
            proxy.RecheckForegroundHdrLevel(desktop, "TestHdrW5", null);

            checklist.Check(device.LevelFor(gameDeviceName) == int.MinValue,
                string.Format("W5 (NVIDIA recheck, suppressed): a suppressed profile is never re-applied, got level={0}", device.LevelFor(gameDeviceName)));

            HdrStateTracker.ResetForTests(null);
            ProfileToggleHelper.ResetForTests();
        }

        // A1 (the trap). The AMD guard at the apply branch's own line - "if
        // (_vibranceInfo.userVibranceSettingDefault != resolvedLevel)" - has to compare against the
        // RESOLVED level, not applicationSetting.IngameLevel. This is #147 part 2's most natural
        // configuration: Windows level 100, IngameLevel 100 (so the SDR guard would read
        // "100 != 100" == false and skip), HdrIngameLevel 40, display Hdr. A write-only fix that
        // leaves the guard comparing IngameLevel would still skip here and this check would see
        // int.MinValue (never written) instead of 40 - this is the check the team's own review
        // called out as the one thing most likely to go wrong.
        private static void CheckAmdGuardUsesResolvedLevelNotRawIngameLevel(Checklist checklist)
        {
            FakeAmdAdapter adapter = new FakeAmdAdapter();
            ApplicationSetting matchingSetting = new ApplicationSetting();
            matchingSetting.Name = "TestHdrA1";
            matchingSetting.IngameLevel = 100;
            matchingSetting.HdrIngameLevel = 40;
            List<ApplicationSetting> settings = new List<ApplicationSetting> { matchingSetting };

            AmdDynamicVibranceProxy proxy = BuildAmdProxy(adapter, settings);
            proxy.SetAffectPrimaryMonitorOnly(true);
            proxy.SetVibranceWindowsLevel(100);

            IntPtr desktop = GetDesktopWindow();
            string gameDeviceName = Screen.FromHandle(desktop).DeviceName;
            SetHdrState(gameDeviceName, HdrDisplayState.Hdr);

            InvokeAmdOnWinEventHook(proxy, desktop, "TestHdrA1");

            int writtenLevel = adapter.SetSaturationOnDisplayLevels.Count > 0 ? adapter.SetSaturationOnDisplayLevels[adapter.SetSaturationOnDisplayLevels.Count - 1] : int.MinValue;
            checklist.Check(adapter.SetSaturationOnDisplayLevels.Count == 1 && writtenLevel == 40, string.Format(
                "A1 (AMD guard trap): Windows==IngameLevel==100, HdrIngameLevel=40, display Hdr - the guard must compare against the RESOLVED level (40 != 100) and write it, not skip on \"100 != 100\", got writeCount={0} lastLevel={1}",
                adapter.SetSaturationOnDisplayLevels.Count, writtenLevel));

            HdrStateTracker.ResetForTests(null);
        }

        // A2. The guard's ORIGINAL purpose - "don't rewrite the game level onto its own Windows
        // default" - must still hold for a profile with no separate HDR level configured, in both
        // display states. Mutation this guards: losing the skip-when-equal behaviour entirely while
        // fixing A1 above (e.g. always writing regardless of the guard).
        private static void CheckAmdNoSeparateLevelBehavesExactlyAsBefore(Checklist checklist)
        {
            foreach (HdrDisplayState state in new[] { HdrDisplayState.Sdr, HdrDisplayState.Hdr })
            {
                FakeAmdAdapter adapter = new FakeAmdAdapter();
                ApplicationSetting matchingSetting = new ApplicationSetting();
                matchingSetting.Name = "TestHdrA2";
                matchingSetting.IngameLevel = 100;
                // HdrIngameLevel left at HdrLevelUnset - no separate level configured.
                List<ApplicationSetting> settings = new List<ApplicationSetting> { matchingSetting };

                AmdDynamicVibranceProxy proxy = BuildAmdProxy(adapter, settings);
                proxy.SetAffectPrimaryMonitorOnly(true);
                proxy.SetVibranceWindowsLevel(100);

                IntPtr desktop = GetDesktopWindow();
                string gameDeviceName = Screen.FromHandle(desktop).DeviceName;
                SetHdrState(gameDeviceName, state);

                InvokeAmdOnWinEventHook(proxy, desktop, "TestHdrA2");

                checklist.Check(adapter.SetSaturationOnDisplayLevels.Count == 0, string.Format(
                    "A2 (AMD guard, no separate HDR level, state={0}): resolves to IngameLevel (100), equal to the Windows level (100), so the pre-existing skip-when-equal guard still makes zero writes exactly as before, got {1}",
                    state, adapter.SetSaturationOnDisplayLevels.Count));

                HdrStateTracker.ResetForTests(null);
            }
        }

        // A3. The write itself uses the resolved level, independent of A1's guard-equality trap -
        // Windows/IngameLevel/HdrIngameLevel all distinct here, so this fails on its own if the
        // write site is reverted to applicationSetting.IngameLevel even where the guard happens to
        // still pass.
        private static void CheckAmdApplyBranchWritesResolvedLevel(Checklist checklist)
        {
            FakeAmdAdapter adapter = new FakeAmdAdapter();
            ApplicationSetting matchingSetting = new ApplicationSetting();
            matchingSetting.Name = "TestHdrA3";
            matchingSetting.IngameLevel = 80;
            matchingSetting.HdrIngameLevel = 40;
            List<ApplicationSetting> settings = new List<ApplicationSetting> { matchingSetting };

            AmdDynamicVibranceProxy proxy = BuildAmdProxy(adapter, settings);
            proxy.SetAffectPrimaryMonitorOnly(true);
            proxy.SetVibranceWindowsLevel(10);

            IntPtr desktop = GetDesktopWindow();
            string gameDeviceName = Screen.FromHandle(desktop).DeviceName;
            SetHdrState(gameDeviceName, HdrDisplayState.Hdr);

            InvokeAmdOnWinEventHook(proxy, desktop, "TestHdrA3");

            int writtenLevel = adapter.SetSaturationOnDisplayLevels.Count > 0 ? adapter.SetSaturationOnDisplayLevels[0] : int.MinValue;
            checklist.Check(writtenLevel == 40, string.Format(
                "A3 (AMD apply): writes the resolved HdrIngameLevel (40), not the raw IngameLevel (80), got {0}", writtenLevel));

            HdrStateTracker.ResetForTests(null);
        }

        // A4. ToggleForegroundProfile's own write must resolve the level too, not just
        // OnWinEventHook's automatic apply branch - checked against BOTH affectPrimaryMonitorOnly
        // branches, since AMD's toggle (unlike NVIDIA's) genuinely differs between them.
        private static void CheckAmdToggleUsesResolvedLevel(Checklist checklist)
        {
            foreach (bool affectPrimaryMonitorOnly in new[] { true, false })
            {
                ProfileToggleHelper.ResetForTests();
                FakeAmdAdapter adapter = new FakeAmdAdapter();
                ApplicationSetting matchingSetting = new ApplicationSetting();
                matchingSetting.Name = "TestHdrA4";
                matchingSetting.IngameLevel = 80;
                matchingSetting.HdrIngameLevel = 35;
                List<ApplicationSetting> settings = new List<ApplicationSetting> { matchingSetting };

                AmdDynamicVibranceProxy proxy = BuildAmdProxy(adapter, settings);
                proxy.SetAffectPrimaryMonitorOnly(affectPrimaryMonitorOnly);
                proxy.SetVibranceWindowsLevel(10);
                ProfileToggleHelper.SetSuppressed("TestHdrA4", true);

                IntPtr desktop = GetDesktopWindow();
                string gameDeviceName = Screen.FromHandle(desktop).DeviceName;
                SetHdrState(gameDeviceName, HdrDisplayState.Hdr);

                ProfileToggleResult result = proxy.ToggleForegroundProfile(desktop, "TestHdrA4", null);

                int writtenLevel = adapter.SetSaturationOnDisplayLevels.Count > 0 ? adapter.SetSaturationOnDisplayLevels[0] : int.MinValue;
                checklist.Check(result == ProfileToggleResult.ToggledOn && writtenLevel == 35, string.Format(
                    "A4 (AMD toggle, affectPrimaryMonitorOnly={0}): writes the resolved HdrIngameLevel (35), not the raw IngameLevel (80), got result={1} level={2}",
                    affectPrimaryMonitorOnly, result, writtenLevel));

                HdrStateTracker.ResetForTests(null);
                ProfileToggleHelper.ResetForTests();
            }
        }

        // A5. RecheckForegroundHdrLevel's AMD side - same property as W4, against
        // ApplyResolvedGameLevel instead of ApplyGameVibranceLevel.
        private static void CheckAmdRecheckAppliesOnDetectedTransition(Checklist checklist)
        {
            FakeAmdAdapter adapter = new FakeAmdAdapter();
            ApplicationSetting matchingSetting = new ApplicationSetting();
            matchingSetting.Name = "TestHdrA5";
            matchingSetting.IngameLevel = 80;
            matchingSetting.HdrIngameLevel = 25;
            List<ApplicationSetting> settings = new List<ApplicationSetting> { matchingSetting };

            AmdDynamicVibranceProxy proxy = BuildAmdProxy(adapter, settings);
            proxy.SetAffectPrimaryMonitorOnly(true);
            proxy.SetVibranceWindowsLevel(10);

            IntPtr desktop = GetDesktopWindow();
            string gameDeviceName = Screen.FromHandle(desktop).DeviceName;

            FakeHdrStateReader fake = new FakeHdrStateReader();
            fake.NextResult = OneDisplay(gameDeviceName, HdrDisplayState.Sdr, true);
            HdrStateTracker.ResetForTests(fake);
            HdrStateTracker.RefreshAndDetectChange();

            proxy.RecheckForegroundHdrLevel(desktop, "TestHdrA5", null);
            int writesWhileSdr = adapter.SetSaturationOnDisplayLevels.Count;
            int levelWhileSdr = writesWhileSdr > 0 ? adapter.SetSaturationOnDisplayLevels[writesWhileSdr - 1] : int.MinValue;

            fake.NextResult = OneDisplay(gameDeviceName, HdrDisplayState.Hdr, true);
            bool changed = HdrStateTracker.RefreshAndDetectChange();

            proxy.RecheckForegroundHdrLevel(desktop, "TestHdrA5", null);
            int levelWhileHdr = adapter.SetSaturationOnDisplayLevels.Count > 0
                ? adapter.SetSaturationOnDisplayLevels[adapter.SetSaturationOnDisplayLevels.Count - 1] : int.MinValue;

            checklist.Check(changed && levelWhileSdr == 80 && levelWhileHdr == 25, string.Format(
                "A5 (AMD recheck): a detected Sdr->Hdr transition re-applies from IngameLevel (80) to HdrIngameLevel (25), got changed={0} levelWhileSdr={1} levelWhileHdr={2}",
                changed, levelWhileSdr, levelWhileHdr));

            HdrStateTracker.ResetForTests(null);
        }

        private static object NewNvidiaInstance()
        {
            return FormatterServices.GetUninitializedObject(typeof(NvidiaDynamicVibranceProxy));
        }

        private static void InvokeNvidiaOnWinEventHook(IntPtr handle, string processName)
        {
            MethodInfo onWinEventHook = typeof(NvidiaDynamicVibranceProxy).GetMethod(
                "OnWinEventHook", BindingFlags.NonPublic | BindingFlags.Static);
            WinEventHookEventArgs args = new WinEventHookEventArgs
            {
                Handle = handle,
                ProcessName = processName,
                ProcessImagePath = null
            };
            onWinEventHook.Invoke(null, new object[] { null, args });
        }

        private static ProfileToggleResult InvokeNvidiaToggle(IntPtr hWnd, string processName, string processImagePath)
        {
            MethodInfo m = typeof(NvidiaDynamicVibranceProxy).GetMethod("ToggleForegroundProfile");
            return (ProfileToggleResult)m.Invoke(NewNvidiaInstance(), new object[] { hWnd, processName, processImagePath });
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

        private static void InvokeAmdOnWinEventHook(AmdDynamicVibranceProxy proxy, IntPtr handle, string processName)
        {
            MethodInfo onWinEventHook = typeof(AmdDynamicVibranceProxy).GetMethod(
                "OnWinEventHook", BindingFlags.NonPublic | BindingFlags.Instance);
            WinEventHookEventArgs args = new WinEventHookEventArgs
            {
                Handle = handle,
                ProcessName = processName,
                ProcessImagePath = null
            };
            onWinEventHook.Invoke(proxy, new object[] { null, args });
        }

        // Mirrors VibranceRestoreFixture.FakeNvidiaVibranceDevice - records every (deviceName ->
        // handle) resolution and every (handle -> level) write, so the real apply/toggle/recheck
        // code is driven for real against it, never reimplemented here. A separate copy rather than
        // a shared one: that class's own copy is private to VibranceRestoreFixture, and this one
        // adds LevelFor (below) purely as a convenience for reading back what a device believes its
        // level is, which none of that fixture's own checks needed.
        private class FakeNvidiaVibranceDevice : INvidiaVibranceDevice
        {
            private readonly Dictionary<string, int> _handlesByDeviceName = new Dictionary<string, int>();
            private readonly Dictionary<int, int> _levelsByHandle = new Dictionary<int, int>();
            private int _nextHandle = 1;

            public int HandleFor(string deviceName)
            {
                return ResolveOrAssign(deviceName);
            }

            // The level this fake currently believes deviceName's display is at, or int.MinValue
            // if nothing has ever been written to it.
            public int LevelFor(string deviceName)
            {
                int level;
                return _levelsByHandle.TryGetValue(ResolveOrAssign(deviceName), out level) ? level : int.MinValue;
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
                return true;
            }

            public int TryResolveDisplayHandle(string deviceName)
            {
                if (string.IsNullOrEmpty(deviceName))
                {
                    return -1;
                }
                return ResolveOrAssign(deviceName);
            }

            public bool IsAtLevel(int displayHandle, int level)
            {
                int current;
                return _levelsByHandle.TryGetValue(displayHandle, out current) && current == level;
            }

            public bool SetLevel(int displayHandle, int level)
            {
                _levelsByHandle[displayHandle] = level;
                return true;
            }
        }

        // Everything IAmdAdapter exposes, none of it touching real hardware - mirrors
        // VibranceRestoreFixture.FakeAmdAdapter/StabilityFixture.FakeAmdAdapter, trimmed to what
        // this file's own checks need (a per-call level/name log, no all-displays call log since
        // nothing here exercises the affectPrimaryMonitorOnly==false apply branch's own write).
        private class FakeAmdAdapter : IAmdAdapter
        {
            public int SetSaturationOnAllDisplaysCallCount;

            public readonly List<int> SetSaturationOnDisplayLevels = new List<int>();
            public readonly List<string> SetSaturationOnDisplayNames = new List<string>();

            public void SetSaturationOnAllDisplays(int vibranceLevel)
            {
                SetSaturationOnAllDisplaysCallCount++;
            }

            public bool SetSaturationOnDisplay(int vibranceLevel, string displayName)
            {
                SetSaturationOnDisplayLevels.Add(vibranceLevel);
                SetSaturationOnDisplayNames.Add(displayName);
                return true;
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
