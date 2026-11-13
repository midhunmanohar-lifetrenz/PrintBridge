using LtzPrintBridge.Core;

namespace LtzPrintBridge.Tests;

public class RequestLogAndResetTests : IClassFixture<BridgeFixture>
{
    readonly BridgeFixture F;
    public RequestLogAndResetTests(BridgeFixture f) { F = f; }

    [Fact]
    public async Task Requests_are_recorded_with_site_and_outcome()
    {
        var seen = new List<RequestEntry>();
        void On(RequestEntry e) { lock (seen) seen.Add(e); }
        F.Server.Requested += On;
        try
        {
            (await F.Http.SendAsync(F.Req(HttpMethod.Get, "/api/v1/status", "https://qa.clinic.example"))).Dispose();
            (await F.Http.SendAsync(F.Req(HttpMethod.Get, "/api/v1/status", "https://sit.unknown-site.example"))).Dispose();
        }
        finally { F.Server.Requested -= On; }
        lock (seen)
        {
            var ok = Assert.Single(seen, e => e.Site == "qa.clinic.example");
            Assert.Equal(200, ok.Status);
            var no = Assert.Single(seen, e => e.Site == "sit.unknown-site.example");
            Assert.Equal(403, no.Status);
            Assert.Contains("not allowed", no.Result);
        }
    }

    [Fact]
    public async Task Reset_forgets_every_site_but_keeps_localhost_and_wildcards_work()
    {
        var store = new SettingsStore();
        var gate = new HostGate(store) { Prompt = _ => Task.FromResult(HostDecision.Block) };
        gate.Accept("*.example-clinic.com");
        gate.Block("evil.example");
        Assert.True(await gate.IsAllowedAsync("uat.example-clinic.com"));
        Assert.False(await gate.IsAllowedAsync("evil.example"));

        gate.ResetAll();

        Assert.Equal(new[] { "localhost", "127.0.0.1" }, store.Value.AcceptedHosts.Take(2));
        Assert.Empty(store.Value.BlockedHosts);
        Assert.DoesNotContain("*.example-clinic.com", store.Value.AcceptedHosts);
        Assert.True(await gate.IsAllowedAsync("localhost"));
    }
}
