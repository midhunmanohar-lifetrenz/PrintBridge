using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LtzPrintBridge.Core;
using LtzPrintBridge.ViewModels;
using LtzPrintBridge.Views;

namespace LtzPrintBridge.Tests;

/// <summary>Produces the pictures for the user guide from the real windows. Runs only when LTZ_GUIDE_DIR is set. Names and sites are illustrative.</summary>
public class GuideScreenshots
{
    static void Flush() { for (int i = 0; i < 6; i++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); } }
    static IEnumerable<T> All<T>(Control c) where T : Visual => c.GetVisualDescendants().OfType<T>();
    static void Nav(Window w, string text) { var rb = All<RadioButton>((Control)w).First(x => (x.Content as string) == text); rb.IsChecked = true; rb.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Flush(); }

    [AvaloniaFact]
    public async Task Capture_guide_pictures()
    {
        var dir = Environment.GetEnvironmentVariable("LTZ_GUIDE_DIR"); if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        using var s1 = new TcpSink(); using var s2 = new TcpSink(); using var s3 = new TcpSink();

        var st = new SettingsStore();
        st.Value.AcceptedHosts = new() { "localhost", "127.0.0.1", "app.clinic.example", "uat.clinic.example", "*.clinic-group.example" }; st.Value.BlockedHosts = new() { "unknown-site.example" };
        st.Value.ManualPrinters = new()
        {
            new ManualPrinter { Name = "TSC TE244", Host = "127.0.0.1", Port = s1.Port, Vendor = "TSC", Language = PrintLanguage.Tspl },
            new ManualPrinter { Name = "TVS LP46 Dlite", Host = "127.0.0.1", Port = s2.Port, Vendor = "TVS / SNBC", Language = PrintLanguage.Bple },
            new ManualPrinter { Name = "Zebra ZD421", Host = "127.0.0.1", Port = s3.Port, Vendor = "Zebra", Language = PrintLanguage.Zpl },
        };
        st.Value.LanguageOverrides = new(); st.Value.DefaultPrinterId = null; st.Value.PreviewMode = PreviewMode.Off; st.Value.PreviewSites = new() { "uat.clinic.example" };
        st.Value.RouteAllToDefault = true;
        st.Save();

        var vm = new MainViewModel();
        await vm.Registry.RefreshAsync(); Flush();
        var tsc = vm.Registry.All.First(p => p.Name == "TSC TE244");
        vm.DefaultPrinter = vm.Printers.First(p => p.Name == "TSC TE244"); Flush();

        // Some history: real jobs through the real pipeline.
        foreach (var (site, name, n) in new[] { ("app.clinic.example", "TSC TE244", 1), ("app.clinic.example", "TSC TE244", 1), ("uat.clinic.example", "TSC TE244", 1), ("app.clinic.example", "TSC TE244", 1) })
            for (int i = 0; i < n; i++) await vm.Jobs.PrintAsync(vm.Registry.All.First(p => p.Name == name), System.Text.Encoding.UTF8.GetBytes("SIZE 50 mm,25 mm\r\nCLS\r\nTEXT 10,10,\"3\",0,1,1,\"x\"\r\nPRINT 1\r\n"), site);
        vm.Store.Value.PreviewMode = PreviewMode.All; vm.Jobs.Review = _ => Task.FromResult(new JobManager.ReviewDecision(false, 1, false));
        await vm.Jobs.PrintAsync(vm.Registry.All.First(p => p.Name == "TSC TE244"), System.Text.Encoding.UTF8.GetBytes("SIZE 50 mm,25 mm\r\nCLS\r\nPRINT 1\r\n"), "uat.clinic.example");
        vm.Store.Value.PreviewMode = PreviewMode.Selected;
        Flush();

        // Make two of the three look like the USB queues most clinics have. (The windows read these values when they open.)
        foreach (var p in vm.Printers)
        {
            if (p.Name == "TSC TE244") { p.Transport = TransportKind.Spooler; p.Connection = "USB"; p.Port = "USB001"; p.Driver = "TSC TE244"; p.Address = p.Name; }
            if (p.Name == "TVS LP46 Dlite") { p.Transport = TransportKind.Spooler; p.Connection = "USB"; p.Port = "USB002"; p.Driver = "TVS-E LP46 Dlite"; p.Address = p.Name; }
            if (p.Name == "Zebra ZD421") { p.Driver = "Direct network (no driver)"; p.Port = "192.168.1.52:9100"; }
        }
        vm.SelectedPrinter = vm.Printers.First(p => p.Name == "TSC TE244");
        vm.RawText = "SIZE 50 mm,25 mm\r\nGAP 2 mm,0 mm\r\nCLS\r\nTEXT 20,20,\"3\",0,1,1,\"TEST LABEL\"\r\nBARCODE 20,60,\"128\",60,1,0,2,2,\"123456789\"\r\nPRINT 1\r\n";

        // Website log lines, raised the way the server raises them.
        var raise = vm.Server.GetType().GetMethod("RaiseRequested", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        foreach (var e in new[]
        {
            new RequestEntry { Site = "app.clinic.example", Method = "GET", Path = "/available", Status = 200, Result = "Allowed" },
            new RequestEntry { Site = "app.clinic.example", Method = "POST", Path = "/write", Status = 200, Result = "Allowed" },
            new RequestEntry { Site = "uat.clinic.example", Method = "OPTIONS", Path = "/write", Status = 200, Result = "Allowed (browser preflight)" },
            new RequestEntry { Site = "unknown-site.example", Method = "GET", Path = "/available", Status = 403, Result = "Website not allowed. Add it on the Websites page." },
        }) raise.Invoke(vm.Server, new object[] { e });
        Flush();

        // Healthy diagnostics, with the same wording the app uses.
        vm.Diag.Clear();
        foreach (var d in new[]
        {
            new DiagItem { Title = "Bridge (HTTP)", Detail = "Listening on 127.0.0.1:9100. Not reachable from other computers." },
            new DiagItem { Title = "Bridge (HTTPS)", Detail = "Listening on 127.0.0.1:9101." },
            new DiagItem { Title = "Secure connection", Detail = "Your computer trusts the local certificate. Chrome and Edge connect without warnings." },
            new DiagItem { Title = "Windows printers", Detail = "3 printer queue(s) found in the Windows spooler." },
            new DiagItem { Title = "Default printer", Detail = "TSC TE244 (USB, TSPL). Every print request goes to this printer." },
        }) vm.Diag.Add(d);
        vm.BridgeText = "Running"; vm.BridgeLevel = "ok";
        vm.ToastText = "";

        var win = new MainWindow { DataContext = vm, Width = 1040, Height = 700 };
        win.Show(); Flush();
        void Shot(Window w, string name) { Flush(); w.CaptureRenderedFrame()?.Save(Path.Combine(dir, name + ".png")); }

        Nav(win, "Printers"); Shot(win, "printers");
        Nav(win, "Websites"); Shot(win, "websites");
        Nav(win, "Print jobs"); Shot(win, "jobs");
        Nav(win, "Settings");
        // The pictures are used in the Windows guide, so show the Windows wording and folder here.
        foreach (var t in All<TextBlock>(win))
        {
            if (t.Text == "Start when I log in to this Mac") t.Text = "Start when I sign in to Windows";
            else if (t.Text == "Runs quietly in the menu bar.") t.Text = "Runs quietly in the system tray.";
            else if (t.Text == AppPaths.Dir) t.Text = @"C:\Users\clinic.user\AppData\Local\LtzPrintBridge";
        }
        Shot(win, "settings");
        // Diagnostics runs the live checks when opened, so show it last with the prepared healthy list.
        All<RadioButton>(win).First(x => (x.Content as string) == "Diagnostics").IsChecked = true; vm.Page = 3; Flush(); vm.Diag.Clear(); foreach (var d in new[] {
            new DiagItem { Title = "Bridge (HTTP)", Detail = "Listening on 127.0.0.1:9100. Not reachable from other computers." },
            new DiagItem { Title = "Bridge (HTTPS)", Detail = "Listening on 127.0.0.1:9101." },
            new DiagItem { Title = "Secure connection", Detail = "Your computer trusts the local certificate. Chrome and Edge connect without warnings." },
            new DiagItem { Title = "Windows printers", Detail = "3 printer queue(s) found in the Windows spooler." },
            new DiagItem { Title = "Default printer", Detail = "TSC TE244 (USB, TSPL). Every print request goes to this printer." } }) vm.Diag.Add(d);
        Shot(win, "diagnostics");

        win.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark; vm.Page = 0; Flush(); Shot(win, "printers-dark");
        win.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light; vm.Page = 0; Flush();
        win.Close();

        var prompt = new HostPromptWindow("uat.clinic.example"); prompt.Show(); Flush(); Shot(prompt, "prompt"); prompt.Close();
        var add = new AddPrinterWindow(); add.Show(); Flush();
        add.FindControl<TextBox>("HostBox")!.Text = "192.168.1.52"; add.FindControl<TextBox>("PortBox")!.Text = "9100"; Flush(); Shot(add, "add-printer"); add.Close();

        var zpl = ZplPreviewTests.Sample; var labels = ZplPreview.SplitLabels(zpl);
        var review = new JobManager.PrintReview("uat.clinic.example", "TSC TE244", "TSPL", zpl, labels, 1, true, PrintLanguage.Zpl);
        var pv = new PrintPreviewWindow(review, labels.Select(l => ZplPreview.Render(l)).ToList(), labels.Count); pv.Show(); Flush(); Shot(pv, "preview"); pv.Close();
        const string guideTspl = "SIZE 50 mm,30 mm\r\nGAP 2 mm,0 mm\r\nCLS\r\nTEXT 20,15,\"3\",0,1,1,\"SAMPLE LABEL\"\r\nTEXT 20,50,\"2\",0,1,1,\"ID 000123456\"\r\n" +
            "BARCODE 20,85,\"128\",60,1,0,2,2,\"000123456\"\r\nQRCODE 290,45,M,4,A,0,\"000123456\"\r\nBAR 20,175,360,3\r\nTEXT 20,190,\"2\",0,1,1,\"Collected 22-Oct-2026 08:15\"\r\nPRINT 1,1\r\n";
        var tspl = LabelPreview.Split(PrintLanguage.Tspl, guideTspl);
        var review2 = new JobManager.PrintReview("app.clinic.example", "TSC TE244", "TSPL / TSPL2", guideTspl, tspl, 2, true, PrintLanguage.Tspl);
        var pv2 = new PrintPreviewWindow(review2, tspl.Select(l => LabelPreview.Render(PrintLanguage.Tspl, l)).ToList(), tspl.Count); pv2.Show(); Flush(); Shot(pv2, "preview-tspl"); pv2.Close();
    }
}
