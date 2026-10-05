using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Html;

namespace Mailserver.Web;

/// <summary>
/// Small charts rendered on the server as inline SVG (no JavaScript, allowed by the content security policy). Colours come
/// from the stylesheet (series classes s1…s6), so they follow the light and dark design.
/// </summary>
public static class Charts
{
    private static int _ids;

    public sealed record Series(string Name, IReadOnlyList<int> Values, int Color);

    /// <summary>Tiny trend line without axes, e.g. in a key figure tile.</summary>
    public static IHtmlContent Sparkline(IReadOnlyList<int> values, int color = 1)
    {
        const double width = 72, height = 26;
        if (values.Count < 2)
        {
            return HtmlString.Empty;
        }

        var max = Math.Max(1, values.Max());
        var points = values.Select((v, i) => (X: i * width / (values.Count - 1), Y: height - 2 - v * (height - 5) / max)).ToList();
        var id = NextId();
        var line = Smooth(points);
        var svg = new StringBuilder();
        svg.Append($"""<svg class="chart spark" viewBox="0 0 {N(width)} {N(height)}" preserveAspectRatio="none" aria-hidden="true">""");
        svg.Append($"""<defs><linearGradient id="g{id}" x1="0" y1="0" x2="0" y2="1"><stop offset="0" class="stop{color}" stop-opacity=".45"/><stop offset="1" class="stop{color}" stop-opacity="0"/></linearGradient></defs>""");
        svg.Append($"""<path d="{line} L{N(width)},{N(height)} L0,{N(height)} Z" fill="url(#g{id})"/>""");
        svg.Append($"""<path d="{line}" class="line s{color}"/></svg>""");
        return new HtmlString(svg.ToString());
    }

    /// <summary>Line chart with filled areas, grid and axis labels.</summary>
    public static IHtmlContent Area(IReadOnlyList<string> labels, params Series[] series)
    {
        // Sized for a panel about 350 px wide, so the labels keep a readable size; wider panels scale it up.
        const double width = 360, height = 190, left = 30, right = 8, top = 10, bottom = 22;
        var plotWidth = width - left - right;
        var plotHeight = height - top - bottom;
        var max = NiceMax(series.SelectMany(s => s.Values).DefaultIfEmpty(0).Max());
        var count = Math.Max(2, labels.Count);
        double X(int i) => left + i * plotWidth / (count - 1);
        double Y(int v) => top + plotHeight - v * plotHeight / max;

        var svg = new StringBuilder();
        svg.Append($"""<svg class="chart" viewBox="0 0 {N(width)} {N(height)}" role="img" aria-label="{WebUtility.HtmlEncode(string.Join(", ", series.Select(s => s.Name)))}">""");
        svg.Append("<defs>");
        var ids = series.Select(_ => NextId()).ToList();
        for (var s = 0; s < series.Length; s++)
        {
            svg.Append($"""<linearGradient id="g{ids[s]}" x1="0" y1="0" x2="0" y2="1"><stop offset="0" class="stop{series[s].Color}" stop-opacity=".38"/><stop offset="1" class="stop{series[s].Color}" stop-opacity="0"/></linearGradient>""");
        }

        svg.Append("</defs>");
        for (var g = 0; g <= 4; g++)
        {
            var value = max * g / 4;
            var y = Y(value);
            svg.Append($"""<line class="grid" x1="{N(left)}" x2="{N(width - right)}" y1="{N(y)}" y2="{N(y)}"/>""");
            svg.Append($"""<text x="{N(left - 8)}" y="{N(y + 4)}" text-anchor="end">{Compact(value)}</text>""");
        }

        var step = labels.Count > 10 ? 3 : labels.Count > 6 ? 2 : 1;
        for (var i = 0; i < labels.Count; i += step)
        {
            svg.Append($"""<text x="{N(X(i))}" y="{N(height - 6)}" text-anchor="middle">{WebUtility.HtmlEncode(labels[i])}</text>""");
        }

        for (var s = 0; s < series.Length; s++)
        {
            var points = series[s].Values.Select((v, i) => (X: X(i), Y: Y(v))).ToList();
            if (points.Count < 2)
            {
                continue;
            }

            var line = Smooth(points);
            svg.Append($"""<path d="{line} L{N(points[^1].X)},{N(top + plotHeight)} L{N(points[0].X)},{N(top + plotHeight)} Z" fill="url(#g{ids[s]})"/>""");
            svg.Append($"""<path d="{line}" class="line s{series[s].Color}"/>""");
            var last = points[^1];
            svg.Append($"""<circle class="dot f{series[s].Color}" cx="{N(last.X)}" cy="{N(last.Y)}" r="4"/>""");
        }

        svg.Append("</svg>");
        return new HtmlString(svg.ToString());
    }

