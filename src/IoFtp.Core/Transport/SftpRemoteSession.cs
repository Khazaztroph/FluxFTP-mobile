using System.Security.Cryptography;
using IoFtp.Core.Abstractions;
using IoFtp.Core.Models;
using Renci.SshNet;

namespace IoFtp.Core.Transport;

public sealed class SftpRemoteSession : IRemoteSession
{
    private SftpClient? _client;
    private string? _observedFingerprint;

    public bool IsConnected => _client?.IsConnected == true;
    public IReadOnlySet<string> Capabilities { get; } = new HashSet<string>(
        ["LIST", "RETR", "STOR", "MKDIR", "DELETE", "RENAME", "RESUME"],
        StringComparer.OrdinalIgnoreCase);

    public async Task ConnectAsync(ConnectionProfile profile, CancellationToken cancellationToken)
    {
        if (profile.Protocol != TransferProtocol.Sftp)
            throw new ArgumentException("The profile is not configured for SFTP.", nameof(profile));

        await DisconnectAsync(CancellationToken.None);
        _observedFingerprint = null;
        var connection = new PasswordConnectionInfo(
            profile.Host, profile.Port, profile.Username, profile.Password)
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        var client = new SftpClient(connection);
        client.HostKeyReceived += (_, args) =>
        {
            _observedFingerprint = "SHA256:" +
                Convert.ToBase64String(SHA256.HashData(args.HostKey)).TrimEnd('=');
            args.CanTrust = profile.AllowInvalidCertificate ||
                FingerprintsEqual(profile.SshHostKeyFingerprint, _observedFingerprint);
        };
        _client = client;
        try
        {
            await client.ConnectAsync(cancellationToken);
        }
        catch (Exception exception) when (
            !profile.AllowInvalidCertificate &&
            string.IsNullOrWhiteSpace(profile.SshHostKeyFingerprint) &&
            _observedFingerprint is not null)
        {
            client.Dispose();
            _client = null;
            throw new SshHostKeyException(_observedFingerprint, exception);
        }
        catch
        {
            client.Dispose();
            _client = null;
            throw;
        }
    }

    public async Task<IReadOnlyList<RemoteEntry>> ListAsync(
        string path, CancellationToken cancellationToken)
    {
        var client = Client();
        var files = await Task.Run(
            () => client.ListDirectory(path).ToList(), cancellationToken);
        return files
            .Where(file => file.Name is not "." and not "..")
            .Select(file => new RemoteEntry(
                file.Name,
                NormalizeRemotePath(file.FullName),
                file.IsDirectory,
                file.IsDirectory ? null : file.Length,
                file.LastWriteTimeUtc == default
                    ? null
                    : new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero),
                file.Attributes?.ToString() ?? ""))
            .ToList();
    }

    public async Task DownloadAsync(
        string remotePath, Stream destination, long offset,
        IProgress<long>? progress, CancellationToken cancellationToken)
    {
        await using var source = Client().OpenRead(remotePath);
        if (offset > 0)
        {
            source.Seek(offset, SeekOrigin.Begin);
            if (destination.CanSeek) destination.Seek(offset, SeekOrigin.Begin);
        }
        await CopyWithProgressAsync(source, destination, offset, progress, cancellationToken);
    }

    public async Task UploadAsync(
        string remotePath, Stream source, long offset,
        IProgress<long>? progress, CancellationToken cancellationToken)
    {
        await using var destination = Client().Open(
            remotePath,
            offset > 0 ? FileMode.OpenOrCreate : FileMode.Create,
            FileAccess.Write);
        if (offset > 0)
        {
            destination.Seek(offset, SeekOrigin.Begin);
            if (source.CanSeek) source.Seek(offset, SeekOrigin.Begin);
        }
        await CopyWithProgressAsync(source, destination, offset, progress, cancellationToken);
    }

    public Task<long?> GetSizeAsync(string remotePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var client = Client();
        return Task.FromResult(client.Exists(remotePath)
            ? (long?)client.GetAttributes(remotePath).Size
            : null);
    }

    public Task<RemoteCommandResult> ExecuteCommandAsync(
        string command, CancellationToken cancellationToken)
    {
        if (command.StartsWith("MKD ", StringComparison.OrdinalIgnoreCase))
        {
            var path = command[4..].Trim();
            if (!Client().Exists(path)) Client().CreateDirectory(path);
            return Task.FromResult(new RemoteCommandResult(257, $"Created {path}"));
        }
        return Task.FromResult(new RemoteCommandResult(
            502, "Raw FTP commands are not available over SFTP."));
    }

    public Task DisconnectAsync(CancellationToken cancellationToken)
    {
        var client = Interlocked.Exchange(ref _client, null);
        if (client is null) return Task.CompletedTask;
        try
        {
            if (client.IsConnected) client.Disconnect();
        }
        finally
        {
            client.Dispose();
        }
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() =>
        await DisconnectAsync(CancellationToken.None);

    private SftpClient Client() =>
        _client is { IsConnected: true } client
            ? client
            : throw new InvalidOperationException("The SFTP session is not connected.");

    private static async Task CopyWithProgressAsync(
        Stream source, Stream destination, long offset,
        IProgress<long>? progress, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        var transferred = offset;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            transferred += read;
            progress?.Report(transferred);
        }
        await destination.FlushAsync(cancellationToken);
    }

    private static bool FingerprintsEqual(string configured, string observed)
    {
        static string Normalize(string value) =>
            value.Trim().Replace("SHA256:", "", StringComparison.OrdinalIgnoreCase)
                .TrimEnd('=');
        if (string.IsNullOrWhiteSpace(configured)) return false;
        var configuredBytes = System.Text.Encoding.ASCII.GetBytes(Normalize(configured));
        var observedBytes = System.Text.Encoding.ASCII.GetBytes(Normalize(observed));
        return configuredBytes.Length == observedBytes.Length &&
            CryptographicOperations.FixedTimeEquals(configuredBytes, observedBytes);
    }

    private static string NormalizeRemotePath(string path) =>
        path.StartsWith('/') ? path : "/" + path;
}

public sealed class SshHostKeyException(string fingerprint, Exception innerException)
    : IOException(
        $"Serverns SSH-host key är inte betrodd. Fingeravtryck: {fingerprint}",
        innerException)
{
    public string Fingerprint { get; } = fingerprint;
}
