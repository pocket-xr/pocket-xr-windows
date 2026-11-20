using System;
using System.Formats.Asn1;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PocketXR.Core;

namespace PocketXR.PcScreens;

/// <summary>
/// PC screens: this PC's identity as a Moonlight client, made once: a random 16-hex-digit client id and a self-signed
/// RSA 2048 certificate named "NVIDIA GameStream Client", valid for 20 years, as moonlight-android's
/// AndroidCryptoProvider makes them. The host pins this certificate when paired.
///
/// The key lives in Windows' own key storage for this user (CNG, "Microsoft Software Key Storage Provider", named
/// <see cref="KeyName"/>), not in a file: Windows' TLS signs with a client certificate's key only from there. A key
/// loaded from a PFX failed every HTTPS request to Apollo (20 Nov 2026: TLS 1.3 "InternalError", TLS 1.2 "The Local
/// Security Authority cannot be contacted"). The id and certificate are in %LOCALAPPDATA%\Pocket XR\pc-screens.
/// </summary>
public sealed class PcIdentity
{
    public const string KeyName = "Pocket XR PC screens client";

    public string UniqueId { get; }

    /// <summary>The certificate in PEM with Unix line endings: the host refuses others (moonlight-android's note).</summary>
    public byte[] PemCert { get; }

    /// <summary>The certificate's own signature bytes, which the pairing hashes (Java's getSignature()).</summary>
    public byte[] CertSignature { get; }

    private readonly byte[] certDer;
    private readonly RSA key;

    public static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pocket XR", "pc-screens");

    private static readonly object gate = new();
    private static PcIdentity? shared;

    /// <summary>The identity, made and saved the first time. Every PC screen uses the same one, as the phone does.</summary>
    public static PcIdentity Shared
    {
        get
        {
            lock (gate) return shared ??= Load();
        }
    }

    private PcIdentity(string id, byte[] der, CngKey cng)
    {
        UniqueId = id;
        certDer = der;
        key = new RSACng(cng);
        using var cert = new X509Certificate2(der);
        PemCert = Encoding.ASCII.GetBytes(cert.ExportCertificatePem().Replace("\r\n", "\n") + "\n");
        CertSignature = SignatureOf(der);
    }

    private static PcIdentity Load()
    {
        Directory.CreateDirectory(Folder);
        string idFile = Path.Combine(Folder, "client-id.txt");
        string certFile = Path.Combine(Folder, "client.cer");
        // Two PC screen processes may start together: one makes the identity, the other waits for it.
        using var made = new System.Threading.Mutex(false, "PocketXR.pcscreen.identity");
        made.WaitOne(10_000);
        try
        {
            if (File.Exists(idFile) && File.Exists(certFile) && CngKey.Exists(KeyName, CngProvider.MicrosoftSoftwareKeyStorageProvider))
            {
                string id = File.ReadAllText(idFile).Trim();
                byte[] der = File.ReadAllBytes(certFile);
                var cng = CngKey.Open(KeyName, CngProvider.MicrosoftSoftwareKeyStorageProvider);
                using var check = new X509Certificate2(der);
                using var pub = check.GetRSAPublicKey();
                using var rsa = new RSACng(cng);
                if (id.Length == 16 && pub != null && pub.ExportParameters(false).Modulus.AsSpan().SequenceEqual(rsa.ExportParameters(false).Modulus))
                    return new PcIdentity(id, der, CngKey.Open(KeyName, CngProvider.MicrosoftSoftwareKeyStorageProvider));
                Log.P("pc screens: this PC's certificate and key do not match: making new ones");
            }
            // Files from an earlier build that kept the key in a file.
            foreach (string old in new[] { "client.pfx", "client.key" }) try { File.Delete(Path.Combine(Folder, old)); } catch (IOException) { }

            string newId = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
            var p = new CngKeyCreationParameters
            {
                Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider,
                KeyCreationOptions = CngKeyCreationOptions.OverwriteExistingKey,
                ExportPolicy = CngExportPolicies.None,
                KeyUsage = CngKeyUsages.AllUsages,
            };
            p.Parameters.Add(new CngProperty("Length", BitConverter.GetBytes(2048), CngPropertyOptions.None));
            using var newKey = CngKey.Create(CngAlgorithm.Rsa, KeyName, p);
            using var rsaNew = new RSACng(newKey);
            var req = new CertificateRequest("CN=NVIDIA GameStream Client", rsaNew, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            byte[] serial = RandomNumberGenerator.GetBytes(8);
            serial[0] &= 0x7f;
            var now = DateTimeOffset.UtcNow.AddMinutes(-5);
            using var cert = req.Create(req.SubjectName, X509SignatureGenerator.CreateForRSA(rsaNew, RSASignaturePadding.Pkcs1),
                now, now.AddYears(20), serial);
            File.WriteAllBytes(certFile, cert.RawData);
            File.WriteAllText(idFile, newId);
            Log.P("pc screens: made this PC's client id, certificate and key");
            return new PcIdentity(newId, cert.RawData, CngKey.Open(KeyName, CngProvider.MicrosoftSoftwareKeyStorageProvider));
        }
        finally
        {
            try { made.ReleaseMutex(); } catch (ApplicationException) { }
        }
    }

    /// <summary>The certificate joined to its key in Windows' key storage, for TLS.</summary>
    public X509Certificate2 TlsCertificate()
    {
        using var cert = new X509Certificate2(certDer);
        return cert.CopyWithPrivateKey(key);
    }

    public byte[] Sign(byte[] data) => key.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

    /// <summary>The signatureValue of a DER certificate: SEQUENCE { tbsCertificate, signatureAlgorithm, BIT STRING }.</summary>
    public static byte[] SignatureOf(byte[] der)
    {
        var reader = new AsnReader(der, AsnEncodingRules.DER);
        var seq = reader.ReadSequence();
        seq.ReadEncodedValue();
        seq.ReadEncodedValue();
        return seq.ReadBitString(out _);
    }
}
