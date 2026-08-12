using Android.Content;
using Android.Provider;
using Uri = Android.Net.Uri;

namespace IoFtp.Mobile.Services;

public sealed record LocalFolderLocation(
    string TreeUri,
    string DocumentId,
    string Name,
    string DisplayPath);

public sealed record LocalTransferFile(
    string FileName,
    string RelativePath,
    string DisplayPath,
    long Size,
    Func<Task<Stream>>? OpenReadAsync,
    LocalFolderLocation? Folder = null,
    DateTimeOffset? ModifiedAt = null)
{
    public bool IsDirectory => Folder is not null;
    public string Icon => IsDirectory ? "📁" : "📄";
    public string SizeText => IsDirectory ? "Mapp" : FormatSize(Size);
    public string ModifiedText => ModifiedAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm") ?? "";

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var value = Math.Max(0, (double)bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.0} {units[unit]}";
    }
}

public static class AndroidFolderPicker
{
    public static async Task<IReadOnlyList<LocalTransferFile>> PickFilesAsync()
    {
        var results = await FilePicker.Default.PickMultipleAsync(
            new PickOptions { PickerTitle = "Välj filer för uppladdning" });
        var files = new List<LocalTransferFile>();
        foreach (var result in results)
        {
            var size = 0L;
            await using (var stream = await result.OpenReadAsync())
                if (stream.CanSeek) size = stream.Length;
            files.Add(new LocalTransferFile(
                result.FileName, result.FileName, result.FullPath, size, result.OpenReadAsync));
        }
        return files;
    }

    public static async Task<LocalFolderLocation?> PickFolderAsync()
    {
        var activity = Platform.CurrentActivity ??
            throw new InvalidOperationException("Android-aktiviteten är inte tillgänglig.");
        var completion = new TaskCompletionSource<Uri?>(TaskCreationOptions.RunContinuationsAsynchronously);
        MainActivity.FolderPickerCompletion = completion;
        var intent = new Intent(Intent.ActionOpenDocumentTree);
        intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission |
                        ActivityFlags.GrantPersistableUriPermission | ActivityFlags.GrantPrefixUriPermission);
        activity.StartActivityForResult(intent, MainActivity.FolderPickerRequest);
        var treeUri = await completion.Task;
        if (treeUri is null) return null;

        var resolver = activity.ContentResolver ??
            throw new InvalidOperationException("Androids dokumentlagring är inte tillgänglig.");
        var rootId = DocumentsContract.GetTreeDocumentId(treeUri) ??
            throw new IOException("Den valda mappen saknar dokument-ID.");
        var rootUri = DocumentsContract.BuildDocumentUriUsingTree(treeUri, rootId);
        var rootName = QueryName(resolver, rootUri!) ?? "Lokal mapp";
        return new LocalFolderLocation(treeUri.ToString()!, rootId, rootName, rootName);
    }

    public static async Task<IReadOnlyList<LocalTransferFile>> ListFolderAsync(
        LocalFolderLocation folder, CancellationToken token = default)
    {
        var resolver = Resolver();
        var treeUri = Uri.Parse(folder.TreeUri) ?? throw new IOException("Ogiltig lokal mapp-URI.");
        return await Task.Run(() => ListFolder(resolver, treeUri, folder, token), token);
    }

    public static async Task<IReadOnlyList<LocalTransferFile>> ExpandAsync(
        LocalTransferFile entry, CancellationToken token = default)
    {
        if (!entry.IsDirectory) return [entry];
        var result = new List<LocalTransferFile>();
        await WalkAsync(entry.Folder!, entry.FileName, result, token);
        return result;
    }

    private static async Task WalkAsync(
        LocalFolderLocation folder, string relativeParent,
        List<LocalTransferFile> result, CancellationToken token)
    {
        foreach (var child in await ListFolderAsync(folder, token))
        {
            token.ThrowIfCancellationRequested();
            var relative = $"{relativeParent}/{child.FileName}";
            if (child.IsDirectory)
                await WalkAsync(child.Folder!, relative, result, token);
            else
                result.Add(child with { RelativePath = relative });
        }
    }

    private static IReadOnlyList<LocalTransferFile> ListFolder(
        ContentResolver resolver, Uri treeUri, LocalFolderLocation folder, CancellationToken token)
    {
        var childrenUri = DocumentsContract.BuildChildDocumentsUriUsingTree(treeUri, folder.DocumentId);
        string[] projection =
        [
            DocumentsContract.Document.ColumnDocumentId,
            DocumentsContract.Document.ColumnDisplayName,
            DocumentsContract.Document.ColumnMimeType,
            DocumentsContract.Document.ColumnSize,
            DocumentsContract.Document.ColumnLastModified
        ];
        using var cursor = resolver.Query(childrenUri!, projection, null, null, null);
        if (cursor is null) return [];
        var files = new List<LocalTransferFile>();
        var idIndex = cursor.GetColumnIndex(projection[0]);
        var nameIndex = cursor.GetColumnIndex(projection[1]);
        var mimeIndex = cursor.GetColumnIndex(projection[2]);
        var sizeIndex = cursor.GetColumnIndex(projection[3]);
        var modifiedIndex = cursor.GetColumnIndex(projection[4]);
        while (cursor.MoveToNext())
        {
            token.ThrowIfCancellationRequested();
            var id = cursor.GetString(idIndex);
            if (id is null) continue;
            var name = cursor.GetString(nameIndex) ?? "fil";
            var mime = cursor.GetString(mimeIndex);
            var display = $"{folder.DisplayPath}/{name}";
            DateTimeOffset? modified = cursor.IsNull(modifiedIndex)
                ? null
                : DateTimeOffset.FromUnixTimeMilliseconds(cursor.GetLong(modifiedIndex));
            if (mime == DocumentsContract.Document.MimeTypeDir)
            {
                var childFolder = new LocalFolderLocation(folder.TreeUri, id, name, display);
                files.Add(new LocalTransferFile(name, name, display, 0, null, childFolder, modified));
                continue;
            }
            var documentUri = DocumentsContract.BuildDocumentUriUsingTree(treeUri, id);
            var size = cursor.IsNull(sizeIndex) ? 0 : cursor.GetLong(sizeIndex);
            files.Add(new LocalTransferFile(name, name, display, size,
                () => Task.FromResult<Stream>(resolver.OpenInputStream(documentUri!) ??
                    throw new IOException($"Kan inte öppna {display}.")), ModifiedAt: modified));
        }
        return files.OrderByDescending(file => file.IsDirectory)
            .ThenBy(file => file.FileName, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static ContentResolver Resolver() =>
        (Platform.CurrentActivity?.ContentResolver) ??
        throw new InvalidOperationException("Androids dokumentlagring är inte tillgänglig.");

    private static string? QueryName(ContentResolver resolver, Uri uri)
    {
        using var cursor = resolver.Query(uri, [DocumentsContract.Document.ColumnDisplayName], null, null, null);
        return cursor?.MoveToFirst() == true ? cursor.GetString(0) : null;
    }
}
