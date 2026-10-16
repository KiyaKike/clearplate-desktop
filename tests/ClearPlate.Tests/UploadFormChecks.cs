using ClearPlate.Core;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

internal static class UploadFormChecks
{
    internal static async Task<int> RunAsync()
    {
        var now = DateTimeOffset.Parse("2026-10-15T12:00:00+08:00");
        var receiver = new StrictOssReceiver();
        var result = await new PlateApi(new HttpClient(receiver), () => now).UploadAsync(
            "fake-account-token", Enumerable.Repeat((byte)'x', 2048).ToArray(), "image/png", "png");
        if (!result.StartsWith("/clockImg/2026/10/15/", StringComparison.Ordinal) || !result.EndsWith(".png", StringComparison.Ordinal)
            || receiver.ImageRequests != 1 || !receiver.Accepted || receiver.ReceivedToken || result != "/" + receiver.UploadKey)
            throw new Exception("FAILED upload: browser-compatible OSS form must preserve image bytes and isolate login token");
        return 1;
    }

    // Characterization of the actual service's stricter-than-RFC form parser:
    // anonymous live probes reject the default .NET headers as MalformedPOSTRequest,
    // but accept the browser header grammar and proceed to argument validation.
    private sealed class StrictOssReceiver : HttpMessageHandler
    {
        internal int ImageRequests;
        internal bool Accepted, ReceivedToken;
        internal string UploadKey = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == "clearplate-api.guangpanxingdong.com")
                return new(HttpStatusCode.OK) { Content = new StringContent(
                    "{\"code\":10007,\"data\":{\"host\":\"https://clearplateimg.guangpanxingdong.com\",\"dir\":\"clockImg/2026/10/15/\",\"policy\":\"fake-policy\",\"accessid\":\"fake-access\",\"signature\":\"fake-signature\",\"expire\":4102444800}}") };
            ImageRequests++; ReceivedToken |= request.Headers.Contains("X-Token");
            var content = request.Content ?? throw new Exception("Missing upload form");
            var boundary = content.Headers.ContentType?.Parameters.Single(p => p.Name == "boundary").Value ?? "";
            if (boundary.Length == 0 || boundary.StartsWith('"')) return Reject();
            var raw = Encoding.Latin1.GetString(await content.ReadAsByteArrayAsync(cancellationToken));
            var parts = raw.Split("--" + boundary, StringSplitOptions.None);
            if (parts.Length != 9 || parts[0] != "" || parts[^1] != "--\r\n") return Reject();
            var fields = new Dictionary<string, string>();
            var fileName = "";
            for (var i = 1; i < parts.Length - 1; i++)
            {
                var part = parts[i];
                if (!part.StartsWith("\r\n", StringComparison.Ordinal) || !part.EndsWith("\r\n", StringComparison.Ordinal)) return Reject();
                var separator = part.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (separator < 0) return Reject();
                var headers = part[2..separator].Split("\r\n");
                var disposition = Regex.Match(headers[0], "^Content-Disposition: form-data; name=\"([A-Za-z_-]+)\"(?:; filename=\"([A-Za-z0-9.]+)\")?$");
                if (!disposition.Success) return Reject();
                var name = disposition.Groups[1].Value;
                var value = part[(separator + 4)..^2];
                if (name == "file")
                {
                    fileName = disposition.Groups[2].Value;
                    if (i != parts.Length - 2 || disposition.Groups[2].Value.Length == 0
                        || headers.Length != 2 || headers[1] != "Content-Type: image/png" || value != new string('x', 2048)) return Reject();
                }
                else if (headers.Length != 1 || !fields.TryAdd(name, value)) return Reject();
            }
            UploadKey = fields.GetValueOrDefault("key") ?? "";
            Accepted = fields.Count == 6 && fields.GetValueOrDefault("policy") == "fake-policy"
                && fields.GetValueOrDefault("OSSAccessKeyId") == "fake-access"
                && fields.GetValueOrDefault("signature") == "fake-signature"
                && fields.GetValueOrDefault("success_action_status") == "200"
                && fields.GetValueOrDefault("Content-Type") == "image/png"
                && Regex.IsMatch(UploadKey, "^clockImg/2026/10/15/[a-f0-9]{32}\\.png$")
                && UploadKey.EndsWith("/" + fileName, StringComparison.Ordinal);
            return Accepted ? new(HttpStatusCode.OK) : Reject();
        }
        private static HttpResponseMessage Reject([System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
        {
            Console.WriteLine("Strict OSS fixture rejected request at test line " + line);
            return new(HttpStatusCode.BadRequest) { Content = new StringContent("<Error><Code>MalformedPOSTRequest</Code></Error>") };
        }
    }
}
