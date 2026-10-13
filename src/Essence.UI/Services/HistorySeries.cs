using System.Globalization;
using System.Text;
using Essence.Contracts;

namespace Essence.UI.Services;

/// <summary>A price held from <see cref="From"/> to <see cref="To"/> (exclusive).</summary>
public sealed record PriceStep(DateTimeOffset From, DateTimeOffset To, decimal PriceCents);

/// <summary>The steps of one fuel, in time order. Time not covered by a step is a gap (unavailable or not sold).</summary>
public sealed record FuelSeries(string Fuel, IReadOnlyList<PriceStep> Steps)
{
    public PriceStep? At(DateTimeOffset time) => Steps.FirstOrDefault(s => s.From <= time && time < s.To);
}

public sealed record PriceChartData(DateTimeOffset From, DateTimeOffset To, IReadOnlyList<FuelSeries> Series)
{
    public bool IsEmpty => Series.Count == 0;

    public decimal MinPrice => Series.SelectMany(s => s.Steps).Min(s => s.PriceCents);

    public decimal MaxPrice => Series.SelectMany(s => s.Steps).Max(s => s.PriceCents);
}

public static class HistorySeries
{
    /// <summary>
    /// Turns history rows into one series per fuel over [from, to). Rows are clamped to the range, a row still open
    /// (<c>ValidTo</c> null) runs to <paramref name="to"/>, unavailable rows become gaps, and consecutive steps at the
    /// same price are merged. Known fuels come first, in <see cref="Fuels.All"/> order.
    /// When the history starts after <paramref name="from"/> (a station first seen recently), the chart starts with it,
    /// so the data isn't squeezed against the right edge.
    /// </summary>
    public static PriceChartData Build(IEnumerable<PriceHistoryDto> rows, DateTimeOffset from, DateTimeOffset to)
    {
        var series = rows
            .Where(r => r is { IsAvailable: true, PriceCents: not null })
            .Select(r => (r.Fuel, Step: new PriceStep(Max(r.ValidFrom, from), Min(r.ValidTo ?? to, to), r.PriceCents!.Value)))
            .Where(x => x.Step.From < x.Step.To)
            .GroupBy(x => x.Fuel, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => Fuels.IndexOf(g.Key))
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new FuelSeries(g.Key, MergeSteps(g.Select(x => x.Step).OrderBy(s => s.From))))
            .ToList();
        var start = series.Count == 0 ? from : series.Min(s => s.Steps[0].From);
        return new PriceChartData(start, to, series);
    }

    private static List<PriceStep> MergeSteps(IEnumerable<PriceStep> steps)
    {
        var merged = new List<PriceStep>();
        foreach (var step in steps)
        {
            if (merged.Count > 0 && merged[^1] is var last && last.To == step.From && last.PriceCents == step.PriceCents)
            {
                merged[^1] = last with { To = step.To };
            }
            else
            {
                merged.Add(step);
            }
        }

        return merged;
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}

/// <summary>Maps chart data to SVG coordinates: time on x over the whole range, price on y with some headroom.</summary>
public sealed class ChartScale
{
    /// <summary>The y axis spans at least this many cents, so a flat line doesn't fill the chart top to bottom.</summary>
    private const decimal MinSpanCents = 4;

    private readonly PriceChartData _data;
    private readonly double _width;
    private readonly double _top;
    private readonly double _plotHeight;

    public ChartScale(PriceChartData data, double width, double height, double padTop, double padBottom)
    {
        _data = data;
        _width = width;
        _top = padTop;
        _plotHeight = height - padTop - padBottom;
        if (data.IsEmpty)
        {
            (YMin, YMax) = (0, 1);
            return;
        }

        var (min, max) = (data.MinPrice, data.MaxPrice);
        var span = Math.Max(max - min, MinSpanCents);
        var middle = (min + max) / 2;
        (YMin, YMax) = (Math.Floor(middle - span * 0.6m), Math.Ceiling(middle + span * 0.6m));
    }

    public decimal YMin { get; }

    public decimal YMax { get; }

    public double X(DateTimeOffset time) =>
        (time - _data.From).TotalSeconds / (_data.To - _data.From).TotalSeconds * _width;

    public double Y(decimal priceCents) => _top + (double)((YMax - priceCents) / (YMax - YMin)) * _plotHeight;

    /// <summary>SVG path of a stepped line: horizontal runs, joined vertically when a step starts where the previous one ends.</summary>
    public string Path(FuelSeries series)
    {
        var path = new StringBuilder();
        PriceStep? previous = null;
        foreach (var step in series.Steps)
        {
            var y = Y(step.PriceCents);
            if (previous is not null && previous.To == step.From)
            {
                path.Append(CultureInfo.InvariantCulture, $"V{y:0.##}");
            }
            else
            {
                path.Append(CultureInfo.InvariantCulture, $"M{X(step.From):0.##} {y:0.##}");
            }

            path.Append(CultureInfo.InvariantCulture, $"H{X(step.To):0.##}");
            previous = step;
        }

        return path.ToString();
    }
}
