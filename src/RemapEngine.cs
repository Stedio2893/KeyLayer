using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;

namespace KeyLayer
{
    internal enum ActivationMode
    {
        SingleTap = 0,  // one tap of the switcher key switches the layer
        DoubleTap = 1,  // two quick taps switch the layer
        Hold = 2,       // layer active only while the switcher key is held
    }

    /// <summary>A key that switches a profile's layer on, with its gesture.</summary>
    internal sealed class Switcher
    {
        public int Vk;
        public ActivationMode Mode;
        public string Profile;
        public Dictionary<int, Mapping> Map;
    }

    /// <summary>
    /// Low-level keyboard/mouse hooks on a dedicated high-priority thread (a busy UI never lags the keyboard).
    /// Handles the switcher gestures and, while a layer is on, swallows remapped keys and injects their targets.
    ///
    /// Switchers that are modifiers (Shift, Ctrl) keep working normally in the tap modes. Any other switcher
    /// (F-keys, Caps Lock, Ins…) is held back: a lone tap that does not complete the gesture is replayed, so
    /// the key still does its normal job.
    /// </summary>
    internal sealed class RemapEngine : IDisposable
    {
        static readonly IntPtr Signature = new IntPtr(0x4B4C5952); // marks our own injected input
        const int WM_APP_TAPTIMEOUT = 0x8000 + 0x4B;
        const int WM_APP_REHOOK = 0x8000 + 0x4C;
        const uint StaleMs = 1200;      // a key "held" longer than this without any event was released without us seeing it
        const uint TapMaxMs = 600;      // a press longer than this is not a tap
        const uint HoldTapMaxMs = 400;  // hold mode, non-modifier switcher: a press shorter than this alone is a plain tap

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
        Timer tapTimer;

        static readonly Dictionary<int, Mapping> Empty = new Dictionary<int, Mapping>();
        volatile Switcher[] switchers = new Switcher[0];
        volatile Switcher current;          // the switcher whose layer is on; null = layer off
        volatile bool activeByHold;
        volatile Switcher holdPrev;         // layer to go back to when a hold ends
        volatile int doubleTapMs = 400;
        volatile bool enabled = true;
        volatile int[] pressedSnapshot = new int[0];
        volatile int holdVkPublic;
        Action<int> capture;

        volatile int lastHookTick = Environment.TickCount;   // last time a hook saw any input
        int lastRehookTick = Environment.TickCount;

        // ---- hook-thread-only state ----
        readonly Dictionary<int, Held> held = new Dictionary<int, Held>();
        readonly HashSet<int> pressed = new HashSet<int>();
        readonly Dictionary<int, uint> lastDown = new Dictionary<int, uint>();
        readonly HashSet<int> swallowUp = new HashSet<int>();
        // modifier switcher, tap modes (the key itself passes through)
        bool modTapDown, modTapDirty;
        int modTapVk;
        uint modTapTime;
        bool lastTapValid;
        int lastTapVk;
        uint lastTapTime;
        // non-modifier switcher, tap modes (the key is held back)
        bool ntDown, ntPass, ntMouse;
        int ntVk;
        uint ntTime, ntScan;
        bool ntExt;
        int pendingVk;                      // a lone first tap waiting for the second one
        uint pendingUpTime, pendingScan;
        bool pendingExt;
        volatile int tapGen;
        // hold mode: the first other key decides whether this hold is "layer" or the plain key
        const int HoldUndecided = 0, HoldLayer = 1, HoldPlain = 2;
        bool holdDown, holdIsMod, holdInjected, holdExt;
        int holdVk, holdDecision;
        uint holdTime, holdScan;

        /// <summary>Raised on the hook thread or the caller's thread: handlers must marshal to the UI.</summary>
        public event EventHandler ActiveChanged;
        public event EventHandler PressedChanged;

