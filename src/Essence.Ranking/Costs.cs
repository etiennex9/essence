namespace Essence.Ranking;

/// <summary>The car a ranking is worked out for.</summary>
/// <param name="FillLitres">What the driver buys at a stop.</param>
/// <param name="LitresPer100Km">The car's consumption.</param>
public sealed record Car(decimal FillLitres, decimal LitresPer100Km);

public static class Costs
{
    /// <summary>
    /// What a fill-up costs once the driving it takes is counted: price (¢/L) ÷ 100 × (fill + extra km × L/100 km ÷ 100),
    /// to the cent. Savings are differences of these rounded costs, so they match the costs shown next to them.
    /// </summary>
    /// <param name="extraKm">What getting there adds to the drive.</param>
    public static decimal Fill(decimal priceCents, Car car, double extraKm)
    {
        var litres = car.FillLitres + (decimal)extraKm * car.LitresPer100Km / 100;
        return Math.Round(priceCents / 100 * litres, 2, MidpointRounding.AwayFromZero);
    }
}
