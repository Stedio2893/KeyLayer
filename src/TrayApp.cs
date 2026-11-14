using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Threading;
using System.Windows.Forms;

namespace KeyLayer
{
    internal static class Program
    {
        public const string ShowSettingsMessage = "KeyLayer.ShowSettings.{7B1D2E4A-93C5-4F0E-9D7B-2C6A1E5F8B30}";
        public const string ExitMessage = "KeyLayer.Exit.{7B1D2E4A-93C5-4F0E-9D7B-2C6A1E5F8B30}";
        public const string HostTitle = "KeyLayer Host {7B1D2E4A-93C5-4F0E-9D7B-2C6A1E5F8B30}";

        [STAThread]
        static void Main(string[] args)
        {
            bool created;
            using (var mutex = new Mutex(true, @"Local\KeyLayer.SingleInstance.{7B1D2E4A-93C5-4F0E-9D7B-2C6A1E5F8B30}", out created))
            {
                bool exitRequest = Array.Exists(args, a => a.Equals("/exit", StringComparison.OrdinalIgnoreCase));
                if (!created || exitRequest)
                {
                    // Already running: ask it to open its settings window, or to quit with /exit.
                    // (/exit with nothing running: just return.)
                    if (!created)
                    {
                        int msg = Native.RegisterWindowMessage(exitRequest ? ExitMessage : ShowSettingsMessage);
                        IntPtr hostWnd = Native.FindWindow(null, HostTitle);
                        if (msg != 0)
                            Native.PostMessage(hostWnd != IntPtr.Zero ? hostWnd : new IntPtr(0xFFFF) /* HWND_BROADCAST */, msg, IntPtr.Zero, IntPtr.Zero);
                    }
                    return;
                }

                try { Native.SetProcessDPIAware(); } catch (EntryPointNotFoundException) { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                bool silent = false;
                foreach (string a in args)
                {
                    string l = a.ToLowerInvariant();
                    if (l == "/autostart" || l == "/silent" || l == "-silent") silent = true;
                }

                Application.ThreadException += delegate (object s, ThreadExceptionEventArgs e)
                {
                    MessageBox.Show(e.Exception.ToString(), "KeyLayer - errore", MessageBoxButtons.OK, MessageBoxIcon.Error);
                };
                try { Application.Run(new TrayApp(silent)); }
                finally { AccessibilityGuard.Restore(); }
                GC.KeepAlive(mutex);
            }
        }
    }

    internal sealed class TrayApp : ApplicationContext
    {
        const int HoldShowDelayMs = 180;   // hold mode: quick Shift+letter for capitals must not flash the overlay

        readonly AppConfig cfg;
        readonly RemapEngine engine = new RemapEngine();
        readonly HostWindow host = new HostWindow();
        readonly List<KeyCap> layout = Layout.Build();
        readonly NotifyIcon tray = new NotifyIcon();
        readonly Icon icoIdle, icoActive, icoPaused;
        readonly System.Windows.Forms.Timer followTimer = new System.Windows.Forms.Timer { Interval = 150 };
        readonly System.Windows.Forms.Timer holdShowTimer = new System.Windows.Forms.Timer { Interval = HoldShowDelayMs };
        readonly System.Windows.Forms.Timer hookWatchdog = new System.Windows.Forms.Timer { Interval = 2000 };
        ToolStripMenuItem miLayer, miPause;
        SettingsForm settings;
        OverlayForm overlay;               // exists only while visible
        Rectangle overlayBounds;
        Screen overlayScreen;
        IntPtr overlayHkl;
        int redrawPending;
        bool saveErrorShown;

        public TrayApp(bool silent)
        {
            cfg = AppConfig.Load();
            if (!cfg.Existed) TrySave();

            IntPtr forceHandle = host.Handle;
            host.ShowSettingsRequested += delegate { ShowSettings(); };
            host.ExitRequested += delegate { Exit(); };

            ApplyEngineSettings();
            engine.ActiveChanged += delegate { SafeInvoke(OnActiveChanged); };
            engine.PressedChanged += delegate { RequestRedraw(); };

            followTimer.Tick += delegate { FollowTick(); };
            holdShowTimer.Tick += delegate
            {
                holdShowTimer.Stop();
                if (engine.Active && overlay == null) ShowOverlayNow();
            };

            icoIdle = MakeTrayIcon(Color.FromArgb(235, 235, 240));
            icoActive = MakeTrayIcon(KeyboardRenderer.Yellow);
            icoPaused = MakeTrayIcon(Color.FromArgb(110, 110, 118));

            tray.Icon = icoIdle;
            tray.ContextMenuStrip = BuildMenu();
            tray.MouseClick += delegate (object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) ShowSettings(); };
            UpdateTrayText();
            tray.Visible = true;

