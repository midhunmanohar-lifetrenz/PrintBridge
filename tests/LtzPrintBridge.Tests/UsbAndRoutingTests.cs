using System.Net;
using System.Text;
using System.Text.Json;
using LtzPrintBridge.Core;

namespace LtzPrintBridge.Tests;

public class UsbTests
{
    [Theory]
    [InlineData("USB001", "USB")] [InlineData("USB002", "USB")] [InlineData("DOT4_001", "USB")] [InlineData("TSCUSB001", "USB")] [InlineData("usb003", "USB")]
    [InlineData("COM3:", "Serial")] [InlineData("LPT1:", "Parallel")]
    [InlineData("IP_192.168.1.50", "Network")] [InlineData("192.168.1.50", "Network")] [InlineData("WSD-1234-abcd", "Network")] [InlineData("\\\\server\\zebra", "Network")]
    [InlineData("FILE:", "File")] [InlineData("PORTPROMPT:", "File")] [InlineData("nul:", "File")] [InlineData("", "")]
    public void Port_names_are_classified(string port, string expected) => Assert.Equal(expected, WinSpool.ClassifyPort(port));

    [Theory]
    [InlineData("Microsoft Print to PDF", "PORTPROMPT:", true)] [InlineData("Microsoft XPS Document Writer", "XPSPort:", true)]
    [InlineData("OneNote (Desktop)", "nul:", true)] [InlineData("Fax", "SHRFAX:", true)]
    [InlineData("ZDesigner ZD421-203dpi ZPL", "USB001", false)] [InlineData("SNBC TVSE LP46 Dlite BPLE", "USB002", false)] [InlineData("TSC TE244", "192.168.1.5", false)]
    public void Virtual_printers_are_hidden_but_label_printers_are_kept(string name, string port, bool hidden) => Assert.Equal(hidden, WinSpool.IsVirtual(name, port));

    static (JobManager jobs, PrinterInfo p) Make((string, string)? status)
    {
        var store = new SettingsStore(); var jm = new JobManager(new PrinterRegistry(store), store) { StatusProvider = _ => status };
        return (jm, new PrinterInfo { Id = "spool:USB Zebra", Name = "USB Zebra", Transport = TransportKind.Spooler, Address = "USB Zebra", Connection = "USB" });
    }

    [Theory]
    [InlineData("Offline")] [InlineData("Paper out")] [InlineData("Door open")]
    public async Task Usb_printer_that_is_off_or_unplugged_fails_fast_instead_of_queueing_silently(string state)
    {
        var (jm, p) = Make((state, "bad"));
        var r = await jm.PrintAsync(p, Encoding.UTF8.GetBytes("^XA^XZ"), "t");
        Assert.False(r.Ok); Assert.Contains(state.ToLowerInvariant(), r.Message.ToLowerInvariant()); Assert.Contains("Check power", r.Message);
    }

    [Fact] public async Task Printer_that_is_ready_is_not_blocked_by_the_check()
    {
        var (jm, p) = Make(("Ready", "ok"));
        var r = await jm.PrintAsync(p, Encoding.UTF8.GetBytes("^XA^XZ"), "t");
        Assert.DoesNotContain("Check power", r.Message);   // (on a non-Windows machine the spooler itself then reports it is unavailable)
    }

    [Fact] public void Usb_printer_shows_usb_not_a_generic_label()
        => Assert.Equal("USB", new PrinterInfo { Transport = TransportKind.Spooler, Connection = "USB" }.TransportText);

    [SkippableFact] public async Task Windows_local_port_job_reaches_the_port_unchanged()
    {
        Skip.IfNot(OperatingSystem.IsWindows() && new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator), "Windows admin only");
        // A Windows "Local Port" pointing at a file behaves like USB001 for the spooler: the job goes straight to the port monitor.
        var file = Path.Combine(Path.GetTempPath(), "ltz-usb-sim-" + Guid.NewGuid().ToString("N")[..6] + ".bin");
        const string name = "LTZ Test USB Printer";
        void Ps(string c) { var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell.exe", $"-NoProfile -Command \"{c}\"") { RedirectStandardOutput = true, RedirectStandardError = true })!; p.StandardOutput.ReadToEnd(); p.WaitForExit(60000); }
        try
        {
            Ps($"Add-PrinterPort -Name '{file}'; Add-Printer -Name '{name}' -DriverName 'Generic / Text Only' -PortName '{file}'");
            var payload = new byte[] { 0x1B, 0x40, 0x4E, 0x0A, 0xFF, 0x00, 0x50, 0x31, 0x0A };
            WinSpool.WriteRaw(name, payload, "LTZ usb-like test");
            for (int i = 0; i < 100 && !(File.Exists(file) && new FileInfo(file).Length >= payload.Length); i++) await Task.Delay(200);
            Assert.Equal(payload, File.ReadAllBytes(file));
        }
        finally { Ps($"Remove-Printer -Name '{name}' -ErrorAction SilentlyContinue; Remove-PrinterPort -Name '{file}' -ErrorAction SilentlyContinue"); try { File.Delete(file); } catch { } }
    }

    [SkippableFact] public void Windows_reports_a_switched_off_usb_style_printer_as_offline()
    {
        Skip.IfNot(OperatingSystem.IsWindows() && new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator), "Windows admin only");
        const string name = "LTZ Test Offline Printer", port = "LTZOFFLINE_PORT";
        void Ps(string c) { var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell.exe", $"-NoProfile -Command \"{c}\"") { RedirectStandardOutput = true, RedirectStandardError = true })!; p.StandardOutput.ReadToEnd(); p.WaitForExit(60000); }
        try
        {
            Ps($"Add-PrinterPort -Name {port} -PrinterHostAddress 127.0.0.1 -PortNumber 9; Add-Printer -Name '{name}' -DriverName 'Generic / Text Only' -PortName {port}; $p = Get-WmiObject Win32_Printer -Filter \\\"Name='{name}'\\\"; $p.WorkOffline = $true; $p.Put() | Out-Null");
            Assert.Equal("bad", WinSpool.QueueStatus(name)?.level);
        }
        finally { Ps($"Remove-Printer -Name '{name}' -ErrorAction SilentlyContinue; Remove-PrinterPort -Name {port} -ErrorAction SilentlyContinue"); }
    }
}

