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

public static class UiTestApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseSkia().WithInterFont()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public class UiTests
{
    sealed class Rig : IDisposable
    {
        public TcpSink Sink = new(), Sink2 = new(); public MainViewModel VM = default!; public MainWindow Win = default!;
        public void Dispose() { Win.Close(); Sink.Dispose(); Sink2.Dispose(); }
    }

    static void Flush() { for (int i = 0; i < 4; i++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); } }

    static async Task<Rig> MakeAsync(bool withPrinters = true, string page = "Printers")
    {
        var r = new Rig();
        var st = new SettingsStore();
        st.Value.AcceptedHosts = new() { "localhost", "127.0.0.1", "qa.clinic.example" }; st.Value.BlockedHosts = new();
        st.Value.ManualPrinters = new(); st.Value.LanguageOverrides = new(); st.Value.DefaultPrinterId = null;
        if (withPrinters)
        {
            st.Value.ManualPrinters.Add(new ManualPrinter { Name = "Fake Zebra", Host = "127.0.0.1", Port = r.Sink.Port, Vendor = "Zebra", Language = PrintLanguage.Zpl });
            st.Value.ManualPrinters.Add(new ManualPrinter { Name = "Fake TVS", Host = "127.0.0.1", Port = r.Sink2.Port, Vendor = "TVS / SNBC", Language = PrintLanguage.Bple });
        }
        st.Save();
        r.VM = new MainViewModel();
        r.Win = new MainWindow { DataContext = r.VM, Width = 1040, Height = 720 };
        r.Win.Show();
        await r.VM.Registry.RefreshAsync(); Flush();
        if (page != "Overview") Nav(r, page);
        return r;
    }

    static IEnumerable<T> All<T>(Control c) where T : Visual => c.GetVisualDescendants().OfType<T>();
    static Button Btn(Control c, string text) => All<Button>(c).First(b => b.IsEffectivelyVisible && (b.Content as string) == text);
    static void Click(Button b) { b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Flush(); }
    static void Nav(Rig r, string text) { var rb = All<RadioButton>(r.Win).First(x => (x.Content as string) == text); rb.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Flush(); }
    static bool Shows(Control c, string text) => All<TextBlock>(c).Any(t => t.IsEffectivelyVisible && t.Text == text);

    [AvaloniaFact]
    public async Task Printers_page_lists_configured_printers_with_language_chips()
    {
        using var r = await MakeAsync();
        Assert.True(r.VM.IsPrinters);
        Assert.True(Shows(r.Win, "Fake Zebra")); Assert.True(Shows(r.Win, "Fake TVS"));
        Assert.True(Shows(r.Win, "ZPL II")); Assert.True(Shows(r.Win, "BPLE (EPL-style)"));
        Assert.True(Shows(r.Win, "2 of 2 printers ready"));
        Assert.True(Shows(r.Win, "Default"));
    }

    [AvaloniaFact]
    public async Task Empty_state_when_no_printers()
    {
        using var r = await MakeAsync(false);
        Assert.True(Shows(r.Win, "No printers found yet"));
        Assert.False(r.VM.HasSelection);
        Assert.DoesNotContain(All<Button>(r.Win), b => b.IsEffectivelyVisible && (b.Content as string) == "Print test label");
    }

    [AvaloniaFact]
    public async Task Navigation_switches_pages()
    {
        using var r = await MakeAsync();
        Nav(r, "Websites"); Assert.True(r.VM.IsHosts); Assert.True(Shows(r.Win, "Only websites on the allowed list can see your printers or print. Unknown websites ask first.")); Assert.False(Shows(r.Win, "Scan again"));
        Nav(r, "Print jobs"); Assert.True(r.VM.IsJobs);
        Nav(r, "Diagnostics"); Assert.True(r.VM.IsDiag);
        Nav(r, "Settings"); Assert.True(r.VM.IsSettings); Assert.True(Shows(r.Win, "Data folder"));
        Nav(r, "Printers"); Assert.True(r.VM.IsPrinters); Assert.True(Shows(r.Win, "Scan again"));
    }

    [AvaloniaFact]
    public async Task Selecting_a_printer_updates_the_detail_panel()
    {
        using var r = await MakeAsync();
        r.VM.SelectedPrinter = r.VM.Printers.First(p => p.Name == "Fake TVS"); Flush();
        Assert.True(Shows(r.Win, "Fake TVS"));
        Assert.Contains("N\n", r.VM.RawText);                 // EPL-style sample for BPLE
        Assert.Equal(PrintLanguage.Bple, r.VM.SelectedLanguage);
    }

    [AvaloniaFact]
    public async Task Print_test_label_button_sends_the_sample_and_shows_a_toast()
    {
        using var r = await MakeAsync();
        r.VM.SelectedPrinter = r.VM.Printers.First(p => p.Name == "Fake Zebra"); Flush();
        Click(Btn(r.Win, "Print test label"));
        var got = await r.Sink.WaitFor(1); Flush();
        Assert.Contains("^XA", System.Text.Encoding.UTF8.GetString(got.Single()));
        Assert.Contains("sent to Fake Zebra", r.VM.ToastText);
        Assert.Equal("Fake Zebra", r.VM.JobList.First().Printer); Assert.True(r.VM.JobList.First().Ok);
    }

    [AvaloniaFact]
    public async Task Send_command_box_sends_exactly_what_was_typed()
    {
        using var r = await MakeAsync();
        r.VM.SelectedPrinter = r.VM.Printers.First(p => p.Name == "Fake TVS"); Flush();
        r.VM.RawText = "N\nA10,10,0,3,1,1,N,\"HELLO\"\nP1\n";
        Click(Btn(r.Win, "Send command"));
        Assert.Equal("N\nA10,10,0,3,1,1,N,\"HELLO\"\nP1\n", System.Text.Encoding.UTF8.GetString((await r.Sink2.WaitFor(1)).Single()));
    }

    [AvaloniaFact]
    public async Task Changing_language_updates_sample_and_is_remembered()
    {
        using var r = await MakeAsync();
        r.VM.SelectedPrinter = r.VM.Printers.First(p => p.Name == "Fake TVS"); Flush();
        r.VM.SelectedLanguage = PrintLanguage.Tspl; Flush();
        Assert.Contains("SIZE", r.VM.RawText);
        var again = new SettingsStore(); again.Load();
        Assert.Equal(PrintLanguage.Tspl, again.Value.LanguageOverrides["tcp:127.0.0.1:" + r.Sink2.Port]);
    }

    [AvaloniaFact]
    public async Task Make_default_moves_the_default_tag()
    {
        using var r = await MakeAsync();
        var tvs = r.VM.Printers.First(p => p.Name == "Fake TVS"); r.VM.SelectedPrinter = tvs; Flush();
        Click(Btn(r.Win, "Make default")); Flush();
        Assert.True(r.VM.Printers.First(p => p.Name == "Fake TVS").IsDefault);
        Assert.False(r.VM.Printers.First(p => p.Name == "Fake Zebra").IsDefault);
        Assert.Contains("is now the default", r.VM.ToastText);
    }

    [AvaloniaFact]
    public async Task Default_printer_picker_changes_the_default_and_routing_toggle_persists()
    {
        using var r = await MakeAsync();
        Assert.True(Shows(r.Win, "Default printer")); Assert.True(Shows(r.Win, "Send every print request here"));
        var tvs = r.VM.Printers.First(p => p.Name == "Fake TVS");
        r.VM.DefaultPrinter = tvs; Flush();
        Assert.Equal("Fake TVS", r.VM.DefaultPrinter!.Name);
        var again = new SettingsStore(); again.Load(); Assert.Equal(tvs.Id, again.Value.DefaultPrinterId);
        r.VM.RouteAllToDefault = false; again.Load(); Assert.False(again.Value.RouteAllToDefault);
        r.VM.RouteAllToDefault = true; again.Load(); Assert.True(again.Value.RouteAllToDefault);
    }

    [AvaloniaFact]
    public async Task Background_refresh_does_not_wipe_a_command_being_typed()
    {
        using var r = await MakeAsync();
        r.VM.SelectedPrinter = r.VM.Printers.First(p => p.Name == "Fake Zebra"); Flush();
        r.VM.RawText = "~HS typed by the user";
        await r.VM.Registry.RefreshAsync(); Flush();
        Assert.Equal("~HS typed by the user", r.VM.RawText);
        Assert.Equal("Fake Zebra", r.VM.SelectedPrinter!.Name);
    }

    [AvaloniaFact]
    public async Task Websites_page_allow_block_unblock_remove()
    {
        using var r = await MakeAsync();
        Nav(r, "Websites");
        r.VM.NewHost = "https://New.Clinic.org/"; Click(Btn(r.Win, "Allow"));
        Assert.Contains("new.clinic.org", r.VM.Allowed.Select(h => h.Host)); Assert.Equal("", r.VM.NewHost);
        Flush(); Click(All<Button>(r.Win).First(b => b.IsEffectivelyVisible && (b.Content as string) == "Block" && b.DataContext is HostRow h && h.Host == "new.clinic.org"));
        Assert.Contains("new.clinic.org", r.VM.Blocked); Assert.DoesNotContain("new.clinic.org", r.VM.Allowed.Select(h => h.Host));
        Flush(); Click(All<Button>(r.Win).First(b => b.IsEffectivelyVisible && (b.Content as string) == "Unblock"));
        Assert.Contains("new.clinic.org", r.VM.Allowed.Select(h => h.Host));
        Flush(); Click(All<Button>(r.Win).First(b => b.IsEffectivelyVisible && (b.Content as string) == "Remove" && b.DataContext is HostRow h && h.Host == "new.clinic.org"));
        Assert.DoesNotContain("new.clinic.org", r.VM.Allowed.Select(h => h.Host)); Assert.DoesNotContain("new.clinic.org", r.VM.Blocked);
    }

    [AvaloniaFact]
    public async Task Preview_mode_and_per_website_tick_are_saved()
    {
        using var r = await MakeAsync();
        Nav(r, "Websites");
        r.VM.NewHost = "uat.clinic.org"; Click(Btn(r.Win, "Allow")); Flush();
        Assert.DoesNotContain(All<CheckBox>(r.Win), c => c.IsEffectivelyVisible && (c.Content as string) == "Preview");   // hidden until "Only chosen websites"

        r.VM.PreviewModeIndex = 2; Flush();
        var tick = All<CheckBox>(r.Win).First(c => c.IsEffectivelyVisible && (c.Content as string) == "Preview" && c.DataContext is HostRow h && h.Host == "uat.clinic.org");
        tick.IsChecked = true; Flush();
        Assert.Equal(PreviewMode.Selected, r.VM.Store.Value.PreviewMode);
        Assert.Contains("uat.clinic.org", r.VM.Store.Value.PreviewSites);
        tick.IsChecked = false; Flush();
        Assert.DoesNotContain("uat.clinic.org", r.VM.Store.Value.PreviewSites);

        r.VM.PreviewModeIndex = 0; Flush();
        Assert.Equal(PreviewMode.Off, r.VM.Store.Value.PreviewMode);
    }

    [AvaloniaFact]
    public async Task Blank_host_is_ignored()
    {
        using var r = await MakeAsync(); Nav(r, "Websites"); int n = r.VM.Allowed.Count;
        r.VM.NewHost = "   "; Click(Btn(r.Win, "Allow")); Assert.Equal(n, r.VM.Allowed.Count);
    }

    [AvaloniaFact]
    public async Task Jobs_page_shows_completed_jobs_without_label_content()
    {
        using var r = await MakeAsync();
        r.VM.SelectedPrinter = r.VM.Printers.First(p => p.Name == "Fake Zebra"); Flush();
        Click(Btn(r.Win, "Print test label")); await r.Sink.WaitFor(1); Flush();
        Nav(r, "Print jobs");
        Assert.True(Shows(r.Win, "PrintBridge app")); Assert.True(Shows(r.Win, "Sent"));
        Assert.DoesNotContain(All<TextBlock>(r.Win), t => t.IsEffectivelyVisible && (t.Text ?? "").Contains("^XA"));
    }

    [AvaloniaFact]
    public async Task Diagnostics_lists_checks()
    {
        using var r = await MakeAsync(); await r.VM.RunDiagnostics(); Flush(); Nav(r, "Diagnostics");
        Assert.True(Shows(r.Win, "Bridge (HTTP)")); Assert.True(Shows(r.Win, "Secure connection")); Assert.False(Shows(r.Win, "Zebra compatibility"));
        Assert.True(r.VM.Diag.Count >= 5);
        Assert.True(Shows(r.Win, "Install certificate"));
    }

    [AvaloniaFact]
    public void Host_prompt_window_returns_each_decision()
    {
        foreach (var (label, expected) in new[] { ("Always allow", HostDecision.AllowAlways), ("Allow once", HostDecision.AllowOnce), ("Block", HostDecision.Block) })
        {
            var w = new HostPromptWindow("new.example.com"); w.Show(); Flush();
            Assert.Contains(All<TextBlock>(w), t => t.Text == "new.example.com");
            Click(All<Button>(w).First(b => (b.Content as string) == label));
            Assert.Equal(expected, w.Decision);
        }
    }

    [AvaloniaFact]
    public void Closing_host_prompt_without_choosing_is_not_a_permanent_block()
    {
        var w = new HostPromptWindow("x.example"); w.Show(); Flush(); w.Close(); Assert.Equal(HostDecision.NotNow, w.Decision);
    }

    [AvaloniaFact]
    public void Add_printer_window_validates_and_returns_printer()
    {
        var w = new AddPrinterWindow(); w.Show(); Flush();
        Click(Btn(w, "Add printer")); Assert.Null(w.Result);
        Assert.True(All<TextBlock>(w).First(t => t.Name == "Err").IsVisible);
        w.FindControl<TextBox>("NameBox")!.Text = "Pharmacy"; w.FindControl<TextBox>("HostBox")!.Text = "192.168.1.50"; w.FindControl<TextBox>("PortBox")!.Text = "9100";
        w.FindControl<ComboBox>("VendorBox")!.SelectedItem = "TSC"; Flush();
        Click(Btn(w, "Add printer"));
        Assert.NotNull(w.Result); Assert.Equal("192.168.1.50", w.Result!.Host); Assert.Equal(PrintLanguage.Tspl, w.Result.Language); Assert.Equal(9100, w.Result.Port);
    }

    [AvaloniaFact]
    public void Add_printer_window_rejects_bad_port()
    {
        var w = new AddPrinterWindow(); w.Show(); Flush();
        w.FindControl<TextBox>("HostBox")!.Text = "10.0.0.2"; w.FindControl<TextBox>("PortBox")!.Text = "99999";
        Click(Btn(w, "Add printer")); Assert.Null(w.Result);
    }

    [AvaloniaFact]
    public async Task Capture_screenshots_of_every_page()
    {
        var dir = Environment.GetEnvironmentVariable("LTZ_SHOT_DIR"); if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        using var r = await MakeAsync();
        await r.VM.RunDiagnostics();
        r.VM.SelectedPrinter = r.VM.Printers.First(p => p.Name == "Fake Zebra"); Flush();
        Click(Btn(r.Win, "Print test label")); await r.Sink.WaitFor(1); r.VM.ToastText = ""; Flush();
        void Shot(string n) { Flush(); r.Win.CaptureRenderedFrame()?.Save(Path.Combine(dir, n + ".png")); }
        Shot("1-printers"); Nav(r, "Websites"); Shot("2-websites"); Nav(r, "Print jobs"); Shot("3-jobs");
        Nav(r, "Diagnostics"); Shot("4-diagnostics"); Nav(r, "Settings"); Shot("5-settings");
        var p = new HostPromptWindow("new.clinic.example"); p.Show(); Flush(); p.CaptureRenderedFrame()?.Save(Path.Combine(dir, "6-prompt.png")); p.Close();
        var a = new AddPrinterWindow(); a.Show(); Flush(); a.CaptureRenderedFrame()?.Save(Path.Combine(dir, "7-add-printer.png")); a.Close();
        r.Win.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        Nav(r, "Printers"); Shot("9-printers-dark"); Nav(r, "Websites"); Shot("9-websites-dark");
        r.Win.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
        r.Win.Width = 820; r.Win.Height = 520; Nav(r, "Printers"); Shot("9-small-printers"); Nav(r, "Websites"); Shot("9-small-websites"); Nav(r, "Settings"); Shot("9-small-settings");
        var zpl = ZplPreviewTests.Sample; var labels = ZplPreview.SplitLabels(zpl);
        var review = new JobManager.PrintReview("utkal-uat.example.com", "Fake Zebra", "ZPL II", zpl, labels, 1, true);
        var pv = new PrintPreviewWindow(review, labels.Select(l => ZplPreview.Render(l)).ToList(), labels.Count); pv.Show(); Flush();
        pv.CaptureRenderedFrame()?.Save(Path.Combine(dir, "8-print-preview.png")); pv.Close();
    }

    [AvaloniaFact]
    public void Print_preview_window_returns_copies_and_choice()
    {
        var labels = ZplPreview.SplitLabels(ZplPreviewTests.Sample);
        var review = new JobManager.PrintReview("site.example.com", "Fake Zebra", "ZPL II", ZplPreviewTests.Sample, labels, 2, true);
        var w = new PrintPreviewWindow(review, labels.Select(l => ZplPreview.Render(l)).ToList(), 1); w.Show(); Flush();
        Assert.True(Shows(w, "Copies of each label"));
        w.FindControl<NumericUpDown>("Copies")!.Value = 5; w.FindControl<CheckBox>("SkipRest")!.IsChecked = true;
        Click(Btn(w, "Print"));
        Assert.True(w.Print); Assert.Equal(5, w.CopiesChosen); Assert.True(w.SkipRestChosen);

        var c = new PrintPreviewWindow(review, new List<PreviewImage?>(), 0); c.Show(); Flush();
        Assert.Contains(All<TextBlock>(c), t => t.Text != null && t.Text.Contains("job is in ZPL II"));                   // no picture: shows the commands and says why
        Click(Btn(c, "Cancel")); Assert.False(c.Print);
    }
}
