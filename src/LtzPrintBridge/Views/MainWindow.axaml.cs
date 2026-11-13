using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using System.Linq;
using LtzPrintBridge.Core;
using LtzPrintBridge.ViewModels;

namespace LtzPrintBridge.Views;

public partial class MainWindow : Window
{
    MainViewModel VM => (MainViewModel)DataContext!;
    public MainWindow()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Adapt();
        // Never open larger than the screen's usable area (laptops with 125-150% scaling), and keep the window on screen.
        Opened += (_, _) =>
        {
            if (Screens.ScreenFromVisual(this) is not { } sc) return;
            var area = sc.WorkingArea; var scale = sc.Scaling <= 0 ? 1 : sc.Scaling;
            double maxW = area.Width / scale * 0.96, maxH = area.Height / scale * 0.94;
            if (Width > maxW) Width = Math.Max(MinWidth, maxW);
            if (Height > maxH) Height = Math.Max(MinHeight, maxH);
        }; DataContextChanged += (_, _) => { if (DataContext is MainViewModel vm) { vm.SelectionChanged += UpdatePreview; UpdatePreview(); } };
    }

    /// <summary>Below about 1000 px wide the printer list and its detail panel stack instead of sitting side by side, so nothing is squeezed or cut off.</summary>
    void Adapt()
    {
        if (this.FindControl<Grid>("PrinterSplit") is not { } g || this.FindControl<ListBox>("PrinterList") is not { } list || this.FindControl<ScrollViewer>("PrinterDetail") is not { } detail) return;
        bool narrow = Bounds.Width < 1000;
        g.ColumnDefinitions = narrow ? new ColumnDefinitions("*") : new ColumnDefinitions("*,370");
        g.RowDefinitions = narrow ? new RowDefinitions("Auto,Auto") : new RowDefinitions();
        Grid.SetColumn(detail, narrow ? 0 : 1); Grid.SetRow(detail, narrow ? 1 : 0);
        list.Margin = narrow ? new Thickness(0, 0, 0, 12) : new Thickness(0, 0, 16, 0);
    }

    void OnNav(object? s, RoutedEventArgs e) { if (s is RadioButton r && int.TryParse(r.Tag?.ToString(), out var i)) { VM.Page = i; if (i == 3) _ = VM.RunDiagnostics(); } }
    async void OnRescan(object? s, RoutedEventArgs e) => await VM.Rescan();
    async void OnAddPrinter(object? s, RoutedEventArgs e)
    {
        var w = new AddPrinterWindow(); await w.ShowDialog(this);
        if (w.Result != null) await VM.AddManual(w.Result);
    }
    async void OnTest(object? s, RoutedEventArgs e) => await VM.PrintTest();
    async void OnSendRaw(object? s, RoutedEventArgs e) => await VM.SendRaw(VM.RawText);
    void OnDefault(object? s, RoutedEventArgs e) => VM.MakeDefault();
    async void OnRemovePrinter(object? s, RoutedEventArgs e) => await VM.RemoveSelected();
    void OnAllowHost(object? s, RoutedEventArgs e) => VM.AllowHost();
    void OnBlockHost(object? s, RoutedEventArgs e) { if ((s as Control)?.DataContext is HostRow h) VM.Gate.Block(h.Host); }
    void OnRemoveHost(object? s, RoutedEventArgs e) { if ((s as Control)?.DataContext is HostRow h) VM.Gate.Remove(h.Host); }
    void OnUnblockHost(object? s, RoutedEventArgs e) { if ((s as Control)?.DataContext is string h) VM.Gate.Accept(h); }
    async void OnRunDiag(object? s, RoutedEventArgs e) { await VM.RunDiagnostics(); VM.Toast("Checks finished"); }
    async void OnInstallCert(object? s, RoutedEventArgs e) => await VM.InstallCertificate();
    void OnOpenTest(object? s, RoutedEventArgs e) => VM.OpenUrl($"http://127.0.0.1:{VM.Store.Value.HttpPort}/test");
    void OnOpenFolder(object? s, RoutedEventArgs e) => VM.OpenFolder();
    async void OnResetPermissions(object? s, RoutedEventArgs e) => await VM.ResetPermissions();
    async void OnResetAll(object? s, RoutedEventArgs e) => await VM.ResetAllSettings();
    void OnClearRequests(object? s, RoutedEventArgs e) { VM.Requests.Clear(); }
    void OnQuit(object? s, RoutedEventArgs e) => VM.Quit();

    void UpdatePreview()
    {
        var bars = this.FindControl<StackPanel>("Bars"); var v = this.FindControl<TextBlock>("LblVendor");
        if (bars == null || v == null) return;
        var p = VM.SelectedPrinter; v.Text = (p?.Vendor ?? "").ToUpperInvariant();
        bars.Children.Clear();
        uint seed = (uint)((p?.Name ?? "x").GetHashCode() * 9301 + 49297);
        for (int i = 0; i < 44; i++)
        {
            seed = (seed * 9301 + 49297) % 233280;
            double w = 1 + Math.Floor(seed / 233280.0 * 3.4);
            bars.Children.Add(new Avalonia.Controls.Shapes.Rectangle { Width = w, Height = 40, Fill = i % 2 == 0 ? new SolidColorBrush(Color.Parse("#101418")) : Brushes.Transparent, Margin = new Avalonia.Thickness(0) });
        }
    }
}
