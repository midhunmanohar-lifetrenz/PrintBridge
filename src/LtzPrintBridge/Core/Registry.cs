using System.Net.Sockets;
using System.Text;

namespace LtzPrintBridge.Core;

public sealed class PrinterRegistry
{
    readonly SettingsStore _store;
    readonly object _lock = new();
    List<PrinterInfo> _printers = new();
    public event Action? Changed;
    public PrinterRegistry(SettingsStore store) { _store = store; }

    public IReadOnlyList<PrinterInfo> All { get { lock (_lock) return _printers.ToList(); } }

    public async Task RefreshAsync()
    {
        var s = _store.Value;
        var list = new List<PrinterInfo>();
        var def = await Task.Run(Spool.DefaultName);
        foreach (var q in await Task.Run(Spool.List))
        {
            var (vendor, lang) = Languages.Guess(q.Name, q.Driver);
            var id = "spool:" + q.Name;
            if (s.LanguageOverrides.TryGetValue(id, out var o)) lang = o;
            if (q.IsVirtual) continue; // PDF, XPS, fax: not label printers
                        list.Add(new PrinterInfo { Id = id, Name = q.Name, Vendor = vendor, Language = lang, Transport = TransportKind.Spooler, Address = q.Name, Driver = q.Driver, Port = q.Port, Connection = q.Connection, Status = q.Status, StatusLevel = q.Level, IsDefault = q.Name == def });
        }
        var checks = new List<Task>();
        foreach (var m in s.ManualPrinters)
        {
            var id = $"tcp:{m.Host}:{m.Port}";
            var p = new PrinterInfo { Id = id, Name = m.Name, Vendor = m.Vendor, Language = s.LanguageOverrides.TryGetValue(id, out var o) ? o : m.Language, Transport = TransportKind.Tcp, Driver = "Direct network (no driver)", Address = $"{m.Host}:{m.Port}", Port = $"{m.Host}:{m.Port}", IsManual = true, Status = "Checking", StatusLevel = "warn" };
            list.Add(p);
            checks.Add(Task.Run(async () => { var ok = await Reachable(m.Host, m.Port); p.Status = ok ? "Reachable" : "Offline"; p.StatusLevel = ok ? "ok" : "bad"; }));
        }
        await Task.WhenAll(checks);
        var defId = s.DefaultPrinterId;
        if (defId == null && Policy.DefaultPrinterName is { } pn) defId = list.FirstOrDefault(p => string.Equals(p.Name, pn, StringComparison.OrdinalIgnoreCase))?.Id;
        // A default the person chose is never silently replaced: while it is missing (unplugged, spooler slow) nothing is the default.
        bool missing = defId != null && !list.Any(p => p.Id == defId);
        if (missing) foreach (var p in list) p.IsDefault = false;
        else if (defId != null) foreach (var p in list) p.IsDefault = p.Id == defId;
        else if (!list.Any(p => p.IsDefault) && list.Count > 0) list[0].IsDefault = true;
        lock (_lock) { _printers = list; DefaultMissing = missing; }
        Changed?.Invoke();
    }

    static async Task<bool> Reachable(string host, int port)
    {
        try
        {
            using var c = new TcpClient(); using var cts = new CancellationTokenSource(1500);
            await c.ConnectAsync(host, port, cts.Token); return true;
        }
        catch { return false; }
    }

    /// <summary>True while the default printer the person chose is not in the list. Jobs are refused instead of going to another printer.</summary>
    public bool DefaultMissing { get; private set; }

    public PrinterInfo? DefaultPrinter() { var all = All; return all.FirstOrDefault(p => p.IsDefault) ?? (DefaultMissing ? null : all.FirstOrDefault()); }

    /// <summary>The printers a website is allowed to see: just the default one when all requests are routed to it.</summary>
    public IReadOnlyList<PrinterInfo> Visible() { if (_store.Value.RouteAllToDefault) { var d = DefaultPrinter(); return d == null ? Array.Empty<PrinterInfo>() : new[] { d }; } return All; }

