using System.IO;
using System.Net;
using System.Runtime.InteropServices;

using Eto.Forms;
using Rhino.AI.WebPanel;

namespace Rhino.AI.UI;

[Guid("8b60b4e1-e98e-4991-9c09-ed2966a5be95")]
public class NewWebPanel : Panel
{

    private WebView View { get; }

    private NewWebPanelViewModel Model => (DataContext as NewWebPanelViewModel)!;

    public NewWebPanel() : this(RhinoDoc.ActiveDoc?.RuntimeSerialNumber ?? 0U)
    {

    }

    public NewWebPanel(uint documentSerialNumber)
    {
        Content = View = new();
        DataContext = new NewWebPanelViewModel(View, documentSerialNumber);
        LoadUI();
    }

    private void LoadUI()
    {
        Stream? htmlStream = typeof(NewWebPanel).Assembly.GetManifestResourceStream("Rhino.AI.panel.html");
        using StreamReader reader = new(htmlStream!);
        string html = reader.ReadToEnd();
        View.LoadHtml(html);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
    }

    private void Init(HttpListener listener)
    {
        // listener.GetContextAsync()
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        PanelTheme.Rgb Read(Eto.Drawing.Color c) => new(c.R, c.G, c.B);
        PanelTheme.Rgb Convert(System.Drawing.Color c) => new(c.R / 255f, c.G / 255f, c.B / 255f);
        PanelTheme.Rgb Paint(Rhino.ApplicationSettings.PaintColor which) =>
            Convert(Rhino.ApplicationSettings.AppearanceSettings.GetPaintColor(which));

        // Rhino's own paint palette, which is exactly what it paints panels with: Panels.SetBackColor
        // assigns the host's BackColor from GetPaintColor(PaintColor.PanelBackground). Eto's
        // SystemColors cannot answer this, because its handlers disagree across platforms about which
        // constant carries the panel tone, and neither of them is Rhino's themed value anyway.
        //
        // Only the four Rhino has no equivalent for still come from Eto.
        bool windows = !Environment.OSVersion.Platform.Equals(PlatformID.Unix);
        PanelTheme.Palette palette = new(
            Chrome: Paint(Rhino.ApplicationSettings.PaintColor.PanelBackground),
            Field: Read(Rhino.UI.ThemeSettings.Content.List.Enabled.Background),
            Text: Paint(Rhino.ApplicationSettings.PaintColor.TextEnabled),
            Dim: Paint(Rhino.ApplicationSettings.PaintColor.TextDisabled),
            Border: Paint(Rhino.ApplicationSettings.PaintColor.GridLinesOnPanelBackground),
            Accent: Read(Eto.Drawing.SystemColors.Highlight),
            AccentText: Read(Eto.Drawing.SystemColors.HighlightText),
            // Rhino's own hyperlink colour. Eto's LinkText is no use here: its Windows handler maps
            // it to the selection highlight.
            Link: Convert(Rhino.ApplicationSettings.AppearanceSettings.CommandPromptHypertextColor),
            Selection: Read(Eto.Drawing.SystemColors.Selection),
            SelectionText: Read(Eto.Drawing.SystemColors.SelectionText));

        Dictionary<string, string> tokens = PanelTheme.Tokens(palette);

        // Rhino themes Eto, so its default UI font is the one every other Rhino panel uses.
        Eto.Drawing.Font font = Eto.Drawing.SystemFonts.Default();
        foreach (KeyValuePair<string, string> entry in PanelTheme.Fonts(font.FamilyName, font.Size, windows))
            tokens[entry.Key] = entry.Value;

        string scheme = PanelTheme.IsDarkTheme(palette) ? "dark" : "light";
        string fingerprint = scheme + string.Join(";", tokens.OrderBy(t => t.Key).Select(t => $"{t.Key}={t.Value}"));

        Model.Bridge.Post(new ThemeEvent(scheme, tokens));
    }

    protected override void OnUnLoad(EventArgs e)
    {
        base.OnUnLoad(e);
    }

}