        /// <summary>Index 0 is the main switcher (it serves «Layer attivo» in the menu). Replace, never mutate.</summary>
        public Switcher[] Switchers
        {
            get { return switchers; }
            set
            {
                switchers = value ?? new Switcher[0];
                Switcher cur = current;
                if (cur == null) return;
                Switcher match = null;
                foreach (Switcher s in switchers)
                    if (s.Vk == cur.Vk && s.Mode == cur.Mode) match = s; // same key and gesture: stays on (maybe with a new profile)
                if (match == null) { Deactivate(); return; }
                lock (sync) { if (current == cur) current = match; }
                Switcher prev = holdPrev;
                if (prev != null)
                {
                    Switcher pm = null;
                    foreach (Switcher s in switchers) if (s.Vk == prev.Vk && s.Mode == prev.Mode) pm = s;
                    holdPrev = pm;
                }
            }
        }

        public int DoubleTapMs { get { return doubleTapMs; } set { doubleTapMs = value; } }
        public bool Active { get { return current != null; } }
        public bool ActiveIsHold { get { return current != null && activeByHold; } }
        public string ActiveProfile { get { Switcher c = current; return c != null ? c.Profile : null; } }
        public int ActiveVk { get { Switcher c = current; return c != null ? c.Vk : 0; } }
        public Dictionary<int, Mapping> ActiveMap { get { Switcher c = current; return c != null && c.Map != null ? c.Map : Empty; } }
        public int[] Pressed { get { return pressedSnapshot; } }
        /// <summary>The hold-mode switcher being held right now (it is not a "real" Shift / Ctrl), or 0.</summary>
        public int HoldVk { get { return holdVkPublic; } }

        public bool Enabled
        {
            get { return enabled; }
            set { enabled = value; if (!value) Deactivate(); }
        }

        /// <summary>The next physical key press is swallowed and reported to the callback (on the hook thread).</summary>
        public void BeginCapture(Action<int> callback) { Interlocked.Exchange(ref capture, callback); }
        public void CancelCapture() { Interlocked.Exchange(ref capture, null); }

        /// <summary>From the menu / settings: on = the main switcher's layer (selected profile).</summary>
        public void SetActive(bool value)
        {
            if (!value) { Deactivate(); return; }
            Switcher[] sw = switchers;
            if (sw.Length > 0) Activate(sw[0], false);
        }

        void Activate(Switcher s, bool byHold)
        {
            lock (sync)
            {
                if (current == s && activeByHold == byHold) return;
                current = s;
                activeByHold = byHold;
            }
            RaiseActiveChanged();
        }

        void Deactivate()
        {
            lock (sync)
            {
                holdPrev = null;
                if (current == null) return;
                current = null;
                activeByHold = false;
            }
            RaiseActiveChanged();
        }

        void RaiseActiveChanged()
        {
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
                tapTimer = new Timer(delegate (object gen)
                {
                    Native.PostThreadMessage(threadId, WM_APP_TAPTIMEOUT, (IntPtr)tapGen, IntPtr.Zero);
                }, null, Timeout.Infinite, Timeout.Infinite);
                ready.Set();

                while (Native.GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
                {
                    if (msg.hwnd == IntPtr.Zero && msg.message == WM_APP_TAPTIMEOUT)
                    {
                        if (msg.wParam.ToInt32() == tapGen) OnPendingTimeout();
                        continue;
                    }
                    if (msg.hwnd == IntPtr.Zero && msg.message == WM_APP_REHOOK)
                    {
                        Rehook();
                        continue;
                    }
                    Native.TranslateMessage(ref msg);
                    Native.DispatchMessage(ref msg);
                }

                tapTimer.Dispose();
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

        /// <summary>
        /// Puts the hooks back and forgets every half-finished key press. For when Windows may have dropped the
        /// hooks or swallowed key releases: unlock, resume from sleep, or input that never reached us.
        /// </summary>
        public void Reinstall()
        {
            if (threadId != 0) Native.PostThreadMessage(threadId, WM_APP_REHOOK, IntPtr.Zero, IntPtr.Zero);
        }

        /// <summary>
        /// Watchdog (call every few seconds): Windows silently removes a low-level hook that it judges too slow.
        /// If there was input the hooks never saw, reinstall them.
        /// </summary>
        public void CheckHooks()
        {
            uint lastInput = Native.LastInputTick();
            if (lastInput == 0) return;
            int unseen = unchecked((int)(lastInput - (uint)lastHookTick));
            int sinceRehook = unchecked(Environment.TickCount - lastRehookTick);
            if (unseen > 1500 && sinceRehook > 5000) Reinstall();
        }

        void Rehook()
        {
            lastRehookTick = Environment.TickCount;
            ResetState();
            IntPtr hMod = Native.GetModuleHandle(null);
            if (kbHook != IntPtr.Zero) Native.UnhookWindowsHookEx(kbHook);
            if (msHook != IntPtr.Zero) Native.UnhookWindowsHookEx(msHook);
            kbHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, kbProc, hMod, 0);
            msHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, msProc, hMod, 0);
            lastHookTick = Environment.TickCount;
        }

