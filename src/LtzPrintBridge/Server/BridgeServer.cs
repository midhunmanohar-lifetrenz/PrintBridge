using System.Net;
using System.Text;
using System.Text.Json;
using LtzPrintBridge.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace LtzPrintBridge.Server;

public sealed class BridgeServer
{
    readonly SettingsStore _store; readonly PrinterRegistry _reg; readonly JobManager _jobs; readonly HostGate _gate; readonly CertManager _certs;
    WebApplication? _app;
    public string? HttpError { get; private set; }
    public string? HttpsError { get; private set; }
    public bool HttpUp { get; private set; }
    public bool HttpsUp { get; private set; }
    public static readonly string Version = typeof(BridgeServer).Assembly.GetName().Version?.ToString(3) ?? "1.0.0"; // from <Version> in the project file

    public BridgeServer(SettingsStore s, PrinterRegistry r, JobManager j, HostGate g, CertManager c) { _store = s; _reg = r; _jobs = j; _gate = g; _certs = c; }

    static bool PortFree(int port)
    {
        try { var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, port); l.Start(); l.Stop(); return true; } catch { return false; }
    }

    static bool PortFree6(int port)
    {
        if (!System.Net.Sockets.Socket.OSSupportsIPv6) return false;
        try { var l = new System.Net.Sockets.TcpListener(IPAddress.IPv6Loopback, port); l.Start(); l.Stop(); return true; } catch { return false; }
    }

    /// <summary>Raised when a port that was busy at start became free and the bridge is now listening on it.</summary>
    public event Action? StateChanged;
    CancellationTokenSource? _retry;

    public async Task StartAsync()
    {
        var s = _store.Value;
        HttpError = HttpsError = null; HttpUp = HttpsUp = false;
        bool http = PortFree(s.HttpPort), https = _certs.ServerCert != null && PortFree(s.HttpsPort);
        if (!http) HttpError = $"Port {s.HttpPort} is in use. Close Zebra Browser Print or change the port.";
        if (_certs.ServerCert == null) HttpsError = "No certificate available.";
        else if (!https) HttpsError = $"Port {s.HttpsPort} is in use. Close Zebra Browser Print or change the port.";
        if (http || https)
        {
            bool http6 = http && PortFree6(s.HttpPort), https6 = https && PortFree6(s.HttpsPort);
            var b = WebApplication.CreateSlimBuilder();
            b.Logging.ClearProviders();
            b.WebHost.ConfigureKestrel(o =>
            {
                o.AddServerHeader = false;
                o.Limits.MaxRequestBodySize = s.MaxPayloadKb * 1024 + 8192;
                if (http) o.Listen(IPAddress.Loopback, s.HttpPort);
                if (https) o.Listen(IPAddress.Loopback, s.HttpsPort, lo => lo.UseHttps(_certs.ServerCert!));
                if (http6) o.Listen(IPAddress.IPv6Loopback, s.HttpPort);
                if (https6) o.Listen(IPAddress.IPv6Loopback, s.HttpsPort, lo => lo.UseHttps(_certs.ServerCert!));
            });
            _app = b.Build();
            _app.Run(Handle);
            try { await _app.StartAsync(); HttpUp = http; HttpsUp = https; Log.Info($"Bridge listening http={http} https={https}"); }
            catch (Exception e)
            {
                Log.Error("Bridge failed to start", e); HttpError = HttpsError = e.Message; _app = null;
            }
        }
        else Log.Warn("Bridge not started: both ports are in use");
        if ((!HttpUp || (!HttpsUp && _certs.ServerCert != null)) && _retry == null) _ = RetryLoop();
    }

    /// <summary>While a port is taken by another program, look again every 20 seconds and start listening as soon as it is free.</summary>
    async Task RetryLoop()
    {
        var cts = _retry = new CancellationTokenSource();
        try
        {
            while (!cts.IsCancellationRequested)
            {
                await Task.Delay(RetryEvery, cts.Token);
                var s = _store.Value;
                bool gain = (!HttpUp && PortFree(s.HttpPort)) || (!HttpsUp && _certs.ServerCert != null && PortFree(s.HttpsPort));
                if (!gain) continue;
                if (_app != null) { try { await _app.StopAsync(); await _app.DisposeAsync(); } catch { } _app = null; }
                _retry = null; await StartAsync(); StateChanged?.Invoke();
                return;
            }
        }
        catch (OperationCanceledException) { }
    }
    public static TimeSpan RetryEvery { get; set; } = TimeSpan.FromSeconds(20);

    public Task StopAsync() { _retry?.Cancel(); return _app?.StopAsync() ?? Task.CompletedTask; }

    static readonly string[] Loop = { "localhost", "127.0.0.1", "[::1]", "::1" };
    public const string LocalProgram = "program on this computer";

    public event Action<RequestEntry>? Requested;

    async Task Handle(HttpContext ctx)
    {
        await HandleCore(ctx);
        var res = ctx.Response; var req = ctx.Request;
        var site = ctx.Items["site"] as string ?? "";
        if (site == "" && res.StatusCode < 400) return; // test and certificate pages are not interesting
        var note = ctx.Items["note"] as string ?? res.StatusCode switch
        {
            >= 200 and < 300 => req.Method == "OPTIONS" ? "Allowed (browser preflight)" : "Allowed",
            403 => "Website not allowed. Add it on the Websites page.",
            421 => "Wrong host name in the request",
            404 => "Unknown address",
            _ => "Failed"
        };
        var e = new RequestEntry { Site = site == "" ? "(none)" : site, Method = req.Method, Path = req.Path.Value ?? "/", Status = res.StatusCode, Result = note };
        if (res.StatusCode >= 400) Log.Warn($"Request {e.Method} {e.Path} from {e.Site}: {res.StatusCode} {note}");
        RaiseRequested(e);
    }

    void RaiseRequested(RequestEntry e) => Requested?.Invoke(e);

    async Task HandleCore(HttpContext ctx)
    {
        var req = ctx.Request; var res = ctx.Response;
        var hostOnly = req.Host.Host;
        if (!Loop.Contains(hostOnly, StringComparer.OrdinalIgnoreCase)) { res.StatusCode = 421; await res.WriteAsync("Misdirected request"); return; }
        var path = (req.Path.Value ?? "/").ToLowerInvariant().TrimEnd('/'); if (path == "") path = "/";

        // Public pages (no printer data).
        if (req.Method == "GET")
        {
            switch (path)
            {
                case "/": case "/test": await Html(res, Pages.Test()); return;
                case "/ssl_support": await Html(res, Pages.Ssl(_certs.IsTrusted())); return;
                case "/ca.cer":
                    if (File.Exists(_certs.CaPath)) { res.ContentType = "application/x-x509-ca-cert"; res.Headers["Content-Disposition"] = "attachment; filename=ltz-printbridge-ca.cer"; await res.Body.WriteAsync(await File.ReadAllBytesAsync(_certs.CaPath)); }
                    else res.StatusCode = 404;
                    return;
            }
        }

        // Everything below is the printer API and goes through the website gate.
        string site;
        var origin = req.Headers.Origin.ToString();
        if (!string.IsNullOrEmpty(origin) && origin != "null")
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var ou)) { res.StatusCode = 400; return; }
            site = ou.Host; ctx.Items["site"] = site;
            // Only web pages may ask. Browser extensions, file:// pages and app schemes have no website name a person could judge.
            if (ou.Scheme is not ("https" or "http")) { res.StatusCode = 403; ctx.Items["note"] = $"Not a website ({ou.Scheme}:// is not accepted)"; await res.WriteAsync("Only http and https websites can use PrintBridge."); return; }
        }
        else
        {
            var sfs = req.Headers["Sec-Fetch-Site"].ToString();
            if (origin == "null" || (sfs != "" && sfs is not ("same-origin" or "none"))) { res.StatusCode = 403; ctx.Items["note"] = "No website name in the request (blocked for safety)"; await res.WriteAsync("Origin required"); return; }
            // No website name and no browser marker: a program on this computer (a script, a desktop app), not a web page.
            site = LocalProgram; ctx.Items["site"] = site;
            if (!_store.Value.AllowLocalPrograms) { res.StatusCode = 403; ctx.Items["note"] = "Programs on this computer are not allowed (Settings)"; await res.WriteAsync("Requests from programs on this computer are switched off in PrintBridge settings."); return; }
        }

        bool allowed;
        try { allowed = site == LocalProgram || await _gate.IsAllowedAsync(site); }
        catch (Exception e) { Log.Error("Website check failed", e); res.StatusCode = 500; return; }
        if (!string.IsNullOrEmpty(origin) && allowed)
        {
            res.Headers["Access-Control-Allow-Origin"] = origin;
            res.Headers["Vary"] = "Origin";
            res.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
            var rh = req.Headers["Access-Control-Request-Headers"].ToString();
            res.Headers["Access-Control-Allow-Headers"] = string.IsNullOrEmpty(rh) ? "Content-Type" : rh;
            res.Headers["Access-Control-Allow-Private-Network"] = "true";
            res.Headers["Access-Control-Max-Age"] = "600";
        }
        if (req.Method == "OPTIONS") { res.StatusCode = allowed ? 204 : 403; return; }
        if (!allowed) { res.StatusCode = 403; await res.WriteAsync("Unauthorized device detected. Allow this website in PrintBridge."); return; }

        try
        {
            switch (req.Method + " " + path)
            {
                case "GET /api/v1/status":
                    await Json(res, new { name = "PrintBridge", version = Version, running = true, printers = _reg.Visible().Count, routeAllToDefault = _store.Value.RouteAllToDefault, defaultPrinterAvailable = !_reg.DefaultMissing, apiVersion = 1 }); return;
                case "GET /api/v1/printers":
                    await Json(res, _reg.Visible().Select(p => new { id = p.Id, name = p.Name, vendor = p.Vendor, language = p.Language.ToString().ToLowerInvariant(), transport = p.Transport.ToString().ToLowerInvariant(), status = p.Status, isDefault = p.IsDefault })); return;
                case "POST /api/v1/print": await ApiPrint(ctx, site); return;
                case "GET /available":
                    await Json(res, new { printer = _reg.Visible().Select(Device).ToArray() }); return;
                case "GET /default":
                    var d = _reg.DefaultPrinter();
                    if (d == null) { res.StatusCode = 200; return; }
                    await Json(res, Device(d)); return;
                case "POST /write": await ZebraWrite(ctx, site); return;
                case "POST /read": await ZebraRead(ctx); return;
                default: res.StatusCode = 404; await res.WriteAsync("Not found"); return;
            }
        }
        catch (BadHttpRequestException e)
        {
            ctx.Items["note"] = e.StatusCode == 413 ? "Job is larger than the size limit" : "Bad request";
            if (!res.HasStarted) { res.StatusCode = e.StatusCode; await Json(res, new { ok = false, error = e.StatusCode == 413 ? "PAYLOAD_TOO_LARGE" : "BAD_REQUEST", message = e.StatusCode == 413 ? $"The job is larger than {_store.Value.MaxPayloadKb} KB." : "The request could not be read." }); }
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or ArgumentException)
        {
            // Broken JSON, bad base64, wrong field types, unknown encoding: the caller's mistake, said plainly.
            ctx.Items["note"] = "Bad request: " + e.Message;
            if (!res.HasStarted) { res.StatusCode = 400; await Json(res, new { ok = false, error = "BAD_REQUEST", message = e is JsonException ? "The request body is not valid JSON." : e.Message }); }
        }
        catch (Exception e)
        {
            Log.Error("Request failed " + path, e);
            if (!res.HasStarted) { res.StatusCode = 500; await Json(res, new { ok = false, error = "INTERNAL_ERROR", message = "PrintBridge could not handle the request. See bridge.log." }); }
        }
    }

    static object Device(PrinterInfo p) => new
    {
        deviceType = "printer", uid = p.Id, provider = "com.lifetrenz.printbridge", name = p.Name,
        connection = p.Transport == TransportKind.Tcp ? "network" : "usb", version = 3, manufacturer = p.Vendor
    };

    async Task<JsonDocument> Body(HttpContext ctx)
    {
        using var ms = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms);
        return JsonDocument.Parse(ms.ToArray());
    }

    async Task ZebraWrite(HttpContext ctx, string site)
    {
        using var doc = await Body(ctx);
        var root = doc.RootElement;
        string? uid = null, name = null;
        if (root.TryGetProperty("device", out var dev) && dev.ValueKind == JsonValueKind.Object)
        {
            if (dev.TryGetProperty("uid", out var u)) uid = u.GetString();
            if (dev.TryGetProperty("name", out var n)) name = n.GetString();
        }
        var p = _reg.Resolve(uid) ?? _reg.Resolve(name);
        if (p == null) { bool gone = _reg.DefaultMissing; ctx.Response.StatusCode = gone ? 503 : 404; await ctx.Response.WriteAsync(gone ? "The default printer is not connected." : "Printer not found"); return; }
        var r = await _jobs.PrintAsync(p, Payload(root, p), site, 1, ctx.RequestAborted);
        if (!r.Ok) { ctx.Response.StatusCode = r.State == "cancelled" ? 409 : r.State == "rejected" ? 400 : 500; await ctx.Response.WriteAsync(r.Message); }
    }

    async Task ZebraRead(HttpContext ctx)
    {
        using var doc = await Body(ctx);
        string? uid = doc.RootElement.TryGetProperty("device", out var dev) && dev.TryGetProperty("uid", out var u) ? u.GetString() : null;
        var p = _reg.Resolve(uid);
        if (p == null) { ctx.Response.StatusCode = 404; return; }
        await ctx.Response.WriteAsync(_jobs.TakeReply(p.Id)); // the answer to the last status question written to this printer
    }

    async Task ApiPrint(HttpContext ctx, string site)
    {
        using var doc = await Body(ctx);
        var root = doc.RootElement;
        string? id = root.TryGetProperty("printerId", out var pi) ? pi.GetString() : root.TryGetProperty("printer", out var pn) ? pn.GetString() : null;
        var p = _reg.Resolve(id);
        if (p == null)
        {
            bool gone = _reg.DefaultMissing && (_store.Value.RouteAllToDefault || string.IsNullOrWhiteSpace(id));
            ctx.Response.StatusCode = gone ? 503 : 404;
            await Json(ctx.Response, gone
                ? new { ok = false, error = "DEFAULT_PRINTER_UNAVAILABLE", message = "The default printer is not connected. Switch it on or choose another default in PrintBridge." }
                : new { ok = false, error = "PRINTER_NOT_FOUND", message = "No printer with that id or name." });
            return;
        }
        int copies = 1;
        if (root.TryGetProperty("copies", out var c) && !(c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out copies))) throw new FormatException("\"copies\" must be a whole number.");
        var r = await _jobs.PrintAsync(p, Payload(root, p), site, copies, ctx.RequestAborted);
        ctx.Response.StatusCode = r.Ok ? 200 : r.State switch { "cancelled" => 409, "rejected" => 400, _ => 502 };
        await Json(ctx.Response, new { ok = r.Ok, printer = p.Name, jobId = r.JobId, state = r.State, message = r.Message, error = r.Ok ? null : r.State switch { "cancelled" => "PRINT_CANCELLED", "rejected" => "BAD_REQUEST", _ => "PRINT_FAILED" } });
    }

    /// <summary>The bytes to print: "base64" as they are, or "data" text in the encoding the request or the printer setting names (UTF-8 otherwise).</summary>
    byte[] Payload(JsonElement root, PrinterInfo p)
    {
        if (root.TryGetProperty("base64", out var b64)) return Convert.FromBase64String(b64.GetString() ?? "");
        string? name = root.TryGetProperty("encoding", out var en) ? en.GetString() : _store.Value.PrinterEncodings.TryGetValue(p.Id, out var pe) ? pe : null;
        var enc = Languages.FindEncoding(name) ?? throw new FormatException($"Unknown text encoding \"{name}\". Examples: utf-8, windows-1252, cp850, cp437.");
        return Languages.Encode(root.TryGetProperty("data", out var d) ? d.GetString() ?? "" : "", p.Language, enc);
    }

    static Task Json(HttpResponse r, object o) { r.ContentType = "application/json"; return r.WriteAsync(JsonSerializer.Serialize(o, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull })); }
    static Task Html(HttpResponse r, string h) { r.ContentType = "text/html; charset=utf-8"; r.Headers["Cache-Control"] = "no-store"; return r.WriteAsync(h); }
}
