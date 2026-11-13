using LtzPrintBridge.Core;

namespace LtzPrintBridge.Server;

/// <summary>
/// Answers in the exact shape a Zebra printer gives, for printers that cannot be asked (USB and driver printers have no
/// return channel, and non-Zebra printers do not know the question). Web pages built on Zebra's BrowserPrint library and
/// its common wrappers read these answers by character position, so the layout below must not change.
/// </summary>
public static class ZebraEmulation
{
    /// <summary>True when the text a page wrote is only a status question, not a label.</summary>
    public static string? StatusQuestion(string data)
    {
        var t = data.Trim().ToUpperInvariant();
        return t is "~HQES" or "~HS" ? t : null;
    }

    public static string Answer(string question, PrinterState s) => question == "~HS" ? HostStatus(s) : HostQueryStatus(s);

    /// <summary>
    /// ~HQES. The error flag is at character 70 and the error bits at 81 to 88 (paused at 84, head faults at 87, media at 88),
    /// which is where zebra-browser-print-wrapper and Zebra's own sample code look.
    /// </summary>
    public static string HostQueryStatus(PrinterState s)
    {
        uint bits = 0;
        foreach (var i in s.Issues)
            bits |= i switch { "paper_out" => 0x1u, "ribbon_out" => 0x2u, "head_open" => 0x4u, "over_temperature" => 0x10u, "paused" => 0x10000u, _ => 0u };
        bool error = !s.Ready || bits != 0;
        return "\u0002\r\n\r\n" + "  PRINTER STATUS".PadRight(44) + "\r\n"
             + "   ERRORS:         " + (error ? "1" : "0") + " 00000000 " + bits.ToString("X8") + "\r\n"
             + "   WARNINGS:       0 00000000 00000000\r\n\u0003\r\n";
    }

    /// <summary>~HS: three strings between STX and ETX, with the paper-out, pause, head-open and ribbon-out flags set from the status.</summary>
    public static string HostStatus(PrinterState s)
    {
        string F(string issue) => s.Issues.Contains(issue) ? "1" : "0";
        return $"\u0002030,{F("paper_out")},{F("paused")},0000,{(s.Busy ? "001" : "000")},0,0,0,000,0,0,{F("over_temperature")}\u0003\r\n"
             + $"\u0002000,0,{F("head_open")},{F("ribbon_out")},0,0,0,0,00000000,1,000\u0003\r\n"
             + "\u00020000,0\u0003\r\n";
    }

    /// <summary>
    /// The text form of GET /default (no type given): a heading and six "key: value" lines separated by newline + tab.
    /// Readers take the text after the first colon, so the uid here is the printer name (ids contain colons).
    /// </summary>
    public static string DefaultDeviceText(PrinterInfo p)
    {
        static string V(string v) => v.Replace(':', ' ').Replace('\n', ' ').Replace('\t', ' ');
        return "Device Info:"
             + "\n\tname: " + V(p.Name) + "\n\tdeviceType: printer"
             + "\n\tconnection: " + (p.Transport == TransportKind.Tcp ? "network" : "usb")
             + "\n\tuid: " + V(p.Name) + "\n\tprovider: com.lifetrenz.printbridge"
             + "\n\tmanufacturer: " + V(string.IsNullOrEmpty(p.Vendor) ? "Unknown" : p.Vendor);
    }
}
