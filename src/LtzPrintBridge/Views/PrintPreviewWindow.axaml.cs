using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using LtzPrintBridge.Core;

namespace LtzPrintBridge.Views;

public partial class PrintPreviewWindow : Window
{
    public bool Print { get; private set; }
    public int CopiesChosen => (int)(this.FindControl<NumericUpDown>("Copies")!.Value ?? 1);
    public bool SkipRestChosen => this.FindControl<CheckBox>("SkipRest")!.IsChecked == true;

    public PrintPreviewWindow() { InitializeComponent(); }

    /// <param name="images">One entry per label in the job; null where a label could not be drawn.</param>
    public PrintPreviewWindow(JobManager.PrintReview r, IReadOnlyList<PreviewImage?> images, int totalLabels) : this()
    {
        this.FindControl<TextBlock>("Who")!.Text = $"{r.Site} wants to print on {r.Printer}.";
        var copies = this.FindControl<NumericUpDown>("Copies")!;
        copies.Maximum = Math.Max(r.Copies, r.PerLabel ? LabelPreview.MaxQuantity : JobManager.MaxRepeats);
        copies.Value = Math.Max(1, r.Copies);
        var host = this.FindControl<StackPanel>("Labels")!;
        var note = this.FindControl<TextBlock>("Note")!;

        if (images.Any(i => i != null))
        {
            foreach (var img in images.Where(i => i != null))
            {
                var bmp = new Bitmap(new MemoryStream(img!.Png));
                // Show the label at the size it will print: 8 dots per millimetre on a 203 dpi printer.
                host.Children.Add(new Border
                {
                    Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.Parse("#C9D2D7")), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Center,
                    Child = new Image { Source = bmp, Stretch = Stretch.Uniform, MaxWidth = 480, MaxHeight = 300, Width = Math.Min(480, img.Width * 0.8) }
                });
            }
            var more = totalLabels - images.Count;
            note.Text = (totalLabels == 1 ? "1 label in this job." : $"{totalLabels} labels in this job" + (more > 0 ? $" ({more} more not shown)." : "."))
                        + " The preview is an approximation. The printer may differ slightly in fonts and spacing.";
        }
        else
        {
            var text = r.Text.Length > 3000 ? r.Text[..3000] + "\n..." : r.Text;
            host.Children.Add(new TextBlock { Text = text, FontFamily = new FontFamily("Consolas, Menlo, monospace"), FontSize = 12, TextWrapping = TextWrapping.Wrap });
            note.Text = $"A picture is shown for ZPL, TSPL and EPL labels. This job is in {r.Language} (or could not be drawn), so its printer commands are shown instead.";
        }

        this.FindControl<TextBlock>("CopiesLabel")!.Text = r.PerLabel ? "Copies of each label" : "Copies";
        this.FindControl<TextBlock>("CopiesHint")!.Text = r.PerLabel ? "The printer makes this many of every label" : "The whole job is sent this many times";
        Opened += (_, _) => { Activate(); this.FindControl<Button>("PrintButton")!.Focus(); };
    }

    void OnPrint(object? s, RoutedEventArgs e) { Print = true; Close(); }
    void OnCancel(object? s, RoutedEventArgs e) { Print = false; Close(); }
}
