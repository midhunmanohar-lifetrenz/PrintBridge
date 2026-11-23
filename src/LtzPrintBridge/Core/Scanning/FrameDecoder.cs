using SkiaSharp;
using ZXingCpp;

namespace LtzPrintBridge.Core.Scanning;

public sealed record ScanResult(string Text, string Format, Gs1Data? Gs1);

/// <summary>Finds a barcode or QR code in one camera frame. With several codes in view, takes the one nearest the centre.</summary>
public sealed class FrameDecoder
{
    static FrameDecoder() => NativeReader.Register();

    readonly BarcodeReader _reader = new()
    {
        Formats = BarcodeFormat.AllReadable,
        TryHarder = true,
        TryRotate = true,
        TryInvert = true,
        TryDownscale = true,
        MaxNumberOfSymbols = 4,
        TextMode = TextMode.HRI,
    };

    /// <summary>Decodes an encoded frame (JPEG, PNG or BMP, as cameras deliver them).</summary>
    public ScanResult? Decode(byte[] encodedImage)
    {
        using var bmp = SKBitmap.Decode(encodedImage);
        return bmp == null ? null : Decode(bmp);
    }

    public ScanResult? Decode(SKBitmap bmp)
    {
        using var gray = ToGray(bmp);
        var view = new ImageView(gray.GetPixels(), gray.Width, gray.Height, ImageFormat.Lum, gray.RowBytes, 1);
        Barcode[] codes;
        lock (_reader) codes = _reader.From(view);
        try
        {
            double cx = gray.Width / 2.0, cy = gray.Height / 2.0;
            var best = codes
                .Where(c => c.IsValid && !string.IsNullOrEmpty(c.Text))
                .OrderBy(c =>
                {
                    var p = c.Position;
                    double x = (p.TopLeft.X + p.BottomRight.X) / 2.0 - cx, y = (p.TopLeft.Y + p.BottomRight.Y) / 2.0 - cy;
                    return x * x + y * y;
                })
                .FirstOrDefault();
            if (best == null) return null;
            var gs1 = best.ContentType == ContentType.GS1 ? Gs1.Parse(best.Text) : null;
            return new ScanResult(best.Text, best.Format.ToString(), gs1);
        }
        finally
        {
            foreach (var c in codes) c.Dispose();
        }
    }

    static SKBitmap ToGray(SKBitmap src)
    {
        if (src.ColorType == SKColorType.Gray8) return src.Copy();
        var gray = new SKBitmap(new SKImageInfo(src.Width, src.Height, SKColorType.Gray8, SKAlphaType.Opaque));
        using var canvas = new SKCanvas(gray);
        canvas.DrawBitmap(src, 0, 0);
        return gray;
    }
}
