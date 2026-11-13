using System.Security.Cryptography.X509Certificates;
using LtzPrintBridge.Core;

namespace LtzPrintBridge.Tests;

public class LanguageTests
{
    [Theory]
    [InlineData("SNBC TVSE LP46 Dlite BPLE", "", PrintLanguage.Bple)]
    [InlineData("LP 46 Dlite", "", PrintLanguage.Bple)]
    [InlineData("Label printer", "SNBC BPLZ", PrintLanguage.Zpl)]
    [InlineData("Label printer", "SNBC BPLB", PrintLanguage.Tspl)]
    [InlineData("ZDesigner ZD421-203dpi ZPL", "ZDesigner ZD421", PrintLanguage.Zpl)]
    [InlineData("TSC TE244", "TSC TE244", PrintLanguage.Tspl)]
    [InlineData("Honeywell PC42", "Datamax", PrintLanguage.Dpl)]
    [InlineData("Microsoft Print to PDF", "", PrintLanguage.Auto)]
    public void Guess_picks_language_from_names(string name, string driver, PrintLanguage expected) =>
        Assert.Equal(expected, Languages.Guess(name, driver).lang);

    [Theory]
    [InlineData(PrintLanguage.Zpl, "^XA", "^XZ")]
    [InlineData(PrintLanguage.Tspl, "SIZE", "PRINT")]
    [InlineData(PrintLanguage.Epl, "\nN\n", "P1")]
    [InlineData(PrintLanguage.Bple, "\nN\n", "P1")]
    [InlineData(PrintLanguage.Cpcl, "! 0", "PRINT")]
    public void Sample_labels_have_language_framing(PrintLanguage l, string start, string end)
    {
        var s = Languages.Sample(l);
        Assert.Contains(start, s); Assert.Contains(end, s);
        Assert.Contains("TEST LABEL", s);
    }

    [Fact] public void Encode_is_utf8_without_bom() => Assert.Equal(new byte[] { 0x5E, 0x58, 0x41 }, Languages.Encode("^XA", PrintLanguage.Zpl));
    [Fact] public void Every_language_choice_has_a_name() { foreach (var l in Languages.Choices) Assert.False(string.IsNullOrWhiteSpace(Languages.Display(l))); }
}

public class HostGateTests
{
    static (HostGate g, SettingsStore s) Make(Func<string, Task<HostDecision>>? prompt = null)
    {
        var s = new SettingsStore(); s.Value.AcceptedHosts.Add("qa.clinic.example"); var g = new HostGate(s) { Prompt = prompt }; return (g, s);
    }

    [Fact] public async Task Accepted_hosts_pass_without_prompt()
    {
        int asked = 0; var (g, _) = Make(h => { asked++; return Task.FromResult(HostDecision.Block); });
        Assert.True(await g.IsAllowedAsync("qa.clinic.example")); Assert.True(await g.IsAllowedAsync("LOCALHOST")); Assert.Equal(0, asked);
    }
    [Fact] public async Task Blocked_wins_over_everything()
    {
        var (g, s) = Make(); s.Value.BlockedHosts.Add("qa.clinic.example");
        Assert.False(await g.IsAllowedAsync("qa.clinic.example"));
    }
    [Fact] public async Task Unknown_host_without_prompt_handler_is_denied() { var (g, _) = Make(); Assert.False(await g.IsAllowedAsync("evil.example")); }
    [Fact] public async Task Wildcard_matches_subdomains_only()
    {
        var (g, s) = Make(); s.Value.AcceptedHosts.Add("*.clinic.org");
        Assert.True(await g.IsAllowedAsync("a.clinic.org")); Assert.False(await g.IsAllowedAsync("clinic.org.evil.com"));
    }
    [Fact] public async Task Always_allow_persists()
    {
        var (g, s) = Make(_ => Task.FromResult(HostDecision.AllowAlways));
        Assert.True(await g.IsAllowedAsync("new.site")); Assert.Contains("new.site", s.Value.AcceptedHosts);
    }
    [Fact] public async Task Allow_once_does_not_persist_but_sticks_for_session()
    {
        int asked = 0; var (g, s) = Make(_ => { asked++; return Task.FromResult(HostDecision.AllowOnce); });
        Assert.True(await g.IsAllowedAsync("once.site")); Assert.True(await g.IsAllowedAsync("once.site"));
        Assert.DoesNotContain("once.site", s.Value.AcceptedHosts); Assert.Equal(1, asked);
    }
    [Fact] public async Task Block_persists_and_is_not_asked_again()
    {
        int asked = 0; var (g, s) = Make(_ => { asked++; return Task.FromResult(HostDecision.Block); });
        Assert.False(await g.IsAllowedAsync("bad.site")); Assert.False(await g.IsAllowedAsync("bad.site"));
        Assert.Contains("bad.site", s.Value.BlockedHosts); Assert.Equal(1, asked);
    }
    [Fact] public async Task Concurrent_requests_share_one_prompt()
    {
        int asked = 0; var tcs = new TaskCompletionSource<HostDecision>();
        var (g, _) = Make(_ => { Interlocked.Increment(ref asked); return tcs.Task; });
        var t = Enumerable.Range(0, 5).Select(_ => g.IsAllowedAsync("burst.site")).ToArray();
        await Task.Delay(100); tcs.SetResult(HostDecision.AllowAlways);
        Assert.All(await Task.WhenAll(t), Assert.True); Assert.Equal(1, asked);
    }
}