        /// <summary>Releases whatever we hold down and forgets all key-press state (the layer itself stays as it is).</summary>
        void ResetState()
        {
            ReleaseAll();
            if (holdDown)
            {
                holdDown = false;
                holdVkPublic = 0;
                if (holdDecision != HoldPlain) EndHoldLayer();
            }
            ntDown = false;
            modTapDown = false;
            lastTapValid = false;
            pendingVk = 0;
            tapGen++;
            pressed.Clear();
            pressedSnapshot = new int[0];
            swallowUp.Clear();
            lastDown.Clear();
        }

        /// <summary>
        /// A fresh press of a key we believed still down: its release was lost (secure desktop, elevated window…).
        /// Finish what the old press started, so nothing stays stuck.
        /// </summary>
        void RecoverLostRelease(int vk)
        {
            Held hd;
            if (held.TryGetValue(vk, out hd))
            {
                held.Remove(vk);
                var batch = new List<Native.INPUT>();
                if (hd.M.IsSimple && hd.M.Vk != 0) AddKey(batch, hd.M.Vk, true);
                for (int i = hd.Mods.Count - 1; i >= 0; i--) AddKey(batch, hd.Mods[i], true);
                Flush(batch);
            }
            if (holdDown && holdVk == vk)
            {
                holdDown = false;
                holdVkPublic = 0;
                if (holdInjected) { Send(holdVk, true); holdInjected = false; }
                if (holdDecision != HoldPlain) EndHoldLayer();
            }
            if (ntDown && ntVk == vk) ntDown = false;
            if (modTapDown && modTapVk == vk) modTapDown = false;
            swallowUp.Remove(vk);
        }

        /// <summary>
        /// Forgets keys we think are down but Windows says are up (their release happened where we could not see it).
        /// Only for keys whose events reach Windows: the ones we swallow have no reliable system state.
        /// </summary>
        void PruneStaleKeys(int except)
        {
            List<int> gone = null;
            foreach (int p in pressed)
            {
                if (p == except || held.ContainsKey(p) || (holdDown && p == holdVk) || (ntDown && !ntPass && p == ntVk)) continue;
                if ((Native.GetAsyncKeyState(p) & 0x8000) != 0) continue;
                if (gone == null) gone = new List<int>();
                gone.Add(p);
            }
            if (gone == null) return;
            foreach (int p in gone)
            {
                pressed.Remove(p);
                if (modTapDown && modTapVk == p) modTapDown = false;
            }
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

        Switcher FindSwitcher(int vk)
        {
            foreach (Switcher s in switchers) if (s.Vk == vk) return s;
            return null;
        }

        // ------------------------------------------------------------------ mouse

        IntPtr MouseProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            lastHookTick = Environment.TickCount;
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
                        // Shift+click / Shift+wheel is not a tap of the switcher.
                        if (modTapDown) modTapDirty = true;
                        if (ntDown) ntMouse = true;
                        lastTapValid = false;
                        FlushPendingTap();

                        // Hold mode with a modifier: a click before any key means the user wants a plain Shift+click.
                        if (holdDown && holdIsMod && holdDecision == HoldUndecided)
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

        IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            lastHookTick = Environment.TickCount;
            if (nCode < 0) return Native.CallNextHookEx(kbHook, nCode, wParam, lParam);

            var k = (Native.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.KBDLLHOOKSTRUCT));
            if (k.dwExtraInfo == Signature) return Native.CallNextHookEx(kbHook, nCode, wParam, lParam);

