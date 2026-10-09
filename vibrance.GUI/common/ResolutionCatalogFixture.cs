using System;
using System.Collections.Generic;
using System.IO;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using System.Xml.Serialization;

namespace vibrance.GUI.common
{
    /// <summary>
    /// Regression coverage for the resolution picker model (ResolutionCatalog, ResolutionPicker,
    /// ResolutionStarPreferences) and the two star keys in SettingsController. Fakes only: the mode
    /// lists are built in code and the INI is a temp file, never the user's real one. No hardware,
    /// no display change. The dialog checks build a real VibranceSettings form but never show it.
    /// Run by vibrance.GUI.exe --selftest-resolution-picker.
    /// </summary>
    public static class ResolutionCatalogFixture
    {
        public static List<string> Run()
        {
            Checklist checklist = new Checklist();
            checklist.Lines.Add("vibranceGUI resolution picker self test");
            checklist.Lines.Add(string.Empty);

            CheckGrouping(checklist);
            CheckHighestRefreshRate(checklist);
            CheckDefaultStars(checklist);
            CheckStarPersistence(checklist);
            CheckRoundTrip(checklist);
            CheckDialog(checklist);
            CheckEdgeCases(checklist);

            checklist.Lines.Add(string.Empty);
            checklist.Lines.Add(string.Format("PASSED {0}/{1}", checklist.Passed, checklist.Total));
            return checklist.Lines;
        }

        // ---- fake displays -------------------------------------------------------------------

        private static ResolutionModeWrapper Mode(uint w, uint h, uint bpp, uint hz, uint fo)
        {
            ResolutionModeWrapper mode = new ResolutionModeWrapper();
            mode.DmPelsWidth = w;
            mode.DmPelsHeight = h;
            mode.DmBitsPerPel = bpp;
            mode.DmDisplayFrequency = hz;
            mode.DmDisplayFixedOutput = fo;
            return mode;
        }

        private static ResolutionSize Size(uint w, uint h)
        {
            return new ResolutionSize(w, h);
        }

        private static void AddSize(List<ResolutionModeWrapper> list, uint w, uint h, uint[] hzs, uint[] fos)
        {
            foreach (uint hz in hzs)
            {
                foreach (uint fo in fos)
                {
                    list.Add(Mode(w, h, 32, hz, fo));
                }
            }
        }

        // NVIDIA-style list: every size x several rates x fixedOutput {0,1,2}, 32 bpp, plus a few
        // 16 bpp duplicates and one fixedOutput=3 mode. Reversed at the end so nothing depends on
        // the order the driver happens to enumerate in.
        private static List<ResolutionModeWrapper> BuildDisplayA()
        {
            uint[] all = new uint[] { 0, 1, 2 };
            List<ResolutionModeWrapper> list = new List<ResolutionModeWrapper>();
            AddSize(list, 3840, 2160, new uint[] { 60 }, all);
            AddSize(list, 2560, 1440, new uint[] { 240, 144, 60 }, all);
            AddSize(list, 1920, 1080, new uint[] { 240, 144, 60 }, all);
            AddSize(list, 1680, 1050, new uint[] { 60 }, new uint[] { 1, 2 });
            AddSize(list, 1440, 1080, new uint[] { 120, 60 }, all);
            AddSize(list, 1366, 768, new uint[] { 1 }, new uint[] { 0 });
            AddSize(list, 1280, 960, new uint[] { 60 }, all);
            AddSize(list, 1280, 800, new uint[] { 60 }, new uint[] { 0 });
            AddSize(list, 1280, 720, new uint[] { 120, 60 }, all);
            AddSize(list, 1024, 768, new uint[] { 75, 60 }, new uint[] { 0 });
            AddSize(list, 800, 600, new uint[] { 0, 1, 60 }, new uint[] { 0 });
            AddSize(list, 720, 400, new uint[] { 0 }, new uint[] { 0 });
            AddSize(list, 640, 480, new uint[] { 1, 0 }, new uint[] { 0 });
            list.Add(Mode(1920, 1080, 16, 60, 0));
            list.Add(Mode(1920, 1080, 16, 60, 2));
            list.Add(Mode(1280, 720, 16, 60, 0));
            list.Add(Mode(1920, 1080, 32, 144, 3));
            list.Reverse();
            return list;
        }

        // A 21:9 panel that offers neither 1440x1080 nor 1280x960.
        private static List<ResolutionModeWrapper> BuildDisplayB()
        {
            List<ResolutionModeWrapper> list = new List<ResolutionModeWrapper>();
            AddSize(list, 3440, 1440, new uint[] { 144, 60 }, new uint[] { 0, 2 });
            AddSize(list, 2560, 1440, new uint[] { 60 }, new uint[] { 0 });
            AddSize(list, 2560, 1080, new uint[] { 60 }, new uint[] { 0 });
            AddSize(list, 1920, 1080, new uint[] { 60 }, new uint[] { 0 });
            AddSize(list, 1280, 720, new uint[] { 60 }, new uint[] { 0 });
            return list;
        }

        private static ResolutionModeWrapper WindowsA()
        {
            return Mode(1920, 1080, 32, 240, 0);
        }

        private static ResolutionModeWrapper WindowsB()
        {
            return Mode(3440, 1440, 32, 144, 0);
        }

        private static ResolutionCatalog CatalogA(ResolutionStarPreferences stars)
        {
            return ResolutionCatalog.Build(BuildDisplayA(), WindowsA(), stars);
        }

        private static string Describe(List<ResolutionEntry> entries)
        {
            return string.Join("|", entries.Select(e => e.IsSeparator ? "---" : e.Size.ToToken()).ToArray());
        }

        private static string Join(IEnumerable<uint> values)
        {
            return string.Join(",", values.Select(v => v.ToString()).ToArray());
        }

        // ---- grouping ------------------------------------------------------------------------

        private static void CheckGrouping(Checklist c)
        {
            c.Lines.Add("Grouping and ordering");

            List<ResolutionModeWrapper> modes = BuildDisplayA();
            ResolutionCatalog catalog = CatalogA(null);
            List<ResolutionEntry> entries = catalog.BuildEntries();

            HashSet<string> distinct = new HashSet<string>(modes.Select(m => m.DmPelsWidth + "x" + m.DmPelsHeight));
            List<ResolutionEntry> sizeEntries = entries.Where(e => !e.IsSeparator).ToList();
            c.Check(sizeEntries.Count == distinct.Count && catalog.Sizes.Count == distinct.Count,
                "one entry per distinct size (" + distinct.Count + ")");
            c.Check(sizeEntries.Select(e => e.Size.ToToken()).Distinct().Count() == sizeEntries.Count,
                "no size appears twice");
            c.Check(distinct.SetEquals(sizeEntries.Select(e => e.Size.ToToken())), "every offered size appears");

            c.Check(entries.Count(e => e.IsSeparator) == 1, "exactly one separator when both sections exist");
            c.Check(Describe(entries) == "1920x1080|1680x1050|1440x1080|1280x960|1280x800|1280x720|1024x768|---|3840x2160|2560x1440|1366x768|800x600|720x400|640x480",
                "order: native, starred by width/height desc, separator, rest by width/height desc; got " + Describe(entries));
            int sep = entries.FindIndex(e => e.IsSeparator);
            c.Check(entries.Take(sep).All(e => e.IsStarred) && entries.Skip(sep + 1).All(e => !e.IsStarred),
                "everything before the separator is starred, nothing after it is");
            c.Check(entries[0].IsNative && entries[0].Size.Equals(Size(1920, 1080)), "native is first and flagged");
            c.Check(entries[sep].Size == null && entries[sep].ToString() == "----------------", "separator has no size and renders as dashes");

            // Only starred sizes exist: no separator.
            List<ResolutionModeWrapper> small = new List<ResolutionModeWrapper>();
            AddSize(small, 1920, 1080, new uint[] { 60 }, new uint[] { 0 });
            AddSize(small, 1280, 720, new uint[] { 60 }, new uint[] { 0 });
            c.Check(!ResolutionCatalog.Build(small, Mode(1920, 1080, 32, 60, 0), null).BuildEntries().Any(e => e.IsSeparator),
                "no separator when every size is starred");
            List<ResolutionModeWrapper> one = new List<ResolutionModeWrapper>();
            AddSize(one, 2560, 1440, new uint[] { 60 }, new uint[] { 0 });
            List<ResolutionEntry> oneEntries = ResolutionCatalog.Build(one, null, null).BuildEntries();
            c.Check(oneEntries.Count == 1 && oneEntries[0].IsNative && !oneEntries.Any(e => e.IsSeparator),
                "a single size is native with no separator");
            c.Check(ResolutionCatalog.Build(new List<ResolutionModeWrapper>(), null, null).BuildEntries().Count == 0,
                "an empty mode list gives no entries (no separator either)");
            c.Check(ResolutionCatalog.Build(null, null, null).Native == null, "a null mode list is tolerated");

            // Starred section empty except native, rest unstarred: one separator.
            ResolutionStarPreferences unstarAll = new ResolutionStarPreferences();
            foreach (ResolutionSize s in ResolutionCatalog.DefaultStarCandidates)
            {
                unstarAll.Removed.Add(s);
            }
            List<ResolutionEntry> onlyNative = CatalogA(unstarAll).BuildEntries();
            c.Check(onlyNative[0].IsNative && onlyNative[1].IsSeparator && onlyNative.Count(e => e.IsSeparator) == 1 && onlyNative.Count(e => e.IsStarred) == 1,
                "with every default unstarred only the native entry is starred, then one separator");

            // Native selection.
            c.Check(ResolutionCatalog.DetermineNative(Mode(1920, 1080, 32, 60, 0), modes).Equals(Size(1920, 1080)), "native is the Windows mode's size when offered");
            c.Check(ResolutionCatalog.DetermineNative(Mode(1111, 555, 32, 60, 0), modes).Equals(Size(3840, 2160)), "native falls back to the largest-area size");
            c.Check(ResolutionCatalog.DetermineNative(null, modes).Equals(Size(3840, 2160)), "native with no Windows mode is the largest-area size");
            c.Check(ResolutionCatalog.DetermineNative(Mode(1920, 1080, 32, 60, 0), new List<ResolutionModeWrapper>()) == null, "native of an empty list is null");

            // Aspect labels.
            c.Check(ResolutionCatalog.DescribeAspect(Size(1366, 768)) == "16:9", "1366x768 is 16:9");
            c.Check(ResolutionCatalog.DescribeAspect(Size(2560, 1080)) == "21:9", "2560x1080 is 21:9");
            c.Check(ResolutionCatalog.DescribeAspect(Size(3440, 1440)) == "21:9", "3440x1440 is 21:9");
            c.Check(ResolutionCatalog.DescribeAspect(Size(1280, 800)) == "16:10", "1280x800 is 16:10");
            c.Check(ResolutionCatalog.DescribeAspect(Size(1280, 960)) == "4:3", "1280x960 is 4:3");
            c.Check(ResolutionCatalog.DescribeAspect(Size(1280, 1024)) == "5:4", "1280x1024 is 5:4");
            c.Check(ResolutionCatalog.DescribeAspect(Size(1920, 1280)) == "3:2", "1920x1280 is 3:2");
            c.Check(ResolutionCatalog.DescribeAspect(Size(3840, 1080)) == "32:9", "3840x1080 is 32:9");
            c.Check(ResolutionCatalog.DescribeAspect(Size(1280, 768)) == null, "1280x768 is outside the 3% tolerance of every ratio");
            c.Check(ResolutionCatalog.DescribeAspect(Size(400, 1000)) == null, "a portrait size has no label");

            // Texts.
            c.Check(entries[0].ToString() == "\u2605 1920 x 1080 (16:9, native)", "native text: " + entries[0]);
            c.Check(entries[3].ToString() == "\u2605 1280 x 960 (4:3)", "starred text: " + entries[3]);
            c.Check(entries.First(e => !e.IsSeparator && !e.IsStarred && e.Size.Equals(Size(2560, 1440))).ToString() == "2560 x 1440 (16:9)",
                "unstarred text has no star");
            c.Check(ResolutionCatalog.DescribeScaling(0) == "Default" && ResolutionCatalog.DescribeScaling(1) == "Stretch" &&
                ResolutionCatalog.DescribeScaling(2) == "Center" && ResolutionCatalog.DescribeScaling(3) == "Unknown (3)",
                "scaling labels");
            c.Check(ResolutionCatalog.DescribeRefreshRate(240) == "240 Hz" && ResolutionCatalog.DescribeRefreshRate(0) == "Default rate" &&
                ResolutionCatalog.DescribeRefreshRate(1) == "Default rate", "refresh rate labels");
        }