    /// <summary>Picks the printer for a request. With routing on, the website's choice is ignored and the default printer is used.</summary>
    public PrinterInfo? Resolve(string? requested) => _store.Value.RouteAllToDefault ? DefaultPrinter() : Find(requested);

    public PrinterInfo? Find(string? idOrName)
    {
        var all = All;
        if (string.IsNullOrWhiteSpace(idOrName)) return DefaultPrinter();
        return all.FirstOrDefault(p => p.Id == idOrName) ?? all.FirstOrDefault(p => string.Equals(p.Name, idOrName, StringComparison.OrdinalIgnoreCase));
    }

    public void SetDefault(string id) { _store.Value.DefaultPrinterId = id; _store.Save(); lock (_lock) DefaultMissing = false; foreach (var p in All) p.IsDefault = p.Id == id; Changed?.Invoke(); }
    public void SetLanguage(PrinterInfo p, PrintLanguage l) { p.Language = l; _store.Value.LanguageOverrides[p.Id] = l; _store.Save(); Changed?.Invoke(); }

    public void AddManual(ManualPrinter m)
    {
        _store.Value.ManualPrinters.RemoveAll(x => x.Host == m.Host && x.Port == m.Port);
        _store.Value.ManualPrinters.Add(m); _store.Save();
    }
    public void RemoveManual(PrinterInfo p)
    {
        _store.Value.ManualPrinters.RemoveAll(x => $"tcp:{x.Host}:{x.Port}" == p.Id); _store.Save();
    }
}

public sealed class JobManager
{
    readonly PrinterRegistry _reg; readonly SettingsStore _store;
    readonly Dictionary<string, SemaphoreSlim> _locks = new();
    public event Action<JobRecord>? JobDone;
    public Func<string, (string text, string level)?> StatusProvider { get; set; } = Spool.QueueStatus;
    public JobManager(PrinterRegistry reg, SettingsStore store) { _reg = reg; _store = store; }

    public sealed record Result(bool Ok, string JobId, string Message, string State, bool Confirmed = false);

    /// <summary>
    /// How far a job got after it was handed over. State: submitted (handed over, nothing more known), printing,
    /// printed, attention (the printer or queue reports a problem), failed, cancelled, rejected.
    /// Confirmed is true once the printer itself (or, for driver printers, the system queue) said so.
    /// </summary>
    public sealed class JobStatus
    {
        public string JobId { get; init; } = ""; public string Printer { get; init; } = ""; public DateTime Time { get; init; } = DateTime.Now;
        public string State { get; set; } = "submitted"; public string Message { get; set; } = ""; public bool Confirmed { get; set; }
        public string Source { get; set; } = ""; // printer | system | none
        internal Task Done = Task.CompletedTask;
    }

    readonly Dictionary<string, JobStatus> _status = new(); readonly Queue<string> _statusOrder = new();
    const int KeepStatus = 300;
    public JobStatus? Status(string jobId) { lock (_status) return _status.TryGetValue(jobId, out var s) ? s : null; }
    JobStatus Remember(JobStatus s)
    {
        lock (_status) { _status[s.JobId] = s; _statusOrder.Enqueue(s.JobId); while (_statusOrder.Count > KeepStatus) _status.Remove(_statusOrder.Dequeue()); }
        return s;
    }

    /// <summary>Reads the printer's state. Replaceable in tests.</summary>
    public Func<PrinterInfo, Task<PrinterState>>? StateReader { get; set; }
    /// <summary>Pauses between the checks that follow a job until it is printed (about 20 seconds in total).</summary>
    public static int[] DefaultConfirmDelaysMs { get; set; } = { 400, 600, 1000, 1500, 2000, 2500, 3000, 3000, 3000, 3000 };
    public int[] ConfirmDelaysMs { get; set; } = DefaultConfirmDelaysMs;

    /// <summary>The printer's own answer when it can give one, otherwise the system queue or network state. Waits its turn behind a job that is being sent.</summary>
    public async Task<PrinterState> ReadStateAsync(PrinterInfo p)
    {
        if (StateReader != null) return await StateReader(p);
        var sem = LockFor(p);
        if (!await sem.WaitAsync(3000)) return new PrinterState(true, "busy", "A print job is being sent or is waiting in the preview window.", "none", Array.Empty<string>(), true);
        try { return await PrinterStatus.ReadAsync(p, StatusProvider); }
        finally { sem.Release(); }
    }

