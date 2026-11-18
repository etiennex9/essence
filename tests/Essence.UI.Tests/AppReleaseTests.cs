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

    [Theory]
    [InlineData("essence-449482.apk", 449482L)]
    [InlineData("essence-2.apk", 2L)]
    [InlineData("essence-.apk", null)]
    [InlineData("essence-449482.aab", null)]
    [InlineData("essence-449482.apk.part", null)]
    [InlineData("essence-+12.apk", null)]
    [InlineData("essence-1234567890123456.apk", null)]
    [InlineData("other-449482.apk", null)]
    [InlineData(null, null)]
    public void An_apk_names_its_version_code(string? name, long? code) =>
        Assert.Equal(code, AppRelease.VersionCodeOfApk(name));

    [Fact]
    public void A_sha256_digest_gives_its_hash_in_lowercase()
    {
        var hex = "0A88FF8E28928E4254BC284C4EAA46E5E14382744787DA713FAB868D43D0297E";

        Assert.Equal(hex.ToLowerInvariant(), AppRelease.Sha256OfDigest("sha256:" + hex));
    }

    [Theory]
    [InlineData("sha512:0a88ff8e28928e4254bc284c4eaa46e5e14382744787da713fab868d43d0297e")]
    [InlineData("sha256:0a88ff8e")]
    [InlineData("sha256:0a88ff8e28928e4254bc284c4eaa46e5e14382744787da713fab868d43d0297z")]
    [InlineData(null)]
    public void Another_digest_gives_no_hash(string? digest) =>
        Assert.Null(AppRelease.Sha256OfDigest(digest));
}
