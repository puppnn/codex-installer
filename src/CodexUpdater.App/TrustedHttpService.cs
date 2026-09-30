using System.Net;
using System.Net.Http;

namespace CodexUpdater.App;

internal static class TrustedHttpService
{
    // Callers must disable HttpClientHandler.AllowAutoRedirect so every hop is checked here.
    public static async Task<HttpResponseMessage> GetResponseAsync(
        HttpClient client, Uri start, Func<Uri, bool> isAllowed, CancellationToken cancellationToken)
    {
        var current = start;
        for (var redirects = 0; ; redirects++)
        {
            if (!isAllowed(current))
                throw new InvalidOperationException("请求被重定向到非受信任地址，已停止连接。");
            var response = await client.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Found or
                HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect))
                return response;
            var location = response.Headers.Location;
            response.Dispose();
            if (location is null || redirects >= 5)
                throw new HttpRequestException("重定向地址无效或次数过多。");
            current = location.IsAbsoluteUri ? location : new Uri(current, location);
        }
    }
}
