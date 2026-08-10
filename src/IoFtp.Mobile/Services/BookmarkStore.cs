using System.Text.Json;

namespace IoFtp.Mobile.Services;

public sealed record SiteBookmark(Guid Id, Guid SiteId, string Name, string RemotePath);

public sealed class BookmarkStore
{
    private readonly string _path = Path.Combine(FileSystem.AppDataDirectory, "bookmarks.json");

    public async Task<IReadOnlyList<SiteBookmark>> LoadAsync()
    {
        if (!File.Exists(_path)) return [];
        try
        {
            await using var stream = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<List<SiteBookmark>>(stream) ?? [];
        }
        catch { return []; }
    }

    public async Task SaveAsync(IEnumerable<SiteBookmark> bookmarks)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        await using var stream = File.Create(_path);
        await JsonSerializer.SerializeAsync(stream, bookmarks.OrderBy(item => item.Name),
            new JsonSerializerOptions { WriteIndented = true });
    }
}
