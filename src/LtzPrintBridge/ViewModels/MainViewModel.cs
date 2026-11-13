using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using LtzPrintBridge.Core;
using LtzPrintBridge.Server;

namespace LtzPrintBridge.ViewModels;

/// <summary>One allowed website in the Websites list, with its own "show a preview" tick.</summary>
public sealed class HostRow : Obs
{
    readonly Action<string, bool> _save; bool _preview;
    public HostRow(string host, bool preview, Action<string, bool> save) { Host = host; _preview = preview; _save = save; }
    public string Host { get; }
    public bool Preview { get => _preview; set { if (Set(ref _preview, value)) _save(Host, value); } }
    public override string ToString() => Host;
}

public abstract class Obs : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T f, T v, [CallerMemberName] string? n = null)
    { if (EqualityComparer<T>.Default.Equals(f, v)) return false; f = v; PropertyChanged?.Invoke(this, new(n)); return true; }
    protected void Raise(params string[] names) { foreach (var n in names) PropertyChanged?.Invoke(this, new(n)); }
}

public sealed class DiagItem
{
    public string Title { get; set; } = ""; public string Detail { get; set; } = ""; public string Level { get; set; } = "ok";
    public string LevelText => Level switch { "ok" => "Pass", "warn" => "Check", _ => "Fail" };
}

public sealed class MainViewModel : Obs
{
    public SettingsStore Store { get; } = new();
    public PrinterRegistry Registry { get; }
    public JobManager Jobs { get; }
    public HostGate Gate { get; }
    public CertManager Certs { get; } = new();
    public BridgeServer Server { get; }
    public bool Quitting { get; private set; }
    public event Action? QuitRequested;

    public ObservableCollection<PrinterInfo> Printers { get; } = new();
    public ObservableCollection<HostRow> Allowed { get; } = new();
    public ObservableCollection<string> Blocked { get; } = new();
    public ObservableCollection<JobRecord> JobList { get; } = new();
    public ObservableCollection<DiagItem> Diag { get; } = new();
    public ObservableCollection<RequestEntry> Requests { get; } = new();
    public bool NoRequests => Requests.Count == 0;
    public IReadOnlyList<PrintLanguage> LanguageChoices => Languages.Choices;
    public bool AllowLocalPrograms { get => Store.Value.AllowLocalPrograms; set { Store.Value.AllowLocalPrograms = value; Store.Save(); Raise(nameof(AllowLocalPrograms)); } }

    int _page; public int Page { get => _page; set { if (Set(ref _page, value)) Raise(nameof(IsPrinters), nameof(IsHosts), nameof(IsJobs), nameof(IsDiag), nameof(IsSettings)); } }
    public bool IsPrinters => Page == 0; public bool IsHosts => Page == 1; public bool IsJobs => Page == 2; public bool IsDiag => Page == 3; public bool IsSettings => Page == 4;

    PrinterInfo? _sel;
    public PrinterInfo? SelectedPrinter
    {
        get => _sel;
        set { if (Set(ref _sel, value)) { Raise(nameof(HasSelection), nameof(SelectedLanguage), nameof(SelectedIsManual)); RawText = value == null ? "" : Languages.Sample(value.Language); SelectionChanged?.Invoke(); } }
    }
    public event Action? SelectionChanged;
    public bool HasSelection => _sel != null;
    public bool SelectedIsManual => _sel?.IsManual == true;
    public PrintLanguage SelectedLanguage
    {
        get => _sel?.Language ?? PrintLanguage.Auto;
        set { if (_sel != null && value != _sel.Language && value != PrintLanguage.Auto) { Registry.SetLanguage(_sel, value); RawText = Languages.Sample(value); Raise(nameof(SelectedLanguage)); } }
    }