        // ---- refresh rate and scaling selection ------------------------------------------------

        private static ResolutionEntry EntryFor(ResolutionPicker picker, uint w, uint h)
        {
            return picker.Entries.First(e => !e.IsSeparator && !e.IsUnavailable && e.Size.Equals(Size(w, h)));
        }

        private static void CheckHighestRefreshRate(Checklist c)
        {
            c.Lines.Add(string.Empty);
            c.Lines.Add("Highest refresh rate, size change, scaling fallback");

            ResolutionCatalog catalog = CatalogA(null);
            c.Check(Join(catalog.GetRefreshRates(Size(1920, 1080))) == "240,144,60", "1920x1080 rates descend: " + Join(catalog.GetRefreshRates(Size(1920, 1080))));
            c.Check(catalog.GetHighestRefreshRate(Size(1920, 1080)) == 240, "highest for 1920x1080 is 240");
            c.Check(Join(catalog.GetRefreshRates(Size(800, 600))) == "60,1,0", "0 and 1 sort last: " + Join(catalog.GetRefreshRates(Size(800, 600))));
            c.Check(catalog.GetHighestRefreshRate(Size(800, 600)) == 60, "0/1 lose to a real rate");
            c.Check(catalog.GetHighestRefreshRate(Size(1366, 768)) == 1, "a lone 1 is the highest");
            c.Check(catalog.GetHighestRefreshRate(Size(720, 400)) == 0, "a lone 0 is the highest");
            c.Check(Join(catalog.GetRefreshRates(Size(640, 480))) == "1,0", "only 0 and 1: 1 first then 0");
            c.Check(catalog.GetHighestRefreshRate(Size(9999, 9999)) == 0 && catalog.GetRefreshRates(Size(9999, 9999)).Count == 0, "unknown size has no rates");

            c.Check(Join(catalog.GetScalings(Size(1920, 1080), 240)) == "0,2,1", "scalings ordered Default, Center, Stretch: " + Join(catalog.GetScalings(Size(1920, 1080), 240)));
            c.Check(Join(catalog.GetScalings(Size(1920, 1080), 144)) == "0,2,1,3", "unknown scaling 3 sorts last: " + Join(catalog.GetScalings(Size(1920, 1080), 144)));
            c.Check(Join(catalog.GetScalings(Size(1680, 1050), 60)) == "2,1", "a size without Default offers only what it has");

            ResolutionPicker picker = new ResolutionPicker(catalog);
            bool allHighest = true;
            foreach (ResolutionEntry entry in picker.Entries.Where(e => !e.IsSeparator).ToList())
            {
                // Move away first so the "same size" short cut does not hide a failure.
                picker.SelectEntry(EntryFor(picker, 640, 480));
                picker.SelectEntry(entry);
                if (picker.SelectedRefreshRate != catalog.GetHighestRefreshRate(entry.Size))
                {
                    allHighest = false;
                }
            }
            c.Check(allHighest, "SelectEntry picks the highest rate for every size");

            c.Check(!picker.SelectEntry(null), "SelectEntry(null) is refused");
            c.Check(!picker.SelectEntry(picker.Entries.First(e => e.IsSeparator)), "SelectEntry(separator) is refused");

            // Size change resets Hz.
            picker.SelectDefault();
            picker.SelectRefreshRate(60);
            c.Check(picker.SelectedRefreshRate == 60, "rate 60 selected on 1920x1080");
            picker.SelectEntry(EntryFor(picker, 1280, 720));
            c.Check(picker.SelectedRefreshRate == 120, "size change resets to that size's highest (120)");
            picker.SelectRefreshRate(75);
            c.Check(picker.SelectedRefreshRate == 120, "a rate the size does not offer is ignored");

            // Scaling kept across a size change when offered.
            picker.SelectDefault();
            picker.SelectScaling(1);
            picker.SelectEntry(EntryFor(picker, 1280, 720));
            c.Check(picker.SelectedScaling == 1, "Stretch is kept on a size that offers it");
            // Falls back to Default.
            picker.SelectEntry(EntryFor(picker, 1024, 768));
            c.Check(picker.SelectedScaling == 0, "falls back to Default when the new size lacks Stretch");
            // Falls back to the first when Default is not offered either.
            picker.SelectEntry(EntryFor(picker, 1680, 1050));
            c.Check(picker.SelectedScaling == 2, "falls back to the first offered (Center) when Default is absent");
            picker.SelectScaling(0);
            c.Check(picker.SelectedScaling == 2, "a scaling the size does not offer is ignored");

            // Hz change: scaling kept or falls back.
            picker.SelectDefault();
            picker.SelectRefreshRate(144);
            picker.SelectScaling(3);
            c.Check(picker.SelectedScaling == 3, "unknown scaling 3 selectable at 144 Hz");
            picker.SelectRefreshRate(240);
            c.Check(picker.SelectedRefreshRate == 240 && picker.SelectedScaling == 0, "rate change falls back to Default when scaling 3 is not offered at 240 Hz");
            picker.SelectScaling(1);
            picker.SelectRefreshRate(144);
            c.Check(picker.SelectedScaling == 1, "rate change keeps Stretch when offered at the new rate");
            c.Check(picker.GetSelectedMode().DmDisplayFrequency == 144 && picker.GetSelectedMode().DmDisplayFixedOutput == 1, "selected mode follows rate and scaling");

            // Choice lists.
            picker.SelectDefault();
            c.Check(string.Join(",", picker.RefreshRateChoices.Select(x => x.ToString()).ToArray()) == "240 Hz,144 Hz,60 Hz", "rate choices text");
            c.Check(string.Join(",", picker.ScalingChoices.Select(x => x.ToString()).ToArray()) == "Default,Center,Stretch", "scaling choices text");
            c.Check(picker.ScalingChoices.Select(x => x.Value).SequenceEqual(new uint[] { 0, 2, 1 }), "scaling choice values are the raw uints");
        }

