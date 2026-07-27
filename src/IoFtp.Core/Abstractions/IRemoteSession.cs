using IoFtp.Core.Models;

namespace IoFtp.Core.Abstractions;

public interface IRemoteSession : IAsyncDisposable
{
    bool IsConnected { get; }
    IReadOnlySet<string> Capabilities { get; }

    Task ConnectAsync(ConnectionProfile profile, CancellationToken cancellationToken);
    Task<IReadOnlyList<RemoteEntry>> ListAsync(string path, CancellationToken cancellationToken);
    Task DownloadAsync(string remotePath, Stream destination, long offset, IProgress<long>? progress, CancellationToken cancellationToken);
    Task UploadAsync(string remotePath, Stream source, long offset, IProgress<long>? progress, CancellationToken cancellationToken);
    Task<RemoteCommandResult> ExecuteCommandAsync(string command, CancellationToken cancellationToken);
    Task DisconnectAsync(CancellationToken cancellationToken);
}

public sealed record RemoteEntry(
    string Name,
    string FullPath,
    bool IsDirectory,
    long? Size,
    DateTimeOffset? ModifiedAt,
    string Attributes = "");

public sealed record RemoteCommandResult(int StatusCode, string Message);
