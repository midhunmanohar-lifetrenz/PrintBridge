using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LtzPrintBridge.Core;
using LtzPrintBridge.Server;
using LtzPrintBridge.ViewModels;

namespace LtzPrintBridge.Tests;

/// <summary>One test (or more) for every defect in docs/Review_and_Test_Report.md.</summary>
public class ReviewFixTests
{
    static string TempDir() { var d = Path.Combine(Path.GetTempPath(), "ltz-fix-" + Guid.NewGuid().ToString("N")[..8]); Directory.CreateDirectory(d); return d; }

    static (JobManager jobs, SettingsStore store, PrinterInfo printer, TcpSink sink, List<JobManager.PrintReview> asked) Rig(Func<JobManager.PrintReview, JobManager.ReviewDecision> answer)
    {
        var sink = new TcpSink();
        var store = new SettingsStore(Path.Combine(TempDir(), "settings.json")); store.Value.PreviewMode = PreviewMode.All;
        var jobs = new JobManager(new PrinterRegistry(store), store);
        var asked = new List<JobManager.PrintReview>();
        jobs.Review = r => { asked.Add(r); return Task.FromResult(answer(r)); };
        var p = new PrinterInfo { Id = $"tcp:127.0.0.1:{sink.Port}", Name = "Fake", Transport = TransportKind.Tcp, Address = $"127.0.0.1:{sink.Port}", Language = PrintLanguage.Zpl };
        return (jobs, store, p, sink, asked);
    }

    // ---- H2: quantities above 100 ----

    [Fact]
    public async Task Preview_left_unchanged_sends_the_job_exactly_as_received_even_for_500_labels()
    {
        var (jobs, _, p, sink, asked) = Rig(r => new(true, r.Copies, false)); using var _s = sink;
        var data = Encoding.UTF8.GetBytes("^XA^FO10,10^FDA^FS^PQ500^XZ");
        var r = await jobs.PrintAsync(p, data, "site.example.com");
        Assert.True(r.Ok); Assert.Equal(500, asked[0].Copies);
        Assert.Equal(data, (await sink.WaitFor(1))[0]);
    }

