using Android.Content;
using Android.Provider;
using Uri = Android.Net.Uri;

namespace IoFtp.Mobile.Services;

public sealed record LocalTransferFile(
    string FileName,
    string RelativePath,
    string DisplayPath,
    long Size,
    Func<Task<Stream>> OpenReadAsync);

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

    public static async Task<IReadOnlyList<LocalTransferFile>> PickFolderAsync()
    {
        var activity = Platform.CurrentActivity ??
            throw new InvalidOperationException("Android-aktiviteten är inte tillgänglig.");
        var completion = new TaskCompletionSource<Uri?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        MainActivity.FolderPickerCompletion = completion;
        var intent = new Intent(Intent.ActionOpenDocumentTree);
        intent.AddFlags(
            ActivityFlags.GrantReadUriPermission |
            ActivityFlags.GrantWriteUriPermission |
            ActivityFlags.GrantPersistableUriPermission |
            ActivityFlags.GrantPrefixUriPermission);
        activity.StartActivityForResult(intent, MainActivity.FolderPickerRequest);
        var treeUri = await completion.Task;
        if (treeUri is null) return [];

        var resolver = activity.ContentResolver ??
            throw new InvalidOperationException("Androids dokumentlagring är inte tillgänglig.");
        var rootId = DocumentsContract.GetTreeDocumentId(treeUri);
        var rootUri = DocumentsContract.BuildDocumentUriUsingTree(treeUri, rootId);
        var rootName = QueryName(resolver, rootUri!) ?? "Mapp";
        var files = new List<LocalTransferFile>();
        Walk(resolver, treeUri, rootId!, rootName, files);
        return files;
    }

    private static void Walk(
        ContentResolver resolver, Uri treeUri, string parentId, string relativeParent,
        List<LocalTransferFile> files)
    {
        var childrenUri = DocumentsContract.BuildChildDocumentsUriUsingTree(treeUri, parentId);
        var projection = new[]
        {
            DocumentsContract.Document.ColumnDocumentId,
            DocumentsContract.Document.ColumnDisplayName,
            DocumentsContract.Document.ColumnMimeType,
            DocumentsContract.Document.ColumnSize
        };
        using var cursor = resolver.Query(childrenUri!, projection, null, null, null);
        if (cursor is null) return;
        var idIndex = cursor.GetColumnIndex(projection[0]);
        var nameIndex = cursor.GetColumnIndex(projection[1]);
        var mimeIndex = cursor.GetColumnIndex(projection[2]);
        var sizeIndex = cursor.GetColumnIndex(projection[3]);
        while (cursor.MoveToNext())
        {
            var id = cursor.GetString(idIndex);
            var name = cursor.GetString(nameIndex) ?? "fil";
            var mime = cursor.GetString(mimeIndex);
            var relative = $"{relativeParent}/{name}";
            if (mime == DocumentsContract.Document.MimeTypeDir)
            {
                Walk(resolver, treeUri, id!, relative, files);
                continue;
            }
            var documentUri = DocumentsContract.BuildDocumentUriUsingTree(treeUri, id!);
            var size = cursor.IsNull(sizeIndex) ? 0 : cursor.GetLong(sizeIndex);
            files.Add(new LocalTransferFile(
                name, relative, relative, size,
                () => Task.FromResult<Stream>(
                    resolver.OpenInputStream(documentUri!) ??
                    throw new IOException($"Kan inte öppna {relative}."))));
        }
    }

    private static string? QueryName(ContentResolver resolver, Uri uri)
    {
        using var cursor = resolver.Query(
            uri, [DocumentsContract.Document.ColumnDisplayName], null, null, null);
        return cursor?.MoveToFirst() == true ? cursor.GetString(0) : null;
    }
}
