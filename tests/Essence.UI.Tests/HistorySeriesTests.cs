using Essence.Contracts;
using Essence.UI.Services;

namespace Essence.UI.Tests;

public class HistorySeriesTests
{
    private static readonly DateTimeOffset From = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = From.AddDays(10);

    [Fact]
    public void Rows_are_clamped_to_the_range_and_open_rows_run_to_the_end()
    {
        var data = HistorySeries.Build(
            [
                Row("Régulier", 160m, From.AddDays(-5), From.AddDays(2)),
                Row("Régulier", 158m, From.AddDays(2), null),
            ],
            From,
            To);

        var steps = Assert.Single(data.Series).Steps;
        Assert.Equal(new PriceStep(From, From.AddDays(2), 160m), steps[0]);
        Assert.Equal(new PriceStep(From.AddDays(2), To, 158m), steps[1]);
    }

    [Fact]
    public void Unavailable_rows_become_gaps()
    {
        var data = HistorySeries.Build(
            [
                Row("Diesel", 180m, From, From.AddDays(1)),
                new PriceHistoryDto("Diesel", null, false, From.AddDays(1), From.AddDays(2)),
                Row("Diesel", 181m, From.AddDays(2), null),
            ],
            From,
            To);

        var series = Assert.Single(data.Series);
        Assert.Equal(2, series.Steps.Count);
        Assert.Null(series.At(From.AddDays(1.5)));
        Assert.Equal(181m, series.At(From.AddDays(3))!.PriceCents);
    }

    [Fact]
    public void Contiguous_steps_at_the_same_price_are_merged()
    {
        var data = HistorySeries.Build(
            [Row("Super", 175m, From, From.AddDays(1)), Row("Super", 175m, From.AddDays(1), null)],
            From,
            To);

        Assert.Equal([new PriceStep(From, To, 175m)], data.Series[0].Steps);
    }

    [Fact]
    public void Known_fuels_come_first_in_their_usual_order()
    {
        var data = HistorySeries.Build(
            [Row("Diesel", 180m, From, null), Row("Propane", 90m, From, null), Row("Régulier", 160m, From, null)],
            From,
            To);

        Assert.Equal(["Régulier", "Diesel", "Propane"], data.Series.Select(s => s.Fuel));
        Assert.Equal(90m, data.MinPrice);
        Assert.Equal(180m, data.MaxPrice);
    }

    [Fact]
    public void Chart_starts_at_the_first_price_when_history_is_shorter_than_the_range()
    {
        var data = HistorySeries.Build(
            [Row("Régulier", 160m, From.AddDays(8), null), Row("Diesel", 180m, From.AddDays(9), null)],
            From,
            To);

        Assert.Equal(From.AddDays(8), data.From);
        Assert.Equal(To, data.To);
    }

    [Fact]
    public void Rows_outside_the_range_are_dropped()
    {
        var data = HistorySeries.Build([Row("Régulier", 160m, From.AddDays(-9), From.AddDays(-1))], From, To);
        Assert.True(data.IsEmpty);
    }

    [Fact]
    public void Path_joins_contiguous_steps_and_breaks_at_gaps()
    {
        var series = new FuelSeries("Régulier", [
            new PriceStep(From, From.AddDays(5), 160m),
            new PriceStep(From.AddDays(5), From.AddDays(6), 150m),
            new PriceStep(From.AddDays(8), To, 155m),
        ]);
        var scale = new ChartScale(new PriceChartData(From, To, [series]), width: 100, height: 60, padTop: 0, padBottom: 0);

        var path = scale.Path(series);

        Assert.StartsWith("M0 ", path);
        Assert.Equal(2, path.Count(c => c == 'M')); // one break, at the gap
        Assert.Contains("V", path);
        Assert.EndsWith("H100", path);
    }

    [Fact]
    public void Y_axis_has_headroom_around_a_flat_line()
    {
        var series = new FuelSeries("Régulier", [new PriceStep(From, To, 160m)]);
        var scale = new ChartScale(new PriceChartData(From, To, [series]), 100, 60, 0, 0);

        Assert.True(scale.YMin < 160m && scale.YMax > 160m);
        Assert.InRange(scale.Y(160m), 1, 59);
    }

    private static PriceHistoryDto Row(string fuel, decimal price, DateTimeOffset from, DateTimeOffset? to) =>
        new(fuel, price, true, from, to);
}
