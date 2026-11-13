using LtzPrintBridge.Core;

namespace LtzPrintBridge.Tests;

public class ZplPreviewTests
{
    public const string Sample =
        "^XA^PW609^LL305^CI28\n^FO30,25^A0N,36,36^FDSAMPLE LABEL^FS\n^FO30,70^A0N,26,26^FDID 000123456   Ward 3^FS\n" +
        "^FO30,110^BY2^BCN,70,Y,N,N^FD000123456^FS\n^FO420,25^BQN,2,4^FDQA,000123456^FS\n^FO30,230^GB550,3,3^FS\n" +
        "^FO30,245^FB550,2,0,L^A0N,24,24^FDCollected 22-Oct-2026 08:15 in ward 3, bed 12^FS\n^PQ1^XZ";

    [Fact]
    public void Labels_are_split_and_quantity_is_read()
    {
        var two = Sample + "\n^XA^FO10,10^A0N,30,30^FDTwo^FS^PQ3,0,1,Y^XZ";
        var labels = ZplPreview.SplitLabels(two);
        Assert.Equal(2, labels.Count);
        Assert.Equal(1, ZplPreview.QuantityOf(labels[0]));
        Assert.Equal(3, ZplPreview.QuantityOf(labels[1]));
    }

    [Fact]
    public void Quantity_is_rewritten_or_added_for_every_label()
    {
        var two = "^XA^FO1,1^FDa^FS^XZ^XA^FO1,1^FDb^FS^PQ5,0,1,Y^XZ";
        var r = ZplPreview.WithQuantity(two, 7);
        Assert.Equal(new[] { 7, 7 }, ZplPreview.SplitLabels(r).Select(ZplPreview.QuantityOf));
        Assert.Contains("^PQ7,0,1,Y", r);            // other ^PQ options are kept
        Assert.EndsWith("^XZ", r.TrimEnd());
    }

    [Fact]
    public void Label_renders_to_a_png_of_the_requested_size_with_dark_pixels()
    {
        var img = ZplPreview.Render(ZplPreview.SplitLabels(Sample)[0]);
        Assert.NotNull(img);
        Assert.Equal(609, img!.Width); Assert.Equal(305, img.Height);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, img.Png.Take(4).ToArray());
        using var bmp = SkiaSharp.SKBitmap.Decode(img.Png);
        int dark = 0; for (int y = 0; y < bmp.Height; y += 2) for (int x = 0; x < bmp.Width; x += 2) if (bmp.GetPixel(x, y).Red < 80) dark++;
        Assert.True(dark > 800, $"expected text, bars and QR to leave dark pixels, got {dark}");
        var dir = Environment.GetEnvironmentVariable("LTZ_SHOT_DIR");
        if (!string.IsNullOrEmpty(dir)) File.WriteAllBytes(Path.Combine(dir, "zpl-preview.png"), img.Png);
    }

    [Fact]
    public void Unknown_or_broken_commands_do_not_crash()
    {
        Assert.NotNull(ZplPreview.Render("^XA^FO10,10^BXN,5^FD^FS^GFA,0,0,0,^FO^FD^FS^ZZ^XZ"));
    }

    [Fact]
    public void Hex_escapes_and_graphics_render()
    {
        var gf = "^XA^FO10,10^GFA,8,8,1,FF00FF00FF00FF00^FS^FO40,10^FH^A0N,30,30^FDa_41b^FS^XZ";
        Assert.NotNull(ZplPreview.Render(gf));
    }
}
