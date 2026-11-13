using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LtzPrintBridge.Core;
using LtzPrintBridge.Server;

namespace LtzPrintBridge.Tests;

/// <summary>
/// Sends the same requests, and reads the answers the same way, as Zebra's BrowserPrint-3.0.216.min.js and the widely used
/// zebra-browser-print-wrapper (both were read, not run). Bodies are JSON text sent as text/plain, as XMLHttpRequest does.
/// </summary>
public class ZebraLibraryTests : IClassFixture<BridgeFixture>
{
    readonly BridgeFixture F;
    public ZebraLibraryTests(BridgeFixture f) { F = f; }
    const string Good = "https://qa.clinic.example";

    HttpRequestMessage Lib(HttpMethod m, string path, object? body = null)
    {
        var r = new HttpRequestMessage(m, F.Base + path); r.Headers.TryAddWithoutValidation("Origin", Good);
        if (body != null) r.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "text/plain");
        return r;
    }
    async Task<(HttpStatusCode code, string text)> Send(HttpRequestMessage r) { var res = await F.Http.SendAsync(r); return (res.StatusCode, await res.Content.ReadAsStringAsync()); }

    // BrowserPrint.Device as the library rebuilds it before every call
    static object Device(JsonElement d) => new { name = d.GetProperty("name").GetString(), uid = d.GetProperty("uid").GetString(), connection = d.GetProperty("connection").GetString(),
        deviceType = d.GetProperty("deviceType").GetString(), version = 2, provider = d.GetProperty("provider").GetString(), manufacturer = d.GetProperty("manufacturer").GetString() };

    async Task<string> Add(TalkingPrinter p, PrintLanguage lang)
    {
        F.Store.Value.ManualPrinters.Add(new ManualPrinter { Name = "Talking " + p.Port, Host = "127.0.0.1", Port = p.Port, Language = lang, Vendor = "Zebra" });
        await F.Registry.RefreshAsync();
        return "Talking " + p.Port;
    }

    [Fact]
    public async Task Library_flow_get_devices_get_default_send_label()
    {
        // BrowserPrint.getLocalDevices(cb, err, "printer")
        var (c, text) = await Send(Lib(HttpMethod.Get, "/available"));
        Assert.Equal(HttpStatusCode.OK, c);
        var printers = JsonDocument.Parse(text).RootElement.GetProperty("printer"); Assert.True(printers.GetArrayLength() >= 1);
        // BrowserPrint.getDefaultDevice("printer", cb)
        (c, text) = await Send(Lib(HttpMethod.Get, "/default?type=printer"));
        var dev = JsonDocument.Parse(text).RootElement; Assert.Equal("printer", dev.GetProperty("deviceType").GetString());
        // device.send(zpl)
        var before = (await F.Sink.WaitFor(0, 1)).Count;
        (c, text) = await Send(Lib(HttpMethod.Post, "/write", new { device = Device(dev), data = "^XA^FO20,20^FDLibrary^FS^XZ" }));
        Assert.Equal(HttpStatusCode.OK, c);
        Assert.Contains(await F.Sink.WaitFor(before + 1), b => Encoding.UTF8.GetString(b) == "^XA^FO20,20^FDLibrary^FS^XZ");
    }

    [Fact]
    public async Task Application_configuration_has_the_fields_the_library_defines()
    {
        // BrowserPrint.ApplicationConfiguration: { application: { version, build_number, api_level, platform, supportedConversions } }
        var app = JsonDocument.Parse((await Send(Lib(HttpMethod.Get, "/config"))).text).RootElement.GetProperty("application");
        Assert.Equal(JsonValueKind.String, app.GetProperty("version").ValueKind); Assert.Equal(JsonValueKind.Number, app.GetProperty("build_number").ValueKind);
        Assert.Equal(JsonValueKind.Number, app.GetProperty("api_level").ValueKind); Assert.Equal(JsonValueKind.String, app.GetProperty("platform").ValueKind);
        Assert.Equal(JsonValueKind.Object, app.GetProperty("supportedConversions").ValueKind);
    }

    [Fact]
    public async Task Wrapper_reads_the_default_printer_from_the_text_form_and_can_print_with_it()
    {
        // zebra-browser-print-wrapper getDefaultPrinter(): GET /default, text, split on "\n\t", exactly 7 parts, value = text after the first colon
        var (c, data) = await Send(Lib(HttpMethod.Get, "/default"));
        Assert.Equal(HttpStatusCode.OK, c);
        var raw = data.Split("\n\t"); Assert.Equal(7, raw.Length);
        static string Clean(string s) => s.Split(":")[1].Trim();
        var device = new { name = Clean(raw[1]), deviceType = Clean(raw[2]), connection = Clean(raw[3]), uid = Clean(raw[4]), provider = Clean(raw[5]), manufacturer = Clean(raw[6]), version = 0 };
        Assert.Equal("Fake Zebra", device.name); Assert.Equal("printer", device.deviceType); Assert.Equal("network", device.connection); Assert.Equal("Fake Zebra", device.uid);
        var before = (await F.Sink.WaitFor(0, 1)).Count;
        Assert.Equal(HttpStatusCode.OK, (await Send(Lib(HttpMethod.Post, "/write", new { device, data = "^XA^FDWrapper^FS^XZ" }))).code);
        Assert.Contains(await F.Sink.WaitFor(before + 1), b => Encoding.UTF8.GetString(b) == "^XA^FDWrapper^FS^XZ");
    }

    // zebra-browser-print-wrapper checkPrinterStatus(): write "~HQES", read, then look at fixed character positions
    static (bool ready, List<string> errors) WrapperStatus(string result)
    {
        static char At(string s, int i) => i < s.Length ? s[i] : '\0';
        var errors = new List<string>();
        bool ready = At(result, 70) == '0';
        switch (At(result, 88)) { case '1': errors.Add("Paper out"); break; case '2': errors.Add("Ribbon Out"); break; case '4': errors.Add("Media Door Open"); break; case '8': errors.Add("Cutter Fault"); break; }
        switch (At(result, 87)) { case '1': errors.Add("Printhead Overheating"); break; case '2': errors.Add("Motor Overheating"); break; case '4': errors.Add("Printhead Fault"); break; case '8': errors.Add("Incorrect Printhead"); break; }
        if (At(result, 84) == '1') errors.Add("Printer Paused");
        if (!ready && errors.Count == 0) errors.Add("Error: Unknown Error");
        return (ready, errors);
    }

    [Theory]
    [InlineData(0x00, true, "")]
    [InlineData(0x04, false, "Paper out")]
    [InlineData(0x08, false, "Ribbon Out")]
    [InlineData(0x01, false, "Media Door Open")]
    [InlineData(0x10, false, "Printer Paused")]
    public async Task Wrapper_status_check_works_for_a_printer_that_is_not_a_zebra(byte tscStatus, bool ready, string error)
    {
        using var p = new TalkingPrinter(new[] { tscStatus }); var name = await Add(p, PrintLanguage.Tspl);
        var device = new { name, uid = name, connection = "network", deviceType = "printer", version = 0, provider = "com.lifetrenz.printbridge", manufacturer = "TSC" };
        Assert.Equal(HttpStatusCode.OK, (await Send(Lib(HttpMethod.Post, "/write", new { device, data = "~HQES" }))).code);
        var (c, result) = await Send(Lib(HttpMethod.Post, "/read", new { device }));
        Assert.Equal(HttpStatusCode.OK, c);
        var (isReady, errors) = WrapperStatus(result);
        Assert.Equal(ready, isReady); Assert.Equal(error, string.Join(",", errors));
        await Task.Delay(150); Assert.Empty(p.Jobs); // the Zebra question itself must never be sent to a TSC printer
    }

    [Fact]
    public async Task Status_question_to_a_network_zebra_is_answered_by_the_printer_itself_without_a_preview()
    {
        const string real = "\u0002\r\n\r\n  PRINTER STATUS                            \r\n   ERRORS:         1 00000000 00000005\r\n   WARNINGS:       0 00000000 00000000\r\n\u0003\r\n";
        using var p = new TalkingPrinter(Encoding.ASCII.GetBytes(real)); var name = await Add(p, PrintLanguage.Zpl);
        var device = new { name, uid = $"tcp:127.0.0.1:{p.Port}", connection = "network", deviceType = "printer", version = 2, provider = "com.lifetrenz.printbridge", manufacturer = "Zebra" };
        var asked = 0; var old = F.Jobs.Review; F.Store.Value.PreviewMode = PreviewMode.All; F.Jobs.Review = r => { asked++; return Task.FromResult(new JobManager.ReviewDecision(false, 1, false)); };
        try
        {
            Assert.Equal(HttpStatusCode.OK, (await Send(Lib(HttpMethod.Post, "/write", new { device, data = "~HQES" }))).code);
            var (_, result) = await Send(Lib(HttpMethod.Post, "/read", new { device }));
            Assert.Equal(real, result); Assert.Equal(0, asked);
            Assert.False(WrapperStatus(result).ready);
        }
        finally { F.Store.Value.PreviewMode = PreviewMode.Off; F.Jobs.Review = old; }
    }

    [Fact]
    public async Task Send_file_posts_a_form_and_the_file_reaches_the_printer_unchanged()
    {
        // device.sendFile(blob): FormData with "json" = {device} and "blob" = the file
        var dev = JsonDocument.Parse((await Send(Lib(HttpMethod.Get, "/default?type=printer"))).text).RootElement;
        var bytes = new byte[] { 0x5E, 0x58, 0x41, 0x00, 0xFF, 0x80, 0x1B, 0x0A, 0x5E, 0x58, 0x5A };
        var form = new MultipartFormDataContent { { new StringContent(JsonSerializer.Serialize(new { device = Device(dev) })), "json" } };
        var file = new ByteArrayContent(bytes); file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream"); form.Add(file, "blob", "blob");
        var r = new HttpRequestMessage(HttpMethod.Post, F.Base + "/write") { Content = form }; r.Headers.TryAddWithoutValidation("Origin", Good);
        var before = (await F.Sink.WaitFor(0, 1)).Count;
        Assert.Equal(HttpStatusCode.OK, (await Send(r)).code);
        Assert.Contains(await F.Sink.WaitFor(before + 1), b => b.SequenceEqual(bytes));
    }

    [Fact]
    public async Task Send_url_is_refused_because_printbridge_does_not_download_for_websites()
    {
        var dev = JsonDocument.Parse((await Send(Lib(HttpMethod.Get, "/default?type=printer"))).text).RootElement;
        var (c, text) = await Send(Lib(HttpMethod.Post, "/write", new { device = Device(dev), url = "http://192.168.1.1/admin" }));
        Assert.Equal(HttpStatusCode.NotImplemented, c); Assert.Contains("does not download", text);
    }

    [Fact]
    public async Task Read_with_nothing_waiting_is_an_empty_200_as_the_library_expects()
    {
        var dev = JsonDocument.Parse((await Send(Lib(HttpMethod.Get, "/default?type=printer"))).text).RootElement;
        var (c, text) = await Send(Lib(HttpMethod.Post, "/read", new { device = Device(dev) }));
        Assert.Equal(HttpStatusCode.OK, c); Assert.Equal("", text);
    }
}

