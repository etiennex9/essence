namespace Essence.Data.Entities;

/// <summary>
/// An Android installation that called the API on a day (<c>usage_android</c>), with the build it ran: one row per day,
/// installation and version code, written by the API (its <c>essence_usage</c> role) and read by the usage dashboard
/// (README → Usage). An installation that updated during the day has a row for each build.
/// </summary>
public sealed class AndroidUsage
{
    /// <summary>Québec's day.</summary>
    public DateOnly Day { get; set; }

    /// <summary>A hash of the id the installation sends (<c>Essence-Install</c>), never the id itself.</summary>
    public Guid Install { get; set; }

    /// <summary>The build's version code, which grows with every build.</summary>
    public long VersionCode { get; set; }

    /// <summary>The build's version name, e.g. <c>2026.11.18.2</c>.</summary>
    public required string Version { get; set; }
}

/// <summary>
/// A browser that called the API from the web app on a day (<c>usage_web</c>). The visitor is a hash of its address and
/// User-Agent with that day's <see cref="UsageSalt"/>, which is deleted the next day: the same browser is another visitor
/// every day, and no address can be found from a row.
/// </summary>
public sealed class WebUsage
{
    /// <summary>Québec's day.</summary>
    public DateOnly Day { get; set; }

    public Guid Visitor { get; set; }

    /// <summary>The operating system its User-Agent names: <c>android</c>, <c>ios</c>, <c>windows</c>, <c>macos</c>, <c>linux</c>, <c>chromeos</c> or <c>other</c>.</summary>
    public required string Os { get; set; }
}

/// <summary>
/// The secret mixed into the day's <see cref="WebUsage.Visitor"/> hashes (<c>usage_salts</c>): made by the API on the
/// day's first web visit, kept so that a restart counts the same visitors, and deleted once the day is over. Only the
/// <c>essence_usage</c> role can read it.
/// </summary>
public sealed class UsageSalt
{
    /// <summary>Québec's day.</summary>
    public DateOnly Day { get; set; }

    public required byte[] Salt { get; set; }
}
