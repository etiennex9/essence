namespace Essence.Ranking.Tests;

public class EligibilityTests
{
    [Fact]
    public void An_operating_station_with_an_available_price() =>
        Assert.True(Eligibility.IsEligible("En opération", true, 159.9m, membersOnly: false, isMember: false));

    [Theory]
    [InlineData("Fermé temporairement")]
    [InlineData("En operation")]
    [InlineData("")]
    public void Other_statuses_are_left_out(string status) =>
        Assert.False(Eligibility.IsEligible(status, true, 159.9m, false, false));

    [Fact]
    public void An_unavailable_price_is_left_out() =>
        Assert.False(Eligibility.IsEligible("En opération", false, 159.9m, false, false));

    [Fact]
    public void A_missing_price_is_left_out() =>
        Assert.False(Eligibility.IsEligible("En opération", true, null, false, false));

    [Fact]
    public void Costco_only_for_members()
    {
        Assert.False(Eligibility.IsEligible("En opération", true, 155.9m, membersOnly: true, isMember: false));
        Assert.True(Eligibility.IsEligible("En opération", true, 155.9m, membersOnly: true, isMember: true));
    }
}
