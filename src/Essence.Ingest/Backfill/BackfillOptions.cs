namespace Essence.Ingest.Backfill;

/// <summary>The <c>Backfill</c> configuration section: recovering missed versions from the Régie's dated xlsx files.</summary>
public sealed class BackfillOptions
{
    public const string SectionName = "Backfill";

    public bool Enabled { get; set; } = true;

    /// <summary>Folder of the <c>stations-{yyyyMMddHHmmss}.xlsx</c> files (the feed's <c>metadata.excel_url</c>). Ends with a slash.</summary>
    public Uri BaseUrl { get; set; } = new("https://regieessencequebec.ca/data/");

    /// <summary>Slots older than this aren't requested: the server keeps the files for about an hour.</summary>
    public TimeSpan MaxAge { get; set; } = TimeSpan.FromMinutes(70);
}
