using IoFtp.Core.Abstractions;
using IoFtp.Core.Models;
using IoFtp.Core.Transport;
using ConnectionProfile = IoFtp.Core.Models.ConnectionProfile;

namespace IoFtp.Mobile.Services;

public sealed class RemoteBrowserService : IAsyncDisposable
{
    private IRemoteSession? _session;

    public async Task ConnectAsync(ConnectionProfile profile, CancellationToken token)
    {
        if (_session is not null) await _session.DisposeAsync();
        _session = profile.Protocol switch
        {
            TransferProtocol.Sftp => new SftpRemoteSession(),
            _ => new FtpRemoteSession()
        };
        await _session.ConnectAsync(profile, token);
    }

    public Task<IReadOnlyList<RemoteEntry>> ListAsync(string path, CancellationToken token) =>
        Session().ListAsync(path, token);
    public Task UploadAsync(string path, Stream source, IProgress<long>? progress, CancellationToken token) =>
        Session().UploadAsync(path, source, 0, progress, token);
    public Task DownloadAsync(string path, Stream destination, IProgress<long>? progress, CancellationToken token) =>
        Session().DownloadAsync(path, destination, 0, progress, token);
    public async Task CreateDirectoryAsync(string path, CancellationToken token)
    {
        var result = await Session().ExecuteCommandAsync($"MKD {path}", token);
        if (result.StatusCode is >= 400 and not 550)
            throw new IOException(result.Message);
    }

    private IRemoteSession Session() =>
        _session is { IsConnected: true } ? _session :
            throw new InvalidOperationException("Anslut till en site först.");

    public async ValueTask DisposeAsync()
    {
        if (_session is not null) await _session.DisposeAsync();
    }
}