public class CertificateTests
{
    [Fact] public void Leaf_cert_is_valid_for_loopback_names_and_signed_by_discarded_ca()
    {
        var m = new CertManager(); m.Ensure(); Assert.Null(m.Error); Assert.NotNull(m.ServerCert);
        var leaf = m.ServerCert!;
        var san = leaf.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();
        Assert.Contains("localhost", san.EnumerateDnsNames());
        Assert.Contains(System.Net.IPAddress.Loopback, san.EnumerateIPAddresses());
        Assert.Contains(System.Net.IPAddress.IPv6Loopback, san.EnumerateIPAddresses());
        Assert.False(leaf.Extensions.OfType<X509BasicConstraintsExtension>().Single().CertificateAuthority);
        Assert.Contains(leaf.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single().EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>(), o => o.Value == "1.3.6.1.5.5.7.3.1");
        Assert.True((leaf.NotAfter - leaf.NotBefore).TotalDays <= 400);
        Assert.True(leaf.HasPrivateKey);

        var ca = new X509Certificate2(m.CaPath);
        Assert.False(ca.HasPrivateKey);             // the CA key is never stored
        Assert.True(ca.Extensions.OfType<X509BasicConstraintsExtension>().Single().CertificateAuthority);
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        Assert.True(chain.Build(leaf), string.Join(";", chain.ChainStatus.Select(s => s.StatusInformation)));
    }
    [Fact] public void Second_start_reuses_the_same_certificate()
    {
        var a = new CertManager(); a.Ensure(); var b = new CertManager(); b.Ensure();
        Assert.Equal(a.ServerCert!.Thumbprint, b.ServerCert!.Thumbprint);
    }
}

public class SettingsTests
{
    [Fact] public void Roundtrip_keeps_printers_hosts_and_overrides()
    {
        var a = new SettingsStore();
        a.Value.AcceptedHosts.Add("roundtrip.test"); a.Value.LanguageOverrides["spool:X"] = PrintLanguage.Tspl;
        a.Value.ManualPrinters.Add(new ManualPrinter { Name = "N", Host = "10.0.0.5", Port = 9100, Language = PrintLanguage.Bple });
        a.Save();
        var b = new SettingsStore(); b.Load();
        Assert.Contains("roundtrip.test", b.Value.AcceptedHosts);
        Assert.Equal(PrintLanguage.Tspl, b.Value.LanguageOverrides["spool:X"]);
        Assert.Equal(PrintLanguage.Bple, b.Value.ManualPrinters.Single(p => p.Host == "10.0.0.5").Language);
    }
}
