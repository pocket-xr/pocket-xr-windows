using System;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace PocketXR.PcScreens;

/// <summary>
/// PC screens: what each PC screen keeps (%LOCALAPPDATA%\Pocket XR\pc-screens\screen-N.json): the host address (with
/// its HTTP port when not the usual one), its HTTPS port, its certificate as pinned when paired, the PC's name, and the
/// stream choices. As the phone's PcScreenStore.
/// </summary>
public sealed class PcScreenStore
{
    public int Slot { get; }

    public sealed class Data
    {
        public string Address { get; set; } = "";
        public int HttpsPort { get; set; }
        public string? ServerCert { get; set; }
        public string? PcName { get; set; }
        public string? Mac { get; set; }
        /// <summary>"auto" (60, or 120 alone), "60" or "120".</summary>
        public string Fps { get; set; } = "auto";
        /// <summary>"auto", "sharp" or "saver".</summary>
        public string Quality { get; set; } = "auto";
        /// <summary>Whether this PC screen plays the other computer's sound here.</summary>
        public bool Sound { get; set; } = true;
    }

    public Data D { get; private set; } = new();

    private string File => Path.Combine(PcIdentity.Folder, $"screen-{Slot + 1}.json");

    public PcScreenStore(int slot)
    {
        Slot = slot;
        Load();
    }

    public void Load()
    {
        try
        {
            D = JsonSerializer.Deserialize<Data>(System.IO.File.ReadAllText(File)) ?? new Data();
        }
        catch (Exception)
        {
            D = new Data();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(PcIdentity.Folder);
        string tmp = File + ".tmp";
        System.IO.File.WriteAllText(tmp, JsonSerializer.Serialize(D, new JsonSerializerOptions { WriteIndented = true }));
        System.IO.File.Move(tmp, File, overwrite: true);
    }

    public X509Certificate2? ServerCert
    {
        get
        {
            if (string.IsNullOrEmpty(D.ServerCert)) return null;
            try { return new X509Certificate2(Convert.FromBase64String(D.ServerCert)); }
            catch (Exception) { return null; }
        }
    }

    public bool Paired => D.Address.Length > 0 && D.HttpsPort > 0 && ServerCert != null;

    public void SavePairing(string address, int httpsPort, X509Certificate2 cert, string? pcName, string? mac)
    {
        D.Address = address.Trim();
        D.HttpsPort = httpsPort;
        D.ServerCert = Convert.ToBase64String(cert.RawData);
        if (pcName != null) D.PcName = pcName;
        if (!string.IsNullOrWhiteSpace(mac) && mac != "00:00:00:00:00:00") D.Mac = mac;
        Save();
    }

    /// <summary>The pairing only (port and certificate); the address is kept for pairing again.</summary>
    public void Unpair()
    {
        D.HttpsPort = 0;
        D.ServerCert = null;
        Save();
    }

    /// <summary>This PC screen's computer forgotten.</summary>
    public void Forget()
    {
        D = new Data { Fps = D.Fps, Quality = D.Quality, Sound = D.Sound };
        Save();
    }
}