            try { engine.Start(); }
            catch (Exception ex)
            {
                MessageBox.Show("Impossibile intercettare la tastiera:\n" + ex.Message, "KeyLayer",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            ApplyStickyKeys();
            try { Autostart.RefreshPath(); } catch (Exception) { }

            // Windows may drop the keyboard hook (it does so silently) or swallow key releases while the session is
            // locked / asleep: re-hook and forget half-finished presses when the user comes back, and keep an eye
            // on the hook in between.
            Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
            Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
            hookWatchdog.Tick += delegate { engine.CheckHooks(); };
            hookWatchdog.Start();

            if (!cfg.Existed && !silent) ShowSettings();
            else if (!silent)
                tray.ShowBalloonTip(4000, "KeyLayer è in esecuzione",
                    Capitalize(cfg.GestureText()) + " per usare il layer" +
                    (cfg.ProfileGestures().Count > 0 ? " (" + string.Join(", ", cfg.ProfileGestures()) + ")" : "") +
                    ". Clic sull'icona per le impostazioni.", ToolTipIcon.Info);
        }

        static string Capitalize(string s) { return s.Length == 0 ? s : char.ToUpper(s[0]) + s.Substring(1); }

        /// <summary>The main switcher serves the selected profile; profiles with their own key get one more each.</summary>
        void ApplyEngineSettings()
        {
            cfg.Remap.Remove(cfg.ToggleVk);
            var keys = new HashSet<int> { cfg.ToggleVk };
            foreach (Profile p in cfg.Profiles) if (p.KeyVk != 0) keys.Add(p.KeyVk);
            var list = new List<Switcher> { MakeSwitcher(cfg.ToggleVk, cfg.Mode, cfg.Active, keys) };
            foreach (Profile p in cfg.Profiles)
                if (p.KeyVk != 0 && p.KeyVk != cfg.ToggleVk) list.Add(MakeSwitcher(p.KeyVk, p.KeyMode, p, keys));
            engine.DoubleTapMs = cfg.DoubleTapMs;
            engine.Switchers = list.ToArray();
        }

        static Switcher MakeSwitcher(int vk, ActivationMode mode, Profile p, HashSet<int> switcherKeys)
        {
            var map = new Dictionary<int, Mapping>(p.Remap);
            foreach (int k in switcherKeys) map.Remove(k); // a switcher is never remapped
            return new Switcher { Vk = vk, Mode = mode, Profile = p.Name, Map = map };
        }

        /// <summary>"tocca due volte Shift destro · Vim: tieni premuto F9"</summary>
        string AllGestures()
        {
            var l = cfg.ProfileGestures();
            l.Insert(0, cfg.GestureText());
            return string.Join(" · ", l);
        }

        void ApplyStickyKeys()
        {
            try
            {
                if (cfg.DisableStickyKeys) AccessibilityGuard.Apply();
                else AccessibilityGuard.Restore();
            }
            catch (Exception) { }
        }

        ContextMenuStrip BuildMenu()
        {
            var menu = new ContextMenuStrip();
            var miSettings = new ToolStripMenuItem("Impostazioni…", null, delegate { ShowSettings(); });
            miSettings.Font = new Font(miSettings.Font, FontStyle.Bold);
            miLayer = new ToolStripMenuItem("Layer attivo", null, delegate { if (engine.Enabled) engine.SetActive(!engine.Active); });
            miPause = new ToolStripMenuItem("Sospendi KeyLayer", null, delegate
            {
                engine.Enabled = !engine.Enabled;
                miPause.Checked = !engine.Enabled;
                UpdateTrayText();
            });
            var miProfile = new ToolStripMenuItem("Profilo");
            miProfile.DropDownItems.Add("(caricamento)");
            miProfile.DropDownOpening += delegate
            {
                miProfile.DropDownItems.Clear();
                foreach (Profile p in cfg.Profiles)
                {
                    string name = p.Name;
                    var it = new ToolStripMenuItem(name, null, delegate { SwitchProfile(name); });
                    it.Checked = p == cfg.Active;
                    miProfile.DropDownItems.Add(it);
                }
            };
            var miFolder = new ToolStripMenuItem("Apri cartella del programma", null, delegate
            {
                try { Process.Start("explorer.exe", "\"" + AppDomain.CurrentDomain.BaseDirectory + "\""); } catch (Exception) { }
            });
            var miExit = new ToolStripMenuItem("Esci", null, delegate { Exit(); });
            menu.Items.AddRange(new ToolStripItem[] { miSettings, miLayer, miProfile, miPause, new ToolStripSeparator(), miFolder, new ToolStripSeparator(), miExit });
            return menu;
        }

        void SwitchProfile(string name)
        {
            cfg.ActiveProfileName = name;
            ApplyConfig();
            if (settings != null && !settings.IsDisposed) settings.OnProfileChanged();
        }

        void UpdateTrayText()
        {
            string state = !engine.Enabled ? "sospeso" : engine.Active ? "layer ATTIVO" : AllGestures();
            string t = "KeyLayer – " + (engine.Active ? engine.ActiveProfile : cfg.Active.Name) + " – " + state;
            tray.Text = t.Length > 63 ? t.Substring(0, 63) : t;
            tray.Icon = !engine.Enabled ? icoPaused : engine.Active ? icoActive : icoIdle;
            if (miLayer != null)
            {
                miLayer.Text = "Layer attivo  (" + cfg.Active.Name + ": " + cfg.GestureText() + ")";
                miLayer.Checked = engine.Active;
                miLayer.Enabled = engine.Enabled;
            }
        }

        void SafeInvoke(Action a)
        {
            try { if (host.IsHandleCreated && !host.IsDisposed) host.BeginInvoke(a); }
            catch (InvalidOperationException) { }
        }

        void OnActiveChanged()
        {
            UpdateTrayText();
            if (engine.Active)
            {
                // Hold: wait a moment (a quick Shift+letter must not flash the overlay), unless it is already up
                // and only the profile changed.
                if (engine.ActiveIsHold && overlay == null) { holdShowTimer.Stop(); holdShowTimer.Start(); }
                else ShowOverlayNow();
            }
            else
            {
                holdShowTimer.Stop();
                HideOverlay();
            }
            if (settings != null && !settings.IsDisposed) settings.OnLayerChanged(engine.Active);
        }

        void ShowOverlayNow()
        {
            HideOverlay();
            overlayHkl = Native.ForegroundKeyboardLayout();
            Layout.Resolve(layout, overlayHkl);
            // A brand-new window always opens on the virtual desktop the user is on.
            overlay = new OverlayForm();
            IntPtr h = overlay.Handle;
            Redraw(true);
            overlay.ShowOverlay();
            followTimer.Start();
        }

        void HideOverlay()
        {
            followTimer.Stop();
            if (overlay == null) return;
            overlay.HideOverlay();
            overlay.Dispose();
            overlay = null;
        }

        /// <summary>While visible: follow the user to another virtual desktop / monitor, and dodge the mouse.</summary>
        void FollowTick()
        {
            if (overlay == null || !engine.Active) return;
            if (!VirtualDesktop.IsOnCurrent(overlay.Handle)) { ShowOverlayNow(); return; }
            Point c = Cursor.Position;
            Screen s = Screen.FromPoint(c);
            if (!s.Equals(overlayScreen) || (cfg.Position == "Auto" && overlayBounds.Contains(c)))
                Redraw(true);
        }

        void RequestRedraw()
        {
            if (!engine.Active) return;
            if (Interlocked.Exchange(ref redrawPending, 1) == 1) return;
            SafeInvoke(delegate
            {
                Interlocked.Exchange(ref redrawPending, 0);
                if (engine.Active && overlay != null) Redraw(false);
            });
        }

        static float ScreenDpi()
        {
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) return g.DpiX;
        }

