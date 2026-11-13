using System.Text;
using LtzPrintBridge.Core;

namespace LtzPrintBridge.Tests;

public class PreviewFlowTests
{
    static (JobManager jobs, SettingsStore store, PrinterInfo printer, TcpSink sink, List<JobManager.PrintReview> asked) Rig(bool preview, PrintLanguage lang, Func<JobManager.PrintReview, JobManager.ReviewDecision> answer)
    {
        var sink = new TcpSink();
        var store = new SettingsStore(); store.Value.PreviewMode = preview ? PreviewMode.All : PreviewMode.Off;
        var jobs = new JobManager(new PrinterRegistry(store), store);
        var asked = new List<JobManager.PrintReview>();
        jobs.Review = r => { asked.Add(r); return Task.FromResult(answer(r)); };
        var p = new PrinterInfo { Id = $"tcp:127.0.0.1:{sink.Port}", Name = "Fake", Transport = TransportKind.Tcp, Address = $"127.0.0.1:{sink.Port}", Language = lang };
        return (jobs, store, p, sink, asked);
    }

    static byte[] Zpl(string s) => Encoding.UTF8.GetBytes(s);
    const string One = "^XA^FO10,10^A0N,30,30^FDHello^FS^XZ";

    [Fact]
    public async Task Preview_off_sends_straight_away_and_never_asks()
    {
        var (jobs, _, p, sink, asked) = Rig(false, PrintLanguage.Zpl, _ => new(true, 1, false));
        using var _s = sink;
        var r = await jobs.PrintAsync(p, Zpl(One), "site.example.com");
        Assert.True(r.Ok); Assert.Empty(asked);
        Assert.Equal(Zpl(One), (await sink.WaitFor(1))[0]);
    }

    [Fact]
    public async Task Zpl_copies_chosen_in_preview_become_one_job_with_pq()
    {
        var (jobs, _, p, sink, asked) = Rig(true, PrintLanguage.Zpl, _ => new(true, 3, false));
        using var _s = sink;
        var r = await jobs.PrintAsync(p, Zpl(One), "site.example.com");
        Assert.True(r.Ok);
        var q = Assert.Single(asked); Assert.True(q.PerLabel); Assert.Equal(1, q.Copies); Assert.Single(q.Labels);
        var got = await sink.WaitFor(1); Assert.Single(got);
        Assert.Equal("^XA^FO10,10^A0N,30,30^FDHello^FS^PQ3^XZ", Encoding.UTF8.GetString(got[0]));
    }

    [Fact]
    public async Task Existing_quantity_is_the_starting_value()
    {
        var (jobs, _, p, sink, asked) = Rig(true, PrintLanguage.Zpl, r => new(true, r.Copies, false));
        using var _s = sink;
        await jobs.PrintAsync(p, Zpl("^XA^FDx^FS^PQ4^XZ"), "site.example.com");
        Assert.Equal(4, asked[0].Copies);
        Assert.Equal("^XA^FDx^FS^PQ4^XZ", Encoding.UTF8.GetString((await sink.WaitFor(1))[0]));
    }

    [Fact]
    public async Task Tspl_copies_chosen_in_preview_are_written_into_print()
    {
        var (jobs, _, p, sink, asked) = Rig(true, PrintLanguage.Tspl, _ => new(true, 3, false));
        using var _s = sink;
        var r = await jobs.PrintAsync(p, Zpl("SIZE 50 mm,25 mm\r\nGAP 2 mm,0\r\nCLS\r\nTEXT 20,20,\"3\",0,1,1,\"Hi\"\r\nPRINT 1\r\n"), "site.example.com");
        Assert.True(r.Ok); Assert.True(asked[0].PerLabel); Assert.Equal(PrintLanguage.Tspl, asked[0].Kind); Assert.Single(asked[0].Labels);
        var got = await sink.WaitFor(1); Assert.Single(got);
        Assert.EndsWith("PRINT 1,3\r\n", Encoding.UTF8.GetString(got[0]));
    }

    [Fact]
    public async Task Epl_copies_chosen_in_preview_are_written_into_p()
    {
        var (jobs, _, p, sink, asked) = Rig(true, PrintLanguage.Epl, _ => new(true, 4, false));
        using var _s = sink;
        await jobs.PrintAsync(p, Zpl("\nN\nq400\nQ200,24\nA20,20,0,3,1,1,N,\"Hi\"\nP1\n"), "site.example.com");
        Assert.Equal(PrintLanguage.Epl, asked[0].Kind); Assert.True(asked[0].PerLabel);
        Assert.EndsWith("P4\n", Encoding.UTF8.GetString((await sink.WaitFor(1))[0]));
    }

