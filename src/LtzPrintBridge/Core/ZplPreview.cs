using System.Text;
using System.Text.RegularExpressions;
using SkiaSharp;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;
using ZXing.QrCode.Internal;

namespace LtzPrintBridge.Core;

/// <summary>One rendered label for the print preview window.</summary>
public sealed record PreviewImage(byte[] Png, int Width, int Height);

/// <summary>
/// Draws a close approximation of a ZPL II label on this computer (nothing is sent anywhere) so people can check it before it prints.
/// Covers the commands real label templates use: text, boxes, barcodes (Code 128, Code 39, EAN, UPC, ITF, QR, Data Matrix, PDF417) and
/// ASCII graphics. Anything else is skipped, so the preview can differ slightly from the printer's own output.
/// </summary>
public static class ZplPreview
{
    static readonly Regex LabelRx = new(@"\^XA.*?\^XZ", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex PqRx = new(@"\^PQ(\d*)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool LooksLikeZpl(string text) => text.Contains("^XA", StringComparison.OrdinalIgnoreCase);

    /// <summary>Each ^XA ... ^XZ block, in order. Text outside blocks (such as ~ commands) is ignored.</summary>
    public static List<string> SplitLabels(string zpl) => LabelRx.Matches(zpl).Select(m => m.Value).Where(Printable).ToList();

    static readonly Regex FieldRx = new(@"\^(FD|GF|XG|XF|GB|GC|GD|GE|GS|B[0-9A-Z]|IM)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    /// <summary>False for blocks that print nothing: stored formats (^DF) and settings-only blocks such as ^XA^JUS^XZ.</summary>
    public static bool Printable(string label) => !label.Contains("^DF", StringComparison.OrdinalIgnoreCase) && FieldRx.IsMatch(label);

    /// <summary>Quantity a label already asks for (^PQ), 1 when none.</summary>
    public static int QuantityOf(string label)
    {
        var all = PqRx.Matches(label); // the printer obeys the last ^PQ in a label
        return all.Count > 0 && int.TryParse(all[^1].Groups[1].Value, out var q) && q > 0 ? q : 1;
    }

    /// <summary>Sets how many copies of every label the printer produces, by writing ^PQ, so one job prints exactly that many.</summary>
    public static string WithQuantity(string zpl, int quantity)
    {
        quantity = Math.Max(1, Math.Min(quantity, LabelPreview.MaxQuantity));
        return LabelRx.Replace(zpl, m =>
        {
            var label = m.Value;
            if (!Printable(label)) return label;
            var all = PqRx.Matches(label);
            if (all.Count > 0) { var last = all[^1]; return label[..last.Index] + "^PQ" + quantity + label[(last.Index + last.Length)..]; }
            var end = label.LastIndexOf("^XZ", StringComparison.OrdinalIgnoreCase);
            return label[..end] + "^PQ" + quantity + label[end..];
        });
    }

    // ---- drawing ----

    sealed class Field
    {
        public int X, Y; public bool Baseline;
        public string FontName = "0"; public int FontH = 30, FontW; public char Orient = 'N';
        public int BlockW, BlockLines = 1, BlockSpacing, BlockIndent; public char BlockJust = 'L';
        public bool Reverse, Hex; public char HexChar = '_';
    }

    sealed class Barcode
    {
        public char Kind; public int Height; public bool Line = true, LineAbove; public int Mag = 2; public string Ec = "Q";
    }

    sealed record Op(int Right, int Bottom, Action<SKCanvas> Draw);

    public static PreviewImage? Render(string label, int dpi = 203)
    {
        try { return RenderCore(label, dpi); } catch { return null; }
    }

    static PreviewImage? RenderCore(string label, int dpi)
    {
        var ops = new List<Op>();
        int pw = 0, ll = 0, lhX = 0, lhY = 0, byW = 2, byH = 10; double byRatio = 3;
        var f = new Field(); var cfName = "0"; int cfH = 30, cfW = 0;
        Barcode? bc = null;

        foreach (var seg in label.Split('^').Skip(1))
        {
            if (seg.Length < 2) continue;
            var cmd = seg[..2].ToUpperInvariant(); var a = seg[2..];
            switch (cmd)
            {
                case "PW": pw = Int(a, 0); break;
                case "LL": ll = Int(a, 0); break;
                case "LH": { var p = Parts(a); lhX = Int(p, 0, 0); lhY = Int(p, 1, 0); break; }
                case "BY": { var p = Parts(a); byW = Math.Max(1, Int(p, 0, 2)); byRatio = p.Length > 1 && double.TryParse(p[1], out var r) ? r : 3; byH = Int(p, 2, 10); break; }
                case "CF": { var p = Parts(a); cfName = p.Length > 0 && p[0].Length > 0 ? p[0][..1] : "0"; cfH = Int(p, 1, cfH); cfW = Int(p, 2, 0); break; }
                case "FO": case "FT": { var p = Parts(a); f = new Field { X = lhX + Int(p, 0, 0), Y = lhY + Int(p, 1, 0), Baseline = cmd == "FT", FontName = cfName, FontH = cfH, FontW = cfW }; bc = null; break; }
                case "A": { if (a.Length == 0) break; var p = Parts(a); f.FontName = p[0].Length > 0 ? p[0][..1] : f.FontName; if (p[0].Length > 1) f.Orient = char.ToUpperInvariant(p[0][1]); f.FontH = Int(p, 1, f.FontH); f.FontW = Int(p, 2, f.FontW); break; }
                case "FB": { var p = Parts(a); f.BlockW = Int(p, 0, 0); f.BlockLines = Math.Max(1, Int(p, 1, 1)); f.BlockSpacing = Int(p, 2, 0); f.BlockJust = p.Length > 3 && p[3].Length > 0 ? char.ToUpperInvariant(p[3][0]) : 'L'; f.BlockIndent = Int(p, 4, 0); break; }
                case "FR": f.Reverse = true; break;
                case "FH": f.Hex = true; if (a.Length > 0) f.HexChar = a[0]; break;
                case "GB": { var p = Parts(a); AddBox(ops, f, Int(p, 0, 1), Int(p, 1, 1), Math.Max(1, Int(p, 2, 1)), p.Length > 3 && p[3].StartsWith("W", StringComparison.OrdinalIgnoreCase)); break; }
                case "GC": { var p = Parts(a); AddCircle(ops, f, Int(p, 0, 3), Math.Max(1, Int(p, 1, 1))); break; }
                case "GF": AddGraphic(ops, f, a); break;
                case "BC": case "B3": case "BE": case "B8": case "BU": case "B2": case "B7":
                { var p = Parts(a); bc = new Barcode { Kind = cmd == "BC" ? 'C' : cmd == "B3" ? '3' : cmd == "BE" ? 'E' : cmd == "B8" ? '8' : cmd == "BU" ? 'U' : cmd == "B2" ? 'I' : 'P' };
                  // Code 39 has a check-digit flag first; the others start with orientation.
                  int hi = cmd == "B3" ? 2 : 1; bc.Height = Int(p, hi, byH);
                  var yn = p.Length > hi + 1 ? p[hi + 1] : "Y"; bc.Line = !yn.StartsWith("N", StringComparison.OrdinalIgnoreCase);
                  var ab = p.Length > hi + 2 ? p[hi + 2] : "N"; bc.LineAbove = ab.StartsWith("Y", StringComparison.OrdinalIgnoreCase); break; }
                case "BQ": { var p = Parts(a); bc = new Barcode { Kind = 'Q', Mag = Math.Max(1, Int(p, 2, 2)), Ec = p.Length > 3 && p[3].Length > 0 ? p[3] : "Q" }; break; }
                case "BX": { var p = Parts(a); bc = new Barcode { Kind = 'D', Mag = Math.Max(1, Int(p, 1, 2)) }; break; }
                case "FD": { var data = a; var fd = f.Hex ? DecodeHex(data, f.HexChar) : data; try { AddField(ops, f, bc, fd, byW, byRatio, cfH); } catch { } break; }
                case "FS": f = new Field { FontName = cfName, FontH = cfH, FontW = cfW }; bc = null; break;
            }
        }

        int width = pw > 0 ? pw : Math.Max(ops.Count == 0 ? 400 : ops.Max(o => o.Right) + 20, 400);
        int height = ll > 0 ? ll : Math.Max(ops.Count == 0 ? 200 : ops.Max(o => o.Bottom) + 20, 100);
        width = Math.Min(width, 2400); height = Math.Min(height, 4000);

        using var bmp = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.White);
        foreach (var o in ops) { try { o.Draw(canvas); } catch { } }
        using var img = SKImage.FromBitmap(bmp);
        using var data2 = img.Encode(SKEncodedImageFormat.Png, 100);
        return new PreviewImage(data2.ToArray(), width, height);
    }

    // ---- parsing helpers ----

    static string[] Parts(string a) => a.Split(',');
    static int Int(string a, int d) => int.TryParse(a.Trim(), out var v) ? v : d;
    static int Int(string[] p, int i, int d) => i < p.Length && int.TryParse(p[i].Trim(), out var v) ? v : d;

    static string DecodeHex(string s, char ind)
    {
        var bytes = new List<byte>(); var sb = new StringBuilder();
        void Flush() { if (bytes.Count > 0) { sb.Append(Encoding.UTF8.GetString(bytes.ToArray())); bytes.Clear(); } }
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == ind && i + 2 < s.Length + 0 && Uri.IsHexDigit(s[i + 1]) && Uri.IsHexDigit(s[i + 2])) { bytes.Add(Convert.ToByte(s.Substring(i + 1, 2), 16)); i += 2; }
            else { Flush(); sb.Append(s[i]); }
        }
        Flush(); return sb.ToString();
    }

