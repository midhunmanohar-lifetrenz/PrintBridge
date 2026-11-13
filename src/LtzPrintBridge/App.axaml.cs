using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using LtzPrintBridge.Core;
using LtzPrintBridge.ViewModels;
using LtzPrintBridge.Views;
using System.Globalization;

namespace LtzPrintBridge;

public sealed class LevelBrushConverter : IValueConverter
{
    public bool Foreground { get; set; }
    public object? Convert(object? v, Type t, object? p, CultureInfo c) => (v as string) switch
    {
        "ok" => new SolidColorBrush(Color.Parse(Foreground ? "#17803D" : "#DCF2E3")),
        "warn" => new SolidColorBrush(Color.Parse(Foreground ? "#A15C07" : "#FBECD0")),
        "bad" => new SolidColorBrush(Color.Parse(Foreground ? "#B42318" : "#FBE0DD")),
        _ => new SolidColorBrush(Color.Parse(Foreground ? "#5D6C78" : "#E8EDEF"))
    };
    public object? ConvertBack(object? v, Type t, object? p, CultureInfo c) => null;
}

public sealed class LanguageNameConverter : IValueConverter
{
    public object? Convert(object? v, Type t, object? p, CultureInfo c) => v is PrintLanguage l ? Languages.Display(l) : v?.ToString();
    public object? ConvertBack(object? v, Type t, object? p, CultureInfo c) => null;
}

public partial class App : Application
{
    MainViewModel? _vm; MainWindow? _win; TrayIcon? _tray;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _vm = new MainViewModel();
            _win = new MainWindow { DataContext = _vm };
            _win.Closing += (_, e) => { if (!_vm.Quitting) { e.Cancel = true; _win.Hide(); } };
            desktop.MainWindow = _win;
            _vm.Gate.Prompt = AskHost;
            _vm.Jobs.Review = AskReview;
            _vm.QuitRequested += () => { _tray?.Dispose(); desktop.Shutdown(); };
            BuildTray();
            bool minimized = desktop.Args?.Contains("--minimized") == true;
            if (minimized) _win.ShowInTaskbar = true;
            _ = _vm.InitAsync();
            if (minimized) { /* stay in tray */ _win.WindowState = WindowState.Normal; _win.Hide(); }
        }
        base.OnFrameworkInitializationCompleted();
    }

    void BuildTray()
    {
        try
        {
            var open = new NativeMenuItem("Open PrintBridge"); open.Click += (_, _) => ShowWindow();
            var quit = new NativeMenuItem("Quit"); quit.Click += (_, _) => _vm!.Quit();
            _tray = new TrayIcon
            {
                Icon = TrayGlyph(),
                ToolTipText = "PrintBridge: running",
                Menu = new NativeMenu { open, new NativeMenuItemSeparator(), quit }
            };
            if (OperatingSystem.IsMacOS()) MacOSProperties.SetIsTemplateIcon(_tray, true); // the menu bar tints it for light, dark and wallpaper colours
            _tray.Clicked += (_, _) => ShowWindow();
            TrayIcon.SetIcons(this, new TrayIcons { _tray });
            // Windows has no template images: switch between a black and a white glyph when the taskbar theme changes.
            if (OperatingSystem.IsWindows())
            {
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
                timer.Tick += (_, _) => { if (_tray != null) _tray.Icon = TrayGlyph(); };
                timer.Start();
            }
        }
        catch (Exception e) { Log.Error("Tray icon unavailable", e); }
    }

    /// <summary>One-colour tray icon, like other utilities. Black on light taskbars and on Mac (where it is a template), white on dark taskbars.</summary>
    static WindowIcon TrayGlyph()
    {
        bool light = true;
        if (OperatingSystem.IsWindows())
        {
            try { light = (Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) as int? ?? 0) != 0; }
            catch { light = false; }
        }
        var name = OperatingSystem.IsMacOS() || light ? "tray-black.png" : "tray-white.png";
        return new WindowIcon(AssetLoader.Open(new Uri("avares://PrintBridge/Assets/" + name)));
    }

    void ShowWindow() { if (_win == null) return; _win.Show(); if (_win.WindowState == WindowState.Minimized) _win.WindowState = WindowState.Normal; _win.Activate(); }

    /// <summary>Shows the print preview window and waits for the answer. No answer within two minutes counts as cancel.</summary>
    async Task<JobManager.ReviewDecision> AskReview(JobManager.PrintReview r)
    {
        var shown = r.Labels.Take(6).ToList();
        var images = await Task.Run(() => (IReadOnlyList<PreviewImage?>)shown.Select(l => r.Kind is { } k ? LabelPreview.Render(k, l) : null).ToList());
        var tcs = new TaskCompletionSource<JobManager.ReviewDecision>();
        Dispatcher.UIThread.Post(() =>
        {
            var w = new PrintPreviewWindow(r, images, r.Labels.Count);
            w.Closed += (_, _) => tcs.TrySetResult(new JobManager.ReviewDecision(w.Print, w.CopiesChosen, w.SkipRestChosen));
            w.Show(); w.Activate();
            // The website gave up (timeout, page closed): take the window away so nobody confirms a job that no longer exists.
            r.Aborted.Register(() => Dispatcher.UIThread.Post(() => { if (!tcs.Task.IsCompleted) w.Close(); }));
            _ = Task.Delay(TimeSpan.FromMinutes(2)).ContinueWith(_ => Dispatcher.UIThread.Post(() => { if (!tcs.Task.IsCompleted) w.Close(); }));
        });
        return await tcs.Task;
    }

    Task<HostDecision> AskHost(string host)
    {
        var tcs = new TaskCompletionSource<HostDecision>();
        Dispatcher.UIThread.Post(() =>
        {
            var w = new HostPromptWindow(host);
            void Expired(string h) { if (h == host) Dispatcher.UIThread.Post(w.Close); }
            _vm!.Gate.PromptExpired += Expired;
            w.Closed += (_, _) => { _vm.Gate.PromptExpired -= Expired; tcs.TrySetResult(w.Decision); };
            w.Show(); w.Activate();
        });
        return tcs.Task;
    }
}
