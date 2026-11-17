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

    [Fact]
    public void Configured_boxes_and_servers_replace_the_default_ones_rather_than_adding_to_them()
    {
        var options = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Hours:CircleK:Boxes:0:North"] = "46",
                ["Hours:CircleK:Boxes:0:West"] = "-74",
                ["Hours:CircleK:Boxes:0:South"] = "45",
                ["Hours:CircleK:Boxes:0:East"] = "-73",
                ["Hours:Osm:Endpoints:0"] = "https://overpass.example.org/api/interpreter",
                ["Hours:Shell:Box:North"] = "47",
            })
            .Build()
            .GetSection(HoursOptions.SectionName)
            .Get<HoursOptions>()!;

        var box = Assert.Single(options.CircleK.BoxList);
        Assert.Equal((46.0, -74.0, 45.0, -73.0), (box.North, box.West, box.South, box.East));
        Assert.Equal(["https://overpass.example.org/api/interpreter"], options.Osm.EndpointList);
        Assert.Equal((47.0, -79.8), (options.Shell.Box.North, options.Shell.Box.West));
        Assert.Null(options.Validate());
    }

    [Fact]
    public void Every_source_is_on_by_default()
    {
        var options = new HoursOptions();

        Assert.True(options.Curated.Enabled && options.Costco.Enabled && options.Ultramar.Enabled && options.CircleK.Enabled
            && options.Shell.Enabled && options.Osm.Enabled);
        Assert.Equal(2, options.CircleK.BoxList.Count);
        Assert.Equal(2, options.Osm.EndpointList.Count);
    }

    [Theory]
    [InlineData("every")]
    [InlineData("night")]
    [InlineData("share")]
    [InlineData("backoff")]
    [InlineData("user-agent")]
    [InlineData("costco-url")]
    [InlineData("cache")]
    [InlineData("pause")]
    [InlineData("ultramar-url")]
    [InlineData("circlek-url")]
    [InlineData("circlek-box")]
    [InlineData("shell-detail-url")]
    [InlineData("shell-box")]
    [InlineData("shell-lists")]
    [InlineData("osm-endpoint")]
    [InlineData("osm-query")]
    [InlineData("osm-timeout")]
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
            case "pause":
                options.MinPause = TimeSpan.FromSeconds(3);
                options.MaxPause = TimeSpan.FromSeconds(2);
                break;
            case "ultramar-url":
                options.Ultramar.Url = "journie.ca/ultramar/api/locations/nearest";
                break;
            case "circlek-url":
                options.CircleK.Url = "https://api.circlek.com/us/ngrp-store-locator/v1/stations?topLeftLatitude={north}";
                break;
            case "circlek-box":
                options.CircleK.Boxes = [new GeoBox(45, -73, 46, -74)];
                break;
            case "shell-detail-url":
                options.Shell.DetailUrl = "https://shellgsllocator.geoapp.me/api/v2/locations/";
                break;
            case "shell-box":
                options.Shell.Box = new GeoBox(91, -80, 45, -57);
                break;
            case "shell-lists":
                options.Shell.MaxListRequests = 0;
                break;
            case "osm-endpoint":
                options.Osm.Endpoints = ["ftp://overpass.example.org/"];
                break;
            case "osm-query":
                options.Osm.Query = "";
                break;
            case "osm-timeout":
                options.Osm.RequestTimeout = TimeSpan.Zero;
                break;
        }

        Assert.NotNull(options.Validate());
    }
}
