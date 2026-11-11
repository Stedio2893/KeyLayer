using System;
using System.Collections.Generic;
using System.Drawing;

namespace KeyLayer
{
    /// <summary>One physical key of the drawn keyboard. Coordinates are in key units (1u = one letter key).</summary>
    internal sealed class KeyCap
    {
        public RectangleF R;
        public RectangleF R2 = RectangleF.Empty; // lower part of the ISO Enter key
        public int Scan;
        public int FixedVk;   // non-zero for keys whose VK does not depend on the layout
        public int Vk;        // resolved VK for the current layout
    }

    /// <summary>ISO (Italian/European) keyboard: function row, main block, navigation cluster and arrows.</summary>
    internal static class Layout
    {
        public const float Width = 18.25f;
        public const float Height = 6.25f;

        static KeyCap Add(List<KeyCap> l, float x, float y, float w, int scan, int vk)
        {
            var k = new KeyCap { R = new RectangleF(x, y, w, 1f), Scan = scan, FixedVk = vk, Vk = vk };
            l.Add(k);
            return k;
        }

        public static List<KeyCap> Build()
        {
            var l = new List<KeyCap>();

            // Function row
            Add(l, 0, 0, 1, 0x01, 0x1B);
            float[] fx = { 2, 3, 4, 5, 6.5f, 7.5f, 8.5f, 9.5f, 11, 12, 13, 14 };
            int[] fsc = { 0x3B, 0x3C, 0x3D, 0x3E, 0x3F, 0x40, 0x41, 0x42, 0x43, 0x44, 0x57, 0x58 };
            for (int i = 0; i < 12; i++) Add(l, fx[i], 0, 1, fsc[i], 0x70 + i);

            // Number row
            float y = 1.25f;
            Add(l, 0, y, 1, 0x29, 0);
            for (int i = 0; i < 12; i++) Add(l, 1 + i, y, 1, 0x02 + i, 0);
            Add(l, 13, y, 2, 0x0E, 0x08);

            // Q row
            y += 1;
            Add(l, 0, y, 1.5f, 0x0F, 0x09);
            for (int i = 0; i < 12; i++) Add(l, 1.5f + i, y, 1, 0x10 + i, 0);
            KeyCap enter = Add(l, 13.5f, y, 1.5f, 0x1C, 0x0D);
            enter.R2 = new RectangleF(13.75f, y + 1, 1.25f, 1f);

            // A row
            y += 1;
            Add(l, 0, y, 1.75f, 0x3A, 0x14);
            for (int i = 0; i < 11; i++) Add(l, 1.75f + i, y, 1, 0x1E + i, 0);
            Add(l, 12.75f, y, 1, 0x2B, 0);

            // Z row
            y += 1;
            Add(l, 0, y, 1.25f, 0x2A, 0xA0);
            Add(l, 1.25f, y, 1, 0x56, 0);
            for (int i = 0; i < 10; i++) Add(l, 2.25f + i, y, 1, 0x2C + i, 0);
            Add(l, 12.25f, y, 2.75f, 0x36, 0xA1);

            // Bottom row
            y += 1;
            float x = 0;
            int[] bvk = { 0xA2, 0x5B, 0xA4, 0x20, 0xA5, 0x5C, 0x5D, 0xA3 };
            int[] bsc = { 0x1D, 0x5B, 0x38, 0x39, 0x38, 0x5C, 0x5D, 0x1D };
            float[] bw = { 1.25f, 1.25f, 1.25f, 6.25f, 1.25f, 1.25f, 1.25f, 1.25f };
            for (int i = 0; i < bvk.Length; i++) { Add(l, x, y, bw[i], bsc[i], bvk[i]); x += bw[i]; }

            // Navigation cluster + arrows
            float nx = 15.25f;
            Add(l, nx, 0, 1, 0x37, 0x2C);
            Add(l, nx + 1, 0, 1, 0x46, 0x91);
            Add(l, nx + 2, 0, 1, 0x45, 0x13);
            Add(l, nx, 1.25f, 1, 0x52, 0x2D);
            Add(l, nx + 1, 1.25f, 1, 0x47, 0x24);
            Add(l, nx + 2, 1.25f, 1, 0x49, 0x21);
            Add(l, nx, 2.25f, 1, 0x53, 0x2E);
            Add(l, nx + 1, 2.25f, 1, 0x4F, 0x23);
            Add(l, nx + 2, 2.25f, 1, 0x51, 0x22);
            Add(l, nx + 1, 4.25f, 1, 0x48, 0x26);
            Add(l, nx, 5.25f, 1, 0x4B, 0x25);
            Add(l, nx + 1, 5.25f, 1, 0x50, 0x28);
            Add(l, nx + 2, 5.25f, 1, 0x4D, 0x27);

            return l;
        }

        /// <summary>Resolves the VK of character keys for the given keyboard layout (QWERTY, AZERTY, ...).</summary>
        public static void Resolve(List<KeyCap> keys, IntPtr hkl)
        {
            foreach (KeyCap k in keys)
                k.Vk = k.FixedVk != 0 ? k.FixedVk : (int)Native.MapVirtualKeyEx((uint)k.Scan, 1 /* MAPVK_VSC_TO_VK */, hkl);
        }
    }
}
