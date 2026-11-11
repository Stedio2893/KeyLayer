using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace KeyLayer
{
    internal sealed class SettingsForm : Form
    {
        sealed class Item
        {
            public readonly int Value;
            public readonly string Text;
            public readonly string Code;
            public Item(int value, string text) { Value = value; Text = text; }
            public Item(string code, string text) { Code = code; Text = text; }
            public override string ToString() { return Text; }
        }

        static readonly string[,] Positions =
        {
            { "Auto", "Automatica (lontano dal cursore)" },
            { "BottomCenter", "In basso al centro" }, { "BottomLeft", "In basso a sinistra" }, { "BottomRight", "In basso a destra" },
            { "TopCenter", "In alto al centro" }, { "TopLeft", "In alto a sinistra" }, { "TopRight", "In alto a destra" },
            { "Center", "Al centro dello schermo" },
        };

        readonly AppConfig cfg;
        readonly RemapEngine engine;
        readonly Action changed;
        readonly List<KeyCap> keys;
        readonly IntPtr hkl;
        readonly float dpi;

        KeyboardView kb;
        ListView list;
        Label lblSel, lblOpacity, lblScale, hint;
        ComboBox cbTarget, cbToggle, cbPos, cbMode;
        CheckBox chCtrl, chShift, chAlt, chWin, chAuto, chSticky;
        Button btAssign, btRemove, btCapture, btTest;
        TrackBar tbOpacity, tbScale;
        int selected = -1;
        bool loading;

        public SettingsForm(AppConfig cfg, RemapEngine engine, Action changed)
        {
            this.cfg = cfg;
            this.engine = engine;
            this.changed = changed;
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) dpi = g.DpiX;
            hkl = Native.GetKeyboardLayout(0);
            keys = KeyLayer.Layout.Build();
            KeyLayer.Layout.Resolve(keys, hkl);

            Text = "KeyLayer – Impostazioni";
            Font = new Font("Segoe UI", 9f);
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }

            BuildUi();
            LoadOptions();
            RefreshList();
            SelectKey(-1);
        }

        int S(float px) { return (int)Math.Round(px * dpi / 96f); }

        Button Btn(string text, Action onClick)
        {
            var b = new Button { Text = text, AutoSize = true, UseVisualStyleBackColor = true, Padding = new Padding(S(4), S(1), S(4), S(1)) };
            b.Click += delegate { onClick(); };
            return b;
        }

        static FlowLayoutPanel Flow(FlowDirection dir)
        {
            return new FlowLayoutPanel { FlowDirection = dir, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty };
        }

        GroupBox Group(string title, Control content)
        {
            var gb = new GroupBox { Text = title, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(S(8), S(4), S(8), S(8)), Margin = new Padding(0, 0, S(10), S(10)) };
            content.Dock = DockStyle.Fill;
            gb.Controls.Add(content);
            return gb;
        }

        void BuildUi()
        {
            var root = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(S(12)) };
            Controls.Add(root);

            kb = new KeyboardView(keys, S(34));
            kb.State.Map = cfg.Remap;
            kb.State.ToggleVk = cfg.ToggleVk;
            kb.State.Hkl = hkl;
            kb.State.Background = Color.FromArgb(255, 24, 24, 28);
            kb.KeyClicked = SelectKey;
            kb.Margin = new Padding(0, 0, 0, S(10));

            hint = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(kb.Width, 0),
                Margin = new Padding(0, 0, 0, S(8)),
            };
            UpdateHint();
            root.Controls.Add(hint);
            root.Controls.Add(kb);

            var mid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty };
            root.Controls.Add(mid);

            // --- Remap list ---
            var left = Flow(FlowDirection.TopDown);
            list = new ListView
            {
                View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false,
                HeaderStyle = ColumnHeaderStyle.Nonclickable, Size = new Size(S(300), S(196)), Margin = new Padding(0, S(4), 0, S(6)),
            };
            list.Columns.Add("Tasto", S(100));
            list.Columns.Add("Diventa", S(176));
            list.SelectedIndexChanged += delegate
            {
                if (!loading && list.SelectedItems.Count == 1) SelectKey((int)list.SelectedItems[0].Tag);
            };
            left.Controls.Add(list);
            var lb = Flow(FlowDirection.LeftToRight);
            lb.Controls.Add(Btn("Preset (IJKL frecce, U/O pag., N/M inizio/fine)", delegate { cfg.ApplyVimPreset(); Changed(); SelectKey(selected); }));
            lb.Controls.Add(Btn("Rimuovi tutte", delegate
            {
                if (cfg.Remap.Count == 0) return;
                if (MessageBox.Show(this, "Rimuovere tutte le rimappature?", "KeyLayer", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                cfg.Remap.Clear(); Changed(); SelectKey(selected);
            }));
            left.Controls.Add(lb);
            mid.Controls.Add(Group("Rimappature", left), 0, 0);

            // --- Key editor ---
            var right = Flow(FlowDirection.TopDown);
            lblSel = new Label { AutoSize = true, Font = new Font("Segoe UI", 10.5f, FontStyle.Bold), Margin = new Padding(0, S(4), 0, S(6)), MaximumSize = new Size(S(280), 0) };
            right.Controls.Add(lblSel);
            btCapture = Btn("Premi un tasto…", StartCapture);
            right.Controls.Add(btCapture);
            right.Controls.Add(new Label { AutoSize = true, Text = "Diventa:", Margin = new Padding(0, S(10), 0, S(2)) });
            cbTarget = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(270), MaxDropDownItems = 22, Margin = new Padding(0, 0, 0, S(4)) };
            FillTargets();
            right.Controls.Add(cbTarget);
            var mods = Flow(FlowDirection.LeftToRight);
            mods.Controls.Add(new Label { AutoSize = true, Text = "con:", Margin = new Padding(0, S(5), S(4), 0) });
            chCtrl = new CheckBox { Text = "Ctrl", AutoSize = true };
            chShift = new CheckBox { Text = "Shift", AutoSize = true };
            chAlt = new CheckBox { Text = "Alt", AutoSize = true };
            chWin = new CheckBox { Text = "Win", AutoSize = true };
            mods.Controls.AddRange(new Control[] { chCtrl, chShift, chAlt, chWin });
            right.Controls.Add(mods);
            var eb = Flow(FlowDirection.LeftToRight);
            eb.Margin = new Padding(0, S(8), 0, 0);
            btAssign = Btn("Assegna", Assign);
            btRemove = Btn("Rimuovi", delegate { if (selected > 0) { cfg.Remap.Remove(selected); Changed(); SelectKey(selected); } });
            eb.Controls.Add(btAssign);
            eb.Controls.Add(btRemove);
            right.Controls.Add(eb);
            mid.Controls.Add(Group("Tasto selezionato", right), 1, 0);

            // --- Options ---
            var opt = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty };
            cbToggle = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(200) };
            foreach (int vk in AppConfig.ToggleChoices) cbToggle.Items.Add(new Item(vk, AppConfig.ToggleName(vk)));
            cbToggle.SelectedIndexChanged += delegate
            {
                if (loading) return;
                var it = cbToggle.SelectedItem as Item;
                if (it == null) return;
                cfg.ToggleVk = it.Value;
                cfg.Remap.Remove(it.Value);
                kb.State.ToggleVk = it.Value;
                Changed();
                SelectKey(selected);
                UpdateHint();
            };
            cbMode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(280) };
            foreach (ActivationMode m in new[] { ActivationMode.DoubleTap, ActivationMode.Hold, ActivationMode.SingleTap })
                cbMode.Items.Add(new Item((int)m, AppConfig.ModeName(m)));
            cbMode.SelectedIndexChanged += delegate
            {
                if (loading) return;
                var it = cbMode.SelectedItem as Item;
                if (it == null) return;
                cfg.Mode = (ActivationMode)it.Value;
                changed();
                UpdateHint();
            };
            chSticky = new CheckBox { Text = "Disattiva «Tasti permanenti» (e la scorciatoia di «Filtro tasti») mentre KeyLayer è in esecuzione", AutoSize = true, Margin = new Padding(0, S(6), 0, 0) };
            chSticky.CheckedChanged += delegate
            {
                if (loading) return;
                cfg.DisableStickyKeys = chSticky.Checked;
                changed();
            };
            cbPos = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(280) };
            for (int i = 0; i < Positions.GetLength(0); i++) cbPos.Items.Add(new Item(Positions[i, 0], Positions[i, 1]));
            cbPos.SelectedIndexChanged += delegate
            {
                if (loading) return;
                var it = cbPos.SelectedItem as Item;
                if (it != null) { cfg.Position = it.Code; changed(); }
            };
            tbOpacity = new TrackBar { Minimum = 30, Maximum = 100, TickFrequency = 10, SmallChange = 5, LargeChange = 10, Width = S(200), AutoSize = false, Height = S(30) };
            lblOpacity = new Label { AutoSize = true, Anchor = AnchorStyles.Left };
            tbOpacity.ValueChanged += delegate
            {
                lblOpacity.Text = tbOpacity.Value + " %";
                if (!loading) { cfg.Opacity = tbOpacity.Value; changed(); }
            };
            tbScale = new TrackBar { Minimum = 50, Maximum = 200, TickFrequency = 25, SmallChange = 10, LargeChange = 25, Width = S(200), AutoSize = false, Height = S(30) };
            lblScale = new Label { AutoSize = true, Anchor = AnchorStyles.Left };
            tbScale.ValueChanged += delegate
            {
                lblScale.Text = tbScale.Value + " %";
                if (!loading) { cfg.Scale = tbScale.Value; changed(); }
            };
            chAuto = new CheckBox { Text = "Avvia con Windows (registro utente, si può togliere qui in ogni momento)", AutoSize = true, Margin = new Padding(0, S(6), 0, 0) };
            chAuto.CheckedChanged += delegate
            {
                if (loading) return;
                try { Autostart.Set(chAuto.Checked); }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "KeyLayer", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            };

            AddRow(opt, "Tasto di attivazione:", cbToggle, null);
            AddRow(opt, "Modalità:", cbMode, null);
            AddRow(opt, "Posizione overlay:", cbPos, null);
            AddRow(opt, "Opacità overlay:", tbOpacity, lblOpacity);
            AddRow(opt, "Dimensione overlay:", tbScale, lblScale);
            foreach (CheckBox cb in new[] { chSticky, chAuto })
            {
                opt.Controls.Add(cb, 0, opt.RowCount);
                opt.SetColumnSpan(cb, 3);
                opt.RowCount++;
            }
            var gbOpt = Group("Opzioni", opt);
            root.Controls.Add(gbOpt);

            // --- Footer ---
            var foot = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty, Width = kb.Width };
            var path = new Label
            {
                AutoSize = true, ForeColor = SystemColors.GrayText, Anchor = AnchorStyles.Left,
                MaximumSize = new Size(kb.Width - S(330), 0),
                Text = "Configurazione: KeyLayer.ini, nella cartella del programma",
            };
            var fb = Flow(FlowDirection.LeftToRight);
            fb.Anchor = AnchorStyles.Right;
            btTest = Btn("Mostra overlay", delegate { if (engine.Enabled) engine.SetActive(!engine.Active); });
            fb.Controls.Add(btTest);
            fb.Controls.Add(Btn("Apri cartella", delegate
            {
                try { Process.Start("explorer.exe", "\"" + AppDomain.CurrentDomain.BaseDirectory + "\""); } catch (Exception) { }
            }));
            fb.Controls.Add(Btn("Chiudi", Close));
            foot.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foot.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            foot.Controls.Add(path, 0, 0);
            foot.Controls.Add(fb, 1, 0);
            foot.AutoSize = false;
            foot.Size = new Size(kb.Width, Math.Max(path.PreferredSize.Height, fb.PreferredSize.Height) + S(4));
            root.Controls.Add(foot);
            OnLayerChanged(engine.Active);
        }

        void AddRow(TableLayoutPanel t, string label, Control c, Control extra)
        {
            int r = t.RowCount;
            t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, S(4), S(10), S(4)) }, 0, r);
            c.Anchor = AnchorStyles.Left;
            t.Controls.Add(c, 1, r);
            if (extra != null) t.Controls.Add(extra, 2, r);
            t.RowCount = r + 1;
        }

        void AddTarget(HashSet<int> seen, int vk, string text)
        {
            if (vk != 0 && !seen.Add(vk)) return;
            cbTarget.Items.Add(new Item(vk, text));
        }

        void FillTargets()
        {
            var seen = new HashSet<int>();
            AddTarget(seen, 0, "— Nessuno (disattiva il tasto) —");
            AddTarget(seen, 0x26, "↑  Freccia su");
            AddTarget(seen, 0x28, "↓  Freccia giù");
            AddTarget(seen, 0x25, "←  Freccia sinistra");
            AddTarget(seen, 0x27, "→  Freccia destra");
            AddTarget(seen, 0x24, "Home  (inizio riga)");
            AddTarget(seen, 0x23, "Fine  (End)");
            AddTarget(seen, 0x21, "Pagina su");
            AddTarget(seen, 0x22, "Pagina giù");
            AddTarget(seen, 0x08, "Backspace");
            AddTarget(seen, 0x2E, "Canc  (Delete)");
            AddTarget(seen, 0x2D, "Ins");
            AddTarget(seen, 0x0D, "Invio");
            AddTarget(seen, 0x1B, "Esc");
            AddTarget(seen, 0x09, "Tab");
            AddTarget(seen, 0x20, "Spazio");
            AddTarget(seen, 0xA0, "Shift sinistro");
            AddTarget(seen, 0xA1, "Shift destro");
            AddTarget(seen, 0xA2, "Ctrl sinistro");
            AddTarget(seen, 0xA3, "Ctrl destro");
            AddTarget(seen, 0xA4, "Alt");
            AddTarget(seen, 0xA5, "AltGr");
            AddTarget(seen, 0x5B, "Win");
            AddTarget(seen, 0x5D, "Menu contestuale");
            AddTarget(seen, 0x14, "Bloc Maiusc");
            AddTarget(seen, 0x90, "Bloc Num");
            AddTarget(seen, 0x91, "Bloc Scorr");
            AddTarget(seen, 0x2C, "Stamp");
            AddTarget(seen, 0x13, "Pausa");
            AddTarget(seen, 0xAF, "Volume +");
            AddTarget(seen, 0xAE, "Volume −");
            AddTarget(seen, 0xAD, "Volume muto");
            AddTarget(seen, 0xB3, "Play / Pausa");
            AddTarget(seen, 0xB0, "Traccia successiva");
            AddTarget(seen, 0xB1, "Traccia precedente");
            AddTarget(seen, 0xB2, "Stop");
            AddTarget(seen, 0xA6, "Browser: indietro");
            AddTarget(seen, 0xA7, "Browser: avanti");
            for (int i = 1; i <= 24; i++) AddTarget(seen, 0x6F + i, "F" + i);
            for (char c = 'A'; c <= 'Z'; c++) AddTarget(seen, c, "Lettera " + c);
            for (char c = '0'; c <= '9'; c++) AddTarget(seen, c, "Numero " + c);
            foreach (KeyCap k in keys)
                if (k.Vk != 0) AddTarget(seen, k.Vk, "Tasto  " + KeyNames.Label(k.Vk, hkl) + "   (" + KeyNames.Name(k.Vk) + ")");
            for (int i = 0; i <= 9; i++) AddTarget(seen, 0x60 + i, "Tastierino " + i);
        }

        void SelectTarget(int vk)
        {
            foreach (object o in cbTarget.Items)
            {
                var it = (Item)o;
                if (it.Value == vk) { cbTarget.SelectedItem = it; return; }
            }
            var added = new Item(vk, KeyNames.Name(vk));
            cbTarget.Items.Add(added);
            cbTarget.SelectedItem = added;
        }

        void LoadOptions()
        {
            loading = true;
            foreach (object o in cbToggle.Items) if (((Item)o).Value == cfg.ToggleVk) cbToggle.SelectedItem = o;
            if (cbToggle.SelectedItem == null)
            {
                var it = new Item(cfg.ToggleVk, KeyNames.Name(cfg.ToggleVk));
                cbToggle.Items.Add(it);
                cbToggle.SelectedItem = it;
            }
            foreach (object o in cbPos.Items) if (((Item)o).Code == cfg.Position) cbPos.SelectedItem = o;
            if (cbPos.SelectedItem == null) cbPos.SelectedIndex = 0;
            tbOpacity.Value = Math.Max(tbOpacity.Minimum, Math.Min(tbOpacity.Maximum, cfg.Opacity));
            tbScale.Value = Math.Max(tbScale.Minimum, Math.Min(tbScale.Maximum, cfg.Scale));
            lblOpacity.Text = tbOpacity.Value + " %";
            lblScale.Text = tbScale.Value + " %";
            try { chAuto.Checked = Autostart.IsEnabled(); } catch (Exception) { }
            foreach (object o in cbMode.Items) if (((Item)o).Value == (int)cfg.Mode) cbMode.SelectedItem = o;
            chSticky.Checked = cfg.DisableStickyKeys;
            loading = false;
        }

        void RefreshList()
        {
            loading = true;
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var kv in cfg.Remap.OrderBy(p => KeyNames.Name(p.Key), StringComparer.OrdinalIgnoreCase))
            {
                var li = new ListViewItem(KeyNames.Label(kv.Key, hkl));
                li.SubItems.Add(kv.Value.Describe(hkl));
                li.Tag = kv.Key;
                li.Selected = kv.Key == selected;
                list.Items.Add(li);
            }
            list.EndUpdate();
            loading = false;
        }

        void SelectKey(int vk)
        {
            selected = vk;
            kb.State.SelectedVk = vk;
            kb.Invalidate();
            loading = true;
            foreach (ListViewItem li in list.Items) li.Selected = (int)li.Tag == vk;
            loading = false;

            bool has = false;
            Mapping m = null;
            if (vk > 0) has = cfg.Remap.TryGetValue(vk, out m);
            bool isToggle = vk == cfg.ToggleVk;
            bool editable = vk > 0 && !isToggle;

            if (vk <= 0)
                lblSel.Text = "Nessun tasto selezionato";
            else
                lblSel.Text = "Tasto:  " + KeyNames.Label(vk, hkl) + "   (" + KeyNames.Name(vk) + ")" +
                              (isToggle ? "\nÈ il tasto di attivazione: non si può rimappare." : "");

            if (has)
            {
                SelectTarget(m.Vk);
                chCtrl.Checked = (m.Mods & Mapping.ModCtrl) != 0;
                chShift.Checked = (m.Mods & Mapping.ModShift) != 0;
                chAlt.Checked = (m.Mods & Mapping.ModAlt) != 0;
                chWin.Checked = (m.Mods & Mapping.ModWin) != 0;
            }
            else
            {
                if (cbTarget.SelectedIndex < 0) cbTarget.SelectedIndex = 1;
                chCtrl.Checked = chShift.Checked = chAlt.Checked = chWin.Checked = false;
            }
            cbTarget.Enabled = chCtrl.Enabled = chShift.Enabled = chAlt.Enabled = chWin.Enabled = btAssign.Enabled = editable;
            btAssign.Text = has ? "Aggiorna" : "Assegna";
            btRemove.Enabled = has;
        }

        void Assign()
        {
            if (selected <= 0 || selected == cfg.ToggleVk) return;
            var it = cbTarget.SelectedItem as Item;
            if (it == null) return;
            int mods = (chCtrl.Checked ? Mapping.ModCtrl : 0) | (chShift.Checked ? Mapping.ModShift : 0) |
                       (chAlt.Checked ? Mapping.ModAlt : 0) | (chWin.Checked ? Mapping.ModWin : 0);
            if (it.Value == selected && mods == 0) cfg.Remap.Remove(selected); // mapped to itself = no remap
            else cfg.Remap[selected] = new Mapping(it.Value, mods);
            Changed();
            SelectKey(selected);
        }

        void StartCapture()
        {
            btCapture.Text = "Premi ora il tasto da rimappare…";
            btCapture.Enabled = false;
            engine.BeginCapture(delegate (int vk)
            {
                try
                {
                    BeginInvoke(new Action(delegate
                    {
                        btCapture.Text = "Premi un tasto…";
                        btCapture.Enabled = true;
                        SelectKey(vk);
                    }));
                }
                catch (InvalidOperationException) { }
            });
        }

        void Changed()
        {
            RefreshList();
            kb.Invalidate();
            changed();
        }

        void UpdateHint()
        {
            string extra = cfg.Mode == ActivationMode.Hold
                ? " In modalità «tieni premuto», se il primo tasto premuto non è rimappato, " + AppConfig.ToggleName(cfg.ToggleVk) +
                  " funziona normalmente (es. per le maiuscole)."
                : "";
            hint.Text = "Clicca un tasto sulla tastiera (oppure «Premi un tasto…») e scegli in cosa trasformarlo. " +
                        "I tasti rimappati sono in giallo e funzionano solo mentre il layer è attivo: " + cfg.GestureText() + "." +
                        extra + " Le modifiche vengono salvate subito.";
        }

        public void OnLayerChanged(bool active)
        {
            btTest.Text = active ? "Nascondi overlay" : "Mostra overlay";
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            engine.CancelCapture();
            base.OnFormClosed(e);
        }
    }
}
