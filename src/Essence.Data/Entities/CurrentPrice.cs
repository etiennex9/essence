namespace Essence.Data.Entities;

/// <summary>The latest price of each fuel a station currently sells: a copy of its open <see cref="PriceHistory"/> row.</summary>
public sealed class CurrentPrice
{
    public long StationId { get; set; }
    public short FuelTypeId { get; set; }
    public decimal? PriceCents { get; set; }
    public bool IsAvailable { get; set; }
    public DateTimeOffset Since { get; set; }
    public long PriceHistoryId { get; set; }

    public Station Station { get; set; } = null!;
    public FuelType FuelType { get; set; } = null!;
    public PriceHistory PriceHistory { get; set; } = null!;
}
