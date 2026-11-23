using LtzPrintBridge.Core.Scanning;
using SkiaSharp;
using Xunit;
using ZXingCpp;

namespace LtzPrintBridge.Tests;

public class Gs1Tests
{
    [Fact]
    public void Reads_bracketed_medicine_code()
    {
        var g = Gs1.Parse("(01)08901234567890(17)271231(10)AB12C(21)SER123")!;
        Assert.Equal("08901234567890", g.Gtin);
        Assert.Equal("AB12C", g.Batch);
        Assert.Equal(new DateOnly(2027, 12, 31), g.Expiry);
        Assert.Equal("SER123", g.Serial);
    }

    [Fact]
    public void Reads_raw_code_with_group_separators_and_prefix()
    {
        var g = Gs1.Parse("]d2010890123456789017271231" + "10AB12C\u001d21SER123")!;
        Assert.Equal("08901234567890", g.Gtin);
        Assert.Equal("AB12C", g.Batch);
        Assert.Equal("SER123", g.Serial);
        Assert.Equal(new DateOnly(2027, 12, 31), g.Expiry);
    }

    [Fact]
    public void Day_zero_means_end_of_month()
    {
        var g = Gs1.Parse("(01)08901234567890(17)260200")!;
        Assert.Equal(new DateOnly(2026, 2, 28), g.Expiry);
    }

    [Fact]
    public void Flags_expired_stock()
    {
        var g = Gs1.Parse("(01)08901234567890(17)240131")!;
        Assert.True(g.IsExpired(new DateOnly(2026, 11, 23)));
        Assert.False(g.IsExpired(new DateOnly(2024, 1, 31)));
    }

    [Theory]
    [InlineData("143555")]
    [InlineData("8901234567890")]
    [InlineData("https://example.com/verify?id=123")]
    [InlineData("(ab)xyz")]
    [InlineData("")]
    public void Plain_codes_are_not_gs1(string text) => Assert.Null(Gs1.Parse(text));

    [Fact]
    public void Output_forms_for_the_web_page()
    {
        var r = new ScanResult("(01)08901234567890(17)271231(10)AB12C(21)SER123", "DataMatrix", Gs1.Parse("(01)08901234567890(17)271231(10)AB12C(21)SER123"));
        Assert.Equal("(01)08901234567890(17)271231(10)AB12C(21)SER123", new ScanPrefs { Gs1 = Gs1Output.Bracketed }.TextFor(r));
        Assert.Equal("01089012345678901727123110AB12C21SER123", new ScanPrefs { Gs1 = Gs1Output.Compact }.TextFor(r));
        Assert.Equal("08901234567890", new ScanPrefs { Gs1 = Gs1Output.GtinOnly }.TextFor(r));
        Assert.Equal("143555", new ScanPrefs { Gs1 = Gs1Output.GtinOnly }.TextFor(new ScanResult("143555", "Code128", null)));
    }
}

public class FrameDecoderTests
{
    static FrameDecoderTests() => NativeReader.Register(); // these tests call the reader directly before any PrintBridge code runs

    /// <summary>Draws a code into a camera-sized grey frame, slightly off centre, and encodes it as a camera would (JPEG).</summary>
    static byte[] Frame(string text, BarcodeFormat format, string options = "", int scale = 4, int offsetX = 0)
    {
        using var code = new Barcode(text, new CreatorOptions(format, options));
        using var img = code.ToImage(new WriterOptions { Scale = scale, AddQuietZones = true });
        var px = img.ToArray();
        using var sym = new SKBitmap(new SKImageInfo(img.Width, img.Height, SKColorType.Gray8, SKAlphaType.Opaque));
        System.Runtime.InteropServices.Marshal.Copy(px, 0, sym.GetPixels(), px.Length);
        using var frame = new SKBitmap(1280, 720);
        using (var c = new SKCanvas(frame))
        {
            c.Clear(new SKColor(200, 200, 195));
            c.DrawBitmap(sym, (1280 - img.Width) / 2 + offsetX, (720 - img.Height) / 2);
        }
        using var data = SKImage.FromBitmap(frame).Encode(SKEncodedImageFormat.Jpeg, 85);
        return data.ToArray();
    }

    [Fact]
    public void Reads_medicine_datamatrix_with_batch_and_expiry()
    {
        var r = new FrameDecoder().Decode(Frame("(01)08901234567890(17)271231(10)AB12C(21)SER123", BarcodeFormat.DataMatrix, "gs1"))!;
        Assert.NotNull(r.Gs1);
        Assert.Equal("08901234567890", r.Gs1!.Gtin);
        Assert.Equal("AB12C", r.Gs1.Batch);
        Assert.Equal(new DateOnly(2027, 12, 31), r.Gs1.Expiry);
    }

    [Theory]
    [InlineData("8901234567890", "EAN13")]
    [InlineData("143555", "Code128")]
    [InlineData("LT-SAMPLE-55120", "QRCode")]
    public void Reads_common_codes(string text, string format)
    {
        var r = new FrameDecoder().Decode(Frame(text, BarcodeFormat.Parse(format), scale: 3))!;
        Assert.Equal(text, r.Text);
        Assert.Null(r.Gs1);
    }

    [Fact]
    public void Empty_frame_reads_nothing()
    {
        using var frame = new SKBitmap(640, 480);
        using (var c = new SKCanvas(frame)) c.Clear(SKColors.Gray);
        Assert.Null(new FrameDecoder().Decode(frame));
    }

