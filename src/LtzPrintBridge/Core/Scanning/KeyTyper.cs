using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace LtzPrintBridge.Core.Scanning;

/// <summary>
/// Types scanned text into whichever field has the keyboard focus, the way a USB barcode scanner does.
/// Windows: SendInput with Unicode key events. macOS: CGEvent keyboard events (needs the Accessibility permission).
/// </summary>
public static class KeyTyper
{
    // ---------- which window to type into ----------

    static IntPtr _lastOutside;

    /// <summary>
    /// Windows: remembers the last window the user worked in that is not PrintBridge or the taskbar.
    /// Picking the tray menu moves focus to the taskbar, so this is where the text must go back to. Call it every few hundred ms.
    /// </summary>
    public static void TrackForeground()
    {
        if (!OperatingSystem.IsWindows()) return;
        var h = Win.GetForegroundWindow();
        if (h == IntPtr.Zero) return;
        Win.GetWindowThreadProcessId(h, out uint pid);
        if (pid == (uint)Environment.ProcessId) return;
        var cls = new StringBuilder(128);
        Win.GetClassName(h, cls, cls.Capacity);
        if (cls.ToString() is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland" or "Progman" or "WorkerW") return;
        _lastOutside = h;
    }

    /// <summary>Gives the keyboard focus back to the page the user was on before choosing Scan.</summary>
    public static void ReturnFocus()
    {
        if (OperatingSystem.IsWindows())
        {
            if (_lastOutside != IntPtr.Zero && Win.GetForegroundWindow() != _lastOutside) Win.SetForegroundWindow(_lastOutside);
        }
        else if (OperatingSystem.IsMacOS())
        {
            Mac.HideIfActive();
        }
    }

    public static string? TargetName()
    {
        if (!OperatingSystem.IsWindows() || _lastOutside == IntPtr.Zero) return null;
        var sb = new StringBuilder(256);
        Win.GetWindowText(_lastOutside, sb, sb.Capacity);
        return sb.Length > 0 ? sb.ToString() : null;
    }

    // ---------- permission ----------

    /// <summary>macOS only lets an app type into other apps once it is allowed under Privacy &amp; Security › Accessibility.</summary>
    public static bool CanType(bool prompt)
    {
        if (!OperatingSystem.IsMacOS()) return true;
        try { return Mac.IsTrusted(prompt); } catch (Exception e) { Log.Warn("Accessibility check: " + e.Message); return false; }
    }

    public static void OpenAccessibilitySettings()
    {
        if (!OperatingSystem.IsMacOS()) return;
        try { Process.Start("open", "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility"); } catch { }
    }

    // ---------- typing ----------

    public static void Type(string text, ScanSuffix suffix)
    {
        if (OperatingSystem.IsWindows()) Win.Type(text, suffix);
        else if (OperatingSystem.IsMacOS()) Mac.Type(text, suffix);
        else throw new PlatformNotSupportedException();
    }

    static class Win
    {
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder name, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);
        [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);

        const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 0x2, KEYEVENTF_UNICODE = 0x4;
        const ushort VK_RETURN = 0x0D, VK_TAB = 0x09;

        [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)] struct HARDWAREINPUT { public uint uMsg; public ushort wParamL, wParamH; }
        [StructLayout(LayoutKind.Explicit)] struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; [FieldOffset(0)] public HARDWAREINPUT hi; }
        [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public InputUnion u; }

        static INPUT Key(ushort vk, ushort scan, uint flags) => new() { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags } } };

        public static void Type(string text, ScanSuffix suffix)
        {
            var list = new List<INPUT>();
            foreach (var ch in text)
            {
                list.Add(Key(0, ch, KEYEVENTF_UNICODE));
                list.Add(Key(0, ch, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP));
            }
            ushort? vk = suffix switch { ScanSuffix.Enter => VK_RETURN, ScanSuffix.Tab => VK_TAB, _ => null };
            if (vk is { } v) { list.Add(Key(v, 0, 0)); list.Add(Key(v, 0, KEYEVENTF_KEYUP)); }
            var arr = list.ToArray();
            uint sent = SendInput((uint)arr.Length, arr, Marshal.SizeOf<INPUT>());
            if (sent != arr.Length) throw new InvalidOperationException($"Windows accepted {sent} of {arr.Length} key events (error {Marshal.GetLastWin32Error()}).");
        }
    }

    static class Mac
    {
        const string AppServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
        const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        const string ObjC = "/usr/lib/libobjc.A.dylib";

        [DllImport(AppServices)] static extern IntPtr CGEventCreateKeyboardEvent(IntPtr source, ushort key, [MarshalAs(UnmanagedType.I1)] bool down);
        [DllImport(AppServices)] static extern void CGEventKeyboardSetUnicodeString(IntPtr ev, nuint length, [MarshalAs(UnmanagedType.LPArray)] char[] chars);
        [DllImport(AppServices)] static extern void CGEventPost(int tap, IntPtr ev);
        [DllImport(AppServices)] [return: MarshalAs(UnmanagedType.I1)] static extern bool AXIsProcessTrustedWithOptions(IntPtr options);
        [DllImport(CoreFoundation)] static extern void CFRelease(IntPtr obj);
        [DllImport(CoreFoundation)] static extern IntPtr CFDictionaryCreate(IntPtr alloc, IntPtr[] keys, IntPtr[] values, nint count, IntPtr keyCallbacks, IntPtr valueCallbacks);
        [DllImport(ObjC)] static extern IntPtr objc_getClass(string name);
        [DllImport(ObjC)] static extern IntPtr sel_registerName(string name);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern IntPtr Send(IntPtr receiver, IntPtr selector);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] static extern bool SendBool(IntPtr receiver, IntPtr selector);

        const int kCGHIDEventTap = 0;
        const ushort kVK_Return = 36, kVK_Tab = 48;

        public static bool IsTrusted(bool prompt)
        {
            if (!prompt) return AXIsProcessTrustedWithOptions(IntPtr.Zero);
            var ax = NativeLibrary.Load(AppServices);
            var cf = NativeLibrary.Load(CoreFoundation);
            var key = Marshal.ReadIntPtr(NativeLibrary.GetExport(ax, "kAXTrustedCheckOptionPrompt"));
            var yes = Marshal.ReadIntPtr(NativeLibrary.GetExport(cf, "kCFBooleanTrue"));
            var dict = CFDictionaryCreate(IntPtr.Zero, new[] { key }, new[] { yes }, 1,
                NativeLibrary.GetExport(cf, "kCFTypeDictionaryKeyCallBacks"), NativeLibrary.GetExport(cf, "kCFTypeDictionaryValueCallBacks"));
            try { return AXIsProcessTrustedWithOptions(dict); }
            finally { if (dict != IntPtr.Zero) CFRelease(dict); }
        }

        /// <summary>If PrintBridge is the active app (the user clicked its scan window), hide it so the browser becomes active again.</summary>
        public static void HideIfActive()
        {
            try
            {
                var app = Send(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
                if (SendBool(app, sel_registerName("isActive"))) Send(app, sel_registerName("hide:"), IntPtr.Zero);
            }
            catch (Exception e) { Log.Warn("Could not hand focus back: " + e.Message); }
        }

        static void Post(ushort key, char[]? chars)
        {
            foreach (var down in new[] { true, false })
            {
                var ev = CGEventCreateKeyboardEvent(IntPtr.Zero, key, down);
                if (ev == IntPtr.Zero) throw new InvalidOperationException("macOS refused to create a key event.");
                if (chars != null) CGEventKeyboardSetUnicodeString(ev, (nuint)chars.Length, chars);
                CGEventPost(kCGHIDEventTap, ev);
                CFRelease(ev);
            }
        }

        public static void Type(string text, ScanSuffix suffix)
        {
            foreach (var ch in text)
            {
                Post(0, new[] { ch });
                Thread.Sleep(2);
            }
            if (suffix == ScanSuffix.Enter) Post(kVK_Return, null);
            else if (suffix == ScanSuffix.Tab) Post(kVK_Tab, null);
        }
    }
}
