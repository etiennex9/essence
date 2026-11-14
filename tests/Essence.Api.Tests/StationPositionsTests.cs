using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Essence.Api.Endpoints;
using Essence.Contracts;
using Essence.Data;
using Essence.Data.Entities;
using Essence.Hours;
using Essence.Ranking;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Essence.Api.Tests;

/// <summary>
/// Where the API puts the stations whose Régie point a reviewed correction fixes: the snapshot, and through it the details,
/// Best near you's distances and the map's nearby stations; the 304 rule once the corrections change; and
/// <c>/stations/positions</c>. Reading the stations needs PostgreSQL, which these tests don't have: the snapshot and the
/// review are built from values given here, and the endpoint is checked in the API's description.
/// </summary>
public sealed class StationPositionsTests
{
    // REZZO LA JONCTION, 1918 boul. Tadoussac, Saint-Fulgence: the Régie draws it in Saguenay, 21.7 km away.
    private const string Jonction = "1918 boul. tadoussac, saint-fulgence|G0V1S0";
    private const double FeedLatitude = 48.458942, FeedLongitude = -71.003062;
    private const double Latitude = 48.443209, Longitude = -70.709136;

    // La station Mansfield, 281 rue Principale: the Régie's point, 653 m from where the station is.
    private const string Mansfield = "281 rue principale, mansfield-et-pontefract|J0X1R0";

    private static readonly DateTimeOffset Now = new(2026, 11, 14, 15, 0, 0, TimeSpan.Zero);

    private static readonly PositionCorrections Corrections = PositionCorrections.Parse(Encoding.UTF8.GetBytes(string.Create(
        CultureInfo.InvariantCulture,
        $$"""
        {"corrections": [
          {"naturalKey": "{{Jonction}}", "latitude": {{Latitude}}, "longitude": {{Longitude}}, "feedLatitude": {{FeedLatitude}},
           "feedLongitude": {{FeedLongitude}}, "reviewed": "2026-11-14", "note": "OpenStreetMap way/549635443 and the address node/1266988689 (Photon) agree within 39 m."},
          {"naturalKey": "{{Mansfield}}", "latitude": 45.853863, "longitude": -76.736303, "feedLatitude": 45.85927,
           "feedLongitude": -76.739588, "reviewed": "2026-11-13", "note": "OpenStreetMap node/5312347403 and the address node/4195696185 agree within 4 m."},
          {"naturalKey": "1 rue gone, nowhere|G0G0G0", "latitude": 46.1, "longitude": -72.1, "feedLatitude": 46.2,
           "feedLongitude": -72.2, "reviewed": "2026-11-01", "note": "Gone since."}
        ]}
        """)));

    private static SnapshotStation Station(long id, double latitude, double longitude, string brand = "R.L.") =>
        new(id, "REZZO LA JONCTION", brand, "En opération", "1918 boul. Tadoussac, Saint-Fulgence", latitude, longitude, false,
            [new SnapshotPrice(1, 159.9m, true)]);

    /// <summary>The snapshot as loaded: the Jonction at the Régie's point, and its neighbour #2372 46 m from it.</summary>
    private static (IReadOnlyList<SnapshotStation> Stations, IReadOnlyDictionary<long, PositionCorrection> Corrected) Placed() =>
        StationSnapshot.Correct(
            [
                (Station(2448, FeedLatitude, FeedLongitude), Jonction),
                (Station(2372, 48.459135, -71.002545), "281 boul. tadoussac, saguenay|G7H5A5"),
                // The Régie moved Mansfield's point 100 m since the review: its correction expired.
                (Station(2288, 45.85927 + 0.0009, -76.739588, "Stinson"), Mansfield),
            ],
            Corrections);

    private static Snapshot Snapshot()
    {
        var (stations, corrected) = Placed();
        return new Snapshot(Now.AddMinutes(-3), stations, null, new Dictionary<long, SnapshotHours>(), null, null, null, corrected, Now.AddHours(-1));
    }

