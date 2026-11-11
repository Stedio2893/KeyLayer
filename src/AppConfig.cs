using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Win32;
using System.Windows.Forms;

namespace KeyLayer
{
    /// <summary>A named set of remappings. Keys are source VKs, optionally with KeyNames.SrcShift / SrcCtrl.</summary>
    internal sealed class Profile
    {
        public string Name;
        public readonly Dictionary<int, Mapping> Remap = new Dictionary<int, Mapping>();
        public Profile(string name) { Name = name; }
    }

    /// <summary>Ready-made profiles that can be loaded from the settings window.</summary>
    internal static class ProfileTemplates
    {
        public const string Ijkl = "Navigazione IJKL";
        public const string Vim = "Vim";

        public static readonly string[] Names = { Vim, Ijkl };

        public static string Description(string name)
        {
            if (name == Vim) return "Vim (hjkl, w/b, gg/G, x, u, dd, yy, p, i/a/o…)";
            if (name == Ijkl) return "Navigazione IJKL (frecce, U/O pagina, N/M inizio/fine)";
            return name;
        }

        static readonly string[] IjklLines =
        {
            "I=Up", "J=Left", "K=Down", "L=Right",
            "U=PageUp", "O=PageDown", "N=Home", "M=End",
        };

        // Vim "normal mode" approximated with Windows editing keys. The layer is normal mode;
        // i / a / o / A / I / O switch the layer off, i.e. go back to typing ("insert mode").
        static readonly string[] VimLines =
        {
            // movement
            "H=Left", "J=Down", "K=Up", "L=Right",
            "W=Ctrl+Right | par→", "E=Ctrl+Right | par→", "B=Ctrl+Left | ←par",
            "0=Home | inizio", "Shift+4=End | fine",
            "G=Ctrl+Home | doc↑", "Shift+G=Ctrl+End | doc↓",
            "Ctrl+F=PageDown | pag↓", "Ctrl+B=PageUp | pag↑", "Ctrl+D=PageDown | pag↓", "Ctrl+U=PageUp | pag↑",
            // editing
            "X=Delete | canc", "Shift+X=Backspace | bksp",
            "U=Ctrl+Z | undo", "Ctrl+R=Ctrl+Y | redo",
            "D=Home, Shift+Down, Ctrl+X | taglia", "Shift+D=Shift+End, Delete | canc→",
            "Y=Home, Shift+Down, Ctrl+C, Left | copia",
            "P=Down, Home, Ctrl+V | inc↓", "Shift+P=Home, Ctrl+V | inc↑",
            "V=Home, Shift+End | riga",
            // search (/ is Shift+7 on the Italian layout)
            "Shift+7=Ctrl+F | cerca", "N=F3 | succ", "Shift+N=Shift+F3 | prec",
            // back to typing
            "I=LayerOff | ins", "A=Right, LayerOff | app",
            "Shift+I=Home, LayerOff | ←ins", "Shift+A=End, LayerOff | ins→",
            "O=End, Enter, LayerOff | riga↓", "Shift+O=Home, Enter, Up, LayerOff | riga↑",
            "Shift+C=Shift+End, Delete, LayerOff | camb→",
        };

        public static Profile Build(string name)
        {
            var p = new Profile(name);
            string[] lines = name == Vim ? VimLines : IjklLines;
            foreach (string line in lines)
            {
                int eq = line.IndexOf('=');
                int src;
                Mapping m = Mapping.Parse(line.Substring(eq + 1));
                if (m != null && KeyNames.TryParseSource(line.Substring(0, eq).Trim(), out src)) p.Remap[src] = m;
            }
            return p;
        }
    }

    /// <summary>Settings stored in KeyLayer.ini next to the executable (portable).</summary>
    internal sealed class AppConfig
    {
        public const string DefaultProfileName = "Personale";

        public readonly List<Profile> Profiles = new List<Profile>();
        public string ActiveProfileName = DefaultProfileName;
        public int Opacity = 90;          // %
        public int Scale = 100;           // %
        public string Position = "Auto";
        public int ToggleVk = 0xA1;       // right Shift
        public ActivationMode Mode = ActivationMode.DoubleTap;
        public int DoubleTapMs = 400;
        public bool DisableStickyKeys = true;
        public bool Existed;

        public Profile Active
        {
            get
            {
                Profile p = Find(ActiveProfileName);
                if (p == null)
                {
                    if (Profiles.Count == 0) Profiles.Add(new Profile(DefaultProfileName));
                    p = Profiles[0];
                    ActiveProfileName = p.Name;
                }
                return p;
            }
        }

        /// <summary>Remappings of the active profile.</summary>
        public Dictionary<int, Mapping> Remap { get { return Active.Remap; } }

        public Profile Find(string name)
        {
            foreach (Profile p in Profiles)
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p;
            return null;
        }

