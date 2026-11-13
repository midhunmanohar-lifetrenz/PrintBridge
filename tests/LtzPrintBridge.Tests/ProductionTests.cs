using LtzPrintBridge.Core;

namespace LtzPrintBridge.Tests;

public class ProductionDefaultsTests
{
    [Fact] public void Fresh_install_has_no_sample_printers_or_third_party_hosts()
    {
        var s = new AppSettings();
        Assert.Equal(new[] { "localhost", "127.0.0.1" }, s.AcceptedHosts);
        Assert.Empty(s.BlockedHosts); Assert.Empty(s.ManualPrinters); Assert.Empty(s.LanguageOverrides); Assert.Null(s.DefaultPrinterId);
    }

    [Fact] public void Test_label_contains_no_personal_or_sample_patient_data()
    {
        foreach (var l in Languages.Choices)
        {
            var s = Languages.Sample(l);
            Assert.DoesNotContain("Rahul", s); Assert.DoesNotContain("Menon", s); Assert.DoesNotContain("1000482", s);
            Assert.Contains("TEST LABEL", s);
        }
    }

    [Fact] public async Task Built_in_pages_contain_no_sample_data()
    {
        using var f = new BridgeFixtureLite(); await f.StartAsync();
        foreach (var p in new[] { "/test", "/ssl_support" })
        { var t = await f.Http.GetStringAsync(f.Base + p); Assert.DoesNotContain("Rahul", t); Assert.DoesNotContain("1000482", t); }
    }
}

public class PolicyTests
{
    static string Write(string json) { var p = Path.Combine(Path.GetTempPath(), "policy-" + Guid.NewGuid().ToString("N")[..6] + ".json"); File.WriteAllText(p, json); return p; }

    [Fact] public void Policy_preloads_hosts_without_duplicates_and_lowercases()
    {
        var s = new SettingsStore();
        Assert.True(Policy.Apply(s, new[] { Write("{\"acceptedHosts\":[\"App.Clinic.org\",\"localhost\"],\"zebraCompatibility\":false}") }));
        Assert.Contains("app.clinic.org", s.Value.AcceptedHosts); Assert.Single(s.Value.AcceptedHosts, h => h == "localhost");
    }
    [Fact] public void Policy_blocked_wins_over_accepted()
    {
        var s = new SettingsStore();
        Policy.Apply(s, new[] { Write("{\"acceptedHosts\":[\"x.org\"],\"blockedHosts\":[\"x.org\"]}") });
        Assert.DoesNotContain("x.org", s.Value.AcceptedHosts); Assert.Contains("x.org", s.Value.BlockedHosts);
    }
    [Fact] public void Policy_sets_default_printer_name_and_valid_ports_only()
    {
        var s = new SettingsStore();
        Policy.Apply(s, new[] { Write("{\"defaultPrinter\":\"Pharmacy\",\"httpPort\":19100,\"httpsPort\":99999}") });
        Assert.Equal("Pharmacy", Policy.DefaultPrinterName); Assert.Equal(19100, s.Value.HttpPort); Assert.Equal(9101, s.Value.HttpsPort);
    }
    [Fact] public void Missing_or_broken_policy_is_ignored_safely()
    {
        var s = new SettingsStore(); var before = s.Value.AcceptedHosts.Count;
        Assert.False(Policy.Apply(s, new[] { "/does/not/exist.json" }));
        Assert.False(Policy.Apply(s, new[] { Write("{ not json") }));
        Assert.Equal(before, s.Value.AcceptedHosts.Count);
    }
    [Fact] public void Policy_allows_comments_and_trailing_commas()
    {
        var s = new SettingsStore();
        Assert.True(Policy.Apply(s, new[] { Write("{ // clinic sites\n \"acceptedHosts\": [\"a.org\",], }") })); Assert.Contains("a.org", s.Value.AcceptedHosts);
    }
}

/// <summary>Minimal bridge for page checks.</summary>
sealed class BridgeFixtureLite : IDisposable
{
    readonly BridgeFixture _f = new();
    public HttpClient Http => _f.Http; public string Base => _f.Base;
    public Task StartAsync() => _f.InitializeAsync();
    public void Dispose() { _f.DisposeAsync().GetAwaiter().GetResult(); }
}
