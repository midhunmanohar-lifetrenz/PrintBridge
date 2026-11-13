using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LtzPrintBridge.Core;

namespace LtzPrintBridge.Tests;

/// <summary>
/// A network label printer that answers status questions the way a Zebra (~HS) or a TSC (ESC ! ?) does and records
/// everything else as a print job. Each answer is taken from <see cref="Answers"/>; the last one repeats.
/// </summary>
public sealed class TalkingPrinter : IDisposable
{
    readonly TcpListener _l = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource _cts = new();
    public int Port => ((IPEndPoint)_l.LocalEndpoint).Port;
    public List<byte[]> Jobs { get; } = new();
    public int Questions;
    public Queue<byte[]> Answers { get; } = new();

    public static byte[] Zebra(bool paperOut = false, bool paused = false, bool headOpen = false, bool ribbonOut = false, int waiting = 0, int remaining = 0) => Encoding.ASCII.GetBytes(
        $"\u0002030,{(paperOut ? 1 : 0)},{(paused ? 1 : 0)},1245,{waiting:000},0,0,0,000,0,0,0\u0003\r\n" +
        $"\u0002000,0,{(headOpen ? 1 : 0)},{(ribbonOut ? 1 : 0)},0,2,4,0,{remaining:00000000},1,000\u0003\r\n\u00021234,0\u0003\r\n");

    public TalkingPrinter(params byte[][] answers)
    {
        foreach (var a in answers) Answers.Enqueue(a);
        _l.Start();
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    using var c = await _l.AcceptTcpClientAsync(_cts.Token); var ns = c.GetStream(); var buf = new byte[65536];
                    using var first = new CancellationTokenSource(300);
                    int n; try { n = await ns.ReadAsync(buf, first.Token); } catch (OperationCanceledException) { continue; }
                    if (n == 0) continue; // a "are you switched on" probe
                    var data = buf[..n];
                    bool question = Encoding.ASCII.GetString(data).StartsWith("~H") || data.SequenceEqual(new byte[] { 0x1B, 0x21, 0x3F });
                    if (question)
                    {
                        Interlocked.Increment(ref Questions);
                        byte[] answer; lock (Answers) answer = Answers.Count > 1 ? Answers.Dequeue() : Answers.Count == 1 ? Answers.Peek() : Array.Empty<byte>();
                        if (answer.Length > 0) await ns.WriteAsync(answer);
                        await Task.Delay(50);
                    }
                    else
                    {
                        using var ms = new MemoryStream(); ms.Write(data);
                        try { using var rest = new CancellationTokenSource(300); while ((n = await ns.ReadAsync(buf, rest.Token)) > 0) ms.Write(buf, 0, n); } catch (OperationCanceledException) { }
                        lock (Jobs) Jobs.Add(ms.ToArray());
                    }
                }
                catch { }
            }
        });
    }
    public void Dispose() { _cts.Cancel(); _l.Stop(); }
}

public class PrinterStatusParsingTests
{
    [Fact]
    public void Zebra_ready_answer_is_ready()
    {
        var s = PrinterStatus.ParseZplHostStatus(Encoding.ASCII.GetString(TalkingPrinter.Zebra()))!;
        Assert.True(s.Ready); Assert.Equal("ready", s.State); Assert.Equal("printer", s.Source); Assert.False(s.Busy); Assert.Empty(s.Issues);
    }

    [Fact]
    public void Zebra_problems_are_named()
    {
        var s = PrinterStatus.ParseZplHostStatus(Encoding.ASCII.GetString(TalkingPrinter.Zebra(paperOut: true, headOpen: true, paused: true)))!;
        Assert.False(s.Ready); Assert.Equal("paper_out", s.State);
        Assert.Equal(new[] { "paper_out", "head_open", "paused" }, s.Issues);
        Assert.Contains("Out of labels", s.Message); Assert.Contains("Print head is open", s.Message);
        Assert.Equal("ribbon_out", PrinterStatus.ParseZplHostStatus(Encoding.ASCII.GetString(TalkingPrinter.Zebra(ribbonOut: true)))!.State);
    }

