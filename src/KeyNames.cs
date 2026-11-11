using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace KeyLayer
{
    /// <summary>Names (for the config file) and short labels (for drawing) of virtual keys.</summary>
    internal static class KeyNames
    {
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

        /// <summary>Short label to draw on a key cap.</summary>
        public static string Label(int vk, IntPtr hkl)
        {
            if (vk == 0) return "OFF";
            string s;
            if (labels.TryGetValue(vk, out s)) return s;
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

    /// <summary>What a remapped key turns into: a key plus optional modifiers.</summary>
    internal sealed class Mapping
    {
        public const int ModCtrl = 1, ModShift = 2, ModAlt = 4, ModWin = 8;

        public readonly int Vk;    // 0 = key disabled
        public readonly int Mods;

        public Mapping(int vk, int mods) { Vk = vk; Mods = mods; }

        string ModPrefix()
        {
            var sb = new StringBuilder();
            if ((Mods & ModCtrl) != 0) sb.Append("Ctrl+");
            if ((Mods & ModShift) != 0) sb.Append("Shift+");
            if ((Mods & ModAlt) != 0) sb.Append("Alt+");
            if ((Mods & ModWin) != 0) sb.Append("Win+");
            return sb.ToString();
        }

        public string ToConfig() { return ModPrefix() + KeyNames.Name(Vk); }

        public string ShortLabel(IntPtr hkl)
        {
            if (Vk == 0 && Mods == 0) return "OFF";
            string p = ModPrefix();
            if (Vk == 0) return p.TrimEnd('+');
            return p + KeyNames.Label(Vk, hkl);
        }

        public string Describe(IntPtr hkl)
        {
            if (Vk == 0 && Mods == 0) return "(tasto disattivato)";
            string p = ModPrefix();
            if (Vk == 0) return p.TrimEnd('+');
            string n = KeyNames.Name(Vk), l = KeyNames.Label(Vk, hkl);
            return p + (l == n ? n : l + "   (" + n + ")");
        }

        public static Mapping Parse(string s)
        {
            if (s == null || s.Trim().Length == 0) return null;
            string[] parts = s.Split('+');
            int mods = 0;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                string p = parts[i].Trim().ToLowerInvariant();
                if (p == "ctrl" || p == "control") mods |= ModCtrl;
                else if (p == "shift") mods |= ModShift;
                else if (p == "alt") mods |= ModAlt;
                else if (p == "win") mods |= ModWin;
                else return null;
            }
            int vk;
            if (!KeyNames.TryParse(parts[parts.Length - 1], out vk)) return null;
            return new Mapping(vk, mods);
        }
    }
}
