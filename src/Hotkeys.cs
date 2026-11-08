using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace YukisPad
{
    internal sealed class Hotkeys : IDisposable
    {
        [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr window, int id);
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        readonly HwndSource source;
        readonly Dictionary<int, Action> actions = new Dictionary<int, Action>();
        public Hotkeys(IntPtr hwnd) { source = HwndSource.FromHwnd(hwnd); source.AddHook(Hook); }
        public bool Add(int id, uint mods, int key, Action action)
        {
            if (!RegisterHotKey(source.Handle, id, mods | 0x4000, (uint)key)) return false;
            actions[id] = action; return true;
        }
        IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            Action action;
            if (msg == 0x312 && actions.TryGetValue(wParam.ToInt32(), out action)) { action(); handled = true; }
            return IntPtr.Zero;
        }
        public void Clear() { foreach (int id in actions.Keys) UnregisterHotKey(source.Handle, id); actions.Clear(); }
        public bool Available(uint mods, int key) { const int probe = 0xBFFF; bool ok = RegisterHotKey(source.Handle, probe, mods | 0x4000, (uint)key); if (ok) UnregisterHotKey(source.Handle, probe); return ok; }
        public void Dispose() { Clear(); source.RemoveHook(Hook); }
        public static string Label(uint mods, int key)
        {
            return ((mods & 2) != 0 ? "Ctrl + " : "") + ((mods & 1) != 0 ? "Alt + " : "") + ((mods & 4) != 0 ? "Shift + " : "") + ((mods & 8) != 0 ? "Win + " : "") + KeyInterop.KeyFromVirtualKey(key);
        }
        internal void VerifyDispatch()
        {
            bool called = false;
            if (!Add(12000, 7, 122, delegate { called = true; })) throw new InvalidOperationException("Test shortcut Ctrl+Alt+Shift+F11 is occupied.");
            try
            {
                SendMessage(source.Handle, 0x312, new IntPtr(12000), IntPtr.Zero);
                if (!called) throw new InvalidOperationException("Hotkey hook failed.");
            }
            finally { Clear(); }
        }
    }
}
