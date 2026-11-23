using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using FlashCap;
using LtzPrintBridge.Core;
using LtzPrintBridge.Core.Scanning;

namespace LtzPrintBridge.Views;

/// <summary>
/// A small always-on-top strip (camera thumbnail + last value) that opens without taking the keyboard focus,
/// so the web page field the user clicked stays active. Each code it reads is typed into that field,
/// followed by Enter, Tab or nothing. It keeps scanning until closed, and closes itself after a minute with no scans.
/// </summary>
public partial class ScanWindow : Window
{
    static readonly TimeSpan RepeatWindow = TimeSpan.FromSeconds(3);
    static readonly TimeSpan IdleClose = TimeSpan.FromSeconds(60);
    static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#93A4B1"));
    static readonly IBrush Good = new SolidColorBrush(Color.Parse("#5EEAD4"));
    static readonly IBrush Warn = new SolidColorBrush(Color.Parse("#F5B94F"));
    static readonly IBrush Bad = new SolidColorBrush(Color.Parse("#FF8A80"));

    readonly ScanPrefs _prefs = ScanPrefs.Load();
    readonly FrameDecoder _decoder = new();
    readonly CameraSource _camera = new();
    readonly DispatcherTimer _idle;
    readonly Dictionary<string, DateTime> _seen = new();
    IReadOnlyList<CaptureDeviceDescriptor> _cameras = Array.Empty<CaptureDeviceDescriptor>();
    int _cameraIndex = -1;
    int _busy;
    long _frames;
    bool _loggedFrame;
    volatile bool _paused;
    volatile bool _closed;
    ScanResult? _pending;

    T C<T>(string name) where T : Control => this.FindControl<T>(name)!;

    /// <summary>Tests and screenshots show the window without opening a camera.</summary>
    public static bool CameraDisabled { get; set; }

