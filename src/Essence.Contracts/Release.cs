using System.Text.Json;
using System.Text.Json.Serialization;

namespace Essence.Contracts;

// The release notes the web app serves under release/: written at build time by tools/Essence.Release from git history,
// read by Essence.UI's About dialog. README → Versions has the whole picture.

/// <summary>release.json: the version the app was built at and the months its changelog spans.</summary>
/// <param name="Version">
/// The day (America/Toronto) the newest change in the build landed on main. Null for a commit no day tag contains yet: a
/// pull request, a local branch.
/// </param>
/// <param name="Commit">The commit built, abbreviated.</param>
/// <param name="Months">The months with changes ("2026-10"), newest first. Each one is <c>changelog-{month}.json</c>.</param>
/// <param name="Unreleased">Changes not on main yet: only when <paramref name="Version"/> is null.</param>
public sealed record ReleaseInfo(DateOnly? Version, string? Commit, IReadOnlyList<string> Months, IReadOnlyList<ChangelogEntry> Unreleased);

/// <summary><c>changelog-{month}.json</c>: that month's versions, newest first.</summary>
public sealed record ChangelogMonth(IReadOnlyList<ChangelogVersion> Versions);

/// <summary>Everything that landed on main on one day.</summary>
public sealed record ChangelogVersion(DateOnly Date, IReadOnlyList<ChangelogEntry> Changes);

/// <summary>One change, as its <c>changelog/*.json</c> file describes it.</summary>
public sealed record ChangelogEntry(string En, string Fr);

/// <summary>Source-generated (trim-safe) serialization of the release notes, with web defaults (camelCase).</summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(ReleaseInfo))]
[JsonSerializable(typeof(ChangelogMonth))]
public sealed partial class ReleaseJsonContext : JsonSerializerContext;