        // ---- default stars ---------------------------------------------------------------------

        private static void CheckDefaultStars(Checklist c)
        {
            c.Lines.Add(string.Empty);
            c.Lines.Add("Default stars");

            ResolutionCatalog a = CatalogA(null);
            c.Check(ResolutionCatalog.DefaultStarCandidates.Length == 7, "seven default candidates");
            c.Check(ResolutionCatalog.DefaultStarCandidates.All(s => a.IsStarred(s)), "every candidate the display offers is starred on display A");
            c.Check(!a.IsStarred(Size(2560, 1440)) && !a.IsStarred(Size(3840, 2160)), "non-candidates are not starred");

            ResolutionCatalog b = ResolutionCatalog.Build(BuildDisplayB(), WindowsB(), null);
            c.Check(!b.Contains(Size(1440, 1080)) && !b.IsStarred(Size(1440, 1080)), "display B lacks 1440x1080 and does not star it");
            c.Check(!b.BuildEntries().Any(e => !e.IsSeparator && (e.Size.Equals(Size(1440, 1080)) || e.Size.Equals(Size(1280, 960)))), "unoffered candidates are not shown");
            c.Check(b.Native.Equals(Size(3440, 1440)) && b.IsStarred(Size(3440, 1440)), "a 21:9 native (3440x1440) is starred");
            List<ResolutionEntry> bEntries = b.BuildEntries();
            c.Check(Describe(bEntries) == "3440x1440|1920x1080|1280x720|---|2560x1440|2560x1080", "display B order: " + Describe(bEntries));
            c.Check(bEntries[0].ToString() == "\u2605 3440 x 1440 (21:9, native)", "21:9 native text: " + bEntries[0]);

            b.SetStarred(Size(3440, 1440), false);
            c.Check(b.IsStarred(Size(3440, 1440)) && b.Stars.Removed.Count == 0 && b.Stars.Added.Count == 0, "SetStarred(native, false) is a no-op");
            b.SetStarred(Size(3440, 1440), true);
            c.Check(b.Stars.Added.Count == 0 && b.Stars.Removed.Count == 0, "SetStarred(native, true) is a no-op too");
            ResolutionPicker bp = new ResolutionPicker(b);
            bp.SelectDefault();
            c.Check(!bp.CanToggleStar, "CanToggleStar is false on the native size");
            bp.ToggleStar();
            c.Check(b.IsStarred(Size(3440, 1440)) && bp.SelectedEntry.IsNative, "ToggleStar on native changes nothing");

            // Added sizes the display does not offer are not shown.
            ResolutionStarPreferences stars = new ResolutionStarPreferences();
            stars.Added.Add(Size(5120, 2880));
            stars.Added.Add(Size(2560, 1440));
            ResolutionCatalog withAdded = CatalogA(stars);
            c.Check(!withAdded.IsStarred(Size(5120, 2880)) && !withAdded.BuildEntries().Any(e => !e.IsSeparator && e.Size.Equals(Size(5120, 2880))),
                "an Added size the display does not offer is neither starred nor shown");
            c.Check(withAdded.IsStarred(Size(2560, 1440)), "an Added size the display offers is starred");
            c.Check(Describe(withAdded.BuildEntries()) == "1920x1080|2560x1440|1680x1050|1440x1080|1280x960|1280x800|1280x720|1024x768|---|3840x2160|1366x768|800x600|720x400|640x480",
                "Added size sorts into the starred section by width: " + Describe(withAdded.BuildEntries()));

            // The catalog works on a private copy of the preferences.
            withAdded.SetStarred(Size(1280, 720), false);
            c.Check(!stars.Removed.Contains(Size(1280, 720)) && withAdded.Stars.Removed.Contains(Size(1280, 720)), "the caller's preferences object is not mutated");

            // SetStarred round trips.
            ResolutionCatalog sc = CatalogA(null);
            sc.SetStarred(Size(2560, 1440), true);
            c.Check(sc.IsStarred(Size(2560, 1440)) && sc.Stars.Added.Contains(Size(2560, 1440)), "starring a non-default adds it to Added");
            sc.SetStarred(Size(2560, 1440), false);
            c.Check(!sc.IsStarred(Size(2560, 1440)) && sc.Stars.Added.Count == 0, "unstarring it removes it from Added");
            sc.SetStarred(Size(1280, 720), false);
            c.Check(!sc.IsStarred(Size(1280, 720)) && sc.Stars.Removed.Contains(Size(1280, 720)), "unstarring a default adds it to Removed");
            sc.SetStarred(Size(1280, 720), true);
            c.Check(sc.IsStarred(Size(1280, 720)) && sc.Stars.Removed.Count == 0, "re-starring a default removes it from Removed");
            sc.SetStarred(Size(5120, 2880), true);
            c.Check(sc.Stars.Added.Count == 0, "starring a size the display does not offer is a no-op");
        }

        // ---- star persistence ------------------------------------------------------------------

        private static void CheckStarPersistence(Checklist c)
        {
            c.Lines.Add(string.Empty);
            c.Lines.Add("Star persistence");

            ResolutionStarPreferences empty = ResolutionStarPreferences.Parse(null, null);
            c.Check(empty.Added.Count == 0 && empty.Removed.Count == 0 && empty.FormatAdded() == "" && empty.FormatRemoved() == "", "null strings parse to empty and format to empty");

            ResolutionStarPreferences p = new ResolutionStarPreferences();
            p.Added.Add(Size(2560, 1440));
            p.Added.Add(Size(1600, 900));
            p.Removed.Add(Size(1280, 720));
            p.Removed.Add(Size(1024, 768));
            c.Check(p.FormatAdded() == "1600x900,2560x1440", "FormatAdded is sorted: " + p.FormatAdded());
            c.Check(p.FormatRemoved() == "1024x768,1280x720", "FormatRemoved is sorted: " + p.FormatRemoved());
            ResolutionStarPreferences back = ResolutionStarPreferences.Parse(p.FormatAdded(), p.FormatRemoved());
            c.Check(back.Added.SetEquals(p.Added) && back.Removed.SetEquals(p.Removed), "Format then Parse round trips");

            ResolutionStarPreferences bad = ResolutionStarPreferences.Parse(
                "abc, 0x0,1920x,x1080,0x1080,1920x0,19 20x1080,-1x5,99999999999x5,,  1600X900 ,2560x1440,2560x1440,1920x1080,1280x720",
                "1280x720,3840x2160,1280x720,bad,1280x,1024X768");
            c.Check(bad.Added.Count == 2 && bad.Added.Contains(Size(1600, 900)) && bad.Added.Contains(Size(2560, 1440)),
                "Added keeps only valid non-default sizes, trimmed, X accepted, duplicates folded, got " + bad.FormatAdded());
            c.Check(bad.Removed.Count == 2 && bad.Removed.Contains(Size(1280, 720)) && bad.Removed.Contains(Size(1024, 768)),
                "Removed keeps only valid default sizes (a non-default is dropped), got " + bad.FormatRemoved());

            string tempIni = Path.Combine(Path.GetTempPath(), "vibranceGUI-resolutionpicker-selftest-" + Guid.NewGuid().ToString("N") + ".ini");
            string tempXml = Path.Combine(Path.GetTempPath(), "vibranceGUI-resolutionpicker-selftest-" + Guid.NewGuid().ToString("N") + ".xml");
            try
            {
                SettingsController missing = new SettingsController(tempIni, tempXml);
                ResolutionStarPreferences none = missing.ReadResolutionStars();
                c.Check(none != null && none.Added.Count == 0 && none.Removed.Count == 0, "a missing INI reads as empty preferences");
                c.Check(!File.Exists(tempIni), "reading does not create the INI");

                SettingsController writer = new SettingsController(tempIni, tempXml);
                writer.SetResolutionStars(p);
                string raw = File.ReadAllText(tempIni);
                c.Check(raw.Contains("\nstarredResolutions=1600x900,2560x1440"), "starredResolutions written to [Settings]");
                c.Check(raw.Contains("unstarredResolutions=1024x768,1280x720"), "unstarredResolutions written to [Settings]");

                SettingsController reader = new SettingsController(tempIni, tempXml);
                ResolutionStarPreferences loaded = reader.ReadResolutionStars();
                c.Check(loaded.Added.SetEquals(p.Added) && loaded.Removed.SetEquals(p.Removed), "a NEW controller reads back what was written");

                ResolutionCatalog catalog = CatalogA(loaded);
                c.Check(!catalog.IsStarred(Size(1280, 720)) && !catalog.IsStarred(Size(1024, 768)), "an unstarred default stays unstarred after reload");
                c.Check(catalog.IsStarred(Size(2560, 1440)) && catalog.IsStarred(Size(1920, 1080)), "an added size and the other defaults are starred after reload");

                catalog.SetStarred(Size(1280, 720), true);
                c.Check(catalog.Stars.Removed.Count == 1 && !catalog.Stars.Removed.Contains(Size(1280, 720)), "re-starring removes it from Removed");
                writer.SetResolutionStars(catalog.Stars);
                ResolutionStarPreferences again = new SettingsController(tempIni, tempXml).ReadResolutionStars();
                c.Check(again.Removed.Count == 1 && again.Removed.Contains(Size(1024, 768)) && again.Added.Count == 2, "the re-star persisted");
                writer.SetResolutionStars(new ResolutionStarPreferences());
                ResolutionStarPreferences cleared = new SettingsController(tempIni, tempXml).ReadResolutionStars();
                c.Check(cleared.Added.Count == 0 && cleared.Removed.Count == 0, "writing empty preferences clears both keys");

                // Garbage in the file never throws and is filtered.
                writer.SetVibranceSetting("starredResolutions", "junk,1920x1080,2560x1440");
                writer.SetVibranceSetting("unstarredResolutions", "2560x1440,1280x720");
                ResolutionStarPreferences filtered = new SettingsController(tempIni, tempXml).ReadResolutionStars();
                c.Check(filtered.Added.Count == 1 && filtered.Added.Contains(Size(2560, 1440)) && filtered.Removed.Count == 1 && filtered.Removed.Contains(Size(1280, 720)),
                    "hand-edited garbage is filtered on read");

                // A long list survives the 4096-char buffer.
                ResolutionStarPreferences many = new ResolutionStarPreferences();
                for (uint i = 0; i < 200; i++)
                {
                    many.Added.Add(Size(2000 + i, 1500 + i));
                }
                writer.SetResolutionStars(many);
                c.Check(new SettingsController(tempIni, tempXml).ReadResolutionStars().Added.Count == 200, "a 200-size list (about 2.4k chars) round trips");

                bool threw = false;
                try { writer.SetResolutionStars(null); } catch (Exception) { threw = true; }
                c.Check(!threw && new SettingsController(tempIni, tempXml).ReadResolutionStars().Added.Count == 0,
                    "SetResolutionStars(null) does not throw and writes empty preferences");
            }
            finally
            {
                DeleteQuietly(tempIni);
                DeleteQuietly(tempXml);
            }
        }

