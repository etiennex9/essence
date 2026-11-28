using System.Net;
using Essence.UI.Services;

namespace Essence.UI.Tests;

/// <summary>Android's handler, unable to reach the server, throws what <see cref="NetworkFailureHandler"/> translates.</summary>
public class NetworkFailureHandlerTests
{
    public static TheoryData<string> Failures => ["web", "io"];

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task A_server_that_can_t_be_reached_is_a_failed_call_not_an_exception(string failure)
    {
        var client = Client((_, _) => throw Failure(failure));

        var nearby = await client.GetNearbyAsync(45.5, -73.56, 2, "Régulier", 50, TestContext.Current.CancellationToken);

        Assert.Equal(ApiError.Failed, nearby.Error);
    }

    [Fact]
    public async Task What_Android_throws_becomes_an_HttpRequestException()
    {
        using var http = new HttpClient(new NetworkFailureHandler(new Throwing((_, _) => throw Failure("web"))));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync("http://localhost:8080/api", TestContext.Current.CancellationToken));

        Assert.IsType<WebException>(error.InnerException);
    }

    [Fact]
    public async Task A_call_the_caller_cancels_still_throws_its_cancellation()
    {
        using var cancel = new CancellationTokenSource();
        var client = Client((_, _) =>
        {
            // Android closes the socket of a cancelled request: "Socket closed".
            cancel.Cancel();
            throw Failure("web");
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetNearbyAsync(45.5, -73.56, 2, "Régulier", 50, cancel.Token));
    }

    [Fact]
    public async Task A_call_that_times_out_is_a_failed_call()
    {
        var client = Client(
            async (_, token) =>
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, token);
                }
                catch (OperationCanceledException)
                {
                }

                throw Failure("web");
            },
            TimeSpan.FromMilliseconds(50));

        var nearby = await client.GetNearbyAsync(45.5, -73.56, 2, "Régulier", 50, TestContext.Current.CancellationToken);

        Assert.Equal(ApiError.Failed, nearby.Error);
    }

    /// <summary>What Android's handler threw on the phone, its Java cause aside.</summary>
    private static Exception Failure(string kind) => kind == "web"
        ? new WebException("Socket closed", new IOException("Socket closed"), WebExceptionStatus.ConnectFailure, null)
        : new IOException("unexpected end of stream on http://localhost:8080/...");

    private static EssenceApiClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send, TimeSpan? timeout = null) =>
        new(new HttpClient(new NetworkFailureHandler(new Throwing(send)))
        {
            BaseAddress = new Uri("http://localhost:8080/api/"),
            Timeout = timeout ?? TimeSpan.FromSeconds(20),
        });

    private sealed class Throwing(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
