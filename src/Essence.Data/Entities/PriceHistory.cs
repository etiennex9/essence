namespace Essence.Data.Entities;

/// <summary>
/// One price (or availability) state of a station's fuel over <c>[ValidFrom, ValidTo)</c>.
/// A row is only written when something changes; the open row has <see cref="ValidTo"/> = null.
/// </summary>
public sealed class PriceHistory
{
    public long Id { get; set; }
    public long StationId { get; set; }
    public short FuelTypeId { get; set; }

    /// <summary>Cents per litre, null when unavailable.</summary>
    public decimal? PriceCents { get; set; }

    public bool IsAvailable { get; set; }
    public DateTimeOffset ValidFrom { get; set; }
    public DateTimeOffset? ValidTo { get; set; }
    public long ImportId { get; set; }

    public Station Station { get; set; } = null!;
    public FuelType FuelType { get; set; } = null!;
    public Import Import { get; set; } = null!;
}
