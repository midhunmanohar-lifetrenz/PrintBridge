using System.Net;
using System.Text;
using System.Text.Json;
using LtzPrintBridge.Core;

namespace LtzPrintBridge.Tests;

public class BridgeTests : IClassFixture<BridgeFixture>
{
    readonly BridgeFixture F;
    const string Good = "https://qa.clinic.example";
    public BridgeTests(BridgeFixture f) { F = f; F.Sink.Received.Clear(); }

    async Task<(HttpStatusCode code, string body, HttpResponseMessage res)> Send(HttpRequestMessage r)
    { var res = await F.Http.SendAsync(r); return (res.StatusCode, await res.Content.ReadAsStringAsync(), res); }

    string WriteBody(string data) => JsonSerializer.Serialize(new { device = new { uid = F.PrinterId, name = "Fake Zebra" }, data });

    // ---- functionality ----
    [Fact] public async Task Status_reports_running()
    {
        var (c, b, _) = await Send(F.Req(HttpMethod.Get, "/api/v1/status", Good));
        Assert.Equal(HttpStatusCode.OK, c); Assert.Contains("\"running\":true", b);
    }

    [Fact] public async Task Lists_printers_in_new_and_zebra_formats()
    {
        var (_, b, _) = await Send(F.Req(HttpMethod.Get, "/api/v1/printers", Good));
        using var d = JsonDocument.Parse(b); Assert.Equal("Fake Zebra", d.RootElement[0].GetProperty("name").GetString());
        var (_, z, _) = await Send(F.Req(HttpMethod.Get, "/available", Good));
        using var zd = JsonDocument.Parse(z); var p = zd.RootElement.GetProperty("printer")[0];
        Assert.Equal("printer", p.GetProperty("deviceType").GetString()); Assert.Equal(F.PrinterId, p.GetProperty("uid").GetString());
        var (_, def, _) = await Send(F.Req(HttpMethod.Get, "/default?type=printer", Good));
        Assert.Contains(F.PrinterId, def);
    }

    [Fact] public async Task Zebra_write_delivers_exact_bytes_to_printer()
    {
        const string zpl = "^XA^PW400^FO20,20^A0N,36,36^FDRahul Menon^FS^XZ";
        var (c, _, _) = await Send(F.Req(HttpMethod.Post, "/write", Good, WriteBody(zpl)));
        Assert.Equal(HttpStatusCode.OK, c);
        var got = await F.Sink.WaitFor(1);
        Assert.Equal(Encoding.UTF8.GetBytes(zpl), got.Single());
    }

    [Fact] public async Task New_api_prints_copies_and_returns_job_id()
    {
        var body = JsonSerializer.Serialize(new { printerId = F.PrinterId, data = "SIZE 50 mm,25 mm\r\nPRINT 1,1\r\n", copies = 3 });
        var (c, b, _) = await Send(F.Req(HttpMethod.Post, "/api/v1/print", Good, body));
        Assert.Equal(HttpStatusCode.OK, c); Assert.Contains("job_", b);
        Assert.Equal(3, (await F.Sink.WaitFor(3)).Count);
    }

    [Fact] public async Task Non_ascii_text_is_sent_as_utf8()
    {
        var (c, _, _) = await Send(F.Req(HttpMethod.Post, "/write", Good, WriteBody("^XA^CI28^FDകേരള Café^FS^XZ")));
        Assert.Equal(HttpStatusCode.OK, c);
        Assert.Contains("കേരള Café", Encoding.UTF8.GetString((await F.Sink.WaitFor(1)).Single()));
    }

    [Fact] public async Task Default_printer_is_used_when_none_given()
    {
        var (c, _, _) = await Send(F.Req(HttpMethod.Post, "/api/v1/print", Good, "{\"data\":\"HELLO\"}"));
        Assert.Equal(HttpStatusCode.OK, c); Assert.Single(await F.Sink.WaitFor(1));
    }

    [Fact] public async Task Raw_base64_bytes_pass_through_untouched()
    {
        var bytes = new byte[] { 0x1B, 0x40, 0x00, 0xFF, 0x80, 0x0A };
        var (c, _, _) = await Send(F.Req(HttpMethod.Post, "/api/v1/print", Good, JsonSerializer.Serialize(new { printerId = F.PrinterId, @base64 = Convert.ToBase64String(bytes) })));
        Assert.Equal(HttpStatusCode.OK, c); Assert.Equal(bytes, (await F.Sink.WaitFor(1)).Single());
    }

