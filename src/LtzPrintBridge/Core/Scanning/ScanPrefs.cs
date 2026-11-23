using System.Text.Json;

namespace LtzPrintBridge.Core.Scanning;

public enum ScanSuffix { Enter, Tab, None }

/// <summary>What to type for a medicine (GS1) code.</summary>
public enum Gs1Output { Bracketed, Compact, GtinOnly }

/// <summary>Scanner choices, kept in scan.json beside settings.json.</summary>
public sealed class ScanPrefs
{
    public string? Camera { get; set; }
    public ScanSuffix Suffix { get; set; } = ScanSuffix.Enter;
    public Gs1Output Gs1 { get; set; } = Gs1Output.Bracketed;
    public bool Beep { get; set; } = true;
    /// <summary>Where the strip was last left, in screen pixels; null means the bottom-right corner.</summary>
    public int? X { get; set; }
    public int? Y { get; set; }

    static string FilePath => Path.Combine(AppPaths.Dir, "scan.json");

    public static ScanPrefs Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<ScanPrefs>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch { return new(); }
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, JsonSerializer.Serialize(this)); }
        catch (Exception e) { Log.Warn("Could not save scan.json: " + e.Message); }
    }

    /// <summary>The text that goes into the web page's field for this scan.</summary>
    public string TextFor(ScanResult r) => r.Gs1 is { } g
        ? Gs1 switch
        {
            Gs1Output.GtinOnly when g.Gtin != null => g.Gtin,
            // Keyboards cannot type the GS separator, so the compact form leaves it out.
            Gs1Output.Compact => g.Compact().Replace(Scanning.Gs1.GroupSeparator.ToString(), ""),
            _ => g.Bracketed(),
        }
        : r.Text;
}