            int msg = wParam.ToInt32();
            bool down = msg == Native.WM_KEYDOWN || msg == Native.WM_SYSKEYDOWN;
            bool up = msg == Native.WM_KEYUP || msg == Native.WM_SYSKEYUP;
            int vk = (int)k.vkCode;
            bool ext = (k.flags & 1) != 0;

            if (down)
            {
                // A down of a key we think is already down is normally auto-repeat. If nothing came from it for a
                // long time, though, its release was lost: recover before handling this new press.
                uint last;
                bool wasDown = pressed.Contains(vk);
                if (wasDown && lastDown.TryGetValue(vk, out last) && unchecked(k.time - last) > StaleMs)
                {
                    RecoverLostRelease(vk);
                    pressed.Remove(vk);
                    wasDown = false;
                }
                lastDown[vk] = k.time;
                if (!wasDown) PruneStaleKeys(vk);
            }

            // Track physically pressed keys (overlay highlight)
            bool changed = down ? pressed.Add(vk) : (up && pressed.Remove(vk));
            if (changed)
            {
                var arr = new int[pressed.Count];
                pressed.CopyTo(arr);
                pressedSnapshot = arr;
                if (current != null)
                {
                    EventHandler h = PressedChanged;
                    if (h != null) h(this, EventArgs.Empty);
                }
            }

            // Key capture for the settings window
            if (down && !held.ContainsKey(vk) && !(holdDown && vk == holdVk) && !(ntDown && vk == ntVk))
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

            // ---- switcher presses in progress (finished even if the settings changed meanwhile) ----
            if (holdDown && vk == holdVk) return HoldKey(down, up, k.time);
            if (ntDown && vk == ntVk) return NonModTapKey(nCode, wParam, lParam, down, up, k.time);
            if (up && modTapDown && vk == modTapVk) ModTapUp(k.time);

            // ---- a switcher key goes down ----
            Switcher sw = down && enabled && !held.ContainsKey(vk) ? FindSwitcher(vk) : null;
            if (sw != null)
            {
                if (sw.Mode == ActivationMode.Hold)
                {
                    if (!holdDown) return StartHold(sw, k);
                    // a second hold switcher while one is held: just an ordinary key
                }
                else if (IsModifier(vk))
                {
                    if (!modTapDown || modTapVk != vk)
                    {
                        if (pendingVk != 0) return ReplayFirst(k, down);
                        modTapDown = true; modTapDirty = false; modTapVk = vk; modTapTime = k.time;
                    }
                    return Native.CallNextHookEx(kbHook, nCode, wParam, lParam); // Shift / Ctrl keep working normally
                }
                else
                {
                    if (pendingVk != 0 && pendingVk != vk) FlushPendingTap();
                    ntDown = true; ntPass = false; ntMouse = false;
                    ntVk = vk; ntTime = k.time; ntScan = k.scanCode; ntExt = ext;
                    return (IntPtr)1; // held back until we know whether this is a tap
                }
            }

