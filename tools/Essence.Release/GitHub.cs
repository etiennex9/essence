using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Essence.Release;

/// <summary>The GitHub REST calls the tool needs, for <c>owner/name</c>, with a token that can read (and to tag, write) contents.</summary>
public sealed class GitHub(HttpClient http, string repository)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static GitHub FromEnvironment()
    {
        var repository = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") is { Length: > 0 } r
            ? r
            : throw new ReleaseException("GITHUB_REPOSITORY (owner/name) isn't set");
        var token = Environment.GetEnvironmentVariable("GH_TOKEN") is { Length: > 0 } t
            ? t
            : throw new ReleaseException("GH_TOKEN isn't set");

        var http = new HttpClient { BaseAddress = new Uri("https://api.github.com/"), Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Essence-Release/1.0");
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return new GitHub(http, repository);
    }

    /// <summary>The newest 100 moves of main (newest first): pushes, merges, force pushes, with the time GitHub saw them.</summary>
    public async Task<List<Activity>> GetMainActivityAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"repos/{repository}/activity?ref=refs/heads/main&per_page=100");
        // The response says max-age=60: ask for the current list, which has the push that started this run.
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        using var response = await SendAsync(request, "reading main's activity", cancellationToken);
        return ParseActivity(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    /// <summary>The activity API's JSON (snake_case): <c>[{"before", "after", "timestamp", "activity_type", …}]</c>.</summary>
    public static List<Activity> ParseActivity(string json) => JsonSerializer.Deserialize<List<Activity>>(json, Json) ?? [];

    /// <summary>Points the lightweight tag <paramref name="name"/> at <paramref name="commit"/>, creating it or moving it.</summary>
    public async Task SetTagAsync(string name, string commit, bool exists, CancellationToken cancellationToken)
    {
        using var request = exists
            ? new HttpRequestMessage(HttpMethod.Patch, $"repos/{repository}/git/refs/tags/{name}") { Content = JsonContent.Create(new { sha = commit, force = true }) }
            : new HttpRequestMessage(HttpMethod.Post, $"repos/{repository}/git/refs") { Content = JsonContent.Create(new { @ref = $"refs/tags/{name}", sha = commit }) };
        using var response = await SendAsync(request, $"setting {name} to {commit[..7]}", cancellationToken);
    }

    /// <summary>Sends <paramref name="request"/>. Any failure, an error status, the network or a timeout, is a <see cref="ReleaseException"/>.</summary>
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, string what, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new ReleaseException($"GitHub couldn't be reached {what}: {ex.Message}");
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            response.Dispose();
            throw new ReleaseException($"GitHub answered {(int)response.StatusCode} ({response.StatusCode}) {what}: {body.Trim()}");
        }

        return response;
    }
}
