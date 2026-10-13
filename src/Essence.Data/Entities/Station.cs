namespace Essence.Data.Entities;

/// <summary>A fuel station. Identified by <see cref="NaturalKey"/> because the feed has no station ID.</summary>
public sealed class Station
{
    public long Id { get; set; }

    /// <summary>Normalized address + postal code, see <see cref="StationKey"/>.</summary>
    public required string NaturalKey { get; set; }

    public string? Name { get; set; }
    public string? Brand { get; set; }
    public required string Status { get; set; }
    public required string Address { get; set; }
    public required string PostalCode { get; set; }
    public required string Region { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }

    /// <summary>False once the station disappears from the feed; set back to true if it returns.</summary>
    public bool IsActive { get; set; }

    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public ICollection<CurrentPrice> CurrentPrices { get; } = [];
}