    /// <summary>Ring chart with a figure in the middle.</summary>
    public static IHtmlContent Donut(IReadOnlyList<(string Label, int Value, int Color)> parts, string center, string centerLabel)
    {
        const double radius = 40, stroke = 12;
        var circumference = 2 * Math.PI * radius;
        var total = parts.Sum(p => p.Value);
        var svg = new StringBuilder();
        svg.Append($"""<svg class="chart donut" viewBox="0 0 100 100" role="img" aria-label="{WebUtility.HtmlEncode(centerLabel)}">""");
        svg.Append($"""<circle class="track" cx="50" cy="50" r="{N(radius)}" fill="none" stroke-width="{N(stroke)}"/>""");
        var offset = 0.0;
        var gap = parts.Count(p => p.Value > 0) > 1 ? 1.5 : 0;
        foreach (var part in parts.Where(p => p.Value > 0))
        {
            var length = part.Value * circumference / total;
            var visible = Math.Max(0.5, length - gap);
            svg.Append($"""<circle class="s{part.Color}" cx="50" cy="50" r="{N(radius)}" fill="none" stroke-width="{N(stroke)}" stroke-linecap="butt" """ +
                       $"""stroke-dasharray="{N(visible)} {N(circumference - visible)}" stroke-dashoffset="{N(-offset)}" transform="rotate(-90 50 50)"/>""");
            offset += length;
        }

        svg.Append($"""<text class="center" x="50" y="53" text-anchor="middle">{WebUtility.HtmlEncode(center)}</text>""");
        svg.Append($"""<text class="center-label" x="50" y="64" text-anchor="middle">{WebUtility.HtmlEncode(centerLabel)}</text></svg>""");
        return new HtmlString(svg.ToString());
    }

    /// <summary>Progress ring 0–100 % with the percentage inside.</summary>
    public static IHtmlContent Ring(int percent, int color, string text)
    {
        const double radius = 26;
        var circumference = 2 * Math.PI * radius;
        var length = Math.Clamp(percent, 0, 100) * circumference / 100;
        return new HtmlString(
            $"""<svg class="ring" viewBox="0 0 62 62" aria-hidden="true"><circle class="track" cx="31" cy="31" r="{N(radius)}" fill="none" stroke-width="6"/>""" +
            $"""<circle class="s{color}" cx="31" cy="31" r="{N(radius)}" fill="none" stroke-width="6" stroke-linecap="round" stroke-dasharray="{N(length)} {N(circumference)}" transform="rotate(-90 31 31)"/>""" +
            $"""<text x="31" y="36" text-anchor="middle">{WebUtility.HtmlEncode(text)}</text></svg>""");
    }

    /// <summary>Compact number for axes and tiles: 950, 1,2K, 15K, 1,1 Mio.</summary>
    public static string Compact(long value) => value switch
    {
        < 1000 => value.ToString(CultureInfo.InvariantCulture),
        < 10_000 => (value / 1000.0).ToString("0.#", CultureInfo.InvariantCulture).Replace('.', ',') + "K",
        < 1_000_000 => (value / 1000).ToString(CultureInfo.InvariantCulture) + "K",
        _ => (value / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture).Replace('.', ',') + " Mio.",
    };

    private static int NiceMax(int max)
    {
        if (max <= 4)
        {
            return 4;
        }

        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(max)));
        foreach (var factor in new[] { 1, 1.2, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10 })
        {
            var candidate = factor * magnitude;
            if (candidate >= max && candidate % 4 == 0)
            {
                return (int)candidate;
            }
        }

        return (int)(Math.Ceiling(max / 4.0) * 4);
    }

    /// <summary>A smooth curve through the points (Catmull-Rom as cubic Bézier), clamped so it does not overshoot the axis.</summary>
    private static string Smooth(IReadOnlyList<(double X, double Y)> points)
    {
        var maxY = points.Max(p => p.Y);
        var minY = points.Min(p => p.Y);
        var path = new StringBuilder($"M{N(points[0].X)},{N(points[0].Y)}");
        for (var i = 0; i < points.Count - 1; i++)
        {
            var p0 = points[Math.Max(0, i - 1)];
            var p1 = points[i];
            var p2 = points[i + 1];
            var p3 = points[Math.Min(points.Count - 1, i + 2)];
            var c1 = (X: p1.X + (p2.X - p0.X) / 6, Y: Math.Clamp(p1.Y + (p2.Y - p0.Y) / 6, minY, maxY));
            var c2 = (X: p2.X - (p3.X - p1.X) / 6, Y: Math.Clamp(p2.Y - (p3.Y - p1.Y) / 6, minY, maxY));
            path.Append($" C{N(c1.X)},{N(c1.Y)} {N(c2.X)},{N(c2.Y)} {N(p2.X)},{N(p2.Y)}");
        }

        return path.ToString();
    }

    private static int NextId() => Interlocked.Increment(ref _ids);

    private static string N(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
