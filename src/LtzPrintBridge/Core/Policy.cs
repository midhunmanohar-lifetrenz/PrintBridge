using System.Text.Json;

namespace LtzPrintBridge.Core;

/// <summary>
/// Optional policy.json that IT places next to the EXE or in %ProgramData%\LtzPrintBridge. It preloads approved
/// websites and the default printer so staff never see a prompt for the clinic's own sites.
/// </summary>
public static class Policy
{
    sealed class File_
    {
        public List<string>? AcceptedHosts { get; set; }
        public List<string>? BlockedHosts { get; set; }
        public string? DefaultPrinter { get; set; }
        public int? HttpPort { get; set; }
        public int? HttpsPort { get; set; }
        public bool? AllowLocalPrograms { get; set; }
        public Dictionary<string, string>? PrinterEncodings { get; set; }
    }

    public static string? DefaultPrinterName { get; private set; }

    public static IEnumerable<string> Locations() => new[]
    {
        Path.Combine(AppContext.BaseDirectory, "policy.json"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LtzPrintBridge", "policy.json")
    };

    public static bool Apply(SettingsStore store, IEnumerable<string>? locations = null)
    {
        bool any = false;
        foreach (var path in locations ?? Locations())
        {
            try
            {
                if (!File.Exists(path)) continue;
                var p = JsonSerializer.Deserialize<File_>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                if (p == null) continue;
                var s = store.Value;
                foreach (var h in p.AcceptedHosts ?? new()) { var x = h.Trim().ToLowerInvariant(); if (x.Length > 0 && !s.AcceptedHosts.Contains(x)) s.AcceptedHosts.Add(x); s.BlockedHosts.Remove(x); }
                foreach (var h in p.BlockedHosts ?? new()) { var x = h.Trim().ToLowerInvariant(); if (x.Length > 0) { s.AcceptedHosts.Remove(x); if (!s.BlockedHosts.Contains(x)) s.BlockedHosts.Add(x); } }
                if (p.DefaultPrinter is { Length: > 0 }) DefaultPrinterName = p.DefaultPrinter;
                if (p.HttpPort is > 0 and < 65536) s.HttpPort = p.HttpPort.Value;
                if (p.HttpsPort is > 0 and < 65536) s.HttpsPort = p.HttpsPort.Value;
                if (p.AllowLocalPrograms is { } lp) s.AllowLocalPrograms = lp;
                foreach (var kv in p.PrinterEncodings ?? new()) if (Languages.FindEncoding(kv.Value) != null) s.PrinterEncodings[kv.Key] = kv.Value;
                any = true; Log.Info("Applied policy from " + path);
            }
            catch (Exception e) { Log.Error("Policy file ignored: " + path, e); }
        }
        if (any) store.Save();
        return any;
    }
}