    [Fact]
    public async Task Preview_can_set_a_quantity_above_100()
    {
        var (jobs, _, p, sink, _) = Rig(_ => new(true, 250, false)); using var _s = sink;
        await jobs.PrintAsync(p, Encoding.UTF8.GetBytes("^XA^FO10,10^FDA^FS^PQ500^XZ"), "site.example.com");
        Assert.Equal("^XA^FO10,10^FDA^FS^PQ250^XZ", Encoding.UTF8.GetString((await sink.WaitFor(1))[0]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task Copies_outside_1_to_100_are_refused_not_silently_changed(int copies)
    {
        var (jobs, _, p, sink, _) = Rig(r => new(true, r.Copies, false)); using var _s = sink;
        var r = await jobs.PrintAsync(p, Encoding.UTF8.GetBytes("hello"), "site.example.com", copies);
        Assert.False(r.Ok); Assert.Equal("rejected", r.State); Assert.Empty(await sink.WaitFor(1, 150));
    }

    // ---- H4, H5, M2: quantity read and written correctly ----

    [Theory]
    [InlineData("SIZE 50 mm,25 mm\r\nCLS\r\nTEXT 10,10,\"3\",0,1,1,\"A\"\r\nPRINT 4\r\n", 4, "PRINT 3\r\n")]
    [InlineData("SIZE 50 mm,25 mm\r\nCLS\r\nTEXT 10,10,\"3\",0,1,1,\"A\"\r\nPRINT 1,2\r\n", 2, "PRINT 1,3\r\n")]
    [InlineData("SIZE 50 mm,25 mm\r\nCLS\r\nTEXT 10,10,\"3\",0,1,1,\"A\"\r\nPRINT 2,3\r\n", 6, "PRINT 3,1\r\n")]
    public void Tspl_quantity_is_sets_times_copies(string job, int total, string after)
    {
        var label = LabelPreview.Split(PrintLanguage.Tspl, job).Single();
        Assert.Equal(total, LabelPreview.Quantity(PrintLanguage.Tspl, label));
        var r = LabelPreview.WithQuantity(PrintLanguage.Tspl, job, 3);
        Assert.EndsWith(after, r); Assert.Equal(3, LabelPreview.Quantity(PrintLanguage.Tspl, LabelPreview.Split(PrintLanguage.Tspl, r).Single()));
    }

    [Theory]
    [InlineData("\nN\nA20,20,0,3,1,1,N,\"A\"\nP1\n", 1, "P3\n")]
    [InlineData("\nN\nA20,20,0,3,1,1,N,\"A\"\nP2,3\n", 6, "P3,1\n")]
    [InlineData("\nN\nA20,20,0,3,1,1,N,\"A\"\nP1,4\n", 4, "P1,3\n")]
    public void Epl_quantity_is_sets_times_copies(string job, int total, string after)
    {
        Assert.Equal(total, LabelPreview.Quantity(PrintLanguage.Epl, LabelPreview.Split(PrintLanguage.Epl, job).Single()));
        var r = LabelPreview.WithQuantity(PrintLanguage.Epl, job, 3);
        Assert.EndsWith(after, r); Assert.Equal(3, LabelPreview.Quantity(PrintLanguage.Epl, LabelPreview.Split(PrintLanguage.Epl, r).Single()));
    }

    [Fact]
    public void Zpl_uses_the_last_pq_like_the_printer_does()
    {
        const string job = "^XA^FX remember ^PQ9 later^FS^FO10,10^FDA^FS^PQ2^XZ";
        Assert.Equal(2, ZplPreview.QuantityOf(job));
        Assert.Equal("^XA^FX remember ^PQ9 later^FS^FO10,10^FDA^FS^PQ3^XZ", ZplPreview.WithQuantity(job, 3));
    }

    [Fact]
    public void Zpl_blocks_that_print_nothing_are_left_alone()
    {
        const string job = "^XA^DFR:F.ZPL^FS^FO10,10^FN1^FS^XZ\n^XA^JUS^XZ\n^XA^XFR:F.ZPL^FS^FN1^FDhello^FS^XZ";
        Assert.Single(ZplPreview.SplitLabels(job));
        Assert.Equal("^XA^DFR:F.ZPL^FS^FO10,10^FN1^FS^XZ\n^XA^JUS^XZ\n^XA^XFR:F.ZPL^FS^FN1^FDhello^FS^PQ3^XZ", ZplPreview.WithQuantity(job, 3));
    }

    // ---- H3: the chosen default printer is never silently replaced ----

    [Fact]
    public async Task Missing_default_printer_refuses_jobs_instead_of_using_another_printer()
    {
        using var sink = new TcpSink();
        var store = new SettingsStore(Path.Combine(TempDir(), "settings.json"));
        store.Value.ManualPrinters.Add(new ManualPrinter { Name = "Other", Host = "127.0.0.1", Port = sink.Port });
        store.Value.DefaultPrinterId = "spool:Pharmacy Label Printer (unplugged)";
        var reg = new PrinterRegistry(store); await reg.RefreshAsync();
        Assert.True(reg.DefaultMissing); Assert.Null(reg.DefaultPrinter()); Assert.Null(reg.Resolve("Other")); Assert.Empty(reg.Visible());
        reg.SetDefault($"tcp:127.0.0.1:{sink.Port}");
        Assert.False(reg.DefaultMissing); Assert.Equal("Other", reg.Resolve(null)!.Name);
    }

    // ---- M1: Windows printer status flags ----

    [Theory]
    [InlineData(0x8u, "Paper jam", "bad")]
    [InlineData(0x10u, "Paper out", "bad")]
    [InlineData(0x80u, "Offline", "bad")]
    [InlineData(0x1000u, "Not available", "bad")]
    [InlineData(0x400000u, "Door open", "bad")]
    [InlineData(0x20000u, "Ribbon or toner low", "warn")]
    [InlineData(0x400u, "Printing", "ok")]
    [InlineData(0u, "Ready", "ok")]
    public void Windows_status_flags_are_read_correctly(uint status, string text, string level) => Assert.Equal((text, level), WinSpool.Describe(status, 0));

    // ---- H1: certificate renewal ----

    [Fact]
    public void A_new_or_unreadable_certificate_is_created_and_flagged_for_trust()
    {
        var dir = TempDir();
        var first = new CertManager(dir); first.Ensure(); Assert.True(first.Regenerated); Assert.NotNull(first.ServerCert);
        var again = new CertManager(dir); again.Ensure(); Assert.False(again.Regenerated);
        File.WriteAllText(Path.Combine(dir, "server.pfx"), "not a certificate");
        var repaired = new CertManager(dir); repaired.Ensure(); Assert.True(repaired.Regenerated); Assert.NotNull(repaired.ServerCert); Assert.Null(repaired.Error);
    }

    // ---- M5: permission windows ----

    [Fact]
    public async Task Closing_the_prompt_does_not_block_the_website_for_good()
    {
        var store = new SettingsStore(Path.Combine(TempDir(), "settings.json")); var gate = new HostGate(store);
        gate.Prompt = _ => Task.FromResult(HostDecision.NotNow);
        Assert.False(await gate.IsAllowedAsync("new.example")); Assert.DoesNotContain("new.example", store.Value.BlockedHosts);
        gate.Prompt = _ => Task.FromResult(HostDecision.AllowAlways);
        Assert.True(await gate.IsAllowedAsync("new.example"));
    }

    [Fact]
    public async Task No_more_than_three_permission_windows_at_once()
    {
        var store = new SettingsStore(Path.Combine(TempDir(), "settings.json")); var gate = new HostGate(store);
        var open = new List<string>(); var never = new TaskCompletionSource<HostDecision>();
        gate.Prompt = h => { lock (open) open.Add(h); return never.Task; };
        var waiting = Enumerable.Range(1, HostGate.MaxPrompts).Select(i => gate.IsAllowedAsync($"site{i}.example")).ToList();
        Assert.False(await gate.IsAllowedAsync("flood.example")); // refused at once, no fourth window
        Assert.Equal(HostGate.MaxPrompts, open.Count);
        never.SetResult(HostDecision.NotNow); await Task.WhenAll(waiting);
        Assert.DoesNotContain("flood.example", store.Value.BlockedHosts);
    }

    // ---- M6: the website gave up while the preview was open ----

    [Fact]
    public async Task Nothing_prints_when_the_website_stopped_waiting()
    {
        using var gone = new CancellationTokenSource();
        var (jobs, _, p, sink, _) = Rig(r => { gone.Cancel(); return new(true, r.Copies, false); }); using var _s = sink;
        var r = await jobs.PrintAsync(p, Encoding.UTF8.GetBytes("^XA^FDA^FS^XZ"), "site.example.com", 1, gone.Token);
        Assert.Equal("cancelled", r.State); Assert.Empty(await sink.WaitFor(1, 200));
    }

    // ---- M10: settings are never lost ----

    [Fact]
    public void Damaged_settings_fall_back_to_the_last_good_copy()
    {
        var path = Path.Combine(TempDir(), "settings.json");
        var s = new SettingsStore(path); s.Value.AcceptedHosts.Add("app.clinic.example"); s.Save(); s.Value.AcceptedHosts.Add("uat.clinic.example"); s.Save();
        File.WriteAllText(path, "{ \"AcceptedHosts\": [ \"app.cl"); // power cut half way through a write
        var loaded = new SettingsStore(path); loaded.Load();
        Assert.Contains("app.clinic.example", loaded.Value.AcceptedHosts);
        Assert.True(File.Exists(path + ".damaged")); Assert.False(File.Exists(path + ".tmp"));
    }

    // ---- low: allow-list entry typed as a full address ----

    [Theory]
    [InlineData("https://App.Clinic.org:8443/login?x=1", "app.clinic.org")]
    [InlineData(" uat.clinic.org/ ", "uat.clinic.org")]
    [InlineData("*.clinic.org", "*.clinic.org")]
    [InlineData("http://localhost:4200", "localhost")]
    public void Typed_addresses_become_plain_website_names(string typed, string host) => Assert.Equal(host, MainViewModel.CleanHost(typed));

    // ---- M9: text encodings ----

    [Fact]
    public void Text_can_be_sent_in_a_code_page()
    {
        Assert.Equal(new byte[] { 0xE9 }, Languages.Encode("é", PrintLanguage.Epl, Languages.FindEncoding("windows-1252")));
        Assert.Equal(new byte[] { 0x82 }, Languages.Encode("é", PrintLanguage.Epl, Languages.FindEncoding("cp850")));
        Assert.Equal(new byte[] { 0xC3, 0xA9 }, Languages.Encode("é", PrintLanguage.Zpl, Languages.FindEncoding(null)));
        Assert.Null(Languages.FindEncoding("klingon"));
    }
}

/// <summary>API-level checks for the review fixes, on a running bridge.</summary>
public class ReviewFixApiTests : IClassFixture<BridgeFixture>
{
    readonly BridgeFixture F;
    public ReviewFixApiTests(BridgeFixture f) { F = f; }
    const string Good = "https://qa.clinic.example";
    async Task<(HttpStatusCode code, string body)> Send(HttpRequestMessage r) { var res = await F.Http.SendAsync(r); return (res.StatusCode, await res.Content.ReadAsStringAsync()); }
    string Print(object o) => JsonSerializer.Serialize(o);

    [Theory]
    [InlineData("{ not json")]
    [InlineData("{\"printerId\":\"PID\",\"base64\":\"***\"}")]
    [InlineData("{\"printerId\":\"PID\",\"data\":\"x\",\"copies\":\"two\"}")]
    [InlineData("{\"printerId\":\"PID\",\"data\":\"x\",\"copies\":500}")]
    [InlineData("{\"printerId\":\"PID\",\"data\":\"x\",\"encoding\":\"klingon\"}")]
    public async Task Bad_requests_get_a_clear_400_not_a_500(string body)
    {
        var (c, b) = await Send(F.Req(HttpMethod.Post, "/api/v1/print", Good, body.Replace("PID", F.PrinterId)));
        Assert.Equal(HttpStatusCode.BadRequest, c); Assert.Contains("BAD_REQUEST", b); Assert.DoesNotContain("Exception", b);
    }

    [Fact]
    public async Task Print_accepts_a_text_encoding()
    {
        var before = (await F.Sink.WaitFor(0, 1)).Count;
        var (c, _) = await Send(F.Req(HttpMethod.Post, "/api/v1/print", Good, Print(new { printerId = F.PrinterId, data = "N\nA1,1,0,1,1,1,N,\"café\"\nP1\n", encoding = "windows-1252" })));
        Assert.Equal(HttpStatusCode.OK, c);
        var got = (await F.Sink.WaitFor(before + 1)).Last();
        Assert.Contains((byte)0xE9, got); Assert.DoesNotContain((byte)0xC3, got);
    }

    [Fact]
    public async Task Zebra_write_accepts_base64_for_binary_data()
    {
        var bytes = new byte[] { 0x1B, 0x40, 0x00, 0xFF, 0x80, 0x0A, 0x7E };
        var before = (await F.Sink.WaitFor(0, 1)).Count;
        var (c, _) = await Send(F.Req(HttpMethod.Post, "/write", Good, Print(new { device = new { uid = F.PrinterId }, base64 = Convert.ToBase64String(bytes) })));
        Assert.Equal(HttpStatusCode.OK, c);
        Assert.Contains(await F.Sink.WaitFor(before + 1), b => b.SequenceEqual(bytes));
    }

    [Theory]
    [InlineData("chrome-extension://abcdefghijklmnop")]
    [InlineData("file://")]
    [InlineData("app://qa.clinic.example")]
    public async Task Only_http_and_https_websites_are_accepted(string origin)
    {
        var (c, _) = await Send(F.Req(HttpMethod.Get, "/api/v1/status", origin));
        Assert.Equal(HttpStatusCode.Forbidden, c);
    }

    [Fact]
    public async Task Programs_on_this_computer_can_be_switched_off()
    {
        Assert.Equal(HttpStatusCode.OK, (await Send(F.Req(HttpMethod.Get, "/api/v1/status"))).code);
        F.Store.Value.AllowLocalPrograms = false;
        try
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await Send(F.Req(HttpMethod.Get, "/api/v1/status"))).code);
            Assert.Equal(HttpStatusCode.OK, (await Send(F.Req(HttpMethod.Get, "/api/v1/status", Good))).code); // websites are unaffected
        }
        finally { F.Store.Value.AllowLocalPrograms = true; }
    }

