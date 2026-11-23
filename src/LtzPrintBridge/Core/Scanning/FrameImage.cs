using System.Runtime.InteropServices;
using SkiaSharp;

namespace LtzPrintBridge.Core.Scanning;

/// <summary>
/// Turns a camera frame into a bitmap. Cameras that send MJPEG give JPEG bytes; the rest (RGB32, UYVY and so on, the
/// usual case on Mac) come from FlashCap as an uncompressed BMP, which is read here directly in case the image decoder
/// rejects it.
/// </summary>
public static class FrameImage
{
    public static SKBitmap? Decode(byte[] data)
    {
        if (data.Length < 2) return null;
        if (data[0] == (byte)'B' && data[1] == (byte)'M' && ReadBmp(data) is { } bmp) return bmp;
        return SKBitmap.Decode(data);
    }

    /// <summary>Uncompressed 24- or 32-bit BMP (bottom-up or top-down), as FlashCap produces.</summary>
    static SKBitmap? ReadBmp(byte[] d)
    {
        if (d.Length < 54) return null;
        int offset = BitConverter.ToInt32(d, 10);
        int width = BitConverter.ToInt32(d, 18);
        int height = BitConverter.ToInt32(d, 22);
        int bpp = BitConverter.ToInt16(d, 28);
        int compression = BitConverter.ToInt32(d, 30);
        bool topDown = height < 0;
        height = Math.Abs(height);
        if (width <= 0 || height <= 0 || (bpp != 24 && bpp != 32) || (compression != 0 && compression != 3)) return null;
        int stride = (width * bpp / 8 + 3) & ~3;
        if (offset < 0 || offset + (long)stride * height > d.Length) return null;

        // Windows sends 32-bit pixels as B,G,R,X. The Mac camera sends A,R,G,B, whose always-full alpha byte would be
        // read as blue (a blue, odd-coloured picture). Tell them apart by which byte never changes.
        bool argb = bpp == 32 && IsArgb(d, offset, stride, width, height);
        // Those Mac frames also store rows top to bottom although the header says bottom to top (a positive height),
        // which turned the preview upside down.
        if (argb) topDown = true;

        var bmp = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        var dest = new byte[width * 4];
        var ptr = bmp.GetPixels();
        int rowBytes = bmp.RowBytes;
        for (int y = 0; y < height; y++)
        {
            int src = offset + (topDown ? y : height - 1 - y) * stride;
            if (argb)
            {
                for (int x = 0, s = src, t = 0; x < width; x++, s += 4, t += 4)
                {
                    dest[t] = d[s + 3]; dest[t + 1] = d[s + 2]; dest[t + 2] = d[s + 1]; dest[t + 3] = 255;
                }
            }
            else if (bpp == 32)
            {
                Buffer.BlockCopy(d, src, dest, 0, width * 4);
                for (int x = 3; x < dest.Length; x += 4) dest[x] = 255;
            }
            else
            {
                for (int x = 0, s = src, t = 0; x < width; x++, s += 3, t += 4)
                {
                    dest[t] = d[s]; dest[t + 1] = d[s + 1]; dest[t + 2] = d[s + 2]; dest[t + 3] = 255;
                }
            }
            Marshal.Copy(dest, 0, ptr + y * rowBytes, dest.Length);
        }
        return bmp;
    }

    /// <summary>True when the first byte of every sampled pixel is 255 (alpha first) while the last byte varies.</summary>
    static bool IsArgb(byte[] d, int offset, int stride, int width, int height)
    {
        bool firstFull = true, lastFull = true;
        for (int i = 0; i < 64; i++)
        {
            int y = (int)((long)height * (i % 8 * 2 + 1) / 16), x = (int)((long)width * (i / 8 * 2 + 1) / 16);
            int p = offset + y * stride + x * 4;
            if (d[p] != 255) firstFull = false;
            if (d[p + 3] != 255) lastFull = false;
        }
        return firstFull && !lastFull;
    }

    /// <summary>A small JPEG of the frame for the preview, cropped to fill <paramref name="w"/>×<paramref name="h"/>.</summary>
    public static byte[] Thumbnail(SKBitmap src, int w, int h)
    {
        using var thumb = new SKBitmap(w, h);
        using (var c = new SKCanvas(thumb))
        {
            float scale = Math.Max((float)w / src.Width, (float)h / src.Height);
            float sw = w / scale, sh = h / scale;
            var srcRect = SKRect.Create((src.Width - sw) / 2, (src.Height - sh) / 2, sw, sh);
            using var paint = new SKPaint { FilterQuality = SKFilterQuality.Medium };
            c.DrawBitmap(src, srcRect, SKRect.Create(0, 0, w, h), paint);
        }
        using var img = SKImage.FromBitmap(thumb);
        using var data = img.Encode(SKEncodedImageFormat.Jpeg, 80);
        return data.ToArray();
    }
}

/// <summary>macOS camera permission state, so the window can say "allow the camera" instead of showing a black picture.</summary>
public static class CameraPermission
{
    public enum State { NotAsked, Restricted, Denied, Allowed, Unknown }

    [DllImport("/usr/lib/libobjc.A.dylib")] static extern IntPtr objc_getClass(string name);
    [DllImport("/usr/lib/libobjc.A.dylib")] static extern IntPtr sel_registerName(string name);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] static extern nint SendInt(IntPtr receiver, IntPtr selector, IntPtr arg);

    public static State Mac()
    {
        if (!OperatingSystem.IsMacOS()) return State.Allowed;
        try
        {
            var av = NativeLibrary.Load("/System/Library/Frameworks/AVFoundation.framework/AVFoundation");
            var video = Marshal.ReadIntPtr(NativeLibrary.GetExport(av, "AVMediaTypeVideo"));
            var status = SendInt(objc_getClass("AVCaptureDevice"), sel_registerName("authorizationStatusForMediaType:"), video);
            return status switch { 0 => State.NotAsked, 1 => State.Restricted, 2 => State.Denied, 3 => State.Allowed, _ => State.Unknown };
        }
        catch (Exception e)
        {
            Log.Warn("Camera permission check: " + e.Message);
            return State.Unknown;
        }
    }

    public static void OpenSettings()
    {
        if (!OperatingSystem.IsMacOS()) return;
        try { System.Diagnostics.Process.Start("open", "x-apple.systempreferences:com.apple.preference.security?Privacy_Camera"); } catch { }
    }
}
