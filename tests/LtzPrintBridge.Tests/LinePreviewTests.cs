using LtzPrintBridge.Core;

namespace LtzPrintBridge.Tests;

public class LinePreviewTests
{
    public const string Tspl =
        "SIZE 50 mm,30 mm\r\nGAP 2 mm,0 mm\r\nDIRECTION 1\r\nCLS\r\n" +
        "TEXT 20,15,\"3\",0,1,1,\"SAMPLE LABEL\"\r\nTEXT 20,50,\"2\",0,1,1,\"ID 000123456  Ward 3\"\r\n" +
        "BARCODE 20,85,\"128\",60,1,0,2,2,\"000123456\"\r\nQRCODE 260,15,M,4,A,0,\"000123456\"\r\nBAR 20,175,360,3\r\n" +
        "BOX 10,5,390,235,2\r\nPRINT 1,1\r\n";

    public const string Epl =
        "\nN\nq400\nQ240,24\nA20,15,0,3,1,1,N,\"SAMPLE LABEL\"\nA20,50,0,2,1,1,N,\"ID 000123456  Ward 3\"\n" +
        "B20,85,0,1,2,5,60,B,\"000123456\"\nLO20,175,360,3\nX10,5,2,390,235\nP1\n";

    [Fact]
    public void Languages_are_recognised_from_the_commands()
    {
        Assert.Equal(PrintLanguage.Zpl, LabelPreview.Detect(ZplPreviewTests.Sample));
        Assert.Equal(PrintLanguage.Tspl, LabelPreview.Detect(Tspl));
        Assert.Equal(PrintLanguage.Epl, LabelPreview.Detect(Epl));
        Assert.Null(LabelPreview.Detect("just some text\r\n"));
    }

    [Fact]
    public void Tspl_is_split_per_print_and_quantity_is_read_and_rewritten()
    {
        var two = Tspl + "CLS\r\nTEXT 10,10,\"2\",0,1,1,\"Two\"\r\nPRINT 2,3\r\n";
        var labels = LabelPreview.Split(PrintLanguage.Tspl, two);
        Assert.Equal(2, labels.Count);
        Assert.All(labels, l => Assert.Contains("SIZE 50 mm", l));         // setup lines travel with each label
        Assert.Equal(new[] { 1, 6 }, labels.Select(l => LabelPreview.Quantity(PrintLanguage.Tspl, l))); // PRINT 2,3 = 2 sets x 3 copies
        var r = LabelPreview.WithQuantity(PrintLanguage.Tspl, two, 5);
        Assert.Contains("PRINT 1,5", r); Assert.Contains("PRINT 5,1", r);
        Assert.All(LabelPreview.Split(PrintLanguage.Tspl, r), l => Assert.Equal(5, LabelPreview.Quantity(PrintLanguage.Tspl, l)));
    }

    [Fact]
    public void Epl_is_split_per_p_and_quantity_is_rewritten()
    {
        var two = Epl + "N\nA10,10,0,2,1,1,N,\"Two\"\nP2,3\n";
        var labels = LabelPreview.Split(PrintLanguage.Epl, two);
        Assert.Equal(2, labels.Count);
        Assert.Equal(new[] { 1, 6 }, labels.Select(l => LabelPreview.Quantity(PrintLanguage.Epl, l))); // P2,3 = 2 sets x 3 copies
        var r = LabelPreview.WithQuantity(PrintLanguage.Epl, two, 7);
        Assert.Contains("P7\n", r); Assert.Contains("P7,1", r);
        Assert.All(LabelPreview.Split(PrintLanguage.Epl, r), l => Assert.Equal(7, LabelPreview.Quantity(PrintLanguage.Epl, l)));
    }

    static int Dark(PreviewImage img)
    {
        using var bmp = SkiaSharp.SKBitmap.Decode(img.Png); int d = 0;
        for (int y = 0; y < bmp.Height; y += 2) for (int x = 0; x < bmp.Width; x += 2) if (bmp.GetPixel(x, y).Red < 80) d++;
        return d;
    }

    [Fact]
    public void Tspl_label_renders_at_its_size()
    {
        var img = LabelPreview.Render(PrintLanguage.Tspl, LabelPreview.Split(PrintLanguage.Tspl, Tspl)[0]);
        Assert.NotNull(img); Assert.InRange(img!.Width, 395, 410); Assert.InRange(img.Height, 235, 250);   // 50 x 30 mm at 203 dpi
        Assert.True(Dark(img) > 800);
        var dir = Environment.GetEnvironmentVariable("LTZ_SHOT_DIR");
        if (!string.IsNullOrEmpty(dir)) File.WriteAllBytes(Path.Combine(dir, "tspl-preview.png"), img.Png);
    }

    [Fact]
    public void Epl_label_renders_at_its_size()
    {
        var img = LabelPreview.Render(PrintLanguage.Epl, LabelPreview.Split(PrintLanguage.Epl, Epl)[0]);
        Assert.NotNull(img); Assert.Equal(400, img!.Width); Assert.Equal(240, img.Height);
        Assert.True(Dark(img) > 600);
        var dir = Environment.GetEnvironmentVariable("LTZ_SHOT_DIR");
        if (!string.IsNullOrEmpty(dir)) File.WriteAllBytes(Path.Combine(dir, "epl-preview.png"), img.Png);
    }

    [Fact]
    public void Broken_lines_do_not_stop_the_picture()
    {
        Assert.NotNull(LabelPreview.Render(PrintLanguage.Tspl, "SIZE x,y\r\nTEXT a,b\r\nBARCODE 1,2,\"999\",0\r\nQRCODE\r\nPRINT 1\r\n"));
        Assert.NotNull(LabelPreview.Render(PrintLanguage.Epl, "N\nA1\nB1,2\nb1,2\nP1\n"));
    }
}