    [Fact]
    public async Task Zebra_read_returns_the_printers_answer_to_a_status_question()
    {
        // A fake printer that answers ~HS the way a Zebra does.
        var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var port = ((IPEndPoint)l.LocalEndpoint).Port;
        _ = Task.Run(async () =>
        {
            while (true) // the bridge also connects just to see whether the printer is switched on; those connections carry no data
            {
                using var c = await l.AcceptTcpClientAsync(); var ns = c.GetStream(); var buf = new byte[256];
                if (await ns.ReadAsync(buf) == 0) continue;
                await ns.WriteAsync(Encoding.ASCII.GetBytes("\u0002030,0,0,1245,000,0,0,0,000,0,0,0\u0003\r\n")); await Task.Delay(600);
            }
        });
        F.Store.Value.ManualPrinters.Add(new ManualPrinter { Name = "Talking Zebra", Host = "127.0.0.1", Port = port });
        await F.Registry.RefreshAsync();
        var id = $"tcp:127.0.0.1:{port}";
        try
        {
            Assert.Equal(HttpStatusCode.OK, (await Send(F.Req(HttpMethod.Post, "/write", Good, Print(new { device = new { uid = id }, data = "~HS" })))).code);
            var (c, body) = await Send(F.Req(HttpMethod.Post, "/read", Good, Print(new { device = new { uid = id } })));
            Assert.Equal(HttpStatusCode.OK, c); Assert.Contains("030,0,0,1245", body);
            Assert.Equal("", (await Send(F.Req(HttpMethod.Post, "/read", Good, Print(new { device = new { uid = id } })))).body); // read once
        }
        finally { l.Stop(); F.Store.Value.ManualPrinters.RemoveAll(m => m.Port == port); await F.Registry.RefreshAsync(); }
    }
}

