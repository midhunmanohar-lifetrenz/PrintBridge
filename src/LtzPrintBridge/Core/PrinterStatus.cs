using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace LtzPrintBridge.Core;

/// <summary>
/// What a printer says about itself right now.
/// State is one of: ready, printing, paused, paper_out, ribbon_out, head_open, paper_jam, offline, error, busy, unknown.
/// Source tells how sure this is: "printer" (the printer answered a status question), "system" (Windows or macOS
/// queue state, as the driver reports it) or "network" (the printer only accepts a connection; it cannot say more).
/// </summary>
public sealed record PrinterState(bool Ready, string State, string Message, string Source, IReadOnlyList<string> Issues, bool Busy = false)
{
    public static PrinterState Ok(string source, string message = "Ready") => new(true, "ready", message, source, Array.Empty<string>());
    public static PrinterState Bad(string state, string message, string source) => new(false, state, message, source, new[] { state });
}

public static class PrinterStatus
{
    static readonly Dictionary<string, string> Text = new()
    {
        ["paper_out"] = "Out of labels", ["ribbon_out"] = "Out of ribbon", ["head_open"] = "Print head is open", ["paused"] = "Paused",
        ["paper_jam"] = "Paper jam", ["over_temperature"] = "Print head too hot", ["under_temperature"] = "Print head too cold",
        ["memory_error"] = "Memory error", ["buffer_full"] = "Receive buffer full", ["error"] = "Printer error"
    };

    static PrinterState From(List<string> issues, bool busy, string source)
    {
        if (issues.Count == 0) return new(true, busy ? "printing" : "ready", busy ? "Printing" : "Ready", source, issues, busy);
        return new(false, issues[0], string.Join(", ", issues.Select(i => Text.TryGetValue(i, out var t) ? t : i)), source, issues, busy);
    }

    static readonly Regex Frame = new(@"\x02([^\x03]*)\x03", RegexOptions.Compiled);

    /// <summary>Reads a Zebra answer to ~HS: three comma-separated strings, each between STX and ETX. Null when the text is not such an answer.</summary>
    public static PrinterState? ParseZplHostStatus(string reply)
    {
        var parts = Frame.Matches(reply).Select(m => m.Groups[1].Value.Split(',')).ToList();
        if (parts.Count < 2 || parts[0].Length < 12 || parts[1].Length < 9) return null;
        string[] a = parts[0], b = parts[1];
        static bool On(string v) => v.Trim() == "1";
        static int Num(string v) => int.TryParse(v.Trim(), out var n) ? n : 0;
        var issues = new List<string>();
        if (On(a[1])) issues.Add("paper_out");
        if (On(b[3])) issues.Add("ribbon_out");
        if (On(b[2])) issues.Add("head_open");
        if (On(a[2])) issues.Add("paused");
        if (On(a[5])) issues.Add("buffer_full");
        if (On(a[9])) issues.Add("memory_error");
        if (On(a[10])) issues.Add("under_temperature");
        if (On(a[11])) issues.Add("over_temperature");
        bool busy = Num(a[4]) > 0 || Num(b[8]) > 0 || On(a[7]); // formats waiting, labels left in the batch, or half a format received
        return From(issues, busy, "printer");
    }

    /// <summary>Reads the one status byte a TSPL printer returns for ESC ! ?.</summary>
    public static PrinterState ParseTsplStatus(byte s)
    {
        var issues = new List<string>();
        if ((s & 0x04) != 0) issues.Add("paper_out");
        if ((s & 0x08) != 0) issues.Add("ribbon_out");
        if ((s & 0x01) != 0) issues.Add("head_open");
        if ((s & 0x02) != 0) issues.Add("paper_jam");
        if ((s & 0x10) != 0) issues.Add("paused");
        if ((s & 0x80) != 0) issues.Add("error");
        return From(issues, (s & 0x20) != 0, "printer");
    }

    /// <summary>Turns the Windows or macOS queue state (as shown on the Printers page) into a status.</summary>
    public static PrinterState FromSystem((string text, string level)? q)
    {
        if (q is not { } s) return new(false, "unknown", "The system did not report a status.", "system", Array.Empty<string>());
        var t = s.text.ToLowerInvariant();
        if (s.level == "ok") return t.Contains("printing") ? new(true, "printing", s.text, "system", Array.Empty<string>(), true) : PrinterState.Ok("system", s.text);
        var state = t.Contains("paper out") ? "paper_out" : t.Contains("jam") ? "paper_jam" : t.Contains("door") ? "head_open" : t.Contains("paused") ? "paused"
                  : t.Contains("offline") || t.Contains("not installed") || t.Contains("not available") ? "offline" : t.Contains("ribbon") || t.Contains("toner") ? "ribbon_low" : "error";
        // A warning (paused queue, ribbon low) still lets jobs through; only "bad" blocks.
        return new(s.level != "bad", state, s.text, "system", new[] { state });
    }

    /// <summary>Sends a short question to a network printer and returns whatever it answers within the time limit (empty when it stays silent).</summary>
    public static async Task<byte[]> AskAsync(string address, byte[] question, int timeoutMs = 1500)
    {
        var i = address.LastIndexOf(':');
        using var c = new TcpClient();
        using var cts = new CancellationTokenSource(timeoutMs);
        await c.ConnectAsync(address[..i], int.Parse(address[(i + 1)..]), cts.Token);
        var ns = c.GetStream();
        await ns.WriteAsync(question, cts.Token);
        using var ms = new MemoryStream(); var buf = new byte[1024];
        try { int n; while ((n = await ns.ReadAsync(buf, cts.Token)) > 0) { ms.Write(buf, 0, n); cts.CancelAfter(200); } } catch (OperationCanceledException) { } catch (IOException) { }
        return ms.ToArray();
    }

    /// <summary>Best available status for a printer: its own answer when it can give one, otherwise what the system or the network shows.</summary>
    public static async Task<PrinterState> ReadAsync(PrinterInfo p, Func<string, (string text, string level)?> queueStatus)
    {
        if (p.Transport == TransportKind.Spooler) return FromSystem(queueStatus(p.Address));
        try
        {
            if (p.Language == PrintLanguage.Zpl && ParseZplHostStatus(Encoding.Latin1.GetString(await AskAsync(p.Address, Encoding.ASCII.GetBytes("~HS")))) is { } z) return z;
            if (p.Language == PrintLanguage.Tspl && await AskAsync(p.Address, new byte[] { 0x1B, 0x21, 0x3F }) is { Length: > 0 } t) return ParseTsplStatus(t[0]);
            if (p.Language is not (PrintLanguage.Zpl or PrintLanguage.Tspl)) await AskAsync(p.Address, Array.Empty<byte>(), 1200); // only checks that it is switched on
            return PrinterState.Ok("network", "Switched on. This printer does not report paper or ribbon state.");
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or IOException)
        {
            return PrinterState.Bad("offline", "The printer does not answer. Check power and network.", "network");
        }
    }
}
