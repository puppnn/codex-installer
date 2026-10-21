using System.Net;
using System.Net.Http.Headers;
using System.Text;
using CodexUpdater.App;
using CodexUpdater.Core;

namespace CodexUpdater.Tests;

public sealed class PersistentDownloadTests : IDisposable
{
    private const string Name = "Contoso.App_1.0.0.0_x64__1234567890123.msix";
    private const string Url = "https://delivery.mp.microsoft.com/file";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "codex-persist-" + Guid.NewGuid().ToString("N"));
    private readonly IProgress<double> _progress = new Progress<double>();
    public PersistentDownloadTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);

    private Task<PackageDownloadService.DownloadResult<string>> Download(
        Func<HttpRequestMessage, HttpResponseMessage> respond, Func<string, string>? validate = null, CancellationToken token = default) =>
        PackageDownloadService.DownloadAndValidateAsync(Name, Url, _directory, null, _progress,
            validate ?? File.ReadAllText, token, clientFactory: _ => new HttpClient(new Handler(respond)));

    [Fact]
    public async Task CancelledDownload_ResumesFromPersistedBytesInNewOperation()
    {
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Download(_ =>
        {
            var response = Response("", 6);
            response.Content = new StreamContent(new CancelAfterPrefixStream(cancellation));
            response.Content.Headers.ContentLength = 6;
            return response;
        }, token: cancellation.Token));

        using (var cache = PartialDownloadCache.Acquire(_directory, Name))
        {
            Assert.Equal("abc", File.ReadAllText(cache.PartialPath));
            Assert.NotNull(PartialDownloadState.TryLoad(cache.StatePath, Name, null, 3, 100, DateTimeOffset.UtcNow));
        }
        var result = await Download(request =>
        {
            Assert.Equal(3, request.Headers.Range!.Ranges.Single().From);
            return Response("def", 3, start: 3);
        });
        Assert.Equal("abcdef", result.Validation);
        using var completed = PartialDownloadCache.Acquire(_directory, Name);
        Assert.False(File.Exists(completed.PartialPath));
        Assert.False(File.Exists(completed.StatePath));
    }

    [Fact]
    public async Task FullCachedFile_IsValidatedWithoutNetwork()
    {
        Seed("abcdef");
        var result = await Download(_ => throw new Exception("Network must not be called"));
        Assert.Equal("abcdef", result.Validation);
    }

    [Fact]
    public async Task ValidationKeepsLeaseUntilPromotion()
    {
        Seed("abcdef");
        using var entered = new ManualResetEventSlim();
        using var proceed = new ManualResetEventSlim();
        var first = Download(_ => throw new Exception("No network expected"), path =>
        {
            entered.Set();
            if (!proceed.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("test release missing");
            return File.ReadAllText(path);
        });
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            await Assert.ThrowsAsync<IOException>(() => Download(_ => throw new Exception("Second download must be locked")));
        }
        finally { proceed.Set(); }
        Assert.Equal("abcdef", (await first).Validation);
    }

    [Fact]
    public async Task InvalidCompletedPartial_IsRedownloadedOnlyOnce()
    {
        Seed("badbad");
        var calls = 0;
        var result = await Download(_ => { calls++; return Response("abcdef", 6); }, path =>
        {
            var data = File.ReadAllText(path);
            return data == "abcdef" ? data : throw new InvalidDataException("hash mismatch");
        });
        Assert.Equal(1, calls);
        Assert.Equal("abcdef", result.Validation);
    }

    private void Seed(string data)
    {
        using var cache = PartialDownloadCache.Acquire(_directory, Name);
        File.WriteAllText(cache.PartialPath, data);
        new PartialDownloadState(Name, null, "\"v1\"", 6, DateTimeOffset.UtcNow).Save(cache.StatePath);
    }

    private static HttpResponseMessage Response(string content, long length, long? start = null)
    {
        var response = new HttpResponseMessage(start is null ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
        { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(content)) };
        response.Headers.ETag = new EntityTagHeaderValue("\"v1\"");
        response.Content.Headers.ContentLength = length;
        if (start is not null) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start.Value, 5, 6);
        return response;
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = respond(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    private sealed class CancelAfterPrefixStream(CancellationTokenSource cancellation) : MemoryStream
    {
        private bool _read;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_read) { cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); }
            _read = true;
            Encoding.UTF8.GetBytes("abc").CopyTo(buffer);
            return ValueTask.FromResult(3);
        }
    }
}
