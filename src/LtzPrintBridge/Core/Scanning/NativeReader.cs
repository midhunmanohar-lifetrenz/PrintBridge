using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace LtzPrintBridge.Core.Scanning;

/// <summary>
/// The barcode reader's native library is embedded in PrintBridge (see the csproj) rather than shipped beside it,
/// because its file name clashes with ZXing.Net on Windows and the installer copies only PrintBridge.exe.
/// On first use it is unpacked into the data folder under a name that includes its hash, then loaded from there.
/// </summary>
public static class NativeReader
{
    static int _registered;

    /// <summary>Runs when PrintBridge's code is first loaded, so every use of the reader (not only FrameDecoder) finds it.</summary>
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Init() => Register();

    public static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1) return;
        try { NativeLibrary.SetDllImportResolver(typeof(ZXingCpp.BarcodeReader).Assembly, Resolve); }
        catch (InvalidOperationException) { /* a resolver is already set */ }
    }

    static IntPtr _handle;

    static IntPtr Resolve(string name, Assembly asm, DllImportSearchPath? path)
    {
        if (name != "ZXing") return IntPtr.Zero;
        if (_handle != IntPtr.Zero) return _handle;
        lock (typeof(NativeReader))
        {
            if (_handle != IntPtr.Zero) return _handle;
            using var res = typeof(NativeReader).Assembly.GetManifestResourceStream("zxingcpp-native")
                ?? throw new DllNotFoundException("The barcode reader library is missing from this build of PrintBridge.");
            using var mem = new MemoryStream();
            res.CopyTo(mem);
            var bytes = mem.ToArray();
            var hash = Convert.ToHexString(SHA256.HashData(bytes))[..12].ToLowerInvariant();
            var ext = OperatingSystem.IsWindows() ? ".dll" : OperatingSystem.IsMacOS() ? ".dylib" : ".so";
            var dir = Path.Combine(AppPaths.Dir, "native");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"zxingcpp-{hash}{ext}");
            if (!File.Exists(file) || new FileInfo(file).Length != bytes.Length)
            {
                var tmp = file + "." + Environment.ProcessId + ".tmp";
                File.WriteAllBytes(tmp, bytes);
                File.Move(tmp, file, true);
            }
            _handle = NativeLibrary.Load(file);
            return _handle;
        }
    }
}
