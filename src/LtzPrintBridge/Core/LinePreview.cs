using System.Text;
using System.Text.RegularExpressions;
using SkiaSharp;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;
using ZXing.QrCode.Internal;

namespace LtzPrintBridge.Core;

/// <summary>
/// Previews for the line-based label languages: TSPL (TSC and compatible) and EPL (Zebra EPL2, and the EPL-style BPLE of TVS / SNBC).
/// Same idea as the ZPL preview: drawn on this computer, close to the printer's output but not identical. Printer resolution is assumed to be 203 dpi.
/// </summary>
public static class LinePreview
{
    const int Dpi = 203;
    sealed record Op(int Right, int Bottom, Action<SKCanvas> Draw);

    // ---- detection, splitting, quantity ----

    static readonly Regex TsplRx = new(@"(?im)^\s*(SIZE\s+[\d.]+|CLS\s*$|TEXT\s+\d+\s*,|BARCODE\s+\d+\s*,|QRCODE\s+\d+\s*,|PRINT\s+\d+)", RegexOptions.Compiled);
    static readonly Regex EplRx = new(@"(?m)^\s*N\s*$", RegexOptions.Compiled);
    static readonly Regex EplPrintRx = new(@"(?m)^P\d+", RegexOptions.Compiled);
    static readonly Regex TsplPrintRx = new(@"(?im)^(\s*PRINT\s+)(\d+)(?:\s*,\s*(\d+))?", RegexOptions.Compiled);
    static readonly Regex EplPrintQty = new(@"(?m)^P(\d+)(?:,(\d+))?", RegexOptions.Compiled);

    public static bool LooksLikeTspl(string t) => !t.Contains("^XA", StringComparison.Ordinal) && TsplRx.Matches(t).Count >= 2;
    public static bool LooksLikeEpl(string t) => !t.Contains("^XA", StringComparison.Ordinal) && EplRx.IsMatch(t) && EplPrintRx.IsMatch(t);

    static readonly string[] TsplHeader = { "SIZE", "GAP", "BLINE", "DIRECTION", "REFERENCE", "DENSITY", "SPEED", "OFFSET", "SHIFT", "CODEPAGE", "SET" };

