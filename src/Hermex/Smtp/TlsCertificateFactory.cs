using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Hermex.Smtp;

/// <summary>Resolves the certificate presented for STARTTLS / implicit TLS connections.</summary>
internal static class TlsCertificateFactory
{
    /// <summary>
    /// Returns the configured certificate, generates a self-signed one, or returns
    /// <c>null</c> when TLS is disabled or no certificate is available.
    /// </summary>
    public static X509Certificate2? Resolve(HermexOptions options)
    {
        if (options.TlsMode == HermexTlsMode.None)
            return null;
        if (options.TlsCertificate is not null)
            return options.TlsCertificate;
        return options.GenerateSelfSignedCertificate
            ? GenerateSelfSigned(options.ServerHostName)
            : null;
    }

    /// <summary>Creates a self-signed certificate suitable for a local development SMTP server.</summary>
    public static X509Certificate2 GenerateSelfSigned(string hostName)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={hostName}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, critical: false)); // serverAuth

        var subjectAltName = new SubjectAlternativeNameBuilder();
        subjectAltName.AddDnsName(hostName);
        subjectAltName.AddDnsName("localhost");
        subjectAltName.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(subjectAltName.Build());

        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));

        // Round-trip through PKCS#12 so SslStream can use the private key reliably on every OS.
        var pfx = certificate.Export(X509ContentType.Pfx);
#if NET9_0_OR_GREATER
        return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.Exportable);
#else
        return new X509Certificate2(pfx, (string?)null, X509KeyStorageFlags.Exportable);
#endif
    }
}