    [Fact]
    public async Task Unrecognised_languages_repeat_the_whole_job()
    {
        var (jobs, _, p, sink, asked) = Rig(true, PrintLanguage.Dpl, _ => new(true, 2, false));
        using var _s = sink;
        var r = await jobs.PrintAsync(p, Zpl("some other printer language\r\n"), "site.example.com");
        Assert.True(r.Ok); Assert.False(asked[0].PerLabel); Assert.Empty(asked[0].Labels); Assert.Null(asked[0].Kind);
        Assert.Equal(2, (await sink.WaitFor(2)).Count);
    }

    [Fact]
    public async Task Selected_mode_asks_only_for_the_chosen_websites()
    {
        var (jobs, store, p, sink, asked) = Rig(true, PrintLanguage.Zpl, _ => new(true, 1, false));
        using var _s = sink;
        store.Value.PreviewMode = PreviewMode.Selected; store.Value.PreviewSites.Add("uat.example.com"); store.Value.PreviewSites.Add("*.clinic.org");
        await jobs.PrintAsync(p, Zpl(One), "qa.example.com");
        Assert.Empty(asked);
        await jobs.PrintAsync(p, Zpl(One), "uat.example.com");
        await jobs.PrintAsync(p, Zpl(One), "north.clinic.org");
        Assert.Equal(new[] { "uat.example.com", "north.clinic.org" }, asked.Select(a => a.Site));
        Assert.Equal(3, (await sink.WaitFor(3)).Count);
    }

    [Fact]
    public async Task Selected_mode_with_no_websites_chosen_asks_nobody()
    {
        var (jobs, store, p, sink, asked) = Rig(true, PrintLanguage.Zpl, _ => new(true, 1, false));
        using var _s = sink; store.Value.PreviewMode = PreviewMode.Selected;
        await jobs.PrintAsync(p, Zpl(One), "any.example.com");
        Assert.Empty(asked);
    }

    [Fact]
    public async Task Cancel_prints_nothing_and_says_cancelled()
    {
        var (jobs, _, p, sink, _) = Rig(true, PrintLanguage.Zpl, _ => new(false, 1, false));
        using var _s = sink;
        var r = await jobs.PrintAsync(p, Zpl(One), "site.example.com");
        Assert.False(r.Ok); Assert.Equal("cancelled", r.State);
        await Task.Delay(300); Assert.Empty(sink.Received);
    }

    [Fact]
    public async Task Skip_rest_silences_only_that_website_for_a_while()
    {
        var (jobs, _, p, sink, asked) = Rig(true, PrintLanguage.Zpl, _ => new(true, 1, true));
        using var _s = sink;
        await jobs.PrintAsync(p, Zpl(One), "a.example.com");
        await jobs.PrintAsync(p, Zpl(One), "a.example.com");
        Assert.Single(asked);
        await jobs.PrintAsync(p, Zpl(One), "b.example.com");
        Assert.Equal(2, asked.Count);
        Assert.Equal(3, (await sink.WaitFor(3)).Count);
    }

    [Theory]
    [InlineData("PrintBridge app")]
    public async Task The_apps_own_test_prints_are_never_held_up(string site)
    {
        var (jobs, _, p, sink, asked) = Rig(true, PrintLanguage.Zpl, _ => new(false, 1, false));
        using var _s = sink;
        var r = await jobs.PrintAsync(p, Zpl(One), site);
        Assert.True(r.Ok); Assert.Empty(asked);
    }

    [Fact]
    public async Task Binary_graphics_are_sent_unchanged_even_when_preview_is_on()
    {
        var (jobs, _, p, sink, asked) = Rig(true, PrintLanguage.Zpl, _ => new(true, 2, false));
        using var _s = sink;
        var data = new byte[] { 0x5E, 0x58, 0x41, 0xFF, 0xFE, 0x00, 0x5E, 0x58, 0x5A };
        await jobs.PrintAsync(p, data, "site.example.com");
        Assert.False(asked[0].PerLabel);
        var got = await sink.WaitFor(2); Assert.All(got, g => Assert.Equal(data, g));
    }
}
