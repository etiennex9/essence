using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Essence.Ingest.Hours.Plugins;

/// <summary>
/// <c>Essence.Ingest --try-hours-plugin &lt;spec.json | name&gt;</c>: fetches a plugin's answer as a run would, and prints
/// where its fields were found, how many records it read and the first ones' hours, and its warnings. It writes nothing,
/// and needs no database: for writing a spec, or checking one on the server.
/// </summary>
public static class HoursPluginTry
{
    public const string Argument = "--try-hours-plugin";

    private const int Shown = 10;

    public static async Task<int> RunAsync(string specOrName, HoursOptions options, TextWriter output, CancellationToken cancellationToken)
    {
        HoursPluginSpec spec;
        try
        {
            spec = File.Exists(specOrName)
                ? HoursPluginSpec.Parse(await File.ReadAllBytesAsync(specOrName, cancellationToken), specOrName)
                : HoursPluginCatalog.Load(options.PluginsPath, new HashSet<string>(StringComparer.Ordinal)).Find(specOrName)
                    ?? throw new HoursPluginSpecException($"No file {specOrName}, and no plugin of that name shipped or in {options.PluginsPath}.");
        }
        catch (HoursPluginSpecException ex)
        {
            await output.WriteLineAsync(ex.Message);
            return 2;
        }

        using var handler = new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All };
        using var http = new HttpClient(handler) { Timeout = options.RequestTimeout, MaxResponseContentBufferSize = 32 * 1024 * 1024 };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);
        var wrapped = Options.Create(options);
        var source = new HoursPluginSource(spec, new HoursClient(http), new HoursPacer(wrapped, TimeProvider.System, Random.Shared), wrapped);

        await output.WriteLineAsync($"{spec.Name} ({spec.Label}, {Essence.Ranking.HoursSourceKinds.Name(spec.Kind)}, rank {spec.Rank}) from {spec.Origin}");
        foreach (var url in spec.Urls())
        {
            await output.WriteLineAsync($"  asks {url}");
        }

        try
        {
            var (raw, mappings) = await source.DownloadAsync(cancellationToken);
            foreach (var mapping in mappings)
            {
                await output.WriteLineAsync("Fields found:");
                foreach (var (field, path) in mapping.Describe())
                {
                    await output.WriteLineAsync($"  {field,-16} {path}");
                }
            }

            var parsed = source.Parse(raw);
            await output.WriteLineAsync($"{parsed.Records.Count} records with hours, {parsed.Warnings.Count} warnings.");
            foreach (var record in parsed.Records.Take(Shown))
            {
                await output.WriteLineAsync(
                    $"  {record.Ref,-12} {record.BrandFamily ?? "(no brand)",-14} {record.Latitude:0.00000},{record.Longitude:0.00000} {record.HouseNumber} {record.Postcode}  {record.Rule}{(record.TemporarilyClosed ? " (closed for now)" : "")}");
            }

            foreach (var warning in parsed.Warnings.Take(30))
            {
                await output.WriteLineAsync($"  warning: {warning}");
            }

            return parsed.Records.Count > 0 ? 0 : 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or HoursFormatException or TaskCanceledException or JsonException)
        {
            await output.WriteLineAsync($"Failed: {ex.Message}");
            return 1;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Not what a run expects: said as it is, for whoever writes the spec, rather than a crash.
            await output.WriteLineAsync($"Failed unexpectedly ({ex.GetType().Name}): {ex.Message}");
            return 1;
        }
    }
}
