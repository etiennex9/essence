using Essence.UI.App;
using Essence.UI.App.Widgets;
using Essence.UI.Services;

namespace Essence.UI.Tests;

public sealed class WidgetLocationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 13, 10, 0, TimeSpan.Zero);
    private static readonly GeoPoint Start = new(45.5017, -73.5673);

    /// <summary>A fix <paramref name="northMeters"/> north of Start, <paramref name="minutesLater"/> after Now.</summary>
    private static LastFix Fix(double northMeters = 0, double minutesLater = 0, double accuracy = 20) =>
        new(Start.Latitude + northMeters / 111_195, Start.Longitude, accuracy, Now.AddMinutes(minutesLater));

    [Theory]
    [InlineData(900, 20, false)]
    [InlineData(1100, 20, true)]
    // An approximate fix is only known to within 2 km: moving less than that isn't a move.
    [InlineData(1500, 2000, false)]
    [InlineData(2500, 2000, true)]
    public void A_move_is_longer_than_a_kilometre_and_than_the_fixes_accuracy(double meters, double accuracy, bool moved)
    {
        Assert.Equal(moved, WidgetLocation.Moved(Fix(accuracy: 20), Fix(meters, accuracy: accuracy)));
        Assert.Equal(moved, WidgetLocation.Moved(Fix(accuracy: accuracy), Fix(meters, accuracy: 20)));
    }

    [Theory]
    [InlineData(4, false)]
    [InlineData(5, true)]
    public void Moving_refreshes_at_most_every_five_minutes(double minutes, bool worth)
    {
        Assert.Equal(worth, WidgetLocation.WorthRefreshing(Fix(), Fix(5000, minutes)));
    }

    [Fact]
    public void Staying_put_never_refreshes_and_a_first_fix_always_does()
    {
        Assert.False(WidgetLocation.WorthRefreshing(Fix(), Fix(200, 60)));
        Assert.True(WidgetLocation.WorthRefreshing(null, Fix()));
    }

    [Fact]
    public void A_refresh_takes_its_own_fix_when_the_kept_one_is_five_minutes_old()
    {
        Assert.True(WidgetLocation.NeedsFix(null, Now));
        Assert.False(WidgetLocation.NeedsFix(Fix(minutesLater: -4.9), Now));
        Assert.True(WidgetLocation.NeedsFix(Fix(minutesLater: -5), Now));
    }

    [Theory]
    [InlineData(800, 20, true)]
    [InlineData(1200, 20, false)]
    [InlineData(1800, 2500, true)]
    public void A_start_within_a_kilometre_or_the_fix_s_accuracy_is_there(double meters, double accuracy, bool there)
    {
        var destination = Fix(meters).Point;

        Assert.Equal(there, WidgetLocation.IsThere(Fix(accuracy: accuracy), destination));
    }

    [Theory]
    [InlineData(false, false, false, false, WidgetLocationAccess.NotAllowed)]
    [InlineData(false, true, true, true, WidgetLocationAccess.NotAllowed)]
    [InlineData(true, false, false, true, WidgetLocationAccess.WhileInUse)]
    [InlineData(true, false, true, true, WidgetLocationAccess.WhileInUse)]
    [InlineData(true, true, false, true, WidgetLocationAccess.WhileInUse)]
    [InlineData(true, true, true, false, WidgetLocationAccess.WhileInUse)]
    [InlineData(true, true, true, true, WidgetLocationAccess.AllTheTime)]
    public void All_the_time_takes_the_permission_the_choice_and_a_build_that_allows_it(
        bool location, bool background, bool follow, bool build, WidgetLocationAccess access)
    {
        Assert.Equal(access, WidgetLocation.AccessOf(location, background, follow, build));
    }

    [Theory]
    [InlineData(false, WidgetLocationAccess.AllTheTime, WidgetFollowState.Off)]
    [InlineData(true, WidgetLocationAccess.AllTheTime, WidgetFollowState.On)]
    [InlineData(true, WidgetLocationAccess.WhileInUse, WidgetFollowState.Paused)]
    [InlineData(true, WidgetLocationAccess.NotAllowed, WidgetFollowState.Paused)]
    public void Follow_is_paused_when_the_permission_went(bool follow, WidgetLocationAccess access, WidgetFollowState state)
    {
        Assert.Equal(state, WidgetLocation.FollowStateOf(follow, access));
    }

    [Fact]
    public async Task The_choice_and_where_the_widgets_ranked_from_are_kept()
    {
        var preferences = new MemoryPreferences();
        var fix = Fix(300, 2, 35);

        Assert.False(await WidgetLocation.IsFollowOnAsync(preferences));
        Assert.Null(await WidgetLocation.ReadRankedFromAsync(preferences));
        await WidgetLocation.SetFollowAsync(preferences, true);
        await WidgetLocation.WriteRankedFromAsync(preferences, fix);

        Assert.True(await WidgetLocation.IsFollowOnAsync(preferences));
        Assert.Equal(fix, await WidgetLocation.ReadRankedFromAsync(preferences));

        await WidgetLocation.SetFollowAsync(preferences, false);
        await preferences.SetAsync(WidgetLocation.RankedFromKey, "{not json");

        Assert.False(await WidgetLocation.IsFollowOnAsync(preferences));
        Assert.Null(await WidgetLocation.ReadRankedFromAsync(preferences));
    }
}
