namespace LtzPrintBridge.Core;

/// <summary>Picks the right preview for the label commands in a job. The kind comes from the commands themselves, not from the printer setting.</summary>
public static class LabelPreview
{
    /// <summary>Largest quantity the preview window writes into a label.</summary>
    public const int MaxQuantity = 9999;

    /// <summary>ZPL, TSPL or EPL when the text is clearly one of them, otherwise null.</summary>
    public static PrintLanguage? Detect(string text)
    {
        if (ZplPreview.LooksLikeZpl(text)) return PrintLanguage.Zpl;
        if (LinePreview.LooksLikeTspl(text)) return PrintLanguage.Tspl;
        if (LinePreview.LooksLikeEpl(text)) return PrintLanguage.Epl;
        return null;
    }

    public static List<string> Split(PrintLanguage kind, string text) => kind switch
    {
        PrintLanguage.Zpl => ZplPreview.SplitLabels(text),
        PrintLanguage.Tspl => LinePreview.SplitTspl(text),
        PrintLanguage.Epl => LinePreview.SplitEpl(text),
        _ => new()
    };

    public static int Quantity(PrintLanguage kind, string label) => kind switch
    {
        PrintLanguage.Zpl => ZplPreview.QuantityOf(label),
        PrintLanguage.Tspl => LinePreview.TsplQuantity(label),
        PrintLanguage.Epl => LinePreview.EplQuantity(label),
        _ => 1
    };

    /// <summary>Rewrites the job so the printer itself makes this many copies of every label.</summary>
    public static string WithQuantity(PrintLanguage kind, string text, int q) => kind switch
    {
        PrintLanguage.Zpl => ZplPreview.WithQuantity(text, q),
        PrintLanguage.Tspl => LinePreview.TsplWithQuantity(text, q),
        PrintLanguage.Epl => LinePreview.EplWithQuantity(text, q),
        _ => text
    };

    public static PreviewImage? Render(PrintLanguage kind, string label) => kind switch
    {
        PrintLanguage.Zpl => ZplPreview.Render(label),
        PrintLanguage.Tspl => LinePreview.RenderTspl(label),
        PrintLanguage.Epl => LinePreview.RenderEpl(label),
        _ => null
    };
}
