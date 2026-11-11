using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace KeyLayer
{
    internal enum ActivationMode
    {
        SingleTap = 0,  // one tap of the toggle key switches the layer
        DoubleTap = 1,  // two quick taps switch the layer
        Hold = 2,       // layer active only while the toggle key is held
    }

    /// <summary>
    /// Low-level keyboard/mouse hooks on a dedicated high-priority thread (a busy UI never lags the keyboard).
    /// Handles the toggle gesture and, while the layer is on, swallows remapped keys and injects their targets.
    /// </summary>
    internal sealed class RemapEngine : IDisposable
    {
        static readonly IntPtr Signature = new IntPtr(0x4B4C5952); // marks our own injected input

        sealed class Held
        {
            public Mapping M;
            public bool HasLayerOff;
            public readonly List<int> Mods = new List<int>();        // modifiers we pressed for the target
            public readonly List<int> Suppressed = new List<int>();  // user's modifiers we released (Shift+X variants)
        }

        readonly object sync = new object();
        Thread thread;
        volatile uint threadId;
        IntPtr kbHook, msHook;
        Native.HookProc kbProc, msProc; // kept alive against GC

        volatile Dictionary<int, Mapping> map = new Dictionary<int, Mapping>();
        volatile int toggleVk = 0xA1;
        volatile int mode = (int)ActivationMode.DoubleTap;
        volatile int doubleTapMs = 400;
        volatile bool enabled = true;
        volatile bool active;
        volatile int[] pressedSnapshot = new int[0];
        Action<int> capture;

        // ---- hook-thread-only state ----
        readonly Dictionary<int, Held> held = new Dictionary<int, Held>();
        readonly HashSet<int> pressed = new HashSet<int>();
        readonly HashSet<int> swallowUp = new HashSet<int>();
        // tap modes
        bool toggleDown, toggleDirty, lastTapValid;
        uint toggleTime, lastTapTime;
        // hold mode: the first other key decides whether this hold is "layer" or plain "shift"
        const int HoldUndecided = 0, HoldLayer = 1, HoldPlain = 2;
        bool holdDown, holdInjectedToggle;
        int holdDecision;

        /// <summary>Raised on the hook thread or the caller's thread: handlers must marshal to the UI.</summary>
        public event EventHandler ActiveChanged;
        public event EventHandler PressedChanged;

        /// <summary>Replace the whole dictionary; never mutate one that was handed over.</summary>
        public Dictionary<int, Mapping> Map
        {
            get { return map; }
            set { map = value ?? new Dictionary<int, Mapping>(); }
        }

        public int ToggleVk { get { return toggleVk; } set { toggleVk = value; } }
        public ActivationMode Mode { get { return (ActivationMode)mode; } set { mode = (int)value; } }
        public int DoubleTapMs { get { return doubleTapMs; } set { doubleTapMs = value; } }
        public bool Active { get { return active; } }
        public int[] Pressed { get { return pressedSnapshot; } }

        public bool Enabled
        {
            get { return enabled; }
            set { enabled = value; if (!value) SetActive(false); }
        }

        /// <summary>The next physical key press is swallowed and reported to the callback (on the hook thread).</summary>
        public void BeginCapture(Action<int> callback) { Interlocked.Exchange(ref capture, callback); }
        public void CancelCapture() { Interlocked.Exchange(ref capture, null); }

        public void SetActive(bool value)
        {
            lock (sync)
            {
                if (active == value) return;
                active = value;
            }
            EventHandler h = ActiveChanged;
            if (h != null) h(this, EventArgs.Empty);
        }

        public void Start()
        {
            Exception error = null;
            var ready = new ManualResetEvent(false);
            thread = new Thread(delegate ()
            {
                threadId = Native.GetCurrentThreadId();
                Native.MSG msg;
                Native.PeekMessage(out msg, IntPtr.Zero, 0, 0, 0); // make sure the thread has a message queue
                kbProc = KeyboardProc;
                msProc = MouseProc;
                IntPtr hMod = Native.GetModuleHandle(null);
                kbHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, kbProc, hMod, 0);
                if (kbHook == IntPtr.Zero)
                {
                    error = new Win32Exception(Marshal.GetLastWin32Error());
                    ready.Set();
                    return;
                }
                msHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, msProc, hMod, 0);
                ready.Set();

                while (Native.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
                {
                    Native.TranslateMessage(ref msg);
                    Native.DispatchMessage(ref msg);
                }

                ReleaseAll();
                Native.UnhookWindowsHookEx(kbHook);
                if (msHook != IntPtr.Zero) Native.UnhookWindowsHookEx(msHook);
            });
            thread.IsBackground = true;
            thread.Name = "KeyLayer hook";
            thread.Priority = ThreadPriority.Highest;
            thread.Start();
            ready.WaitOne();
            if (error != null) throw error;
        }

        public void Dispose()
        {
            if (thread == null) return;
            if (threadId != 0) Native.PostThreadMessage(threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            thread.Join(2000);
            thread = null;
        }

        static bool IsModifier(int vk)
        {
            return (vk >= 0x10 && vk <= 0x12) || (vk >= 0xA0 && vk <= 0xA5) || vk == 0x5B || vk == 0x5C;
        }

        // ------------------------------------------------------------------ mouse

        IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int m = wParam.ToInt32();
                bool button = m == Native.WM_LBUTTONDOWN || m == Native.WM_RBUTTONDOWN || m == Native.WM_MBUTTONDOWN ||
                              m == Native.WM_XBUTTONDOWN || m == Native.WM_MOUSEWHEEL || m == Native.WM_MOUSEHWHEEL;
                if (button)
                {
                    var ms = (Native.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.MSLLHOOKSTRUCT));
                    if (ms.dwExtraInfo != Signature)
                    {
                        // Shift+click / Shift+wheel is not a tap of the toggle key.
                        if (toggleDown) toggleDirty = true;
                        lastTapValid = false;

                        // Hold mode: a click before any key means the user wants a plain Shift+click.
                        if (holdDown && holdDecision == HoldUndecided)
                        {
                            BecomePlainHold();
                            ReinjectMouse(m, ms.mouseData);
                            return (IntPtr)1;
                        }
                    }
                }
            }
            return Native.CallNextHookEx(msHook, nCode, wParam, lParam);
        }

        static void ReinjectMouse(int msg, uint mouseData)
        {
            var inp = new Native.INPUT[1];
            inp[0].type = Native.INPUT_MOUSE;
            uint flags = 0, data = 0;
            switch (msg)
            {
                case Native.WM_LBUTTONDOWN: flags = Native.MOUSEEVENTF_LEFTDOWN; break;
                case Native.WM_RBUTTONDOWN: flags = Native.MOUSEEVENTF_RIGHTDOWN; break;
                case Native.WM_MBUTTONDOWN: flags = Native.MOUSEEVENTF_MIDDLEDOWN; break;
                case Native.WM_XBUTTONDOWN: flags = Native.MOUSEEVENTF_XDOWN; data = mouseData >> 16; break;
                case Native.WM_MOUSEWHEEL: flags = Native.MOUSEEVENTF_WHEEL; data = (uint)(short)(mouseData >> 16); break;
                case Native.WM_MOUSEHWHEEL: flags = Native.MOUSEEVENTF_HWHEEL; data = (uint)(short)(mouseData >> 16); break;
            }
            inp[0].u.mi.dwFlags = flags;
            inp[0].u.mi.mouseData = data;
            inp[0].u.mi.dwExtraInfo = Signature;
            Native.SendInput(1, inp, InputSize);
        }

        // ------------------------------------------------------------------ keyboard

        /// <summary>Hold mode, first event is not a remapped key: behave like the normal key (e.g. Shift for capitals).</summary>
        void BecomePlainHold()
        {
            holdDecision = HoldPlain;
            SetActive(false);
            Send(toggleVk, false);
            holdInjectedToggle = true;
        }

        IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0) return Native.CallNextHookEx(kbHook, nCode, wParam, lParam);

            var k = (Native.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.KBDLLHOOKSTRUCT));
            if (k.dwExtraInfo == Signature) return Native.CallNextHookEx(kbHook, nCode, wParam, lParam);

            int msg = wParam.ToInt32();
            bool down = msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN;
            bool up = msg == Native.WM_KEYUP || msg == Native.WM_SYSKEYUP;
            int vk = (int)k.vkCode;

            // Track physically pressed keys (overlay highlight)
            bool changed = down ? pressed.Add(vk) : (up && pressed.Remove(vk));
            if (changed)
            {
                var arr = new int[pressed.Count];
                pressed.CopyTo(arr);
                pressedSnapshot = arr;
                if (active)
                {
                    EventHandler h = PressedChanged;
                    if (h != null) h(this, EventArgs.Empty);
                }
            }

            // Key capture for the settings window
            if (down && !held.ContainsKey(vk) && !(vk == toggleVk && holdDown))
            {
                Action<int> cb = Interlocked.Exchange(ref capture, null);
                if (cb != null)
                {
                    swallowUp.Add(vk);
                    cb(vk);
                    return (IntPtr)1;
                }
            }
            if (swallowUp.Contains(vk))
            {
                if (up) swallowUp.Remove(vk);
                return (IntPtr)1;
            }

            // ---- the toggle key ----
            if (vk == toggleVk && !held.ContainsKey(vk))
            {
                if (((ActivationMode)mode == ActivationMode.Hold && enabled) || holdDown) // finish a hold even if the mode changed meanwhile
                    return HoldToggleKey(down, up);

                if (down)
                {
                    if (!toggleDown) { toggleDown = true; toggleDirty = false; toggleTime = k.time; }
                }
                else if (up)
                {
                    bool tap = toggleDown && !toggleDirty && unchecked(k.time - toggleTime) < 600;
                    toggleDown = false;
                    if (tap && enabled)
                    {
                        if ((ActivationMode)mode == ActivationMode.SingleTap)
                        {
                            SetActive(!active);
                        }
                        else if (lastTapValid && unchecked(toggleTime - lastTapTime) <= (uint)doubleTapMs)
                        {
                            lastTapValid = false;
                            SetActive(!active);
                        }
                        else
                        {
                            lastTapValid = true;
                            lastTapTime = k.time;
                        }
                    }
                    else lastTapValid = false;
                }
                return Native.CallNextHookEx(kbHook, nCode, wParam, lParam); // the toggle key keeps working normally
            }

            if (down)
            {
                if (toggleDown) toggleDirty = true;
                lastTapValid = false;

                // Hold mode: the first non-modifier key decides what this hold means.
                if (holdDown && holdDecision == HoldUndecided && !IsModifier(vk))
                {
                    Mapping probe; int probeFlags;
                    if (Lookup(vk, out probe, out probeFlags)) holdDecision = HoldLayer;
                    else
                    {
                        BecomePlainHold();
                        SendScan(vk, k.scanCode, (k.flags & 1) != 0, false); // re-inject after the Shift
                        return (IntPtr)1;
                    }
                }
            }

            Held hd;
            if (down)
            {
                if (held.TryGetValue(vk, out hd))
                {
                    // auto-repeat of a key that was remapped when it went down
                    var rep = new List<Native.INPUT>();
                    if (hd.M.IsSimple) { if (hd.M.Vk != 0) AddKey(rep, hd.M.Vk, false); }
                    else if (!hd.HasLayerOff) RunSequence(hd.M, rep, hd.Suppressed);
                    Flush(rep);
                    return (IntPtr)1;
                }
                Mapping m;
                int variant;
                if (active && enabled && Lookup(vk, out m, out variant))
                {
                    hd = new Held { M = m };
                    foreach (KeyStroke s in m.Steps) if (s.Vk == KeyNames.LayerOff) hd.HasLayerOff = true;
                    held[vk] = hd;
                    var batch = new List<Native.INPUT>();
                    // "Shift+G" / "Ctrl+F" variants: the physical modifier must not reach the target.
                    if ((variant & KeyNames.SrcShift) != 0) Suppress(batch, hd.Suppressed, 0xA0, 0xA1);
                    if ((variant & KeyNames.SrcCtrl) != 0) Suppress(batch, hd.Suppressed, 0xA2, 0xA3);
                    if (m.IsSimple)
                    {
                        PressMods(batch, m.Mods, hd.Mods, hd.Suppressed);
                        if (m.Vk != 0) AddKey(batch, m.Vk, false);
                        Flush(batch);
                    }
                    else RunSequence(m, batch, hd.Suppressed);
                    return (IntPtr)1;
                }
            }
            else if (up)
            {
                // Released keys follow the mapping they were pressed with, even if the layer was switched off meanwhile.
                if (held.TryGetValue(vk, out hd))
                {
                    held.Remove(vk);
                    var batch = new List<Native.INPUT>();
                    if (hd.M.IsSimple && hd.M.Vk != 0) AddKey(batch, hd.M.Vk, true);
                    for (int i = hd.Mods.Count - 1; i >= 0; i--) AddKey(batch, hd.Mods[i], true);
                    foreach (int mod in hd.Suppressed) if (pressed.Contains(mod)) AddKey(batch, mod, false); // give the modifier back
                    Flush(batch);
                    return (IntPtr)1;
                }
            }
            return Native.CallNextHookEx(kbHook, nCode, wParam, lParam);
        }

        /// <summary>Physically held, and not the toggle key that hold mode swallows.</summary>
        bool PhysicallyHeld(int vk)
        {
            return pressed.Contains(vk) && !((ActivationMode)mode == ActivationMode.Hold && vk == toggleVk);
        }

        /// <summary>Finds the mapping for a key: the Shift/Ctrl variant matching the held modifiers first, then the plain key.</summary>
        bool Lookup(int vk, out Mapping m, out int variant)
        {
            Dictionary<int, Mapping> mp = map;
            int f = 0;
            if (PhysicallyHeld(0xA0) || PhysicallyHeld(0xA1)) f |= KeyNames.SrcShift;
            if (PhysicallyHeld(0xA2) || PhysicallyHeld(0xA3)) f |= KeyNames.SrcCtrl;
            if (f != 0 && mp.TryGetValue(vk | f, out m)) { variant = f; return true; }
            variant = 0;
            return mp.TryGetValue(vk, out m);
        }

        void Suppress(List<Native.INPUT> batch, List<int> suppressed, int left, int right)
        {
            foreach (int mod in new[] { left, right })
                if (PhysicallyHeld(mod)) { AddKey(batch, mod, true); suppressed.Add(mod); }
        }

        /// <summary>Is any key of a modifier group logically down (held by the user and not suppressed by us)?</summary>
        bool GroupDown(List<int> suppressed, params int[] keys)
        {
            foreach (int k in keys) if (PhysicallyHeld(k) && !suppressed.Contains(k)) return true;
            return false;
        }

        void PressMods(List<Native.INPUT> batch, int mods, List<int> pressedMods, List<int> suppressed)
        {
            if ((mods & Mapping.ModCtrl) != 0 && !GroupDown(suppressed, 0xA2, 0xA3)) { AddKey(batch, 0xA2, false); pressedMods.Add(0xA2); }
            if ((mods & Mapping.ModShift) != 0 && !GroupDown(suppressed, 0xA0, 0xA1)) { AddKey(batch, 0xA0, false); pressedMods.Add(0xA0); }
            if ((mods & Mapping.ModAlt) != 0 && !GroupDown(suppressed, 0xA4, 0xA5)) { AddKey(batch, 0xA4, false); pressedMods.Add(0xA4); }
            if ((mods & Mapping.ModWin) != 0 && !GroupDown(suppressed, 0x5B, 0x5C)) { AddKey(batch, 0x5B, false); pressedMods.Add(0x5B); }
        }

        /// <summary>Taps every step of a sequence; "LayerOff" switches the layer off at that point.</summary>
        void RunSequence(Mapping m, List<Native.INPUT> batch, List<int> suppressed)
        {
            foreach (KeyStroke s in m.Steps)
            {
                if (s.Vk == KeyNames.LayerOff)
                {
                    Flush(batch);
                    SetActive(false);
                    continue;
                }
                var mods = new List<int>();
                PressMods(batch, s.Mods, mods, suppressed);
                if (s.Vk != 0) { AddKey(batch, s.Vk, false); AddKey(batch, s.Vk, true); }
                for (int i = mods.Count - 1; i >= 0; i--) AddKey(batch, mods[i], true);
            }
            Flush(batch);
        }

        /// <summary>Hold mode: the toggle key is swallowed; the layer lives while it is held.</summary>
        IntPtr HoldToggleKey(bool down, bool up)
        {
            if (down)
            {
                if (!holdDown)
                {
                    holdDown = true;
                    holdDecision = HoldUndecided;
                    holdInjectedToggle = false;
                    SetActive(true);
                }
                return (IntPtr)1; // also swallows auto-repeat
            }
            if (up && holdDown)
            {
                holdDown = false;
                if (holdInjectedToggle) Send(toggleVk, true);
                holdInjectedToggle = false;
                SetActive(false);
                return (IntPtr)1;
            }
            return (IntPtr)1;
        }

        void ReleaseAll()
        {
            var batch = new List<Native.INPUT>();
            foreach (Held hd in held.Values)
            {
                if (hd.M.IsSimple && hd.M.Vk != 0) AddKey(batch, hd.M.Vk, true);
                for (int i = hd.Mods.Count - 1; i >= 0; i--) AddKey(batch, hd.Mods[i], true);
            }
            held.Clear();
            if (holdInjectedToggle) { AddKey(batch, toggleVk, true); holdInjectedToggle = false; }
            Flush(batch);
        }

        static readonly int InputSize = Marshal.SizeOf(typeof(Native.INPUT));

        static Native.INPUT MakeKey(int vk, uint scan, bool extended, bool keyUp)
        {
            var inp = new Native.INPUT();
            inp.type = Native.INPUT_KEYBOARD;
            inp.u.ki.wVk = (ushort)vk;
            inp.u.ki.wScan = (ushort)scan;
            uint flags = keyUp ? Native.KEYEVENTF_KEYUP : 0;
            if (extended) flags |= Native.KEYEVENTF_EXTENDEDKEY;
            inp.u.ki.dwFlags = flags;
            inp.u.ki.dwExtraInfo = Signature;
            return inp;
        }

        static void AddKey(List<Native.INPUT> batch, int vk, bool keyUp)
        {
            batch.Add(MakeKey(vk, Native.MapVirtualKey((uint)vk, 0 /* MAPVK_VK_TO_VSC */), KeyNames.IsExtended(vk), keyUp));
        }

        /// <summary>Sends a batch in one SendInput call, so nothing can interleave with it.</summary>
        static void Flush(List<Native.INPUT> batch)
        {
            if (batch.Count == 0) return;
            Native.SendInput((uint)batch.Count, batch.ToArray(), InputSize);
            batch.Clear();
        }

        static void Send(int vk, bool keyUp)
        {
            var b = new List<Native.INPUT>();
            AddKey(b, vk, keyUp);
            Flush(b);
        }

        static void SendScan(int vk, uint scan, bool extended, bool keyUp)
        {
            Native.SendInput(1, new[] { MakeKey(vk, scan, extended, keyUp) }, InputSize);
        }
    }
}