    // ---- elements ----

    static SKPaint Black(bool fill = true) => new() { Color = SKColors.Black, IsAntialias = true, Style = fill ? SKPaintStyle.Fill : SKPaintStyle.Stroke };

    static void AddBox(List<Op> ops, Field f, int w, int h, int t, bool white)
    {
        int x = f.X, y = f.Y; w = Math.Max(w, t); h = Math.Max(h, t);
        ops.Add(new Op(x + w, y + h, c =>
        {
            using var p = new SKPaint { Color = white ? SKColors.White : SKColors.Black, IsAntialias = false };
            if (t >= Math.Min(w, h)) c.DrawRect(x, y, w, h, p);
            else { c.DrawRect(x, y, w, t, p); c.DrawRect(x, y + h - t, w, t, p); c.DrawRect(x, y, t, h, p); c.DrawRect(x + w - t, y, t, h, p); }
        }));
    }

    static void AddCircle(List<Op> ops, Field f, int d, int t)
    {
        int x = f.X, y = f.Y;
        ops.Add(new Op(x + d, y + d, c =>
        {
            using var p = new SKPaint { Color = SKColors.Black, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = t };
            c.DrawCircle(x + d / 2f, y + d / 2f, Math.Max(1, d / 2f - t / 2f), p);
        }));
    }