        /// <summary>Loads a ready-made profile: replaces a profile with the same name, or adds it. Makes it active.</summary>
        public Profile LoadTemplate(string templateName)
        {
            Profile fresh = ProfileTemplates.Build(templateName);
            fresh.Remap.Remove(ToggleVk);
            Profile existing = Find(templateName);
            if (existing != null) Profiles[Profiles.IndexOf(existing)] = fresh;
            else Profiles.Add(fresh);
            ActiveProfileName = fresh.Name;
            return fresh;
        }

        public static bool IsValidProfileName(string name)
        {
            return !string.IsNullOrEmpty(name) && name.Trim().Length > 0 && name.IndexOfAny(new[] { '[', ']', '=', ';', '#' }) < 0;
        }

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
                Profile first = ProfileTemplates.Build(ProfileTemplates.Ijkl);
                first.Name = DefaultProfileName;
                c.Profiles.Add(first);
                return c;
            }
            c.Existed = true;
            string section = "";
            Profile current = null;
            foreach (string raw in File.ReadAllLines(FilePath, Encoding.UTF8))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
                if (line[0] == '[' && line[line.Length - 1] == ']')
                {
                    string name = line.Substring(1, line.Length - 2).Trim();
                    section = name.ToLowerInvariant();
                    current = null;
                    if (section == "remap") current = c.GetOrAdd(DefaultProfileName);           // pre-profile files
                    else if (section.StartsWith("profile:")) current = c.GetOrAdd(name.Substring(8).Trim());
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
                        case "profile": c.ActiveProfileName = val; break;
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
                else if (current != null)
                {
                    int src;
                    Mapping m = Mapping.Parse(val);
                    if (m != null && KeyNames.TryParseSource(key, out src)) current.Remap[src] = m;
                }
            }
            if (c.Profiles.Count == 0) c.Profiles.Add(new Profile(DefaultProfileName));
            foreach (Profile p in c.Profiles) p.Remap.Remove(c.ToggleVk);
            if (c.Find(c.ActiveProfileName) == null) c.ActiveProfileName = c.Profiles[0].Name;
            return c;
        }

        Profile GetOrAdd(string name)
        {
            Profile p = Find(name);
            if (p == null) { p = new Profile(name); Profiles.Add(p); }
            return p;
        }

        public void Save()
        {
            var sb = new StringBuilder();
            sb.AppendLine("; KeyLayer - configurazione (si puo' modificare anche a mano, con l'app chiusa)");
            sb.AppendLine("; Ogni [Profile:Nome] contiene le rimappature di un profilo; Profile= sceglie quello attivo.");
            sb.AppendLine("; Riga:  Tasto=Destinazione   es.  I=Up   Shift+G=Ctrl+End   Q=None (disattiva)");
            sb.AppendLine(";   Tasto con varianti:  Shift+X, Ctrl+F (valgono solo con quel modificatore premuto)");
            sb.AppendLine(";   Sequenza ed etichetta:  D=Home, Shift+Down, Ctrl+X | dd     O=End, Enter, LayerOff | o");
            sb.AppendLine(";   LayerOff = disattiva il layer (come tornare in insert mode in Vim)");
            sb.AppendLine("; Nomi: A-Z, 0-9, F1-F24, Up, Down, Left, Right, Home, End, PageUp, PageDown, Insert, Delete,");
            sb.AppendLine(";       Backspace, Enter, Escape, Tab, Space, LShift, RShift, LCtrl, RCtrl, LAlt, RAlt, LWin, Apps,");
            sb.AppendLine(";       CapsLock, VolumeUp, VolumeDown, VolumeMute, MediaPlayPause, MediaNext, MediaPrev, OEM_1..OEM_102, VK_xx");
            sb.AppendLine();
            sb.AppendLine("[Settings]");
            sb.AppendLine("Profile=" + Active.Name);
            sb.AppendLine("ToggleKey=" + KeyNames.Name(ToggleVk));
            sb.AppendLine("; ActivationMode: DoubleTap | Hold | SingleTap");
            sb.AppendLine("ActivationMode=" + Mode);
            sb.AppendLine("DoubleTapMs=" + DoubleTapMs);
            sb.AppendLine("DisableStickyKeys=" + (DisableStickyKeys ? "1" : "0"));
            sb.AppendLine("Opacity=" + Opacity);
            sb.AppendLine("Scale=" + Scale);
            sb.AppendLine("; Position: Auto | BottomCenter | BottomLeft | BottomRight | TopCenter | TopLeft | TopRight | Center");
            sb.AppendLine("Position=" + Position);
            foreach (Profile p in Profiles)
            {
                sb.AppendLine();
                sb.AppendLine("[Profile:" + p.Name + "]");
                foreach (var kv in p.Remap.OrderBy(x => KeyNames.Name(x.Key & 0xFFFF), StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Key))
                    sb.AppendLine(KeyNames.SourceName(kv.Key) + "=" + kv.Value.ToConfig());
            }
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
