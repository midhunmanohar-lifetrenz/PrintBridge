using System.Diagnostics;

namespace LtzPrintBridge.Core;

/// <summary>Printer access for the current operating system: the Windows spooler or macOS CUPS.</summary>
public static class Spool
{
    public sealed record Item(string Name, string Driver, string Port, string Status, string Level, string Connection, bool IsVirtual);

    public static string SystemName => OperatingSystem.IsMacOS() ? "Mac" : "Windows";
    public static string QueueName => OperatingSystem.IsMacOS() ? "CUPS" : "Windows spooler";

    public static List<Item> List() => OperatingSystem.IsMacOS() ? CupsSpool.List() : WinList();

    public static string? DefaultName() => OperatingSystem.IsMacOS() ? CupsSpool.DefaultName() : WinSpool.DefaultName();

    public static (string text, string level)? QueueStatus(string name) =>
        OperatingSystem.IsMacOS() ? CupsSpool.QueueStatus(name) : WinSpool.QueueStatus(name);

    public static void WriteRaw(string queue, byte[] data, string docName)
    {
        if (OperatingSystem.IsMacOS()) CupsSpool.WriteRaw(queue, data, docName);
        else WinSpool.WriteRaw(queue, data, docName);
    }

    static List<Item> WinList() => WinSpool.List().Select(q =>
    {
        var (st, lvl) = WinSpool.Describe(q.Status, q.Attributes);
        return new Item(q.Name, q.Driver, q.Port, st, lvl, WinSpool.ClassifyPort(q.Port), WinSpool.IsVirtual(q.Name, q.Port));
    }).ToList();
}

/// <summary>macOS printing through the CUPS command line tools (lpstat, lp), which ship with macOS.</summary>
public static class CupsSpool
{
    static readonly Dictionary<string, string> Models = new();

    static (int code, string output) Run(string file, IEnumerable<string> args, byte[]? stdin = null)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = stdin != null, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["LC_ALL"] = "C";
        using var p = Process.Start(psi) ?? throw new IOException($"Cannot start {file}.");
        var err = p.StandardError.ReadToEndAsync();
        var outp = p.StandardOutput.ReadToEndAsync();
        if (stdin != null) { p.StandardInput.BaseStream.Write(stdin, 0, stdin.Length); p.StandardInput.Close(); }
        if (!p.WaitForExit(20000)) { try { p.Kill(true); } catch { } throw new IOException($"{Path.GetFileName(file)} did not answer in time."); }
        return (p.ExitCode, outp.Result + err.Result);
    }

    public static string Classify(string uri)
    {
        var u = (uri ?? "").ToLowerInvariant();
        var scheme = u.Split(':')[0];
        return scheme switch
        {
            "usb" => "USB",
            "serial" => "Serial",
            "parallel" => "Parallel",
            "ipp" or "ipps" or "http" or "https" or "socket" or "lpd" or "dnssd" or "smb" or "mdns" or "bonjour" => "Network",
            "file" or "cups-pdf" => "File",
            "" => "",
            _ => "Other"
        };
    }

    public static string DeviceName(string uri)
    {
        // usb://Zebra%20Technologies/ZTC%20GK420d?serial=... -> "Zebra Technologies ZTC GK420d"
        if (!uri.StartsWith("usb://", StringComparison.OrdinalIgnoreCase)) return "";
        var rest = uri["usb://".Length..];
        var q = rest.IndexOf('?'); if (q >= 0) rest = rest[..q];
        return Uri.UnescapeDataString(rest.Replace('/', ' ')).Trim();
    }

    public static List<Spool.Item> List()
    {
        var list = new List<Spool.Item>();
        try
        {
            var (_, dev) = Run("/usr/bin/lpstat", new[] { "-v" });
            var (_, state) = Run("/usr/bin/lpstat", new[] { "-p" });
            var status = ParseStates(state);
            foreach (var line in dev.Split('\n'))
            {
                // device for NAME: URI
                if (!line.StartsWith("device for ")) continue;
                var rest = line["device for ".Length..];
                var i = rest.IndexOf(": ", StringComparison.Ordinal);
                if (i < 0) continue;
                var name = rest[..i].Trim(); var uri = rest[(i + 2)..].Trim();
                var (st, lvl) = status.TryGetValue(name, out var s) ? s : ("Ready", "ok");
                var conn = Classify(uri);
                var model = ModelOf(name);
                if (model.Length == 0) model = DeviceName(uri);
                                var virt = conn == "File" || (name.Contains("PDF", StringComparison.OrdinalIgnoreCase) && model.Length == 0);
                list.Add(new Spool.Item(name, model, uri, st, lvl, conn, virt));
            }
        }
        catch (Exception e) { Log.Error("Could not list CUPS printers", e); }
        return list;
    }

    static Dictionary<string, (string, string)> ParseStates(string text)
    {
        var d = new Dictionary<string, (string, string)>();
        foreach (var line in text.Split('\n'))
        {
            string? rest = null;
            if (line.StartsWith("printer ")) rest = line["printer ".Length..];
            if (rest == null) continue;
            var sp = rest.IndexOf(' ');
            if (sp < 0) continue;
            var name = rest[..sp]; var tail = rest[sp..].ToLowerInvariant();
            d[name] = tail.Contains("disabled") ? ("Paused or offline", "bad")
                    : tail.Contains("now printing") || tail.Contains("processing") ? ("Printing", "ok")
                    : ("Ready", "ok");
        }
        return d;
    }

    static string ModelOf(string name)
    {
        lock (Models) if (Models.TryGetValue(name, out var m)) return m;
        var model = "";
        try
        {
            var (_, o) = Run("/usr/bin/lpoptions", new[] { "-p", name });
            var k = o.IndexOf("printer-make-and-model=", StringComparison.Ordinal);
            if (k >= 0)
            {
                var v = o[(k + "printer-make-and-model=".Length)..];
                if (v.StartsWith("'")) { var e = v.IndexOf('\'', 1); model = e > 0 ? v[1..e] : v.Trim('\''); }
                else model = v.Split(' ')[0];
            }
        }
        catch { }
        lock (Models) Models[name] = model;
        return model;
    }

    public static string? DefaultName()
    {
        try
        {
            var (_, o) = Run("/usr/bin/lpstat", new[] { "-d" });
            var i = o.IndexOf(": ", StringComparison.Ordinal);
            return o.Contains("no system default") || i < 0 ? null : o[(i + 2)..].Trim();
        }
        catch { return null; }
    }

    public static (string text, string level)? QueueStatus(string name)
    {
        try
        {
            var (code, o) = Run("/usr/bin/lpstat", new[] { "-p", name });
            if (code != 0) return ("Not installed", "bad");
            return ParseStates(o).TryGetValue(name, out var s) ? s : ("Ready", "ok");
        }
        catch { return null; }
    }

    public static void WriteRaw(string queue, byte[] data, string docName)
    {
        var (code, o) = Run("/usr/bin/lp", new[] { "-d", queue, "-o", "raw", "-t", docName }, data);
        if (code != 0) throw new IOException($"CUPS refused the job for '{queue}': {o.Trim()}");
    }
}