    [Fact]
    public void Two_codes_in_view_picks_the_one_in_the_centre()
    {
        using var a = new Barcode("CENTRE-111", new CreatorOptions(BarcodeFormat.QRCode));
        using var b = new Barcode("EDGE-222", new CreatorOptions(BarcodeFormat.QRCode));
        using var frame = new SKBitmap(1280, 720);
        using (var c = new SKCanvas(frame))
        {
            c.Clear(SKColors.White);
            Draw(c, a, 560, 280);
            Draw(c, b, 40, 40);
        }
        Assert.Equal("CENTRE-111", new FrameDecoder().Decode(frame)!.Text);

        static void Draw(SKCanvas c, Barcode code, int x, int y)
        {
            using var img = code.ToImage(new WriterOptions { Scale = 4, AddQuietZones = true });
            var px = img.ToArray();
            using var bmp = new SKBitmap(new SKImageInfo(img.Width, img.Height, SKColorType.Gray8, SKAlphaType.Opaque));
            System.Runtime.InteropServices.Marshal.Copy(px, 0, bmp.GetPixels(), px.Length);
            c.DrawBitmap(bmp, x, y);
        }
    }
}

public class ScanWindowTests
{
    /// <summary>Builds the window without showing it, so the camera never starts.</summary>
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void Scan_window_is_small_and_does_not_take_focus()
    {
        var w = new LtzPrintBridge.Views.ScanWindow();
        Assert.False(w.ShowActivated);
        Assert.True(w.Topmost);
        Assert.True(w.Width <= 320 && w.Height <= 50);
        Assert.NotNull(Avalonia.Controls.ControlExtensions.FindControl<Avalonia.Controls.Image>(w, "Preview"));
        Assert.NotNull(Avalonia.Controls.ControlExtensions.FindControl<Avalonia.Controls.TextBlock>(w, "Value"));
    }
}

public class FrameImageTests
{
    /// <summary>FlashCap hands over uncompressed frames as BMP; both row orders and both pixel sizes must read.</summary>
    /// <summary>The Mac camera sends A,R,G,B pixels; read as B,G,R,A they would turn blue.</summary>
    [Fact]
    public void Reads_mac_argb_frames_in_the_right_colours()
    {
        int w = 16, h = 16, stride = w * 4;
        var d = new byte[54 + stride * h];
        d[0] = (byte)'B'; d[1] = (byte)'M';
        BitConverter.GetBytes(54).CopyTo(d, 10);
        BitConverter.GetBytes(40).CopyTo(d, 14);
        BitConverter.GetBytes(w).CopyTo(d, 18);
        BitConverter.GetBytes(h).CopyTo(d, 22);
        BitConverter.GetBytes((short)32).CopyTo(d, 28);
        for (int i = 0; i < w * h; i++)
        {
            int p = 54 + i * 4;
            d[p] = 255;                     // A
            d[p + 1] = 220;                 // R  (a skin-like orange)
            d[p + 2] = (byte)(120 + i % 7); // G
            d[p + 3] = 60;                  // B
        }
        using var bmp = FrameImage.Decode(d)!;
        var c = bmp.GetPixel(5, 5);
        Assert.Equal(220, c.Red);
        Assert.Equal(60, c.Blue);
    }

    [Theory]
    [InlineData(24, false)]
    [InlineData(32, false)]
    [InlineData(32, true)]
    public void Reads_uncompressed_bmp_frames(int bpp, bool topDown)
    {
        int w = 4, h = 2, stride = (w * bpp / 8 + 3) & ~3;
        var d = new byte[54 + stride * h];
        d[0] = (byte)'B'; d[1] = (byte)'M';
        BitConverter.GetBytes(d.Length).CopyTo(d, 2);
        BitConverter.GetBytes(54).CopyTo(d, 10);
        BitConverter.GetBytes(40).CopyTo(d, 14);
        BitConverter.GetBytes(w).CopyTo(d, 18);
        BitConverter.GetBytes(topDown ? -h : h).CopyTo(d, 22);
        BitConverter.GetBytes((short)1).CopyTo(d, 26);
        BitConverter.GetBytes((short)bpp).CopyTo(d, 28);
        // first stored row: blue pixels; second: red
        for (int x = 0; x < w; x++)
        {
            d[54 + x * bpp / 8] = 255;                 // blue in row 0
            d[54 + stride + x * bpp / 8 + 2] = 255;    // red in row 1
        }
        using var bmp = FrameImage.Decode(d)!;
        Assert.Equal(w, bmp.Width);
        Assert.Equal(h, bmp.Height);
        var top = bmp.GetPixel(0, 0);
        // bottom-up BMPs store the last row first, so the first stored row (blue) is the bottom of the picture
        if (topDown) Assert.Equal(255, top.Blue); else Assert.Equal(255, top.Red);
        Assert.NotNull(FrameImage.Thumbnail(bmp, 96, 72));
    }
}

/// <summary>Pictures of the scan strip for review. Runs only when LTZ_SCAN_SHOT points at a folder.</summary>
public class ScanWindowScreenshots
{
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void Capture_scan_strip()
    {
        var dir = Environment.GetEnvironmentVariable("LTZ_SCAN_SHOT");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        LtzPrintBridge.Views.ScanWindow.CameraDisabled = true;
        var shots = new (string file, string status, string value, string? detail)[]
        {
            ("scan-sent.png", "Sent ✓", "8901234567890", null),
            ("scan-medicine.png", "Sent ✓", "(01)08901234567890(17)271231(10)AB12C", "Batch AB12C · Exp Dec 2027"),
            ("scan-scanning.png", "Scanning…", "—", null),
        };
        foreach (var (file, status, value, detail) in shots)
        {
            var w = new LtzPrintBridge.Views.ScanWindow();
            w.Show();
            w.ShowForTest(status, value, detail);
            for (int i = 0; i < 6; i++) { Avalonia.Threading.Dispatcher.UIThread.RunJobs(); Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
            Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(w)!.Save(Path.Combine(dir, file));
            w.Close();
        }
    }
}
