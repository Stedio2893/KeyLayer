using System;
using System.Runtime.InteropServices;

namespace KeyLayer
{
    [ComImport, Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IVirtualDesktopManager
    {
        [PreserveSig] int IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow, out int onCurrentDesktop);
        [PreserveSig] int GetWindowDesktopId(IntPtr topLevelWindow, out Guid desktopId);
        [PreserveSig] int MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);
    }

    /// <summary>Windows 10/11 virtual desktops (documented IVirtualDesktopManager). Use from the UI (STA) thread.</summary>
    internal static class VirtualDesktop
    {
        static IVirtualDesktopManager mgr;
        static bool tried;

        static IVirtualDesktopManager Manager
        {
            get
            {
                if (!tried)
                {
                    tried = true;
                    try
                    {
                        Type t = Type.GetTypeFromCLSID(new Guid("aa509086-5ca9-4c25-8f95-589d3c07b48a"));
                        if (t != null) mgr = (IVirtualDesktopManager)Activator.CreateInstance(t);
                    }
                    catch (Exception) { mgr = null; }
                }
                return mgr;
            }
        }

        /// <summary>True when the window is on the desktop the user is looking at (or when it can't be determined).</summary>
        public static bool IsOnCurrent(IntPtr hwnd)
        {
            IVirtualDesktopManager m = Manager;
            if (m == null || hwnd == IntPtr.Zero) return true;
            try
            {
                int on;
                int hr = m.IsWindowOnCurrentVirtualDesktop(hwnd, out on);
                return hr != 0 || on != 0;
            }
            catch (Exception) { return true; }
        }
    }

    /// <summary>
    /// Turns off the Sticky Keys feature and the Sticky/Filter Keys shortcuts (5x Shift, hold right Shift 8 s)
    /// while KeyLayer runs. Changes are session-only (not written to the profile) and restored on exit.
    /// </summary>
    internal static class AccessibilityGuard
    {
        const uint SKF_STICKYKEYSON = 0x1, SKF_HOTKEYACTIVE = 0x4, SKF_CONFIRMHOTKEY = 0x8;
        const uint FKF_HOTKEYACTIVE = 0x4, FKF_CONFIRMHOTKEY = 0x8;

        static Native.STICKYKEYS origSticky;
        static Native.FILTERKEYS origFilter;
        static bool applied;

        public static bool IsApplied { get { return applied; } }

        public static void Apply()
        {
            if (applied) return;
            var sk = new Native.STICKYKEYS { cbSize = (uint)Marshal.SizeOf(typeof(Native.STICKYKEYS)) };
            var fk = new Native.FILTERKEYS { cbSize = (uint)Marshal.SizeOf(typeof(Native.FILTERKEYS)) };
            if (!Native.SystemParametersInfo(Native.SPI_GETSTICKYKEYS, sk.cbSize, ref sk, 0)) return;
            bool haveFilter = Native.SystemParametersInfo(Native.SPI_GETFILTERKEYS, fk.cbSize, ref fk, 0);
            origSticky = sk;
            origFilter = fk;

            sk.dwFlags &= ~(SKF_STICKYKEYSON | SKF_HOTKEYACTIVE | SKF_CONFIRMHOTKEY);
            Native.SystemParametersInfo(Native.SPI_SETSTICKYKEYS, sk.cbSize, ref sk, 0);
            if (haveFilter)
            {
                fk.dwFlags &= ~(FKF_HOTKEYACTIVE | FKF_CONFIRMHOTKEY);
                Native.SystemParametersInfo(Native.SPI_SETFILTERKEYS, fk.cbSize, ref fk, 0);
            }
            else origFilter.cbSize = 0;
            applied = true;
        }

        public static void Restore()
        {
            if (!applied) return;
            Native.SystemParametersInfo(Native.SPI_SETSTICKYKEYS, origSticky.cbSize, ref origSticky, 0);
            if (origFilter.cbSize != 0)
                Native.SystemParametersInfo(Native.SPI_SETFILTERKEYS, origFilter.cbSize, ref origFilter, 0);
            applied = false;
        }
    }
}