        void Redraw(bool reposition)
        {
            if (overlay == null) return;
            float dpi = ScreenDpi();
            float unit = 22f * cfg.Scale / 100f * dpi / 96f;
            SizeF sz = KeyboardRenderer.Measure(unit);
            int w = (int)Math.Ceiling(sz.Width), h = (int)Math.Ceiling(sz.Height);
            if (reposition || overlayBounds.Width != w || overlayBounds.Height != h)
            {
                Point c = Cursor.Position;
                overlayScreen = Screen.FromPoint(c);
                overlayBounds = new Rectangle(ComputePosition(w, h, dpi, c, overlayScreen.WorkingArea), new Size(w, h));
            }

            // While Shift / Ctrl are held, show what the keys do with that modifier (e.g. Vim's Shift+G).
            int[] pressed = engine.Pressed;
            int holdVk = engine.HoldVk;
            Func<int, bool> held = vk => Array.IndexOf(pressed, vk) >= 0 && vk != holdVk;
            int variant = (held(0xA0) || held(0xA1) ? KeyNames.SrcShift : 0) | (held(0xA2) || held(0xA3) ? KeyNames.SrcCtrl : 0);

            var st = new RenderState
            {
                Map = engine.ActiveMap,
                Pressed = pressed,
                ToggleVk = engine.ActiveVk,
                LayerActive = true,
                Hkl = overlayHkl,
                Variant = variant,
                ProfileName = engine.ActiveProfile ?? cfg.Active.Name,
            };
            using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    KeyboardRenderer.Draw(g, layout, unit, st);
                }
                overlay.Render(bmp, overlayBounds.Location, (byte)(cfg.Opacity * 255 / 100));
            }
        }

