using System.Text.Json;
using LtzPrintBridge.Core;

namespace LtzPrintBridge.Tests;

/// <summary>
/// Runs the language guesser over about 500 real printer and driver names collected from vendor and driver pages
/// (data/printer_catalog.json). No physical printer is involved: this checks names only.
/// </summary>
public class PrinterCatalogTests
{
    public sealed record Row(string Vendor, string Model, string Queue, string Driver, string Native, string Notes, string Confidence);

    public static List<Row> Load()
    {
        var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "printer_catalog.json")));
        string S(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        return doc.RootElement.EnumerateArray().Select(e => new Row(S(e, "vendor"), S(e, "model"), S(e, "windowsQueueName"), S(e, "driverName"), S(e, "nativeLanguage"),
            S(e, "notes") + " " + (e.TryGetProperty("alsoSupports", out var a) ? string.Join(" ", a.EnumerateArray().Select(x => x.GetString())) : ""), S(e, "confidence"))).ToList();
    }

    /// <summary>The languages PrintBridge may show for a printer whose real language is <paramref name="r"/>.Native.</summary>
    public static PrintLanguage[] Acceptable(Row r) => (r.Queue + " " + r.Driver).Contains("BPL-Z", StringComparison.OrdinalIgnoreCase)
        ? new[] { PrintLanguage.Zpl } // a driver that says it uses the ZPL emulation
        : r.Native switch
    {
        "ZPL" or "PPLZ" or "ZSim" or "ESC/Label" => new[] { PrintLanguage.Zpl },
        "EPL" => new[] { PrintLanguage.Epl, PrintLanguage.Pplb, PrintLanguage.Bple },
        "PPLB" => new[] { PrintLanguage.Pplb, PrintLanguage.Epl },
        "BPLE" => new[] { PrintLanguage.Bple, PrintLanguage.Epl },
        "TSPL" => new[] { PrintLanguage.Tspl },
        "CPCL" => new[] { PrintLanguage.Cpcl },
        "DPL" => new[] { PrintLanguage.Dpl, PrintLanguage.Ppla },
        "PPLA" => new[] { PrintLanguage.Ppla, PrintLanguage.Dpl },
        "ESCPOS" => new[] { PrintLanguage.EscPos },
        "Raster" => new[] { PrintLanguage.Pdf },
        "EZPL" => new[] { PrintLanguage.Ezpl },
        "Fingerprint" => new[] { PrintLanguage.Fingerprint },
        "IPL" => new[] { PrintLanguage.Ipl },
        "SBPL" => new[] { PrintLanguage.Sbpl },
        "Other" when r.Notes.Contains("TPCL", StringComparison.OrdinalIgnoreCase) => new[] { PrintLanguage.Tpcl },
        _ => new[] { PrintLanguage.Auto } // languages PrintBridge has no entry for (SLCS, JScript, PGL, StarPRNT...)
    };

    [Fact]
    public void Catalog_is_present() => Assert.True(Load().Count >= 500);

    [Fact]
    public void Real_printer_names_are_recognised()
    {
        var rows = Load(); var wrong = new List<string>(); int missed = 0;
        foreach (var r in rows)
        {
            var got = Languages.Guess(r.Queue, r.Driver).lang; var ok = Acceptable(r);
            if (ok.Contains(got)) continue;
            if (got == PrintLanguage.Auto) { missed++; continue; }      // "Not set": the person picks it, nothing misleading is shown
            wrong.Add($"{r.Vendor} | {r.Queue} | {r.Driver} | real {r.Native}, guessed {got} ({r.Confidence})");
        }
        if (Environment.GetEnvironmentVariable("LTZ_CATALOG_REPORT") is { Length: > 0 } f) File.WriteAllLines(f, wrong.Prepend($"missed={missed} wrong={wrong.Count} of {rows.Count}"));
        Assert.True(wrong.Count == 0, $"{wrong.Count} names guessed wrongly:\n" + string.Join("\n", wrong.Take(40)));
        Assert.True(missed <= rows.Count * 0.05, $"{missed} of {rows.Count} names not recognised");
    }
}
