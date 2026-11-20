using System;
using System.Linq;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using PocketXR.Core;

namespace PocketXR.PcScreens;

/// <summary>
/// PC screens: the host's web side (Apollo or Sunshine, GameStream's protocol): serverinfo, pairing, the app list,
/// launch, resume and cancel. Ported from moonlight-android's NvHTTP and PairingManager (GPLv3, credited in
/// README.md). Blocking calls: run off the window's thread.
/// </summary>
public sealed class NvHttp : IDisposable
{
    public string Host { get; }
    public int HttpPort { get; }
    public int HttpsPort { get; private set; }

    /// <summary>The host's certificate this PC pinned when it paired; HTTPS to the host must present exactly this.</summary>
    public X509Certificate2? ServerCert { get; private set; }

    /// <summary>The TLS versions offered to hosts.</summary>
    public static System.Security.Authentication.SslProtocols Protocols = System.Security.Authentication.SslProtocols.Tls12;

    private readonly PcIdentity id;
    private X509Certificate2? clientCert;
    private HttpClient? https;
    private readonly HttpClient http;

    public NvHttp(string host, int httpPort, int httpsPort, X509Certificate2? serverCert, PcIdentity? identity = null)
    {
        Host = host;
        HttpPort = httpPort;
        HttpsPort = httpsPort;
        ServerCert = serverCert;
        id = identity ?? PcIdentity.Shared;
        http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5) }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private string HostInUrl => Host.Contains(':') ? "[" + Host + "]" : Host;

    private HttpClient Https()
    {
        if (https != null) return https;
        clientCert ??= id.TlsCertificate();
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(5),
            SslOptions = new SslClientAuthenticationOptions
            {
                ClientCertificates = new X509CertificateCollection { clientCert },
                // Always this PC's certificate: the host names no issuers it accepts, and Windows' TLS then sends none.
                LocalCertificateSelectionCallback = (_, _, _, _, _) => clientCert,
                // TLS 1.2: Windows' TLS 1.3 sends a client certificate only when asked after the handshake.
                EnabledSslProtocols = Protocols,
                // No resumed TLS sessions: Apollo refuses a resumed session with a client certificate, so every HTTPS
                // request after the first failed ("The Local Security Authority cannot be contacted", 20 Nov 2026).
                AllowTlsResume = false,
                // The host's own self-signed certificate, matched byte for byte against the pinned one (as Moonlight).
                RemoteCertificateValidationCallback = (_, cert, _, _) =>
                    cert != null && ServerCert != null && cert.GetRawCertData().AsSpan().SequenceEqual(ServerCert.RawData),
            },
        };
        return https = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private string Get(bool secure, string path, string? query, int timeoutMs)
    {
        string url = (secure ? $"https://{HostInUrl}:{HttpsPort}/" : $"http://{HostInUrl}:{HttpPort}/") + path +
            "?" + (string.IsNullOrEmpty(query) ? "" : query + "&") + "uniqueid=" + id.UniqueId + "&uuid=" + Guid.NewGuid();
        using var cts = timeoutMs > 0 ? new CancellationTokenSource(timeoutMs) : new CancellationTokenSource();
        var client = secure ? Https() : http;
        using var response = client.GetAsync(url, cts.Token).GetAwaiter().GetResult();
        string body = response.Content.ReadAsStringAsync(cts.Token).GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode) throw new HostException((int)response.StatusCode, response.ReasonPhrase ?? "");
        var (code, message) = PcScreenLogic.XmlStatus(body);
        if (code != 200 && code != 0) throw new HostException(code, message);
        return body;
    }

    /// <summary>The host's serverinfo: over HTTPS when paired (only then does it say PairStatus 1), else plain HTTP.</summary>
    public string ServerInfo(int timeoutMs = 5000)
    {
        if (ServerCert != null)
        {
            if (HttpsPort <= 0) HttpsPort = HttpsPortOf(Get(false, "serverinfo", null, timeoutMs));
            try
            {
                return Get(true, "serverinfo", null, timeoutMs);
            }
            catch (HostException e) when (e.Code == 401)
            {
                // Not paired any more: the plain answer says so (PairStatus 0).
                Log.P($"pc screens: {Host}:{HttpsPort} over HTTPS: {e.Message}");
            }
            catch (HttpRequestException e) when (e.InnerException is System.Security.Authentication.AuthenticationException a &&
                a.Message.Contains("RemoteCertificate", StringComparison.OrdinalIgnoreCase))
            {
                // The host's certificate changed (Apollo reinstalled): plain HTTP, which says PairStatus 0.
                Log.P($"pc screens: {Host}:{HttpsPort}: the host's certificate changed: {a.Message}");
            }
            catch (HttpRequestException e) when (e.InnerException is System.Security.Authentication.AuthenticationException)
            {
                // Any other TLS failure is not a lost pairing: said as it is, the pairing kept.
                Log.P($"pc screens: {Host}:{HttpsPort} over HTTPS: {e.InnerException?.Message} / {e.InnerException?.InnerException?.Message}");
                throw;
            }
        }
        return Get(false, "serverinfo", null, timeoutMs);
    }

    public static int HttpsPortOf(string serverInfo) =>
        int.TryParse(PcScreenLogic.Xml(serverInfo, "HttpsPort"), out int p) && p > 0 ? p : 47984;

    public string AppList() => Get(true, "applist", null, 10_000);

    /// <summary>The app running in the host's session, or 0 when it is free.</summary>
    public static int CurrentGame(string serverInfo) =>
        (PcScreenLogic.Xml(serverInfo, "state") ?? "").EndsWith("_SERVER_BUSY", StringComparison.Ordinal) &&
        int.TryParse(PcScreenLogic.Xml(serverInfo, "currentgame"), out int g) ? g : 0;

    /// <summary>
    /// Launch (or resume) the app at this size. Returns the RTSP session URL the host gave (null from older hosts).
    /// <paramref name="riKey"/> and <paramref name="riKeyId"/> encrypt the input this PC sends.
    /// </summary>
    public string? Launch(string verb, int appId, int w, int h, int fps, byte[] riKey, int riKeyId, bool virtualDisplay,
        bool localAudio, int surroundAudioInfo, string coreQuery)
    {
        string query = $"appid={appId}&mode={w}x{h}x{fps}&additionalStates=1&sops=0" +
            $"&rikey={PcScreenLogic.Hex(riKey)}&rikeyid={riKeyId}&localAudioPlayMode={(localAudio ? 1 : 0)}" +
            $"&virtualDisplay={(virtualDisplay ? 1 : 0)}&surroundAudioInfo={surroundAudioInfo}" +
            "&remoteControllersBitmap=0&gcmap=0&gcpersist=0" + coreQuery;
        string xml = Get(true, verb, query, 0);
        string? ok = PcScreenLogic.Xml(xml, verb == "launch" ? "gamesession" : "resume");
        if (ok == null || ok == "0") throw new HostException(0, $"the host refused to {verb} the app");
        return PcScreenLogic.Xml(xml, "sessionUrl0");
    }

    /// <summary>Ends the host's session, so Apollo removes the session's virtual display.</summary>
    public void Quit() => Get(true, "cancel", null, 10_000);

    private void Unpair()
    {
        try { Get(false, "unpair", null, 5000); } catch (Exception) { }
    }

    public enum PairState { Paired, PinWrong, Failed, AlreadyInProgress }

    /// <summary>
    /// Pairs with the host using <paramref name="pin"/>, which the user (or the Pocket XR PC helper) types on the host
    /// while this waits. On success, <see cref="ServerCert"/> is the host's certificate, to be saved.
    /// </summary>
    public PairState Pair(string serverInfo, string pin, CancellationToken stop)
    {
        bool sha256 = PcScreenLogic.MajorVersion(serverInfo) >= 7;
        int hashLen = sha256 ? 32 : 20;
        byte[] Hash(byte[] d) => sha256 ? SHA256.HashData(d) : SHA1.HashData(d);
        if (HttpsPort <= 0) HttpsPort = HttpsPortOf(serverInfo);

        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] aesKey = Hash(salt.Concat(Encoding.UTF8.GetBytes(pin)).ToArray()).Take(16).ToArray();

        // The host answers only once the PIN has been typed there: no read timeout, but stoppable.
        string getCert = PairStep(false, $"phrase=getservercert&salt={PcScreenLogic.Hex(salt)}&clientcert={PcScreenLogic.Hex(id.PemCert)}", 0, stop);
        if (PcScreenLogic.Xml(getCert, "paired") != "1") return PairState.Failed;
        string? plain = PcScreenLogic.Xml(getCert, "plaincert");
        if (string.IsNullOrEmpty(plain))
        {
            Unpair();
            return PairState.AlreadyInProgress;
        }
        var serverCert = new X509Certificate2(Convert.FromHexString(plain));
        ServerCert = serverCert;
        https?.Dispose();
        https = null;

        byte[] challenge = RandomNumberGenerator.GetBytes(16);
        string r1 = PairStep(false, "clientchallenge=" + PcScreenLogic.Hex(Aes(challenge, aesKey, true)), 10_000, stop);
        if (PcScreenLogic.Xml(r1, "paired") != "1") { Unpair(); return PairState.Failed; }
        byte[] dec = Aes(Convert.FromHexString(PcScreenLogic.Xml(r1, "challengeresponse") ?? ""), aesKey, false);
        byte[] serverResponse = dec[..hashLen];
        byte[] serverChallenge = dec[hashLen..(hashLen + 16)];

        byte[] clientSecret = RandomNumberGenerator.GetBytes(16);
        byte[] respHash = Hash(serverChallenge.Concat(id.CertSignature).Concat(clientSecret).ToArray());
        string r2 = PairStep(false, "serverchallengeresp=" + PcScreenLogic.Hex(Aes(respHash, aesKey, true)), 10_000, stop);
        if (PcScreenLogic.Xml(r2, "paired") != "1") { Unpair(); return PairState.Failed; }

        byte[] secretResp = Convert.FromHexString(PcScreenLogic.Xml(r2, "pairingsecret") ?? "");
        byte[] serverSecret = secretResp[..16];
        byte[] serverSig = secretResp[16..];
        using (var pub = serverCert.GetRSAPublicKey())
        {
            if (pub == null || !pub.VerifyData(serverSecret, serverSig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            {
                Unpair();
                return PairState.Failed;
            }
        }
        byte[] expected = Hash(challenge.Concat(PcIdentity.SignatureOf(serverCert.RawData)).Concat(serverSecret).ToArray());
        if (!expected.AsSpan().SequenceEqual(serverResponse))
        {
            Unpair();
            return PairState.PinWrong;
        }

        byte[] clientPairingSecret = clientSecret.Concat(id.Sign(clientSecret)).ToArray();
        string r3 = PairStep(false, "clientpairingsecret=" + PcScreenLogic.Hex(clientPairingSecret), 10_000, stop);
        if (PcScreenLogic.Xml(r3, "paired") != "1") { Unpair(); return PairState.Failed; }

        string r4 = PairStep(true, "phrase=pairchallenge", 10_000, stop);
        if (PcScreenLogic.Xml(r4, "paired") != "1") { Unpair(); return PairState.Failed; }
        return PairState.Paired;
    }

    private string PairStep(bool secure, string args, int timeoutMs, CancellationToken stop)
    {
        string url = (secure ? $"https://{HostInUrl}:{HttpsPort}/" : $"http://{HostInUrl}:{HttpPort}/") +
            "pair?devicename=roth&updateState=1&" + args + "&uniqueid=" + id.UniqueId + "&uuid=" + Guid.NewGuid();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stop);
        if (timeoutMs > 0) cts.CancelAfter(timeoutMs);
        var client = secure ? Https() : http;
        using var response = client.GetAsync(url, cts.Token).GetAwaiter().GetResult();
        string body = response.Content.ReadAsStringAsync(cts.Token).GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode) throw new HostException((int)response.StatusCode, response.ReasonPhrase ?? "");
        return body;
    }

    /// <summary>AES-128 ECB over the data padded with zeros to whole blocks, as Moonlight's pairing does.</summary>
    private static byte[] Aes(byte[] data, byte[] key, bool encrypt)
    {
        byte[] padded = new byte[(data.Length + 15) / 16 * 16];
        data.CopyTo(padded, 0);
        using var aes = System.Security.Cryptography.Aes.Create();
        aes.Key = key;
        return encrypt ? aes.EncryptEcb(padded, PaddingMode.None) : aes.DecryptEcb(padded, PaddingMode.None);
    }

    /// <summary>A four-digit PIN.</summary>
    public static string NewPin() => RandomNumberGenerator.GetInt32(0, 10000).ToString("D4");

    public void Dispose()
    {
        https?.Dispose();
        http.Dispose();
        clientCert?.Dispose();
    }
}

/// <summary>The host answered with an error: an HTTP status, or a status_code other than 200 in its XML.</summary>
public sealed class HostException(int code, string message) : Exception($"host said {code} {message}".Trim())
{
    public int Code { get; } = code;
}
