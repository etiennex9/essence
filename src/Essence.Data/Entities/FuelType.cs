namespace Essence.Data.Entities;

/// <summary>A fuel grade as named in the feed ("Régulier", "Super", "Diesel"). Rows are added automatically on first sight.</summary>
public sealed class FuelType
{
    public short Id { get; set; }
    public required string Code { get; set; }
}
