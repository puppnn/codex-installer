using System.Net;
using System.Net.Http.Headers;
using CodexUpdater.App;
using CodexUpdater.Core;

namespace CodexUpdater.Tests;

public sealed class PackageTransferTests
{
    private const string Url = "https://delivery.mp.microsoft.com/package";
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
    private static IProgress<double> Progress => new InlineProgress<double>(_ => { });

    [Fact]
    public async Task InterruptedTransfer_ResumesOnlyWithMatchingValidatorAndRange()
    {
        var calls = 0;
        using var output = new MemoryStream();
        await Download(output, request =>
        {
            if (++calls == 1) return Response("abc", 6, "\"v1\"");
            Assert.Equal(3, request.Headers.Range!.Ranges.Single().From);
            Assert.Equal("\"v1\"", request.Headers.IfRange!.EntityTag!.Tag);
            return Response("def", 3, "\"v1\"", 3, 6);
        });
        Assert.Equal("abcdef", System.Text.Encoding.UTF8.GetString(output.ToArray()));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task IgnoredRange_RestartsInsteadOfAppending()
    {
        var calls = 0;
        using var output = new MemoryStream();
        await Download(output, _ => ++calls == 1 ? Response("abc", 6, "\"v1\"") : Response("uvwxyz", 6, "\"v2\""));
        Assert.Equal("uvwxyz", System.Text.Encoding.UTF8.GetString(output.ToArray()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("W/\"v1\"")]
    public async Task MissingOrWeakValidator_DoesNotResume(string? etag)
    {
        var calls = 0;
        using var output = new MemoryStream();
        await Download(output, request =>
        {
            Assert.Null(request.Headers.Range);
            return ++calls == 1 ? Response("abc", 6, etag) : Response("abcdef", 6, etag);
        });
        Assert.Equal(6, output.Length);
    }

    [Fact]
    public async Task WrongResumeIdentity_RestartsFromZero()
    {
        var calls = 0;
        using var output = new MemoryStream();
        await Download(output, request =>
        {
            calls++;
            if (calls == 1) return Response("abc", 6, "\"v1\"");
            if (calls == 2) return Response("XYZ", 3, "\"v2\"", 3, 6);
            Assert.Null(request.Headers.Range);
            return Response("uvwxyz", 6, "\"v2\"");
        });
        Assert.Equal("uvwxyz", System.Text.Encoding.UTF8.GetString(output.ToArray()));
    }

    [Fact]
    public async Task ReadIOException_RetriesAndResumes()
    {
        var calls = 0;
        using var output = new MemoryStream();
        await Download(output, request =>
        {
            if (++calls == 1)
            {
                var response = Response("", 6, "\"v1\"");
                response.Content = new StreamContent(new BrokenStream());
                response.Content.Headers.ContentLength = 6;
                return response;
            }
            Assert.Equal(3, request.Headers.Range!.Ranges.Single().From);
            return Response("def", 3, "\"v1\"", 3, 6);
        });
        Assert.Equal("abcdef", System.Text.Encoding.UTF8.GetString(output.ToArray()));
    }

    [Fact]
    public async Task AutoRoute_FallsBackWithoutChangingGlobalProxy()
    {
        var routes = new List<bool>();
        using var output = new MemoryStream();
        await PackageTransferService.DownloadAsync(Url, output, 100, DownloadRoute.Auto, Progress, null, CancellationToken.None,
            direct =>
            {
                routes.Add(direct);
                return new HttpClient(new Handler(_ => direct ? throw new HttpRequestException("connection failed") : Response("abc", 3, null)));
            });
        Assert.Equal(new[] { true, false }, routes);
        Assert.Equal(3, output.Length);
    }

    [Fact]
    public async Task SizeLimit_StopsBeforeReadingTooMuch()
    {
        using var output = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => Download(output, _ => Response("abcdef", 6, null), limit: 4));
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public async Task Cancellation_DoesNotBecomeRetry()
    {
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        using var output = new MemoryStream();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PackageTransferService.DownloadAsync(
            Url, output, 100, DownloadRoute.Auto, Progress, null, cancellation.Token,
            _ => new HttpClient(new Handler(request =>
            {
                calls++;
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            }))));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void HttpsOnlySource_NeverGetsHttpFallback()
    {
        Assert.All(PackageTransferService.Endpoints(Url, DownloadRoute.Auto), endpoint => Assert.StartsWith("https://", endpoint.Url));
        Assert.All(PackageTransferService.Endpoints(Url, DownloadRoute.Direct), endpoint => Assert.True(endpoint.Direct));
        Assert.All(PackageTransferService.Endpoints(Url, DownloadRoute.SystemProxy), endpoint => Assert.False(endpoint.Direct));
    }

    [Fact]
    public async Task WrongRangeOffset_DiscardsPartialResponse()
    {
        var calls = 0;
        using var output = new MemoryStream();
        await Download(output, request =>
        {
            if (++calls == 1) return Response("abc", 6, "\"v1\"");
            if (calls == 2) return Response("cdef", 4, "\"v1\"", 2, 6);
            Assert.Null(request.Headers.Range);
            return Response("abcdef", 6, "\"v1\"");
        });
        Assert.Equal("abcdef", System.Text.Encoding.UTF8.GetString(output.ToArray()));
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task RangeNotSatisfiable_RestartsSafely()
    {
        var calls = 0;
        using var output = new MemoryStream();
        await Download(output, request =>
        {
            if (++calls == 1) return Response("abc", 6, "\"v1\"");
            if (calls == 2) return new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable);
            Assert.Null(request.Headers.Range);
            return Response("abcdef", 6, "\"v1\"");
        });
        Assert.Equal(6, output.Length);
    }

    [Fact]
    public async Task RepeatedNetworkFailures_AreBounded()
    {
        var calls = 0;
        using var output = new MemoryStream();
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => Download(output, _ =>
        {
            calls++;
            throw new HttpRequestException("upstream reset");
        }));
        Assert.Equal(6, calls);
        Assert.Contains("下载连续失败", error.Message);
    }

    [Fact]
    public async Task ExpiredLinkStatus_IsPreservedForCallerRefresh()
    {
        using var output = new MemoryStream();
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => Download(output,
            _ => new HttpResponseMessage(HttpStatusCode.Gone)));
        Assert.Equal(HttpStatusCode.Gone, error.StatusCode);
    }

    [Fact]
    public async Task DiskWriteFailure_IsNotTreatedAsNetworkFailure()
    {
        var calls = 0;
        using var output = new UnwritableStream();
        await Assert.ThrowsAsync<IOException>(() => Download(output, _ =>
        {
            calls++;
            return Response("abc", 3, null);
        }));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task SavedResumeState_ContinuesExistingBytesWithIfRange()
    {
        var calls = 0;
        using var output = Existing("abc");
        await ResumeDownload(output, Resume("\"v1\"", 6), request =>
        {
            calls++;
            Assert.Equal(3, request.Headers.Range!.Ranges.Single().From);
            Assert.Equal("\"v1\"", request.Headers.IfRange!.EntityTag!.Tag);
            return Response("def", 3, "\"v1\"", 3, 6);
        });
        Assert.Equal("abcdef", System.Text.Encoding.UTF8.GetString(output.ToArray()));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task SavedResumeState_RestartsAndReportsNewValidatorWhenServerFileChanged()
    {
        var validators = new List<(string?, long?)>();
        using var output = Existing("abc");
        await ResumeDownload(output, Resume("\"v1\"", 6), _ => Response("uvwxyz", 6, "\"v2\""), validators);
        Assert.Equal("uvwxyz", System.Text.Encoding.UTF8.GetString(output.ToArray()));
        Assert.Equal(("\"v2\"", (long?)6), validators.Last());
    }

    [Fact]
    public async Task CompleteSavedDownload_SkipsNetwork()
    {
        using var output = Existing("abcdef");
        await ResumeDownload(output, Resume("\"v1\"", 6), _ => throw new InvalidOperationException("network must not be used"));
        Assert.Equal(6, output.Length);
    }

    [Fact]
    public async Task OversizedSavedState_IsIgnored()
    {
        using var output = Existing("abc");
        await ResumeDownload(output, Resume("\"v1\"", 1000), request =>
        {
            Assert.Null(request.Headers.Range);
            return Response("abcdef", 6, "\"v1\"");
        });
        Assert.Equal(6, output.Length);
    }

    [Fact]
    public async Task MismatchedResumeResponse_ClearsSavedValidator()
    {
        var calls = 0;
        var validators = new List<(string?, long?)>();
        using var output = Existing("abc");
        await ResumeDownload(output, Resume("\"v1\"", 6), request =>
        {
            if (++calls == 1) return Response("XYZ", 3, "\"v2\"", 3, 6);
            Assert.Null(request.Headers.Range);
            return Response("uvwxyz", 6, "\"v2\"");
        }, validators);
        Assert.Equal(((string?)null, (long?)null), validators.First());
        Assert.Equal("uvwxyz", System.Text.Encoding.UTF8.GetString(output.ToArray()));
    }

    [Fact]
    public void LegacySettings_DefaultToAutoRoute()
    {
        var settings = System.Text.Json.JsonSerializer.Deserialize<UserSettings>("{\"DownloadDirectory\":\"C:\\\\Packages\"}");
        Assert.NotNull(settings);
        Assert.Equal(DownloadRoute.Auto, settings.DownloadRoute);
    }

    private sealed class UnwritableStream : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => throw new IOException("disk full");
    }

    private static Task Download(Stream output, Func<HttpRequestMessage, HttpResponseMessage> handler, long limit = 100) =>
        PackageTransferService.DownloadAsync(Url, output, limit, DownloadRoute.Direct, Progress, null, CancellationToken.None,
            _ => new HttpClient(new Handler(handler)));

    private static MemoryStream Existing(string data)
    {
        var stream = new MemoryStream();
        stream.Write(System.Text.Encoding.UTF8.GetBytes(data));
        return stream;
    }

    private static PartialDownloadState Resume(string etag, long total) =>
        new("package.msix", null, etag, total, DateTimeOffset.UtcNow);

    private static Task ResumeDownload(Stream output, PartialDownloadState resume,
        Func<HttpRequestMessage, HttpResponseMessage> handler, List<(string?, long?)>? validators = null) =>
        PackageTransferService.DownloadAsync(Url, output, 100, DownloadRoute.Direct, Progress, null, CancellationToken.None,
            _ => new HttpClient(new Handler(handler)), resume, (etag, total) => validators?.Add((etag, total)));

    private static HttpResponseMessage Response(string data, long length, string? etag, long? start = null, long? total = null)
    {
        var response = new HttpResponseMessage(start is null ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
        { Content = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(data)) };
        response.Content.Headers.ContentLength = length;
        if (etag is not null) response.Headers.ETag = EntityTagHeaderValue.Parse(etag);
        if (start is not null) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start.Value, total!.Value - 1, total.Value);
        return response;
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = responder(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    private sealed class BrokenStream : MemoryStream
    {
        private bool _read;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_read) throw new IOException("Response ended prematurely");
            _read = true;
            System.Text.Encoding.UTF8.GetBytes("abc").CopyTo(buffer);
            return ValueTask.FromResult(3);
        }
    }
}