    /// <summary>TSPL: one entry per PRINT. Each entry carries the setup lines (SIZE, GAP...) so it can be drawn on its own.</summary>
    public static List<string> SplitTspl(string text)
    {
        var header = new StringBuilder(); var body = new StringBuilder(); var list = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0) continue;
            var word = line.TrimStart().Split(' ', ',')[0].ToUpperInvariant();
            if (word == "PRINT") { list.Add(header + body.ToString() + line + "\n"); body.Clear(); }
            else if (TsplHeader.Contains(word)) header.Append(line).Append('\n');
            else body.Append(line).Append('\n');
        }
        return list;
    }

    /// <summary>EPL: one entry per P command (N clears the buffer, P prints it).</summary>
    public static List<string> SplitEpl(string text)
    {
        var header = new StringBuilder(); var body = new StringBuilder(); var list = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r'); var t = line.Trim();
            if (t.Length == 0) continue;
            if (Regex.IsMatch(t, @"^P\d+")) { list.Add(header + body.ToString() + line + "\n"); body.Clear(); }
            else if (t == "N") body.Clear();
            else if (t[0] is 'q' or 'Q' or 'R' or 'S' or 'D' or 'Z' or 'O' || t.StartsWith("JF", StringComparison.Ordinal)) header.Append(line).Append('\n');
            else body.Append(line).Append('\n');
        }
        return list;
    }

    public static int TsplQuantity(string label)
    {
        // PRINT m[,n]: m label sets, n copies of each. The total that comes out is m x n.
        var m = TsplPrintRx.Match(label);
        if (!m.Success) return 1;
        int sets = int.TryParse(m.Groups[2].Value, out var a) && a > 0 ? a : 1;
        int each = m.Groups[3].Success && int.TryParse(m.Groups[3].Value, out var n) && n > 0 ? n : 1;
        return sets * each;
    }

    public static string TsplWithQuantity(string text, int q) =>
        TsplPrintRx.Replace(text, m =>
        {
            bool one = !int.TryParse(m.Groups[2].Value, out var sets) || sets <= 1;
            return m.Groups[1].Value + (one ? "1," + q : m.Groups[3].Success ? q + ",1" : q.ToString());
        });

    public static int EplQuantity(string label)
    {
        var m = EplPrintQty.Match(label.Trim());
        if (!m.Success) m = EplPrintQty.Match(label);
        if (!m.Success) return 1;
        // Pp1[,p2]: p1 label sets, p2 copies of each. The total that comes out is p1 x p2.
        int sets = int.TryParse(m.Groups[1].Value, out var a) && a > 0 ? a : 1;
        int each = m.Groups[2].Success && int.TryParse(m.Groups[2].Value, out var n) && n > 0 ? n : 1;
        return sets * each;
    }

    public static string EplWithQuantity(string text, int q) =>
        EplPrintQty.Replace(text, m => !m.Groups[2].Success ? $"P{q}" : m.Groups[1].Value == "1" ? $"P1,{q}" : $"P{q},1");

    // ---- shared helpers ----

    static string[] Args(string s)
    {
        var list = new List<string>(); var sb = new StringBuilder(); bool q = false;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\\' && i + 1 < s.Length && s[i + 1] == '"') { sb.Append('"'); i++; continue; }
            if (c == '"') { q = !q; continue; }
            if (c == ',' && !q) { list.Add(sb.ToString().Trim()); sb.Clear(); continue; }
            sb.Append(c);
        }
        list.Add(q ? sb.ToString() : sb.ToString().Trim());
        return list.ToArray();
    }

    static int I(string[] a, int i, int d = 0) => i < a.Length && int.TryParse(a[i].Trim(), out var v) ? v : d;

    static SKTypeface Mono() => SKTypeface.FromFamilyName("Courier New", SKFontStyle.Bold) ?? SKTypeface.Default;

    /// <summary>Text in a fixed cell grid like the printer's built-in bitmap fonts: each character takes cellW dots, the glyphs are cellH tall.</summary>
    static Op Text(int x, int y, int rot, string text, int cellW, int cellH, bool reverse = false)
    {
        int w = text.Length * cellW;
        return new Op(x + (rot % 2 == 0 ? w : cellH), y + (rot % 2 == 0 ? cellH : w), c =>
        {
            using var p = new SKPaint { Typeface = Mono(), TextSize = cellH * 0.95f, IsAntialias = true, Color = reverse ? SKColors.White : SKColors.Black };
            float adv = p.MeasureText("M"); float sx = adv > 0 ? cellW / adv : 1f;
            c.Save(); c.Translate(x, y); c.RotateDegrees(rot * 90);
            if (reverse) { using var bg = new SKPaint { Color = SKColors.Black }; c.DrawRect(0, 0, w, cellH, bg); }
            c.Scale(sx, 1);
            c.DrawText(text, 0, cellH * 0.82f, p);
            c.Restore();
        });
    }

    static bool[]? Bars(BarcodeFormat f, string data)
    {
        try
        {
            ZXing.Writer w = f switch
            {
                BarcodeFormat.CODE_128 => new ZXing.OneD.Code128Writer(), BarcodeFormat.CODE_39 => new ZXing.OneD.Code39Writer(),
                BarcodeFormat.EAN_13 => new ZXing.OneD.EAN13Writer(), BarcodeFormat.EAN_8 => new ZXing.OneD.EAN8Writer(),
                BarcodeFormat.UPC_A => new ZXing.OneD.UPCAWriter(), BarcodeFormat.ITF => new ZXing.OneD.ITFWriter(),
                BarcodeFormat.CODABAR => new ZXing.OneD.CodaBarWriter(), BarcodeFormat.CODE_93 => new ZXing.OneD.Code93Writer(),
                _ => new ZXing.OneD.Code128Writer()
            };
            var m = w.encode(data, f, 0, 0);
            var all = new bool[m.Width]; for (int i = 0; i < m.Width; i++) all[i] = m[i, 0];
            int first = Array.IndexOf(all, true), last = Array.LastIndexOf(all, true);
            return first < 0 ? all : all[first..(last + 1)];
        }
        catch { return null; }
    }

    static Op Barcode1D(int x, int y, int rot, bool[] bars, int mw, int h, string? human)
    {
        int w = bars.Length * mw; int th = human != null ? 24 : 0;
        return new Op(x + (rot % 2 == 0 ? w : h + th), y + (rot % 2 == 0 ? h + th : w), c =>
        {
            c.Save(); c.Translate(x, y); c.RotateDegrees(rot * 90);
            using var p = new SKPaint { Color = SKColors.Black, IsAntialias = false };
            int bx = 0;
            for (int i = 0; i < bars.Length;)
            {
                if (!bars[i]) { bx += mw; i++; continue; }
                int run = 1; while (i + run < bars.Length && bars[i + run]) run++;
                c.DrawRect(bx, 0, run * mw, h, p); bx += run * mw; i += run;
            }
            if (human != null)
            {
                using var tp = new SKPaint { Typeface = Mono(), TextSize = 22, Color = SKColors.Black, IsAntialias = true };
                c.DrawText(human, Math.Max(0, (w - tp.MeasureText(human)) / 2f), h + 20, tp);
            }
            c.Restore();
        });
    }

    static Op Matrix(int x, int y, int rot, BitMatrix m, int mod)
    {
        int w = m.Width * mod, h = m.Height * mod;
        return new Op(x + w, y + h, c =>
        {
            c.Save(); c.Translate(x, y); c.RotateDegrees(rot * 90);
            using var p = new SKPaint { Color = SKColors.Black, IsAntialias = false };
            for (int r = 0; r < m.Height; r++) for (int col = 0; col < m.Width; col++) if (m[col, r]) c.DrawRect(col * mod, r * mod, mod, mod, p);
            c.Restore();
        });
    }

    static Op Rect(int x, int y, int w, int h, bool white = false) =>
        new(x + w, y + h, c => { using var p = new SKPaint { Color = white ? SKColors.White : SKColors.Black }; c.DrawRect(x, y, w, h, p); });

    static Op Frame(int x1, int y1, int x2, int y2, int t) =>
        new(x2, y2, c =>
        {
            using var p = new SKPaint { Color = SKColors.Black };
            int w = x2 - x1, h = y2 - y1; t = Math.Max(1, t);
            c.DrawRect(x1, y1, w, t, p); c.DrawRect(x1, y2 - t, w, t, p); c.DrawRect(x1, y1, t, h, p); c.DrawRect(x2 - t, y1, t, h, p);
        });

    static PreviewImage Finish(List<Op> ops, int width, int height)
    {
        width = width > 0 ? width : Math.Max(ops.Count == 0 ? 400 : ops.Max(o => o.Right) + 20, 400);
        height = height > 0 ? height : Math.Max(ops.Count == 0 ? 200 : ops.Max(o => o.Bottom) + 20, 100);
        width = Math.Min(width, 2400); height = Math.Min(height, 4000);
        using var bmp = new SKBitmap(width, height); using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.White);
        foreach (var o in ops) { try { o.Draw(canvas); } catch { } }
        using var img = SKImage.FromBitmap(bmp); using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return new PreviewImage(data.ToArray(), width, height);
    }

    static BarcodeFormat? TsplFormat(string t) => t.ToUpperInvariant() switch
    {
        "128" or "128M" or "EAN128" => BarcodeFormat.CODE_128, "39" or "39C" or "39S" => BarcodeFormat.CODE_39,
        "EAN13" or "EAN13+2" or "EAN13+5" => BarcodeFormat.EAN_13, "EAN8" => BarcodeFormat.EAN_8, "UPCA" => BarcodeFormat.UPC_A,
        "25" or "25C" => BarcodeFormat.ITF, "CODA" => BarcodeFormat.CODABAR, "93" => BarcodeFormat.CODE_93, _ => null
    };

    // ---- TSPL ----

    static (int cw, int ch) TsplFont(string f) => f switch
    {
        "1" => (8, 12), "2" => (12, 20), "3" => (16, 24), "4" => (24, 32), "5" => (32, 48), "6" => (14, 19), "7" => (21, 27), "8" => (14, 25),
        _ => (12, 24)
    };

    public static PreviewImage? RenderTspl(string label)
    {
        try
        {
            var ops = new List<Op>(); int width = 0, height = 0, refX = 0, refY = 0;
            foreach (var raw in label.Split('\n'))
            {
                var line = raw.TrimEnd('\r').Trim(); if (line.Length == 0) continue;
                int sp = line.IndexOfAny(new[] { ' ', '\t' }); var cmd = (sp < 0 ? line : line[..sp]).ToUpperInvariant(); var rest = sp < 0 ? "" : line[(sp + 1)..];
                var a = Args(rest);
                try
                {
                    switch (cmd)
                    {
                        case "SIZE":
                        {
                            int One(string s)
                            {
                                s = s.Trim().ToLowerInvariant(); var num = Regex.Match(s, @"[\d.]+").Value;
                                if (!double.TryParse(num, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) return 0;
                                return s.Contains("mm") ? (int)(v * Dpi / 25.4) : s.Contains("dot") ? (int)v : (int)(v * Dpi);
                            }
                            if (a.Length >= 2) { width = One(a[0]); height = One(a[1]); }
                            break;
                        }
                        case "REFERENCE": refX = I(a, 0); refY = I(a, 1); break;
                        case "TEXT":
                        {
                            int x = refX + I(a, 0), y = refY + I(a, 1); var (cw, ch) = TsplFont(a.Length > 2 ? a[2] : "3");
                            int xm = Math.Max(1, I(a, 4, 1)), ym = Math.Max(1, I(a, 5, 1));
                            ops.Add(Text(x, y, (I(a, 3) / 90) % 4, a.Length > 6 ? a[6] : "", cw * xm, ch * ym));
                            break;
                        }
                        case "BLOCK":
                        {
                            int x = refX + I(a, 0), y = refY + I(a, 1), bw = I(a, 2), bh = I(a, 3); var (cw, ch) = TsplFont(a.Length > 4 ? a[4] : "3");
                            int xm = Math.Max(1, I(a, 6, 1)), ym = Math.Max(1, I(a, 7, 1)); cw *= xm; ch *= ym;
                            var txt = a.Length > 8 ? a[8] : ""; int per = Math.Max(1, bw / cw); int row = 0;
                            for (int i = 0; i < txt.Length && (row + 1) * ch <= Math.Max(bh, ch); i += per, row++)
                                ops.Add(Text(x, y + row * ch, 0, txt.Substring(i, Math.Min(per, txt.Length - i)), cw, ch));
                            break;
                        }
                        case "BARCODE":
                        {
                            int x = refX + I(a, 0), y = refY + I(a, 1); var fmt = TsplFormat(a.Length > 2 ? a[2] : "128");
                            int h = Math.Max(10, I(a, 3, 50)); int human = I(a, 4, 1); int rot = (I(a, 5) / 90) % 4; int narrow = Math.Max(1, I(a, 6, 2));
                            var data = a.Length > 8 ? a[8] : ""; if (fmt == null || data.Length == 0) break;
                            var bars = Bars(fmt.Value, data); if (bars == null) break;
                            ops.Add(Barcode1D(x, y, rot, bars, narrow, h, human > 0 ? data : null));
                            break;
                        }
                        case "QRCODE":
                        {
                            int x = refX + I(a, 0), y = refY + I(a, 1); var ecc = a.Length > 2 ? a[2].ToUpperInvariant() : "M"; int cell = Math.Max(1, I(a, 3, 4));
                            int rot = (I(a, 5) / 90) % 4; var data = a.Length > 0 ? a[^1] : ""; if (data.Length == 0) break;
                            var hints = new Dictionary<EncodeHintType, object> { { EncodeHintType.MARGIN, 0 }, { EncodeHintType.CHARACTER_SET, "UTF-8" },
                                { EncodeHintType.ERROR_CORRECTION, ecc.StartsWith("H") ? ErrorCorrectionLevel.H : ecc.StartsWith("Q") ? ErrorCorrectionLevel.Q : ecc.StartsWith("L") ? ErrorCorrectionLevel.L : ErrorCorrectionLevel.M } };
                            ops.Add(Matrix(x, y, rot, new QRCodeWriter().encode(data, BarcodeFormat.QR_CODE, 0, 0, hints), cell));
                            break;
                        }
                        case "DMATRIX":
                        {
                            int x = refX + I(a, 0), y = refY + I(a, 1), w = I(a, 2, 100); var data = a.Length > 0 ? a[^1] : "";
                            var m = new MultiFormatWriter().encode(data, BarcodeFormat.DATA_MATRIX, 0, 0, new Dictionary<EncodeHintType, object> { { EncodeHintType.MARGIN, 0 } });
                            ops.Add(Matrix(x, y, 0, m, Math.Max(1, w / Math.Max(1, m.Width)))); break;
                        }
                        case "BAR": ops.Add(Rect(refX + I(a, 0), refY + I(a, 1), Math.Max(1, I(a, 2)), Math.Max(1, I(a, 3)))); break;
                        case "BOX": ops.Add(Frame(refX + I(a, 0), refY + I(a, 1), refX + I(a, 2), refY + I(a, 3), I(a, 4, 1))); break;
                        case "ERASE": ops.Add(Rect(refX + I(a, 0), refY + I(a, 1), I(a, 2), I(a, 3), true)); break;
                        case "REVERSE":
                        {
                            int x = refX + I(a, 0), y = refY + I(a, 1), w = I(a, 2), h = I(a, 3);
                            ops.Add(new Op(x + w, y + h, c => { using var p = new SKPaint { Color = SKColors.White, BlendMode = SKBlendMode.Difference }; c.DrawRect(x, y, w, h, p); }));
                            break;
                        }
                        case "CIRCLE":
                        {
                            int x = refX + I(a, 0), y = refY + I(a, 1), d = I(a, 2), t = Math.Max(1, I(a, 3, 1));
                            ops.Add(new Op(x + d, y + d, c => { using var p = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Stroke, StrokeWidth = t, IsAntialias = true }; c.DrawCircle(x + d / 2f, y + d / 2f, d / 2f, p); }));
                            break;
                        }
                    }
                }
                catch { }
            }
            return Finish(ops, width, height);
        }
        catch { return null; }
    }

    // ---- EPL ----

    static (int cw, int ch) EplFont(int f) => f switch { 1 => (8, 12), 2 => (10, 16), 3 => (12, 20), 4 => (14, 24), 5 => (32, 48), 6 => (14, 19), 7 => (14, 19), 8 => (14, 19), _ => (12, 20) };

    static BarcodeFormat? EplFormat(string t) => t.ToUpperInvariant() switch
    {
        "0" or "1" or "1E" or "1A" or "1B" or "1C" => BarcodeFormat.CODE_128, "2" or "2C" => BarcodeFormat.ITF, "3" or "3C" => BarcodeFormat.CODE_39,
        "9" => BarcodeFormat.CODE_93, "K" => BarcodeFormat.CODABAR, "E30" or "E32" or "E35" => BarcodeFormat.EAN_13, "E80" or "E82" or "E85" => BarcodeFormat.EAN_8,
        "UA0" or "UA2" or "UA5" => BarcodeFormat.UPC_A, _ => null
    };

    public static PreviewImage? RenderEpl(string label)
    {
        try
        {
            var ops = new List<Op>(); int width = 0, height = 0, refX = 0, refY = 0;
            foreach (var raw in label.Split('\n'))
            {
                var line = raw.TrimEnd('\r'); var t = line.Trim(); if (t.Length == 0) continue;
                try
                {
                    if (t[0] == 'q' && int.TryParse(t[1..], out var qw)) { width = qw; continue; }
                    if (t[0] == 'Q') { var q = Args(t[1..]); height = I(q, 0); continue; }
                    if (t[0] == 'R') { var q = Args(t[1..]); refX = I(q, 0); refY = I(q, 1); continue; }
                    if (t.StartsWith("LO", StringComparison.Ordinal)) { var q = Args(t[2..]); ops.Add(Rect(refX + I(q, 0), refY + I(q, 1), Math.Max(1, I(q, 2)), Math.Max(1, I(q, 3)))); continue; }
                    if (t.StartsWith("LW", StringComparison.Ordinal)) { var q = Args(t[2..]); ops.Add(Rect(refX + I(q, 0), refY + I(q, 1), Math.Max(1, I(q, 2)), Math.Max(1, I(q, 3)))); continue; }
                    if (t[0] == 'X') { var q = Args(t[1..]); ops.Add(Frame(refX + I(q, 0), refY + I(q, 1), refX + I(q, 3), refY + I(q, 4), I(q, 2, 1))); continue; }
                    if (t[0] == 'A' && t.Length > 1 && char.IsDigit(t[1]))
                    {
                        var q = Args(t[1..]); var (cw, ch) = EplFont(I(q, 3, 3)); int hm = Math.Max(1, I(q, 4, 1)), vm = Math.Max(1, I(q, 5, 1));
                        ops.Add(Text(refX + I(q, 0), refY + I(q, 1), I(q, 2) % 4, q.Length > 7 ? q[7] : "", cw * hm, ch * vm, q.Length > 6 && q[6].StartsWith("R", StringComparison.OrdinalIgnoreCase)));
                        continue;
                    }
                    if (t[0] == 'B' && t.Length > 1 && char.IsDigit(t[1]))
                    {
                        var q = Args(t[1..]); var fmt = EplFormat(q.Length > 3 ? q[3] : "1"); var data = q.Length > 8 ? q[8] : "";
                        if (fmt == null || data.Length == 0) continue;
                        var bars = Bars(fmt.Value, data); if (bars == null) continue;
                        bool human = !(q.Length > 7 && q[7].StartsWith("N", StringComparison.OrdinalIgnoreCase));
                        ops.Add(Barcode1D(refX + I(q, 0), refY + I(q, 1), I(q, 2) % 4, bars, Math.Max(1, I(q, 4, 2)), Math.Max(10, I(q, 6, 50)), human ? data : null));
                        continue;
                    }
                    if (t[0] == 'b' && t.Length > 1 && char.IsDigit(t[1]))
                    {
                        // 2D codes: QR is the one in common use. Others are shown as a grey block.
                        var q = Args(t[1..]); int x = refX + I(q, 0), y = refY + I(q, 1);
                        if (q.Length > 2 && q[2].StartsWith("Q", StringComparison.OrdinalIgnoreCase) && q.Length > 0)
                        {
                            var data = q[^1]; int mod = 4; foreach (var p in q.Skip(3)) if (p.StartsWith("s", StringComparison.Ordinal) && int.TryParse(p[1..], out var s)) mod = Math.Max(1, s);
                            var hints = new Dictionary<EncodeHintType, object> { { EncodeHintType.MARGIN, 0 }, { EncodeHintType.CHARACTER_SET, "UTF-8" } };
                            ops.Add(Matrix(x, y, 0, new QRCodeWriter().encode(data, BarcodeFormat.QR_CODE, 0, 0, hints), mod));
                        }
                        else ops.Add(new Op(x + 80, y + 80, c => { using var p = new SKPaint { Color = new SKColor(200, 200, 200) }; c.DrawRect(x, y, 80, 80, p); }));
                    }
                }
                catch { }
            }
            return Finish(ops, width, height);
        }
        catch { return null; }
    }
}
