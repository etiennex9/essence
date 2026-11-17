using Essence.Ingest.Hours;
using Microsoft.Extensions.Configuration;

namespace Essence.Ingest.Tests;

public sealed class HoursOptionsTests
{
    [Fact]
    public void The_defaults_are_valid()
    {
        Assert.Null(new HoursOptions().Validate());
    }

    [Fact]
    public void A_configured_backoff_replaces_the_default_rather_than_adding_to_it()
    {
        var options = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Hours:Backoff:0"] = "00:01:00", ["Hours:Backoff:1"] = "00:05:00" })
            .Build()
            .GetSection(HoursOptions.SectionName)
            .Get<HoursOptions>()!;

        Assert.Equal([TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)], options.BackoffSteps);
    }

    [Theory]
    [InlineData("every")]
    [InlineData("night")]
    [InlineData("share")]
    [InlineData("backoff")]
    [InlineData("user-agent")]
    [InlineData("costco-url")]
    [InlineData("cache")]
    public void Settings_that_can_t_work_are_refused_on_start(string what)
    {
        var options = new HoursOptions();
        switch (what)
        {
            case "every":
                options.Every = TimeSpan.FromHours(12);
                break;
            case "night":
                options.NightStart = TimeSpan.FromHours(5);
                options.NightEnd = TimeSpan.FromHours(1);
                break;
            case "share":
                options.MinShareOfLastGood = 1.5;
                break;
            case "backoff":
                options.Backoff = [TimeSpan.Zero];
                break;
            case "user-agent":
                options.UserAgent = "Essence (station hours";
                break;
            case "costco-url":
                options.Costco.Url = "https://www.costco.ca/AjaxWarehouseBrowseLookupView?latitude=45.5";
                break;
            case "cache":
                options.CachePath = " ";
                break;
        }

        Assert.NotNull(options.Validate());
    }
}