    [Fact]
    public void Zebra_with_labels_still_to_print_is_busy_not_finished()
    {
        Assert.True(PrinterStatus.ParseZplHostStatus(Encoding.ASCII.GetString(TalkingPrinter.Zebra(remaining: 3)))!.Busy);
        Assert.True(PrinterStatus.ParseZplHostStatus(Encoding.ASCII.GetString(TalkingPrinter.Zebra(waiting: 1)))!.Busy);
        Assert.Equal("printing", PrinterStatus.ParseZplHostStatus(Encoding.ASCII.GetString(TalkingPrinter.Zebra(remaining: 3)))!.State);
    }

    [Theory]
    [InlineData("")] [InlineData("garbage")] [InlineData("\u0002too,short\u0003")]
    public void Text_that_is_not_a_zebra_status_is_not_trusted(string reply) => Assert.Null(PrinterStatus.ParseZplHostStatus(reply));

    [Theory]
    [InlineData(0x00, true, "ready")]
    [InlineData(0x01, false, "head_open")]
    [InlineData(0x02, false, "paper_jam")]
    [InlineData(0x04, false, "paper_out")]
    [InlineData(0x08, false, "ribbon_out")]
    [InlineData(0x10, false, "paused")]
    [InlineData(0x20, true, "printing")]
    [InlineData(0x80, false, "error")]
    [InlineData(0x05, false, "paper_out")]
    public void Tsc_status_byte_is_read(byte b, bool ready, string state)
    {
        var s = PrinterStatus.ParseTsplStatus(b); Assert.Equal(ready, s.Ready); Assert.Equal(state, s.State);
    }

    [Theory]
    [InlineData("Ready", "ok", true, "ready")]
    [InlineData("Printing", "ok", true, "printing")]
    [InlineData("Paper out", "bad", false, "paper_out")]
    [InlineData("Paper jam", "bad", false, "paper_jam")]
    [InlineData("Offline", "bad", false, "offline")]
    [InlineData("Door open", "bad", false, "head_open")]
    [InlineData("Paused", "warn", true, "paused")]
    [InlineData("Ribbon or toner low", "warn", true, "ribbon_low")]
    public void System_queue_state_becomes_a_status(string text, string level, bool ready, string state)
    {
        var s = PrinterStatus.FromSystem((text, level)); Assert.Equal(ready, s.Ready); Assert.Equal(state, s.State); Assert.Equal("system", s.Source);
    }
}

public class StatusAndConfirmApiTests : IClassFixture<BridgeFixture>
{
    readonly BridgeFixture F;
    public StatusAndConfirmApiTests(BridgeFixture f) { F = f; F.Jobs.ConfirmDelaysMs = new[] { 60, 60, 60, 60, 60, 60, 60, 60 }; }
    const string Good = "https://qa.clinic.example";
    async Task<(HttpStatusCode code, JsonElement json, string body)> Send(HttpRequestMessage r)
    {
        var res = await F.Http.SendAsync(r); var b = await res.Content.ReadAsStringAsync();
        JsonElement j = default; try { j = JsonDocument.Parse(b).RootElement; } catch { }
        return (res.StatusCode, j, b);
    }
    static string Q(string id) => Uri.EscapeDataString(id);

    async Task<string> Add(TalkingPrinter p, PrintLanguage lang = PrintLanguage.Zpl)
    {
        F.Store.Value.ManualPrinters.Add(new ManualPrinter { Name = "Talking " + p.Port, Host = "127.0.0.1", Port = p.Port, Language = lang });
        await F.Registry.RefreshAsync();
        return $"tcp:127.0.0.1:{p.Port}";
    }
    string Body(string id, object? extra = null)
    {
        var d = new Dictionary<string, object> { ["printerId"] = id, ["data"] = "^XA^FO10,10^FDA^FS^XZ" };
        if (extra != null) foreach (var pr in extra.GetType().GetProperties()) d[pr.Name] = pr.GetValue(extra)!;
        return JsonSerializer.Serialize(d);
    }

