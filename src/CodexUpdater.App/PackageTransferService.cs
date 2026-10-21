using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace CodexUpdater.App;

internal static class PackageTransferService
{
    internal sealed record Endpoint(string Url, bool Direct);

    internal static IReadOnlyList<Endpoint> Endpoints(string url, DownloadRoute route)
    {
        var urls = PackageDownloadService.BuildDownloadAttemptUrls(url);
        return urls.SelectMany(address => route switch
        {
            DownloadRoute.Direct => new[] { new Endpoint(address, true) },
            DownloadRoute.SystemProxy => new[] { new Endpoint(address, false) },
            _ => new[] { new Endpoint(address, true), new Endpoint(address, false) },
        }).ToArray();
    }

    public static async Task DownloadAsync(
        string url, Stream destination, long maximumBytes, DownloadRoute route,
        IProgress<double> progress, IProgress<string>? status, CancellationToken cancellationToken,
        Func<bool, HttpClient>? clientFactory = null)
    {
        clientFactory ??= DownloadConnection.Create;
        var endpoints = Endpoints(url, route);
        var clients = new Dictionary<bool, HttpClient>();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(30));
        var token = deadline.Token;
        string? etag = null;
        long? total = null;
        var endpointIndex = 0;
        var interruptedOnEndpoint = false;
        Exception? lastError = null;
        try
        {
            for (var attempt = 0; attempt < 6; attempt++)
            {
                token.ThrowIfCancellationRequested();
                var endpoint = endpoints[endpointIndex];
                var offset = etag is not null && total is not null ? destination.Length : 0;
                if (offset == 0) { destination.SetLength(0); destination.Position = 0; }
                status?.Report($"{(endpoint.Direct ? "IPv4 直连" : "系统代理")} / {(new Uri(endpoint.Url).Scheme == "https" ? "HTTPS" : "HTTP")}：" +
                    (offset > 0 ? $"从 {offset / 1048576d:F1} MiB 继续下载" : "正在连接下载服务器"));
                try
                {
                    if (!clients.TryGetValue(endpoint.Direct, out var client))
                    {
                        client = clientFactory(endpoint.Direct);
                        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) CodexUpdater/1.2");
                        clients.Add(endpoint.Direct, client);
                    }
                    using var response = await TrustedHttpService.GetResponseAsync(client, new Uri(endpoint.Url),
                        uri => PackageDownloadService.IsAllowedFinalDownloadUrl(url, uri.AbsoluteUri), token,
                        request =>
                        {
                            request.Headers.AcceptEncoding.ParseAdd("identity");
                            if (offset > 0)
                            {
                                request.Headers.Range = new RangeHeaderValue(offset, null);
                                request.Headers.IfRange = new RangeConditionHeaderValue(new EntityTagHeaderValue(etag!));
                            }
                        });
                    if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && offset > 0)
                    {
                        etag = null; total = null;
                        continue;
                    }
                    response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentEncoding.Any(value => !value.Equals("identity", StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidDataException("服务器返回了不支持的压缩传输，已停止拼接安装包。");
                    var responseTag = response.Headers.ETag is { IsWeak: false } tag ? tag.ToString() : null;
                    if (response.StatusCode == HttpStatusCode.PartialContent)
                    {
                        var range = response.Content.Headers.ContentRange;
                        if (offset == 0 || range is null || range.Unit != "bytes" || range.From != offset ||
                            range.Length != total || range.To != total - 1 || responseTag != etag ||
                            (response.Content.Headers.ContentLength is { } length && length != total - offset))
                        {
                            etag = null; total = null;
                            status?.Report("续传响应的文件标识或范围不一致，将重新下载。");
                            continue;
                        }
                    }
                    else if (response.StatusCode == HttpStatusCode.OK)
                    {
                        offset = 0;
                        destination.SetLength(0); destination.Position = 0;
                        total = response.Content.Headers.ContentLength;
                        etag = responseTag;
                    }
                    else throw new InvalidDataException("服务器没有返回可下载的完整文件或分段数据。");
                    if (total > maximumBytes) throw new InvalidDataException("安装包超过单文件或本次安装计划的剩余大小上限。");
                    if (total is > 0 && destination is FileStream file)
                    {
                        var root = Path.GetPathRoot(Path.GetFullPath(file.Name))!;
                        if (!root.StartsWith(@"\\", StringComparison.Ordinal))
                        {
                            var drive = new DriveInfo(root);
                            if (drive.IsReady && drive.AvailableFreeSpace < total.Value - offset + 16L * 1024 * 1024)
                                throw new IOException("下载目录的可用磁盘空间不足。");
                        }
                    }
                    destination.Position = offset;
                    progress.Report(total is > 0 ? offset * 100d / total.Value : 0);
                    await using var source = await response.Content.ReadAsStreamAsync(token);
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
                    var buffer = new byte[128 * 1024];
                    while (true)
                    {
                        int read;
                        try
                        {
                            idle.CancelAfter(TimeSpan.FromSeconds(30));
                            read = await source.ReadAsync(buffer, idle.Token);
                            idle.CancelAfter(Timeout.InfiniteTimeSpan);
                        }
                        catch (IOException ex) { throw new HttpRequestException("下载连接提前中断。", ex); }
                        if (read == 0) break;
                        if (destination.Position + read > maximumBytes ||
                            (total is not null && destination.Position + read > total))
                            throw new InvalidDataException("服务器返回的数据超过预期大小，已停止下载。");
                        await destination.WriteAsync(buffer.AsMemory(0, read), token);
                        if (total is > 0) progress.Report(Math.Min(99, destination.Position * 100d / total.Value));
                    }
                    if (total is not null && destination.Length != total)
                        throw new HttpRequestException("服务器在安装包传完前结束了响应。");
                    if (destination.Length == 0) throw new InvalidDataException("服务器返回了空文件。");
                    await destination.FlushAsync(token);
                    return;
                }
                catch (Exception ex) when (ex is HttpRequestException || ex is OperationCanceledException && !token.IsCancellationRequested)
                {
                    lastError = ex;
                    if (ex is HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.Gone } && attempt >= endpoints.Count - 1)
                        throw;
                    // Retain partial bytes only with a strong validator and a known complete length.
                    if (destination.Length > offset && !interruptedOnEndpoint)
                    {
                        interruptedOnEndpoint = true;
                    }
                    else
                    {
                        interruptedOnEndpoint = false;
                        endpointIndex = (endpointIndex + 1) % endpoints.Count;
                    }
                    status?.Report($"下载中断或连接失败，正在重试 ({attempt + 1}/6)...");
                    if (attempt < 5) await Task.Delay(500, token);
                }
            }
            throw new HttpRequestException("下载连续失败。请在“下载线路”中切换直连或系统代理后重试。", lastError);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("下载超过 30 分钟，请切换下载线路后重试。");
        }
        finally { foreach (var client in clients.Values) client.Dispose(); }
    }
}
