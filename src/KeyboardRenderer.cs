using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace KeyLayer
{
    internal sealed class RenderState
    {
        public IDictionary<int, Mapping> Map;
        public ICollection<int> Pressed;
        public int SelectedVk = -1;
        public int ToggleVk;
        public bool LayerActive;
        public IntPtr Hkl;
        public Color Background = Color.FromArgb(225, 16, 16, 20);
        public int Variant;          // KeyNames.SrcShift / SrcCtrl: show the mappings of that variant
        public bool StrictVariant;   // true: with a variant, show only its own mappings (settings); false: fall back to plain keys (overlay)
        public string ProfileName;   // drawn in the empty area under the navigation cluster
    }

    /// <summary>Draws the keyboard; shared by the overlay and the settings window.</summary>
    internal static class KeyboardRenderer
    {
        public static readonly Color Yellow = Color.FromArgb(255, 255, 204, 0);
        static readonly Color KeyFill = Color.FromArgb(255, 46, 47, 54);
        static readonly Color KeyFillPressed = Color.FromArgb(255, 100, 102, 115);
        static readonly Color MapFill = Color.FromArgb(255, 58, 49, 6);
        static readonly Color MapFillPressed = Color.FromArgb(255, 130, 108, 8);
        static readonly Color TextNormal = Color.FromArgb(235, 222, 223, 230);
        static readonly Color TextDim = Color.FromArgb(150, 222, 223, 230);
        static readonly Color SelectColor = Color.FromArgb(255, 64, 150, 255);

        public static float Margin(float unit) { return unit * 0.3f; }

        public static SizeF Measure(float unit)
        {
            float m = Margin(unit);
            return new SizeF(Layout.Width * unit + 2 * m, Layout.Height * unit + 2 * m);
        }

        public static void AddRounded(GraphicsPath p, RectangleF r, float rad)
        {
            float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            if (d <= 0.5f) { p.AddRectangle(r); return; }
            p.StartFigure();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
        }

        static RectangleF ToPx(RectangleF u, float unit, float m, float pad)
        {
            return new RectangleF(m + u.X * unit + pad, m + u.Y * unit + pad, u.Width * unit - 2 * pad, u.Height * unit - 2 * pad);
        }

        static GraphicsPath KeyPath(KeyCap k, float unit, float m, float pad, float rad)
        {
            var p = new GraphicsPath(FillMode.Winding);
            AddRounded(p, ToPx(k.R, unit, m, pad), rad);
            if (!k.R2.IsEmpty)
            {
                RectangleF b = ToPx(k.R2, unit, m, pad);
                AddRounded(p, b, rad);
                p.AddRectangle(new RectangleF(b.X, b.Y - 2 * pad - rad, b.Width, 2 * pad + 2 * rad)); // join the two halves
            }
            return p;
        }

        public static void Draw(Graphics g, List<KeyCap> keys, float unit, RenderState st)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            SizeF total = Measure(unit);
            float m = Margin(unit);
            using (var bg = new SolidBrush(st.Background))
            using (var p = new GraphicsPath())
            {
                AddRounded(p, new RectangleF(0, 0, total.Width - 1, total.Height - 1), unit * 0.35f);
                g.FillPath(bg, p);
            }

            float pad = Math.Max(1.5f, unit * 0.055f);
            float rad = unit * 0.14f;

            using (var center = new StringFormat(StringFormatFlags.NoWrap))
            using (var topLeft = new StringFormat(StringFormatFlags.NoWrap))
            {
                center.Alignment = StringAlignment.Center;
                center.LineAlignment = StringAlignment.Center;
                topLeft.Alignment = StringAlignment.Near;
                topLeft.LineAlignment = StringAlignment.Near;

                foreach (KeyCap k in keys)
                {
                    RectangleF r = ToPx(k.R, unit, m, pad);
                    Mapping map = null;
                    bool mapped = false;
                    if (st.Map != null && k.Vk != 0)
                    {
                        if (st.Variant != 0 && st.Map.TryGetValue(k.Vk | st.Variant, out map)) mapped = true;
                        else if ((st.Variant == 0 || !st.StrictVariant) && st.Map.TryGetValue(k.Vk, out map)) mapped = true;
                    }
                    bool isPressed = st.Pressed != null && st.Pressed.Contains(k.Vk);
                    bool isToggle = k.Vk == st.ToggleVk;

                    Color fill = mapped ? (isPressed ? MapFillPressed : MapFill) : (isPressed ? KeyFillPressed : KeyFill);
                    using (GraphicsPath path = KeyPath(k, unit, m, pad, rad))
                    {
                        using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                        if (k.R2.IsEmpty && (mapped || (isToggle && st.LayerActive)))
                            using (var pen = new Pen(Color.FromArgb(mapped ? 190 : 255, Yellow), Math.Max(1f, unit * 0.045f)))
                                g.DrawPath(pen, path);
                        if (k.Vk == st.SelectedVk)
                            using (var pen = new Pen(SelectColor, Math.Max(2f, unit * 0.09f)))
                                g.DrawPath(pen, path);
                    }

                    string orig = KeyNames.Label(k.Vk, st.Hkl);
                    if (mapped)
                    {
                        var small = new RectangleF(r.X + unit * 0.06f, r.Y + unit * 0.02f, r.Width - unit * 0.1f, unit * 0.3f);
                        DrawText(g, orig, small, unit * 0.22f, TextDim, topLeft, false);
                        var main = new RectangleF(r.X + unit * 0.04f, r.Y + unit * 0.22f, r.Width - unit * 0.08f, r.Height - unit * 0.24f);
                        string lbl = map.ShortLabel(st.Hkl);
                        DrawText(g, lbl, main, lbl.Length <= 2 ? unit * 0.46f : unit * 0.3f, Yellow, center, true);
                    }
                    else
                    {
                        var tr = RectangleF.Inflate(r, -unit * 0.05f, -unit * 0.03f);
                        Color c = (isToggle && st.LayerActive) ? Yellow : TextNormal;
                        DrawText(g, orig, tr, orig.Length <= 2 ? unit * 0.38f : unit * 0.25f, c, center, false);
                    }
                }

                // Profile name (and active variant) in the free space under Ins/Home/PgUp - Del/End/PgDn
                string info = st.ProfileName ?? "";
                if (st.Variant != 0)
                    info += (info.Length > 0 ? "\n" : "") + ((st.Variant & KeyNames.SrcCtrl) != 0 ? "Ctrl" : "") +
                            ((st.Variant & KeyNames.SrcMask) == KeyNames.SrcMask ? "+" : "") +
                            ((st.Variant & KeyNames.SrcShift) != 0 ? "Shift" : "") + " +";
                if (info.Length > 0)
                {
                    var area = new RectangleF(m + 15.25f * unit, m + 3.3f * unit, 3f * unit, 0.9f * unit);
                    using (var sf = new StringFormat())
                    {
                        sf.Alignment = StringAlignment.Center;
                        sf.LineAlignment = StringAlignment.Center;
                        DrawText(g, info, area, unit * 0.3f, st.Variant != 0 ? Yellow : TextDim, sf, true);
                    }
                }
            }
        }

        static bool NeedsSymbolFont(string s)
        {
            foreach (char c in s) if (c >= 0x2190 && c <= 0x2BFF) return true;
            return false;
        }

        static void DrawText(Graphics g, string s, RectangleF r, float px, Color c, StringFormat sf, bool bold)
        {
            if (string.IsNullOrEmpty(s) || r.Width <= 1 || r.Height <= 1) return;
            string family = NeedsSymbolFont(s) ? "Segoe UI Symbol" : "Segoe UI";
            FontStyle style = bold ? FontStyle.Bold : FontStyle.Regular;
            float size = px;
            while (true)
            {
                using (var f = new Font(family, size, style, GraphicsUnit.Pixel))
                {
                    // Same (non-typographic) layout DrawString uses, padding included, so the fitted text is never clipped.
                    SizeF sz = g.MeasureString(s, f);
                    if ((sz.Width <= r.Width && sz.Height <= r.Height * 1.3f) || size < 7f)
                    {
                        using (var b = new SolidBrush(c)) g.DrawString(s, f, b, r, sf);
                        return;
                    }
                }
                size *= 0.9f;
            }
        }
    }
}
