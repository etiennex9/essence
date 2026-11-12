using Essence.UI.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Essence.UI.Tests;

public class ServiceCollectionTests
{
    [Fact]
    public void The_web_app_gets_one_client_for_the_api_it_names()
    {
        using var services = new ServiceCollection()
            .AddEssenceUI(new EssenceUIOptions { ApiBaseUrl = new Uri("https://example.com/api") })
            .BuildServiceProvider();

        Assert.Same(services.GetRequiredService<EssenceApiClient>(), services.GetRequiredService<EssenceApiClient>());
    }

    [Fact]
    public void A_host_can_bring_its_own_client_instead()
    {
        var own = new EssenceApiClient(new HttpClient { BaseAddress = new Uri("https://example.com/api/") });
        using var services = new ServiceCollection()
            .AddScoped(_ => own)
            .AddEssenceUI(new EssenceUIOptions())
            .BuildServiceProvider();

        using var scope = services.CreateScope();
        Assert.Same(own, scope.ServiceProvider.GetRequiredService<EssenceApiClient>());
    }

    [Fact]
    public void A_host_s_own_clock_is_kept()
    {
        // The web app's simulated time, in Development.
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        using var services = new ServiceCollection()
            .AddSingleton<TimeProvider>(clock)
            .AddEssenceUI(new EssenceUIOptions { ApiBaseUrl = new Uri("https://example.com/api") })
            .BuildServiceProvider();

        Assert.Same(clock, services.GetRequiredService<TimeProvider>());
    }

    [Fact]
    public void Without_an_api_nor_a_client_it_says_what_s_missing()
    {
        var error = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddEssenceUI(new EssenceUIOptions()));

        Assert.Contains(nameof(EssenceUIOptions.ApiBaseUrl), error.Message);
    }
}