    static void AddGraphic(List<Op> ops, Field f, string a)
    {
        var p = Parts(a); var fmt = p[0].Trim().ToUpperInvariant();
        int bytesPerRow = Int(p, 3, 0); int total = Int(p, 2, 0);
        int x = f.X, y = f.Y;
        if (fmt != "A" || bytesPerRow <= 0 || total <= 0 || p.Length < 5)
        {
            ops.Add(new Op(x + 60, y + 30, c => { using var q = new SKPaint { Color = new SKColor(200, 200, 200) }; c.DrawRect(x, y, 60, 30, q); }));
            return;
        }
        var data = string.Join(",", p.Skip(4));
        var bits = ExpandGraphic(data, bytesPerRow, total);
        int rows = total / bytesPerRow, wpx = bytesPerRow * 8;
        ops.Add(new Op(x + wpx, y + rows, c =>
        {
            using var paint = new SKPaint { Color = SKColors.Black };
            for (int r = 0; r < rows; r++)
                for (int b = 0; b < bytesPerRow; b++)
                {
                    int v = bits[r * bytesPerRow + b]; if (v == 0) continue;
                    for (int k = 0; k < 8; k++) if ((v & (0x80 >> k)) != 0) c.DrawRect(x + b * 8 + k, y + r, 1, 1, paint);
                }
        }));
    }

    /// <summary>Decodes ZPL ASCII graphics, including the run-length shortcuts (G-Y, g-z, comma, exclamation, colon).</summary>
    static byte[] ExpandGraphic(string s, int bytesPerRow, int total)
    {
        var outHex = new StringBuilder(); int rowChars = bytesPerRow * 2; var prev = "";
        string CurRow() { var all = outHex.ToString(); int start = (all.Length / rowChars) * rowChars; return all[start..]; }
        for (int i = 0; i < s.Length && outHex.Length < total * 2; i++)
        {
            char ch = s[i];
            if (ch == ',') { outHex.Append('0', rowChars - CurRow().Length); prev = LastRow(outHex, rowChars); continue; }
            if (ch == '!') { outHex.Append('F', rowChars - CurRow().Length); prev = LastRow(outHex, rowChars); continue; }
            if (ch == ':') { outHex.Append(prev); continue; }
            int count = 0;
            while (i < s.Length && ((s[i] >= 'G' && s[i] <= 'Y') || (s[i] >= 'g' && s[i] <= 'z')))
            { count += s[i] <= 'Y' ? s[i] - 'F' : (s[i] - 'f') * 20; i++; }
            if (i >= s.Length) break;
            char d = s[i];
            if (!Uri.IsHexDigit(d)) continue;
            outHex.Append(d, Math.Max(1, count));
            if (outHex.Length % rowChars == 0) prev = LastRow(outHex, rowChars);
        }
        while (outHex.Length < total * 2) outHex.Append('0');
        var bytes = new byte[total];
        for (int i = 0; i < total; i++) bytes[i] = Convert.ToByte(outHex.ToString(i * 2, 2), 16);
        return bytes;
    }

