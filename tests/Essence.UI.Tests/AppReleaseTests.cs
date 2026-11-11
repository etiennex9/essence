using Essence.UI.App;

namespace Essence.UI.Tests;

public sealed class AppReleaseTests
{
    [Theory]
    [InlineData("android-2026.11.10", "2026.11.10")]
    [InlineData("android-2026.11.10.2", "2026.11.10.2")]
    // The first releases sat on the day's tag.
    [InlineData("v2026-11-10", "2026-11-10")]
    [InlineData(null, "")]
    public void A_release_shows_the_version_its_tag_names(string? tag, string version) =>
        Assert.Equal(version, AppRelease.VersionOfTag(tag));
}
