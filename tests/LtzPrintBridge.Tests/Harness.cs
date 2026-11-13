using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using LtzPrintBridge.Core;
using LtzPrintBridge.Server;

namespace LtzPrintBridge.Tests;

static class TestEnv
{
    [ModuleInitializer]
    public static void Init() => Environment.SetEnvironmentVariable("LTZ_DATA_DIR", Path.Combine(Path.GetTempPath(), "ltz-tests-" + Guid.NewGuid().ToString("N")[..8]));
}

/// <summary>A fake network label printer: accepts connections and records every payload.</summary>
public sealed class TcpSink : IDisposable
{
    readonly TcpListener _l = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource _cts = new();
    public List<byte[]> Received { get; } = new();
    public int Port => ((IPEndPoint)_l.LocalEndpoint).Port;
    public TcpSink()
    {
        _l.Start();
        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    var c = await _l.AcceptTcpClientAsync(_cts.Token);
                    using var ms = new MemoryStream();
                    await c.GetStream().CopyToAsync(ms, _cts.Token);
                    if (ms.Length > 0) lock (Received) Received.Add(ms.ToArray());
                    c.Dispose();
                }
                catch { }
            }
        });
    }
    public async Task<List<byte[]>> WaitFor(int count, int ms = 3000)
    {
        var end = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < end) { lock (Received) if (Received.Count >= count) break; await Task.Delay(25); }
        lock (Received) return Received.ToList();
    }
    public void Dispose() { _cts.Cancel(); _l.Stop(); }
}

public sealed class BridgeFixture : IAsyncLifetime
{
    public TcpSink Sink { get; } = new();
    public SettingsStore Store { get; } = new();
    public PrinterRegistry Registry = default!;
    public HostGate Gate = default!;
    public BridgeServer Server = default!;
    public CertManager Certs { get; } = new();
    public int HttpPort, HttpsPort;
    public HttpClient Http = default!;
    public string PrinterId => $"tcp:127.0.0.1:{Sink.Port}";
    public string Base => $"http://127.0.0.1:{HttpPort}";

    static int FreePort() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }

    public async Task InitializeAsync()
    {
        HttpPort = FreePort(); HttpsPort = FreePort();
        Store.Value.AcceptedHosts.Add("qa.clinic.example"); Store.Value.RouteAllToDefault = false; Store.Value.HttpPort = HttpPort; Store.Value.HttpsPort = HttpsPort;
        Store.Value.ManualPrinters.Add(new ManualPrinter { Name = "Fake Zebra", Host = "127.0.0.1", Port = Sink.Port, Vendor = "Zebra", Language = PrintLanguage.Zpl });
        Certs.Ensure();
        Registry = new PrinterRegistry(Store);
        var jobs = new JobManager(Registry, Store);
        Gate = new HostGate(Store);
        Gate.Prompt = _ => Task.FromResult(HostDecision.Block);
        Server = new BridgeServer(Store, Registry, jobs, Gate, Certs);
        await Registry.RefreshAsync();
        await Server.StartAsync();
        Http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true }) { Timeout = TimeSpan.FromSeconds(10) };
    }

    public Task DisposeAsync() { Sink.Dispose(); return Server.StopAsync(); }

    public HttpRequestMessage Req(HttpMethod m, string path, string? origin = null, string? body = null)
    {
        var r = new HttpRequestMessage(m, Base + path);
        if (origin != null) r.Headers.TryAddWithoutValidation("Origin", origin);
        if (body != null) r.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        return r;
    }
}