    SemaphoreSlim LockFor(PrinterInfo p) { lock (_locks) { if (!_locks.TryGetValue(p.Id, out var sem)) _locks[p.Id] = sem = new(1, 1); return sem; } }

    /// <summary>Follows a job after it was handed over, until the printer or the system queue says it is done, or time runs out.</summary>
    async Task Confirm(JobStatus js, PrinterInfo p, string? queueJob)
    {
        try
        {
            if (!_store.Value.FollowJobs || ConfirmDelaysMs.Length == 0) return;
            if (p.Transport == TransportKind.Tcp && p.Language is not (PrintLanguage.Zpl or PrintLanguage.Tspl)) { js.Message = "Sent. This printer cannot report when a label is finished."; return; }
            foreach (var wait in ConfirmDelaysMs)
            {
                await Task.Delay(wait);
                if (p.Transport == TransportKind.Spooler)
                {
                    if (queueJob == null || Spool.JobState(p.Address, queueJob) is not { } q) { js.Message = "Handed to the system print queue. It cannot report more."; return; }
                    js.Source = "system"; js.State = q.state; js.Message = q.message; js.Confirmed = q.state == "printed";
                    if (js.Confirmed) return;
                }
                else
                {
                    var st = await ReadStateAsync(p);
                    if (st.Source != "printer") { js.Message = st.State == "offline" ? st.Message : "Sent. This printer cannot report when a label is finished."; if (st.State == "offline") js.State = "attention"; return; }
                    js.Source = "printer";
                    if (st.Issues.Count > 0) { js.State = "attention"; js.Message = st.Message; }
                    else if (st.Busy) { js.State = "printing"; js.Message = "Printing"; }
                    else { js.State = "printed"; js.Message = "The printer finished the job."; js.Confirmed = true; return; }
                }
            }
        }
        catch (Exception e) { Log.Warn("Could not follow job " + js.JobId + ": " + e.Message); }
    }

    /// <summary>What the preview window is asked to show. Labels holds each ZPL label (empty for other languages); Text is the full job.</summary>
    public sealed record PrintReview(string Site, string Printer, string Language, string Text, IReadOnlyList<string> Labels, int Copies, bool PerLabel, PrintLanguage? Kind = null, CancellationToken Aborted = default);
    public sealed record ReviewDecision(bool Print, int Copies, bool SkipRest);
    sealed class CancelledException : Exception { public CancelledException(string m = "Printing was cancelled in the preview window.") : base(m) { } }
    /// <summary>Most times a whole job is sent again when the copies cannot be written into the label itself.</summary>
    public const int MaxRepeats = 100;

    /// <summary>Set by the app. Used only while "Preview before printing" is on.</summary>
    public Func<PrintReview, Task<ReviewDecision>>? Review { get; set; }
    readonly Dictionary<string, DateTime> _skipUntil = new(StringComparer.OrdinalIgnoreCase);
    static readonly TimeSpan SkipFor = TimeSpan.FromMinutes(2);
    static readonly string[] OwnSites = { "PrintBridge app" };

