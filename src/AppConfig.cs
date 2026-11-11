using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Win32;
using System.Windows.Forms;

namespace KeyLayer
{
    /// <summary>Settings stored in KeyLayer.ini next to the executable (portable).</summary>
    internal sealed class AppConfig
    {
        public readonly Dictionary<int, Mapping> Remap = new Dictionary<int, Mapping>();
        public int Opacity = 90;          // %
        public int Scale = 100;           // %
        public string Position = "Auto";
        public int ToggleVk = 0xA1;       // right Shift
        public ActivationMode Mode = ActivationMode.DoubleTap;
        public int DoubleTapMs = 400;
        public bool DisableStickyKeys = true;
        public bool Existed;

        /// <summary>"Tocca due volte Shift destro" etc., for hints and notifications.</summary>
        public string GestureText()
        {
            string key = ToggleName(ToggleVk);
            switch (Mode)
            {
                case ActivationMode.SingleTap: return "tocca " + key;
                case ActivationMode.Hold: return "tieni premuto " + key;
                default: return "tocca due volte " + key;
            }
        }

        public static string ModeName(ActivationMode m)
        {
            switch (m)
            {
                case ActivationMode.SingleTap: return "Tocco singolo (attiva / disattiva)";
                case ActivationMode.Hold: return "Tieni premuto (attivo finché lo tieni)";
                default: return "Doppio tocco (attiva / disattiva)";
            }
        }

        public static readonly int[] ToggleChoices = { 0xA1, 0xA3, 0x5D, 0x91, 0x13, 0x14 };

        public static string ToggleName(int vk)
        {
            switch (vk)
            {
                case 0xA1: return "Shift destro";
                case 0xA3: return "Ctrl destro";
                case 0x5D: return "Menu contestuale";
                case 0x91: return "Bloc Scorr";
                case 0x13: return "Pausa";
                case 0x14: return "Bloc Maiusc";
            }
            return KeyNames.Name(vk);
        }

        public static string FilePath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "KeyLayer.ini"); }
        }

        public void ApplyVimPreset()
        {
            Remap['I'] = new Mapping(0x26, 0); // Up
            Remap['J'] = new Mapping(0x25, 0); // Left
            Remap['K'] = new Mapping(0x28, 0); // Down
            Remap['L'] = new Mapping(0x27, 0); // Right
            Remap['U'] = new Mapping(0x21, 0); // Page Up
            Remap['O'] = new Mapping(0x22, 0); // Page Down
            Remap['N'] = new Mapping(0x24, 0); // Home
            Remap['M'] = new Mapping(0x23, 0); // End
        }

        static int ParseInt(string s, int def, int min, int max)
        {
            int v;
            if (!int.TryParse(s, out v)) return def;
            return Math.Max(min, Math.Min(max, v));
        }

        public static AppConfig Load()
        {
            var c = new AppConfig();
            if (!File.Exists(FilePath))
            {
                c.ApplyVimPreset();
                return c;
            }
            c.Existed = true;
            string section = "";
            foreach (string raw in File.ReadAllLines(FilePath, Encoding.UTF8))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
                if (line[0] == '[' && line[line.Length - 1] == ']')
                {
                    section = line.Substring(1, line.Length - 2).Trim().ToLowerInvariant();
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                string val = line.Substring(eq + 1).Trim();

                if (section == "settings")
                {
                    switch (key.ToLowerInvariant())
                    {
                        case "opacity": c.Opacity = ParseInt(val, 90, 20, 100); break;
                        case "scale": c.Scale = ParseInt(val, 100, 50, 250); break;
                        case "position": c.Position = val; break;
                        case "activationmode":
                            if (val.Equals("SingleTap", StringComparison.OrdinalIgnoreCase)) c.Mode = ActivationMode.SingleTap;
                            else if (val.Equals("Hold", StringComparison.OrdinalIgnoreCase)) c.Mode = ActivationMode.Hold;
                            else c.Mode = ActivationMode.DoubleTap;
                            break;
                        case "doubletapms": c.DoubleTapMs = ParseInt(val, 400, 150, 1000); break;
                        case "disablestickykeys": c.DisableStickyKeys = !(val == "0" || val.Equals("false", StringComparison.OrdinalIgnoreCase)); break;
                        case "togglekey":
                            int t;
                            if (KeyNames.TryParse(val, out t) && t != 0) c.ToggleVk = t;
                            break;
                    }
                }
                else if (section == "remap")
                {
                    int src;
                    Mapping m = Mapping.Parse(val);
                    if (m != null && KeyNames.TryParse(key, out src) && src != 0) c.Remap[src] = m;
                }
            }
            c.Remap.Remove(c.ToggleVk);
            return c;
        }

        public void Save()
        {
            var sb = new StringBuilder();
            sb.AppendLine("; KeyLayer - configurazione (si puo' modificare anche a mano, con l'app chiusa)");
            sb.AppendLine("; [Remap]  Tasto=Destinazione   es.  I=Up   U=Ctrl+Left   CapsLock=Escape   Q=None (disattiva)");
            sb.AppendLine("; Nomi: A-Z, 0-9, F1-F24, Up, Down, Left, Right, Home, End, PageUp, PageDown, Insert, Delete,");
            sb.AppendLine(";       Backspace, Enter, Escape, Tab, Space, LShift, RShift, LCtrl, RCtrl, LAlt, RAlt, LWin, Apps,");
            sb.AppendLine(";       CapsLock, VolumeUp, VolumeDown, VolumeMute, MediaPlayPause, MediaNext, MediaPrev, OEM_1..OEM_102, VK_xx");
            sb.AppendLine();
            sb.AppendLine("[Settings]");
            sb.AppendLine("ToggleKey=" + KeyNames.Name(ToggleVk));
            sb.AppendLine("; ActivationMode: DoubleTap | Hold | SingleTap");
            sb.AppendLine("ActivationMode=" + Mode);
            sb.AppendLine("DoubleTapMs=" + DoubleTapMs);
            sb.AppendLine("DisableStickyKeys=" + (DisableStickyKeys ? "1" : "0"));
            sb.AppendLine("Opacity=" + Opacity);
            sb.AppendLine("Scale=" + Scale);
            sb.AppendLine("; Position: Auto | BottomCenter | BottomLeft | BottomRight | TopCenter | TopLeft | TopRight | Center");
            sb.AppendLine("Position=" + Position);
            sb.AppendLine();
            sb.AppendLine("[Remap]");
            foreach (var kv in Remap.OrderBy(p => KeyNames.Name(p.Key), StringComparer.OrdinalIgnoreCase))
                sb.AppendLine(KeyNames.Name(kv.Key) + "=" + kv.Value.ToConfig());
            File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
        }
    }

    /// <summary>Optional "start with Windows" (HKCU Run key). Off unless the user enables it.</summary>
    internal static class Autostart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "KeyLayer";

        static string Command
        {
            get { return "\"" + Application.ExecutablePath + "\" /autostart"; }
        }

        public static bool IsEnabled()
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k != null && k.GetValue(ValueName) != null;
        }

        public static void Set(bool enabled)
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (enabled) k.SetValue(ValueName, Command);
                else k.DeleteValue(ValueName, false);
            }
        }

        /// <summary>If autostart is on but the portable folder was moved, point it to the current location.</summary>
        public static void RefreshPath()
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
            {
                if (k == null) return;
                string cur = k.GetValue(ValueName) as string;
                if (cur != null && cur != Command) k.SetValue(ValueName, Command);
            }
        }
    }
}