public class ListenerRetryTests
{
    [Fact]
    public async Task A_busy_port_is_picked_up_once_it_becomes_free()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ltz-retry-" + Guid.NewGuid().ToString("N")[..8]); Directory.CreateDirectory(dir);
        var blocker = new TcpListener(IPAddress.Loopback, 0); blocker.Start(); var busy = ((IPEndPoint)blocker.LocalEndpoint).Port;
        var free = new TcpListener(IPAddress.Loopback, 0); free.Start(); var httpsPort = ((IPEndPoint)free.LocalEndpoint).Port; free.Stop();
        var store = new SettingsStore(Path.Combine(dir, "settings.json")); store.Value.HttpPort = busy; store.Value.HttpsPort = httpsPort;
        var certs = new CertManager(dir); certs.Ensure();
        var reg = new PrinterRegistry(store);
        var server = new BridgeServer(store, reg, new JobManager(reg, store), new HostGate(store), certs);
        var old = BridgeServer.RetryEvery; BridgeServer.RetryEvery = TimeSpan.FromMilliseconds(150);
        try
        {
            var changed = new TaskCompletionSource(); server.StateChanged += () => changed.TrySetResult();
            await server.StartAsync();
            Assert.False(server.HttpUp); Assert.True(server.HttpsUp); Assert.Contains("in use", server.HttpError);
            blocker.Stop();
            await Task.WhenAny(changed.Task, Task.Delay(5000));
            Assert.True(server.HttpUp); Assert.True(server.HttpsUp); Assert.Null(server.HttpError);
        }
        finally { BridgeServer.RetryEvery = old; await server.StopAsync(); }
    }
}