    [Fact]
    public void The_snapshot_puts_a_station_whose_correction_applies_at_the_corrected_point_and_the_others_at_the_feed_s()
    {
        var (stations, corrected) = Placed();

        Assert.Equal((Latitude, Longitude), (stations[0].Latitude, stations[0].Longitude));
        Assert.Equal((48.459135, -71.002545), (stations[1].Latitude, stations[1].Longitude));
        Assert.Equal((45.85927 + 0.0009, -76.739588), (stations[2].Latitude, stations[2].Longitude));
        Assert.Equal(Jonction, Assert.Single(corrected).Value.NaturalKey);
        Assert.Equal(2448, corrected.Keys.Single());
    }

    [Fact]
    public void The_details_give_the_corrected_point_and_the_Google_Maps_directions_follow()
    {
        var detail = new StationDetail(
            2448, "REZZO LA JONCTION", "R.L.", "En opération", "1918 boul. Tadoussac, Saint-Fulgence", "G0V 1S0", "Saguenay-Lac-Saint-Jean",
            FeedLatitude, FeedLongitude, true, Now, Now, Now, []);
        var other = detail with { Id = 2372, Latitude = 48.459135, Longitude = -71.002545 };
        var current = Snapshot();

        Assert.Equal((Latitude, Longitude), (StationEndpoints.WithSnapshot(detail, current).Latitude, StationEndpoints.WithSnapshot(detail, current).Longitude));
        Assert.Equal((48.459135, -71.002545), (StationEndpoints.WithSnapshot(other, current).Latitude, StationEndpoints.WithSnapshot(other, current).Longitude));
        Assert.Equal(((double?)null, (double?)null), current.PointOf(9, null, null));
    }

    [Fact]
    public void Best_near_you_and_the_map_measure_from_the_corrected_point()
    {
        var current = Snapshot();

        // A driver at the Saint-Fulgence station: 0 km from it, 21.7 km from its old point and its old neighbour.
        var eligible = StationEndpoints.Eligible(current.Stations, 1, costco: false, Latitude, Longitude);
        var nearby = StationEndpoints.Closest(current.Stations, Latitude, Longitude, radiusKm: 5, fuelId: null, limit: 20);
        var inSaguenay = StationEndpoints.Closest(current.Stations, FeedLatitude, FeedLongitude, radiusKm: 5, fuelId: 1, limit: 20);

        Assert.Equal(0, eligible.Single(s => s.Station.Id == 2448).Km, 3);
        Assert.Equal(21.7, eligible.Single(s => s.Station.Id == 2372).Km, 1);
        Assert.Equal(2448, Assert.Single(nearby).Station.Id);
        Assert.Equal(new long[] { 2372 }, inSaguenay.Select(s => s.Station.Id));
        Assert.Empty(StationEndpoints.Closest(current.Stations, FeedLatitude, FeedLongitude, radiusKm: 5, fuelId: 2, limit: 20));
    }

    [Fact]
    public void A_corrected_station_s_hours_are_in_the_time_zone_where_it_is()
    {
        // A made-up correction from Gaspé (Eastern time) to Cap-aux-Meules (Atlantic time): the zone follows the point.
        var islands = new Dictionary<long, PositionCorrection>
        {
            [7] = new("1 rue x, cap-aux-meules|G4T1A1", 47.38, -61.86, 48.83, -64.48, new DateOnly(2026, 11, 14), "x"),
        };
        SnapshotHoursRow[] rows =
        [
            new(new HoursRow(7, "osm", "Mo-Su 07:00-21:00", true, false, null, null), 48.83, -64.48),
            new(new HoursRow(8, "osm", "Mo-Su 07:00-21:00", true, false, null, null), 48.83, -64.48),
        ];

        var placed = StationSnapshot.Correct(rows, islands);
        var hours = StationSnapshot.ChooseHours(placed, new Dictionary<string, DateTimeOffset> { ["osm"] = Now }, Now, TimeSpan.FromDays(21));

        Assert.Equal(((double?)47.38, (double?)-61.86), (placed[0].Latitude, placed[0].Longitude));
        Assert.Same(rows[1], placed[1]);
        Assert.Same(QuebecTime.Atlantic, hours[7].Zone);
        Assert.Same(QuebecTime.Eastern, hours[8].Zone);
    }