    string _raw = ""; public string RawText { get => _raw; set => Set(ref _raw, value); }
    string _newHost = ""; public string NewHost { get => _newHost; set => Set(ref _newHost, value); }
    string _toast = ""; public string ToastText { get => _toast; set { Set(ref _toast, value); Raise(nameof(HasToast)); } }
    public bool HasToast => !string.IsNullOrEmpty(_toast);
    string _summary = "Starting..."; public string Summary { get => _summary; set => Set(ref _summary, value); }
    string _bridgeText = "Starting"; public string BridgeText { get => _bridgeText; set => Set(ref _bridgeText, value); }
    string _bridgeLevel = "warn"; public string BridgeLevel { get => _bridgeLevel; set { if (Set(ref _bridgeLevel, value)) Raise(nameof(BridgeColor)); } }
    public string BridgeColor => _bridgeLevel switch { "ok" => "#52C27A", "bad" => "#F08075", _ => "#E5A53F" };
    public bool NoBlocked => Blocked.Count == 0;
    public string DataFolder => AppPaths.Dir;
    public string VersionText => "v" + BridgeServer.Version;
    public string PortsText => $"HTTP {Store.Value.HttpPort}  ·  HTTPS {Store.Value.HttpsPort}";

    public string AutostartText => OperatingSystem.IsMacOS() ? "Start when I log in to this Mac" : "Start when I sign in to Windows";
    public string AutostartHint => OperatingSystem.IsMacOS() ? "Runs quietly in the menu bar." : "Runs quietly in the system tray.";
    public bool StartWithWindows { get => Store.Value.StartWithWindows; set { Store.Value.StartWithWindows = value; Store.Save(); ApplyAutostart(); Raise(nameof(StartWithWindows)); } }
    public PrinterInfo? DefaultPrinter
    {
        get => Printers.FirstOrDefault(p => p.IsDefault);
        set { if (value != null && !value.IsDefault) { Registry.SetDefault(value.Id); Toast(value.Name + " is now the default printer"); Raise(nameof(DefaultPrinter)); } }
    }
public static string[] PreviewModes { get; } = { "Off", "All websites", "Only chosen websites" };
    public int PreviewModeIndex
    {
        get => (int)Store.Value.PreviewMode;
        set
        {
            if (value < 0 || value > 2 || value == (int)Store.Value.PreviewMode) return;
            Store.Value.PreviewMode = (PreviewMode)value; Store.Save(); Raise(nameof(PreviewModeIndex), nameof(PreviewSelected));
            Toast(value == 0 ? "Prints go straight to the printer" : value == 1 ? "A preview will be shown for every website" : "Tick Preview next to the websites that should show one");
        }
    }
    public bool PreviewSelected => Store.Value.PreviewMode == PreviewMode.Selected;
    public void SetPreviewSite(string host, bool on)
    {
        var l = Store.Value.PreviewSites; l.RemoveAll(h => string.Equals(h, host, StringComparison.OrdinalIgnoreCase));
        if (on) l.Add(host); Store.Save();
    }
    public bool RouteAllToDefault { get => Store.Value.RouteAllToDefault; set { Store.Value.RouteAllToDefault = value; Store.Save(); Raise(nameof(RouteAllToDefault)); Toast(value ? "All print requests now go to the default printer" : "Websites can choose any printer"); } }

    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(4) };
    DispatcherTimer? _timer;

    public MainViewModel()
    {
        Store.Load();
        Policy.Apply(Store);
        Registry = new PrinterRegistry(Store);
        Jobs = new JobManager(Registry, Store);
        Gate = new HostGate(Store);
        Server = new BridgeServer(Store, Registry, Jobs, Gate, Certs);
        Registry.Changed += () => Dispatcher.UIThread.Post(SyncPrinters);
        Gate.Changed += () => Dispatcher.UIThread.Post(SyncHosts);
        Jobs.JobDone += r => Dispatcher.UIThread.Post(() => { JobList.Insert(0, r); while (JobList.Count > 500) JobList.RemoveAt(JobList.Count - 1); });
        Server.Requested += e => Dispatcher.UIThread.Post(() => { Requests.Insert(0, e); while (Requests.Count > 100) Requests.RemoveAt(Requests.Count - 1); Raise(nameof(NoRequests)); });
        LoadJobHistory(); SyncHosts();
    }

    void LoadJobHistory()
    {
        try
        {
            if (!File.Exists(AppPaths.Jobs)) return;
            if (new FileInfo(AppPaths.Jobs).Length > 1_000_000) File.WriteAllLines(AppPaths.Jobs, File.ReadLines(AppPaths.Jobs).TakeLast(1000));
            foreach (var line in File.ReadLines(AppPaths.Jobs).TakeLast(200).Reverse())
            { var j = System.Text.Json.JsonSerializer.Deserialize<JobRecord>(line); if (j != null) JobList.Add(j); }
        }
        catch { }
    }

    public async Task InitAsync()
    {
        await Task.Run(Certs.Ensure);
        await Registry.RefreshAsync();
        await Server.StartAsync();
        UpdateBridge();
        ApplyAutostart();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _timer.Tick += async (_, _) => await Registry.RefreshAsync();
        _timer.Start();
        Server.StateChanged += () => Dispatcher.UIThread.Post(async () => { UpdateBridge(); await RunDiagnostics(); });
        // A renewed certificate (about once a year) has to be trusted again, so ask even if we asked before.
        if (!Certs.IsTrusted() && (!Store.Value.CertificateTrustAsked || Certs.Regenerated))
        {
            Store.Value.CertificateTrustAsked = true; Store.Save();
            Toast(OperatingSystem.IsMacOS() ? "Installing the secure certificate. Enter your Mac password when asked." : "Installing the secure certificate. Click Yes in the Windows dialog.", 6000);
            await Task.Delay(1200);
            var (ok, msg) = await Task.Run(Certs.InstallTrust);
            Toast(msg, 4000);
        }
        await RunDiagnostics();
    }

    void UpdateBridge()
    {
        if (Server.HttpUp || Server.HttpsUp) { BridgeText = "Running"; BridgeLevel = "ok"; }
        else { BridgeText = "Not running"; BridgeLevel = "bad"; }
        Raise(nameof(PortsText));
    }

    string _sig = "\0";
    void SyncPrinters()
    {
        var all = Registry.All;
        var sig = string.Join("|", all.Select(p => $"{p.Id}:{p.Status}:{p.Language}:{p.IsDefault}:{p.Connection}"));
        if (sig == _sig) return; // nothing changed: keep the screen and any command being typed untouched
        _sig = sig;
        var keep = SelectedPrinter?.Id; var keepRaw = RawText;
        Printers.Clear();
        foreach (var p in all) Printers.Add(p);
        SelectedPrinter = Printers.FirstOrDefault(p => p.Id == keep) ?? Printers.FirstOrDefault(p => p.IsDefault) ?? Printers.FirstOrDefault();
        if (keep != null && SelectedPrinter?.Id == keep) RawText = keepRaw;
        Raise(nameof(DefaultPrinter));
        int ready = Printers.Count(p => p.StatusLevel == "ok");
        Summary = Printers.Count == 0 ? "No printers found yet" : $"{ready} of {Printers.Count} printers ready";
    }

    void SyncHosts()
    {
        Allowed.Clear(); foreach (var h in Store.Value.AcceptedHosts) Allowed.Add(new HostRow(h, Store.Value.PreviewSites.Any(x => string.Equals(x, h, StringComparison.OrdinalIgnoreCase)), SetPreviewSite));
        Blocked.Clear(); foreach (var h in Store.Value.BlockedHosts) Blocked.Add(h);
        Raise(nameof(NoBlocked));
    }

    public void Toast(string t, int ms = 2800)
    {
        Dispatcher.UIThread.Post(async () => { ToastText = t; await Task.Delay(ms); if (ToastText == t) ToastText = ""; });
    }

    public async Task Rescan() { await Registry.RefreshAsync(); Toast("Scan complete"); }

    public async Task PrintTest()
    {
        if (SelectedPrinter == null) return;
        var p = SelectedPrinter;
        if (p.Language == PrintLanguage.Auto) { Toast("Choose the printer language first."); return; }
        await SendRaw(Languages.Sample(p.Language), "Test label");
    }

    public async Task SendRaw(string text, string what = "Command")
    {
        if (SelectedPrinter == null) return;
        var p = SelectedPrinter;
        var r = await Jobs.PrintAsync(p, Languages.Encode(text, p.Language), "PrintBridge app");
        Toast(r.Ok ? $"{what} sent to {p.Name}" : $"Failed: {r.Message}", r.Ok ? 2800 : 6000);
    }

    public void MakeDefault() { if (SelectedPrinter != null) { Registry.SetDefault(SelectedPrinter.Id); Toast(SelectedPrinter.Name + " is now the default"); } }
    public async Task RemoveSelected() { if (SelectedPrinter is { IsManual: true } p) { Registry.RemoveManual(p); await Registry.RefreshAsync(); } }
    public async Task AddManual(ManualPrinter m) { Registry.AddManual(m); await Registry.RefreshAsync(); Toast(m.Name + " added"); }

    string _resetAsk = ""; public string ResetAsk { get => _resetAsk; set { Set(ref _resetAsk, value); Raise(nameof(ResetPermissionsText), nameof(ResetAllText)); } }
    public string ResetPermissionsText => ResetAsk == "perm" ? "Click again to confirm" : "Reset website permissions";
    public string ResetAllText => ResetAsk == "all" ? "Click again to confirm" : "Reset all settings";

    /// <summary>First click asks, second click within a few seconds does it.</summary>
    async Task<bool> Confirm(string key)
    {
        if (ResetAsk == key) { ResetAsk = ""; return true; }
        ResetAsk = key; await Task.Delay(4000); if (ResetAsk == key) ResetAsk = "";
        return false;
    }

    public async Task ResetPermissions()
    {
        if (!await Confirm("perm")) return;
        Gate.ResetAll(); Toast("Website permissions cleared. Each website will ask again.", 4000);
    }

    public async Task ResetAllSettings()
    {
        if (!await Confirm("all")) return;
        var s = Store.Value;
        s.PreviewMode = PreviewMode.Off; s.PreviewSites.Clear(); Raise(nameof(PreviewModeIndex), nameof(PreviewSelected));
        s.ManualPrinters.Clear(); s.LanguageOverrides.Clear(); s.DefaultPrinterId = null; s.RouteAllToDefault = true;
        Gate.ResetAll();
        await Registry.RefreshAsync(); SyncPrinters();
        Raise(nameof(RouteAllToDefault), nameof(DefaultPrinter));
        Toast("All settings were reset. The certificate was kept.", 4000);
    }

    public void AllowHost() { var h = CleanHost(NewHost); if (h.Length == 0) return; Gate.Accept(h); NewHost = ""; }

    /// <summary>Turns whatever was typed or pasted (a full address with https://, port, path) into the bare website name the allow-list uses.</summary>
    public static string CleanHost(string typed)
    {
        var h = typed.Trim().ToLowerInvariant();
        var i = h.IndexOf("://", StringComparison.Ordinal); if (i >= 0) h = h[(i + 3)..];
        i = h.IndexOfAny(new[] { '/', '?', '#' }); if (i >= 0) h = h[..i];
        i = h.LastIndexOf('@'); if (i >= 0) h = h[(i + 1)..];
        if (!h.StartsWith('[')) { i = h.LastIndexOf(':'); if (i >= 0) h = h[..i]; }
        return h.Trim('.', ' ');
    }

    public async Task RunDiagnostics()
    {
        var list = new List<DiagItem>();
        var s = Store.Value;
        list.Add(Server.HttpUp ? new() { Title = "Bridge (HTTP)", Detail = $"Listening on 127.0.0.1:{s.HttpPort}. Not reachable from other computers." } : new() { Title = "Bridge (HTTP)", Detail = Server.HttpError ?? "Not running.", Level = "bad" });
        list.Add(Server.HttpsUp ? new() { Title = "Bridge (HTTPS)", Detail = $"Listening on 127.0.0.1:{s.HttpsPort}." } : new() { Title = "Bridge (HTTPS)", Detail = Server.HttpsError ?? "Not running.", Level = "bad" });
        bool store = Certs.IsTrusted(); string tlsDetail; string tlsLevel = "ok";
        if (Server.HttpsUp)
        {
            try { using var r = await _http.GetAsync($"https://localhost:{s.HttpsPort}/api/v1/status"); tlsDetail = "Your computer trusts the local certificate. Chrome and Edge connect without warnings."; }
            catch { tlsLevel = store ? "warn" : "bad"; tlsDetail = store ? "Certificate is installed but the secure test failed. Restart the app." : "Certificate is not trusted yet. Press Install certificate."; }
        }
        else { tlsLevel = "bad"; tlsDetail = "HTTPS is not running."; }
        list.Add(new() { Title = "Secure connection", Detail = tlsDetail, Level = tlsLevel });
        var q = Spool.List().Count;
        list.Add(new() { Title = OperatingSystem.IsMacOS() ? "Mac printers" : "Windows printers", Detail = $"{q} printer queue(s) found in {Spool.QueueName}." });
        var dp = Registry.DefaultPrinter();
        if (dp == null && Registry.DefaultMissing) list.Add(new() { Title = "Default printer", Detail = "The default printer you chose is not connected. Print requests are refused until it is back or you choose another one.", Level = "bad" });
        else if (dp == null) list.Add(new() { Title = "Default printer", Detail = "No printer is set. Connect a printer and choose it on the Printers page.", Level = "warn" });
        else list.Add(new() { Title = "Default printer", Detail = $"{dp.Name} ({dp.TransportText}, {dp.LanguageText}). " + (s.RouteAllToDefault ? "Every print request goes to this printer." : "Used when a website does not choose a printer."), Level = dp.StatusLevel == "ok" ? "ok" : dp.StatusLevel });
        foreach (var p in Registry.All.Where(p => p.StatusLevel != "ok" && p.Id != dp?.Id)) list.Add(new() { Title = p.Name, Detail = p.Status + ". Check power, cable and paper.", Level = p.StatusLevel });
        if (Registry.All.Count == 0) list.Add(new() { Title = "Printers", Detail = $"No printers found. Install the printer in {Spool.SystemName} or add a network printer.", Level = "warn" });
        Dispatcher.UIThread.Post(() => { Diag.Clear(); foreach (var d in list) Diag.Add(d); });
    }

    public async Task InstallCertificate()
    {
        Toast(OperatingSystem.IsMacOS() ? "Enter your Mac password to trust the certificate." : "Click Yes in the Windows dialog to trust the certificate.", 5000);
        var (ok, msg) = await Task.Run(Certs.InstallTrust);
        Toast(msg, 4000); await RunDiagnostics();
    }

    void ApplyAutostart()
    {
        if (OperatingSystem.IsMacOS()) { ApplyMacAutostart(); return; }
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (k == null) return;
            if (Store.Value.StartWithWindows) k.SetValue("PrintBridge", $"\"{Environment.ProcessPath}\" --minimized");
            else k.DeleteValue("PrintBridge", false);
        }
        catch (Exception e) { Log.Error("Autostart setting failed", e); }
    }

    void ApplyMacAutostart()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents");
            var plist = Path.Combine(dir, "com.lifetrenz.printbridge.plist");
            if (Store.Value.StartWithWindows && Environment.ProcessPath is { } exe)
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(plist, $"""
                    <?xml version="1.0" encoding="UTF-8"?>
                    <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                    <plist version="1.0"><dict>
                      <key>Label</key><string>com.lifetrenz.printbridge</string>
                      <key>ProgramArguments</key><array><string>{System.Security.SecurityElement.Escape(exe)}</string><string>--minimized</string></array>
                      <key>RunAtLoad</key><true/>
                    </dict></plist>
                    """);
            }
            else if (File.Exists(plist)) File.Delete(plist);
        }
        catch (Exception e) { Log.Error("Autostart setting failed", e); }
    }

    public void OpenUrl(string url) { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { } }
    public void OpenFolder() { try { Process.Start(new ProcessStartInfo(AppPaths.Dir) { UseShellExecute = true }); } catch { } }
    public void Quit() { Quitting = true; QuitRequested?.Invoke(); }
}
