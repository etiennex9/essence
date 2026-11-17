using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Essence.Data;
using Essence.Ingest.Hours;
using Microsoft.Extensions.Options;

namespace Essence.Ingest.Tests;

/// <summary>The hours sources' fixtures (Fixtures/Hours, trimmed real answers fetched 2026-11-12), and what their tests share.</summary>
internal static class HoursFixtures
{
    public static byte[] Bytes(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Hours", name));

    public static JsonNode Json(string name) => JsonNode.Parse(Bytes(name))!;

    public static byte[] Utf8(JsonNode node) => Encoding.UTF8.GetBytes(node.ToJsonString());

    public static IOptions<HoursOptions> Options(Action<HoursOptions>? configure = null)
    {
        var options = new HoursOptions();
        configure?.Invoke(options);
        return Microsoft.Extensions.Options.Options.Create(options);
    }

    /// <summary>A pacer that doesn't wait: the tests about pausing use <see cref="Microsoft.Extensions.Time.Testing.FakeTimeProvider"/>.</summary>
    public static HoursPacer NoPause() => new(Options(o => (o.MinPause, o.MaxPause) = (TimeSpan.Zero, TimeSpan.Zero)), TimeProvider.System, new Random(1));

    public static HoursClient Client(Func<HttpRequestMessage, HttpResponseMessage>? respond = null) =>
        new(new HttpClient(new StubHandler(respond ?? (_ => throw new InvalidOperationException("No request expected.")))));

    public static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Ok(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    public static MatchStation Station(long id, string address, string postalCode, string? brand, double latitude, double longitude, string? name = null) =>
        new(id, StationKey.Create(address, postalCode), BrandFamily.Of(brand), latitude, longitude, HoursMatcher.HouseNumber(address),
            StationKey.NormalizePostalCode(postalCode))
        {
            StoreNumber = HoursMatcher.StoreNumber(name),
        };

    public sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    public sealed class Stations(params MatchStation[] stations) : IHoursStations
    {
        public Task<StationSet> LoadStationsAsync(CancellationToken cancellationToken) => Task.FromResult(new StationSet(stations, null));
    }
}