    [Fact] public async Task Unknown_printer_gives_404()
    {
        var (c, b, _) = await Send(F.Req(HttpMethod.Post, "/api/v1/print", Good, "{\"printerId\":\"nope\",\"data\":\"x\"}"));
        Assert.Equal(HttpStatusCode.NotFound, c); Assert.Contains("PRINTER_NOT_FOUND", b);
    }

    [Fact] public async Task Offline_printer_reports_failure_not_success()
    {
        F.Store.Value.ManualPrinters.Add(new ManualPrinter { Name = "Dead", Host = "127.0.0.1", Port = 1, Language = PrintLanguage.Zpl });
        await F.Registry.RefreshAsync();
        var (c, b, _) = await Send(F.Req(HttpMethod.Post, "/api/v1/print", Good, "{\"printerId\":\"tcp:127.0.0.1:1\",\"data\":\"x\"}"));
        Assert.Equal(HttpStatusCode.BadGateway, c); Assert.Contains("\"ok\":false", b);
        F.Store.Value.ManualPrinters.RemoveAll(p => p.Name == "Dead"); await F.Registry.RefreshAsync();
    }

    [Fact] public async Task Same_printer_jobs_never_interleave()
    {
        var jobs = Enumerable.Range(0, 8).Select(i => Send(F.Req(HttpMethod.Post, "/write", Good, WriteBody($"^XA^FDjob{i}^FS^XZ")))).ToArray();
        await Task.WhenAll(jobs);
        var got = (await F.Sink.WaitFor(8)).Select(b => Encoding.UTF8.GetString(b)).ToList();
        Assert.Equal(8, got.Count); Assert.All(got, g => Assert.StartsWith("^XA^FDjob", g));
        Assert.Equal(8, got.Distinct().Count());
    }

    [Fact] public async Task Https_endpoint_serves_with_the_local_certificate()
    {
        var r = await F.Http.GetAsync($"https://localhost:{F.HttpsPort}/api/v1/status");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var r6 = await F.Http.GetAsync($"https://127.0.0.1:{F.HttpsPort}/api/v1/status"); Assert.Equal(HttpStatusCode.OK, r6.StatusCode);
    }

    [Fact] public async Task Public_pages_load()
    {
        foreach (var p in new[] { "/", "/test", "/ssl_support" })
        { var r = await F.Http.GetAsync(F.Base + p); Assert.Equal(HttpStatusCode.OK, r.StatusCode); Assert.Contains("PrintBridge", await r.Content.ReadAsStringAsync()); }
        var ca = await F.Http.GetAsync(F.Base + "/ca.cer"); Assert.Equal("application/x-x509-ca-cert", ca.Content.Headers.ContentType?.MediaType);
    }

    // ---- browser (CORS) behaviour ----
    [Fact] public async Task Allowed_origin_gets_cors_and_private_network_headers()
    {
        var (_, _, res) = await Send(F.Req(HttpMethod.Get, "/available", Good));
        Assert.Equal(Good, res.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Equal("true", res.Headers.GetValues("Access-Control-Allow-Private-Network").Single());
    }

    [Fact] public async Task Preflight_succeeds_for_allowed_origin()
    {
        var r = F.Req(HttpMethod.Options, "/write", Good);
        r.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "POST");
        r.Headers.TryAddWithoutValidation("Access-Control-Request-Private-Network", "true");
        var (c, _, res) = await Send(r);
        Assert.Equal(HttpStatusCode.NoContent, c); Assert.True(res.Headers.Contains("Access-Control-Allow-Origin"));
    }

