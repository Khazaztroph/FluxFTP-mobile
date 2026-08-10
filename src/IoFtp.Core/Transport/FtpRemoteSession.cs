using System.Globalization;
using System.Net.Security;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using IoFtp.Core.Abstractions;
using IoFtp.Core.Models;

namespace IoFtp.Core.Transport;

public sealed class FtpRemoteSession : IRemoteSession
{
    private TcpClient? _controlClient;
    private Stream? _controlStream;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private ConnectionProfile? _profile;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private bool _protectData = true;
    private bool _automaticTls12Fallback;

    public bool IsConnected { get; private set; }
    public string ConnectedHost { get; private set; } = "";
    public int ConnectedPort { get; private set; }
    public IReadOnlySet<string> Capabilities { get; private set; } = new HashSet<string>();
    public string LastFxpNegotiation { get; private set; } = "None";
    public string ConnectionSecurity { get; private set; } = "FTP";
    public FxpProtectionMode FxpProtection => _profile?.EffectiveOptions.FxpProtection ?? FxpProtectionMode.AutoSecure;

    public async Task ConnectAsync(ConnectionProfile profile, CancellationToken cancellationToken)
    {
        _automaticTls12Fallback = false;
        try
        {
            await ConnectCoreAsync(profile, cancellationToken);
        }
        catch (Exception exception) when (
            profile.Protocol is TransferProtocol.FtpsExplicit or TransferProtocol.FtpsImplicit &&
            profile.TlsPolicy == TlsPolicy.Automatic &&
            IsMalformedTlsFrame(exception) &&
            !cancellationToken.IsCancellationRequested)
        {
            using (var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                await DisconnectAsync(cleanupTimeout.Token);
            _automaticTls12Fallback = true;
            await ConnectCoreAsync(profile, cancellationToken);
        }
    }

    private async Task ConnectCoreAsync(ConnectionProfile profile, CancellationToken cancellationToken)
    {
        if (profile.Protocol == TransferProtocol.Sftp)
            throw new NotSupportedException("SFTP transport is not available yet.");

        _profile = profile;
        _protectData = true;
        // Prefer IPv4 for FTP/FXP. A dual-stack DNS result could otherwise make
        // the control connection IPv6, while PORT/CPSV secure FXP is IPv4-only.
        (_controlClient, var connectedEndpoint) = await ConnectToFirstAddressAsync(profile, cancellationToken);
        ConnectedHost = connectedEndpoint.Host;
        ConnectedPort = connectedEndpoint.Port;
        _controlStream = _controlClient.GetStream();

        if (profile.Protocol == TransferProtocol.FtpsImplicit)
            await EnableTlsAsync(cancellationToken);

        CreateTextStreams();
        EnsureSuccess(await ReadResponseAsync(cancellationToken), 220);

        if (profile.Protocol == TransferProtocol.FtpsExplicit)
        {
            var authResponse = await CommandAsync("AUTH TLS", cancellationToken);
            if (authResponse.Code is not (234 or 334))
                authResponse = await CommandAsync("AUTH SSL", cancellationToken);
            EnsureSuccess(authResponse, 234, 334);
            _writer?.Dispose();
            _reader?.Dispose();
            _writer = null;
            _reader = null;
            await EnableTlsAsync(cancellationToken);
            CreateTextStreams();
        }

        var username = string.IsNullOrWhiteSpace(profile.Username) ? "anonymous" : profile.Username;
        var password = string.IsNullOrWhiteSpace(profile.Username) ? "fluxftp@localhost" : profile.Password;
        var userResponse = await CommandAsync($"USER {username}", cancellationToken);
        if (userResponse.Code == 331)
            EnsureSuccess(await CommandAsync($"PASS {password}", cancellationToken), 230);
        else
            EnsureSuccess(userResponse, 230);

        // DrFTPD requires login before PBSZ/PROT. glFTPD and ioFTPD accept
        // this ordering as well. If PROT P is unavailable, keep the encrypted
        // control channel and explicitly fall back to clear data with PROT C.
        if (profile.Protocol is TransferProtocol.FtpsExplicit or TransferProtocol.FtpsImplicit)
        {
            EnsureSuccess(await CommandAsync("PBSZ 0", cancellationToken), 200);
            var privateProtection = await CommandAsync("PROT P", cancellationToken);
            if (privateProtection.Code is >= 200 and < 300)
                _protectData = true;
            else
            {
                EnsureSuccess(await CommandAsync("PROT C", cancellationToken), 200);
                _protectData = false;
            }
        }

        if (profile.EffectiveOptions.ForceBinaryMode)
            EnsureSuccess(await CommandAsync("TYPE I", cancellationToken), 200);

        if (profile.EffectiveOptions.UseXdupe)
            EnsureSuccess(await CommandAsync("SITE XDUPE 3", cancellationToken), 200);

        IsConnected = true;
        var capabilities = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "LIST", "RETR", "STOR", "PASV" };
        var features = await CommandAsync("FEAT", cancellationToken);
        if (features.Code is >= 200 and < 300)
        {
            foreach (var line in features.Message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Skip(1).SkipLast(1))
            {
                var feature = line.Trim().Split(' ', 2)[0];
                if (feature.Length > 0)
                {
                    capabilities.Add(feature);
                    // RFC 3659 advertises the MLST feature; the companion MLSD
                    // command is implied and is not normally listed separately.
                    if (feature.Equals("MLST", StringComparison.OrdinalIgnoreCase)) capabilities.Add("MLSD");
                }
            }
        }
        Capabilities = capabilities;
    }

