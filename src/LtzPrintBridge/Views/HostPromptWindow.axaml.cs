using Avalonia.Controls;
using Avalonia.Interactivity;
using LtzPrintBridge.Core;

namespace LtzPrintBridge.Views;

public partial class HostPromptWindow : Window
{
    public HostDecision Decision { get; private set; } = HostDecision.NotNow; // closing the window is not a "block forever"
    public HostPromptWindow() { InitializeComponent(); }
    public HostPromptWindow(string host) : this() { this.FindControl<TextBlock>("HostText")!.Text = host; }
    void OnBlock(object? s, RoutedEventArgs e) { Decision = HostDecision.Block; Close(); }
    void OnOnce(object? s, RoutedEventArgs e) { Decision = HostDecision.AllowOnce; Close(); }
    void OnAlways(object? s, RoutedEventArgs e) { Decision = HostDecision.AllowAlways; Close(); }
}