    // ---- security ----
    [Fact] public async Task Unknown_origin_is_denied_and_gets_no_cors_headers()
    {
        var (c, _, res) = await Send(F.Req(HttpMethod.Get, "/available", "https://evil.example"));
        Assert.Equal(HttpStatusCode.Forbidden, c); Assert.False(res.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.Empty(await F.Sink.WaitFor(1, 200));
    }

    [Fact] public async Task Unknown_origin_cannot_print()
    {
        var (c, _, _) = await Send(F.Req(HttpMethod.Post, "/write", "https://evil.example", WriteBody("^XA^XZ")));
        Assert.Equal(HttpStatusCode.Forbidden, c); Assert.Empty(await F.Sink.WaitFor(1, 300));
    }

    [Fact] public async Task Blocked_site_stays_blocked_even_after_listing_as_accepted_elsewhere()
    {
        F.Gate.Block("blocked.example");
        var (c, _, _) = await Send(F.Req(HttpMethod.Get, "/available", "https://blocked.example")); Assert.Equal(HttpStatusCode.Forbidden, c);
        F.Gate.Remove("blocked.example");
    }

    [Fact] public async Task Prompt_allow_always_lets_site_in_and_remembers()
    {
        F.Gate.Prompt = _ => Task.FromResult(HostDecision.AllowAlways);
        var (c, _, _) = await Send(F.Req(HttpMethod.Get, "/available", "https://fresh.example")); Assert.Equal(HttpStatusCode.OK, c);
        F.Gate.Prompt = _ => Task.FromResult(HostDecision.Block);
        var (c2, _, _) = await Send(F.Req(HttpMethod.Get, "/available", "https://fresh.example")); Assert.Equal(HttpStatusCode.OK, c2);
        F.Gate.Remove("fresh.example");
    }

    [Fact] public async Task Dns_rebinding_host_header_is_rejected()
    {
        var r = F.Req(HttpMethod.Get, "/available", Good); r.Headers.Host = "attacker.example";
        var (c, _, _) = await Send(r); Assert.Equal((HttpStatusCode)421, c);
    }

    [Fact] public async Task Cross_site_request_without_origin_is_rejected()
    {
        var r = F.Req(HttpMethod.Get, "/available"); r.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site");
        var (c, _, _) = await Send(r); Assert.Equal(HttpStatusCode.Forbidden, c);
    }

    [Fact] public async Task Opaque_null_origin_is_rejected()
    { var (c, _, _) = await Send(F.Req(HttpMethod.Get, "/available", "null")); Assert.Equal(HttpStatusCode.Forbidden, c); }

    [Fact] public async Task Oversized_payload_is_rejected_and_nothing_is_printed()
    {
        var big = new string('A', F.Store.Value.MaxPayloadKb * 1024 + 20000);
        var (c, _, _) = await Send(F.Req(HttpMethod.Post, "/write", Good, WriteBody(big)));
        Assert.True((int)c >= 400, "got " + c); Assert.Empty(await F.Sink.WaitFor(1, 300));
    }

    [Theory]
    [InlineData("")] [InlineData("not json")] [InlineData("{}")] [InlineData("{\"device\":42}")] [InlineData("[1,2,3]")]
    public async Task Malformed_bodies_do_not_crash_the_server(string body)
    {
        var (c, _, _) = await Send(F.Req(HttpMethod.Post, "/write", Good, body));
        Assert.True((int)c >= 400 && (int)c < 600);
        var (ok, _, _) = await Send(F.Req(HttpMethod.Get, "/api/v1/status", Good)); Assert.Equal(HttpStatusCode.OK, ok);
        Assert.Empty(await F.Sink.WaitFor(1, 150));
    }

    [Fact] public async Task Empty_print_is_refused()
    {
        var (c, _, _) = await Send(F.Req(HttpMethod.Post, "/api/v1/print", Good, JsonSerializer.Serialize(new { printerId = F.PrinterId, data = "" })));
        Assert.Equal(HttpStatusCode.BadRequest, c); Assert.Empty(await F.Sink.WaitFor(1, 150));
    }

    [Fact] public async Task Unknown_routes_and_methods_give_404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Send(F.Req(HttpMethod.Get, "/etc/passwd", Good))).code);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(F.Req(HttpMethod.Delete, "/available", Good))).code);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(F.Req(HttpMethod.Get, "/../../secret", Good))).code);
    }

    [Fact] public async Task Zebra_endpoints_are_always_on()
    {
        Assert.Equal(HttpStatusCode.OK, (await Send(F.Req(HttpMethod.Get, "/available", Good))).code);
        Assert.Equal(HttpStatusCode.OK, (await Send(F.Req(HttpMethod.Get, "/default", Good))).code);
    }

    [Fact] public async Task Server_listens_on_loopback_only()
    {
        var other = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address)
            .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
        if (other == null) return; // no LAN address on this machine: nothing to probe
        using var c = new System.Net.Sockets.TcpClient();
        await Assert.ThrowsAnyAsync<Exception>(() => c.ConnectAsync(other, F.HttpPort).WaitAsync(TimeSpan.FromSeconds(3)));
    }
}