    static string LastRow(StringBuilder sb, int rowChars) => sb.Length >= rowChars ? sb.ToString(sb.Length - rowChars, rowChars) : sb.ToString();

    static SKTypeface Face(string font)
    {
        // Font 0 is the printer's scalable sans font. Letters A-H are fixed-width bitmap fonts.
        var fam = font == "0" ? "Arial" : "Courier New";
        return SKTypeface.FromFamilyName(fam, font == "0" ? SKFontStyle.Bold : SKFontStyle.Normal) ?? SKTypeface.Default;
    }

    static void AddField(List<Op> ops, Field f, Barcode? bc, string data, int byW, double byRatio, int defH)
    {
        if (bc != null) { AddBarcode(ops, f, bc, data, byW, byRatio); return; }
        int x = f.X, y = f.Y;
        float size = Math.Max(6, f.FontH);
        if (f.FontName != "0") size = Math.Max(8, f.FontH);
        var text = data.Replace("\\&", "\n");
        var face = Face(f.FontName);
        using var font0 = new SKPaint { Typeface = face, TextSize = size * (f.FontName == "0" ? 0.92f : 1f), IsAntialias = true };
        var lines = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            if (f.BlockW > 0) lines.AddRange(Wrap(raw, font0, f.BlockW));
            else lines.Add(raw);
        }
        if (f.BlockW > 0 && lines.Count > f.BlockLines) lines = lines.Take(f.BlockLines).ToList();
        float lineH = size + f.BlockSpacing;
        int right = x + (f.BlockW > 0 ? f.BlockW : (int)Math.Ceiling(lines.Select(l => font0.MeasureText(l)).DefaultIfEmpty(0).Max()));
        int bottom = y + (int)(lines.Count * lineH) + (f.Baseline ? 0 : 0);
        char orient = f.Orient;
        ops.Add(new Op(right, bottom, c =>
        {
            using var paint = new SKPaint { Typeface = face, TextSize = size * (f.FontName == "0" ? 0.92f : 1f), Color = f.Reverse ? SKColors.White : SKColors.Black, IsAntialias = true };
            c.Save();
            if (orient == 'R') { c.Translate(x, y); c.RotateDegrees(90); c.Translate(-x, -y); }
            else if (orient == 'I') { c.Translate(x, y); c.RotateDegrees(180); c.Translate(-x, -y); }
            else if (orient == 'B') { c.Translate(x, y); c.RotateDegrees(270); c.Translate(-x, -y); }
            float baseY = f.Baseline ? y : y + size * 0.82f;
            for (int i = 0; i < lines.Count; i++)
            {
                float lx = x;
                if (f.BlockW > 0)
                {
                    float lw = paint.MeasureText(lines[i]);
                    lx = f.BlockJust == 'C' ? x + (f.BlockW - lw) / 2f : f.BlockJust == 'R' ? x + f.BlockW - lw : x;
                }
                c.DrawText(lines[i], lx, baseY + i * lineH, paint);
            }
            c.Restore();
        }));
    }

    static IEnumerable<string> Wrap(string text, SKPaint font, int width)
    {
        var words = text.Split(' '); var line = "";
        foreach (var w in words)
        {
            var t = line.Length == 0 ? w : line + " " + w;
            if (font.MeasureText(t) > width && line.Length > 0) { yield return line; line = w; } else line = t;
        }
        yield return line;
    }

    static void AddBarcode(List<Op> ops, Field f, Barcode bc, string data, int byW, double byRatio)
    {
        int x = f.X, y = f.Y;
        bool[]? bars1d = null; BitMatrix? m2 = null; string shown = data;
        switch (bc.Kind)
        {
            case 'C': { var clean = Regex.Replace(data, @">[0-9A-Za-z;:<=]", ""); shown = clean; bars1d = new ZXing.OneD.Code128Writer().encode(clean, BarcodeFormat.CODE_128, 0, 0).ToBools(); break; }
            case '3': { shown = data; bars1d = Bars(new ZXing.OneD.Code39Writer(), BarcodeFormat.CODE_39, data); break; }
            case 'E': { shown = data; bars1d = Bars(new ZXing.OneD.EAN13Writer(), BarcodeFormat.EAN_13, data); break; }
            case '8': { shown = data; bars1d = Bars(new ZXing.OneD.EAN8Writer(), BarcodeFormat.EAN_8, data); break; }
            case 'U': { shown = data; bars1d = Bars(new ZXing.OneD.UPCAWriter(), BarcodeFormat.UPC_A, data); break; }
            case 'I': { shown = data; bars1d = Bars(new ZXing.OneD.ITFWriter(), BarcodeFormat.ITF, data); break; }
            case 'Q':
            {
                var d = data; if (d.Length > 3 && d[2] == ',') d = d[3..]; else if (d.Length > 3 && d[3] == ',') d = d[4..];
                var hints = new Dictionary<EncodeHintType, object> { { EncodeHintType.MARGIN, 0 }, { EncodeHintType.ERROR_CORRECTION, bc.Ec.StartsWith("H", StringComparison.OrdinalIgnoreCase) ? ErrorCorrectionLevel.H : bc.Ec.StartsWith("M", StringComparison.OrdinalIgnoreCase) ? ErrorCorrectionLevel.M : bc.Ec.StartsWith("L", StringComparison.OrdinalIgnoreCase) ? ErrorCorrectionLevel.L : ErrorCorrectionLevel.Q }, { EncodeHintType.CHARACTER_SET, "UTF-8" } };
                m2 = new QRCodeWriter().encode(d, BarcodeFormat.QR_CODE, 0, 0, hints); break;
            }
            case 'D': m2 = new MultiFormatWriter().encode(data, BarcodeFormat.DATA_MATRIX, 0, 0, new Dictionary<EncodeHintType, object> { { EncodeHintType.MARGIN, 0 } }); break;
            case 'P': m2 = new MultiFormatWriter().encode(data, BarcodeFormat.PDF_417, 0, 0, new Dictionary<EncodeHintType, object> { { EncodeHintType.MARGIN, 0 } }); break;
        }

        if (bars1d != null)
        {
            int mw = byW, h = Math.Max(10, bc.Height); int w = bars1d.Length * mw;
            int textH = bc.Line ? 24 : 0; int top = f.Baseline ? y - h - (bc.LineAbove ? 0 : textH) : y;
            ops.Add(new Op(x + w, top + h + textH, c =>
            {
                using var p = new SKPaint { Color = SKColors.Black, IsAntialias = false };
                int bx = x;
                for (int i = 0; i < bars1d.Length;)
                {
                    if (!bars1d[i]) { bx += mw; i++; continue; }
                    int run = 1; while (i + run < bars1d.Length && bars1d[i + run]) run++;
                    c.DrawRect(bx, top, run * mw, h, p); bx += run * mw; i += run;
                }
                if (bc.Line)
                {
                    using var tp = new SKPaint { Typeface = Face("A"), TextSize = 22, Color = SKColors.Black, IsAntialias = true };
                    float tw = tp.MeasureText(shown);
                    c.DrawText(shown, x + (w - tw) / 2f, bc.LineAbove ? top - 4 : top + h + 20, tp);
                }
            }));
        }
        else if (m2 != null)
        {
            int mod = bc.Kind == 'P' ? Math.Max(1, byW) : Math.Max(1, bc.Kind == 'D' ? bc.Mag : bc.Mag);
            int mh = bc.Kind == 'P' ? mod * 3 : mod;
            int w = m2.Width * mod, h = m2.Height * mh;
            int top = f.Baseline ? y - h : y;
            ops.Add(new Op(x + w, top + h, c =>
            {
                using var p = new SKPaint { Color = SKColors.Black, IsAntialias = false };
                for (int r = 0; r < m2.Height; r++)
                    for (int col = 0; col < m2.Width; col++)
                        if (m2[col, r]) c.DrawRect(x + col * mod, top + r * mh, mod, mh, p);
            }));
        }
    }

    static bool[] Bars(ZXing.Writer w, BarcodeFormat fmt, string data) => w.encode(data, fmt, 0, 0).ToBools();

    /// <summary>Row 0 of a 1D barcode as bars, without the blank quiet zone ZXing adds on both sides.</summary>
    static bool[] ToBools(this BitMatrix m)
    {
        var all = new bool[m.Width];
        for (int i = 0; i < m.Width; i++) all[i] = m[i, 0];
        int first = Array.IndexOf(all, true), last = Array.LastIndexOf(all, true);
        return first < 0 ? all : all[first..(last + 1)];
    }
}
