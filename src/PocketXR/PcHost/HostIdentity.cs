using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using PocketXR.Core;

namespace PocketXR.PcHost;

/// <summary>
/// Pocket XR for Windows as a host: its identity (a unique id and a self-signed RSA 2048 certificate, as Sunshine makes
/// them, the key in Windows' key storage) and the phones paired with it (their certificates, pinned). Kept in
/// %LOCALAPPDATA%\Pocket XR\host.
/// </summary>
internal sealed class HostIdentity
{
    public const string KeyName = "Pocket XR host";

    public string UniqueId { get; }
    public X509Certificate2 Certificate { get; }
    public byte[] PemCert { get; }
    public byte[] CertSignature { get; }
    private readonly RSA key;

    public static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pocket XR", "host");

    private HostIdentity(string id, byte[] der, CngKey cng)
    {
        UniqueId = id;
        key = new RSACng(cng);
        using var plain = new X509Certificate2(der);
        Certificate = plain.CopyWithPrivateKey(key);
        PemCert = System.Text.Encoding.ASCII.GetBytes(plain.ExportCertificatePem().Replace("\r\n", "\n") + "\n");
        CertSignature = PcScreens.PcIdentity.SignatureOf(der);
    }

    public static HostIdentity Load()
    {
        Directory.CreateDirectory(Folder);
        string idFile = Path.Combine(Folder, "host-id.txt");
        string certFile = Path.Combine(Folder, "host.cer");
        var ksp = CngProvider.MicrosoftSoftwareKeyStorageProvider;
        if (File.Exists(idFile) && File.Exists(certFile) && CngKey.Exists(KeyName, ksp))
            return new HostIdentity(File.ReadAllText(idFile).Trim(), File.ReadAllBytes(certFile), CngKey.Open(KeyName, ksp));
        var p = new CngKeyCreationParameters
        {
            Provider = ksp,
            KeyCreationOptions = CngKeyCreationOptions.OverwriteExistingKey,
            ExportPolicy = CngExportPolicies.None,
            KeyUsage = CngKeyUsages.AllUsages,
        };
        p.Parameters.Add(new CngProperty("Length", BitConverter.GetBytes(2048), CngPropertyOptions.None));
        using var cng = CngKey.Create(CngAlgorithm.Rsa, KeyName, p);
        using var rsa = new RSACng(cng);
        var req = new CertificateRequest("CN=Sunshine Gamestream Host", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        byte[] serial = RandomNumberGenerator.GetBytes(8);
        serial[0] &= 0x7f;
        var now = DateTimeOffset.UtcNow.AddMinutes(-5);
        using var cert = req.Create(req.SubjectName, X509SignatureGenerator.CreateForRSA(rsa, RSASignaturePadding.Pkcs1), now, now.AddYears(20), serial);
        string id = Guid.NewGuid().ToString().ToUpperInvariant();
        File.WriteAllBytes(certFile, cert.RawData);
        File.WriteAllText(idFile, id);
        Log.P("host: made this PC's host id, certificate and key");
        return new HostIdentity(id, cert.RawData, CngKey.Open(KeyName, ksp));
    }

    public byte[] Sign(byte[] data) => key.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
}

/// <summary>The phones paired with this host: name, unique id, certificate (DER, base64). Pinned on every HTTPS request.</summary>
internal sealed class HostClients
{
    public sealed class Client
    {
        public string Name { get; set; } = "";
        public string UniqueId { get; set; } = "";
        public string Cert { get; set; } = "";
        public DateTime Paired { get; set; }
    }

    private readonly object gate = new();
    private List<Client> list = new();
    private static string File => Path.Combine(HostIdentity.Folder, "clients.json");

    public HostClients()
    {
        try { list = JsonSerializer.Deserialize<List<Client>>(System.IO.File.ReadAllText(File)) ?? new(); }
        catch (Exception) { list = new(); }
    }

    public bool Knows(byte[] certDer)
    {
        string b = Convert.ToBase64String(certDer);
        lock (gate) return list.Any(c => c.Cert == b);
    }

    public void Add(string name, string uniqueId, byte[] certDer)
    {
        string b = Convert.ToBase64String(certDer);
        lock (gate)
        {
            // One record per certificate: a phone paired again replaces its old record.
            list.RemoveAll(c => c.Cert == b);
            list.Add(new Client { Name = name, UniqueId = uniqueId, Cert = b, Paired = DateTime.Now });
            Directory.CreateDirectory(HostIdentity.Folder);
            System.IO.File.WriteAllText(File, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    public int Count { get { lock (gate) return list.Count; } }

    public List<Client> All() { lock (gate) return list.ToList(); }

    /// <summary>Forgets a client by its record id (its unique id here).</summary>
    public bool Remove(string uniqueId)
    {
        lock (gate)
        {
            int n = list.RemoveAll(c => c.UniqueId == uniqueId);
            if (n > 0) System.IO.File.WriteAllText(File, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
            return n > 0;
        }
    }
}
