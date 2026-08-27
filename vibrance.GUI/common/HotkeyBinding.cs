using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace vibrance.GUI.common
{
    /// <summary>
    /// A parsed global hotkey binding: the RegisterHotKey modifier bits (already including
    /// MOD_NOREPEAT - see HotkeyBindingParser.TryParse) plus the virtual-key code. Pure data - no
    /// P/Invoke, no System.Runtime.InteropServices - so ProfileToggleFixture can construct and
    /// compare these freely with no registrar seam involved at all.
    /// </summary>
    internal struct HotkeyBinding
    {
        internal uint Modifiers;
        internal uint VirtualKey;
        internal bool IsSet;

        // Same as the struct default (IsSet false) - written out as its own property so a caller
        // never has to spell "default(HotkeyBinding)" or "new HotkeyBinding()" to mean "no
        // binding configured".
        internal static HotkeyBinding None
        {
            get { return new HotkeyBinding(); }
        }
    }

    /// <summary>
    /// Parses/formats a HotkeyBinding to and from its canonical text, "Ctrl+Alt+Shift+Win+
    /// &lt;KeyName&gt;" - modifiers always in that fixed order, &lt;KeyName&gt; the Keys enum's
    /// own name (e.g. "F9", "D1"). Round-trips: Format(TryParse(x)) == x for every canonical
    /// string TryParse can produce.
    /// </summary>
    internal static class HotkeyBindingParser
    {
        // MOD_* (user32.h) - RegisterHotKey's own modifier bits. Not pulled from a shared Win32
        // constants file, because this codebase does not have one.
        private const uint ModAlt = 0x0001;
        private const uint ModControl = 0x0002;
        private const uint ModShift = 0x0004;
        private const uint ModWin = 0x0008;

        // MOD_NOREPEAT (user32.h, Vista+) - always OR'd into a parsed binding's Modifiers, never
        // represented in the formatted text (see Format below). Without it, holding the key down
        // fires WM_HOTKEY dozens of times a second and ToggleForegroundProfile flips the matched
        // game's suppression state on every one of them.
        internal const uint ModNoRepeat = 0x4000;

        private static readonly char[] Separator = { '+' };

        /// <summary>
        /// Parses text into binding. False (binding set to HotkeyBinding.None) for null, empty,
        /// a binding with no key at all, an unrecognised token (never silently dropped), or a
        /// purely numeric key token (Enum.TryParse&lt;Keys&gt;("1") would otherwise succeed as
        /// Keys.LButton). Case-insensitive.
        /// </summary>
        internal static bool TryParse(string text, out HotkeyBinding binding)
        {
            binding = HotkeyBinding.None;
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            string[] tokens = text.Split(Separator, StringSplitOptions.None);
            uint modifiers = 0;
            Keys? key = null;

            foreach (string rawToken in tokens)
            {
                string token = rawToken.Trim();
                if (token.Length == 0)
                {
                    // Catches both a trailing separator ("Ctrl+") and a doubled one.
                    return false;
                }

                if (string.Equals(token, "Ctrl", StringComparison.OrdinalIgnoreCase))
                {
                    modifiers |= ModControl;
                }
                else if (string.Equals(token, "Alt", StringComparison.OrdinalIgnoreCase))
                {
                    modifiers |= ModAlt;
                }
                else if (string.Equals(token, "Shift", StringComparison.OrdinalIgnoreCase))
                {
                    modifiers |= ModShift;
                }
                else if (string.Equals(token, "Win", StringComparison.OrdinalIgnoreCase))
                {
                    modifiers |= ModWin;
                }
                else
                {
                    if (key != null)
                    {
                        // A second non-modifier token - "Ctrl+F9+F10" - is an error, not a silent
                        // overwrite of the first.
                        return false;
                    }

                    int numericProbe;
                    if (int.TryParse(token, out numericProbe))
                    {
                        // Enum.TryParse<Keys>("1") succeeds and yields Keys.LButton, since 1 is
                        // that value's underlying int - a purely numeric token is never a valid
                        // key name on its own. "D1"/"NumPad1" name the actual digit keys and are
                        // unaffected by this guard.
                        return false;
                    }

                    Keys parsedKey;
                    if (!Enum.TryParse(token, true, out parsedKey) || !Enum.IsDefined(typeof(Keys), parsedKey))
                    {
                        // An unrecognised token is an error - never silently dropped.
                        return false;
                    }

                    // Keys packs modifier flags (Keys.Control, Keys.Shift, ...) into the same
                    // enum alongside KeyCode - masked off here so a key token can never smuggle a
                    // modifier bit past the fixed-order Modifiers this method already built above.
                    key = parsedKey & Keys.KeyCode;
                }
            }

            if (key == null)
            {
                // Modifiers with no key at all ("Ctrl", "Ctrl+Alt") is not a binding.
                return false;
            }

            HotkeyBinding parsed = new HotkeyBinding();
            parsed.Modifiers = modifiers | ModNoRepeat;
            parsed.VirtualKey = (uint)key.Value;
            parsed.IsSet = true;
            binding = parsed;
            return true;
        }

        /// <summary>
        /// The canonical text for binding, or "" for HotkeyBinding.None. Modifiers always appear
        /// in the fixed Ctrl/Alt/Shift/Win order, regardless of the order TryParse originally
        /// read them in; MOD_NOREPEAT is never represented here - see its own comment above.
        /// </summary>
        internal static string Format(HotkeyBinding binding)
        {
            if (!binding.IsSet)
            {
                return string.Empty;
            }

            List<string> parts = new List<string>();
            if ((binding.Modifiers & ModControl) != 0)
            {
                parts.Add("Ctrl");
            }
            if ((binding.Modifiers & ModAlt) != 0)
            {
                parts.Add("Alt");
            }
            if ((binding.Modifiers & ModShift) != 0)
            {
                parts.Add("Shift");
            }
            if ((binding.Modifiers & ModWin) != 0)
            {
                parts.Add("Win");
            }
            parts.Add(((Keys)binding.VirtualKey).ToString());

            return string.Join("+", parts.ToArray());
        }
    }
}
