using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace CodexUpdater.App;

internal enum DownloadRoute { Auto, Direct, SystemProxy }

internal static class DownloadConnection
{
    public static HttpClient Create(bool direct)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = !direct,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(12),
        };
        if (direct) handler.ConnectCallback = ConnectIPv4Async;
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
    }

    private static async ValueTask<Stream> ConnectIPv4Async(SocketsHttpConnectionContext context, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, AddressFamily.InterNetwork, timeout.Token);
        foreach (var address in addresses)
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                attempt.CancelAfter(TimeSpan.FromSeconds(3));
                await socket.ConnectAsync(address, context.DnsEndPoint.Port, attempt.Token);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                socket.Dispose();
                token.ThrowIfCancellationRequested();
            }
            catch { socket.Dispose(); throw; }
        }
        throw new HttpRequestException("微软 CDN IPv4 直连失败，请尝试系统代理线路。");
    }
}
