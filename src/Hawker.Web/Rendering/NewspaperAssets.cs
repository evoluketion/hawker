using System.Reflection;
using System.Text;

namespace Hawker.Web.Rendering;

/// <summary>
/// The newspaper stylesheet and fonts, embedded in the assembly and inlined into the page so rendering needs no network
/// or file access and looks identical on every machine.
/// </summary>
public static class NewspaperAssets
{
    private static readonly (string Family, string File, string Style, string Weight)[] Fonts =
    [
        ("Source Serif 4", "SourceSerif4.ttf", "normal", "200 900"),
        ("Source Serif 4", "SourceSerif4-Italic.ttf", "italic", "200 900"),
        ("Playfair Display", "PlayfairDisplay.ttf", "normal", "400 900"),
        ("Playfair Display", "PlayfairDisplay-Italic.ttf", "italic", "400 900"),
        ("Libre Franklin", "LibreFranklin.ttf", "normal", "100 900"),
    ];

    private static readonly Lazy<string> LazyStylesheet = new(() =>
    {
        var css = new StringBuilder();
        foreach (var (family, file, style, weight) in Fonts)
        {
            var base64 = Convert.ToBase64String(ReadResource($"Fonts.{file}"));
            css.Append($"@font-face{{font-family:\"{family}\";src:url(data:font/ttf;base64,{base64}) format(\"truetype\");font-style:{style};font-weight:{weight};}}\n");
        }

        css.Append(Encoding.UTF8.GetString(ReadResource("newspaper.css")));
        return css.ToString();
    });

    public static string Stylesheet => LazyStylesheet.Value;

    private static byte[] ReadResource(string name)
    {
        var assembly = typeof(NewspaperAssets).Assembly;
        var resourceName = $"Hawker.Web.Rendering.{name}";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing embedded resource {resourceName}.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
