namespace Essence.Ingest.Hours.Plugins;

/// <summary>
/// The hours plugins to run: the specs shipped in the image (<c>Hours/Plugins/*.json</c>, embedded), then those of the
/// server's folder (<see cref="HoursOptions.PluginsPath"/>), one named as a shipped one replacing it. Read once, on a
/// start. A spec that can't be read, one named as another of its folder, or as a source written in code, is left out and
/// said why (<see cref="Problems"/>, logged by <see cref="HoursWorker"/>): a broken spec never stops the ingest or the
/// feed, and never deletes a source's hours (<see cref="KeepRowsOf"/>).
/// </summary>
public sealed class HoursPluginCatalog(
    IReadOnlyList<HoursPluginSpec> specs, IReadOnlyList<string> problems, IReadOnlySet<string>? unreadable = null, bool incomplete = false)
{
    public const string ResourcePrefix = "Essence.Ingest.Hours.Plugins.";

    public static readonly HoursPluginCatalog Empty = new([], []);

    public IReadOnlyList<HoursPluginSpec> Specs { get; } = specs;
    public IReadOnlyList<string> Problems { get; } = problems;

    /// <summary>
    /// The names of the server's specs that are there but can't be read, when they can be told (<see cref="HoursPluginSpec.NameOf"/>),
    /// for the log: not run, and while any is there, no source's rows are removed (<see cref="Incomplete"/>).
    /// </summary>
    public IReadOnlySet<string> Unreadable { get; } = unreadable ?? new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Some server's spec can't be read, or the folder can't be listed: which sources are gone can't be told (a spec broken
    /// may be one renamed, or name another further on), so none is taken for gone on this start.
    /// </summary>
    public bool Incomplete { get; } = incomplete;

    public HoursPluginSpec? Find(string name) => Specs.FirstOrDefault(s => s.Name == name);

    /// <summary>
    /// The sources whose rows stay on a start: those <paramref name="enabled"/> and the server's specs that can't be read.
    /// Null when that can't be told (<see cref="Incomplete"/>): every source's rows stay.
    /// </summary>
    public IReadOnlySet<string>? KeepRowsOf(IEnumerable<string> enabled) =>
        Incomplete ? null : new HashSet<string>([.. enabled, .. Unreadable], StringComparer.Ordinal);

    /// <param name="folder">The server's folder of specs; null, empty or missing: the shipped ones only.</param>
    /// <param name="codeSources">The sources written in code: a plugin can't take one's name while it's there.</param>
    public static HoursPluginCatalog Load(string? folder, IReadOnlySet<string> codeSources)
    {
        var problems = new List<string>();
        var unreadable = new HashSet<string>(StringComparer.Ordinal);
        var incomplete = false;
        var specs = new Dictionary<string, HoursPluginSpec>(StringComparer.Ordinal);
        Add(Shipped(), replacing: false);
        if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder))
        {
            IEnumerable<(string, Func<byte[]>)> files;
            try
            {
                files = Directory.GetFiles(folder, "*.json").Order(StringComparer.Ordinal).Select(f => (f, (Func<byte[]>)(() => File.ReadAllBytes(f)))).ToList();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                problems.Add($"The folder {folder} can't be read: {ex.Message}; no source's hours are removed on this start.");
                (files, incomplete) = ([], true);
            }

            Add(files, replacing: true);
        }

        unreadable.ExceptWith(codeSources);
        return new HoursPluginCatalog([.. specs.Values.OrderBy(s => s.Name, StringComparer.Ordinal)], problems, unreadable, incomplete);

        void Add(IEnumerable<(string Origin, Func<byte[]> Read)> sources, bool replacing)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (origin, read) in sources)
            {
                byte[]? bytes = null;
                HoursPluginSpec spec;
                try
                {
                    bytes = read();
                    spec = HoursPluginSpec.Parse(bytes, origin);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    problems.Add(ex is HoursPluginSpecException ? ex.Message : $"{origin} can't be read: {ex.Message}");
                    if (!replacing)
                    {
                        continue;
                    }

                    // Whose spec it was can't be told for sure (it may have been renamed, or name another source further
                    // on), so no source's hours are removed until every spec of the folder reads.
                    if (bytes is not null && HoursPluginSpec.NameOf(bytes) is { } name)
                    {
                        unreadable.Add(name);
                    }

                    problems.Add($"{origin}: no source's hours are removed on this start, until it reads.");
                    incomplete = true;
                    continue;
                }

                if (codeSources.Contains(spec.Name))
                {
                    problems.Add($"{origin}: \"{spec.Name}\" is a source written in code; a plugin can take its name only once that source is gone.");
                }
                else if (!seen.Add(spec.Name))
                {
                    problems.Add($"{origin}: another spec of the same place is named \"{spec.Name}\" already; this one is left out.");
                }
                else if (replacing || !specs.ContainsKey(spec.Name))
                {
                    specs[spec.Name] = spec;
                }
            }
        }
    }

    /// <summary>The specs shipped in the image, by their resource's file name.</summary>
    public static IEnumerable<(string Origin, Func<byte[]> Read)> Shipped()
    {
        var assembly = typeof(HoursPluginCatalog).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            yield return ($"shipped {resource[ResourcePrefix.Length..]}", () =>
            {
                using var stream = assembly.GetManifestResourceStream(resource)!;
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                return copy.ToArray();
            });
        }
    }
}
