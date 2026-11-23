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
    public void Scan_window_loads_and_does_not_take_focus()
    {
        var w = new LtzPrintBridge.Views.ScanWindow();
        Assert.False(w.ShowActivated);
        Assert.True(w.Topmost);
        Assert.NotNull(Avalonia.Controls.ControlExtensions.FindControl<Avalonia.Controls.Image>(w, "Preview"));
        Assert.Equal(3, Avalonia.Controls.ControlExtensions.FindControl<Avalonia.Controls.ComboBox>(w, "Gs1Box")!.ItemCount);
    }
}
