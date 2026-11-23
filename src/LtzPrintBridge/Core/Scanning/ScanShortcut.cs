using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Threading;

namespace LtzPrintBridge.Core.Scanning;

/// <summary>
/// Ctrl+Shift+S anywhere on the computer, to start, wake or stop the scanner without going to the tray icon.
/// Windows: RegisterHotKey on PrintBridge's main window. macOS: a Carbon hot key (needs no extra permission).
/// </summary>
public static class ScanShortcut
{
    public const string Text = "Ctrl+Shift+S";

    static Action? _pressed;

    /// <summary>Registers the shortcut. Returns false when another app already owns it.</summary>
    public static bool Register(Window host, Action pressed)
    {
        _pressed = pressed;
        try
        {
            bool ok = OperatingSystem.IsWindows() ? Win.Register(host) : OperatingSystem.IsMacOS() && Mac.Register();
            Log.Info(ok ? $"Scanner shortcut {Text} ready" : $"Scanner shortcut {Text} is taken by another app");
            return ok;
        }
        catch (Exception e)
        {
            Log.Error("Scanner shortcut could not be registered", e);
            return false;
        }
    }

    static void Raise() => Dispatcher.UIThread.Post(() => _pressed?.Invoke());

    static class Win
    {
        [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
        const uint MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000, VK_S = 0x53;
        const uint WM_HOTKEY = 0x0312;
        const int Id = 0x5342; // "SB"
        static Win32Properties.CustomWndProcHookCallback? _hook;

        public static bool Register(Window host)
        {
            var hwnd = host.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (hwnd == IntPtr.Zero) return false;
            _hook = (IntPtr h, uint msg, IntPtr w, IntPtr l, ref bool handled) =>
            {
                if (msg == WM_HOTKEY && w.ToInt32() == Id) { handled = true; Raise(); }
                return IntPtr.Zero;
            };
            Win32Properties.AddWndProcHookCallback(host, _hook);
            return RegisterHotKey(hwnd, Id, MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, VK_S);
        }
    }

    static class Mac
    {
        const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";

        [StructLayout(LayoutKind.Sequential)] struct EventTypeSpec { public uint eventClass; public uint eventKind; }
        [StructLayout(LayoutKind.Sequential)] struct EventHotKeyID { public uint signature; public uint id; }
        delegate int EventHandlerProc(IntPtr callRef, IntPtr eventRef, IntPtr userData);

        [DllImport(Carbon)] static extern IntPtr GetApplicationEventTarget();
        [DllImport(Carbon)] static extern int InstallEventHandler(IntPtr target, EventHandlerProc handler, uint count, EventTypeSpec[] types, IntPtr userData, out IntPtr handlerRef);
        [DllImport(Carbon)] static extern int RegisterEventHotKey(uint keyCode, uint modifiers, EventHotKeyID id, IntPtr target, uint options, out IntPtr hotKeyRef);

        const uint kEventClassKeyboard = 0x6B657962; // 'keyb'
        const uint kEventHotKeyPressed = 5;
        const uint controlKey = 1 << 12, shiftKey = 1 << 9;
        const uint kVK_ANSI_S = 0x01;
        static EventHandlerProc? _handler; // kept alive for the life of the app

        public static bool Register()
        {
            var target = GetApplicationEventTarget();
            _handler = (_, _, _) => { Raise(); return 0; };
            var types = new[] { new EventTypeSpec { eventClass = kEventClassKeyboard, eventKind = kEventHotKeyPressed } };
            if (InstallEventHandler(target, _handler, 1, types, IntPtr.Zero, out _) != 0) return false;
            var id = new EventHotKeyID { signature = 0x50524242 /* 'PRBB' */, id = 1 };
            return RegisterEventHotKey(kVK_ANSI_S, controlKey | shiftKey, id, target, 0, out _) == 0;
        }
    }
}

/// <summary>A short beep on each successful scan, like a USB scanner.</summary>
public static class ScanBeep
{
    [DllImport("kernel32.dll")] static extern bool Beep(uint frequency, uint durationMs);
    [DllImport("/usr/lib/libobjc.A.dylib")] static extern IntPtr objc_getClass(string name);
    [DllImport("/usr/lib/libobjc.A.dylib")] static extern IntPtr sel_registerName(string name);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] static extern IntPtr Send(IntPtr receiver, IntPtr selector);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr arg);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] static extern IntPtr SendUtf8(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string arg);

    /// <summary>Call on the UI thread.</summary>
    public static void Play()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                Task.Run(() => Beep(2200, 70));
            }
            else if (OperatingSystem.IsMacOS())
            {
                NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");
                var name = SendUtf8(objc_getClass("NSString"), sel_registerName("stringWithUTF8String:"), "Tink");
                var sound = Send(objc_getClass("NSSound"), sel_registerName("soundNamed:"), name);
                if (sound != IntPtr.Zero) Send(sound, sel_registerName("play"));
            }
        }
        catch (Exception e) { Log.Warn("Beep: " + e.Message); }
    }
}
