using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace vibrance.GUI.common
{
    // Pure text patcher behind the "sync CS2 video settings" option (issue #61). It edits the four
    // resolution/refresh values of a cs2_video.txt (Valve KeyValues) and nothing else: only the text
    // INSIDE the value quotes of those keys is replaced, every other byte - BOM, line endings,
    // comments, unknown keys, whitespace - comes back identical. Missing keys are inserted before the
    // root's closing brace in the file's own indent / separator / newline style.
    //
    // No file access here: the caller decodes the file as Latin-1 (a lossless byte <-> char mapping,
    // so the round trip cannot alter a byte) and hands the text in. Cs2VideoSettingsWriter does the I/O.
    //
    // Key names are compared ordinal, case-insensitive. Only quoted key/value pairs directly inside
    // the root block (depth 1) are candidates; a key of the same name inside a nested block is left
    // alone. Duplicates of an owned key at depth 1 are all replaced.

    internal enum Cs2VideoPatchStatus
    {
        Patched,
        Unchanged,
        Malformed
    }

    internal sealed class Cs2VideoPatchResult
    {
        internal Cs2VideoPatchResult(Cs2VideoPatchStatus status, string content)
        {
            Status = status;
            Content = content;
        }

        internal Cs2VideoPatchStatus Status { get; private set; }

        // Null when Malformed; equal to the input when Unchanged.
        internal string Content { get; private set; }
    }

    internal static class Cs2VideoConfigPatcher
    {
        internal const string KeyWidth = "setting.defaultres";
        internal const string KeyHeight = "setting.defaultresheight";
        internal const string KeyRefreshNumerator = "setting.refreshrate_numerator";
        internal const string KeyRefreshDenominator = "setting.refreshrate_denominator";

        private const string DefaultNewline = "\r\n";
        private const string DefaultIndent = "\t";
        private const string DefaultSeparator = "\t\t";

        private enum TokenKind
        {
            Quoted,
            Bare,
            Open,
            Close
        }

        // Start is the index of the opening quote / brace, End the index just past the closing one.
        private struct Token
        {
            internal TokenKind Kind;
            internal int Start;
            internal int End;
        }

        private sealed class Pair
        {
            internal Token Key;
            internal Token Value;
        }

        private sealed class Edit
        {
            internal int Position;
            internal int RemoveLength;
            internal string Text;
        }

        // refreshHz <= 1 means "no usable refresh rate" (0 = unknown, 1 = the hardware-default
        // marker): the refresh pair is then neither rewritten nor inserted.
        internal static Cs2VideoPatchResult Patch(string content, uint width, uint height, uint refreshHz)
        {
            List<Token> tokens;
            if (string.IsNullOrEmpty(content) || !Tokenize(content, out tokens))
                return Malformed();

            bool touchRefresh = refreshHz > 1;

            // Root block: first '{' opens it, its matching '}' closes it. Whatever precedes the
            // '{' (the "video.cfg" name, a BOM read as Latin-1) is ignored.
            int rootOpen = -1;
            for (int i = 0; i < tokens.Count; i++)
            {
                if (tokens[i].Kind == TokenKind.Open)
                {
                    rootOpen = i;
                    break;
                }
            }
            if (rootOpen < 0)
                return Malformed();

            List<Pair> pairs = new List<Pair>();
            bool haveFirstKey = false;
            Token firstKey = new Token();
            int rootClose = -1;
            int depth = 1;
            bool havePending = false;
            Token pending = new Token();

            for (int i = rootOpen + 1; i < tokens.Count && rootClose < 0; i++)
            {
                Token t = tokens[i];
                switch (t.Kind)
                {
                    case TokenKind.Quoted:
                        if (depth != 1)
                            break;
                        if (!havePending)
                        {
                            pending = t;
                            havePending = true;
                            if (!haveFirstKey)
                            {
                                firstKey = t;
                                haveFirstKey = true;
                            }
                        }
                        else
                        {
                            Pair pair = new Pair();
                            pair.Key = pending;
                            pair.Value = t;
                            pairs.Add(pair);
                            havePending = false;
                        }
                        break;

                    case TokenKind.Bare:
                        if (depth == 1)
                        {
                            // An unquoted word where a KEY should be (Version "16", #base "x"): the
                            // quoted token after it would become the next key and every pair after
                            // it would pair out of step. Refuse rather than guess.
                            if (!havePending)
                                return Malformed();
                            // An unquoted word where a value should be. Harmless for keys we do not own.
                            if (IsOwned(Unquote(content, pending), touchRefresh))
                                return Malformed();
                        }
                        havePending = false;
                        break;

                    case TokenKind.Open:
                        // A key followed by a block ("name" { ... }) is not a value pair; an owned
                        // key whose value is a block is a shape we refuse to guess about.
                        if (depth == 1 && havePending && IsOwned(Unquote(content, pending), touchRefresh))
                            return Malformed();
                        havePending = false;
                        depth++;
                        break;

                    case TokenKind.Close:
                        if (depth == 1 && havePending && IsOwned(Unquote(content, pending), touchRefresh))
                            return Malformed();
                        havePending = false;
                        depth--;
                        if (depth == 0)
                            rootClose = i;
                        break;
                }
            }

            if (rootClose < 0)
                return Malformed();

            string widthText = width.ToString(CultureInfo.InvariantCulture);
            string heightText = height.ToString(CultureInfo.InvariantCulture);

            List<Pair> widthPairs = Find(content, pairs, KeyWidth);
            List<Pair> heightPairs = Find(content, pairs, KeyHeight);
            List<Pair> numPairs = Find(content, pairs, KeyRefreshNumerator);
            List<Pair> denPairs = Find(content, pairs, KeyRefreshDenominator);

            List<Edit> edits = new List<Edit>();
            List<string[]> inserts = new List<string[]>();   // {key, value}, in canonical order

            Apply(content, widthPairs, KeyWidth, widthText, edits, inserts);
            Apply(content, heightPairs, KeyHeight, heightText, edits, inserts);

            if (touchRefresh)
            {
                string existingNum = numPairs.Count > 0 ? ValueText(content, numPairs[0]) : null;
                string existingDen = denPairs.Count > 0 ? ValueText(content, denPairs[0]) : null;
                string num;
                string den;
                // One key of the pair missing is never "current": ShouldKeepRefreshPair gets a null.
                if (!ShouldKeepRefreshPair(existingNum, existingDen, refreshHz, out num, out den))
                {
                    Apply(content, numPairs, KeyRefreshNumerator, num, edits, inserts);
                    Apply(content, denPairs, KeyRefreshDenominator, den, edits, inserts);
                }
            }

            if (inserts.Count > 0)
            {
                string newline = DetectNewline(content);
                string indent = DetectIndent(content, haveFirstKey ? (int?)firstKey.Start : null);
                string separator = DetectSeparator(content, pairs);
                edits.Add(BuildInsert(content, tokens[rootClose].Start, inserts, newline, indent, separator));
            }

            if (edits.Count == 0)
                return new Cs2VideoPatchResult(Cs2VideoPatchStatus.Unchanged, content);

            edits.Sort(delegate(Edit a, Edit b) { return a.Position.CompareTo(b.Position); });

            StringBuilder sb = new StringBuilder(content.Length + 160);
            int cursor = 0;
            for (int i = 0; i < edits.Count; i++)
            {
                sb.Append(content, cursor, edits[i].Position - cursor);
                sb.Append(edits[i].Text);
                cursor = edits[i].Position + edits[i].RemoveLength;
            }
            sb.Append(content, cursor, content.Length - cursor);

            string result = sb.ToString();
            if (string.Equals(result, content, StringComparison.Ordinal))
                return new Cs2VideoPatchResult(Cs2VideoPatchStatus.Unchanged, content);
            return new Cs2VideoPatchResult(Cs2VideoPatchStatus.Patched, result);
        }

        // CS2 stores the refresh rate as a fraction and the game's own writer produces values such
        // as 260002/1000 for a "260 Hz" mode. The existing pair is kept when it already rounds to the
        // requested rate (so a save does not churn the file, and the exact fraction survives); anything
        // else - different rate, missing, non-numeric, zero denominator - is rewritten as
        // refreshHz*1000 / 1000. refreshHz <= 1 means "no refresh rate to apply": the existing values
        // (possibly null) are handed back untouched and true is returned.
        internal static bool ShouldKeepRefreshPair(string existingNumerator, string existingDenominator, uint refreshHz,
            out string numerator, out string denominator)
        {
            if (refreshHz <= 1)
            {
                numerator = existingNumerator;
                denominator = existingDenominator;
                return true;
            }

            long num;
            long den;
            if (long.TryParse(existingNumerator, NumberStyles.Integer, CultureInfo.InvariantCulture, out num) &&
                long.TryParse(existingDenominator, NumberStyles.Integer, CultureInfo.InvariantCulture, out den) &&
                den > 0 && num >= 0 && num <= long.MaxValue / 2 &&
                (num + den / 2) / den == (long)refreshHz)
            {
                numerator = existingNumerator;
                denominator = existingDenominator;
                return true;
            }

            numerator = ((long)refreshHz * 1000).ToString(CultureInfo.InvariantCulture);
            denominator = "1000";
            return false;
        }

        // ---- editing helpers -----------------------------------------------------------------

        private static Cs2VideoPatchResult Malformed()
        {
            return new Cs2VideoPatchResult(Cs2VideoPatchStatus.Malformed, null);
        }

        private static bool IsOwned(string key, bool touchRefresh)
        {
            if (string.Equals(key, KeyWidth, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, KeyHeight, StringComparison.OrdinalIgnoreCase))
                return true;
            // The refresh keys only count as owned when we would touch them, so a file with a
            // strange refresh entry still gets its resolution synced when there is no rate to apply.
            return touchRefresh &&
                   (string.Equals(key, KeyRefreshNumerator, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(key, KeyRefreshDenominator, StringComparison.OrdinalIgnoreCase));
        }

        private static string Unquote(string content, Token quoted)
        {
            return content.Substring(quoted.Start + 1, quoted.End - quoted.Start - 2);
        }

        private static string ValueText(string content, Pair pair)
        {
            return Unquote(content, pair.Value);
        }

        private static List<Pair> Find(string content, List<Pair> pairs, string key)
        {
            List<Pair> found = new List<Pair>();
            for (int i = 0; i < pairs.Count; i++)
            {
                if (string.Equals(Unquote(content, pairs[i].Key), key, StringComparison.OrdinalIgnoreCase))
                    found.Add(pairs[i]);
            }
            return found;
        }

        // Replace the text inside the value quotes of every existing pair, or queue an insert.
        private static void Apply(string content, List<Pair> existing, string key, string value,
            List<Edit> edits, List<string[]> inserts)
        {
            if (existing.Count == 0)
            {
                inserts.Add(new string[] { key, value });
                return;
            }

            for (int i = 0; i < existing.Count; i++)
            {
                if (string.Equals(ValueText(content, existing[i]), value, StringComparison.Ordinal))
                    continue;
                Edit edit = new Edit();
                edit.Position = existing[i].Value.Start + 1;
                edit.RemoveLength = existing[i].Value.End - existing[i].Value.Start - 2;
                edit.Text = value;
                edits.Add(edit);
            }
        }

        private static string DetectNewline(string content)
        {
            // The first line break decides: CRLF, LF, or a lone CR (old Mac style).
            int brk = content.IndexOfAny(new char[] { '\r', '\n' });
            if (brk < 0)
                return DefaultNewline;
            if (content[brk] == '\n')
                return "\n";
            return brk + 1 < content.Length && content[brk + 1] == '\n' ? "\r\n" : "\r";
        }

        // Whitespace in front of the first depth-1 key, if that key starts its line.
        private static string DetectIndent(string content, int? firstKeyStart)
        {
            if (!firstKeyStart.HasValue)
                return DefaultIndent;
            int i = firstKeyStart.Value;
            while (i > 0 && (content[i - 1] == ' ' || content[i - 1] == '\t'))
                i--;
            if (i > 0 && content[i - 1] != '\n' && content[i - 1] != '\r')
                return DefaultIndent;
            string indent = content.Substring(i, firstKeyStart.Value - i);
            return indent.Length == 0 ? DefaultIndent : indent;
        }

        // Text between the first pair's key and value quotes, if it is plain spaces/tabs.
        private static string DetectSeparator(string content, List<Pair> pairs)
        {
            if (pairs.Count == 0)
                return DefaultSeparator;
            string between = content.Substring(pairs[0].Key.End, pairs[0].Value.Start - pairs[0].Key.End);
            if (between.Length == 0)
                return DefaultSeparator;
            for (int i = 0; i < between.Length; i++)
            {
                if (between[i] != ' ' && between[i] != '\t')
                    return DefaultSeparator;
            }
            return between;
        }

        private static Edit BuildInsert(string content, int closePosition, List<string[]> inserts,
            string newline, string indent, string separator)
        {
            // Only whitespace in front of the '}' on its line: insert at the start of that line.
            // Otherwise start a fresh line first.
            int lineStart = closePosition;
            while (lineStart > 0 && (content[lineStart - 1] == ' ' || content[lineStart - 1] == '\t'))
                lineStart--;
            bool ownLine = lineStart == 0 || content[lineStart - 1] == '\n' || content[lineStart - 1] == '\r';

            StringBuilder sb = new StringBuilder();
            if (!ownLine)
                sb.Append(newline);
            for (int i = 0; i < inserts.Count; i++)
            {
                sb.Append(indent).Append('"').Append(inserts[i][0]).Append('"')
                  .Append(separator)
                  .Append('"').Append(inserts[i][1]).Append('"').Append(newline);
            }

            Edit edit = new Edit();
            edit.Position = ownLine ? lineStart : closePosition;
            edit.RemoveLength = 0;
            edit.Text = sb.ToString();
            return edit;
        }

        // ---- tokenizer -----------------------------------------------------------------------

        // Quoted strings (backslash escapes the next char), bare words, '{', '}', and // line comments
        // (skipped). Bare words must be tokens: otherwise an owned key with an unquoted value would
        // pair up with the NEXT line's key. False only for an unterminated quote.
        private static bool Tokenize(string s, out List<Token> tokens)
        {
            tokens = new List<Token>();
            int i = 0;
            while (i < s.Length)
            {
                char c = s[i];
                if (c == '"')
                {
                    int j = i + 1;
                    bool closed = false;
                    while (j < s.Length)
                    {
                        if (s[j] == '\\')
                        {
                            j += 2;
                            continue;
                        }
                        if (s[j] == '"')
                        {
                            closed = true;
                            break;
                        }
                        j++;
                    }
                    if (!closed)
                        return false;
                    Token t = new Token();
                    t.Kind = TokenKind.Quoted;
                    t.Start = i;
                    t.End = j + 1;
                    tokens.Add(t);
                    i = j + 1;
                }
                else if (c == '{' || c == '}')
                {
                    Token t = new Token();
                    t.Kind = c == '{' ? TokenKind.Open : TokenKind.Close;
                    t.Start = i;
                    t.End = i + 1;
                    tokens.Add(t);
                    i++;
                }
                else if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
                {
                    while (i < s.Length && s[i] != '\n' && s[i] != '\r')
                        i++;
                }
                else if (char.IsWhiteSpace(c))
                {
                    i++;
                }
                else
                {
                    int j = i;
                    while (j < s.Length && !char.IsWhiteSpace(s[j]) && s[j] != '"' && s[j] != '{' && s[j] != '}')
                        j++;
                    Token t = new Token();
                    t.Kind = TokenKind.Bare;
                    t.Start = i;
                    t.End = j;
                    tokens.Add(t);
                    i = j;
                }
            }
            return true;
        }
    }
}
