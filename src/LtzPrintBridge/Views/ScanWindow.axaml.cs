using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using FlashCap;
using LtzPrintBridge.Core;
using LtzPrintBridge.Core.Scanning;

namespace LtzPrintBridge.Views;

/// <summary>
/// Small always-on-top camera window. It opens without taking the keyboard focus, so the web page field the user
/// clicked stays active. Each code it reads is typed into that field, followed by Enter, Tab or nothing.
/// It keeps scanning until closed, and closes by itself after a minute with no scans.
/// </summary>
public partial class ScanWindow : Window
{
    static readonly TimeSpan RepeatWindow = TimeSpan.FromSeconds(3);
    static readonly TimeSpan IdleClose = TimeSpan.FromSeconds(60);

    readonly ScanPrefs _prefs = ScanPrefs.Load();
    readonly FrameDecoder _decoder = new();
    readonly CameraSource _camera = new();
    readonly DispatcherTimer _idle;
    readonly Dictionary<string, DateTime> _seen = new();
    IReadOnlyList<CaptureDeviceDescriptor> _cameras = Array.Empty<CaptureDeviceDescriptor>();
    int _busy;
    volatile bool _paused;
    volatile bool _closed;
    long _lastPreviewTicks;
    bool _ready;
    ScanResult? _pending;

    T C<T>(string name) where T : Control => this.FindControl<T>(name)!;

    public ScanWindow()
    {
        InitializeComponent();
        C<ComboBox>("SuffixBox").SelectedIndex = (int)_prefs.Suffix;
        C<ComboBox>("Gs1Box").SelectedIndex = (int)_prefs.Gs1;
        _camera.Frame += OnFrame;
        _idle = new DispatcherTimer { Interval = IdleClose };
        _idle.Tick += (_, _) => Close();
        Opened += async (_, _) =>
        {
            PlaceBottomRight();
            _idle.Start();
            await LoadCamerasAsync();
            _ready = true;
        };
        Closed += async (_, _) =>
        {
            _closed = true;
            _idle.Stop();
            _camera.Frame -= OnFrame;
            await _camera.StopAsync();
            (C<Image>("Preview").Source as IDisposable)?.Dispose();
        };
    }