    /// <summary>Returns the bytes to send and how many times to send them, after asking the person when preview is on.</summary>
    async Task<(byte[] data, int times)> ReviewAsync(PrinterInfo p, byte[] data, string site, int times, CancellationToken aborted)
    {
        var s = _store.Value;
        if (Review == null || OwnSites.Contains(site)) return (data, times);
        var wanted = s.PreviewMode switch { PreviewMode.All => true, PreviewMode.Selected => HostGate.Match(s.PreviewSites, site.ToLowerInvariant()), _ => false };
        if (!wanted) return (data, times);
        lock (_skipUntil) if (_skipUntil.TryGetValue(site, out var until) && until > DateTime.Now) return (data, times);

        string? text = null;
        try { text = new System.Text.UTF8Encoding(false, true).GetString(data); } catch { } // binary data (graphics) cannot be shown or rewritten
        var kind = text == null ? null : LabelPreview.Detect(text);
        var labels = kind is { } k0 ? LabelPreview.Split(k0, text!) : new List<string>();
        bool perLabel = false; int initial = times;
        if (labels.Count > 0)
        {
            var q = labels.Select(l => LabelPreview.Quantity(kind!.Value, l)).Distinct().ToList();
            if (q.Count == 1 && (long)q[0] * times <= LabelPreview.MaxQuantity) { perLabel = true; initial = q[0] * times; }
        }
        var d = await Review(new PrintReview(site, p.Name, p.LanguageText, text ?? "(binary data)", labels, initial, perLabel, kind, aborted));
        if (!d.Print) throw new CancelledException();
        if (aborted.IsCancellationRequested) throw new CancelledException("The website stopped waiting for an answer, so nothing was printed.");
        if (d.SkipRest) lock (_skipUntil) _skipUntil[site] = DateTime.Now + SkipFor;
        if (d.Copies == initial) return (data, times); // nothing changed: the job goes out exactly as the website sent it
        if (perLabel) return (System.Text.Encoding.UTF8.GetBytes(LabelPreview.WithQuantity(kind!.Value, text!, Math.Clamp(d.Copies, 1, LabelPreview.MaxQuantity))), 1);
        return (data, Math.Clamp(d.Copies, 1, MaxRepeats));
    }

    /// <param name="waitMs">How long to wait for the printer to finish before answering (0 = answer as soon as the job is handed over).</param>
    public async Task<Result> PrintAsync(PrinterInfo p, byte[] data, string site, int copies = 1, CancellationToken aborted = default, int waitMs = 0)
    {
        var id = "job_" + Guid.NewGuid().ToString("N")[..8];
        var rec = new JobRecord { Id = id, Site = site, Printer = p.Name, Language = p.LanguageText, Bytes = data.Length };
        var sem = LockFor(p); var js = Remember(new JobStatus { JobId = id, Printer = p.Name });
        JobStatus End(string state, string message) { js.State = state; js.Message = message; return js; }
        await sem.WaitAsync();
        string? queueJob = null; bool sentAll = false;
        int sent = 0, times = 1;
        try
        {
            if (data.Length == 0) throw new ArgumentException("Nothing to print.");
            if (data.Length > _store.Value.MaxPayloadKb * 1024) throw new ArgumentException("Payload is too large.");
            if (p.Transport == TransportKind.Spooler && StatusProvider(p.Address) is { level: "bad" } bad)
                throw new IOException($"The printer is {bad.text.ToLowerInvariant()}. Check power, USB cable and paper, then print again.");
            if (copies < 1 || copies > MaxRepeats) throw new ArgumentException($"Copies must be between 1 and {MaxRepeats}.");
            (data, times) = await ReviewAsync(p, data, site, copies, aborted);
            rec.Bytes = data.Length;
            for (; sent < times; sent++)
            {
                if (p.Transport == TransportKind.Tcp) { var reply = await SendTcp(p.Address, data, WantsReply(data)); if (reply != null) lock (_replies) _replies[p.Id] = reply; }
                else queueJob = await Task.Run(() => Spool.WriteRaw(p.Address, data, "PrintBridge " + id));
            }
            rec.Ok = true; rec.Result = "Sent"; sentAll = true;
            End("submitted", "Submitted to printer");
        }
        catch (CancelledException e)
        {
            rec.Ok = false; rec.Result = "Cancelled"; End("cancelled", e.Message);
            return new Result(false, id, e.Message, "cancelled");
        }
        catch (Exception e)
        {
            var msg = sent > 0 ? $"{sent} of {times} copies were sent, then: {e.Message}" : e.Message;
            rec.Ok = false; rec.Result = "Failed: " + msg;
            Log.Error($"Print failed on {p.Name}", e);
            End(e is ArgumentException ? "rejected" : "failed", msg);
            return new Result(false, id, msg, js.State);
        }
        finally
        {
            sem.Release();
            try { File.AppendAllText(AppPaths.Jobs, System.Text.Json.JsonSerializer.Serialize(rec) + Environment.NewLine); } catch { }
            JobDone?.Invoke(rec);
        }
        // The job is with the printer or the system queue. Follow it in the background; the caller may wait for the outcome.
        if (!sentAll) return new Result(false, id, js.Message, js.State);
        js.Done = Confirm(js, p, queueJob);
        if (waitMs > 0) await Task.WhenAny(js.Done, Task.Delay(waitMs));
        return new Result(true, id, js.Message, js.State, js.Confirmed);
    }