    [Fact]
    public void The_corrections_that_apply_change_the_data_s_version_so_a_new_image_isn_t_hidden_by_a_304()
    {
        var clock = new FakeTimeProvider(Now);
        var (_, corrected) = Placed();
        var feed = Now.AddMinutes(-3);

        // The first load of a process is a change: a client with the feed's version from before the start gets no 304.
        var (key, first) = StationSnapshot.PositionsChange(null, null, corrected, clock.GetUtcNow());
        clock.Advance(TimeSpan.FromMinutes(5));
        var (sameKey, same) = StationSnapshot.PositionsChange(key, first, corrected, clock.GetUtcNow());
        clock.Advance(TimeSpan.FromMinutes(5));
        var (noneKey, none) = StationSnapshot.PositionsChange(key, first, new Dictionary<long, PositionCorrection>(), clock.GetUtcNow());

        Assert.Equal(Now, first);
        Assert.Equal((key, Now), (sameKey, same));
        Assert.NotEqual(key, noneKey);
        Assert.Equal(Now.AddMinutes(10), none);
        Assert.False(DataVersion.IsNotModified(feed, feed, new Snapshot(feed, [], null, new Dictionary<long, SnapshotHours>(), null, null, PositionsChangedAt: first).ChangedAt));
        Assert.True(DataVersion.IsNotModified(Now, feed, new Snapshot(feed, [], null, new Dictionary<long, SnapshotHours>(), null, null, PositionsChangedAt: first).ChangedAt));
    }

    [Fact]
    public void The_304_rule_takes_the_later_of_the_hours_and_the_positions()
    {
        Snapshot At(DateTimeOffset? hours, DateTimeOffset? positions) =>
            new(Now, [], null, new Dictionary<long, SnapshotHours>(), null, hours, PositionsChangedAt: positions);

        Assert.Equal(Now.AddHours(-1), At(Now.AddHours(-2), Now.AddHours(-1)).ChangedAt);
        Assert.Equal(Now.AddHours(-1), At(Now.AddHours(-1), Now.AddHours(-2)).ChangedAt);
        Assert.Equal(Now.AddHours(-1), At(null, Now.AddHours(-1)).ChangedAt);
        Assert.Equal(Now.AddHours(-1), At(Now.AddHours(-1), null).ChangedAt);
        Assert.Null(At(null, null).ChangedAt);
    }

    [Fact]
    public void The_review_says_what_became_of_each_correction()
    {
        ReviewStation[] stations =
        [
            new(2448, Jonction, "REZZO LA JONCTION", "1918 boul. Tadoussac, Saint-Fulgence", "R.L.", true, FeedLatitude, FeedLongitude),
            new(2288, Mansfield, "La station Mansfield", "281 rue Principale, Mansfield-et-Pontefract", "Stinson", true, 45.85927 + 0.0009, -76.739588),
        ];

        var review = PositionReview.Build(Corrections, stations, [], new DateOnly(2026, 11, 14));

        Assert.Equal(
            new[] { (2448L, PositionReview.Applied, 21748), (2288L, PositionReview.Expired, 746), (0L, PositionReview.StationGone, 13527) },
            review.Corrections.Select(c => (c.StationId ?? 0, c.State, c.DistanceM)));
        Assert.Equal(new PositionPoint(FeedLatitude, FeedLongitude), review.Corrections[0].Feed);
        Assert.Equal(new PositionPoint(Latitude, Longitude), review.Corrections[0].Corrected);
        Assert.Equal(("REZZO LA JONCTION", "2026-11-14"), (review.Corrections[0].Name, review.Corrections[0].Reviewed));
        Assert.Equal(new PositionPoint(46.2, -72.2), review.Corrections[2].Feed);
        Assert.Null(review.Corrections[2].Name);
        Assert.Empty(review.Candidates);
        Assert.Empty(review.Warnings);
    }

