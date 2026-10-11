using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Html;
using SboxNetworkStorage.Domain.Workspace;

namespace SboxNetworkStorage.Server.Owner;

/// <summary>
/// Deterministic generated project tile: a rounded-square inline SVG with a
/// two-tone geometric pattern and the project's initials. The tile reads on
/// both dark and light themes because it always uses mid-tone fills with a
/// luminance-chosen initial color. No inline styles; all color math is
/// culture-invariant.
/// </summary>
public static class OwnerProjectIcon
{
    // Candidates for the initials. Near-white and near-black (never pure)
    // so one of them always reaches WCAG AA against a mid-tone tile.
    private const string LightInitials = "#f0f6f1";
    private const string DarkInitials = "#0b100d";

    /// <summary>
    /// Renders the project tile. Pass an <paramref name="accessibleName"/>
    /// only when the project name is not adjacent; otherwise the tile is
    /// hidden from assistive technology so the name is announced once.
    /// </summary>
    public static IHtmlContent For(WorkspaceProject project, int size = 40, string? accessibleName = null)
    {
        var id = project.Id ?? string.Empty;
        var name = project.Name ?? string.Empty;
        var hash = Fnv1a(id);
        var tile = TileColor(project.Color, hash, out var hue, out var saturation);
        var pattern = PatternColor(tile, hue, saturation);
        var initials = Initials(name);
        var foreground = InitialColor(tile);

        var svg = new StringBuilder();
        svg.Append(CultureInfo.InvariantCulture, $"<svg class=\"project-tile\" width=\"{size}\" height=\"{size}\" viewBox=\"0 0 48 48\" ");
        if (accessibleName is null)
            svg.Append("aria-hidden=\"true\" focusable=\"false\">");
        else
            svg.Append(CultureInfo.InvariantCulture, $"role=\"img\" aria-label=\"{HtmlEncoder.Default.Encode(accessibleName)}\" focusable=\"false\">");
        var clipId = $"pti-{hash:x8}-{size}";
        svg.Append(CultureInfo.InvariantCulture, $"<defs><clipPath id=\"{clipId}\"><rect width=\"48\" height=\"48\" rx=\"8\" /></clipPath></defs>");
        svg.Append(CultureInfo.InvariantCulture, $"<rect width=\"48\" height=\"48\" rx=\"8\" fill=\"{tile}\" />");
        svg.Append(CultureInfo.InvariantCulture, $"<g clip-path=\"url(#{clipId})\" fill=\"{pattern}\" fill-opacity=\"0.55\">");
        // Two offset discs in opposite corners. Position and radius come from
        // hash bits, so every project id gets its own arrangement while the
        // center stays solid for the initials.
        var (ax, ay, ar) = Disc(hash, 3, 11, 19, upper: true);
        var (bx, by, br) = Disc(hash, 5, 13, 23, upper: false);
        svg.Append(CultureInfo.InvariantCulture, $"<circle cx=\"{ax}\" cy=\"{ay}\" r=\"{ar}\" />");
        svg.Append(CultureInfo.InvariantCulture, $"<circle cx=\"{bx}\" cy=\"{by}\" r=\"{br}\" />");
        svg.Append("</g>");
        svg.Append(CultureInfo.InvariantCulture, $"<text x=\"24\" y=\"24.5\" text-anchor=\"middle\" dominant-baseline=\"central\" font-size=\"17\" font-weight=\"600\" fill=\"{foreground}\">{HtmlEncoder.Default.Encode(initials)}</text>");
        svg.Append("</svg>");
        return new HtmlString(svg.ToString());
    }

    /// <summary>The tile fill for a project id without an owner-chosen color. Also used to prefill the settings color picker.</summary>
    public static string DefaultColor(string projectId)
    {
        var hash = Fnv1a(projectId ?? string.Empty);
        AutoBand(hash, out var hue, out var saturation, out var lightness);
        var (r, g, b) = FromHsl(hue, saturation, lightness);
        return ToHex(r, g, b);
    }

    private static string TileColor(string? color, uint hash, out double hue, out double saturation)
    {
        if (TryParseColor(color, out var r, out var g, out var b))
        {
            ToHsl(r, g, b, out hue, out saturation, out _);
            return ToHex(r, g, b);
        }

        AutoBand(hash, out hue, out saturation, out var lightness);
        (r, g, b) = FromHsl(hue, saturation, lightness);
        return ToHex(r, g, b);
    }

    // Full 0-359 hue from the hash; saturation 42-58% and lightness 38-46%
    // keep the tile mid-tone on both the dark (#0b1512) and light (#f4f6f3) themes.
    private static void AutoBand(uint hash, out double hue, out double saturation, out double lightness)
    {
        hue = hash % 360;
        saturation = 42 + ((hash >> 8) % 17);
        lightness = 38 + ((hash >> 16) % 9);
    }

