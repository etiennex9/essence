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

        Assert.Equal(["Régulier", "Diesel", "Propane"], data.Series.Select(s => s.Key));
        Assert.Equal([0, 2, 3], data.Series.Select(s => s.Color));
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
        var series = new PriceSeries("Régulier", "Régulier", 0, [
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
        var series = new PriceSeries("Régulier", "Régulier", 0, [new PriceStep(From, To, 160m)]);
        var scale = new ChartScale(new PriceChartData(From, To, [series]), 100, 60, 0, 0);

        Assert.True(scale.YMin < 160m && scale.YMax > 160m);
        Assert.InRange(scale.Y(160m), 1, 59);
    }

    [Fact]
    public void Compare_draws_one_series_per_station_for_the_fuel()
    {
        var data = HistorySeries.Compare(
            [
                new SeriesSource("7", "Shell", 3, [Row("Régulier", 160m, From, null), Row("Diesel", 180m, From, null)]),
                new SeriesSource("9", "Esso", 0, [Row("régulier", 158m, From.AddDays(1), null)]),
                new SeriesSource("12", "Costco", 1, [Row("Diesel", 175m, From, null)]),
            ],
            "Régulier",
            From,
            To);

        Assert.Equal(["7", "9"], data.Series.Select(s => s.Key));
        Assert.Equal(["Shell", "Esso"], data.Series.Select(s => s.Label));
        Assert.Equal([3, 0], data.Series.Select(s => s.Color));
        Assert.Equal([new PriceStep(From, To, 160m)], data.Series[0].Steps);
        Assert.Equal(158m, data.MinPrice);
    }

    [Fact]
    public void Compare_starts_at_the_first_price_of_any_station()
    {
        var data = HistorySeries.Compare(
            [
                new SeriesSource("1", "A", 0, [Row("Super", 175m, From.AddDays(6), null)]),
                new SeriesSource("2", "B", 1, [Row("Super", 176m, From.AddDays(4), null)]),
            ],
            "Super",
            From,
            To);

        Assert.Equal(From.AddDays(4), data.From);
    }

    [Fact]
    public void Current_is_the_open_row_of_the_fuel()
    {
        PriceHistoryDto[] rows =
        [
            Row("Régulier", 160m, From, From.AddDays(2)),
            Row("Régulier", 158m, From.AddDays(2), null),
            new("Diesel", null, false, From.AddDays(3), null),
        ];

        Assert.Equal(new CurrentPriceDto("Régulier", 158m, true, From.AddDays(2)), HistorySeries.Current(rows, "régulier"));
        Assert.Equal(new CurrentPriceDto("Diesel", null, false, From.AddDays(3)), HistorySeries.Current(rows, "Diesel"));
        Assert.Null(HistorySeries.Current(rows, "Super"));
    }

    [Fact]
    public void Only_keeps_the_chosen_series_as_they_are()
    {
        var chart = HistorySeries.Build(
            [Row("Régulier", 160m, From, null), Row("Super", 175m, From, null), Row("Diesel", 180m, From, null)],
            From,
            To);

        var only = HistorySeries.Only(chart, s => s.Key != "Super");

        Assert.Equal(2, only.Series.Count);
        Assert.Same(chart.Series[0], only.Series[0]);
        Assert.Same(chart.Series[2], only.Series[1]);
        Assert.Equal(2, only.Series[1].Color);
    }

    [Fact]
    public void Only_starts_at_the_first_step_kept()
    {
        var chart = HistorySeries.Build(
            [Row("Diesel", 180m, From.AddDays(2), null), Row("Régulier", 160m, From.AddDays(8), null)],
            From,
            To);

        var only = HistorySeries.Only(chart, s => s.Key == "Régulier");

        Assert.Equal(From.AddDays(2), chart.From);
        Assert.Equal(From.AddDays(8), only.From);
        Assert.Equal(To, only.To);
    }

    [Fact]
    public void Only_tightens_the_prices_and_the_scale()
    {
        var chart = HistorySeries.Build([Row("Régulier", 160m, From, null), Row("Diesel", 190m, From, null)], From, To);

        var only = HistorySeries.Only(chart, s => s.Key == "Régulier");

        Assert.Equal(160m, only.MinPrice);
        Assert.Equal(160m, only.MaxPrice);
        Assert.True(new ChartScale(only, 100, 60, 0, 0).YMax < new ChartScale(chart, 100, 60, 0, 0).YMax);
    }

    [Fact]
    public void Nearest_change_is_the_closer_of_its_neighbours()
    {
        var scale = Scale(Row("Régulier", 160m, From, From.AddDays(4)), Row("Régulier", 158m, From.AddDays(4), null));

        Assert.Equal(From, scale.NearestChange(0.1));
        Assert.Equal(From.AddDays(4), scale.NearestChange(0.3));
    }

    [Fact]
    public void Nearest_change_halfway_is_the_earlier_one()
    {
        var scale = Scale(
            Row("Régulier", 160m, From, From.AddDays(4)),
            Row("Régulier", 158m, From.AddDays(4), From.AddDays(6)),
            Row("Régulier", 156m, From.AddDays(6), null));

        Assert.Equal(From.AddDays(4), scale.NearestChange(0.5));
    }

    [Fact]
    public void Nearest_change_stays_within_the_chart_and_its_right_edge_has_the_latest_price()
    {
        var data = HistorySeries.Build([Row("Régulier", 160m, From, From.AddDays(2)), Row("Régulier", 158m, From.AddDays(2), null)], From, To);
        var scale = new ChartScale(data, 100, 60, 0, 0);

        Assert.Equal(From, scale.NearestChange(-0.5));
        var edge = scale.NearestChange(1.5)!.Value;
        Assert.Equal(edge, scale.NearestChange(1));
        Assert.Equal(158m, data.Series[0].At(edge)!.PriceCents);
        Assert.InRange(scale.X(edge), 99.99, 100);
    }

    [Fact]
    public void Flat_line_has_both_edges()
    {
        var data = HistorySeries.Build([Row("Régulier", 160m, From.AddDays(-20), null)], From, To);
        var scale = new ChartScale(data, 100, 60, 0, 0);

        var left = scale.NearestChange(0.25)!.Value;
        var right = scale.NearestChange(0.75)!.Value;

        Assert.Equal(From, left);
        Assert.True(right > From.AddDays(9));
        Assert.Equal(160m, data.Series[0].At(left)!.PriceCents);
        Assert.Equal(160m, data.Series[0].At(right)!.PriceCents);
    }

    [Fact]
    public void Line_ending_in_a_gap_has_no_price_from_its_end_on()
    {
        var data = HistorySeries.Build([Row("Diesel", 180m, From, From.AddDays(5))], From, To);
        var scale = new ChartScale(data, 100, 60, 0, 0);

        var end = scale.NearestChange(0.6)!.Value;

        Assert.Equal(From.AddDays(5), end);
        Assert.Null(data.Series[0].At(end));
        Assert.Null(data.Series[0].At(scale.NearestChange(1)!.Value));
    }

    [Fact]
    public void Both_edges_of_a_gap_are_changes()
    {
        var data = HistorySeries.Build(
            [
                Row("Diesel", 180m, From, From.AddDays(1)),
                new PriceHistoryDto("Diesel", null, false, From.AddDays(1), From.AddDays(2)),
                Row("Diesel", 181m, From.AddDays(2), null),
            ],
            From,
            To);
        var scale = new ChartScale(data, 100, 60, 0, 0);

        var start = scale.NearestChange(0.12)!.Value;
        var end = scale.NearestChange(0.18)!.Value;

        Assert.Equal(From.AddDays(1), start);
        Assert.Null(data.Series[0].At(start));
        Assert.Equal(From.AddDays(2), end);
        Assert.Equal(181m, data.Series[0].At(end)!.PriceCents);
    }

    [Fact]
    public void Merged_steps_are_no_change()
    {
        var scale = Scale(Row("Super", 175m, From, From.AddDays(1)), Row("Super", 175m, From.AddDays(1), null));

        Assert.Equal(From, scale.NearestChange(0.1));
        Assert.Equal(From, scale.NearestChange(0.4));
    }

    [Fact]
    public void A_row_cut_by_the_range_changes_at_the_chart_start()
    {
        var scale = Scale(Row("Régulier", 160m, From.AddDays(-5), From.AddDays(2)), Row("Régulier", 158m, From.AddDays(2), null));

        Assert.Equal(From, scale.NearestChange(0));
    }

    [Fact]
    public void Changes_of_every_line_count()
    {
        var scale = Scale(
            Row("Régulier", 160m, From, From.AddDays(8)),
            Row("Régulier", 158m, From.AddDays(8), null),
            Row("Diesel", 180m, From, From.AddDays(2)),
            Row("Diesel", 181m, From.AddDays(2), From.AddDays(6)),
            Row("Diesel", 182m, From.AddDays(6), null));

        Assert.Equal(From.AddDays(2), scale.NearestChange(0.2));
        Assert.Equal(From.AddDays(2), scale.NearestChange(0.35));
        Assert.Equal(From.AddDays(6), scale.NearestChange(0.6));
        Assert.Equal(From.AddDays(8), scale.NearestChange(0.75));
    }

    [Fact]
    public void At_a_change_the_price_is_the_new_one()
    {
        var data = HistorySeries.Build([Row("Régulier", 160m, From, From.AddDays(2)), Row("Régulier", 158m, From.AddDays(2), null)], From, To);

        Assert.Equal(158m, data.Series[0].At(From.AddDays(2))!.PriceCents);
        Assert.Null(data.Series[0].At(To));
    }

    [Fact]
    public void Lines_left_out_have_no_changes()
    {
        var chart = HistorySeries.Build(
            [
                Row("Diesel", 180m, From.AddDays(2), From.AddDays(9)),
                Row("Diesel", 181m, From.AddDays(9), null),
                Row("Régulier", 160m, From.AddDays(8), null),
            ],
            From,
            To);
        var scale = new ChartScale(HistorySeries.Only(chart, s => s.Key == "Régulier"), 100, 60, 0, 0);

        Assert.Equal(From.AddDays(8), scale.NearestChange(0));
        Assert.Equal(From.AddDays(8), scale.NearestChange(0.45));
    }

    [Fact]
    public void Share_is_of_the_chart_shown()
    {
        var scale = Scale(Row("Régulier", 160m, From.AddDays(8), From.AddDays(9)), Row("Régulier", 158m, From.AddDays(9), null));

        Assert.Equal(From.AddDays(9), scale.NearestChange(0.5));
    }

    [Fact]
    public void An_empty_chart_has_no_changes()
    {
        var scale = new ChartScale(new PriceChartData(From, To, []), 100, 60, 0, 0);

        Assert.Null(scale.NearestChange(0.5));
    }

    private static ChartScale Scale(params PriceHistoryDto[] rows) => new(HistorySeries.Build(rows, From, To), 100, 60, 0, 0);

    private static PriceHistoryDto Row(string fuel, decimal price, DateTimeOffset from, DateTimeOffset? to) =>
        new(fuel, price, true, from, to);
}