    [Fact]
    public void A_candidate_comes_with_its_evidence_and_the_entry_to_paste_once_checked()
    {
        PositionEvidence[] evidence =
        [
            new(PositionCandidate.FromOsm, "node/5312347403", 45.8538456, -76.7362914, 655),
            new(PositionCandidate.FromPhoton, "node/4195696185", 45.8538806, -76.7363143, 651),
        ];
        var found = new PositionCandidate
        {
            StationId = 2288,
            NaturalKey = Mansfield,
            FeedLatitude = 45.85927,
            FeedLongitude = -76.739588,
            Latitude = 45.853863,
            Longitude = -76.736303,
            DistanceM = 653,
            Evidence = PositionCandidate.Write(evidence),
            FoundAt = Now.AddHours(-9),
        };
        var applied = new PositionCandidate
        {
            StationId = 2448,
            NaturalKey = Jonction,
            FeedLatitude = FeedLatitude,
            FeedLongitude = FeedLongitude,
            Latitude = Latitude,
            Longitude = Longitude,
            DistanceM = 21748,
            Evidence = "[]",
            FoundAt = Now.AddHours(-9),
        };
        ReviewStation[] stations =
        [
            new(2448, Jonction, "REZZO LA JONCTION", "1918 boul. Tadoussac, Saint-Fulgence", "R.L.", true, FeedLatitude, FeedLongitude),
            new(2288, Mansfield, "La station Mansfield", "281 rue Principale, Mansfield-et-Pontefract", "Stinson", true, 45.85927, -76.739588),
        ];

        // Without Mansfield's correction (the Jonction's applies: it was approved since it was found, and is left out).
        var review = PositionReview.Build(
            PositionCorrections.Parse(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture,
                $$"""{"corrections": [{"naturalKey": "{{Jonction}}", "latitude": {{Latitude}}, "longitude": {{Longitude}}, "feedLatitude": {{FeedLatitude}}, "feedLongitude": {{FeedLongitude}}, "reviewed": "2026-11-14", "note": "x"}]}"""))),
            stations,
            [applied, found],
            PositionReview.Today(Now));

        var candidate = Assert.Single(review.Candidates);
        Assert.Equal(
            new PositionCandidateDto(
                2288, Mansfield, "La station Mansfield", "281 rue Principale, Mansfield-et-Pontefract", "Stinson",
                new PositionPoint(45.85927, -76.739588), new PositionPoint(45.853863, -76.736303), 653, candidate.Evidence,
                new PositionEntry(
                    Mansfield, 45.853863, -76.736303, 45.85927, -76.739588, "2026-11-14",
                    "OpenStreetMap node/5312347403 and the address node/4195696185 (Photon) agree within 4 m, 653 m from the Régie's point."),
                Now.AddHours(-9)),
            candidate);
        Assert.Equal(evidence.Select(e => new PositionEvidenceDto(e.Kind, e.Ref, e.Latitude, e.Longitude, e.DistanceM)), candidate.Evidence);
    }

    [Fact]
    public void The_note_names_each_position_by_its_id_never_by_a_name()
    {
        PositionEvidence[] evidence =
        [
            new(PositionCandidate.FromOsm, "node/12114619118", 45.6617918, -75.0204765, 359),
            new(PositionCandidate.FromLocator, "ultramar/56182", 45.6618, -75.0206, 351),
            new(PositionCandidate.FromPhoton, "node/12011210781", 45.661788, -75.0200771, 384),
        ];

        Assert.Equal(
            "OpenStreetMap node/12114619118, Ultramar's locator (56182) and the address node/12011210781 (Photon) agree within 41 m, 355 m from the Régie's point.",
            PositionReview.Note(evidence, 355));
        Assert.Equal(
            "Circle K's locator (6001268) and the address node/11973108312 (Photon) agree within 6 m, 400 m from the Régie's point.",
            PositionReview.Note(
                [
                    new(PositionCandidate.FromLocator, "circlek/6001268", 45.44763, -73.610487, 402),
                    new(PositionCandidate.FromPhoton, "node/11973108312", 45.4476807, -73.6105273, 399),
                ],
                400));
    }

    [Fact]
    public void The_review_s_day_is_Québec_s()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 14, 3, 40, 0, TimeSpan.Zero));

        Assert.Equal(new DateOnly(2026, 11, 13), PositionReview.Today(clock.GetUtcNow()));
        clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal(new DateOnly(2026, 11, 14), PositionReview.Today(clock.GetUtcNow()));
    }

    [Fact]
    public void The_answer_is_in_the_API_s_JSON_with_the_entry_as_the_file_writes_it()
    {
        using var factory = Factory();
        var json = factory.Services.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;
        var positions = new StationPositions(
            [new PositionCorrectionDto(2448, Jonction, "REZZO LA JONCTION", "1918 boul. Tadoussac, Saint-Fulgence",
                new PositionPoint(FeedLatitude, FeedLongitude), new PositionPoint(Latitude, Longitude), 21748, "applied", "2026-11-14", "x")],
            [new PositionCandidateDto(
                2288, Mansfield, "La station Mansfield", "281 rue Principale", "Stinson", new PositionPoint(45.85927, -76.739588),
                new PositionPoint(45.853863, -76.736303), 653, [new PositionEvidenceDto("osm", "node/5312347403", 45.8538456, -76.7362914, 655)],
                new PositionEntry(Mansfield, 45.853863, -76.736303, 45.85927, -76.739588, "2026-11-14", "y"), Now)],
            []);

        Assert.Equal(
            """
            {"corrections":[{"stationId":2448,"naturalKey":"1918 boul. tadoussac, saint-fulgence|G0V1S0","name":"REZZO LA JONCTION",
            "address":"1918 boul. Tadoussac, Saint-Fulgence","feed":{"latitude":48.458942,"longitude":-71.003062},
            "corrected":{"latitude":48.443209,"longitude":-70.709136},"distanceM":21748,"state":"applied","reviewed":"2026-11-14","note":"x"}],
            "candidates":[{"stationId":2288,"naturalKey":"281 rue principale, mansfield-et-pontefract|J0X1R0","name":"La station Mansfield",
            "address":"281 rue Principale","brand":"Stinson","feed":{"latitude":45.85927,"longitude":-76.739588},
            "proposed":{"latitude":45.853863,"longitude":-76.736303},"distanceM":653,
            "evidence":[{"kind":"osm","ref":"node/5312347403","latitude":45.8538456,"longitude":-76.7362914,"distanceM":655}],
            "entry":{"naturalKey":"281 rue principale, mansfield-et-pontefract|J0X1R0","latitude":45.853863,"longitude":-76.736303,
            "feedLatitude":45.85927,"feedLongitude":-76.739588,"reviewed":"2026-11-14","note":"y"},"foundAt":"2026-11-14T15:00:00+00:00"}],
            "warnings":[]}
            """.ReplaceLineEndings(""),
            JsonSerializer.Serialize(positions, json));

        // The entry pasted into the file reads back as the correction it says.
        var entry = JsonSerializer.Serialize(positions.Candidates[0].Entry, json);
        var file = PositionCorrections.Parse(Encoding.UTF8.GetBytes($$"""{"corrections": [{{entry}}]}"""));
        Assert.Equal(
            new PositionCorrection(Mansfield, 45.853863, -76.736303, 45.85927, -76.739588, new DateOnly(2026, 11, 14), "y"),
            Assert.Single(file.All));
    }

    [Fact]
    public async Task The_endpoint_is_in_the_API_s_description_among_the_stations()
    {
        using var factory = Factory();

        var response = await factory.CreateClient().GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var paths = document.RootElement.GetProperty("paths");
        var get = paths.GetProperty("/stations/positions").GetProperty("get");
        Assert.Equal("Stations", get.GetProperty("tags")[0].GetString());
        Assert.Contains("curated-positions.json", get.GetProperty("description").GetString());
        Assert.True(get.GetProperty("responses").TryGetProperty("200", out _));
        foreach (var path in (string[])["/stations/{id}", "/stations/nearby", "/stations/best", "/stations", "/prices/current"])
        {
            Assert.Contains("/stations/positions", paths.GetProperty(path).GetProperty("get").GetProperty("description").GetString());
        }
    }

    private static WebApplicationFactory<Program> Factory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Essence", "Host=127.0.0.1;Port=1;Database=unused;Timeout=1");
            builder.ConfigureTestServices(services =>
            {
                foreach (var service in (DataService[])[DataService.Routing, DataService.Geocoding])
                {
                    services.AddHttpClient(DataServiceMonitor.ClientName(service))
                        .ConfigurePrimaryHttpMessageHandler(() => new NotFoundHandler());
                }
            });
        });

    private sealed class NotFoundHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
