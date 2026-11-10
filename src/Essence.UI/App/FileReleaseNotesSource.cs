using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Essence.Contracts;
using Essence.UI.Services;

namespace Essence.UI.App;

/// <summary>
/// Release notes bundled with the app: the release.json and changelog-{month}.json files that
/// <c>tools/Essence.Release -- notes</c> writes (README → Versions). A build without them is a development version.
/// </summary>
/// <param name="open">Opens a bundled file by name, or gives null when there's none.</param>
public sealed class FileReleaseNotesSource(Func<string, CancellationToken, Task<Stream?>> open) : IReleaseNotesSource
{
    public Task<ApiResult<ReleaseInfo>> GetReleaseAsync(CancellationToken cancellationToken) =>
        ReadAsync("release.json", ReleaseJsonContext.Default.ReleaseInfo, cancellationToken);

    public Task<ApiResult<ChangelogMonth>> GetMonthAsync(string month, CancellationToken cancellationToken) =>
        ReadAsync($"changelog-{month}.json", ReleaseJsonContext.Default.ChangelogMonth, cancellationToken);

    private async Task<ApiResult<T>> ReadAsync<T>(string name, JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await open(name, cancellationToken);
            if (stream is null)
            {
                return new ApiResult<T>(default, ApiError.NotFound);
            }

            var value = await JsonSerializer.DeserializeAsync(stream, typeInfo, cancellationToken);
            return value is null ? new ApiResult<T>(default, ApiError.Failed) : new ApiResult<T>(value);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return new ApiResult<T>(default, ApiError.Failed);
        }
    }
}