public class RoutingTests : IClassFixture<BridgeFixture>
{
    readonly BridgeFixture F; const string Good = "https://qa.clinic.example";
    public RoutingTests(BridgeFixture f) { F = f; F.Sink.Received.Clear(); }

    async Task<(HttpStatusCode code, string body)> Send(HttpRequestMessage r) { var res = await F.Http.SendAsync(r); return (res.StatusCode, await res.Content.ReadAsStringAsync()); }

    [Fact] public async Task With_routing_on_any_printer_requested_prints_on_the_default_printer()
    {
        F.Store.Value.RouteAllToDefault = true;
        try
        {
            var body = JsonSerializer.Serialize(new { device = new { uid = "spool:ZDesigner Some Other Printer", name = "Some Other Printer" }, data = "^XA^FDrouted^FS^XZ" });
            var (c, _) = await Send(F.Req(HttpMethod.Post, "/write", Good, body));
            Assert.Equal(HttpStatusCode.OK, c);
            Assert.Contains("routed", Encoding.UTF8.GetString((await F.Sink.WaitFor(1)).Single()));
            var (c2, b2) = await Send(F.Req(HttpMethod.Post, "/api/v1/print", Good, "{\"printerId\":\"does-not-exist\",\"data\":\"X\"}"));
            Assert.Equal(HttpStatusCode.OK, c2); Assert.Contains("\"printer\":\"Fake Zebra\"", b2);
        }
        finally { F.Store.Value.RouteAllToDefault = false; }
    }

    [Fact] public async Task With_routing_on_websites_only_see_the_default_printer()
    {
        using var second = new TcpSink();
        F.Store.Value.ManualPrinters.Add(new ManualPrinter { Name = "Second", Host = "127.0.0.1", Port = second.Port, Language = PrintLanguage.Tspl });
        await F.Registry.RefreshAsync(); F.Store.Value.RouteAllToDefault = true;
        try
        {
            var (_, z) = await Send(F.Req(HttpMethod.Get, "/available", Good));
            using var d = JsonDocument.Parse(z); Assert.Single(d.RootElement.GetProperty("printer").EnumerateArray());
            var (_, a) = await Send(F.Req(HttpMethod.Get, "/api/v1/printers", Good));
            using var ad = JsonDocument.Parse(a); Assert.Single(ad.RootElement.EnumerateArray());
            F.Store.Value.RouteAllToDefault = false;
            var (_, all) = await Send(F.Req(HttpMethod.Get, "/api/v1/printers", Good));
            using var alld = JsonDocument.Parse(all); Assert.Equal(2, alld.RootElement.GetArrayLength());
        }
        finally { F.Store.Value.RouteAllToDefault = false; F.Store.Value.ManualPrinters.RemoveAll(p => p.Name == "Second"); await F.Registry.RefreshAsync(); }
    }

    [Fact] public async Task Changing_the_default_printer_moves_all_requests_to_it()
    {
        using var second = new TcpSink();
        F.Store.Value.ManualPrinters.Add(new ManualPrinter { Name = "Second", Host = "127.0.0.1", Port = second.Port, Language = PrintLanguage.Zpl });
        await F.Registry.RefreshAsync(); F.Store.Value.RouteAllToDefault = true;
        try
        {
            F.Registry.SetDefault($"tcp:127.0.0.1:{second.Port}");
            var (c, _) = await Send(F.Req(HttpMethod.Post, "/write", Good, JsonSerializer.Serialize(new { device = new { uid = F.PrinterId }, data = "^XA^FDmoved^FS^XZ" })));
            Assert.Equal(HttpStatusCode.OK, c);
            Assert.Contains("moved", Encoding.UTF8.GetString((await second.WaitFor(1)).Single()));
            Assert.Empty(await F.Sink.WaitFor(1, 200));          // the printer the site asked for gets nothing
            var (_, def) = await Send(F.Req(HttpMethod.Get, "/default?type=printer", Good)); Assert.Contains("Second", def);
        }
        finally { F.Store.Value.RouteAllToDefault = false; F.Store.Value.DefaultPrinterId = null; F.Store.Value.ManualPrinters.RemoveAll(p => p.Name == "Second"); await F.Registry.RefreshAsync(); }
    }

    [Fact] public async Task With_routing_off_the_requested_printer_is_honoured()
    {
        var (c, _) = await Send(F.Req(HttpMethod.Post, "/api/v1/print", Good, "{\"printerId\":\"does-not-exist\",\"data\":\"X\"}"));
        Assert.Equal(HttpStatusCode.NotFound, c);
    }

    [Fact] public void Routing_is_on_by_default_for_a_fresh_install() => Assert.True(new AppSettings().RouteAllToDefault);
}