            if (down)
            {
                if (modTapDown) modTapDirty = true;
                lastTapValid = false;

                // A held-back switcher key must reach the apps before this key: send it first, then this key again.
                if (pendingVk != 0 || (ntDown && !ntPass)) return ReplayFirst(k, down);

                // Hold mode: the first non-modifier key decides what this hold means.
                if (holdDown && holdDecision == HoldUndecided && !IsModifier(vk))
                {
                    Mapping probe; int probeFlags;
                    if (Lookup(vk, out probe, out probeFlags)) holdDecision = HoldLayer;
                    else
                    {
                        BecomePlainHold();
                        if (holdIsMod)
                        {
                            SendScan(vk, k.scanCode, ext, false); // re-inject after the Shift
                            return (IntPtr)1;
                        }
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
                if (current != null && enabled && Lookup(vk, out m, out variant))
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

        /// <summary>
        /// Sends what must precede the current key (a pending tap, a held-back switcher), then the current key
        /// itself without our signature, so the hook sees it again in the right order with the state settled.
        /// </summary>
        IntPtr ReplayFirst(Native.KBDLLHOOKSTRUCT k, bool down)
        {
            var batch = new List<Native.INPUT>();
            if (pendingVk != 0)
            {
                batch.Add(MakeKey(pendingVk, pendingScan, pendingExt, false, true));
                batch.Add(MakeKey(pendingVk, pendingScan, pendingExt, true, true));
                pendingVk = 0;
            }
            if (ntDown && !ntPass)
            {
                batch.Add(MakeKey(ntVk, ntScan, ntExt, false, true));
                ntPass = true;
            }
            pressed.Remove((int)k.vkCode); // it comes back with the replay
            batch.Add(MakeKey((int)k.vkCode, k.scanCode, (k.flags & 1) != 0, !down, false));
            Flush(batch);
            return (IntPtr)1;
        }

        // ---- modifier switcher, tap modes ----

        void ModTapUp(uint time)
        {
            bool tap = !modTapDirty && unchecked(time - modTapTime) < TapMaxMs;
            modTapDown = false;
            Switcher sw = enabled ? FindSwitcher(modTapVk) : null;
            if (tap && sw != null && sw.Mode != ActivationMode.Hold) OnTap(sw, modTapTime, time);
            else lastTapValid = false;
        }

        /// <summary>A clean tap of a tap-mode switcher: single tap switches, double tap needs a second one soon.</summary>
        void OnTap(Switcher sw, uint downTime, uint upTime)
        {
            if (sw.Mode == ActivationMode.SingleTap) { lastTapValid = false; Toggle(sw); return; }
            if (lastTapValid && lastTapVk == sw.Vk && unchecked(downTime - lastTapTime) <= (uint)doubleTapMs)
            {
                lastTapValid = false;
                Toggle(sw);
                return;
            }
            lastTapValid = true;
            lastTapVk = sw.Vk;
            lastTapTime = upTime;
        }

        void Toggle(Switcher sw)
        {
            Switcher cur = current;
            if (cur != null && cur.Vk == sw.Vk && !activeByHold) Deactivate();
            else Activate(sw, false); // off, or another profile's layer: switch to this one
        }

        // ---- non-modifier switcher, tap modes ----

        IntPtr NonModTapKey(int nCode, IntPtr wParam, IntPtr lParam, bool down, bool up, uint time)
        {
            // Already let through (another key was pressed with it): its repeats and release go as they are.
            if (ntPass)
            {
                if (up) ntDown = false;
                return Native.CallNextHookEx(kbHook, nCode, wParam, lParam);
            }
            if (!up) return (IntPtr)1; // auto-repeat while we wait
            ntDown = false;

            Switcher sw = enabled ? FindSwitcher(ntVk) : null;
            bool tap = !ntMouse && unchecked(time - ntTime) < TapMaxMs;
            if (!tap || sw == null || sw.Mode == ActivationMode.Hold)
            {
                lastTapValid = false;
                FlushPendingTap();
                Replay(ntVk, ntScan, ntExt);
                return (IntPtr)1;
            }
            if (sw.Mode == ActivationMode.SingleTap)
            {
                FlushPendingTap();
                Toggle(sw);
                return (IntPtr)1;
            }
            // double tap
            if (pendingVk == ntVk && unchecked(ntTime - pendingUpTime) <= (uint)doubleTapMs)
            {
                pendingVk = 0;
                tapGen++;
                Toggle(sw);
                return (IntPtr)1;
            }
            FlushPendingTap();
            pendingVk = ntVk; pendingScan = ntScan; pendingExt = ntExt; pendingUpTime = time;
            tapGen++;
            tapTimer.Change(doubleTapMs + 30, Timeout.Infinite);
            return (IntPtr)1;
        }

        void OnPendingTimeout()
        {
            if (pendingVk == 0) return;
            if (ntDown && ntVk == pendingVk) return; // the second press is in progress: its release decides
            FlushPendingTap();
        }

        /// <summary>The lone first tap did not become a double tap: give the key its normal job.</summary>
        void FlushPendingTap()
        {
            if (pendingVk == 0) return;
            int v = pendingVk;
            pendingVk = 0;
            tapGen++;
            Replay(v, pendingScan, pendingExt);
        }

        static void Replay(int vk, uint scan, bool ext)
        {
            Native.SendInput(2, new[] { MakeKey(vk, scan, ext, false, true), MakeKey(vk, scan, ext, true, true) }, InputSize);
        }

        // ---- hold mode ----

        IntPtr StartHold(Switcher sw, Native.KBDLLHOOKSTRUCT k)
        {
            FlushPendingTap();
            lastTapValid = false;
            holdDown = true;
            holdVk = sw.Vk;
            holdVkPublic = sw.Vk;
            holdIsMod = IsModifier(sw.Vk);
            holdDecision = HoldUndecided;
            holdInjected = false;
            holdTime = k.time;
            holdScan = k.scanCode;
            holdExt = (k.flags & 1) != 0;
            Switcher prev = current;
            bool prevByHold = activeByHold;
            Activate(sw, true);
            holdPrev = prev != null && !prevByHold ? prev : null;
            return (IntPtr)1;
        }

        /// <summary>Hold mode, the first key is not remapped: the switcher is just its normal key (e.g. Shift for capitals).</summary>
        void BecomePlainHold()
        {
            holdDecision = HoldPlain;
            EndHoldLayer();
            if (holdIsMod)
            {
                Send(holdVk, false);
                holdInjected = true;
            }
        }

        /// <summary>The hold's layer ends: back to the layer that was on before it, if any.</summary>
        void EndHoldLayer()
        {
            Switcher prev = holdPrev;
            holdPrev = null;
            if (current == null) return; // switched off meanwhile (e.g. LayerOff)
            if (prev != null) Activate(prev, false);
            else Deactivate();
        }

        IntPtr HoldKey(bool down, bool up, uint time)
        {
            if (down) return (IntPtr)1; // auto-repeat
            if (!up) return (IntPtr)1;
            holdDown = false;
            holdVkPublic = 0;
            if (holdInjected) { Send(holdVk, true); holdInjected = false; }
            else if (!holdIsMod && holdDecision == HoldUndecided && unchecked(time - holdTime) < HoldTapMaxMs)
            {
                EndHoldLayer();
                Replay(holdVk, holdScan, holdExt); // a quick tap alone: the key's normal job
                return (IntPtr)1;
            }
            if (holdDecision != HoldPlain) EndHoldLayer();
            return (IntPtr)1;
        }

        // ---- lookups and injection ----

        /// <summary>Physically held, and not the hold-mode switcher that is swallowed.</summary>
        bool PhysicallyHeld(int vk)
        {
            return pressed.Contains(vk) && !(holdDown && vk == holdVk);
        }

        /// <summary>Finds the mapping for a key: the Shift/Ctrl variant matching the held modifiers first, then the plain key.</summary>
        bool Lookup(int vk, out Mapping m, out int variant)
        {
            Dictionary<int, Mapping> mp = ActiveMap;
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
                    Deactivate();
                    continue;
                }
                var mods = new List<int>();
                PressMods(batch, s.Mods, mods, suppressed);
                if (s.Vk != 0) { AddKey(batch, s.Vk, false); AddKey(batch, s.Vk, true); }
                for (int i = mods.Count - 1; i >= 0; i--) AddKey(batch, mods[i], true);
            }
            Flush(batch);
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
            if (holdInjected) { AddKey(batch, holdVk, true); holdInjected = false; }
            if (ntDown && ntPass) { AddKey(batch, ntVk, true); ntDown = false; }
            Flush(batch);
        }

        static readonly int InputSize = Marshal.SizeOf(typeof(Native.INPUT));

        static Native.INPUT MakeKey(int vk, uint scan, bool extended, bool keyUp, bool signed = true)
        {
            var inp = new Native.INPUT();
            inp.type = Native.INPUT_KEYBOARD;
            inp.u.ki.wVk = (ushort)vk;
            inp.u.ki.wScan = (ushort)scan;
            uint flags = keyUp ? Native.KEYEVENTF_KEYUP : 0;
            if (extended) flags |= Native.KEYEVENTF_EXTENDEDKEY;
            inp.u.ki.dwFlags = flags;
            inp.u.ki.dwExtraInfo = signed ? Signature : IntPtr.Zero;
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
