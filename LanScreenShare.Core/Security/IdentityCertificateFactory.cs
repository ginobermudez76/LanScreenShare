using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace LanScreenShare.Core.Security
{
    internal static class IdentityCertificateFactory
    {
        internal static byte[] CreateSelfSignedIdentityPfx()
        {
            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            string machineName = Environment.MachineName.Replace(" ", "-");
            var distinguishedName = new X500DistinguishedName($"CN=LanScreenShare-{machineName}");

            var request = new CertificateRequest(distinguishedName, ecdsa, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(
                new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            request.CertificateExtensions.Add(
                new X509KeyUsageExtension(
                    X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                    critical: true));

            var eku = new OidCollection
            {
                new Oid("1.3.6.1.5.5.7.3.1"),
                new Oid("1.3.6.1.5.5.7.3.2")
            };
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(eku, critical: true));

            var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
            var notAfter = DateTimeOffset.UtcNow.AddYears(2);
            using var cert = request.CreateSelfSigned(notBefore, notAfter);

            return cert.Export(X509ContentType.Pfx);
        }

        internal static X509Certificate2 LoadExportableCertificate(byte[] pfxBytes) =>
            X509CertificateLoader.LoadPkcs12(
                pfxBytes,
                null,
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
    }
}
