using ClearPlate.Core;
using System.IO.Compression;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

internal static class CaptureChecks
{
    internal static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool valid, string label) { if (!valid) throw new Exception("FAILED capture: " + label); count++; }
        var headers = new Dictionary<string, string> { ["x-token"] = "fake-header-token" };
        var success = "{\"code\":10007,\"data\":{\"token\":\"fake-issued-token\"}}"u8.ToArray();
        Check(CaptureProtocol.Candidate(CaptureProtocol.ApiHost, 200, "/site/login", headers, success, null) == "fake-issued-token", "login body candidate preferred");
        Check(CaptureProtocol.Candidate(CaptureProtocol.ApiHost, 200, "/user/user-info", headers, success, null) == "fake-issued-token", "profile issued token preferred");
        Check(CaptureProtocol.Candidate(CaptureProtocol.ApiHost, 200, "/group/my-groups", headers, success, null) == "fake-header-token", "successful own request header candidate");
        Check(CaptureProtocol.Candidate("api.weixin.qq.com", 200, "/site/login", headers, success, null) is null, "other service never inspected for tokens");
        Check(CaptureProtocol.Candidate(CaptureProtocol.ApiHost, 401, "/site/login", headers, success, null) is null, "HTTP failure ignored");
        Check(CaptureProtocol.Candidate(CaptureProtocol.ApiHost, 200, "/site/login", headers, "{\"code\":10003}"u8, null) is null, "expired request not adopted");
        Check(CaptureProtocol.Candidate(CaptureProtocol.ApiHost, 200, "/site/login", new Dictionary<string, string>(), "{\"code\":10007,\"data\":{\"token\":\"code|:|temporary\"}}"u8, null) is null, "wx login code is not a token");
        Check(CaptureProtocol.Candidate(CaptureProtocol.ApiHost, 200, "/site/login", headers, "not json"u8, null) is null, "invalid JSON ignored");
        Check(CaptureProtocol.Candidate(CaptureProtocol.ApiHost, 200, "/site/login", headers, success, "unknown") is null, "unknown encoding ignored");
        foreach (var encoding in new[] { "gzip", "deflate", "br" })
        {
            using var output = new MemoryStream();
            using (Stream compressor = encoding switch {
                "gzip" => new GZipStream(output, CompressionLevel.Fastest, true),
                "deflate" => new DeflateStream(output, CompressionLevel.Fastest, true),
                _ => new BrotliStream(output, CompressionLevel.Fastest, true) }) compressor.Write(success);
            Check(CaptureProtocol.Candidate(CaptureProtocol.ApiHost, 200, "/site/login", headers, output.ToArray(), encoding) == "fake-issued-token", encoding + " decoding");
        }
        using (var output = new MemoryStream())
        {
            using (var compressor = new GZipStream(output, CompressionLevel.Fastest, true)) compressor.Write(new byte[CaptureProtocol.MaximumBody + 1]);
            Check(CaptureProtocol.Candidate(CaptureProtocol.ApiHost, 200, "/site/login", headers, output.ToArray(), "gzip") is null, "decompression size bounded");
        }
        async Task ExpectInvalid(string request)
        {
            try
            {
                using var stream = new MemoryStream(Encoding.ASCII.GetBytes(request));
                var head = await CaptureProtocol.ReadHeadAsync(stream, CancellationToken.None);
                if (head is not null) _ = await CaptureProtocol.ReadBodyAsync(stream, head, CancellationToken.None);
            }
            catch (IOException) { Check(true, "ambiguous or oversized request rejected"); return; }
            throw new Exception("FAILED: malformed capture request accepted");
        }
        await ExpectInvalid("POST / HTTP/1.1\r\nContent-Length: 1\r\nContent-Length: 2\r\n\r\nxx");
        await ExpectInvalid("POST / HTTP/1.1\r\nContent-Length: 1\r\nTransfer-Encoding: chunked\r\n\r\nx");
        await ExpectInvalid("POST / HTTP/1.1\r\nContent-Length: 2097152\r\n\r\n");
        await ExpectInvalid("POST / HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\ninvalid\r\n");
        await ExpectInvalid("GET / HTTP/1.1\r\n" + new string('x', 33000));
        using (var stream = new MemoryStream("POST / HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n3\r\nabc\r\n2\r\nde\r\n0\r\n\r\n"u8.ToArray()))
        {
            var head = await CaptureProtocol.ReadHeadAsync(stream, CancellationToken.None);
            Check(Encoding.ASCII.GetString(await CaptureProtocol.ReadBodyAsync(stream, head!, CancellationToken.None)) == "abcde", "chunked request body forwarded correctly");
        }
        using var certificates = new CaptureCertificates();
        var constraint = certificates.Root.Extensions.First(e => e.Oid?.Value == "2.5.29.30");
        Check(constraint.Critical && Encoding.ASCII.GetString(constraint.RawData).Contains(CaptureProtocol.ApiHost, StringComparison.Ordinal), "critical API-only root name constraint");
        Check(certificates.Root.NotAfter.ToUniversalTime() < DateTime.UtcNow.AddMinutes(7), "temporary root expires within six minutes");
        Check(certificates.Server.GetNameInfo(X509NameType.DnsName, false) == CaptureProtocol.ApiHost && certificates.Server.HasPrivateKey, "leaf only names API host");
        using (var chain = new X509Chain())
        {
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(certificates.Root);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck; // Isolated synthetic test certificates have no CRL.
            Check(chain.Build(certificates.Server), "synthetic certificate chain validates against isolated root");
        }
        foreach (var forbiddenHost in new[] { "unrelated.example", "child." + CaptureProtocol.ApiHost })
        using (var otherKey = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256))
        {
            var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=" + forbiddenHost, otherKey, System.Security.Cryptography.HashAlgorithmName.SHA256);
            var names = new SubjectAlternativeNameBuilder(); names.AddDnsName(forbiddenHost); request.CertificateExtensions.Add(names.Build());
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            using var unrelated = request.Create(certificates.Root, DateTimeOffset.UtcNow.AddSeconds(-10), DateTimeOffset.UtcNow.AddMinutes(1), System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(certificates.Root);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            Check(!chain.Build(unrelated), "critical root name constraint rejects unrelated host even when root trusted");
        }
        Check(CaptureDiagnostics.Failure("client-tls", new IOException("fake-private-token")) == "CLIENT_TLS", "client TLS stage identified without exposing error text");
        Check(CaptureDiagnostics.Failure("upstream", new HttpRequestException("fake-private-url", new System.Security.Authentication.AuthenticationException())) == "UPSTREAM_TLS", "upstream TLS differs from client TLS");
        Check(CaptureDiagnostics.Failure("upstream", new HttpRequestException(HttpRequestError.NameResolutionError, "fake-private-url")) == "UPSTREAM_DNS", "DNS error classified independently");
        Check(CaptureDiagnostics.Failure("upstream", new TaskCanceledException()) == "UPSTREAM_TIMEOUT", "upstream timeout classified");
        Check(CaptureDiagnostics.Describe("HTTP_412").Contains("HTTP 412", StringComparison.Ordinal), "platform rejection does not blame certificate");
        Check(!CaptureDiagnostics.Describe("fake-private-token").Contains("fake-private-token", StringComparison.Ordinal), "unknown code never echoed");
        foreach (var protocol in new[] { SslProtocols.None, SslProtocols.Tls12 })
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            try
            {
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                var server = Task.Run(async () => {
                    using var tcp = await listener.AcceptTcpClientAsync(deadline.Token);
                    using var ssl = new SslStream(tcp.GetStream());
                    var context = SslStreamCertificateContext.Create(certificates.Server, new X509Certificate2Collection { certificates.Root }, offline: true);
                    await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificateContext = context,
                        ApplicationProtocols = [SslApplicationProtocol.Http11], EnabledSslProtocols = protocol }, deadline.Token);
                    var head = await CaptureProtocol.ReadHeadAsync(ssl, deadline.Token);
                    if (head is null) throw new Exception("Missing synthetic request");
                    await ssl.WriteAsync(success, deadline.Token);
                });
                using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, port, deadline.Token);
                using var ssl = new SslStream(client.GetStream());
                var options = new SslClientAuthenticationOptions { TargetHost = CaptureProtocol.ApiHost,
                    ApplicationProtocols = [SslApplicationProtocol.Http11], EnabledSslProtocols = protocol,
                    CertificateChainPolicy = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck } };
                options.CertificateChainPolicy.CustomTrustStore.Add(certificates.Root);
                await ssl.AuthenticateAsClientAsync(options, deadline.Token);
                Check(ssl.NegotiatedApplicationProtocol == SslApplicationProtocol.Http11, "capture TLS negotiates HTTP/1.1: " + protocol);
                await ssl.WriteAsync("GET /index.php?r=/user/user-info HTTP/1.1\r\nHost: clearplate-api.guangpanxingdong.com\r\n\r\n"u8.ToArray(), deadline.Token);
                var body = new byte[success.Length]; await ssl.ReadExactlyAsync(body, deadline.Token); await server;
                Check(CaptureProtocol.Candidate(CaptureProtocol.ApiHost, 200, "/user/user-info", headers, body, null) == "fake-issued-token", "local TLS transport capture, normal hostname and isolated root validation: " + protocol);
            }
            finally { listener.Stop(); }
        }
        if (OperatingSystem.IsWindows())
        {
            // Schannel needs a user key container, but it must not outlive capture.
            // Check the provider before/after Dispose rather than assuming that
            // HasPrivateKey means the native TLS stack can use the key.
            string keyName;
            CngProvider provider;
            using (var key = (ECDsaCng)certificates.Server.GetECDsaPrivateKey()!)
            {
                keyName = key.Key.KeyName ?? throw new Exception("Capture TLS key has no Windows key container");
                provider = key.Key.Provider ?? throw new Exception("Capture TLS key has no Windows provider");
                Check(CngKey.Exists(keyName, provider), "capture TLS user key exists while certificate is alive");
            }
            certificates.Dispose();
            Check(!CngKey.Exists(keyName, provider), "capture TLS user key is deleted when certificates are disposed");
        }
        return count;
    }
}
