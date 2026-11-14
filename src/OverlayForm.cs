using System;
using System.Drawing;
using System.Windows.Forms;

namespace KeyLayer
{
    /// <summary>
    /// Invisible top-level window that lives as long as the app: target for cross-thread invokes and
    /// for the "show settings" broadcast sent by a second instance.
    /// </summary>
    internal sealed class HostWindow : Form
    {
        public event EventHandler ShowSettingsRequested;
        public event EventHandler ExitRequested;
        readonly int showMsg, exitMsg;

        public HostWindow()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            Text = Program.HostTitle;
            showMsg = Native.RegisterWindowMessage(Program.ShowSettingsMessage);
            exitMsg = Native.RegisterWindowMessage(Program.ExitMessage);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        protected override void SetVisibleCore(bool value) { base.SetVisibleCore(false); }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                if (showMsg != 0) Native.ChangeWindowMessageFilterEx(Handle, showMsg, 1 /* MSGFLT_ALLOW */, IntPtr.Zero);
                if (exitMsg != 0) Native.ChangeWindowMessageFilterEx(Handle, exitMsg, 1, IntPtr.Zero);
            }
            catch (EntryPointNotFoundException) { }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg != 0 && (m.Msg == showMsg || m.Msg == exitMsg))
            {
                EventHandler h = m.Msg == showMsg ? ShowSettingsRequested : ExitRequested;
                if (h != null) BeginInvoke(new Action(delegate { h(this, EventArgs.Empty); }));
                return;
            }
            base.WndProc(ref m);
        }
    }

    /// <summary>
    /// Per-pixel-alpha, click-through, always-on-top window that never takes focus.
    /// A new one is created every time the layer turns on, so it always appears on the current virtual desktop.
    /// </summary>
    internal sealed class OverlayForm : Form
    {
        public OverlayForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Text = "KeyLayer Overlay";
            Size = new Size(1, 1);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW |
                              Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        public void ShowOverlay()
        {
            Native.SetWindowPos(Handle, new IntPtr(-1) /* HWND_TOPMOST */, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        }

        public void HideOverlay()
        {
            Native.ShowWindow(Handle, 0 /* SW_HIDE */);
        }

        public void Render(Bitmap bmp, Point location, byte opacity)
        {
            IntPtr screenDc = Native.GetDC(IntPtr.Zero);
            IntPtr memDc = Native.CreateCompatibleDC(screenDc);
            IntPtr hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
            IntPtr old = Native.SelectObject(memDc, hBitmap);
            try
            {
                var size = new Native.SIZE(bmp.Width, bmp.Height);
                var src = new Native.POINT(0, 0);
                var dst = new Native.POINT(location.X, location.Y);
                var blend = new Native.BLENDFUNCTION
                {
                    BlendOp = Native.AC_SRC_OVER,
                    BlendFlags = 0,
                    SourceConstantAlpha = opacity,
                    AlphaFormat = Native.AC_SRC_ALPHA
                };
                Native.UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, Native.ULW_ALPHA);
            }
            finally
            {
                Native.SelectObject(memDc, old);
                Native.DeleteObject(hBitmap);
                Native.DeleteDC(memDc);
                Native.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }
    }

    /// <summary>Clickable keyboard used in the settings window.</summary>
    internal sealed class KeyboardView : Control
    {
        public readonly System.Collections.Generic.List<KeyCap> Keys;
        public readonly RenderState State = new RenderState();
        public float Unit { get; private set; }
        public Action<int> KeyClicked;

        public KeyboardView(System.Collections.Generic.List<KeyCap> keys, float unit)
        {
            Keys = keys;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            SetUnit(unit);
            Cursor = Cursors.Hand;
        }

        /// <summary>Size of one key unit in pixels; the control resizes to fit the keyboard.</summary>
        public void SetUnit(float unit)
        {
            if (Math.Abs(unit - Unit) < 0.01f) return;
            Unit = unit;
            Size = SizeFor(unit);
            Invalidate();
        }

        public static Size SizeFor(float unit)
        {
            SizeF s = KeyboardRenderer.Measure(unit);
            return new Size((int)Math.Ceiling(s.Width) + 1, (int)Math.Ceiling(s.Height) + 1);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent != null ? Parent.BackColor : BackColor);
            KeyboardRenderer.Draw(e.Graphics, Keys, Unit, State);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            float m = KeyboardRenderer.Margin(Unit);
            float ux = (e.X - m) / Unit, uy = (e.Y - m) / Unit;
            foreach (KeyCap k in Keys)
            {
                if (k.R.Contains(ux, uy) || (!k.R2.IsEmpty && k.R2.Contains(ux, uy)))
                {
                    if (KeyClicked != null && k.Vk != 0) KeyClicked(k.Vk);
                    return;
                }
            }
        }
    }
}
