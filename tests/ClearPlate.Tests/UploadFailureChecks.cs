using ClearPlate.Core;
using System.Net;
using System.Net.Sockets;
using System.Text;

internal static class UploadFailureChecks
{
    internal static async Task<int> RunAsync()
    {
        var count = 0;
        var now = DateTimeOffset.Parse("2026-10-15T12:00:00+08:00");
        foreach (var credentialFailure in new[] { true, false })
        {
            var handler = new FailureHandler(credentialFailure);
            try
            {
                await new PlateApi(new HttpClient(handler), () => now).UploadAsync("fake-private-token", new byte[2048], "image/png", "png");
                throw new Exception("Upload should have failed");
            }
            catch (HttpRequestException error)
            {
                if (!error.Message.Contains(credentialFailure ? "申请上传凭证" : "发送照片", StringComparison.Ordinal)
                    || !error.Message.Contains("ConnectionReset", StringComparison.Ordinal)
                    || error.Message.Contains("fake-private-token", StringComparison.Ordinal)
                    || error.Message.Contains("private.example", StringComparison.Ordinal)
                    || error.HttpRequestError != HttpRequestError.ConnectionError)
                    throw new Exception("FAILED upload: safe message must identify exact upload step and network cause");
                count++;
            }
            if (handler.ImageRequests != (credentialFailure ? 0 : 1) || handler.ImageReceivedToken)
                throw new Exception("FAILED upload: credential request failure must not send photo and image host must not receive token");
            count++;
        }
        var rejectedHandler = new FailureHandler(false) { RejectImage = true };
        try
        {
            await new PlateApi(new HttpClient(rejectedHandler), () => now).UploadAsync("fake-private-token", new byte[2048], "image/png", "png");
            throw new Exception("Rejected upload should have failed");
        }
        catch (BusinessException error)
        {
            if (!error.Message.Contains("HTTP 403", StringComparison.Ordinal) || !error.Message.Contains("照片", StringComparison.Ordinal))
                throw new Exception("FAILED upload: report rejected image HTTP status without response body");
            count++;
        }
        return count;
    }

    private sealed class FailureHandler(bool credentialFailure) : HttpMessageHandler
    {
        internal int ImageRequests;
        internal bool ImageReceivedToken;
        internal bool RejectImage;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == "clearplateimg.guangpanxingdong.com")
            {
                ImageRequests++; ImageReceivedToken |= request.Headers.Contains("X-Token");
                if (RejectImage) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            }
            if (credentialFailure || ImageRequests > 0)
                throw new HttpRequestException(HttpRequestError.ConnectionError, "fake-private-token https://private.example/secret",
                    new SocketException((int)SocketError.ConnectionReset));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                "{\"code\":10007,\"data\":{\"host\":\"https://clearplateimg.guangpanxingdong.com\",\"dir\":\"clockImg/2026/10/15/\",\"policy\":\"fake\",\"accessid\":\"fake\",\"signature\":\"fake\",\"expire\":4102444800}}",
                Encoding.UTF8, "application/json") });
        }
    }
}