    public ScanWindow()
    {
        InitializeComponent();
        _camera.Frame += OnFrame;
        _idle = new DispatcherTimer { Interval = IdleClose };
        _idle.Tick += (_, _) => Close();
        Opened += async (_, _) =>
        {
            PlaceBottomRight();
            _idle.Start();
            if (!CameraDisabled) await LoadCamerasAsync();
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
        int w = (int)(Width * scale), h = (int)(Height * scale), margin = (int)(16 * scale);
        Position = new PixelPoint(area.Right - w - margin, area.Bottom - h - margin);
    }

    public void ShowForTest(string status, string value, string? detail) => Show(status, Good, value, detail);

    void Show(string status, IBrush statusColor, string? value = null, string? detail = null, IBrush? detailColor = null)
    {
        var s = C<TextBlock>("Status"); s.Text = status; s.Foreground = statusColor;
        if (value != null)
        {
            var v = C<TextBlock>("Value");
            v.Text = value;
            v.FontSize = value.Length > 16 ? 12 : 16; // long medicine codes stay readable in the strip
        }
        var d = C<TextBlock>("Detail");
        d.Text = detail ?? "";
        d.IsVisible = !string.IsNullOrEmpty(detail);
        d.Foreground = detailColor ?? Muted;
    }

    // ---------- camera ----------

    async Task LoadCamerasAsync()
    {
        _cameras = await Task.Run(CameraSource.List);
        if (_cameras.Count == 0)
        {
            Show("No camera found", Bad, "—", "Connect a webcam, then open Scan again");
            return;
        }
        await StartCameraAsync(Math.Max(0, _cameras.ToList().FindIndex(c => c.Name == _prefs.Camera)));
    }

    async Task StartCameraAsync(int index)
    {
        if (index < 0 || index >= _cameras.Count) return;
        _cameraIndex = index;
        var cam = CameraPermission.Mac();
        if (cam is CameraPermission.State.Denied or CameraPermission.State.Restricted)
        {
            Show("Camera blocked", Bad, "Allow camera", "Click ⋯ › Allow camera");
            return;
        }
        Show(cam == CameraPermission.State.NotAsked ? "Allow the camera when macOS asks" : "Starting camera…", Muted);
        Interlocked.Exchange(ref _frames, 0);
        try
        {
            await _camera.StartAsync(_cameras[index]);
        }
        catch (Exception e)
        {
            Log.Error("Camera could not start", e);
            Show("Camera could not start", Bad, "—", OperatingSystem.IsWindows()
                ? "Settings › Privacy › Camera › Let desktop apps access" : "Click ⋯ › Allow camera");
            return;
        }
        // No picture after a few seconds: usually the camera permission, or another app holding the camera.
        _ = Task.Delay(5000).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
        {
            if (_closed || _cameraIndex != index || Interlocked.Read(ref _frames) > 0) return;
            Log.Warn("Camera started but sent no picture in 5 s");
            var p = CameraPermission.Mac();
            Show(p == CameraPermission.State.Allowed ? "No picture from the camera" : "Camera not allowed", Bad, "—",
                p == CameraPermission.State.Allowed ? "Close other apps using it, then reopen" : "Click ⋯ › Allow camera");
        }));
    }

    /// <summary>Runs on a camera thread. Frames that arrive while the previous one is still being read are skipped.</summary>
    void OnFrame(byte[] data)
    {
        if (_closed) return;
        if (Interlocked.Increment(ref _frames) == 1)
            Dispatcher.UIThread.Post(() => { if (!_paused && _pending == null) Show("Scanning…", Muted); });
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        Task.Run(() =>
        {
            try
            {
                using var bmp = FrameImage.Decode(data);
                if (!_loggedFrame)
                {
                    _loggedFrame = true;
                    Log.Info(bmp == null
                        ? $"First camera frame could not be read ({data.Length} bytes, starts {Convert.ToHexString(data.AsSpan(0, Math.Min(4, data.Length)))})"
                        : $"First camera frame {bmp.Width}x{bmp.Height}, {data.Length} bytes");
                }
                if (bmp == null) return;

                var thumb = FrameImage.Thumbnail(bmp, 192, 144);
                Dispatcher.UIThread.Post(() =>
                {
                    if (_closed) return;
                    var img = C<Image>("Preview");
                    var old = img.Source as IDisposable;
                    img.Source = new Bitmap(new MemoryStream(thumb));
                    old?.Dispose();
                });

                if (_paused) return;
                var r = _decoder.Decode(bmp);
                if (r == null || _paused || _closed || IsRepeat(r.Text)) return;
                _paused = true;
                Dispatcher.UIThread.Post(() => _ = OnScannedAsync(r));
            }
            catch (Exception e) { Log.Warn("Frame: " + e.Message); }
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
        await DeliverAsync();
    }

    (string? detail, IBrush color) Details(ScanResult r)
    {
        if (r.Gs1 is not { } g) return (null, Muted);
        var parts = new List<string>();
        if (g.Batch != null) parts.Add("Batch " + g.Batch);
        if (g.Expiry is { } exp) parts.Add("Exp " + exp.ToString("MMM yyyy", CultureInfo.InvariantCulture));
        bool expired = g.IsExpired(DateOnly.FromDateTime(DateTime.Today));
        if (expired) parts.Insert(0, "EXPIRED");
        return (parts.Count > 0 ? string.Join(" · ", parts) : null, expired ? Bad : Muted);
    }

    async Task DeliverAsync()
    {
        if (_pending is not { } r) return;
        var text = _prefs.TextFor(r);
        var (detail, detailColor) = Details(r);

        if (!KeyTyper.CanType(prompt: true))
        {
            Show("Not sent · allow typing", Warn, text, "Click ⋯ › Allow typing, then Type again");
            return; // stays paused until "Type again"
        }

        try
        {
            KeyTyper.ReturnFocus();
            await Task.Delay(250); // let the browser take the focus back before the keys arrive
            await Task.Run(() => KeyTyper.Type(text, _prefs.Suffix));
            Show("Sent ✓", Good, text, detail, detailColor);
            Log.Info($"Scan typed: {r.Format}, {text.Length} characters");
            _pending = null;
        }
        catch (Exception e)
        {
            Log.Error("Typing the scan failed", e);
            Show("Could not type", Bad, text, e.Message);
            return;
        }

        await Task.Delay(1200);
        _paused = false; // ready for the next code
    }

    // ---------- menu and buttons ----------

    void OnMenu(object? s, RoutedEventArgs e)
    {
        var menu = new MenuFlyout();
        menu.Items.Add(Group("After the value",
            Radio("Press Enter", _prefs.Suffix == ScanSuffix.Enter, () => _prefs.Suffix = ScanSuffix.Enter),
            Radio("Press Tab", _prefs.Suffix == ScanSuffix.Tab, () => _prefs.Suffix = ScanSuffix.Tab),
            Radio("Nothing", _prefs.Suffix == ScanSuffix.None, () => _prefs.Suffix = ScanSuffix.None)));
        menu.Items.Add(Group("Medicine code",
            Radio("Full code, as printed (01)…(17)…", _prefs.Gs1 == Gs1Output.Bracketed, () => _prefs.Gs1 = Gs1Output.Bracketed),
            Radio("Full code, digits only", _prefs.Gs1 == Gs1Output.Compact, () => _prefs.Gs1 = Gs1Output.Compact),
            Radio("Product code (GTIN) only", _prefs.Gs1 == Gs1Output.GtinOnly, () => _prefs.Gs1 = Gs1Output.GtinOnly)));
        if (_cameras.Count > 0)
            menu.Items.Add(Group("Camera", _cameras.Select((c, i) => Radio(c.Name, i == _cameraIndex, () =>
            {
                _prefs.Camera = c.Name;
                _ = StartCameraAsync(i);
            })).ToArray()));
        menu.Items.Add(new Separator());
        var again = new MenuItem { Header = "Type again", IsEnabled = _pending != null };
        again.Click += async (_, _) => await DeliverAsync();
        menu.Items.Add(again);
        if (OperatingSystem.IsMacOS())
        {
            var typing = new MenuItem { Header = "Allow typing (Accessibility)…" };
            typing.Click += (_, _) => KeyTyper.OpenAccessibilitySettings();
            var camera = new MenuItem { Header = "Allow camera…" };
            camera.Click += (_, _) => CameraPermission.OpenSettings();
            menu.Items.Add(typing);
            menu.Items.Add(camera);
        }
        menu.ShowAt(C<Button>("MenuButton"));
    }

    static MenuItem Group(string header, params MenuItem[] items)
    {
        var g = new MenuItem { Header = header };
        foreach (var i in items) g.Items.Add(i);
        return g;
    }

    MenuItem Radio(string header, bool on, Action choose)
    {
        var m = new MenuItem { Header = header, ToggleType = MenuItemToggleType.Radio, IsChecked = on };
        m.Click += (_, _) => { choose(); _prefs.Save(); };
        return m;
    }

    void OnDrag(object? s, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    void OnClose(object? s, RoutedEventArgs e) => Close();
}
