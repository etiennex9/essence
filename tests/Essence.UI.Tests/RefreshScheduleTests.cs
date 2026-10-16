using Essence.UI.Services;

namespace Essence.UI.Tests;

public class RefreshScheduleTests
{
    private static readonly DateTimeOffset ImportedAt = new(2026, 10, 16, 12, 0, 40, TimeSpan.Zero);

    [Fact]
    public void Refreshes_just_after_the_next_import_is_expected()
    {
        var now = ImportedAt.AddMinutes(1);
        var delay = RefreshSchedule.Next(ImportedAt, now, lateAttempts: 0);
        Assert.Equal(ImportedAt + RefreshSchedule.PublishInterval + RefreshSchedule.Margin, now + delay);
    }

    [Fact]
    public void Waits_at_least_the_minimum_when_the_import_is_almost_due()
    {
        var now = ImportedAt + RefreshSchedule.PublishInterval + RefreshSchedule.Margin - TimeSpan.FromSeconds(3);
        Assert.Equal(RefreshSchedule.MinDelay, RefreshSchedule.Next(ImportedAt, now, lateAttempts: 0));
    }

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(3, 120)]
    [InlineData(10, 120)]
    public void Retries_sooner_then_less_often_while_the_import_is_late(int lateAttempts, int expectedSeconds)
    {
        var now = ImportedAt.AddMinutes(6);
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), RefreshSchedule.Next(ImportedAt, now, lateAttempts));
    }

    [Fact]
    public void Data_already_past_its_next_import_counts_as_late()
    {
        var now = ImportedAt.AddMinutes(20);
        Assert.Equal(TimeSpan.FromSeconds(30), RefreshSchedule.Next(ImportedAt, now, lateAttempts: 0));
    }

    [Fact]
    public void Without_a_version_it_refreshes_every_publish_interval()
    {
        Assert.Equal(RefreshSchedule.PublishInterval, RefreshSchedule.Next(null, ImportedAt, lateAttempts: 0));
        Assert.Equal(RefreshSchedule.PublishInterval, RefreshSchedule.Next(null, ImportedAt, lateAttempts: 3));
    }

    [Fact]
    public void A_device_clock_behind_the_api_waits_no_more_than_one_interval()
    {
        var now = ImportedAt.AddMinutes(-10);
        Assert.Equal(
            RefreshSchedule.PublishInterval + RefreshSchedule.Margin, RefreshSchedule.Next(ImportedAt, now, lateAttempts: 0));
    }
}