    [Fact]
    public async Task Website_can_ask_a_zebra_whether_it_is_ready()
    {
        using var p = new TalkingPrinter(TalkingPrinter.Zebra(), TalkingPrinter.Zebra(paperOut: true)); var id = await Add(p);
        var (c, j, _) = await Send(F.Req(HttpMethod.Get, "/api/v1/printer-status?printerId=" + Q(id), Good));
        Assert.Equal(HttpStatusCode.OK, c); Assert.True(j.GetProperty("ready").GetBoolean()); Assert.Equal("printer", j.GetProperty("source").GetString());
        (_, j, _) = await Send(F.Req(HttpMethod.Get, "/api/v1/printer-status?printerId=" + Q(id), Good));
        Assert.False(j.GetProperty("ready").GetBoolean()); Assert.Equal("paper_out", j.GetProperty("state").GetString()); Assert.Equal("Out of labels", j.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Tsc_printer_status_is_read_from_its_status_byte()
    {
        using var p = new TalkingPrinter(new byte[] { 0x08 }); var id = await Add(p, PrintLanguage.Tspl);
        var (_, j, _) = await Send(F.Req(HttpMethod.Get, "/api/v1/printer-status?printerId=" + Q(id), Good));
        Assert.Equal("ribbon_out", j.GetProperty("state").GetString()); Assert.False(j.GetProperty("ready").GetBoolean());
    }

    [Fact]
    public async Task Printer_that_cannot_answer_is_reported_honestly()
    {
        using var p = new TalkingPrinter(); var id = await Add(p, PrintLanguage.EscPos);
        var (_, j, _) = await Send(F.Req(HttpMethod.Get, "/api/v1/printer-status?printerId=" + Q(id), Good));
        Assert.True(j.GetProperty("ready").GetBoolean()); Assert.Equal("network", j.GetProperty("source").GetString());
        int port = p.Port; p.Dispose(); await Task.Delay(100);
        (_, j, _) = await Send(F.Req(HttpMethod.Get, "/api/v1/printer-status?printerId=" + Q($"tcp:127.0.0.1:{port}"), Good));
        Assert.Equal("offline", j.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Unknown_printer_status_is_404()
        => Assert.Equal(HttpStatusCode.NotFound, (await Send(F.Req(HttpMethod.Get, "/api/v1/printer-status?printerId=nope", Good))).code);

    [Fact]
    public async Task Waiting_print_reports_printed_once_the_printer_has_finished()
    {
        using var p = new TalkingPrinter(TalkingPrinter.Zebra(remaining: 2), TalkingPrinter.Zebra(remaining: 1), TalkingPrinter.Zebra()); var id = await Add(p);
        var (c, j, _) = await Send(F.Req(HttpMethod.Post, "/api/v1/print", Good, Body(id, new { waitSeconds = 5 })));
        Assert.Equal(HttpStatusCode.OK, c); Assert.Equal("printed", j.GetProperty("state").GetString()); Assert.True(j.GetProperty("confirmed").GetBoolean());
        Assert.Single(p.Jobs); Assert.True(p.Questions >= 3);
        var (jc, job, _) = await Send(F.Req(HttpMethod.Get, "/api/v1/jobs/" + j.GetProperty("jobId").GetString(), Good));
        Assert.Equal(HttpStatusCode.OK, jc); Assert.Equal("printed", job.GetProperty("state").GetString()); Assert.Equal("printer", job.GetProperty("source").GetString());
    }

    [Fact]
    public async Task Print_without_waiting_answers_at_once_and_the_job_can_be_followed()
    {
        using var p = new TalkingPrinter(TalkingPrinter.Zebra(remaining: 1), TalkingPrinter.Zebra()); var id = await Add(p);
        var (_, j, _) = await Send(F.Req(HttpMethod.Post, "/api/v1/print", Good, Body(id)));
        Assert.Equal("submitted", j.GetProperty("state").GetString()); Assert.False(j.GetProperty("confirmed").GetBoolean());
        var jobId = j.GetProperty("jobId").GetString(); string state = "";
        for (int i = 0; i < 40 && state != "printed"; i++) { await Task.Delay(50); state = (await Send(F.Req(HttpMethod.Get, "/api/v1/jobs/" + jobId, Good))).json.GetProperty("state").GetString()!; }
        Assert.Equal("printed", state);
    }

    [Fact]
    public async Task Paper_out_after_sending_is_reported_as_attention_not_printed()
    {
        using var p = new TalkingPrinter(TalkingPrinter.Zebra(paperOut: true, waiting: 1)); var id = await Add(p);
        var (c, j, _) = await Send(F.Req(HttpMethod.Post, "/api/v1/print", Good, Body(id, new { waitSeconds = 2 })));
        Assert.Equal(HttpStatusCode.OK, c); Assert.Equal("attention", j.GetProperty("state").GetString());
        Assert.False(j.GetProperty("confirmed").GetBoolean()); Assert.Contains("Out of labels", j.GetProperty("message").GetString());
    }

    [Fact]
    public async Task Require_ready_refuses_to_send_to_a_printer_with_a_problem()
    {
        using var p = new TalkingPrinter(TalkingPrinter.Zebra(headOpen: true)); var id = await Add(p);
        var (c, j, _) = await Send(F.Req(HttpMethod.Post, "/api/v1/print", Good, Body(id, new { requireReady = true })));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, c); Assert.Equal("PRINTER_NOT_READY", j.GetProperty("error").GetString());
        Assert.Equal("head_open", j.GetProperty("printerState").GetProperty("state").GetString());
        await Task.Delay(200); Assert.Empty(p.Jobs);
    }

    [Fact]
    public async Task Printer_without_a_status_language_stays_submitted_and_says_why()
    {
        using var p = new TalkingPrinter(); var id = await Add(p, PrintLanguage.EscPos);
        var (_, j, _) = await Send(F.Req(HttpMethod.Post, "/api/v1/print", Good, Body(id, new { waitSeconds = 2 })));
        Assert.Equal("submitted", j.GetProperty("state").GetString()); Assert.False(j.GetProperty("confirmed").GetBoolean());
        Assert.Contains("cannot report", j.GetProperty("message").GetString()); Assert.Equal(0, p.Questions);
    }

    [Fact]
    public async Task Following_jobs_can_be_switched_off()
    {
        using var p = new TalkingPrinter(TalkingPrinter.Zebra()); var id = await Add(p);
        F.Store.Value.FollowJobs = false;
        try
        {
            var (_, j, _) = await Send(F.Req(HttpMethod.Post, "/api/v1/print", Good, Body(id, new { waitSeconds = 1 })));
            Assert.Equal("submitted", j.GetProperty("state").GetString()); await Task.Delay(300); Assert.Equal(0, p.Questions);
        }
        finally { F.Store.Value.FollowJobs = true; }
    }

    [Theory]
    [InlineData("{\"waitSeconds\":31}")] [InlineData("{\"waitSeconds\":-1}")] [InlineData("{\"waitSeconds\":\"long\"}")]
    public async Task Wait_seconds_out_of_range_is_a_400(string extra)
    {
        var body = "{\"printerId\":\"" + F.PrinterId + "\",\"data\":\"x\"," + extra[1..];
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(F.Req(HttpMethod.Post, "/api/v1/print", Good, body))).code);
    }

    [Fact]
    public async Task Unknown_job_is_404()
        => Assert.Equal(HttpStatusCode.NotFound, (await Send(F.Req(HttpMethod.Get, "/api/v1/jobs/job_00000000", Good))).code);

    [Fact]
    public async Task Zebra_library_calls_for_configuration_and_conversion_are_answered()
    {
        var (c, j, _) = await Send(F.Req(HttpMethod.Get, "/config", Good));
        Assert.Equal(HttpStatusCode.OK, c); Assert.StartsWith("1.3.", j.GetProperty("application").GetProperty("version").GetString());
        var (cc, _, body) = await Send(F.Req(HttpMethod.Post, "/convert", Good, "{}"));
        Assert.Equal(HttpStatusCode.NotImplemented, cc); Assert.Contains("cannot convert", body);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(F.Req(HttpMethod.Get, "/config", "https://evil.example"))).code);
    }
}