public class ZebraEmulationTests
{
    static PrinterState With(params string[] issues) => new(issues.Length == 0, issues.Length == 0 ? "ready" : issues[0], "", "system", issues);

    [Fact]
    public void Hqes_answer_keeps_the_positions_zebra_pages_read()
    {
        var ok = ZebraEmulation.HostQueryStatus(With());
        Assert.Equal('0', ok[70]); Assert.Equal("00000000", ok.Substring(81, 8)); Assert.StartsWith("\u0002\r\n\r\n  PRINTER STATUS", ok); Assert.Contains("   ERRORS:         0 00000000 00000000", ok);
        var bad = ZebraEmulation.HostQueryStatus(With("paper_out", "head_open", "paused"));
        Assert.Equal('1', bad[70]); Assert.Equal("00010005", bad.Substring(81, 8)); Assert.Equal('1', bad[84]); Assert.Equal('5', bad[88]);
        Assert.Equal('1', ZebraEmulation.HostQueryStatus(new PrinterState(false, "offline", "", "system", new[] { "offline" }))[70]); // not ready, even with no Zebra bit for it
    }

    [Fact]
    public void Hs_answer_reads_back_as_the_same_status()
    {
        var s = PrinterStatus.ParseZplHostStatus(ZebraEmulation.HostStatus(With("paper_out", "ribbon_out")))!;
        Assert.Equal(new[] { "paper_out", "ribbon_out" }, s.Issues);
        Assert.True(PrinterStatus.ParseZplHostStatus(ZebraEmulation.HostStatus(With()))!.Ready);
    }

    [Theory]
    [InlineData("~HQES", "~HQES")] [InlineData(" ~hs\r\n", "~HS")] [InlineData("^XA^FDx^FS^XZ", null)] [InlineData("~HQES^XA^XZ", null)]
    public void Only_a_bare_status_question_is_answered_on_the_printers_behalf(string data, string? question) => Assert.Equal(question, ZebraEmulation.StatusQuestion(data));

    [Fact]
    public void Default_device_text_survives_colons_in_names()
    {
        var t = ZebraEmulation.DefaultDeviceText(new PrinterInfo { Id = "tcp:10.0.0.5:9100", Name = "Lab: Zebra", Vendor = "Zebra", Transport = TransportKind.Tcp });
        var parts = t.Split("\n\t"); Assert.Equal(7, parts.Length); Assert.All(parts.Skip(1), p => Assert.Equal(2, p.Split(":").Length));
    }
}
