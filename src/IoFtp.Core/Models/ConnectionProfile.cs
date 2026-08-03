using System.Text.Json.Serialization;

namespace IoFtp.Core.Models;

public enum TransferProtocol
{
    Ftp,
    FtpsExplicit,
    FtpsImplicit,
    Sftp
}

public static class TransferProtocolNames
{
    public static string Display(TransferProtocol protocol) => protocol switch
    {
        TransferProtocol.FtpsExplicit => "AUTH TLS",
        TransferProtocol.FtpsImplicit => "Implicit TLS",
        TransferProtocol.Ftp => "None (FTP)",
        TransferProtocol.Sftp => "SFTP",
        _ => protocol.ToString()
    };
}

public enum DirectoryListingMode
{
    StatThenList,
    StatOnly,
    ListOnly,
    Auto
}

public enum ProxyType { None, Socks4, Socks5, HttpConnect }
public enum FxpProtectionMode { AutoSecure, Clear }
public enum TlsPolicy { Automatic, RequireTls13, Tls12Only }

public sealed record ProxyConfiguration(ProxyType Type = ProxyType.None, string Host = "", int Port = 0,
    string Username = "", string Password = "", bool ProxyDns = true, bool UseForData = true);

public sealed record SiteOptions(
    int MaxSlots = 2,
    int MaxUploadSlots = 2,
    int MaxDownloadSlots = 2,
    int Priority = 0,
    bool AllowUpload = true,
    bool AllowDownload = true,
    bool StayLoggedIn = false,
    string BasePath = "/",
    bool PreferTlsTransfers = true,
    bool ForceBinaryMode = true,
    int MaxIdleSeconds = 60,
    string BlockTransfersFrom = "",
    string BlockTransfersTo = "",
    bool SecureFileListings = true,
    bool NeedsPret = false,
    bool CeprSupported = false,
    bool UseXdupe = false,
    string Affils = "",
    FxpProtectionMode FxpProtection = FxpProtectionMode.AutoSecure,
    bool BrokenPasv = false);

public sealed record ConnectionProfile(
    Guid Id,
    string Name,
    string Host,
    int Port,
    string Username,
    TransferProtocol Protocol,
    [property: JsonIgnore] string Password = "",
    bool AllowInvalidCertificate = false,
    DirectoryListingMode ListingMode = DirectoryListingMode.Auto,
    SiteOptions? Options = null,
    ProxyConfiguration? Proxy = null,
    string AlternateAddresses = "",
    string Description = "",
    string SshHostKeyFingerprint = "",
    TlsPolicy TlsPolicy = TlsPolicy.Automatic)
{
    [JsonIgnore] public SiteOptions EffectiveOptions => Options ?? new SiteOptions();
    [JsonIgnore] public string ProtocolDisplay => TransferProtocolNames.Display(Protocol);
    [JsonIgnore] public IReadOnlyList<SiteEndpoint> EffectiveAddresses
    {
        get
        {
            var result = new List<SiteEndpoint> { new(Host, Port) };
            foreach (var token in AlternateAddresses.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (SiteEndpoint.TryParse(token, Port, out var endpoint) && !result.Contains(endpoint)) result.Add(endpoint);
            return result;
        }
    }
}

public sealed record SiteEndpoint(string Host, int Port)
{
    public override string ToString() => Host.Contains(':') ? $"[{Host}]:{Port}" : $"{Host}:{Port}";

    public static bool TryParse(string value, int defaultPort, out SiteEndpoint endpoint)
    {
        endpoint = new("", defaultPort);
        var text = value.Trim();
        if (text.Length == 0) return false;
        string host; var port = defaultPort;
        if (text.StartsWith('[') && text.IndexOf(']') is var close && close > 0)
        {
            host = text[1..close];
            if (close + 1 < text.Length && (!text[(close + 1)..].StartsWith(':') || !int.TryParse(text[(close + 2)..], out port))) return false;
        }
        else
        {
            var separator = text.LastIndexOf(':');
            if (separator > 0 && text.Count(ch => ch == ':') == 1 && int.TryParse(text[(separator + 1)..], out var parsed))
            { host = text[..separator]; port = parsed; }
            else host = text;
        }
        if (host.Length == 0 || port is < 1 or > 65535) return false;
        endpoint = new(host, port); return true;
    }
}
