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
        ComboBox cbTarget, cbToggle, cbPos, cbMode, cbProfile;
        CheckBox chCtrl, chShift, chAlt, chWin, chAuto, chSticky;
        RadioButton rbPlain, rbShift, rbCtrl;
        TextBox tbSeq, tbLabel;
        Button btAssign, btRemove, btCapture, btTest;
        TrackBar tbOpacity, tbScale;
        int selected = -1;   // plain VK of the selected key
        int variant;         // 0, KeyNames.SrcShift or KeyNames.SrcCtrl
        bool loading;

        int CurKey { get { return selected | variant; } }

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
            RefreshProfiles();
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

            kb = new KeyboardView(keys, S(32));
            kb.State.Map = cfg.Remap;
            kb.State.ToggleVk = cfg.ToggleVk;
            kb.State.Hkl = hkl;
            kb.State.Background = Color.FromArgb(255, 24, 24, 28);
            kb.State.StrictVariant = true;
            kb.State.ProfileName = cfg.Active.Name;
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

            // --- Profile + remap list ---
            var left = Flow(FlowDirection.TopDown);
            var pr = Flow(FlowDirection.LeftToRight);
            pr.Controls.Add(new Label { AutoSize = true, Text = "Profilo:", Margin = new Padding(0, S(7), S(4), 0) });
            cbProfile = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(165), Margin = new Padding(0, S(3), S(6), 0) };
            cbProfile.SelectedIndexChanged += delegate
            {
                if (loading || cbProfile.SelectedItem == null) return;
                cfg.ActiveProfileName = cbProfile.SelectedItem.ToString();
                Changed();
                SelectKey(selected);
            };
            pr.Controls.Add(cbProfile);
            var templateMenu = new ContextMenuStrip();
            foreach (string t in ProfileTemplates.Names)
            {
                string name = t;
                templateMenu.Items.Add(ProfileTemplates.Description(name), null, delegate { LoadTemplate(name); });
            }
            Button btTemplates = null;
            btTemplates = Btn("Carica profilo pronto ▾", delegate { templateMenu.Show(btTemplates, new Point(0, btTemplates.Height)); });
            pr.Controls.Add(btTemplates);
            left.Controls.Add(pr);
            var pb = Flow(FlowDirection.LeftToRight);
            pb.Controls.Add(Btn("Nuovo…", NewProfile));
            pb.Controls.Add(Btn("Rinomina…", RenameProfile));
            pb.Controls.Add(Btn("Elimina", DeleteProfile));
            pb.Controls.Add(Btn("Svuota", delegate
            {
                if (cfg.Remap.Count == 0) return;
                if (MessageBox.Show(this, "Rimuovere tutte le rimappature del profilo «" + cfg.Active.Name + "»?", "KeyLayer",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                cfg.Remap.Clear(); Changed(); SelectKey(selected);
            }));
            left.Controls.Add(pb);

            list = new ListView
            {
                View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false,
                HeaderStyle = ColumnHeaderStyle.Nonclickable, Size = new Size(S(340), S(205)), Margin = new Padding(0, S(6), 0, S(2)),
            };
            list.Columns.Add("Tasto", S(90));
            list.Columns.Add("Diventa", S(226));
            list.SelectedIndexChanged += delegate
            {
                if (loading || list.SelectedItems.Count != 1) return;
                int key = (int)list.SelectedItems[0].Tag;
                SetVariant(key & KeyNames.SrcMask);
                SelectKey(key & 0xFFFF);
            };
            left.Controls.Add(list);
            mid.Controls.Add(Group("Profili e rimappature", left), 0, 0);

            // --- Key editor ---
            var right = Flow(FlowDirection.TopDown);
            lblSel = new Label { AutoSize = true, Font = new Font("Segoe UI", 10.5f, FontStyle.Bold), Margin = new Padding(0, S(4), 0, S(6)), MaximumSize = new Size(S(280), 0) };
            right.Controls.Add(lblSel);
            btCapture = Btn("Premi un tasto…", StartCapture);
            right.Controls.Add(btCapture);
            var vr = Flow(FlowDirection.LeftToRight);
            vr.Margin = new Padding(0, S(6), 0, 0);
            rbPlain = new RadioButton { Text = "da solo", AutoSize = true, Checked = true };
            rbShift = new RadioButton { Text = "con Shift", AutoSize = true };
            rbCtrl = new RadioButton { Text = "con Ctrl", AutoSize = true };
            foreach (RadioButton rb in new[] { rbPlain, rbShift, rbCtrl })
            {
                rb.CheckedChanged += delegate (object s, EventArgs e)
                {
                    if (loading || !((RadioButton)s).Checked) return;
                    variant = rbShift.Checked ? KeyNames.SrcShift : rbCtrl.Checked ? KeyNames.SrcCtrl : 0;
                    kb.State.Variant = variant;
                    kb.Invalidate();
                    SelectKey(selected);
                };
                vr.Controls.Add(rb);
            }
            right.Controls.Add(vr);
            right.Controls.Add(new Label { AutoSize = true, Text = "Diventa:", Margin = new Padding(0, S(8), 0, S(2)) });
            cbTarget = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = S(270), MaxDropDownItems = 22, Margin = new Padding(0, 0, 0, S(4)) };
            FillTargets();
            cbTarget.SelectionChangeCommitted += delegate { tbSeq.Text = ""; }; // a single key replaces a sequence
            right.Controls.Add(cbTarget);
            var mods = Flow(FlowDirection.LeftToRight);
            mods.Controls.Add(new Label { AutoSize = true, Text = "con:", Margin = new Padding(0, S(5), S(4), 0) });
            chCtrl = new CheckBox { Text = "Ctrl", AutoSize = true };
            chShift = new CheckBox { Text = "Shift", AutoSize = true };
            chAlt = new CheckBox { Text = "Alt", AutoSize = true };
            chWin = new CheckBox { Text = "Win", AutoSize = true };
            mods.Controls.AddRange(new Control[] { chCtrl, chShift, chAlt, chWin });
            right.Controls.Add(mods);
            right.Controls.Add(new Label { AutoSize = true, Text = "oppure una sequenza di tasti:", Margin = new Padding(0, S(8), 0, S(2)) });
            tbSeq = new TextBox { Width = S(270) };
            tbSeq.HandleCreated += delegate
            {
                // grey hint inside the empty box (EM_SETCUEBANNER)
                Native.SendMessage(tbSeq.Handle, 0x1501, (IntPtr)1, "es. End, Enter   •   LayerOff = esci dal layer");
            };
            right.Controls.Add(tbSeq);
            var lr = Flow(FlowDirection.LeftToRight);
            lr.Margin = new Padding(0, S(6), 0, 0);
            lr.Controls.Add(new Label { AutoSize = true, Text = "Etichetta sul tasto:", Margin = new Padding(0, S(4), S(4), 0) });
            tbLabel = new TextBox { Width = S(110) };
            lr.Controls.Add(tbLabel);
            right.Controls.Add(lr);
            var eb = Flow(FlowDirection.LeftToRight);
            eb.Margin = new Padding(0, S(8), 0, 0);
            btAssign = Btn("Assegna", Assign);
            btRemove = Btn("Rimuovi", delegate { if (selected > 0) { cfg.Remap.Remove(CurKey); Changed(); SelectKey(selected); } });
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
                Text = "Config: KeyLayer.ini (cartella del programma)",
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
            AddTarget(seen, KeyNames.LayerOff, "Esci dal layer  (come «i» in Vim)");
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
            foreach (var kv in cfg.Remap.OrderBy(p => KeyNames.Name(p.Key & 0xFFFF), StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Key))
            {
                var li = new ListViewItem(KeyNames.SourceLabel(kv.Key, hkl));
                li.SubItems.Add(kv.Value.Describe(hkl));
                li.Tag = kv.Key;
                li.Selected = kv.Key == CurKey;
                list.Items.Add(li);
            }
            list.EndUpdate();
            loading = false;
        }

        void RefreshProfiles()
        {
            loading = true;
            cbProfile.Items.Clear();
            foreach (Profile p in cfg.Profiles) cbProfile.Items.Add(p.Name);
            cbProfile.SelectedItem = cfg.Active.Name;
            loading = false;
        }

        void SetVariant(int v)
        {
            variant = v;
            loading = true;
            rbPlain.Checked = v == 0;
            rbShift.Checked = v == KeyNames.SrcShift;
            rbCtrl.Checked = v == KeyNames.SrcCtrl;
            loading = false;
            kb.State.Variant = v;
            kb.Invalidate();
        }

        void SelectKey(int vk)
        {
            selected = vk;
            kb.State.SelectedVk = vk;
            kb.Invalidate();
            loading = true;
            foreach (ListViewItem li in list.Items) li.Selected = (int)li.Tag == CurKey;
            loading = false;

            bool has = false;
            Mapping m = null;
            if (vk > 0) has = cfg.Remap.TryGetValue(CurKey, out m);
            bool isToggle = vk == cfg.ToggleVk;
            bool editable = vk > 0 && !isToggle;

            if (vk <= 0)
                lblSel.Text = "Nessun tasto selezionato";
            else
                lblSel.Text = "Tasto:  " + KeyNames.SourceLabel(CurKey, hkl) + "   (" + KeyNames.SourceName(CurKey) + ")" +
                              (isToggle ? "\nÈ il tasto di attivazione: non si può rimappare." : "");

            if (has && m.IsSimple)
            {
                SelectTarget(m.Vk);
                chCtrl.Checked = (m.Mods & Mapping.ModCtrl) != 0;
                chShift.Checked = (m.Mods & Mapping.ModShift) != 0;
                chAlt.Checked = (m.Mods & Mapping.ModAlt) != 0;
                chWin.Checked = (m.Mods & Mapping.ModWin) != 0;
                tbSeq.Text = "";
            }
            else
            {
                if (cbTarget.SelectedIndex < 0) cbTarget.SelectedIndex = 1;
                chCtrl.Checked = chShift.Checked = chAlt.Checked = chWin.Checked = false;
                tbSeq.Text = has ? m.StepsText() : "";
            }
            tbLabel.Text = has && m.Label != null ? m.Label : "";
            cbTarget.Enabled = chCtrl.Enabled = chShift.Enabled = chAlt.Enabled = chWin.Enabled =
                tbSeq.Enabled = tbLabel.Enabled = btAssign.Enabled = editable;
            btAssign.Text = has ? "Aggiorna" : "Assegna";
            btRemove.Enabled = has;
        }

        void Assign()
        {
            if (selected <= 0 || selected == cfg.ToggleVk) return;
            string label = tbLabel.Text.Trim().Replace("|", "/");
            Mapping m;
            if (tbSeq.Text.Trim().Length > 0)
            {
                m = Mapping.Parse(tbSeq.Text + (label.Length > 0 ? " | " + label : ""));
                if (m == null)
                {
                    MessageBox.Show(this, "Sequenza non valida. Separa i tasti con la virgola, ad esempio:\n\n" +
                        "End, Enter\nHome, Shift+Down, Ctrl+X\nRight, LayerOff", "KeyLayer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }
            else
            {
                var it = cbTarget.SelectedItem as Item;
                if (it == null) return;
                int mods = (chCtrl.Checked ? Mapping.ModCtrl : 0) | (chShift.Checked ? Mapping.ModShift : 0) |
                           (chAlt.Checked ? Mapping.ModAlt : 0) | (chWin.Checked ? Mapping.ModWin : 0);
                if (it.Value == selected && mods == 0 && variant == 0 && label.Length == 0)
                {
                    cfg.Remap.Remove(CurKey); // mapped to itself = no remap
                    Changed();
                    SelectKey(selected);
                    return;
                }
                m = new Mapping(new[] { new KeyStroke(it.Value, mods) }, label);
            }
            cfg.Remap[CurKey] = m;
            Changed();
            SelectKey(selected);
        }

        // ---------------------------------------------------------------- profiles

        string AskName(string title, string initial)
        {
            while (true)
            {
                string name = Microsoft.VisualBasic.Interaction.InputBox("Nome del profilo:", title, initial).Trim();
                if (name.Length == 0) return null; // cancelled
                Profile other = cfg.Find(name);
                if (!AppConfig.IsValidProfileName(name))
                    MessageBox.Show(this, "Il nome non può contenere [ ] = ; #", "KeyLayer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                else if (other != null && !string.Equals(other.Name, initial, StringComparison.OrdinalIgnoreCase))
                    MessageBox.Show(this, "Esiste già un profilo «" + other.Name + "».", "KeyLayer", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                else return name;
                initial = name;
            }
        }

        void LoadTemplate(string name)
        {
            if (cfg.Find(name) != null)
            {
                DialogResult r = MessageBox.Show(this,
                    "Il profilo «" + name + "» esiste già.\n\n" +
                    "Sì: ripristina la versione originale (le tue modifiche a quel profilo andranno perse)\n" +
                    "No: attivalo così com'è",
                    "KeyLayer", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                if (r == DialogResult.Cancel) return;
                if (r == DialogResult.No) { cfg.ActiveProfileName = cfg.Find(name).Name; OnProfileChanged(); changed(); return; }
            }
            cfg.LoadTemplate(name);
            OnProfileChanged();
            changed();
        }

        void NewProfile()
        {
            string name = AskName("Nuovo profilo", "");
            if (name == null) return;
            cfg.Profiles.Add(new Profile(name));
            cfg.ActiveProfileName = name;
            OnProfileChanged();
            changed();
        }

        void RenameProfile()
        {
            Profile p = cfg.Active;
            string name = AskName("Rinomina profilo", p.Name);
            if (name == null || name == p.Name) return;
            p.Name = name;
            cfg.ActiveProfileName = name;
            OnProfileChanged();
            changed();
        }

        void DeleteProfile()
        {
            if (cfg.Profiles.Count <= 1)
            {
                MessageBox.Show(this, "Deve restare almeno un profilo.", "KeyLayer", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Profile p = cfg.Active;
            if (MessageBox.Show(this, "Eliminare il profilo «" + p.Name + "» e tutte le sue rimappature?", "KeyLayer",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            cfg.Profiles.Remove(p);
            cfg.ActiveProfileName = cfg.Profiles[0].Name;
            OnProfileChanged();
            changed();
        }

        /// <summary>The active profile changed (here or from the tray menu): reload everything that shows it.</summary>
        public void OnProfileChanged()
        {
            RefreshProfiles();
            kb.State.Map = cfg.Remap;
            kb.State.ProfileName = cfg.Active.Name;
            RefreshList();
            SelectKey(selected);
            kb.Invalidate();
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
            kb.State.Map = cfg.Remap;
            kb.State.ProfileName = cfg.Active.Name;
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