    readonly Dictionary<string, string> _replies = new();

    /// <summary>The printer's answer to the last status question sent to it (Zebra-style read). Empty when there is none. Network printers only.</summary>
    public string TakeReply(string printerId) { lock (_replies) return _replies.Remove(printerId, out var r) ? r : ""; }
    /// <summary>Stores an answer for the next read (used when PrintBridge answers a status question on the printer's behalf).</summary>
    public void SetReply(string printerId, string reply) { lock (_replies) _replies[printerId] = reply; }

    // Commands that make a printer answer: ZPL ~HS ~HI ~HQ ~HM ~HD ^HH, Zebra settings (! U1 getvar, {}{...} JSON), TSPL <ESC>!? and <ESC>!S
    static readonly System.Text.RegularExpressions.Regex AskRx = new(@"~H[SIQMD]|\^HH|\x1B![?S]|!\s*U1\s+getvar|\{\}\s*\{", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
    static bool WantsReply(byte[] data) => data.Length <= 4096 && AskRx.IsMatch(Encoding.Latin1.GetString(data));

    /// <summary>True when the data only asks the printer something (status, settings) and prints nothing, so it needs no preview and is not a job.</summary>
    public static bool IsQuestion(byte[] data)
    {
        if (data.Length > 300) return false;
        var t = Encoding.Latin1.GetString(data);
        return AskRx.IsMatch(t) && !t.Contains("^FD", StringComparison.OrdinalIgnoreCase) && !t.Contains("^GF", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Puts a question to a network printer, in turn with its jobs, and keeps the answer for the next read.</summary>
    public async Task AskAsync(PrinterInfo p, byte[] question)
    {
        var sem = LockFor(p);
        if (!await sem.WaitAsync(3000)) return;
        try { SetReply(p.Id, Encoding.UTF8.GetString(await PrinterStatus.AskAsync(p.Address, question))); }
        catch (Exception e) when (e is OperationCanceledException or SocketException or IOException) { SetReply(p.Id, ""); }
        finally { sem.Release(); }
    }

    /// <summary>Sends the job to a network printer. Connecting and sending have separate time limits. Returns the printer's answer when one was asked for.</summary>
    public static async Task<string?> SendTcp(string address, byte[] data, bool readReply = false)
    {
        var i = address.LastIndexOf(':');
        var host = address[..i]; var port = int.Parse(address[(i + 1)..]);
        using var c = new TcpClient { LingerState = new LingerOption(true, 5) }; // closing waits until the printer has taken the data
        using (var connect = new CancellationTokenSource(4000))
        {
            try { await c.ConnectAsync(host, port, connect.Token); }
            catch (OperationCanceledException) { throw new IOException($"The printer at {address} did not answer within 4 seconds."); }
        }
        using var write = new CancellationTokenSource(10000 + data.Length / 50);
        var ns = c.GetStream();
        try { await ns.WriteAsync(data, write.Token); await ns.FlushAsync(write.Token); }
        catch (OperationCanceledException) { throw new IOException($"The printer at {address} stopped accepting data."); }
        if (!readReply) return null;
        var buf = new byte[4096]; var sb = new StringBuilder();
        using var read = new CancellationTokenSource(1500);
        try { int n; while ((n = await ns.ReadAsync(buf, read.Token)) > 0) { sb.Append(Encoding.UTF8.GetString(buf, 0, n)); read.CancelAfter(250); } } catch (OperationCanceledException) { } catch (IOException) { }
        return sb.ToString();
    }
}