    private static async Task<(TcpClient Client, SiteEndpoint Endpoint)> ConnectToFirstAddressAsync(ConnectionProfile profile, CancellationToken cancellationToken)
    {
        using var race = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var attempts = profile.EffectiveAddresses.Select((endpoint, index) => Task.Run(async () =>
        {
            if (index > 0) await Task.Delay(TimeSpan.FromSeconds(1), race.Token);
            var client = await ProxyConnector.ConnectAsync(endpoint.Host, endpoint.Port, profile.Proxy, race.Token);
            return (client, endpoint);
        }, CancellationToken.None)).ToList();
        Exception? lastError = null;
        while (attempts.Count > 0)
        {
            var completed = await Task.WhenAny(attempts); attempts.Remove(completed);
            try
            {
                var result = await completed;
                race.Cancel();
                foreach (var pending in attempts)
                    _ = pending.ContinueWith(task => { if (task.Status == TaskStatus.RanToCompletion) task.Result.client.Dispose(); }, TaskScheduler.Default);
                return (result.client, result.endpoint);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested) { lastError = exception; }
        }
        throw new IOException("None of the configured site addresses could be reached.", lastError);
    }

    public async Task FxpToAsync(FtpRemoteSession destination, string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        EnsureConnected(); destination.EnsureConnected();
        if (ReferenceEquals(this, destination)) throw new InvalidOperationException("FXP requires two different sessions.");

        var sourceProfile = _profile!;
        var destinationProfile = destination._profile!;
        var clearFxp = sourceProfile.EffectiveOptions.FxpProtection == FxpProtectionMode.Clear ||
            destinationProfile.EffectiveOptions.FxpProtection == FxpProtectionMode.Clear;
        var bothControlChannelsUseTls =
            sourceProfile.Protocol is TransferProtocol.FtpsExplicit or TransferProtocol.FtpsImplicit &&
            destinationProfile.Protocol is TransferProtocol.FtpsExplicit or TransferProtocol.FtpsImplicit;
        if (!clearFxp && !bothControlChannelsUseTls)
            throw new NotSupportedException("Auto FXP requires TLS on both sites. Select Clear FXP explicitly on a site to permit unencrypted server-to-server data.");
        var secureFxp = !clearFxp;
        if (clearFxp)
        {
            if (sourceProfile.Protocol is TransferProtocol.FtpsExplicit or TransferProtocol.FtpsImplicit)
            {
                EnsureSuccess(await CommandAsync("PROT C", cancellationToken), 200);
                _protectData = false;
            }
            if (destinationProfile.Protocol is TransferProtocol.FtpsExplicit or TransferProtocol.FtpsImplicit)
            {
                EnsureSuccess(await destination.CommandAsync("PROT C", cancellationToken), 200);
                destination._protectData = false;
            }
        }
        var usedCpsv = false;
        await PrepareDataCommandAsync($"RETR {sourcePath}", cancellationToken);
        await destination.PrepareDataCommandAsync($"STOR {destinationPath}", cancellationToken);
        FtpResponse passive;
        (string Host, int Port) advertised;
        if (destination._profile!.EffectiveOptions.CeprSupported)
        {
            passive = await destination.CommandAsync("EPSV", cancellationToken);
            EnsureSuccess(passive, 229);
            advertised = ParseExtendedPassiveEndpoint(passive.Message, destination._profile.Host, true);
        }
        else if (secureFxp && destination.Capabilities.Contains("CPSV"))
        {
            passive = await destination.CommandAsync("CPSV", cancellationToken);
            usedCpsv = passive.Code == 227;
            if (!usedCpsv) passive = await destination.CommandAsync("PASV", cancellationToken);
            EnsureSuccess(passive, 227);
            advertised = ParsePassiveEndpoint(passive.Message);
        }
        else
        {
            passive = await destination.CommandAsync("PASV", cancellationToken);
            EnsureSuccess(passive, 227);
            advertised = ParsePassiveEndpoint(passive.Message);
        }
        // For FXP the source server must connect to the address explicitly
        // advertised by the passive destination. This may be its public/NAT
        // address and is intentionally not the control connection address.
        if (!IPAddress.TryParse(advertised.Host, out var destinationAddress))
            throw new IOException("The passive FXP server returned an invalid address.");
        if (destinationAddress.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
            throw new NotSupportedException("The passive FXP server returned an unsupported address family.");

        if (secureFxp && usedCpsv)
        {
            // CPSV makes the passive destination the TLS client for this transfer;
            // the active source must remain in its default TLS server role.
            if (Capabilities.Contains("SSCN")) EnsureSuccess(await CommandAsync("SSCN OFF", cancellationToken), 200);
            LastFxpNegotiation = "CPSV";
        }
        else if (secureFxp)
        {
            var secureClient = await CommandAsync("SSCN ON", cancellationToken);
            if (secureClient.Code is < 200 or >= 300)
                throw new FtpCommandException(secureClient.Code, secureClient.Message);
            var secureServer = await destination.CommandAsync("SSCN OFF", cancellationToken);
            if (secureServer.Code is < 200 or >= 300)
                throw new FtpCommandException(secureServer.Code, secureServer.Message);
            LastFxpNegotiation = "SSCN/PASV";
        }
        else LastFxpNegotiation = "PASV";

        if (destinationAddress.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (!Capabilities.Contains("EPRT")) throw new NotSupportedException("IPv6 FXP requires EPRT support on the source server.");
            EnsureSuccess(await CommandAsync($"EPRT |2|{destinationAddress}|{advertised.Port}|", cancellationToken), 200);
        }
        else
        {
            var bytes = destinationAddress.GetAddressBytes();
            EnsureSuccess(await CommandAsync($"PORT {string.Join(',', bytes)},{advertised.Port / 256},{advertised.Port % 256}", cancellationToken), 200);
        }

        var store = await destination.CommandAsync($"STOR {destinationPath}", cancellationToken);
        EnsureSuccess(store, 125, 150);
        var retrieve = await CommandAsync($"RETR {sourcePath}", cancellationToken);
        EnsureSuccess(retrieve, 125, 150);

        var completions = await Task.WhenAll(ReadResponseAsync(cancellationToken), destination.ReadResponseAsync(cancellationToken));
        EnsureSuccess(completions[0], 226, 250);
        EnsureSuccess(completions[1], 226, 250);
    }

    public async Task<IReadOnlyList<RemoteEntry>> ListAsync(string path, CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken);
        var clearListing = _profile?.Protocol is TransferProtocol.FtpsExplicit or TransferProtocol.FtpsImplicit &&
            _profile.EffectiveOptions.SecureFileListings == false;
        try
        {
            if (clearListing)
            {
                EnsureSuccess(await CommandAsync("PROT C", cancellationToken), 200);
                _protectData = false;
            }
            return await ListCoreAsync(path, cancellationToken);
        }
        catch (Exception exception)
        {
            throw new IOException($"LIST operation failed ({(_protectData ? "protected data" : "clear data")}): {exception.Message}", exception);
        }
        finally
        {
            if (clearListing)
            {
                _protectData = true;
                try { EnsureSuccess(await CommandAsync("PROT P", CancellationToken.None), 200); } catch { }
            }
            _operationGate.Release();
        }
    }

