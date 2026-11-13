using System.Text.Json.Serialization;

namespace LtzPrintBridge.Core;

public enum PrintLanguage { Auto, Zpl, Tspl, Epl, Bple, Cpcl, Dpl, EscPos, Pdf, Ezpl, Ppla, Pplb, Fingerprint, Ipl, Sbpl, Tpcl }
public enum TransportKind { Spooler, Tcp }

public sealed class PrinterInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Vendor { get; set; } = "Unknown";
    public PrintLanguage Language { get; set; } = PrintLanguage.Auto;
    public TransportKind Transport { get; set; }
    public string Address { get; set; } = "";   // spooler queue name or host:port
    public string Driver { get; set; } = "";
    public string Port { get; set; } = "";
    public string Status { get; set; } = "Ready";
    public string StatusLevel { get; set; } = "ok"; // ok | warn | bad
    public bool IsDefault { get; set; }
    public bool IsManual { get; set; }
    public string Connection { get; set; } = "";

    [JsonIgnore] public string LanguageText => Languages.Display(Language);
    [JsonIgnore] public string TransportText => Transport == TransportKind.Tcp ? "Network" : string.IsNullOrEmpty(Connection) ? "System printer" : Connection;
    [JsonIgnore] public string VendorBadge => string.IsNullOrEmpty(Vendor) ? "?" : Vendor.Length >= 2 ? Vendor[..2].ToUpperInvariant() : Vendor.ToUpperInvariant();
}

public sealed class ManualPrinter
{
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 9100;
    public string Vendor { get; set; } = "Unknown";
    public PrintLanguage Language { get; set; } = PrintLanguage.Zpl;
}

public enum PreviewMode { Off, All, Selected }

public sealed class AppSettings
{
    public List<string> AcceptedHosts { get; set; } = new() { "localhost", "127.0.0.1" };
    public List<string> BlockedHosts { get; set; } = new();
    public List<ManualPrinter> ManualPrinters { get; set; } = new();
    public Dictionary<string, PrintLanguage> LanguageOverrides { get; set; } = new();
    public string? DefaultPrinterId { get; set; }
    public int HttpPort { get; set; } = 9100;
    public int HttpsPort { get; set; } = 9101;
    /// <summary>When on, every print request goes to the default printer whatever printer the website asks for.</summary>
    public bool RouteAllToDefault { get; set; } = true;
    /// <summary>Off, every website, or only the websites listed in PreviewSites: show the label and ask for copies before anything is sent.</summary>
    public PreviewMode PreviewMode { get; set; } = PreviewMode.Off;
    public List<string> PreviewSites { get; set; } = new();
    public bool StartWithWindows { get; set; } = true;
    public int MaxPayloadKb { get; set; } = 2048;
    public bool CertificateTrustAsked { get; set; }
    /// <summary>After a job, ask the printer (network Zebra and TSC) or the system queue whether it was printed. Turn off if a printer reacts badly to status questions.</summary>
    public bool FollowJobs { get; set; } = true;
    /// <summary>Requests that carry no website name come from programs on this computer, not from a browser. Turn off on shared servers (Citrix, Remote Desktop).</summary>
    public bool AllowLocalPrograms { get; set; } = true;
    /// <summary>Text encoding per printer id for label text sent as a string (for example "windows-1252" or "cp850"). UTF-8 when absent.</summary>
    public Dictionary<string, string> PrinterEncodings { get; set; } = new();
}

/// <summary>One request that reached the bridge, kept in memory so it is clear whether a website connected at all.</summary>
public sealed class RequestEntry
{
    public DateTime Time { get; set; } = DateTime.Now;
    public string Site { get; set; } = "";
    public string Method { get; set; } = "";
    public string Path { get; set; } = "";
    public int Status { get; set; }
    public string Result { get; set; } = "";
    [JsonIgnore] public string TimeText => Time.ToString("HH:mm:ss");
    [JsonIgnore] public string What => Method + " " + Path;
    [JsonIgnore] public string Level => Status is >= 200 and < 300 ? "ok" : Status == 403 ? "warn" : "bad";
}

public sealed class JobRecord
{
    public string Id { get; set; } = "";
    public DateTime Time { get; set; } = DateTime.Now;
    public string Site { get; set; } = "";
    public string Printer { get; set; } = "";
    public string Language { get; set; } = "";
    public int Bytes { get; set; }
    public string Result { get; set; } = "";
    public bool Ok { get; set; }
    [JsonIgnore] public string TimeText => Time.ToString("HH:mm:ss");
    [JsonIgnore] public string SizeText => Bytes < 1024 ? $"{Bytes} B" : $"{Bytes / 1024.0:0.#} KB";
}
