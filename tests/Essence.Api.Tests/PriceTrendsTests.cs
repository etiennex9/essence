using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Essence.Api.Endpoints;
using Essence.Hours;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Essence.Api.Tests;

public class PriceTrendsTests
{
    [Fact]
    public async Task The_trends_are_documented()
    {
        using var factory = Factory();

        var response = await factory.CreateClient().GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var get = document.RootElement.GetProperty("paths").GetProperty("/stations/trends").GetProperty("get");
        Assert.Equal("Stations", get.GetProperty("tags")[0].GetString());
        var description = get.GetProperty("description").GetString();
        Assert.Contains("notRaised", description);
        Assert.Contains("/stations/positions", description);
        Assert.True(get.GetProperty("responses").TryGetProperty("304", out _));
        Assert.Equal(
            ["lat", "lon", "fuel", "costco", "radiusKm"],
            get.GetProperty("parameters").EnumerateArray().Select(p => p.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task Nginx_doesnt_log_where_the_user_is()
    {
        using var factory = Factory();
        var response = await factory.CreateClient().GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var config = await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(), "src", "Essence.Web", "nginx", "default.conf"), TestContext.Current.CancellationToken);
        var map = Regex.Match(config, @"map \$request_uri \$essence_log \{\s*~(\*?)(\S+)\s+0;");
        var pattern = map.Groups[2].Value;
        Assert.NotEmpty(pattern);
        Assert.Equal("*", map.Groups[1].Value);

        var located = document.RootElement.GetProperty("paths").EnumerateObject()
            .Where(path => path.Value.EnumerateObject().Any(operation =>
                operation.Value.TryGetProperty("parameters", out var parameters)
                && parameters.EnumerateArray().Any(p => p.GetProperty("name").GetString() is "lat" or "lon" or "fromLat" or "toLat")))
            .Select(path => path.Name)
            .ToList();

        Assert.Contains("/stations/trends", located);
        Assert.All(located, path => Assert.Matches(pattern, "/api" + path + "?lat=45.5&lon=-73.6"));
        Assert.Matches(new Regex(pattern, RegexOptions.IgnoreCase), "/api/Stations/Trends?lat=45.5&lon=-73.6");
    }

    [Fact]
    public void A_jump_at_about_the_usual_time_is_the_usual_one()
    {
        var zone = QuebecTime.Eastern;
        var since = new DateTimeOffset(2026, 11, 20, 0, 5, 0, TimeSpan.FromHours(-5));

        Assert.True(TrendEndpoints.IsUsual(since, new TimeOnly(0, 0), zone));
        Assert.True(TrendEndpoints.IsUsual(since.AddHours(-2).AddMinutes(-30), new TimeOnly(23, 0), zone));
        Assert.False(TrendEndpoints.IsUsual(since.AddHours(8), new TimeOnly(0, 0), zone));
        Assert.False(TrendEndpoints.IsUsual(since, null, zone));
    }

    [Fact]
    public void Nearby_points_share_a_pattern_cell()
    {
        var montreal = TrendPatterns.CellOf(1, false, 10, 45.5017, -73.5673);

        Assert.Equal(montreal, TrendPatterns.CellOf(1, false, 10, 45.5019, -73.5671));
        Assert.NotEqual(montreal, TrendPatterns.CellOf(1, false, 10, 45.52, -73.5673));
        Assert.NotEqual(montreal, TrendPatterns.CellOf(1, true, 10, 45.5017, -73.5673));
        var (latitude, longitude) = montreal.Centre;
        Assert.InRange(Ranking.Geo.DistanceKm(latitude, longitude, 45.5017, -73.5673), 0, 0.75);
    }

    [Fact]
    public void Nearby_points_with_another_radius_have_their_own_cell() =>
        Assert.NotEqual(TrendPatterns.CellOf(1, false, 10, 45.5017, -73.5673), TrendPatterns.CellOf(1, false, 25, 45.5017, -73.5673));

    [Fact]
    public void A_rise_isnt_told_once_the_data_is_late()
    {
        var version = new DateTimeOffset(2026, 11, 20, 5, 0, 0, TimeSpan.Zero);

        Assert.False(TrendEndpoints.IsLate(version.AddMinutes(9), version));
        Assert.True(TrendEndpoints.IsLate(version.AddMinutes(11), version));
        Assert.True(TrendEndpoints.IsLate(version, null));
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Essence.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("Essence.slnx not found above the test's folder.");
    }

    private static WebApplicationFactory<Program> Factory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseSetting("ConnectionStrings:Essence", "Host=127.0.0.1;Port=1;Database=unused;Timeout=1"));
}
