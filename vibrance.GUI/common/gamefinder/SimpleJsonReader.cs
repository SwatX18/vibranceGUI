using System;
using System.Collections.Generic;
using System.Text;

namespace vibrance.GUI.common.gamefinder
{
    /// <summary>
    /// Extracts the top-level string-valued keys of a flat JSON object. Quote- and escape-aware,
    /// depth-tracked so nested objects and arrays are skipped. Pure, no dependency.
    /// </summary>
    public static class SimpleJsonReader
    {
        // Keyed case-insensitively. Returns an empty dictionary, never null; malformed input is
        // tolerated, never thrown on.
        public static Dictionary<string, string> ReadTopLevelStrings(string json)
        {
            Dictionary<string, string> values =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrEmpty(json))
                return values;

            int position = 0;
            SkipWhitespace(json, ref position);

            if (position >= json.Length || json[position] != '{')
                return values;   // not an object: there is nothing top level to read

            position++;

            while (position < json.Length)
            {
                SkipWhitespace(json, ref position);
                if (position >= json.Length)
                    break;

                char c = json[position];

                if (c == '}')
                    break;       // end of the top-level object

                if (c == ',' || c == ':')
                {
                    position++;  // a separator, or a stray one; keep going either way
                    continue;
                }

                if (c != '"')
                {
                    // Not a key. Step over whatever it is rather than abandoning the file.
                    SkipValue(json, ref position);
                    continue;
                }

                position++;
                string key = ReadString(json, ref position);

                SkipWhitespace(json, ref position);
                if (position < json.Length && json[position] == ':')
                    position++;
                SkipWhitespace(json, ref position);

                if (position >= json.Length)
                    break;       // truncated after the key: keep what was read before it

                if (json[position] == '"')
                {
                    position++;
                    string value = ReadString(json, ref position);
                    if (key.Length > 0)
                        values[key] = value;   // last one wins if a key is repeated
                }
                else
                {
                    // Nested object, array, number, bool, null: not a string, so not ours.
                    SkipValue(json, ref position);
                }
            }

            return values;
        }

        private static void SkipValue(string json, ref int position)
        {
            if (position >= json.Length)
                return;

            char c = json[position];

            if (c == '"')
            {
                position++;
                ReadString(json, ref position);
                return;
            }

            if (c == '{' || c == '[')
            {
                SkipStructure(json, ref position);
                return;
            }

            // A literal - number, true, false, null, or garbage - runs to the next separator.
            int start = position;
            while (position < json.Length)
            {
                char literal = json[position];
                if (literal == ',' || literal == '}' || literal == ']')
                    break;

                position++;
            }

            if (position == start)
                position++;   // a stray separator: consume it so the caller cannot stall
        }

        private static void SkipStructure(string json, ref int position)
        {
            int depth = 0;

            while (position < json.Length)
            {
                char c = json[position];

                if (c == '"')
                {
                    // Strings inside the skipped structure are read, not scanned, so a brace or a
                    // bracket inside one cannot throw off the depth count.
                    position++;
                    ReadString(json, ref position);
                    continue;
                }

                position++;

                if (c == '{' || c == '[')
                {
                    depth++;
                }
                else if (c == '}' || c == ']')
                {
                    depth--;
                    if (depth <= 0)
                        return;
                }
            }

            // Unterminated structure: everything left belongs to it.
        }

        private static string ReadString(string json, ref int position)
        {
            StringBuilder builder = new StringBuilder();

            while (position < json.Length)
            {
                char c = json[position];

                if (c == '"')
                {
                    position++;
                    return builder.ToString();
                }

                if (c != '\\')
                {
                    builder.Append(c);
                    position++;
                    continue;
                }

                if (position + 1 >= json.Length)
                {
                    builder.Append(c);   // a trailing backslash at the end of the input
                    position++;
                    continue;
                }

                char escaped = json[position + 1];
                position += 2;

                switch (escaped)
                {
                    case '"':
                        builder.Append('"');
                        break;
                    case '\\':
                        builder.Append('\\');
                        break;
                    case '/':
                        builder.Append('/');
                        break;
                    case 'b':
                        builder.Append('\b');
                        break;
                    case 'f':
                        builder.Append('\f');
                        break;
                    case 'n':
                        builder.Append('\n');
                        break;
                    case 'r':
                        builder.Append('\r');
                        break;
                    case 't':
                        builder.Append('\t');
                        break;
                    case 'u':
                        char decoded;
                        if (TryReadHex4(json, position, out decoded))
                        {
                            // A surrogate pair arrives as two escapes and appends as two chars,
                            // which is exactly the .NET representation of it.
                            builder.Append(decoded);
                            position += 4;
                        }
                        else
                        {
                            builder.Append('\\');
                            builder.Append(escaped);
                        }
                        break;
                    default:
                        // Unknown escape: keep both characters, so a manifest that wrote
                        // "C:\Games" instead of "C:\\Games" still yields a usable path.
                        builder.Append('\\');
                        builder.Append(escaped);
                        break;
                }
            }

            return builder.ToString();   // unterminated string: hand back what was there
        }

        private static bool TryReadHex4(string json, int position, out char value)
        {
            value = '\0';
            if (position + 4 > json.Length)
                return false;

            int code = 0;
            for (int i = 0; i < 4; i++)
            {
                int digit = HexDigit(json[position + i]);
                if (digit < 0)
                    return false;

                code = (code << 4) | digit;
            }

            value = (char)code;
            return true;
        }

        private static int HexDigit(char c)
        {
            if (c >= '0' && c <= '9')
                return c - '0';
            if (c >= 'a' && c <= 'f')
                return c - 'a' + 10;
            if (c >= 'A' && c <= 'F')
                return c - 'A' + 10;

            return -1;
        }

        private static void SkipWhitespace(string json, ref int position)
        {
            while (position < json.Length &&
                   (char.IsWhiteSpace(json[position]) || json[position] == '\uFEFF'))
            {
                position++;
            }
        }
    }
}
