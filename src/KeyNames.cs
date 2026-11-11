using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace KeyLayer
{
    /// <summary>Names (for the config file) and short labels (for drawing) of virtual keys.</summary>
    internal static class KeyNames
    {
        /// <summary>Pseudo-key: switches the layer off (Vim "insert mode").</summary>
        public const int LayerOff = 0x100;

        /// <summary>Flags added to a source VK for the "with Shift" / "with Ctrl" variants of a key.</summary>
        public const int SrcShift = 0x10000, SrcCtrl = 0x20000;
        public const int SrcMask = SrcShift | SrcCtrl;

        static readonly Dictionary<int, string> names = new Dictionary<int, string>();
        static readonly Dictionary<string, int> byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<int, string> labels = new Dictionary<int, string>();

        static void N(int vk, string name, string label)
        {
            if (!names.ContainsKey(vk)) names[vk] = name;
            byName[name] = vk;
            if (label != null && !labels.ContainsKey(vk)) labels[vk] = label;
        }

        static KeyNames()
        {
            N(0x08, "Backspace", "Bksp");
            N(0x09, "Tab", "Tab");
            N(0x0D, "Enter", "Enter");
            N(0x13, "Pause", "Pause");
            N(0x14, "CapsLock", "Caps");
            N(0x1B, "Escape", "Esc");
            N(0x1B, "Esc", null);
            N(0x20, "Space", "Space");
            N(0x21, "PageUp", "PgUp");
            N(0x22, "PageDown", "PgDn");
            N(0x23, "End", "End");
            N(0x24, "Home", "Home");
            N(0x25, "Left", "←");
            N(0x26, "Up", "↑");
            N(0x27, "Right", "→");
            N(0x28, "Down", "↓");
            N(0x2C, "PrintScreen", "PrtSc");
            N(0x2D, "Insert", "Ins");
            N(0x2E, "Delete", "Del");
            N(0x5B, "LWin", "Win");
            N(0x5C, "RWin", "Win");
            N(0x5D, "Apps", "Menu");
            N(0x90, "NumLock", "Num");
            N(0x91, "ScrollLock", "ScrLk");
            N(0xA0, "LShift", "Shift");
            N(0xA1, "RShift", "Shift");
            N(0xA2, "LCtrl", "Ctrl");
            N(0xA3, "RCtrl", "Ctrl");
            N(0xA4, "LAlt", "Alt");
            N(0xA5, "RAlt", "AltGr");
            N(0xA6, "BrowserBack", "Back");
            N(0xA7, "BrowserForward", "Fwd");
            N(0xAD, "VolumeMute", "Mute");
            N(0xAE, "VolumeDown", "Vol−");
            N(0xAF, "VolumeUp", "Vol+");
            N(0xB0, "MediaNext", "Next");
            N(0xB1, "MediaPrev", "Prev");
            N(0xB2, "MediaStop", "Stop");
            N(0xB3, "MediaPlayPause", "Play");
            N(0x10, "Shift", "Shift");
            N(0x11, "Ctrl", "Ctrl");
            N(0x12, "Alt", "Alt");
            N(LayerOff, "LayerOff", "esci");

            for (int i = 1; i <= 24; i++) N(0x6F + i, "F" + i, "F" + i);
            for (char c = 'A'; c <= 'Z'; c++) N(c, c.ToString(), c.ToString());
            for (char c = '0'; c <= '9'; c++) N(c, c.ToString(), c.ToString());
            for (int i = 0; i <= 9; i++) N(0x60 + i, "Num" + i, "N" + i);

            // OEM keys: their label depends on the active keyboard layout, so no fixed label.
            N(0xBA, "OEM_1", null);
            N(0xBB, "OEM_PLUS", null);
            N(0xBC, "OEM_COMMA", null);
            N(0xBD, "OEM_MINUS", null);
            N(0xBE, "OEM_PERIOD", null);
            N(0xBF, "OEM_2", null);
            N(0xC0, "OEM_3", null);
            N(0xDB, "OEM_4", null);
            N(0xDC, "OEM_5", null);
            N(0xDD, "OEM_6", null);
            N(0xDE, "OEM_7", null);
            N(0xDF, "OEM_8", null);
            N(0xE2, "OEM_102", null);
        }

        public static string Name(int vk)
        {
            if (vk == 0) return "None";
            string s;
            if (names.TryGetValue(vk, out s)) return s;
            return "VK_" + vk.ToString("X2");
        }

        public static bool TryParse(string s, out int vk)
        {
            vk = 0;
            if (string.IsNullOrEmpty(s)) return false;
            s = s.Trim();
            if (s.Equals("None", StringComparison.OrdinalIgnoreCase)) return true;
            if (byName.TryGetValue(s, out vk)) return true;
            string hex = null;
            if (s.StartsWith("VK_", StringComparison.OrdinalIgnoreCase)) hex = s.Substring(3);
            else if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = s.Substring(2);
            if (hex != null && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out vk))
                return vk > 0 && vk < 256;
            vk = 0;
            return false;
        }

        // ---- source keys: a VK plus optional SrcShift / SrcCtrl ----

        public static string SourceName(int key)
        {
            return ((key & SrcCtrl) != 0 ? "Ctrl+" : "") + ((key & SrcShift) != 0 ? "Shift+" : "") + Name(key & 0xFFFF);
        }

        public static string SourceLabel(int key, IntPtr hkl)
        {
            return ((key & SrcCtrl) != 0 ? "Ctrl+" : "") + ((key & SrcShift) != 0 ? "Shift+" : "") + Label(key & 0xFFFF, hkl);
        }

        public static bool TryParseSource(string s, out int key)
        {
            key = 0;
            if (s == null) return false;
            string[] parts = s.Split('+');
            int flags = 0;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                string p = parts[i].Trim().ToLowerInvariant();
                if (p == "shift") flags |= SrcShift;
                else if (p == "ctrl" || p == "control") flags |= SrcCtrl;
                else return false;
            }
            int vk;
            if (!TryParse(parts[parts.Length - 1], out vk) || vk == 0 || vk == LayerOff) return false;
            key = vk | flags;
            return true;
        }

        /// <summary>Short label to draw on a key cap.</summary>
        public static string Label(int vk, IntPtr hkl)
        {
            if (vk == 0) return "OFF";
            string s;
            if (labels.TryGetValue(vk, out s)) return s;
            if (vk > 0xFF) return Name(vk);
            if (hkl == IntPtr.Zero) hkl = Native.GetKeyboardLayout(0);
            uint ch = Native.MapVirtualKeyEx((uint)vk, 2 /* MAPVK_VK_TO_CHAR */, hkl) & 0xFFFF;
            if (ch != 0) return char.ToUpper((char)ch, CultureInfo.CurrentCulture).ToString();
            return Name(vk);
        }

        public static bool IsExtended(int vk)
        {
            switch (vk)
            {
                case 0x21: case 0x22: case 0x23: case 0x24:
                case 0x25: case 0x26: case 0x27: case 0x28:
                case 0x2C: case 0x2D: case 0x2E:
                case 0x5B: case 0x5C: case 0x5D:
                case 0x6F: case 0x90:
                case 0xA3: case 0xA5:
                    return true;
            }
            return vk >= 0xA6 && vk <= 0xB7; // browser / media / launch keys
        }
    }

    /// <summary>One key press of a mapping target: a key plus modifiers.</summary>
    internal struct KeyStroke
    {
        public readonly int Vk;
        public readonly int Mods;
        public KeyStroke(int vk, int mods) { Vk = vk; Mods = mods; }
    }

    /// <summary>
    /// What a remapped key turns into: one key (held as long as the source key), or a sequence of key taps,
    /// optionally ending with "LayerOff". An optional label is shown on the key cap.
    /// Config syntax:  End   |   Ctrl+Z | undo   |   Home, Shift+Down, Ctrl+X | dd
    /// </summary>
    internal sealed class Mapping
    {
        public const int ModCtrl = 1, ModShift = 2, ModAlt = 4, ModWin = 8;

        public readonly KeyStroke[] Steps;
        public readonly string Label;   // may be null

        public Mapping(int vk, int mods) : this(new[] { new KeyStroke(vk, mods) }, null) { }

        public Mapping(KeyStroke[] steps, string label)
        {
            Steps = steps;
            Label = string.IsNullOrEmpty(label) ? null : label;
        }

        /// <summary>Single key, held while the source key is held (0 = key disabled).</summary>
        public bool IsSimple { get { return Steps.Length == 1 && Steps[0].Vk != KeyNames.LayerOff; } }
        public int Vk { get { return Steps[0].Vk; } }
        public int Mods { get { return Steps[0].Mods; } }

        public static string ModPrefix(int mods)
        {
            var sb = new StringBuilder();
            if ((mods & ModCtrl) != 0) sb.Append("Ctrl+");
            if ((mods & ModShift) != 0) sb.Append("Shift+");
            if ((mods & ModAlt) != 0) sb.Append("Alt+");
            if ((mods & ModWin) != 0) sb.Append("Win+");
            return sb.ToString();
        }

        static string StepName(KeyStroke s) { return ModPrefix(s.Mods) + KeyNames.Name(s.Vk); }

        /// <summary>"Home, Shift+Down, Ctrl+X" (without the label).</summary>
        public string StepsText()
        {
            var parts = new string[Steps.Length];
            for (int i = 0; i < Steps.Length; i++) parts[i] = StepName(Steps[i]);
            return string.Join(", ", parts);
        }

        public string ToConfig() { return StepsText() + (Label != null ? " | " + Label : ""); }

        public string ShortLabel(IntPtr hkl)
        {
            if (Label != null) return Label;
            KeyStroke s = Steps[0];
            if (Steps.Length == 1 && s.Vk == 0 && s.Mods == 0) return "OFF";
            string p = ModPrefix(s.Mods);
            string first = s.Vk == 0 ? p.TrimEnd('+') : p + KeyNames.Label(s.Vk, hkl);
            return Steps.Length > 1 ? first + "…" : first;
        }

        public string Describe(IntPtr hkl)
        {
            KeyStroke s = Steps[0];
            string body;
            if (Steps.Length == 1 && s.Vk == 0 && s.Mods == 0) body = "(tasto disattivato)";
            else if (Steps.Length == 1 && s.Vk != KeyNames.LayerOff)
            {
                string p = ModPrefix(s.Mods);
                if (s.Vk == 0) body = p.TrimEnd('+');
                else
                {
                    string n = KeyNames.Name(s.Vk), l = KeyNames.Label(s.Vk, hkl);
                    body = p + (l == n ? n : l + "   (" + n + ")");
                }
            }
            else body = StepsText();
            return Label != null ? Label + "  —  " + body : body;
        }

        static bool TryParseStep(string s, out KeyStroke step)
        {
            step = new KeyStroke();
            string[] parts = s.Split('+');
            int mods = 0;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                string p = parts[i].Trim().ToLowerInvariant();
                if (p == "ctrl" || p == "control") mods |= ModCtrl;
                else if (p == "shift") mods |= ModShift;
                else if (p == "alt") mods |= ModAlt;
                else if (p == "win") mods |= ModWin;
                else return false;
            }
            int vk;
            if (!KeyNames.TryParse(parts[parts.Length - 1], out vk)) return false;
            step = new KeyStroke(vk, mods);
            return true;
        }

        /// <summary>Parses "Ctrl+Z", "End, Enter, LayerOff | o", ... Returns null when invalid.</summary>
        public static Mapping Parse(string s)
        {
            if (s == null) return null;
            string label = null;
            int bar = s.IndexOf('|');
            if (bar >= 0) { label = s.Substring(bar + 1).Trim(); s = s.Substring(0, bar); }
            if (s.Trim().Length == 0) return null;
            var steps = new List<KeyStroke>();
            foreach (string part in s.Split(','))
            {
                if (part.Trim().Length == 0) return null;
                KeyStroke st;
                if (!TryParseStep(part.Trim(), out st)) return null;
                steps.Add(st);
            }
            return new Mapping(steps.ToArray(), label);
        }
    }
}