        private static void DeleteQuietly(string path)
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
            }
        }

        // ---- picker round trip -----------------------------------------------------------------

        private static void CheckRoundTrip(Checklist c)
        {
            c.Lines.Add(string.Empty);
            c.Lines.Add("Picker round trip and unavailable saved modes");

            List<ResolutionModeWrapper> modes = BuildDisplayA();
            ResolutionCatalog catalog = ResolutionCatalog.Build(modes, WindowsA(), null);
            ResolutionPicker picker = new ResolutionPicker(catalog);

            int failures = 0;
            string firstFailure = null;
            foreach (ResolutionModeWrapper mode in modes)
            {
                picker.Load(mode);
                ResolutionModeWrapper got = picker.GetSelectedMode();
                bool ok = ReferenceEquals(got, mode) && mode.Equals(got) && !picker.IsSavedModeUnavailable &&
                    picker.SelectedEntry != null && picker.SelectedEntry.Size.Equals(ResolutionSize.Of(mode)) &&
                    picker.SelectedRefreshRate == mode.DmDisplayFrequency && picker.SelectedScaling == mode.DmDisplayFixedOutput;
                if (!ok)
                {
                    failures++;
                    if (firstFailure == null)
                    {
                        firstFailure = mode.ToString();
                    }
                }
            }
            c.Check(failures == 0, "Load(mode) then GetSelectedMode() returns the same list instance for all " + modes.Count + " modes" +
                (failures == 0 ? string.Empty : "; " + failures + " failed, first " + firstFailure));
            c.Check(modes.Any(m => m.DmBitsPerPel == 16) && modes.Any(m => m.DmDisplayFixedOutput == 3), "the fake list really contains 16 bpp and fixedOutput=3 modes");

            // Load(null) = native, highest rate, Default scaling.
            picker.Load(null);
            ResolutionModeWrapper def = picker.GetSelectedMode();
            c.Check(picker.SelectedEntry.IsNative && picker.SelectedRefreshRate == 240 && picker.SelectedScaling == 0 && !picker.IsSavedModeUnavailable,
                "Load(null) selects native, highest rate, Default scaling");
            c.Check(def != null && def.Equals(Mode(1920, 1080, 32, 240, 0)), "Load(null) resolves to 1920x1080 @ 240, 32 bpp, Default");
            picker.SelectEntry(EntryFor(picker, 1280, 720));
            picker.SelectDefault();
            c.Check(picker.SelectedEntry.IsNative && picker.SelectedRefreshRate == 240, "SelectDefault (Reset) returns to native at the highest rate");

            // A size change goes back to 32 bpp even after loading a 16 bpp profile.
            picker.Load(Mode(1920, 1080, 16, 60, 0));
            c.Check(picker.GetSelectedMode().DmBitsPerPel == 16, "a loaded 16 bpp profile keeps its bit depth");
            picker.SelectEntry(EntryFor(picker, 1280, 720));
            c.Check(picker.GetSelectedMode().DmBitsPerPel == 32, "changing the size returns to 32 bpp");
            picker.SelectRefreshRate(60);
            picker.SelectScaling(0);
            c.Check(picker.GetSelectedMode().DmBitsPerPel == 32, "32 bpp preferred even where a 16 bpp twin exists");

            // Size-missing saved mode.
            int baseCount = picker.Entries.Count(e => !e.IsUnavailable);
            ResolutionModeWrapper missingSize = Mode(1600, 900, 32, 60, 0);
            picker.Load(missingSize);
            c.Check(picker.IsSavedModeUnavailable, "a saved mode with an unoffered size sets IsSavedModeUnavailable");
            c.Check(picker.Entries[0].IsUnavailable && ReferenceEquals(picker.SelectedEntry, picker.Entries[0]), "the unavailable entry is first and selected");
            c.Check(picker.Entries.Count == baseCount + 1, "exactly one extra entry");
            c.Check(picker.Entries[0].ToString() == "1600 x 900 @ 60 Hz, Default (not offered by this display)", "unavailable text: " + picker.Entries[0]);
            c.Check(ReferenceEquals(picker.GetSelectedMode(), missingSize), "GetSelectedMode returns the saved instance");
            c.Check(picker.RefreshRateChoices.Count == 1 && picker.RefreshRateChoices[0].Value == 60 && picker.ScalingChoices.Count == 1 && picker.ScalingChoices[0].Value == 0,
                "rate and scaling choices show only the saved values");
            c.Check(!picker.CanToggleStar, "cannot star the unavailable entry");
            picker.SelectRefreshRate(120);
            picker.SelectScaling(1);
            c.Check(ReferenceEquals(picker.GetSelectedMode(), missingSize), "rate and scaling changes are ignored while unavailable");
            picker.ToggleStar();
            c.Check(picker.IsSavedModeUnavailable && picker.Entries[0].IsUnavailable, "ToggleStar is a no-op while unavailable");
            c.Check(picker.SelectEntry(picker.Entries[0]) && picker.IsSavedModeUnavailable, "re-selecting the unavailable entry keeps it");
            ResolutionEntry other = EntryFor(picker, 2560, 1440);
            c.Check(picker.SelectEntry(other), "SelectEntry(real entry) succeeds");
            c.Check(!picker.IsSavedModeUnavailable && picker.Entries.Count == baseCount && !picker.Entries.Any(e => e.IsUnavailable),
                "selecting a real entry removes the unavailable entry and clears the flag");
            c.Check(picker.GetSelectedMode() != null && modes.Contains(picker.GetSelectedMode()) && picker.GetSelectedMode().DmPelsWidth == 2560,
                "and the selection is a real list mode");

            // Rate-missing and scaling-missing saved modes.
            ResolutionModeWrapper missingRate = Mode(1920, 1080, 32, 200, 1);
            picker.Load(missingRate);
            c.Check(picker.IsSavedModeUnavailable && picker.Entries[0].IsUnavailable, "a saved mode with an unoffered rate sets the flag");
            c.Check(picker.Entries[0].ToString() == "1920 x 1080 @ 200 Hz, Stretch (not offered by this display)", "rate-missing text: " + picker.Entries[0]);
            c.Check(ReferenceEquals(picker.GetSelectedMode(), missingRate), "rate-missing: GetSelectedMode returns the saved instance");
            c.Check(picker.Entries.Count(e => !e.IsSeparator && !e.IsUnavailable && e.Size.Equals(Size(1920, 1080))) == 1,
                "the offered 1920x1080 entry is still listed once");
            c.Check(picker.SelectEntry(EntryFor(picker, 1920, 1080)) && !picker.IsSavedModeUnavailable && picker.SelectedRefreshRate == 240,
                "choosing the same size's real entry clears the flag and goes to the highest rate");

            ResolutionModeWrapper missingScaling = Mode(1920, 1080, 32, 60, 3);
            picker.Load(missingScaling);
            c.Check(picker.IsSavedModeUnavailable && ReferenceEquals(picker.GetSelectedMode(), missingScaling), "a saved scaling not offered at that rate sets the flag");
            ResolutionModeWrapper missingBpp = Mode(1920, 1080, 24, 60, 0);
            picker.Load(missingBpp);
            c.Check(picker.IsSavedModeUnavailable && ReferenceEquals(picker.GetSelectedMode(), missingBpp), "a saved bit depth not offered sets the flag (five-field match)");
            picker.Load(modes[0]);
            c.Check(!picker.IsSavedModeUnavailable && picker.Entries.Count == baseCount, "loading an offered mode afterwards clears the flag");
            picker.Load(missingSize);
            picker.Load(null);
            c.Check(!picker.IsSavedModeUnavailable && picker.SelectedEntry.IsNative && picker.Entries.Count == baseCount, "Load(null) after an unavailable mode clears it");

            // ToggleStar keeps the selection.
            picker.SelectEntry(EntryFor(picker, 2560, 1440));
            picker.SelectRefreshRate(144);
            picker.SelectScaling(2);
            c.Check(picker.CanToggleStar, "a non-native size can be toggled");
            picker.ToggleStar();
            c.Check(picker.SelectedEntry.Size.Equals(Size(2560, 1440)) && picker.SelectedEntry.IsStarred && picker.SelectedRefreshRate == 144 && picker.SelectedScaling == 2,
                "ToggleStar stars it and keeps size, rate and scaling");
            c.Check(ReferenceEquals(picker.SelectedEntry, EntryFor(picker, 2560, 1440)) && picker.Entries.IndexOf(picker.SelectedEntry) < picker.Entries.FindIndex(e => e.IsSeparator),
                "the selected entry is the rebuilt one and now sits above the separator");
            c.Check(catalog.Stars.Added.Contains(Size(2560, 1440)), "the catalog's preferences carry the new star");
            picker.ToggleStar();
            c.Check(picker.SelectedEntry.Size.Equals(Size(2560, 1440)) && !picker.SelectedEntry.IsStarred && picker.SelectedRefreshRate == 144 && catalog.Stars.Added.Count == 0,
                "a second ToggleStar unstars it again");
            picker.SelectEntry(EntryFor(picker, 1280, 720));
            picker.ToggleStar();
            c.Check(!picker.SelectedEntry.IsStarred && catalog.Stars.Removed.Contains(Size(1280, 720)), "toggling a default unstars it into Removed");
            picker.ToggleStar();
            c.Check(picker.SelectedEntry.IsStarred && catalog.Stars.Removed.Count == 0, "toggling it back re-stars it");

            // ApplicationSetting XML round trip.
            ResolutionModeWrapper[] samples = new ResolutionModeWrapper[]
            {
                modes.First(m => m.DmDisplayFixedOutput == 3),
                modes.First(m => m.DmBitsPerPel == 16),
                modes.First(m => m.DmPelsWidth == 2560 && m.DmDisplayFrequency == 144 && m.DmDisplayFixedOutput == 1)
            };
            bool xmlOk = true;
            string xmlNote = string.Empty;
            foreach (ResolutionModeWrapper sample in samples)
            {
                try
                {
                    ApplicationSetting original = new ApplicationSetting();
                    original.Name = "fixture";
                    original.FileName = "fixture.exe";
                    original.IsResolutionChangeNeeded = true;
                    picker.Load(sample);
                    original.ResolutionSettings = picker.GetSelectedMode();

                    XmlSerializer serializer = new XmlSerializer(typeof(ApplicationSetting));
                    StringWriter writer = new StringWriter();
                    serializer.Serialize(writer, original);
                    ApplicationSetting restored = (ApplicationSetting)serializer.Deserialize(new StringReader(writer.ToString()));
                    if (restored.ResolutionSettings == null || !restored.ResolutionSettings.Equals(original.ResolutionSettings) ||
                        !original.ResolutionSettings.Equals(restored.ResolutionSettings))
                    {
                        xmlOk = false;
                        xmlNote = " (" + sample + " did not survive)";
                    }

                    picker.Load(restored.ResolutionSettings);
                    if (picker.IsSavedModeUnavailable || !ReferenceEquals(picker.GetSelectedMode(), sample))
                    {
                        xmlOk = false;
                        xmlNote = " (" + sample + " did not reload)";
                    }
                }
                catch (Exception ex)
                {
                    xmlOk = false;
                    xmlNote = " (" + ex.GetType().Name + ")";
                }
            }
            c.Check(xmlOk, "an ApplicationSetting serialized with XmlSerializer reads back Equal and reloads into the picker" + xmlNote);
        }

        // ---- dialog level ----------------------------------------------------------------------
        // Constructs the real VibranceSettings form (never shown, no handle needed) over fake modes
        // and drives its private handlers by reflection, exactly like a click would.

        private sealed class FakeProxy : IVibranceProxy
        {
            public bool NeverChangeResolution;

            public void SetApplicationSettings(List<ApplicationSetting> refApplicationSettings) { }
            public void SetShouldRun(bool shouldRun) { }
            public void SetVibranceWindowsLevel(int vibranceWindowsLevel) { }
            public void SetVibranceIngameLevel(int vibranceIngameLevel) { }
            public bool UnloadLibraryEx() { return true; }
            public void HandleDvcExit() { }
            public void SetAffectPrimaryMonitorOnly(bool affectPrimaryMonitorOnly) { }
            public VibranceInfo GetVibranceInfo()
            {
                VibranceInfo info = new VibranceInfo();
                info.neverChangeResolution = NeverChangeResolution;
                return info;
            }
            public GraphicsAdapter GraphicsAdapter { get { return GraphicsAdapter.Nvidia; } }
            public void SetNeverSwitchResolution(bool neverSwitchResolution) { }
            public void SetGameExitWatcher(IGameExitWatcher watcher) { }
            public void SetNeverChangeColorSettings(bool neverChangeColorSettings) { }
            public void SetWindowsColorSettings(int brightness, int contrast, int gamma) { }
            public void SetWindowsColorBrightness(int brightness) { }
            public void SetWindowsColorContrast(int contrast) { }
            public void SetWindowsColorGamma(int gamma) { }
            public ProfileToggleResult ToggleForegroundProfile(IntPtr foregroundWindow, string processName, string processImagePath) { return ProfileToggleResult.NoConfiguredGameInForeground; }
            public void RecheckForegroundHdrLevel(IntPtr foregroundWindow, string processName, string processImagePath) { }
            public bool ApplyStartupForegroundProfile(IntPtr hWnd, string processName, string processImagePath) { return false; }
            public void ReplayPersistedVibranceRestore() { }
        }

        private sealed class DialogRig : IDisposable
        {
            public readonly VibranceSettings Dialog;
            public readonly List<ResolutionModeWrapper> Modes;
            // Snapshots, not the live object the dialog hands over
            public readonly List<ResolutionStarPreferences> Saved = new List<ResolutionStarPreferences>();
            private readonly ListView _listView;

            public DialogRig(List<ResolutionModeWrapper> modes, ResolutionModeWrapper windows, ApplicationSetting setting,
                ResolutionStarPreferences stars, bool neverChangeResolution, bool withCallback)
            {
                Modes = modes;
                _listView = new ListView();
                _listView.LargeImageList = new ImageList();
                _listView.LargeImageList.Images.Add(new Bitmap(4, 4));
                ListViewItem item = new ListViewItem("fixture");
                item.ImageIndex = 0;
                item.Tag = "C:\\fixture\\game.exe";
                _listView.Items.Add(item);
                FakeProxy proxy = new FakeProxy();
                proxy.NeverChangeResolution = neverChangeResolution;
                Action<ResolutionStarPreferences> callback = null;
                if (withCallback)
                {
                    callback = delegate (ResolutionStarPreferences s)
                    {
                        Saved.Add(ResolutionStarPreferences.Parse(s.FormatAdded(), s.FormatRemoved()));
                    };
                }
                Dialog = new VibranceSettings(proxy, 0, 63, 20, item, setting, modes, windows, stars, callback, GraphicsAdapter.Nvidia);
            }

            public T Get<T>(string field)
            {
                FieldInfo f = typeof(VibranceSettings).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic);
                return (T)f.GetValue(Dialog);
            }

            public void Call(string method)
            {
                MethodInfo m = typeof(VibranceSettings).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic);
                m.Invoke(Dialog, new object[] { null, EventArgs.Empty });
            }

            public ComboBox Size { get { return Get<ComboBox>("cBoxResolution"); } }
            public ComboBox Rate { get { return Get<ComboBox>("cBoxRefreshRate"); } }
            public ComboBox Scaling { get { return Get<ComboBox>("cBoxScaling"); } }
            public CheckBox Star { get { return Get<CheckBox>("checkBoxStarResolution"); } }
            public CheckBox Change { get { return Get<CheckBox>("checkBoxResolution"); } }
            public Label Warning { get { return Get<Label>("labelResolutionUnavailable"); } }
            public ResolutionEntry SelectedEntry { get { return Size.SelectedItem as ResolutionEntry; } }
            public uint SelectedRate { get { ResolutionChoice c = Rate.SelectedItem as ResolutionChoice; return c == null ? uint.MaxValue : c.Value; } }
            public uint SelectedScaling { get { ResolutionChoice c = Scaling.SelectedItem as ResolutionChoice; return c == null ? uint.MaxValue : c.Value; } }
            public ResolutionModeWrapper Result { get { return Dialog.GetApplicationSetting().ResolutionSettings; } }

            public void PickSize(ResolutionEntry entry)
            {
                Size.SelectedItem = entry;
                Call("cBoxResolution_SelectionChangeCommitted");
            }

            public void PickSize(uint w, uint h)
            {
                PickSize(Size.Items.Cast<object>().OfType<ResolutionEntry>().First(e => !e.IsSeparator && !e.IsUnavailable && e.Size.Equals(new ResolutionSize(w, h))));
            }

            public void PickRate(uint hz)
            {
                Rate.SelectedItem = Rate.Items.Cast<ResolutionChoice>().First(x => x.Value == hz);
                Call("cBoxRefreshRate_SelectionChangeCommitted");
            }

            public void PickScaling(uint value)
            {
                Scaling.SelectedItem = Scaling.Items.Cast<ResolutionChoice>().First(x => x.Value == value);
                Call("cBoxScaling_SelectionChangeCommitted");
            }

            public void Dispose()
            {
                Dialog.Dispose();
                _listView.Dispose();
            }
        }

        // Control.Visible is false for every child of a form that was never shown, so read the
        // control's own visible flag (STATE_VISIBLE = 2) instead.
        private static bool OwnVisible(Control control)
        {
            MethodInfo getState = typeof(Control).GetMethod("GetState", BindingFlags.Instance | BindingFlags.NonPublic, null, new Type[] { typeof(int) }, null);
            return (bool)getState.Invoke(control, new object[] { 2 });
        }

        private static ApplicationSetting Profile(ResolutionModeWrapper mode, bool changeResolution)
        {
            ApplicationSetting setting = new ApplicationSetting();
            setting.Name = "fixture";
            setting.FileName = "C:\\fixture\\game.exe";
            setting.IngameLevel = 30;
            setting.IsResolutionChangeNeeded = changeResolution;
            setting.ResolutionSettings = mode;
            return setting;
        }

        private static void CheckDialog(Checklist c)
        {
            c.Lines.Add(string.Empty);
            c.Lines.Add("Dialog: saved profile -> UI -> back");

            try
            {
                // A: untouched existing profiles of every flavour come back as the list's own instance.
                List<ResolutionModeWrapper> modesA = BuildDisplayA();
                ResolutionModeWrapper[] saved = new ResolutionModeWrapper[]
                {
                    Mode(1920, 1080, 32, 240, 2),   // Center
                    Mode(2560, 1440, 32, 144, 1),   // Stretch
                    Mode(1920, 1080, 16, 60, 0),    // 16 bpp
                    Mode(1920, 1080, 16, 60, 2),    // 16 bpp + Center
                    Mode(1920, 1080, 32, 144, 3),   // unknown scaling value
                    Mode(1280, 720, 32, 120, 0)
                };
                foreach (ResolutionModeWrapper profileMode in saved)
                {
                    using (DialogRig rig = new DialogRig(modesA, WindowsA(), Profile(profileMode, true), null, false, true))
                    {
                        ResolutionModeWrapper result = rig.Result;
                        string label = profileMode.ToString();
                        c.Check(result != null && result.Equals(profileMode) && modesA.Any(m => ReferenceEquals(m, result)),
                            "untouched profile " + label + " saves the identical mode, as an instance of the supported list (apply path Item2.Contains still matches)");
                        c.Check(modesA.Contains(result), "List.Contains (the proxies' Item2.Contains) matches " + label);
                        c.Check(rig.SelectedEntry != null && rig.SelectedEntry.Size.Equals(ResolutionSize.Of(profileMode)) &&
                            rig.SelectedRate == profileMode.DmDisplayFrequency && rig.SelectedScaling == profileMode.DmDisplayFixedOutput,
                            "dialog shows size, rate and scaling of " + label);
                        c.Check(rig.Change.Checked && rig.Size.Enabled && rig.Rate.Enabled && rig.Scaling.Enabled && !OwnVisible(rig.Warning),
                            "all pickers enabled, no warning, for " + label);
                        c.Check(rig.Scaling.Items.Count == rig.Get<ResolutionPicker>("_resolutionPicker").ScalingChoices.Count, "scaling box holds the picker's choices for " + label);
                    }
                }

                // B: new profile (setting == null) defaults.
                using (DialogRig rig = new DialogRig(modesA, WindowsA(), null, null, false, true))
                {
                    c.Check(rig.SelectedEntry.IsNative && rig.SelectedRate == 240 && rig.SelectedScaling == 0 && !rig.Change.Checked,
                        "new profile: native, highest rate, Default scaling, Change Resolution unchecked");
                    c.Check(!rig.Size.Enabled && !rig.Rate.Enabled && !rig.Scaling.Enabled && !rig.Star.Enabled,
                        "unchecked Change Resolution disables size, rate, scaling and star");
                    rig.Change.Checked = true;
                    c.Check(rig.Size.Enabled && rig.Rate.Enabled && rig.Scaling.Enabled && !rig.Star.Enabled,
                        "checking Change Resolution enables size/rate/scaling (star stays off on the pinned native)");
                    rig.Change.Checked = false;
                    c.Check(!rig.Size.Enabled && !rig.Rate.Enabled && !rig.Scaling.Enabled && !rig.Star.Enabled, "unchecking disables everything again");

                    // new pick defaults to the highest rate
                    rig.Change.Checked = true;
                    rig.PickSize(1280, 720);
                    c.Check(rig.SelectedRate == 120 && rig.Result.Equals(Mode(1280, 720, 32, 120, 0)), "a new size pick defaults to its highest rate (120), 32 bpp");
                    rig.PickRate(60);
                    rig.PickScaling(2);
                    c.Check(rig.Result.Equals(Mode(1280, 720, 32, 60, 2)), "rate and scaling are picked separately");
                    c.Check(rig.Size.Items.Cast<object>().OfType<ResolutionEntry>().Count(e => !e.IsSeparator && e.Size.Equals(Size(1280, 720))) == 1,
                        "each size is listed once even though it has several rates and scalings");
                }

                // C: separator can never be selected.
                using (DialogRig rig = new DialogRig(modesA, WindowsA(), Profile(Mode(2560, 1440, 32, 144, 1), true), null, false, true))
                {
                    List<ResolutionEntry> items = rig.Size.Items.Cast<object>().OfType<ResolutionEntry>().ToList();
                    int sepIndex = items.FindIndex(e => e.IsSeparator);
                    ResolutionEntry sep = items[sepIndex];
                    ResolutionEntry lastStarred = items[sepIndex - 1];
                    ResolutionEntry firstOther = items[sepIndex + 1];

                    // stepping down from the last starred size onto the separator continues to the first other size
                    rig.PickSize(lastStarred);
                    rig.PickSize(sep);
                    c.Check(ReferenceEquals(rig.SelectedEntry, rig.Size.Items.Cast<object>().OfType<ResolutionEntry>().First(e => !e.IsSeparator && e.Size.Equals(firstOther.Size))) &&
                        !rig.SelectedEntry.IsSeparator && rig.Result.DmPelsWidth == firstOther.Size.Width,
                        "stepping down from the last starred entry onto the separator lands on the first other entry");

                    // stepping up from the first other size onto the separator continues to the last starred size
                    rig.PickSize(sep);
                    c.Check(!rig.SelectedEntry.IsSeparator && rig.SelectedEntry.Size.Equals(lastStarred.Size) && rig.Result.DmPelsWidth == lastStarred.Size.Width,
                        "stepping up from the first other entry onto the separator lands on the last starred entry");
                }

                // D: star toggle persists via callback, keeps selection.
                using (DialogRig rig = new DialogRig(modesA, WindowsA(), Profile(Mode(2560, 1440, 32, 144, 1), true), null, false, true))
                {
                    c.Check(rig.Star.Enabled && !rig.Star.Checked, "an unstarred non-native size shows an enabled, unchecked star box");
                    rig.Star.Checked = true;
                    c.Check(rig.Saved.Count == 1 && rig.Saved[0].Added.SequenceEqual(new ResolutionSize[] { Size(2560, 1440) }) && rig.Saved[0].Removed.Count == 0,
                        "starring calls the save callback once with the new Added set");
                    c.Check(rig.SelectedEntry.Size.Equals(Size(2560, 1440)) && rig.SelectedEntry.IsStarred && rig.Star.Checked && rig.SelectedRate == 144 && rig.SelectedScaling == 1,
                        "selection, rate, scaling and the checked box survive the starring");
                    c.Check(rig.Size.Items.IndexOf(rig.SelectedEntry) < rig.Size.Items.Cast<object>().ToList().FindIndex(o => ((ResolutionEntry)o).IsSeparator),
                        "the starred size moved above the separator");
                    c.Check(rig.Result.Equals(Mode(2560, 1440, 32, 144, 1)), "the profile's mode is unchanged by starring");
                    rig.Star.Checked = false;
                    c.Check(rig.Saved.Count == 2 && rig.Saved[1].Added.Count == 0, "unstarring calls the callback again with an empty Added set");

                    // survives a "restart": a brand new dialog over the saved preferences
                    rig.Star.Checked = true;
                    ResolutionStarPreferences persisted = rig.Saved[rig.Saved.Count - 1];
                    using (DialogRig second = new DialogRig(modesA, WindowsA(), Profile(Mode(1920, 1080, 32, 240, 0), true), persisted, false, true))
                    {
                        c.Check(second.Size.Items.Cast<object>().OfType<ResolutionEntry>().First(e => !e.IsSeparator && e.Size.Equals(Size(2560, 1440))).IsStarred,
                            "a new dialog built from the persisted preferences shows the star");
                    }
                }
                using (DialogRig rig = new DialogRig(modesA, WindowsA(), Profile(Mode(1280, 720, 32, 120, 0), true), null, false, true))
                {
                    c.Check(rig.Star.Checked, "a default-starred size shows its star checked");
                    rig.Star.Checked = false;
                    c.Check(rig.Saved.Count == 1 && rig.Saved[0].Removed.SequenceEqual(new ResolutionSize[] { Size(1280, 720) }) && rig.Saved[0].Added.Count == 0,
                        "unstarring a default saves it in Removed");
                    c.Check(rig.SelectedEntry.Size.Equals(Size(1280, 720)) && !rig.SelectedEntry.IsStarred, "and the entry stays selected, now unstarred");
                }
                using (DialogRig rig = new DialogRig(modesA, WindowsA(), Profile(Mode(2560, 1440, 32, 144, 1), true), null, false, false))
                {
                    bool threw = false;
                    try { rig.Star.Checked = true; } catch (Exception) { threw = true; }
                    c.Check(!threw && rig.SelectedEntry.IsStarred, "starring with no save callback does not throw");
                }

                // E: Reset
                using (DialogRig rig = new DialogRig(modesA, WindowsA(), Profile(Mode(2560, 1440, 32, 144, 1), true), null, false, true))
                {
                    rig.Call("buttonReset_Click");
                    c.Check(rig.SelectedEntry.IsNative && rig.SelectedRate == 240 && rig.SelectedScaling == 0 && !rig.Change.Checked,
                        "Reset selects native, highest rate, Default scaling and unchecks Change Resolution");
                    c.Check(rig.Result.Equals(Mode(1920, 1080, 32, 240, 0)), "Reset resolves to native @ highest, 32 bpp, Default");
                    c.Check(!rig.Size.Enabled && !rig.Rate.Enabled && !rig.Scaling.Enabled && !rig.Star.Enabled, "Reset leaves everything disabled");
                }
                using (DialogRig rig = new DialogRig(modesA, WindowsA(), Profile(Mode(1600, 900, 32, 60, 0), true), null, false, true))
                {
                    rig.Call("buttonReset_Click");
                    c.Check(!OwnVisible(rig.Warning) && OwnVisible(rig.Rate) && rig.Size.Items.Cast<object>().OfType<ResolutionEntry>().All(e => !e.IsUnavailable) &&
                        rig.SelectedEntry.IsNative, "Reset clears an unavailable-mode warning and its extra entry");
                }

                // F: unavailable saved mode
                int baseCount;
                using (DialogRig baseRig = new DialogRig(modesA, WindowsA(), null, null, false, true)) { baseCount = baseRig.Size.Items.Count; }
                ResolutionModeWrapper[] missing = new ResolutionModeWrapper[]
                {
                    Mode(1600, 900, 32, 60, 0),
                    Mode(1920, 1080, 32, 200, 1),
                    Mode(1920, 1080, 32, 60, 3),
                    Mode(1920, 1080, 24, 60, 0)
                };
                foreach (ResolutionModeWrapper gone in missing)
                {
                    using (DialogRig rig = new DialogRig(modesA, WindowsA(), Profile(gone, true), null, false, true))
                    {
                        string label = gone.ToString();
                        ResolutionEntry first = rig.Size.Items.Count > 0 ? rig.Size.Items[0] as ResolutionEntry : null;
                        c.Check(rig.Size.Items.Count == baseCount + 1 && first != null && first.IsUnavailable && ReferenceEquals(rig.SelectedEntry, first),
                            "unavailable " + label + ": an extra first entry is listed and selected");
                        c.Check(first != null && first.ToString().EndsWith("(not offered by this display)"), "its text says it is not offered: " + first);
                        c.Check(ReferenceEquals(rig.Result, gone), "saving without touching anything returns the saved instance for " + label);
                        c.Check(OwnVisible(rig.Warning) && !OwnVisible(rig.Rate) && !OwnVisible(rig.Scaling), "warning replaces the rate/scaling row for " + label);
                        c.Check(rig.Size.Enabled && !rig.Rate.Enabled && !rig.Scaling.Enabled && !rig.Star.Enabled, "only the size box is enabled for " + label);
                        rig.PickSize(2560, 1440);
                        c.Check(rig.Size.Items.Count == baseCount && !rig.Size.Items.Cast<object>().OfType<ResolutionEntry>().Any(e => e.IsUnavailable) &&
                            !OwnVisible(rig.Warning) && OwnVisible(rig.Rate) && OwnVisible(rig.Scaling) && rig.Rate.Enabled && rig.Scaling.Enabled,
                            "choosing a real size drops the extra entry, hides the warning, re-enables rate/scaling for " + label);
                        c.Check(rig.Result.DmPelsWidth == 2560 && modesA.Any(m => ReferenceEquals(m, rig.Result)), "and the result is a real list mode for " + label);
                    }
                }
                using (DialogRig rig = new DialogRig(modesA, WindowsA(), Profile(Mode(1600, 900, 32, 60, 0), false), null, false, true))
                {
                    c.Check(!rig.Size.Enabled && !rig.Rate.Enabled && !rig.Scaling.Enabled, "unavailable + Change Resolution unchecked: all disabled");
                    rig.Change.Checked = true;
                    c.Check(rig.Size.Enabled && !rig.Rate.Enabled && !rig.Scaling.Enabled && !rig.Star.Enabled,
                        "enabling Change Resolution on an unavailable saved mode enables only the size box");
                    c.Check(rig.Result.Equals(Mode(1600, 900, 32, 60, 0)), "and the saved mode is still what Save returns");
                    // choosing the separator while unavailable does not lose the saved mode
                    ResolutionEntry sep = rig.Size.Items.Cast<object>().OfType<ResolutionEntry>().First(e => e.IsSeparator);
                    rig.PickSize(sep);
                    c.Check(rig.Size.SelectedItem is ResolutionEntry && ((ResolutionEntry)rig.Size.SelectedItem).IsUnavailable && rig.Result.Equals(Mode(1600, 900, 32, 60, 0)),
                        "separator pick while unavailable keeps the unavailable entry selected");
                }

                // G: neverChangeResolution wins over a saved "change resolution" profile.
                using (DialogRig rig = new DialogRig(modesA, WindowsA(), Profile(Mode(1920, 1080, 32, 240, 2), true), null, true, true))
                {
                    c.Check(!rig.Size.Enabled && !rig.Rate.Enabled && !rig.Scaling.Enabled && !rig.Star.Enabled && !rig.Change.Enabled,
                        "never-change-resolution disables the whole group even for a profile that wants a change");
                    c.Check(rig.Result.Equals(Mode(1920, 1080, 32, 240, 2)), "and the saved mode is kept");
                }
            }
            catch (Exception ex)
            {
                c.Check(false, "dialog scenarios threw " + ex.GetType().Name + ": " + ex.Message + " @ " + (ex.StackTrace ?? string.Empty).Split('\n').FirstOrDefault());
            }
        }

        // ---- edge cases ------------------------------------------------------------------------

        private static void CheckEdgeCases(Checklist c)
        {
            c.Lines.Add(string.Empty);
            c.Lines.Add("Edge cases: empty/single/odd lists, duplicates, INI abuse");

            try
            {
                // Empty list, dialog level.
                using (DialogRig rig = new DialogRig(new List<ResolutionModeWrapper>(), null, null, null, false, true))
                {
                    c.Check(rig.Size.Items.Count == 0 && rig.Rate.Items.Count == 0 && rig.Scaling.Items.Count == 0 && rig.Result == null,
                        "empty mode list: empty combos, null result, no exception");
                    rig.Change.Checked = true;
                    rig.Star.Checked = true;
                    c.Check(rig.Result == null && rig.Size.Items.Count == 0, "starring and enabling with nothing to select does not throw or invent a mode");
                }
                // Empty list with a saved profile: it is reported as unavailable, not dropped.
                ResolutionModeWrapper keep = Mode(1920, 1080, 32, 60, 0);
                using (DialogRig rig = new DialogRig(new List<ResolutionModeWrapper>(), null, Profile(keep, true), null, false, true))
                {
                    c.Check(rig.Size.Items.Count == 1 && ((ResolutionEntry)rig.Size.Items[0]).IsUnavailable && ReferenceEquals(rig.Result, keep),
                        "empty mode list + saved profile: the profile is kept and flagged unavailable");
                }
                // Null list.
                using (DialogRig rig = new DialogRig(null, null, null, null, false, true))
                {
                    c.Check(rig.Result == null, "null mode list is tolerated by the dialog");
                }

                // Single mode.
                List<ResolutionModeWrapper> single = new List<ResolutionModeWrapper> { Mode(1920, 1080, 32, 60, 0) };
                using (DialogRig rig = new DialogRig(single, null, Profile(Mode(1920, 1080, 32, 60, 0), true), null, false, true))
                {
                    c.Check(rig.Size.Items.Count == 1 && rig.SelectedEntry.IsNative && rig.Rate.Items.Count == 1 && rig.Scaling.Items.Count == 1 && ReferenceEquals(rig.Result, single[0]),
                        "single mode list: one entry, one rate, one scaling, no separator");
                }

                // Only Hz 0 / 1.
                List<ResolutionModeWrapper> odd = new List<ResolutionModeWrapper> { Mode(1024, 768, 32, 0, 0), Mode(1024, 768, 32, 1, 0), Mode(800, 600, 32, 0, 0) };
                ResolutionPicker oddPicker = new ResolutionPicker(ResolutionCatalog.Build(odd, null, null));
                oddPicker.SelectDefault();
                c.Check(oddPicker.SelectedEntry.Size.Equals(Size(1024, 768)) && oddPicker.SelectedRefreshRate == 1 && oddPicker.GetSelectedMode() != null &&
                    oddPicker.GetSelectedMode().DmDisplayFrequency == 1, "only 0/1 rates: 1 is the highest and resolves");
                oddPicker.SelectRefreshRate(0);
                c.Check(oddPicker.GetSelectedMode().DmDisplayFrequency == 0, "rate 0 selectable and resolves");
                c.Check(string.Join(",", oddPicker.RefreshRateChoices.Select(x => x.ToString()).ToArray()) == "Default rate,Default rate",
                    "0 and 1 both read 'Default rate' (indistinguishable in the combo, but both pickable)");

                // Windows mode size not in the list.
                ResolutionCatalog notNative = ResolutionCatalog.Build(BuildDisplayA(), Mode(5120, 2880, 32, 60, 0), null);
                c.Check(notNative.Native.Equals(Size(3840, 2160)), "windows mode not offered: native falls back to the largest area");
                ResolutionPicker np = new ResolutionPicker(notNative);
                np.SelectDefault();
                c.Check(np.SelectedEntry.IsNative && np.GetSelectedMode() != null, "default selection works with a fallback native");

                // Duplicate identical modes.
                List<ResolutionModeWrapper> dupes = new List<ResolutionModeWrapper>
                {
                    Mode(1920, 1080, 32, 60, 0), Mode(1920, 1080, 32, 60, 0), Mode(1920, 1080, 32, 60, 0), Mode(1280, 720, 32, 60, 0)
                };
                ResolutionCatalog dc = ResolutionCatalog.Build(dupes, null, null);
                c.Check(dc.Sizes.Count == 2 && dc.GetRefreshRates(Size(1920, 1080)).Count == 1 && dc.GetScalings(Size(1920, 1080), 60).Count == 1,
                    "duplicate identical modes collapse to one size, one rate, one scaling");
                ResolutionPicker dp = new ResolutionPicker(dc);
                dp.Load(Mode(1920, 1080, 32, 60, 0));
                c.Check(!dp.IsSavedModeUnavailable && dupes.Contains(dp.GetSelectedMode()), "a saved mode matching duplicates loads as available");

                // Zero-sized and null entries in the list are ignored, not fatal.
                List<ResolutionModeWrapper> junk = new List<ResolutionModeWrapper> { null, Mode(0, 0, 32, 60, 0), Mode(1920, 0, 32, 60, 0), Mode(1920, 1080, 32, 60, 0) };
                ResolutionCatalog jc = ResolutionCatalog.Build(junk, null, null);
                c.Check(jc.Sizes.Count == 1 && jc.Native.Equals(Size(1920, 1080)), "null and zero-sized modes are ignored");

                // Extreme values.
                ResolutionCatalog big = ResolutionCatalog.Build(new List<ResolutionModeWrapper> { Mode(uint.MaxValue, uint.MaxValue, 32, uint.MaxValue, uint.MaxValue), Mode(1920, 1080, 32, 60, 0) }, null, null);
                c.Check(big.Native.Equals(Size(uint.MaxValue, uint.MaxValue)) && big.BuildEntries().Count >= 2, "uint.MaxValue dimensions do not overflow the area comparison or entry building");
                c.Check(ResolutionCatalog.DescribeScaling(uint.MaxValue) == "Unknown (4294967295)", "scaling label for uint.MaxValue");

                // Token parsing abuse.
                ResolutionSize parsed;
                c.Check(!ResolutionSize.TryParse("1920x1080x1", out parsed) && !ResolutionSize.TryParse("", out parsed) && !ResolutionSize.TryParse("x", out parsed) &&
                    !ResolutionSize.TryParse("+1920x1080", out parsed) && !ResolutionSize.TryParse("1,920x1080", out parsed) && !ResolutionSize.TryParse("４０x30", out parsed),
                    "TryParse rejects extra x, empty, bare x, sign, thousands separator and full-width digits");
                c.Check(ResolutionSize.TryParse(" 1920X1080 ", out parsed) && parsed.Equals(Size(1920, 1080)), "TryParse accepts padding and capital X");
                c.Check(ResolutionSize.TryParse("4294967295x1", out parsed) && parsed.Width == uint.MaxValue, "TryParse accepts uint.MaxValue");
                c.Check(!ResolutionSize.TryParse("4294967296x1", out parsed), "TryParse rejects uint overflow");

                // INI abuse against a temp file.
                string tempIni = Path.Combine(Path.GetTempPath(), "vibranceGUI-resolutionpicker-edge-" + Guid.NewGuid().ToString("N") + ".ini");
                string tempXml = Path.Combine(Path.GetTempPath(), "vibranceGUI-resolutionpicker-edge-" + Guid.NewGuid().ToString("N") + ".xml");
                try
                {
                    SettingsController sc = new SettingsController(tempIni, tempXml);
                    // existing INI without either key
                    sc.SetVibranceSetting("somethingElse", "1");
                    ResolutionStarPreferences noKeys = new SettingsController(tempIni, tempXml).ReadResolutionStars();
                    c.Check(noKeys.Added.Count == 0 && noKeys.Removed.Count == 0, "INI present but keys missing reads as empty");
                    // other keys survive star writes, and vice versa
                    ResolutionStarPreferences some = new ResolutionStarPreferences();
                    some.Added.Add(Size(2560, 1440));
                    sc.SetResolutionStars(some);
                    c.Check(new SettingsController(tempIni, tempXml).ReadResolutionStars().Added.SetEquals(some.Added), "the written star list reads back (the bool return rides on SetVibranceSetting, which reads a stale GetLastWin32Error, so it is not asserted)");
                    c.Check(File.ReadAllText(tempIni).Contains("somethingElse=1"), "writing stars leaves other INI keys alone");
                    // pure garbage
                    sc.SetVibranceSetting("starredResolutions", "\u00e9\u00e8,,;;;==,\"\",1920x1080x2,NaN");
                    sc.SetVibranceSetting("unstarredResolutions", ",,,");
                    ResolutionStarPreferences garbage = new SettingsController(tempIni, tempXml).ReadResolutionStars();
                    c.Check(garbage.Added.Count == 0 && garbage.Removed.Count == 0, "garbage values read as empty");
                    // duplicates and mixed case persist as clean
                    sc.SetVibranceSetting("starredResolutions", "2560X1440,2560x1440, 2560x1440 ");
                    ResolutionStarPreferences dedup = new SettingsController(tempIni, tempXml).ReadResolutionStars();
                    c.Check(dedup.Added.Count == 1 && dedup.FormatAdded() == "2560x1440", "duplicate/mixed-case tokens fold into one clean token");
                    // very long list: more than the 1024-char default of old readers and more than 4096
                    ResolutionStarPreferences huge = new ResolutionStarPreferences();
                    for (uint i = 0; i < 700; i++)
                    {
                        huge.Added.Add(Size(2001 + i, 1501 + i));
                    }
                    string formatted = huge.FormatAdded();
                    c.Check(formatted.Length > 4096, "the huge list really exceeds the 4096-char read buffer (" + formatted.Length + ")");
                    sc.SetResolutionStars(huge);
                    ResolutionStarPreferences readHuge = null;
                    bool threw = false;
                    try { readHuge = new SettingsController(tempIni, tempXml).ReadResolutionStars(); } catch (Exception) { threw = true; }
                    c.Check(!threw && readHuge != null, "reading an over-long star list does not throw");
                    c.Check(readHuge != null && readHuge.Added.Count > 0 && readHuge.Added.All(s => huge.Added.Contains(s)),
                        "an over-long list is truncated to whole, valid tokens only (no phantom half-token such as a cut-off height)");
                    // 400-ish sizes still fit and round trip fully
                    ResolutionStarPreferences ok = new ResolutionStarPreferences();
                    for (uint i = 0; i < 380; i++)
                    {
                        ok.Added.Add(Size(2001 + i, 1501 + i));
                    }
                    sc.SetResolutionStars(ok);
                    c.Check(new SettingsController(tempIni, tempXml).ReadResolutionStars().Added.Count == 380, "a 380-size (~3.8k chars) list round trips");
                    // unwritable path never throws
                    SettingsController bad = new SettingsController(Path.Combine(tempIni, "nope", "x.ini"), tempXml);
                    bool badThrew = false;
                    bool badResult = true;
                    try { badResult = bad.SetResolutionStars(some); bad.ReadResolutionStars(); } catch (Exception) { badThrew = true; }
                    c.Check(!badThrew, "an unwritable INI path never throws from SetResolutionStars/ReadResolutionStars (result " + badResult + ")");
                }
                finally
                {
                    DeleteQuietly(tempIni);
                    DeleteQuietly(tempXml);
                }
            }
            catch (Exception ex)
            {
                c.Check(false, "edge-case scenarios threw " + ex.GetType().Name + ": " + ex.Message + " @ " + (ex.StackTrace ?? string.Empty).Split('\n').FirstOrDefault());
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

            public void Skip(string description)
            {
                Lines.Add(string.Format("[SKIP] {0}", description));
            }
        }
    }
}
