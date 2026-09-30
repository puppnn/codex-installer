using System.Net;
using CodexUpdater.App;

namespace CodexUpdater.Tests;

public sealed class DownloadRedirectTests
{
    [Theory]
    [InlineData("https://example.test/package.msix")]
    [InlineData("http://delivery.mp.microsoft.com/package.msix")]
    public async Task Redirect_RejectsUnsafeHopBeforeRequestingIt(string destination)
    {
        using var handler = new StubHandler(_ => Redirect(destination));
        using var client = new HttpClient(handler);
        const string original = "https://delivery.mp.microsoft.com/start";
        await Assert.ThrowsAsync<InvalidOperationException>(() => PackageDownloadService.GetTrustedResponseAsync(client, original, original, CancellationToken.None));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Redirect_FollowsTrustedRelativeHop()
    {
        using var handler = new StubHandler(uri => uri.AbsolutePath == "/start" ? Redirect("/package.msix") : new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new HttpClient(handler);
        const string original = "https://delivery.mp.microsoft.com/start";
        using var response = await PackageDownloadService.GetTrustedResponseAsync(client, original, original, CancellationToken.None);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Redirect_LimitsLoops()
    {
        using var handler = new StubHandler(_ => Redirect("/loop"));
        using var client = new HttpClient(handler);
        const string original = "https://delivery.mp.microsoft.com/start";
        await Assert.ThrowsAsync<HttpRequestException>(() => PackageDownloadService.GetTrustedResponseAsync(client, original, original, CancellationToken.None));
        Assert.Equal(6, handler.Requests.Count);
    }

    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
        return response;
    }

    private sealed class StubHandler(Func<Uri, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            var response = responder(request.RequestUri!);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