    private async Task<IReadOnlyList<RemoteEntry>> ListCoreAsync(string path, CancellationToken cancellationToken)
    {
        EnsureConnected();
        if (_profile!.ListingMode == DirectoryListingMode.Auto)
            return await ListAutomaticAsync(path, cancellationToken);
        return await ListDataCoreAsync(path, "LIST", ParseListing, cancellationToken, allowPreferredStat: true);
    }

    private async Task<IReadOnlyList<RemoteEntry>> ListAutomaticAsync(string path, CancellationToken cancellationToken)
    {
        var failures = new List<Exception>();
        if (Capabilities.Contains("MLSD"))
        {
            try { return await ListDataCoreAsync(path, "MLSD", ParseMlsdListing, cancellationToken); }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failures.Add(exception);
                await ReconnectAfterListResetAsync(cancellationToken);
            }
        }

        try { return await ListDataCoreAsync(path, "LIST", ParseListing, cancellationToken); }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            failures.Add(exception);
            await ReconnectAfterListResetAsync(cancellationToken);
        }

        var stat = await CommandAsync($"STAT -l {path}", cancellationToken);
        if (stat.Code is >= 200 and < 300) return ParseStatListing(path, stat.Message);
        failures.Add(new FtpCommandException(stat.Code, stat.Message));
        throw new AggregateException("MLSD, LIST and STAT -l all failed.", failures);
    }

    private async Task<IReadOnlyList<RemoteEntry>> ListDataCoreAsync(string path, string command,
        Func<string, string, IReadOnlyList<RemoteEntry>> parser, CancellationToken cancellationToken,
        bool allowPreferredStat = false)
    {
        var shouldTryStat = _profile!.ListingMode == DirectoryListingMode.StatOnly ||
            (allowPreferredStat && _profile.ListingMode == DirectoryListingMode.StatThenList && Capabilities.Contains("STAT"));
        if (shouldTryStat)
        {
            var stat = await CommandAsync($"STAT -l {path}", cancellationToken);
            if (stat.Code is >= 200 and < 300)
            {
                var statEntries = ParseStatListing(path, stat.Message);
                if (statEntries.Count > 0 || _profile.ListingMode == DirectoryListingMode.StatOnly)
                    return statEntries;
                // ProFTPD can answer STAT -l with a successful status block
                // without returning directory rows. In automatic mode this
                // must continue to a real LIST data transfer.
            }
            if (_profile.ListingMode == DirectoryListingMode.StatOnly)
                throw new FtpCommandException(stat.Code, stat.Message);
        }

        // FluxFTP 1.0.25-compatible Broken PASV behavior. A marked site is
        // placed in the PORT/active role immediately rather than waiting for
        // EPSV/PASV to fail or time out first.
        if (_profile!.EffectiveOptions.BrokenPasv)
            return await ListActiveAsync(path, command, parser, cancellationToken);

        await PrepareDataCommandAsync($"{command} {path}", cancellationToken);
        var endpoint = await OpenPassiveEndpointAsync(cancellationToken);

        TcpClient dataClient;
        try
        {
            using var passiveTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            passiveTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            dataClient = await ProxyConnector.ConnectAsync(endpoint.Host, endpoint.Port, _profile?.Proxy is { UseForData: true } proxy ? proxy : null, passiveTimeout.Token);
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return await ListActiveAsync(path, command, parser, cancellationToken);
        }

        using var ownedDataClient = dataClient;
        var listResponse = await CommandAsync($"{command} {path}", cancellationToken);
        EnsureSuccess(listResponse, 125, 150);
        // FTPS servers begin the data-channel TLS handshake only after accepting
        // the transfer command. Starting TLS before LIST deadlocks with ioFTPD.
        Stream dataStream;
        try { dataStream = await ProtectDataStreamAsync(dataClient.GetStream(), cancellationToken); }
        catch (Exception exception) when (_protectData &&
            _profile!.Protocol is TransferProtocol.FtpsExplicit or TransferProtocol.FtpsImplicit &&
            exception is IOException or AuthenticationException)
        {
            // Consume the failed LIST completion so the control channel stays
            // aligned before changing protection mode and retrying.
            var failedCompletion = await ReadResponseAsync(cancellationToken);
            var clearProtection = await CommandAsync("PROT C", cancellationToken);
            if (clearProtection.Code != 200)
                throw new FtpCommandException(clearProtection.Code,
                    $"TLS data handshake failed ({failedCompletion.Code}); clear LIST fallback was rejected: {clearProtection.Message}");
            _protectData = false;
            try { return await ListDataCoreAsync(path, command, parser, cancellationToken, allowPreferredStat); }
            finally
            {
                _protectData = true;
                var privateProtection = await CommandAsync("PROT P", CancellationToken.None);
                EnsureSuccess(privateProtection, 200);
            }
        }
        string listing;
        try { listing = await ReadListingDataAsync(dataStream, cancellationToken); }
        finally { await DisposeDataStreamSafelyAsync(dataStream); }
        IReadOnlyList<RemoteEntry> parsedListing = parser(path, listing);
        try
        {
            var completion = await ReadResponseAsync(cancellationToken);
            EnsureSuccess(completion, 226, 250);
        }
        catch (IOException exception)
        {
            // Some managed ProFTPD hosts reset the FTPS control socket after
            // returning a large LIST payload. Preserve valid rows and rebuild
            // the browsing session so the next command starts synchronized.
            await ReconnectAfterListResetAsync(cancellationToken);
            if (parsedListing.Count == 0)
            {
                var stat = await CommandAsync($"STAT -l {path}", cancellationToken);
                if (stat.Code is >= 200 and < 300) parsedListing = ParseStatListing(path, stat.Message);
                if (parsedListing.Count == 0)
                    throw new IOException($"ProFTPD reset {command} for {path}, and STAT -l returned no directory entries.", exception);
            }
        }
        return parsedListing;
    }

    public Task DownloadAsync(string remotePath, Stream destination, long offset, IProgress<long>? progress, CancellationToken cancellationToken) =>
        TransferAsync($"RETR {remotePath}", offset, async data =>
        {
            var buffer = new byte[64 * 1024]; long total = offset; int read;
            while ((read = await data.ReadAsync(buffer, cancellationToken)) > 0)
            { await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken); total += read; progress?.Report(total); }
        }, cancellationToken);

    public Task UploadAsync(string remotePath, Stream source, long offset, IProgress<long>? progress, CancellationToken cancellationToken) =>
        TransferAsync($"STOR {remotePath}", offset, async data =>
        {
            var buffer = new byte[64 * 1024]; long total = offset; int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            { await data.WriteAsync(buffer.AsMemory(0, read), cancellationToken); total += read; progress?.Report(total); }
            await data.FlushAsync(cancellationToken);
        }, cancellationToken);

    public async Task<RemoteCommandResult> ExecuteCommandAsync(string command, CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
        EnsureConnected();
        var normalized = command.Trim();
        if (normalized.Length == 0 || normalized.Contains('\r') || normalized.Contains('\n'))
            throw new ArgumentException("Enter exactly one FTP command.", nameof(command));
        var verb = normalized.Split(' ', 2)[0];
        if (verb.Equals("PASS", StringComparison.OrdinalIgnoreCase) || verb.Equals("USER", StringComparison.OrdinalIgnoreCase) || verb.Equals("ACCT", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Credential commands are blocked in the command console.");
        var response = await CommandAsync(normalized, cancellationToken);
        return new RemoteCommandResult(response.Code, response.Message);
        }
        finally { _operationGate.Release(); }
    }

    public async Task<long?> GetSizeAsync(string remotePath, CancellationToken cancellationToken)
    {
        EnsureConnected();
        var response = await CommandAsync($"SIZE {remotePath}", cancellationToken);
        if (response.Code != 213) return null;
        var value = response.Message.Split([' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return long.TryParse(value, out var size) ? size : null;
    }

    private async Task TransferAsync(string command, long offset, Func<Stream, Task> transfer, CancellationToken cancellationToken)
    {
        EnsureConnected();
        if (_profile!.EffectiveOptions.BrokenPasv)
        {
            await TransferActiveAsync(command, offset, transfer, cancellationToken);
            return;
        }

        TcpClient? dataClient = null;
        try
        {
            await PrepareDataCommandAsync(command, cancellationToken);
            var endpoint = await OpenPassiveEndpointAsync(cancellationToken);
            using var passiveTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            passiveTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            dataClient = await ProxyConnector.ConnectAsync(endpoint.Host, endpoint.Port, _profile?.Proxy is { UseForData: true } proxy ? proxy : null, passiveTimeout.Token);
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            dataClient?.Dispose();
            await TransferActiveAsync(command, offset, transfer, cancellationToken);
            return;
        }

        Exception? dataError = null;
        using (dataClient)
        {
            if (offset > 0) EnsureSuccess(await CommandAsync($"REST {offset}", cancellationToken), 350);
            EnsureSuccess(await CommandAsync(command, cancellationToken), 125, 150);
            try
            {
                await using var stream = await ProtectDataStreamAsync(dataClient.GetStream(), cancellationToken);
                await transfer(stream);
            }
            catch (IOException exception) { dataError = exception; }
        }
        var completion = await ReadResponseAsync(cancellationToken);
        EnsureSuccess(completion, 226, 250);
        // Some Windows FTPS stacks report WSAENETNAMEDELETED when the peer
        // closes TLS immediately after the last byte. A successful 226/250
        // control reply confirms that the transfer itself completed.
        if (dataError is not null && completion.Code is not (226 or 250)) throw dataError;
    }

    private async Task TransferActiveAsync(string command, long offset, Func<Stream, Task> transfer, CancellationToken cancellationToken)
    {
        var localEndpoint = (IPEndPoint)_controlClient!.Client.LocalEndPoint!;
        var listener = new TcpListener(localEndpoint.Address, 0); listener.Start();
        try
        {
            await PrepareDataCommandAsync(command, cancellationToken);
            var port = ((IPEndPoint)listener.LocalEndpoint).Port; var address = localEndpoint.Address.GetAddressBytes();
            EnsureSuccess(await CommandAsync($"PORT {string.Join(',', address)},{port / 256},{port % 256}", cancellationToken), 200);
            if (offset > 0) EnsureSuccess(await CommandAsync($"REST {offset}", cancellationToken), 350);
            EnsureSuccess(await CommandAsync(command, cancellationToken), 125, 150);
            using var client = await listener.AcceptTcpClientAsync(cancellationToken);
            await using var stream = await ProtectDataStreamAsync(client.GetStream(), cancellationToken);
            await transfer(stream);
            EnsureSuccess(await ReadResponseAsync(cancellationToken), 226, 250);
        }
        finally { listener.Stop(); }
    }

    private async Task<IReadOnlyList<RemoteEntry>> ListActiveAsync(string path, string command,
        Func<string, string, IReadOnlyList<RemoteEntry>> parser, CancellationToken cancellationToken)
    {
        var localEndpoint = (IPEndPoint)_controlClient!.Client.LocalEndPoint!;
        if (localEndpoint.AddressFamily != AddressFamily.InterNetwork)
            throw new IOException("Active FTP fallback currently requires an IPv4 connection.");

        var listener = new TcpListener(localEndpoint.Address, 0);
        listener.Start();
        try
        {
            await PrepareDataCommandAsync($"{command} {path}", cancellationToken);
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var address = localEndpoint.Address.GetAddressBytes();
            EnsureSuccess(await CommandAsync($"PORT {string.Join(',', address)},{port / 256},{port % 256}", cancellationToken), 200);
            var listResponse = await CommandAsync($"{command} {path}", cancellationToken);
            EnsureSuccess(listResponse, 125, 150);

            using var dataClient = await listener.AcceptTcpClientAsync(cancellationToken);
            var dataStream = await ProtectDataStreamAsync(dataClient.GetStream(), cancellationToken);
            string listing;
            try { listing = await ReadListingDataAsync(dataStream, cancellationToken); }
            finally { await DisposeDataStreamSafelyAsync(dataStream); }
            var completion = await ReadResponseAsync(cancellationToken);
            EnsureSuccess(completion, 226, 250);
            return parser(path, listing);
        }
        finally { listener.Stop(); }
    }

    private async Task<Stream> ProtectDataStreamAsync(Stream stream, CancellationToken cancellationToken)
    {
        if (!_protectData || _profile!.Protocol is not (TransferProtocol.FtpsExplicit or TransferProtocol.FtpsImplicit)) return stream;
        var ssl = new SslStream(stream, false, ValidateCertificate);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = string.IsNullOrWhiteSpace(ConnectedHost) ? _profile.Host : ConnectedHost,
            EnabledSslProtocols = EnabledTlsProtocols()
        }, cancellationToken);
        return ssl;
    }

    private static async Task<string> ReadListingDataAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, true);
        var result = new StringBuilder();
        var buffer = new char[4096];
        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
                result.Append(buffer, 0, read);
        }
        catch (IOException)
        {
            // ProFTPD on Windows-facing FTPS connections may reset the TLS
            // data socket after the final listing byte instead of sending
            // close_notify. The control-channel completion is authoritative.
        }
        return result.ToString();
    }

    private static async Task DisposeDataStreamSafelyAsync(Stream stream)
    {
        try { await stream.DisposeAsync(); }
        catch (IOException)
        {
            // ProFTPD may reset a completed TLS data connection instead of
            // performing close_notify. The following control reply determines
            // whether the FTP operation succeeded.
        }
    }

    private async Task ReconnectAfterListResetAsync(CancellationToken cancellationToken)
    {
        var profile = _profile ?? throw new IOException("The FTP profile is unavailable for reconnect.");
        using (var disconnectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
            await DisconnectAsync(disconnectTimeout.Token);
        await ConnectAsync(profile, cancellationToken);
        if (!_protectData && profile.Protocol is TransferProtocol.FtpsExplicit or TransferProtocol.FtpsImplicit)
            EnsureSuccess(await CommandAsync("PROT C", cancellationToken), 200);
    }

    private static IReadOnlyList<RemoteEntry> ParseListing(string path, string listing) =>
        listing.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => !IsListingSummary(line))
            .Select(line => ParseEntry(path, line)).Where(entry => entry is not null).Cast<RemoteEntry>().ToList();

    private static IReadOnlyList<RemoteEntry> ParseMlsdListing(string path, string listing) =>
        listing.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => ParseMlsdEntry(path, line)).Where(entry => entry is not null).Cast<RemoteEntry>().ToList();

    private static RemoteEntry? ParseMlsdEntry(string parent, string line)
    {
        var separator = line.IndexOf(' ');
        if (separator <= 0 || separator == line.Length - 1) return null;
        var facts = line[..separator].Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(fact => fact.Split('=', 2)).Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.OrdinalIgnoreCase);
        var name = line[(separator + 1)..].Trim();
        if (name.Length == 0 || name is "." or "..") return null;
        var type = facts.GetValueOrDefault("type", "file");
        if (type.Equals("cdir", StringComparison.OrdinalIgnoreCase) || type.Equals("pdir", StringComparison.OrdinalIgnoreCase)) return null;
        var directory = type.Equals("dir", StringComparison.OrdinalIgnoreCase);
        long? size = !directory && long.TryParse(facts.GetValueOrDefault("size"), out var bytes) ? bytes : null;
        DateTimeOffset? modified = DateTimeOffset.TryParseExact(facts.GetValueOrDefault("modify"), "yyyyMMddHHmmss",
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp) ? timestamp : null;
        return new(name, Combine(parent, name), directory, size, modified, facts.GetValueOrDefault("perm", type));
    }

    private static IReadOnlyList<RemoteEntry> ParseStatListing(string path, string response)
    {
        var lines = response.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        // Multiline FTP replies wrap the raw directory rows in a numeric header/footer.
        var listingLines = lines.Where(line => !(line.Length >= 3 && int.TryParse(line[..3], out _)))
            .Select(line => line.Trim()).Where(LooksLikeListEntry);
        return listingLines.Select(line => ParseEntry(path, line.Trim()))
            .Where(entry => entry is not null).Cast<RemoteEntry>().ToList();
    }

    private static bool LooksLikeListEntry(string line)
    {
        if (IsListingSummary(line)) return false;
        if (line.Length > 0 && line[0] is 'd' or '-' or 'l') return line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 9;
        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length >= 4 && DateTimeOffset.TryParse($"{fields[0]} {fields[1]}", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal, out _);
    }

    private static bool IsListingSummary(string line)
    {
        var fields = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length == 2 &&
            fields[0].Equals("total", StringComparison.OrdinalIgnoreCase) &&
            long.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
    }

    private async Task<(string Host, int Port)> OpenPassiveEndpointAsync(CancellationToken cancellationToken)
    {
        // EPSV keeps the data connection on the same host as the control connection,
        // avoiding the unusable private addresses many FTP servers advertise in PASV.
        var profile = _profile ?? throw new InvalidOperationException("The connection profile is unavailable.");
        // Some older servers fail the complete data operation after an
        // unsupported EPSV probe. Only use EPSV when FEAT advertises it.
        if (Capabilities.Contains("EPSV") || profile.EffectiveOptions.CeprSupported)
        {
            var extended = await CommandAsync("EPSV", cancellationToken);
            if (extended.Code == 229)
                return ParseExtendedPassiveEndpoint(extended.Message, ConnectedHost, profile.EffectiveOptions.CeprSupported);
        }

        var passive = await CommandAsync("PASV", cancellationToken);
        EnsureSuccess(passive, 227);
        var advertised = ParsePassiveEndpoint(passive.Message);
        // When connecting through localhost, never replace it with a server-advertised LAN address.
        var host = profile.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ? profile.Host : advertised.Host;
        return (host, advertised.Port);
    }

    private async Task PrepareDataCommandAsync(string command, CancellationToken cancellationToken)
    {
        if (_profile?.EffectiveOptions.NeedsPret != true) return;
        var response = await CommandAsync($"PRET {command}", cancellationToken);
        EnsureSuccess(response, 200);
    }

    private static async Task<IPAddress> ResolveIpv4Async(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var literal))
        {
            if (literal.AddressFamily == AddressFamily.InterNetwork) return literal;
            if (literal.IsIPv4MappedToIPv6) return literal.MapToIPv4();
            throw new NotSupportedException("FTP and direct FXP currently require an IPv4 server address.");
        }
        var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
        return addresses.FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork)
            ?? throw new NotSupportedException($"No IPv4 address was found for {host}.");
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        if (_writer is not null)
        {
            try { await CommandAsync("QUIT", cancellationToken); } catch { }
        }
        IsConnected = false;
        _writer?.Dispose(); _reader?.Dispose(); _controlStream?.Dispose(); _controlClient?.Dispose();
        _writer = null; _reader = null; _controlStream = null; _controlClient = null; _profile = null;
        Capabilities = new HashSet<string>();
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync(CancellationToken.None);

    private async Task EnableTlsAsync(CancellationToken cancellationToken)
    {
        var ssl = new SslStream(_controlStream!, false, ValidateCertificate);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = string.IsNullOrWhiteSpace(ConnectedHost) ? _profile!.Host : ConnectedHost,
            EnabledSslProtocols = EnabledTlsProtocols()
        }, cancellationToken);
        ConnectionSecurity = DescribeTls(ssl);
        _controlStream = ssl;
    }

    private SslProtocols EnabledTlsProtocols() => _profile?.TlsPolicy switch
    {
        TlsPolicy.RequireTls13 => SslProtocols.Tls13,
        TlsPolicy.Tls12Only => SslProtocols.Tls12,
        _ when _automaticTls12Fallback => SslProtocols.Tls12,
        _ => SslProtocols.Tls12 | SslProtocols.Tls13
    };

    private static string DescribeTls(SslStream ssl) => ssl.SslProtocol switch
    {
        SslProtocols.Tls13 => $"TLS 1.3 • {ssl.NegotiatedCipherSuite}",
        SslProtocols.Tls12 => $"TLS 1.2 • {ssl.NegotiatedCipherSuite}",
        _ => ssl.SslProtocol.ToString()
    };

    private static bool IsMalformedTlsFrame(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current.Message.Contains("Cannot determine the frame size", StringComparison.OrdinalIgnoreCase) ||
                current.Message.Contains("corrupted frame", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private bool ValidateCertificate(object sender, System.Security.Cryptography.X509Certificates.X509Certificate? certificate,
        System.Security.Cryptography.X509Certificates.X509Chain? chain, SslPolicyErrors errors) =>
        errors == SslPolicyErrors.None || _profile?.AllowInvalidCertificate == true;

    private void CreateTextStreams()
    {
        _reader = new StreamReader(_controlStream!, Encoding.ASCII, false, 1024, true);
        _writer = new StreamWriter(_controlStream!, Encoding.ASCII, 1024, true) { NewLine = "\r\n", AutoFlush = true };
    }

    private async Task<FtpResponse> CommandAsync(string command, CancellationToken cancellationToken)
    {
        await _writer!.WriteLineAsync(command.AsMemory(), cancellationToken);
        return await ReadResponseAsync(cancellationToken);
    }

    private async Task<FtpResponse> ReadResponseAsync(CancellationToken cancellationToken)
    {
        var first = await _reader!.ReadLineAsync(cancellationToken) ?? throw new IOException("FTP server closed the connection.");
        if (first.Length < 3 || !int.TryParse(first[..3], out var code)) throw new IOException($"Invalid FTP response: {first}");
        var lines = new List<string> { first };
        if (first.Length > 3 && first[3] == '-')
        {
            var terminator = $"{code} ";
            string line;
            do { line = await _reader.ReadLineAsync(cancellationToken) ?? throw new IOException("FTP server closed the connection."); lines.Add(line); }
            while (!line.StartsWith(terminator, StringComparison.Ordinal));
        }
        return new FtpResponse(code, string.Join(Environment.NewLine, lines));
    }

    private static void EnsureSuccess(FtpResponse response, params int[] allowed)
    {
        if (!allowed.Contains(response.Code)) throw new FtpCommandException(response.Code, response.Message);
    }

    private static (string Host, int Port) ParsePassiveEndpoint(string response)
    {
        var start = response.IndexOf('('); var end = response.IndexOf(')', start + 1);
        if (start < 0 || end < 0) throw new IOException("Server returned an invalid passive-mode address.");
        var values = response[(start + 1)..end].Split(',').Select(int.Parse).ToArray();
        if (values.Length != 6) throw new IOException("Server returned an invalid passive-mode address.");
        return ($"{values[0]}.{values[1]}.{values[2]}.{values[3]}", values[4] * 256 + values[5]);
    }

    private static (string Host, int Port) ParseExtendedPassiveEndpoint(string response, string fallbackHost, bool useCustomAddress)
    {
        var start = response.IndexOf('('); var end = response.IndexOf(')', start + 1);
        if (start < 0 || end < 0) throw new IOException("Server returned an invalid extended passive-mode address.");
        var body = response[(start + 1)..end];
        if (body.Length < 5) throw new IOException("Server returned an invalid extended passive-mode address.");
        var fields = body.Split(body[0], StringSplitOptions.RemoveEmptyEntries);
        var portText = fields.LastOrDefault();
        if (portText is null || !int.TryParse(portText, out var port) || port is < 1 or > 65535)
            throw new IOException("Server returned an invalid extended passive-mode port.");
        var host = useCustomAddress && fields.Length >= 3 && !string.IsNullOrWhiteSpace(fields[^2]) ? fields[^2] : fallbackHost;
        return (host, port);
    }

    private void EnsureConnected() { if (!IsConnected) throw new InvalidOperationException("The remote session is not connected."); }

    private static RemoteEntry? ParseEntry(string parent, string line)
    {
        if (IsListingSummary(line)) return null;
        var unix = line.Split(' ', 9, StringSplitOptions.RemoveEmptyEntries);
        if (unix.Length >= 9 && unix[0].Length > 0 && unix[0][0] is 'd' or '-' or 'l')
        {
            var name = unix[8]; if (name is "." or "..") return null;
            long? size = long.TryParse(unix[4], out var bytes) ? bytes : null;
            var unixModified = ParseUnixModified(unix[5], unix[6], unix[7]);
            return new(name, Combine(parent, name), unix[0][0] == 'd', size, unixModified, unix[0]);
        }
        var windows = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (windows.Length >= 4 && DateTimeOffset.TryParse($"{windows[0]} {windows[1]}", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var modified))
        {
            var name = string.Join(' ', windows.Skip(3)); var directory = windows[2].Equals("<DIR>", StringComparison.OrdinalIgnoreCase);
            long? size = !directory && long.TryParse(windows[2], out var bytes) ? bytes : null;
            return new(name, Combine(parent, name), directory, size, modified, directory ? "<DIR>" : "-");
        }
        return new(line, Combine(parent, line), false, null, null);
    }

    private static DateTimeOffset? ParseUnixModified(string month, string day, string yearOrTime)
    {
        var now = DateTimeOffset.Now;
        var value = $"{month} {day} {yearOrTime}";
        if (yearOrTime.Contains(':'))
        {
            if (!DateTime.TryParseExact(value, "MMM d HH:mm", CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces, out var parsed)) return null;
            var local = new DateTime(now.Year, parsed.Month, parsed.Day, parsed.Hour, parsed.Minute, 0, DateTimeKind.Local);
            if (local > now.LocalDateTime.AddDays(1)) local = local.AddYears(-1);
            return new DateTimeOffset(local);
        }
        if (!DateTime.TryParseExact(value, "MMM d yyyy", CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces, out var dated)) return null;
        return new DateTimeOffset(DateTime.SpecifyKind(dated, DateTimeKind.Local));
    }

    private static string Combine(string parent, string name) => $"/{string.Join('/', new[] { parent.Trim('/'), name }.Where(value => value.Length > 0))}";
    private sealed record FtpResponse(int Code, string Message);
}

public sealed class FtpCommandException(int statusCode, string message) : IOException(message)
{
    public int StatusCode { get; } = statusCode;
}