        /// <summary>
        /// Auto: cursor in the lower third -> overlay on top; upper third -> at the bottom;
        /// middle band -> on the right (on the left if the cursor is near the right edge).
        /// Always on the monitor that contains the cursor.
        /// </summary>
        Point ComputePosition(int w, int h, float dpi, Point c, Rectangle wa)
        {
            int gap = (int)(28 * dpi / 96f);
            int cx = wa.Left + (wa.Width - w) / 2;
            int cy = wa.Top + (wa.Height - h) / 2;
            Point top = new Point(cx, wa.Top + gap), bottom = new Point(cx, wa.Bottom - h - gap);
            switch (cfg.Position)
            {
                case "TopCenter": return top;
                case "TopLeft": return new Point(wa.Left + gap, wa.Top + gap);
                case "TopRight": return new Point(wa.Right - w - gap, wa.Top + gap);
                case "BottomCenter": return bottom;
                case "BottomLeft": return new Point(wa.Left + gap, wa.Bottom - h - gap);
                case "BottomRight": return new Point(wa.Right - w - gap, wa.Bottom - h - gap);
                case "Center": return new Point(cx, cy);
            }
            double ry = (c.Y - wa.Top) / (double)Math.Max(1, wa.Height);
            if (ry >= 2 / 3.0) return top;
            if (ry < 1 / 3.0) return bottom;
            var right = new Point(wa.Right - w - gap, cy);
            if (c.X >= right.X - gap) return new Point(wa.Left + gap, cy); // the cursor would be under it: go left
            return right;
        }

        void ShowSettings()
        {
            // A window living on another virtual desktop would drag the user there: reopen it here instead.
            if (settings != null && !settings.IsDisposed && !VirtualDesktop.IsOnCurrent(settings.Handle))
            {
                settings.Close();
                settings = null;
            }
            if (settings == null || settings.IsDisposed)
            {
                settings = new SettingsForm(cfg, engine, ApplyConfig);
                settings.Show();
            }
            if (settings.WindowState == FormWindowState.Minimized) settings.WindowState = FormWindowState.Normal;
            settings.Activate();
        }

        /// <summary>Called by the settings window after every change: apply + save.</summary>
        void ApplyConfig()
        {
            ApplyEngineSettings();
            ApplyStickyKeys();
            UpdateTrayText();
            TrySave();
            if (engine.Active && overlay != null) Redraw(true);
        }

        void TrySave()
        {
            try { cfg.Save(); }
            catch (Exception ex)
            {
                if (saveErrorShown) return;
                saveErrorShown = true;
                MessageBox.Show("Impossibile salvare " + AppConfig.FilePath + "\n" + ex.Message +
                    "\n\nSposta KeyLayer in una cartella scrivibile.", "KeyLayer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        void OnSessionSwitch(object sender, Microsoft.Win32.SessionSwitchEventArgs e)
        {
            if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionUnlock ||
                e.Reason == Microsoft.Win32.SessionSwitchReason.ConsoleConnect ||
                e.Reason == Microsoft.Win32.SessionSwitchReason.RemoteConnect ||
                e.Reason == Microsoft.Win32.SessionSwitchReason.SessionLogon)
                engine.Reinstall();
        }

        void OnPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
        {
            if (e.Mode == Microsoft.Win32.PowerModes.Resume) engine.Reinstall();
        }

        void Exit()
        {
            Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
            Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            hookWatchdog.Stop();
            engine.Dispose();
            AccessibilityGuard.Restore();
            followTimer.Stop();
            holdShowTimer.Stop();
            tray.Visible = false;
            tray.Dispose();
            if (settings != null && !settings.IsDisposed) settings.Close();
            HideOverlay();
            host.Dispose();
            ExitThread();
        }

        static Icon MakeTrayIcon(Color keyColor)
        {
            int s = Math.Max(16, SystemInformation.SmallIconSize.Width);
            using (var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    float u = s / 16f;
                    using (var body = new SolidBrush(Color.FromArgb(255, 28, 28, 34)))
                    using (var p = new GraphicsPath())
                    {
                        KeyboardRenderer.AddRounded(p, new RectangleF(0.5f * u, 2.5f * u, 15f * u, 11f * u), 2.2f * u);
                        g.FillPath(body, p);
                    }
                    using (var kb = new SolidBrush(keyColor))
                    {
                        for (int i = 0; i < 4; i++)
                        {
                            g.FillRectangle(kb, (2f + i * 3.2f) * u, 4.3f * u, 2.4f * u, 2.2f * u);
                            g.FillRectangle(kb, (2f + i * 3.2f) * u, 7.2f * u, 2.4f * u, 2.2f * u);
                        }
                        g.FillRectangle(kb, 4.2f * u, 10.1f * u, 7.6f * u, 1.7f * u);
                    }
                }
                IntPtr h = bmp.GetHicon();
                try { return (Icon)Icon.FromHandle(h).Clone(); }
                finally { Native.DestroyIcon(h); }
            }
        }
    }
}
