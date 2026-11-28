using static Essence.Ranking.Tests.TrendData;

namespace Essence.Ranking.Tests;

public class PriceWindowTests
{
    private static readonly DateTimeOffset Start = Local(11, 10, 0);

    private static HistoryRow Row(long id, int minutes, decimal? cents, int? closedAfter = null) =>
        new(id, 7, 1, cents, cents is not null, Start.AddMinutes(minutes), closedAfter is { } after ? Start.AddMinutes(after) : null);

    private static ImportTime Import(long id, int minutes) => new(id, Start.AddMinutes(minutes));

    [Fact]
    public void A_new_row_closes_the_open_price()
    {
        var window = PriceWindow.Empty.With([Import(1, 0)], [Row(10, 0, 160.9m)], [], Start.AddDays(-1));

        var next = window.With([Import(2, 5)], [Row(11, 5, 170.9m)], [], Start.AddDays(-1));

        var spans = next.Of(7, 1)!.Spans;
        Assert.Equal(2, spans.Count);
        Assert.Equal(Start.AddMinutes(5).UtcTicks, spans[0].To);
        Assert.Equal(PriceSpan.Open, spans[1].To);
        Assert.Equal([11L], next.OpenRowIds);
        Assert.Single(window.Of(7, 1)!.Spans);
    }

    [Fact]
    public void The_same_rows_again_change_nothing()
    {
        var once = PriceWindow.Empty.With([Import(1, 0), Import(2, 5)], [Row(10, 0, 160.9m, closedAfter: 5), Row(11, 5, 170.9m)], [], Start.AddDays(-1));

        var twice = once.With([Import(2, 5)], [Row(11, 5, 170.9m)], [], Start.AddDays(-1));

        Assert.Equal(once.Of(7, 1)!.Spans, twice.Of(7, 1)!.Spans);
        Assert.Equal(Start.AddMinutes(5), twice.LastImport!.Value.GeneratedAt);
    }

    [Fact]
    public void A_row_closed_with_no_successor_ends()
    {
        var window = PriceWindow.Empty.With([Import(1, 0)], [Row(10, 0, 160.9m)], [], Start.AddDays(-1));

        var closed = window.With([Import(2, 5)], [], [new ClosedRow(10, Start.AddMinutes(5))], Start.AddDays(-1));

        Assert.Equal(Start.AddMinutes(5).UtcTicks, closed.Of(7, 1)!.Spans[0].To);
        Assert.Empty(closed.OpenRowIds);
        Assert.Null(closed.Of(7, 1)!.TenthsAt(Start.AddMinutes(6).UtcTicks, Start.AddMinutes(6).UtcTicks));
    }

    [Fact]
    public void Trimming_drops_what_ended_before_the_window()
    {
        var window = PriceWindow.Empty.With(
            [Import(1, 0), Import(2, 60)], [Row(10, 0, 160.9m, closedAfter: 60), Row(11, 60, 150.9m)], [], Start.AddDays(-1));

        var trimmed = window.Trimmed(Start.AddMinutes(90));

        Assert.Equal(1509, trimmed.Of(7, 1)!.Spans.Single().Tenths);
        Assert.Equal([11L], trimmed.OpenRowIds);
    }

    [Fact]
    public void Prices_up_to_the_feeds_highest_fit()
    {
        Assert.Equal(99_999, PriceSpan.ToTenths(9999.9m, true));
        Assert.Equal(PriceSpan.Unavailable, PriceSpan.ToTenths(164.9m, false));
    }

    [Fact]
    public void A_price_corrected_within_minutes_doesnt_count()
    {
        var prices = Prices((Start, 175.9m), (Start.AddMinutes(60), 168.9m), (Start.AddMinutes(70), 175.9m));
        var asOf = Start.AddHours(3).UtcTicks;

        Assert.Equal(1759, prices.TenthsAt(Start.AddMinutes(65).UtcTicks, asOf));
    }

    [Fact]
    public void A_short_price_that_is_still_the_price_counts()
    {
        var prices = Prices((Start, 175.9m), (Start.AddMinutes(60), 168.9m));
        var asOf = Start.AddMinutes(65).UtcTicks;

        Assert.Equal(1689, prices.TenthsAt(asOf, asOf));
    }

    [Fact]
    public void An_unavailable_fuel_has_no_price()
    {
        var prices = Prices((Start, 175.9m), (Start.AddMinutes(60), null));
        var asOf = Start.AddHours(3).UtcTicks;

        Assert.Null(prices.TenthsAt(Start.AddMinutes(90).UtcTicks, asOf));
        Assert.Equal(1759, prices.TenthsAt(Start.AddMinutes(30).UtcTicks, asOf));
    }
}
