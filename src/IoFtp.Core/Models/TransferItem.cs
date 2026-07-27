namespace IoFtp.Core.Models;

public enum TransferState
{
    Queued,
    Transferring,
    Paused,
    Completed,
    Failed
}

public sealed record TransferItem(
    Guid Id,
    string Name,
    string Source,
    string Destination,
    long Size,
    long BytesTransferred,
    TransferState State);

