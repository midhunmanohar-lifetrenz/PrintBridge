using Avalonia.Controls;
using Avalonia.Interactivity;
using LtzPrintBridge.Core;

namespace LtzPrintBridge.Views;

public partial class AddPrinterWindow : Window
{
    public ManualPrinter? Result { get; private set; }
    public AddPrinterWindow()
    {
        InitializeComponent();
        this.FindControl<ComboBox>("VendorBox")!.ItemsSource = new[] { "Zebra", "TSC", "TVS / SNBC", "Other" };
        this.FindControl<ComboBox>("VendorBox")!.SelectedIndex = 0;
        this.FindControl<ComboBox>("LangBox")!.ItemsSource = Languages.Choices;
        this.FindControl<ComboBox>("LangBox")!.SelectedIndex = 0;
        this.FindControl<ComboBox>("VendorBox")!.SelectionChanged += (_, _) =>
        {
            var v = this.FindControl<ComboBox>("VendorBox")!.SelectedItem as string;
            this.FindControl<ComboBox>("LangBox")!.SelectedItem = v switch { "TSC" => PrintLanguage.Tspl, "TVS / SNBC" => PrintLanguage.Bple, _ => PrintLanguage.Zpl };
        };
    }
    void OnCancel(object? s, RoutedEventArgs e) => Close();
    void OnAdd(object? s, RoutedEventArgs e)
    {
        var name = this.FindControl<TextBox>("NameBox")!.Text?.Trim() ?? ""; var host = this.FindControl<TextBox>("HostBox")!.Text?.Trim() ?? "";
        var err = this.FindControl<TextBlock>("Err")!;
        if (host.Length == 0 || !int.TryParse(this.FindControl<TextBox>("PortBox")!.Text, out var port) || port is < 1 or > 65535)
        { err.Text = "Enter the printer's IP address and a valid port."; err.IsVisible = true; return; }
        Result = new ManualPrinter
        {
            Name = name.Length == 0 ? $"Network printer {host}" : name, Host = host, Port = port,
            Vendor = this.FindControl<ComboBox>("VendorBox")!.SelectedItem as string ?? "Other",
            Language = this.FindControl<ComboBox>("LangBox")!.SelectedItem is PrintLanguage l ? l : PrintLanguage.Zpl
        };
        Close();
    }
}
