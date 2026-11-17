using Essence.UI.App;

namespace Essence.UI.Tests;

/// <summary>
/// The Android app's essence:// links, as its DeepLinks and OsmAuthCallbackActivity read them: a widget's into the page, and
/// OpenStreetMap's sign-in, which must never be taken for one.
/// </summary>
public class AppLinksTests
{
    [Theory]
    [InlineData("essence://open/?station=12", "/?station=12")]
    [InlineData("essence://open/?view=best", "/?view=best")]
    [InlineData("essence://open/?to=home&from=work&pick=3", "/?to=home&from=work&pick=3")]
    [InlineData("essence://open/", "/")]
    public void A_widget_s_link_opens_the_map_page_on_its_parameters(string link, string path) =>
        Assert.Equal(path, AppLinks.PathOf(new Uri(link)));

    [Theory]
    [InlineData("essence://osm-auth?code=abc&state=xyz")]
    [InlineData("essence://osm-auth/?view=best")]
    [InlineData("essence://open/?code=abc&state=xyz")]
    [InlineData("essence://open/other?station=12")]
    [InlineData("https://open/?station=12")]
    public void Anything_else_isn_t_a_path_into_the_page_OpenStreetMap_s_answer_above_all(string link) =>
        Assert.Null(AppLinks.PathOf(new Uri(link)));

    [Fact]
    public void OpenStreetMap_answers_on_its_own_host_with_nothing_after_it()
    {
        Assert.Equal("essence://osm-auth", AppLinks.OsmAuthCallback);
        Assert.NotEqual(AppLinks.OpenHost, new Uri(AppLinks.OsmAuthCallback).Host);
    }
}
