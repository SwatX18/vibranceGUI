using System;
using System.Collections.Generic;
using System.Text;

namespace vibrance.GUI.common.gamefinder
{
    /// <summary>
    /// One node of a text-VDF tree. A node either carries a Value (a string-valued key) or
    /// Children (a brace-delimited block), never both.
    /// </summary>
    public class VdfNode
    {
        public VdfNode()
        {
            this.Children = new List<VdfNode>();
        }

        public VdfNode(string name)
        {
            this.Name = name;
            this.Children = new List<VdfNode>();
        }

        public string Name { get; set; }
        public string Value { get; set; }          // null for a block node
        public List<VdfNode> Children { get; set; } // empty for a string-valued node

        // Both lookups are case-insensitive; they return null when there is no such child.
        public VdfNode FindChild(string name)
        {
            if (name == null || this.Children == null)
                return null;

            for (int i = 0; i < this.Children.Count; i++)
            {
                VdfNode child = this.Children[i];
                if (child != null && child.Name != null &&
                    string.Equals(child.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return child;
                }
            }

            return null;
        }

        public string GetValue(string name)
        {
            VdfNode child = FindChild(name);
            return child == null ? null : child.Value;
        }
    }

    /// <summary>
    /// Minimal text-VDF tokeniser/parser. Handles \, \" and // comments and nested braces.
    /// Serves both .vdf and .acf. Pure: takes text, never touches the filesystem.
    /// </summary>
    public static class VdfTextReader
    {
        private enum TokenType
        {
            None,        // end of input
            String,      // a key or a value, quoted or bare
            BlockStart,  // {
            BlockEnd,    // }
            Conditional  // [$WIN32] and friends; carries no data and is ignored
        }

        // Returns a synthetic root whose Children are the top-level entries of the document.
        // Returns an empty root, never null; malformed input is tolerated, never thrown on.
        public static VdfNode Parse(string text)
        {
            VdfNode root = new VdfNode();
            if (string.IsNullOrEmpty(text))
                return root;

            // Iterative rather than recursive: a pathologically nested file must not blow the
            // stack, because this parser is never allowed to fail on input it did not write.
            Stack<VdfNode> ancestors = new Stack<VdfNode>();
            VdfNode current = root;
            string pendingKey = null;
            int position = 0;

            while (true)
            {
                string token;
                TokenType type = ReadToken(text, ref position, out token);
                if (type == TokenType.None)
                    break;

                switch (type)
                {
                    case TokenType.String:
                        if (pendingKey == null)
                        {
                            pendingKey = token;
                        }
                        else
                        {
                            // A string-valued key: Value set, Children empty. That is how a caller
                            // tells a legacy library entry from a modern brace-delimited one.
                            VdfNode leaf = new VdfNode(pendingKey);
                            leaf.Value = token;
                            current.Children.Add(leaf);
                            pendingKey = null;
                        }
                        break;

                    case TokenType.BlockStart:
                        // A brace with no key in front of it is malformed. Keep the block under an
                        // empty name rather than discarding everything nested inside it.
                        VdfNode block = new VdfNode(pendingKey ?? string.Empty);
                        current.Children.Add(block);
                        ancestors.Push(current);
                        current = block;
                        pendingKey = null;
                        break;

                    case TokenType.BlockEnd:
                        pendingKey = null;   // a key left dangling in front of } never got a value
                        if (ancestors.Count > 0)
                            current = ancestors.Pop();
                        break;
                }
            }

            // A truncated file leaves everything parsed so far in place: partial, never empty.
            return root;
        }

        private static TokenType ReadToken(string text, ref int position, out string value)
        {
            value = null;

            while (position < text.Length)
            {
                char c = text[position];

                if (char.IsWhiteSpace(c) || c == '\uFEFF')
                {
                    position++;
                    continue;
                }

                if (c == '/' && position + 1 < text.Length && text[position + 1] == '/')
                {
                    while (position < text.Length && text[position] != '\n')
                        position++;
                    continue;
                }

                break;
            }

            if (position >= text.Length)
                return TokenType.None;

            char start = text[position];

            if (start == '{')
            {
                position++;
                return TokenType.BlockStart;
            }

            if (start == '}')
            {
                position++;
                return TokenType.BlockEnd;
            }

            if (start == '[')
            {
                // Platform conditional such as [$WIN32]. Reported separately so the parser cannot
                // mistake it for the value of the key in front of it.
                position++;
                while (position < text.Length && text[position] != ']' && text[position] != '\n')
                    position++;
                if (position < text.Length && text[position] == ']')
                    position++;
                return TokenType.Conditional;
            }

            if (start == '"')
            {
                position++;
                value = ReadQuotedString(text, ref position);
                return TokenType.String;
            }

            value = ReadBareString(text, ref position);
            return TokenType.String;
        }

        private static string ReadQuotedString(string text, ref int position)
        {
            StringBuilder builder = new StringBuilder();

            while (position < text.Length)
            {
                char c = text[position];

                if (c == '"')
                {
                    position++;
                    return builder.ToString();
                }

                if (c == '\\' && position + 1 < text.Length)
                {
                    char escaped = text[position + 1];
                    position += 2;

                    switch (escaped)
                    {
                        case '\\':
                            builder.Append('\\');
                            break;
                        case '"':
                            builder.Append('"');
                            break;
                        case 'n':
                            builder.Append('\n');
                            break;
                        case 't':
                            builder.Append('\t');
                            break;
                        case 'r':
                            builder.Append('\r');
                            break;
                        default:
                            // Unknown escape: keep both characters. A hand-edited file that wrote
                            // "E:\Steam" instead of "E:\\Steam" then still yields a usable path.
                            builder.Append('\\');
                            builder.Append(escaped);
                            break;
                    }

                    continue;
                }

                builder.Append(c);
                position++;
            }

            return builder.ToString();   // unterminated string: hand back what was there
        }

        private static string ReadBareString(string text, ref int position)
        {
            int start = position;

            while (position < text.Length)
            {
                char c = text[position];
                if (char.IsWhiteSpace(c) || c == '"' || c == '{' || c == '}' || c == '[')
                    break;

                position++;
            }

            return text.Substring(start, position - start);
        }
    }
}
