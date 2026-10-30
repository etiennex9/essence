using System.Globalization;
using System.Text.Json;

namespace Essence.Ranking;

/// <summary>
/// What a data service (the routing or the geocoding container) says it's doing, at GET :9090/status: its supervisor
/// builds the data, serves it, refreshes it, or waits to try again (src/Essence.Supervisor/supervisor.sh).
/// </summary>
/// <param name="State"><c>building</c> (no data to serve yet), <c>refreshing</c> (serving while building the next),
/// <c>starting</c> (the server is loading its data), <c>serving</c>, or <c>waiting</c> (to try a failed build again).</param>
/// <param name="Serving">The server answers.</param>
/// <param name="Stage">The build's stage (<c>downloading</c>, <c>extracting</c>, <c>importing</c>…) while there's one.</param>
/// <param name="Percent">How far the build is, 0 to 100, while there's one.</param>
/// <param name="DataDate">The date of the data served (or last served), "2026-10-27".</param>
/// <param name="Heartbeat">When the supervisor last wrote its status: it does every 5 seconds while it runs.</param>
public sealed record DataStatus(
    string State,
    bool Serving,
    string? Stage,
    int? Percent,
    string? DataDate,
    string? LastError,
    DateTimeOffset? NextAttemptAt,
    DateTimeOffset Heartbeat)
{
    /// <summary>A status older than this comes from a supervisor that's stuck or gone: it's not believed.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(2);

    /// <summary>The status, or null when it isn't one (a field missing or of the wrong kind).</summary>
    public static DataStatus? Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || Text(root, "state") is not { } state
            || !root.TryGetProperty("serving", out var serving) || serving.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || Time(root, "heartbeat") is not { } heartbeat)
        {
            return null;
        }

        int? percent = root.TryGetProperty("percent", out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var value)
            ? Math.Clamp(value, 0, 100)
            : null;
        return new DataStatus(
            state, serving.GetBoolean(), Text(root, "stage"), percent, Text(root, "dataDate"), Text(root, "lastError"),
            Time(root, "nextAttemptAt"), heartbeat);
    }

    /// <summary>The status still says what the supervisor is doing: its heartbeat is recent.</summary>
    public bool IsFresh(DateTimeOffset now) => now - Heartbeat < StaleAfter;

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text
            : null;

    private static DateTimeOffset? Time(JsonElement element, string name) =>
        Text(element, name) is { } text
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time)
            ? time
            : null;
}

/// <summary>Reads a data service's status. Every failure is null: a service whose status can't be had is called as before.</summary>
public sealed class DataStatusClient(HttpClient http)
{
    public async Task<DataStatus?> GetAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await http.GetAsync("status", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            return DataStatus.Parse(document.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }
}
