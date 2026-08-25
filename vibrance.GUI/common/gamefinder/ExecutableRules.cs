using System;

namespace vibrance.GUI.common.gamefinder
{
    /// <summary>
    /// The maintained artefact: the exclusion set, the Steam appid denylist and the glob matcher.
    /// Data only. The exclusion set is transcribed measurement, not a design; changing it is a
    /// separate change with a re-run of the fixture attached.
    /// </summary>
    public static class ExecutableRules
    {
        // Any path segment of the file's directory, compared case-insensitively for exact equality.
        public static readonly string[] ExcludedDirectorySegments =
        {
            "_CommonRedist", "DirectX", "DotNet", "EasyAntiCheat", "BattlEye", "ThirdParty", "Redist"
        };

        // Matched case-insensitively against the file NAME ONLY, including its extension.
        // '*' matches any run of characters including empty. No '?', no character class,
        // no escaping.
        public static readonly string[] ExcludedFileNameGlobs =
        {
            "*setup*", "*install*", "*uninst*", "*redist*", "vc_redist*", "vcredist*", "dxsetup*",
            "*crash*", "easyanticheat*", "beservice*", "*_be.exe", "start_protected_game*",
            "*updater*", "*_server*", "*dedicated*", "*editor*", "*benchmark*",
            // vconsole*: THE load-bearing entry. Without it Counter-Strike 2 selects vconsole2.exe
            // (4.8 MB) over the correct cs2.exe (2.8 MB), because Source 2 games are thin launchers.
            // Do not remove. See game-finder-evidence.md Finding 8.
            "vconsole*", "hammer*", "*prereq*", "*webhelper*", "*subprocess*"
        };

        // Belt and braces with the globs above: Steamworks Shared is already filtered for free,
        // because all 14 of its executables are redistributables. Kept anyway (Finding 9).
        public static readonly string[] ExcludedSteamAppIds =
        {
            "228980"
        };

        // Both operands are lowered with ToLowerInvariant first.
        public static bool MatchesGlob(string text, string glob)
        {
            if (text == null || glob == null)
                return false;

            string value = text.ToLowerInvariant();
            string pattern = glob.ToLowerInvariant();

            // Greedy match with backtracking to the most recent '*'. Linear in practice and
            // allocation free, which matters because this runs once per glob per executable.
            int valueIndex = 0;
            int patternIndex = 0;
            int starPatternIndex = -1;
            int starValueIndex = 0;

            while (valueIndex < value.Length)
            {
                if (patternIndex < pattern.Length && pattern[patternIndex] == '*')
                {
                    starPatternIndex = patternIndex;
                    starValueIndex = valueIndex;
                    patternIndex++;
                }
                else if (patternIndex < pattern.Length && pattern[patternIndex] == value[valueIndex])
                {
                    patternIndex++;
                    valueIndex++;
                }
                else if (starPatternIndex >= 0)
                {
                    starValueIndex++;
                    patternIndex = starPatternIndex + 1;
                    valueIndex = starValueIndex;
                }
                else
                {
                    return false;
                }
            }

            while (patternIndex < pattern.Length && pattern[patternIndex] == '*')
                patternIndex++;

            return patternIndex == pattern.Length;
        }
    }
}