    private static string PatternColor(string tile, double hue, double saturation)
    {
        TryParseColor(tile, out var r, out var g, out var b);
        ToHsl(r, g, b, out _, out _, out var lightness);
        var shifted = lightness < 50 ? Math.Min(lightness + 16, 88) : Math.Max(lightness - 16, 12);
        (r, g, b) = FromHsl(hue, saturation, shifted);
        return ToHex(r, g, b);
    }

    private static string InitialColor(string tile)
    {
        TryParseColor(tile, out var r, out var g, out var b);
        var tileLuminance = Luminance(r, g, b);
        var lightLuminance = Luminance(0xF0, 0xF6, 0xF1);
        var darkLuminance = Luminance(0x0B, 0x10, 0x0D);
        var light = Contrast(Math.Max(lightLuminance, tileLuminance), Math.Min(lightLuminance, tileLuminance));
        var dark = Contrast(Math.Max(darkLuminance, tileLuminance), Math.Min(darkLuminance, tileLuminance));
        // WCAG AA (4.5:1) when reachable; otherwise the higher of the two.
        if (light >= 4.5 && light >= dark) return LightInitials;
        if (dark >= 4.5) return DarkInitials;
        return light >= dark ? LightInitials : DarkInitials;
    }

    private static (int X, int Y, int R) Disc(uint hash, int xShift, int yShift, int rShift, bool upper)
    {
        var x = (int)((hash >> xShift) % 32);
        var y = (int)((hash >> yShift) % 20);
        var radius = 8 + (int)((hash >> rShift) % 8);
        return upper ? (x, y, radius) : (48 - x, 48 - y, radius);
    }

    private static string Initials(string name)
    {
        var words = name.Split([' ', '-', '_', '.'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length >= 2)
            return string.Concat(char.ToUpperInvariant(words[0][0]), char.ToUpperInvariant(words[1][0]));
        if (words.Length == 1 && words[0].Length >= 2)
            return string.Concat(char.ToUpperInvariant(words[0][0]), char.ToUpperInvariant(words[0][1]));
        if (words.Length == 1 && words[0].Length == 1)
            return words[0].ToUpperInvariant();
        return "P";
    }

    private static uint Fnv1a(string value)
    {
        const uint basis = 2166136261;
        const uint prime = 16777619;
        var hash = basis;
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            hash ^= b;
            hash *= prime;
        }

        return hash;
    }

    private static bool TryParseColor(string? value, out byte r, out byte g, out byte b)
    {
        (r, g, b) = (0, 0, 0);
        if (value is not { Length: 7 } color || color[0] != '#')
            return false;
        try
        {
            r = byte.Parse(color.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            g = byte.Parse(color.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            b = byte.Parse(color.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    // Hue in degrees (0-359), saturation and lightness in percent, matching FromHsl.
    private static void ToHsl(byte r, byte g, byte b, out double h, out double s, out double l)
    {
        var rd = r / 255.0;
        var gd = g / 255.0;
        var bd = b / 255.0;
        var max = Math.Max(rd, Math.Max(gd, bd));
        var min = Math.Min(rd, Math.Min(gd, bd));
        var lightness = (max + min) / 2;
        l = lightness * 100;
        if (max == min)
        {
            h = 0;
            s = 0;
            return;
        }

        var d = max - min;
        s = (lightness > 0.5 ? d / (2 - max - min) : d / (max + min)) * 100;
        h = max switch
        {
            _ when max == rd => ((gd - bd) / d + (gd < bd ? 6 : 0)) * 60,
            _ when max == gd => (((bd - rd) / d) + 2) * 60,
            _ => (((rd - gd) / d) + 4) * 60,
        };
    }

    private static (byte R, byte G, byte B) FromHsl(double h, double s, double l)
    {
        s /= 100;
        l /= 100;
        var c = (1 - Math.Abs(2 * l - 1)) * s;
        var x = c * (1 - Math.Abs(((h / 60) % 2) - 1));
        var m = l - c / 2;
        var (rd, gd, bd) = ((h % 360) / 60) switch
        {
            < 1 => (c, x, 0.0),
            < 2 => (x, c, 0.0),
            < 3 => (0.0, c, x),
            < 4 => (0.0, x, c),
            < 5 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return ((byte)Math.Round((rd + m) * 255), (byte)Math.Round((gd + m) * 255), (byte)Math.Round((bd + m) * 255));
    }

    private static string ToHex(byte r, byte g, byte b)
        => string.Create(CultureInfo.InvariantCulture, $"#{r:x2}{g:x2}{b:x2}");

    private static double Luminance(byte r, byte g, byte b)
    {
        static double Linear(byte c)
        {
            var v = c / 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Linear(r) + 0.7152 * Linear(g) + 0.0722 * Linear(b);
    }

    private static double Contrast(double lighter, double darker) => (lighter + 0.05) / (darker + 0.05);
}