    void PlaceBottomRight()
    {
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen == null) return;
        var area = screen.WorkingArea;
        double scale = screen.Scaling <= 0 ? 1 : screen.Scaling;
        int w = (int)(Bounds.Width * scale), h = (int)(Bounds.Height * scale), margin = (int)(20 * scale);
        Position = new PixelPoint(area.Right - w - margin, area.Bottom - h - margin);
    }

    // ---------- camera ----------

    async Task LoadCamerasAsync()
    {
        _cameras = await Task.Run(CameraSource.List);
        var box = C<ComboBox>("CameraBox");
        box.ItemsSource = _cameras.Select(c => c.Name).ToList();
        if (_cameras.Count == 0)
        {
            ShowOverlay("No camera found. Connect a webcam, then open Scan again.");
            return;
        }
        int i = Math.Max(0, _cameras.ToList().FindIndex(c => c.Name == _prefs.Camera));
        box.SelectedIndex = i;          // OnCameraChanged ignores this until _ready
        await StartCameraAsync(i);
    }

    async Task StartCameraAsync(int index)
    {
        if (index < 0 || index >= _cameras.Count) return;
        ShowOverlay("Starting camera…");
        try
        {
            await _camera.StartAsync(_cameras[index]);
            ShowOverlay(null);
        }
        catch (Exception e)
        {
            Log.Error("Camera could not start", e);
            ShowOverlay(OperatingSystem.IsMacOS()
                ? "The camera could not start. Allow PrintBridge under System Settings › Privacy & Security › Camera, then open Scan again."
                : "The camera could not start. Check Settings › Privacy & security › Camera › Let desktop apps access your camera, and that no other app is using it.");
        }
    }

    async void OnCameraChanged(object? s, SelectionChangedEventArgs e)
    {
        if (!_ready || s is not ComboBox box || box.SelectedIndex < 0) return;
        _prefs.Camera = _cameras[box.SelectedIndex].Name;
        _prefs.Save();
        await StartCameraAsync(box.SelectedIndex);
    }

    void ShowOverlay(string? text)
    {
        var o = C<TextBlock>("Overlay");
        o.Text = text ?? "";
        o.IsVisible = text != null;
    }

    /// <summary>Runs on a camera thread. Shows about 15 preview frames a second and decodes whenever the decoder is free.</summary>
    void OnFrame(byte[] image)
    {
        if (_closed) return;
        long now = DateTime.UtcNow.Ticks;
        if (now - Interlocked.Read(ref _lastPreviewTicks) > TimeSpan.TicksPerMillisecond * 66)
        {
            Interlocked.Exchange(ref _lastPreviewTicks, now);
            try
            {
                var bmp = new Bitmap(new MemoryStream(image));
                Dispatcher.UIThread.Post(() =>
                {
                    if (_closed) { bmp.Dispose(); return; }
                    var p = C<Image>("Preview");
                    var old = p.Source as IDisposable;
                    p.Source = bmp;
                    old?.Dispose();
                });
            }
            catch { /* a damaged frame: skip it */ }
        }

        if (_paused || Interlocked.Exchange(ref _busy, 1) == 1) return;
        Task.Run(() =>
        {
            try
            {
                var r = _decoder.Decode(image);
                if (r == null || _paused || _closed || IsRepeat(r.Text)) return;
                _paused = true;
                Dispatcher.UIThread.Post(() => _ = OnScannedAsync(r));
            }
            catch (Exception e) { Log.Warn("Decode: " + e.Message); }
            finally { Interlocked.Exchange(ref _busy, 0); }
        });
    }

    /// <summary>A code held in front of the camera is typed once; the same code again within 3 seconds of last being seen is ignored.</summary>
    bool IsRepeat(string text)
    {
        var now = DateTime.UtcNow;
        lock (_seen)
        {
            bool repeat = _seen.TryGetValue(text, out var last) && now - last < RepeatWindow;
            _seen[text] = now;
            return repeat;
        }
    }

    // ---------- result ----------

    async Task OnScannedAsync(ScanResult r)
    {
        _idle.Stop();
        _idle.Start();
        _pending = r;
        ShowResult(r);
        await DeliverAsync();
    }

    void ShowResult(ScanResult r)
    {
        C<Border>("ResultBox").IsVisible = true;
        C<TextBlock>("ResultValue").Text = _prefs.TextFor(r);
        var parts = new List<string> { r.Format };
        var warn = C<TextBlock>("ResultWarn");
        warn.IsVisible = false;
        if (r.Gs1 is { } g)
        {
            if (g.Gtin != null) parts.Add("GTIN " + g.Gtin);
            if (g.Batch != null) parts.Add("Batch " + g.Batch);
            if (g.Expiry is { } exp) parts.Add("Expiry " + exp.ToString("dd MMM yyyy", CultureInfo.InvariantCulture));
            if (g.Serial != null) parts.Add("Serial " + g.Serial);
            if (g.IsExpired(DateOnly.FromDateTime(DateTime.Today)))
            {
                warn.Text = "This medicine has expired.";
                warn.IsVisible = true;
            }
        }
        C<TextBlock>("ResultDetail").Text = string.Join(" · ", parts);
    }

    async Task DeliverAsync()
    {
        if (_pending is not { } r) return;
        var title = C<TextBlock>("ResultTitle");
        var permission = C<Border>("PermissionBox");

        if (!KeyTyper.CanType(prompt: true))
        {
            title.Text = "Read, not sent yet";
            permission.IsVisible = true;
            return; // stays paused until "Type again"
        }
        permission.IsVisible = false;

        var text = _prefs.TextFor(r);
        var suffix = _prefs.Suffix;
        try
        {
            KeyTyper.ReturnFocus();
            await Task.Delay(250); // let the browser take the focus back before the keys arrive
            await Task.Run(() => KeyTyper.Type(text, suffix));
            title.Text = KeyTyper.TargetName() is { } target ? "Sent to " + target : "Sent to the web page";
            Log.Info($"Scan typed: {r.Format}, {text.Length} characters");
            _pending = null;
        }
        catch (Exception e)
        {
            Log.Error("Typing the scan failed", e);
            title.Text = "Could not type into the web page: " + e.Message;
        }

        await Task.Delay(1200);
        _paused = false; // ready for the next code
    }

    // ---------- buttons ----------

    void OnPrefChanged(object? s, SelectionChangedEventArgs e)
    {
        var suffix = C<ComboBox>("SuffixBox").SelectedIndex;
        var gs1 = C<ComboBox>("Gs1Box").SelectedIndex;
        if (suffix < 0 || gs1 < 0) return;
        _prefs.Suffix = (ScanSuffix)suffix;
        _prefs.Gs1 = (Gs1Output)gs1;
        _prefs.Save();
    }

    void OnOpenAccessibility(object? s, RoutedEventArgs e) => KeyTyper.OpenAccessibilitySettings();
    async void OnTypeAgain(object? s, RoutedEventArgs e) => await DeliverAsync();
    void OnScanAgain(object? s, RoutedEventArgs e) => _paused = false;
    void OnClose(object? s, RoutedEventArgs e) => Close();
}
