using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace vibrance.GUI.common
{
    // Model behind the per-game resolution picker (issue #60). Pure logic, no
    // System.Windows.Forms, so ResolutionCatalogFixture can drive all of it with fake mode lists
    // and no hardware.
    //
    // "Scaling" has two unrelated meanings in this code base - do not mix them up:
    //   1. Per-mode scaling (THIS file): a mode's dmDisplayFixedOutput (ResolutionHelper.Dmdfo:
    //      Default = 0, Stretch = 1, Center = 2). It is part of each game profile's saved
    //      ResolutionModeWrapper and is applied through ChangeDisplaySettingsEx. It is kept here
    //      as the raw uint, never cast to Dmdfo, so an unknown driver value survives a round trip.
    //   2. The v2.11.0 "Scale (primary display): GPU / Display" toggle in the main window: an
    //      NvAPI setting that decides which side does the scaling (NvScalingMap /
    //      DisplayScalingController). Nothing here reads or writes it.

    internal sealed class ResolutionSize : IEquatable<ResolutionSize>
    {
        public ResolutionSize(uint width, uint height)
        {
            Width = width;
            Height = height;
        }

        public uint Width { get; private set; }
        public uint Height { get; private set; }

        public string ToToken()
        {
            return Width.ToString(CultureInfo.InvariantCulture) + "x" + Height.ToString(CultureInfo.InvariantCulture);
        }

        // "1920x1080" (either case of x, surrounding whitespace tolerated). False for null,
        // garbage, a missing half, internal spaces, signs, overflow or a zero dimension.
        public static bool TryParse(string token, out ResolutionSize size)
        {
            size = null;
            if (token == null)
            {
                return false;
            }
            string[] parts = token.Trim().Split(new char[] { 'x', 'X' });
            if (parts.Length != 2)
            {
                return false;
            }
            uint width;
            uint height;
            if (!uint.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out width) ||
                !uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out height))
            {
                return false;
            }
            if (width == 0 || height == 0)
            {
                return false;
            }
            size = new ResolutionSize(width, height);
            return true;
        }

        public static ResolutionSize Of(ResolutionModeWrapper mode)
        {
            return new ResolutionSize(mode.DmPelsWidth, mode.DmPelsHeight);
        }

        public bool Equals(ResolutionSize other)
        {
            return other != null && other.Width == Width && other.Height == Height;
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as ResolutionSize);
        }

        public override int GetHashCode()
        {
            return unchecked((int)Width * 397 ^ (int)Height);
        }

        public override string ToString()
        {
            return Width.ToString(CultureInfo.InvariantCulture) + " x " + Height.ToString(CultureInfo.InvariantCulture);
        }
    }

    // The user's star choices, stored as a delta against ResolutionCatalog.DefaultStarCandidates
    // so that the defaults can evolve without rewriting anyone's INI.
    //   Added   - only NON-default sizes the user starred.
    //   Removed - only default candidates the user unstarred.
    internal sealed class ResolutionStarPreferences
    {
        public ResolutionStarPreferences()
        {
            Added = new HashSet<ResolutionSize>();
            Removed = new HashSet<ResolutionSize>();
        }

        public HashSet<ResolutionSize> Added { get; private set; }
        public HashSet<ResolutionSize> Removed { get; private set; }

        // Never throws. Malformed, zero, duplicate and mis-filed tokens (a default candidate in
        // Added, a non-default in Removed) are skipped.
        public static ResolutionStarPreferences Parse(string added, string removed)
        {
            ResolutionStarPreferences prefs = new ResolutionStarPreferences();
            foreach (ResolutionSize size in ParseTokens(added))
            {
                if (!ResolutionCatalog.IsDefaultStarCandidate(size))
                {
                    prefs.Added.Add(size);
                }
            }
            foreach (ResolutionSize size in ParseTokens(removed))
            {
                if (ResolutionCatalog.IsDefaultStarCandidate(size))
                {
                    prefs.Removed.Add(size);
                }
            }
            return prefs;
        }

        public string FormatAdded()
        {
            return Format(Added);
        }

        public string FormatRemoved()
        {
            return Format(Removed);
        }

        private static IEnumerable<ResolutionSize> ParseTokens(string text)
        {
            List<ResolutionSize> result = new List<ResolutionSize>();
            if (string.IsNullOrEmpty(text))
            {
                return result;
            }
            foreach (string token in text.Split(','))
            {
                ResolutionSize size;
                if (ResolutionSize.TryParse(token, out size))
                {
                    result.Add(size);
                }
            }
            return result;
        }

        private static string Format(HashSet<ResolutionSize> sizes)
        {
            List<ResolutionSize> sorted = new List<ResolutionSize>(sizes);
            sorted.Sort(delegate (ResolutionSize a, ResolutionSize b)
            {
                int c = a.Width.CompareTo(b.Width);
                return c != 0 ? c : a.Height.CompareTo(b.Height);
            });
            return string.Join(",", sorted.Select(s => s.ToToken()).ToArray());
        }
    }

    // One row of the resolution combo box: a size, the separator between the starred and the
    // other sizes, or the "saved mode this display does not offer" row.
    internal sealed class ResolutionEntry
    {
        public const string StarPrefix = "\u2605 ";
        public const string SeparatorText = "----------------";

        private ResolutionEntry(ResolutionSize size, bool starred, bool native, bool separator, ResolutionModeWrapper unavailable)
        {
            Size = size;
            IsStarred = starred;
            IsNative = native;
            IsSeparator = separator;
            UnavailableMode = unavailable;
        }

        public ResolutionSize Size { get; private set; }
        public bool IsStarred { get; private set; }
        public bool IsNative { get; private set; }
        public bool IsSeparator { get; private set; }
        public bool IsUnavailable { get { return UnavailableMode != null; } }
        public ResolutionModeWrapper UnavailableMode { get; private set; }

        internal static ResolutionEntry ForSize(ResolutionSize size, bool starred, bool native)
        {
            return new ResolutionEntry(size, starred, native, false, null);
        }

        internal static ResolutionEntry ForSeparator()
        {
            return new ResolutionEntry(null, false, false, true, null);
        }

        internal static ResolutionEntry ForUnavailable(ResolutionModeWrapper saved)
        {
            return new ResolutionEntry(ResolutionSize.Of(saved), false, false, false, saved);
        }

        public override string ToString()
        {
            if (IsSeparator)
            {
                return SeparatorText;
            }
            if (IsUnavailable)
            {
                return string.Format("{0} @ {1}, {2} (not offered by this display)",
                    Size,
                    ResolutionCatalog.DescribeRefreshRate(UnavailableMode.DmDisplayFrequency),
                    ResolutionCatalog.DescribeScaling(UnavailableMode.DmDisplayFixedOutput));
            }

            List<string> notes = new List<string>();
            string aspect = ResolutionCatalog.DescribeAspect(Size);
            if (aspect != null)
            {
                notes.Add(aspect);
            }
            if (IsNative)
            {
                notes.Add("native");
            }
            string text = Size.ToString();
            if (notes.Count > 0)
            {
                text += " (" + string.Join(", ", notes.ToArray()) + ")";
            }
            return IsStarred ? StarPrefix + text : text;
        }
    }

    // An item of the refresh-rate or per-mode-scaling combo box.
    internal sealed class ResolutionChoice
    {
        private readonly string _text;

        public ResolutionChoice(uint value, string text)
        {
            Value = value;
            _text = text;
        }

        public uint Value { get; private set; }

        public override string ToString()
        {
            return _text;
        }
    }

    // What a display offers, grouped for the picker. Built once per dialog from the supported
    // mode list; the list's own instances are what Resolve hands back.
    internal sealed class ResolutionCatalog
    {
        public static readonly ResolutionSize[] DefaultStarCandidates = new ResolutionSize[]
        {
            new ResolutionSize(1280, 960),
            new ResolutionSize(1440, 1080),
            new ResolutionSize(1024, 768),
            new ResolutionSize(1680, 1050),
            new ResolutionSize(1280, 800),
            new ResolutionSize(1920, 1080),
            new ResolutionSize(1280, 720)
        };

        private const double AspectTolerance = 0.03;

        private static readonly uint[][] KnownAspects = new uint[][]
        {
            new uint[] { 4, 3 }, new uint[] { 5, 4 }, new uint[] { 3, 2 }, new uint[] { 16, 10 },
            new uint[] { 16, 9 }, new uint[] { 21, 9 }, new uint[] { 32, 9 }
        };

        private readonly List<ResolutionModeWrapper> _modes = new List<ResolutionModeWrapper>();

        private ResolutionCatalog(IList<ResolutionModeWrapper> modes, ResolutionModeWrapper windowsMode, ResolutionStarPreferences stars)
        {
            if (modes != null)
            {
                foreach (ResolutionModeWrapper mode in modes)
                {
                    if (mode != null && mode.DmPelsWidth != 0 && mode.DmPelsHeight != 0)
                    {
                        _modes.Add(mode);
                    }
                }
            }

            Sizes = new List<ResolutionSize>();
            foreach (ResolutionModeWrapper mode in _modes)
            {
                ResolutionSize size = ResolutionSize.Of(mode);
                if (!Sizes.Contains(size))
                {
                    Sizes.Add(size);
                }
            }
            Sizes.Sort(CompareDescending);

            Native = DetermineNative(windowsMode, _modes);

            // A private copy: toggling a star must not mutate the caller's object behind its back.
            Stars = new ResolutionStarPreferences();
            if (stars != null)
            {
                foreach (ResolutionSize size in stars.Added)
                {
                    Stars.Added.Add(size);
                }
                foreach (ResolutionSize size in stars.Removed)
                {
                    Stars.Removed.Add(size);
                }
            }
        }

        public static ResolutionCatalog Build(IList<ResolutionModeWrapper> modes, ResolutionModeWrapper windowsMode, ResolutionStarPreferences stars)
        {
            return new ResolutionCatalog(modes, windowsMode, stars);
        }

        // The Windows (desktop) mode's size if the display offers it, else the largest-area size,
        // null for an empty list.
        public static ResolutionSize DetermineNative(ResolutionModeWrapper windowsMode, IList<ResolutionModeWrapper> modes)
        {
            if (modes == null)
            {
                return null;
            }
            ResolutionSize best = null;
            ulong bestArea = 0;
            foreach (ResolutionModeWrapper mode in modes)
            {
                if (mode == null || mode.DmPelsWidth == 0 || mode.DmPelsHeight == 0)
                {
                    continue;
                }
                ResolutionSize size = ResolutionSize.Of(mode);
                if (windowsMode != null && size.Equals(ResolutionSize.Of(windowsMode)))
                {
                    return size;
                }
                ulong area = (ulong)size.Width * size.Height;
                if (best == null || area > bestArea || (area == bestArea && size.Width > best.Width))
                {
                    best = size;
                    bestArea = area;
                }
            }
            return best;
        }

        // "16:9" etc., or null when the size is not within 3% of a known ratio. Label only.
        public static string DescribeAspect(ResolutionSize size)
        {
            if (size == null || size.Width == 0 || size.Height == 0)
            {
                return null;
            }
            double actual = (double)size.Width / size.Height;
            uint[] best = null;
            double bestDiff = double.MaxValue;
            foreach (uint[] known in KnownAspects)
            {
                double ratio = (double)known[0] / known[1];
                double diff = Math.Abs(actual - ratio) / ratio;
                if (diff < bestDiff)
                {
                    bestDiff = diff;
                    best = known;
                }
            }
            if (best == null || bestDiff > AspectTolerance)
            {
                return null;
            }
            return best[0].ToString(CultureInfo.InvariantCulture) + ":" + best[1].ToString(CultureInfo.InvariantCulture);
        }

        public static string DescribeScaling(uint fixedOutput)
        {
            switch (fixedOutput)
            {
                case 0: return "Default";
                case 2: return "Center";
                case 1: return "Stretch";
                default: return "Unknown (" + fixedOutput.ToString(CultureInfo.InvariantCulture) + ")";
            }
        }

        public static string DescribeRefreshRate(uint hz)
        {
            if (hz <= 1)
            {
                return "Default rate";
            }
            return hz.ToString(CultureInfo.InvariantCulture) + " Hz";
        }

        internal static bool IsDefaultStarCandidate(ResolutionSize size)
        {
            return size != null && Array.IndexOf(DefaultStarCandidates, size) >= 0;
        }

        public ResolutionSize Native { get; private set; }
        public ResolutionStarPreferences Stars { get; private set; }
        public List<ResolutionSize> Sizes { get; private set; }

        public bool Contains(ResolutionSize size)
        {
            return size != null && Sizes.Contains(size);
        }

        public bool IsDefaultCandidate(ResolutionSize size)
        {
            return IsDefaultStarCandidate(size);
        }

        // Effective star = native OR (default candidate not unstarred) OR explicitly added.
        // Sizes the display does not offer are never starred.
        public bool IsStarred(ResolutionSize size)
        {
            if (!Contains(size))
            {
                return false;
            }
            if (size.Equals(Native))
            {
                return true;
            }
            if (IsDefaultCandidate(size))
            {
                return !Stars.Removed.Contains(size);
            }
            return Stars.Added.Contains(size);
        }

        // No-op for the native size and for sizes the display does not offer.
        public void SetStarred(ResolutionSize size, bool starred)
        {
            if (!Contains(size) || size.Equals(Native))
            {
                return;
            }
            if (IsDefaultCandidate(size))
            {
                if (starred)
                {
                    Stars.Removed.Remove(size);
                }
                else
                {
                    Stars.Removed.Add(size);
                }
            }
            else
            {
                if (starred)
                {
                    Stars.Added.Add(size);
                }
                else
                {
                    Stars.Added.Remove(size);
                }
            }
        }

        // Distinct rates, highest first; 0 and 1 ("hardware default") come last.
        public List<uint> GetRefreshRates(ResolutionSize size)
        {
            List<uint> rates = new List<uint>();
            foreach (ResolutionModeWrapper mode in _modes)
            {
                if (SameSize(mode, size) && !rates.Contains(mode.DmDisplayFrequency))
                {
                    rates.Add(mode.DmDisplayFrequency);
                }
            }
            rates.Sort(delegate (uint a, uint b)
            {
                bool aDefault = a <= 1;
                bool bDefault = b <= 1;
                if (aDefault != bDefault)
                {
                    return aDefault ? 1 : -1;
                }
                return b.CompareTo(a);
            });
            return rates;
        }

        public uint GetHighestRefreshRate(ResolutionSize size)
        {
            List<uint> rates = GetRefreshRates(size);
            return rates.Count > 0 ? rates[0] : 0;
        }

        // Distinct raw fixed-output values: Default, Center, Stretch, then unknown ascending.
        public List<uint> GetScalings(ResolutionSize size, uint hz)
        {
            List<uint> scalings = new List<uint>();
            foreach (ResolutionModeWrapper mode in _modes)
            {
                if (SameSize(mode, size) && mode.DmDisplayFrequency == hz && !scalings.Contains(mode.DmDisplayFixedOutput))
                {
                    scalings.Add(mode.DmDisplayFixedOutput);
                }
            }
            scalings.Sort(delegate (uint a, uint b)
            {
                int c = ScalingRank(a).CompareTo(ScalingRank(b));
                return c != 0 ? c : a.CompareTo(b);
            });
            return scalings;
        }

        // The supported-list instance matching size/hz/scaling, preferring preferredBpp, else the
        // highest bit depth available; null when nothing matches.
        public ResolutionModeWrapper Resolve(ResolutionSize size, uint hz, uint fixedOutput, uint preferredBpp)
        {
            ResolutionModeWrapper best = null;
            foreach (ResolutionModeWrapper mode in _modes)
            {
                if (!SameSize(mode, size) || mode.DmDisplayFrequency != hz || mode.DmDisplayFixedOutput != fixedOutput)
                {
                    continue;
                }
                if (mode.DmBitsPerPel == preferredBpp)
                {
                    return mode;
                }
                if (best == null || mode.DmBitsPerPel > best.DmBitsPerPel)
                {
                    best = mode;
                }
            }
            return best;
        }

        // The supported-list instance that Equals the saved mode on all five fields, or null.
        public ResolutionModeWrapper FindExact(ResolutionModeWrapper saved)
        {
            if (saved == null)
            {
                return null;
            }
            foreach (ResolutionModeWrapper mode in _modes)
            {
                if (mode.Equals(saved))
                {
                    return mode;
                }
            }
            return null;
        }

        // Starred sizes (native first, then largest first), one separator only when both
        // sections are non-empty, then the remaining sizes.
        public List<ResolutionEntry> BuildEntries()
        {
            List<ResolutionEntry> starred = new List<ResolutionEntry>();
            List<ResolutionEntry> others = new List<ResolutionEntry>();
            foreach (ResolutionSize size in Sizes)
            {
                bool native = size.Equals(Native);
                bool isStarred = IsStarred(size);
                ResolutionEntry entry = ResolutionEntry.ForSize(size, isStarred, native);
                if (native)
                {
                    starred.Insert(0, entry);
                }
                else if (isStarred)
                {
                    starred.Add(entry);
                }
                else
                {
                    others.Add(entry);
                }
            }

            List<ResolutionEntry> result = new List<ResolutionEntry>(starred);
            if (starred.Count > 0 && others.Count > 0)
            {
                result.Add(ResolutionEntry.ForSeparator());
            }
            result.AddRange(others);
            return result;
        }

        private static bool SameSize(ResolutionModeWrapper mode, ResolutionSize size)
        {
            return size != null && mode.DmPelsWidth == size.Width && mode.DmPelsHeight == size.Height;
        }

        private static int ScalingRank(uint fixedOutput)
        {
            switch (fixedOutput)
            {
                case 0: return 0;
                case 2: return 1;
                case 1: return 2;
                default: return 3;
            }
        }

        private static int CompareDescending(ResolutionSize a, ResolutionSize b)
        {
            int c = b.Width.CompareTo(a.Width);
            return c != 0 ? c : b.Height.CompareTo(a.Height);
        }
    }

    // The selection state machine behind the dialog's three combo boxes (size, refresh rate,
    // per-mode scaling). The dialog only renders it.
    internal sealed class ResolutionPicker
    {
        private const uint DefaultBpp = 32;

        private readonly ResolutionCatalog _catalog;
        private readonly List<ResolutionEntry> _entries;
        private ResolutionEntry _selected;
        private ResolutionEntry _unavailableEntry;
        private ResolutionModeWrapper _savedMode;
        private uint _refreshRate;
        private uint _scaling;
        private uint _bpp = DefaultBpp;

        public ResolutionPicker(ResolutionCatalog catalog)
        {
            _catalog = catalog;
            _entries = new List<ResolutionEntry>(catalog.BuildEntries());
        }

        public ResolutionCatalog Catalog { get { return _catalog; } }

        // The same list instance for the picker's lifetime; its contents are rebuilt in place.
        public List<ResolutionEntry> Entries { get { return _entries; } }

        public ResolutionEntry SelectedEntry { get { return _selected; } }
        public uint SelectedRefreshRate { get { return _refreshRate; } }
        public uint SelectedScaling { get { return _scaling; } }
        public bool IsSavedModeUnavailable { get { return _unavailableEntry != null; } }

        public bool CanToggleStar
        {
            get
            {
                return _unavailableEntry == null && _selected != null && !_selected.IsSeparator &&
                    _selected.Size != null && !_selected.Size.Equals(_catalog.Native);
            }
        }

        public List<ResolutionChoice> RefreshRateChoices
        {
            get
            {
                List<ResolutionChoice> choices = new List<ResolutionChoice>();
                if (_unavailableEntry != null)
                {
                    choices.Add(new ResolutionChoice(_refreshRate, ResolutionCatalog.DescribeRefreshRate(_refreshRate)));
                }
                else if (_selected != null)
                {
                    foreach (uint hz in _catalog.GetRefreshRates(_selected.Size))
                    {
                        choices.Add(new ResolutionChoice(hz, ResolutionCatalog.DescribeRefreshRate(hz)));
                    }
                }
                return choices;
            }
        }

        public List<ResolutionChoice> ScalingChoices
        {
            get
            {
                List<ResolutionChoice> choices = new List<ResolutionChoice>();
                if (_unavailableEntry != null)
                {
                    choices.Add(new ResolutionChoice(_scaling, ResolutionCatalog.DescribeScaling(_scaling)));
                }
                else if (_selected != null)
                {
                    foreach (uint value in _catalog.GetScalings(_selected.Size, _refreshRate))
                    {
                        choices.Add(new ResolutionChoice(value, ResolutionCatalog.DescribeScaling(value)));
                    }
                }
                return choices;
            }
        }

        // Selects a saved profile mode. null selects the default. A saved mode the display does
        // not offer (size, rate, scaling or bit depth) becomes an extra first entry that stays
        // selected, so merely opening and saving the dialog never rewrites the profile.
        public void Load(ResolutionModeWrapper saved)
        {
            if (saved == null)
            {
                SelectDefault();
                return;
            }

            ClearUnavailable();
            ResolutionModeWrapper exact = _catalog.FindExact(saved);
            if (exact != null)
            {
                _selected = FindSizeEntry(ResolutionSize.Of(exact));
                _refreshRate = exact.DmDisplayFrequency;
                _scaling = exact.DmDisplayFixedOutput;
                _bpp = exact.DmBitsPerPel;
                return;
            }

            _savedMode = saved;
            _unavailableEntry = ResolutionEntry.ForUnavailable(saved);
            _entries.Insert(0, _unavailableEntry);
            _selected = _unavailableEntry;
            _refreshRate = saved.DmDisplayFrequency;
            _scaling = saved.DmDisplayFixedOutput;
            _bpp = saved.DmBitsPerPel;
        }

        // Native size, highest rate, Default scaling (or the first scaling offered).
        public void SelectDefault()
        {
            ClearUnavailable();
            _bpp = DefaultBpp;
            _selected = _catalog.Native != null ? FindSizeEntry(_catalog.Native) : null;
            if (_selected == null)
            {
                _refreshRate = 0;
                _scaling = 0;
                return;
            }
            _refreshRate = _catalog.GetHighestRefreshRate(_selected.Size);
            _scaling = ChooseScaling(_selected.Size, _refreshRate, 0);
        }

        // False for null or the separator. Picking a different size resets the rate to the
        // highest and keeps the scaling where the new size offers it.
        public bool SelectEntry(ResolutionEntry entry)
        {
            if (entry == null || entry.IsSeparator)
            {
                return false;
            }
            if (entry.IsUnavailable)
            {
                return _unavailableEntry != null && ReferenceEquals(entry, _unavailableEntry);
            }

            ResolutionEntry real = FindSizeEntry(entry.Size);
            if (real == null)
            {
                return false;
            }
            if (_unavailableEntry == null && _selected != null && _selected.Size.Equals(real.Size))
            {
                _selected = real;
                return true;
            }

            ClearUnavailable();
            _selected = real;
            _bpp = DefaultBpp;
            _refreshRate = _catalog.GetHighestRefreshRate(real.Size);
            _scaling = ChooseScaling(real.Size, _refreshRate, _scaling);
            return true;
        }

        public void SelectRefreshRate(uint hz)
        {
            if (_unavailableEntry != null || _selected == null || !_catalog.GetRefreshRates(_selected.Size).Contains(hz))
            {
                return;
            }
            _refreshRate = hz;
            _scaling = ChooseScaling(_selected.Size, hz, _scaling);
        }

        public void SelectScaling(uint fixedOutput)
        {
            if (_unavailableEntry != null || _selected == null ||
                !_catalog.GetScalings(_selected.Size, _refreshRate).Contains(fixedOutput))
            {
                return;
            }
            _scaling = fixedOutput;
        }

        // Flips the star of the selected size (no-op when CanToggleStar is false), rebuilds the
        // entries and keeps the same size selected. Persisting the change is the caller's job.
        public void ToggleStar()
        {
            if (!CanToggleStar)
            {
                return;
            }
            ResolutionSize size = _selected.Size;
            _catalog.SetStarred(size, !_catalog.IsStarred(size));
            RebuildEntries();
            _selected = FindSizeEntry(size);
        }

        // The profile's mode: the supported-list instance for the selection, or, while the saved
        // mode is unavailable, the saved instance itself. Null only for an empty catalog.
        public ResolutionModeWrapper GetSelectedMode()
        {
            if (_unavailableEntry != null)
            {
                return _savedMode;
            }
            if (_selected == null)
            {
                return null;
            }
            return _catalog.Resolve(_selected.Size, _refreshRate, _scaling, _bpp);
        }

        private uint ChooseScaling(ResolutionSize size, uint hz, uint desired)
        {
            List<uint> offered = _catalog.GetScalings(size, hz);
            if (offered.Contains(desired))
            {
                return desired;
            }
            if (offered.Contains(0))
            {
                return 0;
            }
            return offered.Count > 0 ? offered[0] : 0;
        }

        private ResolutionEntry FindSizeEntry(ResolutionSize size)
        {
            foreach (ResolutionEntry entry in _entries)
            {
                if (!entry.IsSeparator && !entry.IsUnavailable && entry.Size.Equals(size))
                {
                    return entry;
                }
            }
            return null;
        }

        private void ClearUnavailable()
        {
            if (_unavailableEntry != null)
            {
                _entries.Remove(_unavailableEntry);
                _unavailableEntry = null;
                _savedMode = null;
            }
        }

        private void RebuildEntries()
        {
            _entries.Clear();
            if (_unavailableEntry != null)
            {
                _entries.Add(_unavailableEntry);
            }
            _entries.AddRange(_catalog.BuildEntries());
        }
    }
}
