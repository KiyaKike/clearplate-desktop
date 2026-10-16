using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace ClearPlate.Core;

public sealed class CaptureCertificates : IDisposable
{
    public X509Certificate2 Root { get; }
    public X509Certificate2 Server { get; }
    public CaptureCertificates(DateTimeOffset? start = null)
    {
        var now = start ?? DateTimeOffset.UtcNow;
        using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=ClearPlate temporary login capture " + Guid.NewGuid().ToString("N"), rootKey, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        // Critical name constraint: this root can authorize only the ClearPlate API hostname.
        var constraints = new AsnWriter(AsnEncodingRules.DER);
        constraints.PushSequence();
        var permitted = new Asn1Tag(TagClass.ContextSpecific, 0, true);
        constraints.PushSequence(permitted);
        constraints.PushSequence();
        constraints.WriteCharacterString(UniversalTagNumber.IA5String, CaptureProtocol.ApiHost, new Asn1Tag(TagClass.ContextSpecific, 2));
        constraints.PopSequence(); constraints.PopSequence(permitted);
        // A plain DNS subtree also permits children. Exclude those to retain only
        // the exact API hostname when Chromium-compatible SSL trust is used.
        var excluded = new Asn1Tag(TagClass.ContextSpecific, 1, true);
        constraints.PushSequence(excluded); constraints.PushSequence();
        constraints.WriteCharacterString(UniversalTagNumber.IA5String, "." + CaptureProtocol.ApiHost, new Asn1Tag(TagClass.ContextSpecific, 2));
        constraints.PopSequence(); constraints.PopSequence(excluded); constraints.PopSequence();
        request.CertificateExtensions.Add(new X509Extension("2.5.29.30", constraints.Encode(), true));
        Root = request.CreateSelfSigned(now.AddMinutes(-1), now.AddMinutes(6));
        using var serverKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var serverRequest = new CertificateRequest("CN=" + CaptureProtocol.ApiHost, serverKey, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName(CaptureProtocol.ApiHost);
        serverRequest.CertificateExtensions.Add(names.Build());
        serverRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        serverRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        serverRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        using var issued = serverRequest.Create(Root, now.AddMinutes(-1), now.AddMinutes(5), RandomNumberGenerator.GetBytes(16));
        var server = issued.CopyWithPrivateKey(serverKey);
        if (!OperatingSystem.IsWindows())
        {
            Server = server;
            return;
        }
        // Schannel cannot use the ephemeral ECDSA key attached by
        // CopyWithPrivateKey. Import into a temporary current-user container;
        // omitting PersistKeySet lets certificate disposal delete the key.
        using (server)
        {
            var pkcs12 = server.Export(X509ContentType.Pkcs12);
            try { Server = X509CertificateLoader.LoadPkcs12(pkcs12, null, X509KeyStorageFlags.UserKeySet); }
            catch { Root.Dispose(); throw; }
            finally { CryptographicOperations.ZeroMemory(pkcs12); }
        }
    }
    public void Dispose() { Server.Dispose(); Root.Dispose(); }
}
