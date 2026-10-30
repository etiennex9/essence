namespace Essence.Ranking.Tests;

public class CostsTests
{
    private static readonly Car Mazda3 = new(40, 8.5m);

    [Fact]
    public void A_fill_counts_the_gas_burned_there_and_back()
    {
        // The mockup's Costco: 155.9 at 9.0 km, so 18 km there and back: 1.559 × (40 + 18 × 0.085) = 64.745…
        Assert.Equal(64.75m, Costs.Fill(155.9m, Mazda3, 18));

        // Its Ultramar: 166.9 at 0.8 km: 1.669 × (40 + 1.6 × 0.085) = 66.987…
        Assert.Equal(66.99m, Costs.Fill(166.9m, Mazda3, 1.6));
    }

    [Fact]
    public void Without_driving_it_is_the_price_of_the_fill() =>
        Assert.Equal(66.76m, Costs.Fill(166.9m, Mazda3, 0));

    [Fact]
    public void Half_cents_round_away_from_zero() =>
        // 1.25 × 10.02 = 12.525
        Assert.Equal(12.53m, Costs.Fill(125m, new Car(10.02m, 8.5m), 0));
}
