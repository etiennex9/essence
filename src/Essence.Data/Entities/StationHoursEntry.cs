namespace Essence.Data.Entities;

/// <summary>
/// A station's opening hours as one source gives them (<c>station_hours</c>): one row per station and source, so the API
/// can choose between sources, and a source's run replaces only its own rows.
/// </summary>
public sealed class StationHoursEntry
{
    /// <summary>Found by the station's natural key: a curated rule written for that station.</summary>
    public const string ByNaturalKey = "natural_key";

    /// <summary>The store number in the station's name: Couche-Tard's "Couche-Tard #140" is its site 6000140, wherever it is.</summary>
    public const string ByStoreNumber = "store_number";

    /// <summary>Within the source's radius of the station, and of the same brand.</summary>
    public const string ByDistance = "distance";

    /// <summary>
    /// Beyond the source's radius, but of the same brand with the same house number and postal code, within 2 km
    /// (<c>HoursMatcher.AddressReachM</c>): one of the two points is off.
    /// </summary>
    public const string ByAddress = "address";

    /// <summary>A rule for every station of the brand.</summary>
    public const string ByBrand = "brand";

    /// <summary>
    /// OpenStreetMap's element on the station, of another brand or none, its brand being out of date
    /// (<c>HoursMatcher</c>): never confirmed, and without the element's hours, which are likely out of date too. It
    /// says which element to edit.
    /// </summary>
    public const string ByLocation = "location";

    public long Id { get; set; }
    public long StationId { get; set; }

    /// <summary>The source's name, as in <see cref="HoursImport.Source"/>.</summary>
    public required string Source { get; set; }

    /// <summary>
    /// The canonical opening_hours rule (Essence.Hours' <c>OpeningHours.ToRule</c>). Null when the source has the station
    /// but not its hours, as OpenStreetMap often does, or when they aren't taken (<see cref="ByLocation"/>): the row still
    /// says which element to edit.
    /// </summary>
    public string? OpeningHours { get; set; }

    /// <summary>The source's own id for the place: Costco's warehouse number, an OpenStreetMap element (<c>node/6756481766</c>)…</summary>
    public required string SourceRef { get; set; }

    /// <summary>Where a person can check the hours.</summary>
    public string? SourceUrl { get; set; }

    /// <summary>
    /// The brand the source writes for the place, as it writes it, when some station of the feed has that brand: for
    /// OpenStreetMap, the element's <c>brand</c>, else its <c>operator</c>, else its <c>name</c> ("Sonic", "Petroles RL").
    /// Null when it writes none, or one no station has (a name of its own, which can be a person's, is never kept), and
    /// for the brands' own locators and the curated rules. For an element matched by <see cref="ByLocation"/>, it tells
    /// a name that's the old brand's from one of its own, for the app setting the station's brand on it.
    /// </summary>
    public string? SourceBrand { get; set; }

    /// <summary>
    /// <see cref="ByNaturalKey"/>, <see cref="ByStoreNumber"/>, <see cref="ByDistance"/>, <see cref="ByAddress"/>,
    /// <see cref="ByBrand"/> or <see cref="ByLocation"/>.
    /// </summary>
    public required string MatchedBy { get; set; }

    /// <summary>How far the source's point is from the station's, for a match by distance, address, location or store number.</summary>
    public int? MatchDistanceM { get; set; }

    /// <summary>
    /// The row is surely about this station: its natural key, its store number, its brand for a brand's rule, for a match
    /// by distance its postal code or house number agrees, and by address both do. A match by location never is.
    /// </summary>
    public bool Confirmed { get; set; }

    /// <summary>The source says the station is closed for now, whatever its hours.</summary>
    public bool TemporarilyClosed { get; set; }

    /// <summary>When the source says the hours were last checked, if it says.</summary>
    public DateTimeOffset? SourceCheckedAt { get; set; }

    /// <summary>
    /// When the download these values came from was made. Only set when the row is added or changes: when the source was
    /// last fetched is in <see cref="HoursImport"/>.
    /// </summary>
    public DateTimeOffset FetchedAt { get; set; }

    /// <summary>The run that added the row or last changed it.</summary>
    public long HoursImportId { get; set; }

    public Station Station { get; set; } = null!;
    public HoursImport HoursImport { get; set; } = null!;
}
