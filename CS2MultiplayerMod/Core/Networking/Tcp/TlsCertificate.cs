using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CS2MultiplayerMod.Core.Networking.Tcp
{
    /// <summary>
    /// Creates ephemeral self-signed certificate for TLS. Lifetime-scoped, never
    /// persisted. Clients skip CA validation - hash folded into password proof as
    /// channel binding, which defeats active man-in-the-middle when password is set.
    /// </summary>
    public static class TlsCertificate
    {
        /// <summary>
        /// Try to create a self-signed certificate. Returns null with error in
        /// <paramref name="error"/> if runtime cannot create it - caller decides
        /// if plaintext fallback (LAN) is acceptable or fatal (public).
        /// </summary>
        public static X509Certificate2 TryCreateEphemeral(out string error)
        {
            try
            {
#if NETFRAMEWORK
                // PROV_RSA_AES (type 24) — the classic CSP that can sign with SHA-256.
                var rsa = new RSACryptoServiceProvider(2048, new CspParameters(24));
#else
                var rsa = RSA.Create(2048);
#endif
                using (rsa)
                {
                    var request = new CertificateRequest(
                        "CN=CS2MultiplayerMod", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                    using (X509Certificate2 ephemeral = request.CreateSelfSigned(
                               DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1)))
                    {
                        // Round-trip through PFX so the private key is usable by SslStream
                        // (a directly created ephemeral key is often rejected on Windows).
                        byte[] pfx = ephemeral.Export(X509ContentType.Pfx);
                        error = null;
                        // The ctor is the only PFX loader that exists on net48 too.
#pragma warning disable SYSLIB0057
                        return new X509Certificate2(pfx, (string)null,
                            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet);
#pragma warning restore SYSLIB0057
                    }
                }
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return null;
            }
        }

        /// <summary>The channel-binding token for a certificate: SHA-256 over its DER bytes.</summary>
        public static byte[] HashOf(X509Certificate certificate)
        {
            if (certificate == null) return Array.Empty<byte>();
            using (var sha = SHA256.Create())
                return sha.ComputeHash(certificate.GetRawCertData());
        }
    }
}
