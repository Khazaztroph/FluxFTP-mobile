using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO.Compression;
using IoFtp.Core.Models;
using IoFtp.Mobile.Services;
using IoFtp.Core.Transport;
using ConnectionProfile = IoFtp.Core.Models.ConnectionProfile;

namespace IoFtp.Mobile;

public partial class MainPage : ContentPage
{
    private const string SelectedSitePreference = "fluxftp.selected-site.v1";
    private const string DualViewPreference = "fluxftp.dual-view.v1";
    private const string LocalRootPreference = "fluxftp.local-root.v1";
    private readonly SiteStore _sites;
    private readonly BookmarkStore _bookmarks = new();
    private readonly RemoteBrowserService _remote;
    private readonly ObservableCollection<LocalTransferFile> _localFiles = [];
    private readonly ObservableCollection<RemoteEntryView> _remoteFiles = [];
    private IReadOnlyList<ConnectionProfile> _profiles = [];
    private ConnectionProfile? _selectedProfile;
    private CancellationTokenSource? _operation;
    private bool _dualView;
    private bool _connected;
    private readonly List<LocalFolderLocation> _localHistory = [];
    private int _localHistoryIndex = -1;
    private bool _openingLocalFolder;
    private readonly List<string> _remoteHistory = [];
    private int _remoteHistoryIndex = -1;
    private BrowseSort _localSort = BrowseSort.Name;
    private BrowseSort _remoteSort = BrowseSort.Name;
    private bool _localSortDescending;
    private bool _remoteSortDescending;
    private bool _localFoldersFirst = true;
    private bool _remoteFoldersFirst = true;
    private string _connectionSecurity = "";
    private readonly Stopwatch _transferTimer = new();
    private long _transferStartBytes;
    private long _lastSampleBytes;
    private TimeSpan _lastSampleAt;
    private double _smoothedBytesPerSecond;

    public MainPage()
    {
        InitializeComponent();
        _sites = new SiteStore();
        _remote = new RemoteBrowserService();
        LocalFiles.ItemsSource = _localFiles;
        RemoteFiles.ItemsSource = _remoteFiles;
        _dualView = Preferences.Default.Get(DualViewPreference, false);
        _localSort = (BrowseSort)Preferences.Default.Get("fluxftp.local-sort.v1", 0);
        _remoteSort = (BrowseSort)Preferences.Default.Get("fluxftp.remote-sort.v1", 0);
        _localSortDescending = Preferences.Default.Get("fluxftp.local-sort-desc.v1", false);
        _remoteSortDescending = Preferences.Default.Get("fluxftp.remote-sort-desc.v1", false);
        _localFoldersFirst = Preferences.Default.Get("fluxftp.local-folders-first.v1", true);
        _remoteFoldersFirst = Preferences.Default.Get("fluxftp.remote-folders-first.v1", true);
        ApplyViewMode();
        _ = RestoreLocalFolderAsync();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        var selectedId = _selectedProfile?.Id;
        if (selectedId is null &&
            Guid.TryParse(Preferences.Default.Get(SelectedSitePreference, ""), out var storedId))
            selectedId = storedId;
        _profiles = await _sites.LoadAsync();
        _selectedProfile = _profiles.FirstOrDefault(site => site.Id == selectedId) ??
            _profiles.FirstOrDefault();
        UpdateSelectedSite();
    }

    private async void OnChooseSite(object sender, EventArgs e)
    {
        _profiles = await _sites.LoadAsync();
        var manage = "Hantera sites…";
        if (_profiles.Count == 0)
        {
            await Navigation.PushAsync(new SiteManagerPage(_sites));
            return;
        }
        var choices = _profiles.Select(site => site.Name).Append(manage).ToArray();
        var selected = await DisplayActionSheet("Välj site", "Avbryt", null, choices);
        if (selected == manage)
        {
            await Navigation.PushAsync(new SiteManagerPage(_sites));
            return;
        }
        var profile = _profiles.FirstOrDefault(site =>
            site.Name.Equals(selected, StringComparison.Ordinal));
        if (profile is null) return;
        _selectedProfile = profile;
        Preferences.Default.Set(SelectedSitePreference, profile.Id.ToString());
        UpdateSelectedSite();
    }

    private void OnToggleViewMode(object sender, EventArgs e)
    {
        _dualView = !_dualView;
        Preferences.Default.Set(DualViewPreference, _dualView);
        ApplyViewMode();
    }

    private void ApplyViewMode()
    {
        if (PaneGrid is null) return;
        LocalPane.IsVisible = _dualView;
        ApplyResponsivePaneLayout();
        ViewModeButton.Text = _dualView ? "Singelvy" : "Dualvy";
        ViewModeLabel.Text = _dualView ? "DUAL" : "SINGEL";
    }

    private void OnPageSizeChanged(object? sender, EventArgs e) => ApplyResponsivePaneLayout();

    private void ApplyResponsivePaneLayout()
    {
        if (PaneGrid is null || LocalPane is null || RemotePane is null) return;
        var portrait = Width > 0 && Height > 0 && Width < Height;
        PaneGrid.ColumnDefinitions.Clear();
        PaneGrid.RowDefinitions.Clear();
        if (_dualView && portrait)
        {
            PaneGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            PaneGrid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
            PaneGrid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
            Grid.SetColumn(LocalPane, 0);
            Grid.SetRow(LocalPane, 0);
            Grid.SetColumn(RemotePane, 0);
            Grid.SetRow(RemotePane, 1);
            Grid.SetColumnSpan(RemotePane, 1);
        }
        else
        {
            PaneGrid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
            PaneGrid.ColumnDefinitions.Add(new ColumnDefinition(
                _dualView ? GridLength.Star : new GridLength(0)));
            PaneGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            Grid.SetColumn(LocalPane, 0);
            Grid.SetRow(LocalPane, 0);
            Grid.SetColumn(RemotePane, _dualView ? 1 : 0);
            Grid.SetRow(RemotePane, 0);
            Grid.SetColumnSpan(RemotePane, _dualView ? 1 : 2);
        }
    }

    private void UpdateSelectedSite()
    {
        SelectedSiteLabel.Text = _selectedProfile?.Name ?? "Ingen site vald";
        ConnectButton.IsEnabled = _selectedProfile is not null;
    }

    private async void OnConnect(object sender, EventArgs e)
    {
        if (_selectedProfile is not { } profile) return;
        await RunAsync(async token =>
        {
            var connectedProfile = profile;
            try
            {
                await _remote.ConnectAsync(connectedProfile, token);
            }
            catch (SshHostKeyException exception)
            {
                var trust = await DisplayAlert(
                    "Okänd SSH-server",
                    $"Kontrollera serverns fingeravtryck:\n\n{exception.Fingerprint}\n\nLita på denna nyckel?",
                    "Lita på",
                    "Avbryt");
                if (!trust) return;
                connectedProfile = profile with { SshHostKeyFingerprint = exception.Fingerprint };
                var all = (await _sites.LoadAsync())
                    .Where(site => site.Id != connectedProfile.Id)
                    .Append(connectedProfile);
                await _sites.SaveAsync(all);
                await _remote.ConnectAsync(connectedProfile, token);
            }
            RemotePath.Text = connectedProfile.EffectiveOptions.BasePath;
            _remoteHistory.Clear();
            _remoteHistoryIndex = -1;
            AddRemoteHistory(RemotePath.Text);
            await RefreshRemoteAsync(token);
            ConnectButton.Text = "Återanslut";
            _connected = true;
            _connectionSecurity = _remote.ConnectionSecurity;
        }, $"Ansluten till {profile.Name}");
    }

    private async void OnRemotePathCompleted(object sender, EventArgs e)
    {
        AddRemoteHistory(RemotePath.Text);
        await RunAsync(RefreshRemoteAsync);
    }

    private async void OnRemoteUp(object sender, EventArgs e)
    {
        RemotePath.Text = ParentPath(RemotePath.Text);
        AddRemoteHistory(RemotePath.Text);
        await RunAsync(RefreshRemoteAsync);
    }

    private async void OnOpenRemote(object sender, EventArgs e)
    {
        if (RemoteFiles.SelectedItems.FirstOrDefault() is not RemoteEntryView { IsDirectory: true } entry)
        {
            await DisplayAlert("Fjärrmapp", "Markera en mapp att öppna.", "OK");
            return;
        }
        RemoteFiles.SelectedItems.Clear();
        RemotePath.Text = entry.FullPath;
        AddRemoteHistory(RemotePath.Text);
        await RunAsync(RefreshRemoteAsync);
    }

    private async void OnRemoteBack(object sender, EventArgs e)
    {
        if (_remoteHistoryIndex <= 0) return;
        RemotePath.Text = _remoteHistory[--_remoteHistoryIndex];
        await RunAsync(RefreshRemoteAsync);
    }

    private async void OnRemoteForward(object sender, EventArgs e)
    {
        if (_remoteHistoryIndex >= _remoteHistory.Count - 1) return;
        RemotePath.Text = _remoteHistory[++_remoteHistoryIndex];
        await RunAsync(RefreshRemoteAsync);
    }

    private void AddRemoteHistory(string? path)
    {
        var normalized = NormalizePath(path);
        if (_remoteHistoryIndex >= 0 && _remoteHistory[_remoteHistoryIndex] == normalized) return;
        if (_remoteHistoryIndex < _remoteHistory.Count - 1)
            _remoteHistory.RemoveRange(_remoteHistoryIndex + 1, _remoteHistory.Count - _remoteHistoryIndex - 1);
        _remoteHistory.Add(normalized);
        if (_remoteHistory.Count > 20) _remoteHistory.RemoveAt(0);
        _remoteHistoryIndex = _remoteHistory.Count - 1;
    }

    private async void OnSortLocal(object sender, EventArgs e)
    {
        var choice = await DisplayActionSheet("Sortera lokalt", "Avbryt", null,
            "Namn", "Storlek", "Ändrad", "Mappar först", "Filer först", "Vänd riktning");
        if (choice == "Namn") _localSort = BrowseSort.Name;
        else if (choice == "Storlek") _localSort = BrowseSort.Size;
        else if (choice == "Ändrad") _localSort = BrowseSort.Modified;
        else if (choice == "Mappar först") _localFoldersFirst = true;
        else if (choice == "Filer först") _localFoldersFirst = false;
        else if (choice == "Vänd riktning") _localSortDescending = !_localSortDescending;
        else return;
        Preferences.Default.Set("fluxftp.local-sort.v1", (int)_localSort);
        Preferences.Default.Set("fluxftp.local-sort-desc.v1", _localSortDescending);
        Preferences.Default.Set("fluxftp.local-folders-first.v1", _localFoldersFirst);
        ApplyLocalSort();
    }

    private async void OnSortRemote(object sender, EventArgs e)
    {
        var choice = await DisplayActionSheet("Sortera server", "Avbryt", null,
            "Namn", "Storlek", "Ändrad", "Mappar först", "Filer först", "Vänd riktning");
        if (choice == "Namn") _remoteSort = BrowseSort.Name;
        else if (choice == "Storlek") _remoteSort = BrowseSort.Size;
        else if (choice == "Ändrad") _remoteSort = BrowseSort.Modified;
        else if (choice == "Mappar först") _remoteFoldersFirst = true;
        else if (choice == "Filer först") _remoteFoldersFirst = false;
        else if (choice == "Vänd riktning") _remoteSortDescending = !_remoteSortDescending;
        else return;
        Preferences.Default.Set("fluxftp.remote-sort.v1", (int)_remoteSort);
        Preferences.Default.Set("fluxftp.remote-sort-desc.v1", _remoteSortDescending);
        Preferences.Default.Set("fluxftp.remote-folders-first.v1", _remoteFoldersFirst);
        ApplyRemoteSort();
    }

    private async void OnBookmarks(object sender, EventArgs e)
    {
        if (_selectedProfile is not { } profile) return;
        var all = (await _bookmarks.LoadAsync()).ToList();
        var siteBookmarks = all.Where(item => item.SiteId == profile.Id).OrderBy(item => item.Name).ToList();
        var add = "Spara aktuell mapp…";
        var remove = "Ta bort bokmärke…";
        var choices = new List<string> { add };
        choices.AddRange(siteBookmarks.Select(item => $"★ {item.Name}"));
        if (siteBookmarks.Count > 0) choices.Add(remove);
        var selected = await DisplayActionSheet("Bokmärken", "Avbryt", null, choices.ToArray());
        if (selected == add)
        {
            var name = await DisplayPromptAsync("Nytt bokmärke", "Namn", initialValue: RemotePath.Text.Trim('/'));
            if (string.IsNullOrWhiteSpace(name)) return;
            all.Add(new SiteBookmark(Guid.NewGuid(), profile.Id, name.Trim(), NormalizePath(RemotePath.Text)));
            await _bookmarks.SaveAsync(all);
            return;
        }
        if (selected == remove)
        {
            var deleteName = await DisplayActionSheet("Ta bort bokmärke", "Avbryt", null,
                siteBookmarks.Select(item => item.Name).ToArray());
            var doomed = siteBookmarks.FirstOrDefault(item => item.Name == deleteName);
            if (doomed is not null) { all.Remove(doomed); await _bookmarks.SaveAsync(all); }
            return;
        }
        var bookmark = siteBookmarks.FirstOrDefault(item => selected == $"★ {item.Name}");
        if (bookmark is null) return;
        RemotePath.Text = bookmark.RemotePath;
        AddRemoteHistory(RemotePath.Text);
        await RunAsync(RefreshRemoteAsync);
    }

    private async void OnPickFiles(object sender, EventArgs e)
    {
        try
        {
            var files = await AndroidFolderPicker.PickFilesAsync();
            foreach (var file in files)
                if (_localFiles.All(x => x.DisplayPath != file.DisplayPath)) _localFiles.Add(file);
        }
        catch (Exception exception)
        {
            await DisplayAlert("Filväljare", exception.Message, "OK");
        }
    }

    private async void OnPickFolder(object sender, EventArgs e)
    {
        try
        {
            var folder = await AndroidFolderPicker.PickFolderAsync();
            if (folder is null) return;
            Preferences.Default.Set(LocalRootPreference,
                System.Text.Json.JsonSerializer.Serialize(folder));
            _localHistory.Clear();
            _localHistoryIndex = -1;
            await NavigateLocalAsync(folder, true);
        }
        catch (Exception exception)
        {
            await DisplayAlert("Mappväljare", exception.Message, "OK");
        }
    }

    private async void OnLocalSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_openingLocalFolder || e.CurrentSelection.Count != 1 ||
            e.CurrentSelection[0] is not LocalTransferFile { Folder: { } folder }) return;
        _openingLocalFolder = true;
        try
        {
            LocalFiles.SelectedItems.Clear();
            await NavigateLocalAsync(folder, true);
        }
        catch (Exception exception) { await DisplayAlert("Lokal mapp", exception.Message, "OK"); }
        finally { _openingLocalFolder = false; }
    }

    private async void OnLocalBack(object sender, EventArgs e)
    {
        if (_localHistoryIndex <= 0) return;
        _localHistoryIndex--;
        await TryNavigateLocalAsync(_localHistory[_localHistoryIndex]);
    }

    private async void OnLocalForward(object sender, EventArgs e)
    {
        if (_localHistoryIndex >= _localHistory.Count - 1) return;
        _localHistoryIndex++;
        await TryNavigateLocalAsync(_localHistory[_localHistoryIndex]);
    }

    private async void OnLocalUp(object sender, EventArgs e)
    {
        if (_localHistoryIndex <= 0) return;
        var current = _localHistory[_localHistoryIndex];
        for (var index = _localHistoryIndex - 1; index >= 0; index--)
        {
            var candidate = _localHistory[index];
            if (current.DisplayPath.StartsWith(candidate.DisplayPath + "/", StringComparison.Ordinal))
            {
                _localHistoryIndex = index;
                await TryNavigateLocalAsync(candidate);
                return;
            }
        }
    }

    private async void OnLocalRefresh(object sender, EventArgs e)
    {
        if (_localHistoryIndex >= 0)
            await TryNavigateLocalAsync(_localHistory[_localHistoryIndex]);
    }

    private async Task TryNavigateLocalAsync(LocalFolderLocation folder)
    {
        try { await NavigateLocalAsync(folder, false); }
        catch (Exception exception) { await DisplayAlert("Lokal mapp", exception.Message, "OK"); }
    }

    private async Task NavigateLocalAsync(LocalFolderLocation folder, bool addToHistory)
    {
        var entries = await AndroidFolderPicker.ListFolderAsync(folder);
        if (addToHistory)
        {
            if (_localHistoryIndex < _localHistory.Count - 1)
                _localHistory.RemoveRange(_localHistoryIndex + 1, _localHistory.Count - _localHistoryIndex - 1);
            _localHistory.Add(folder);
            _localHistoryIndex = _localHistory.Count - 1;
        }
        _localFiles.Clear();
        foreach (var entry in entries) _localFiles.Add(entry);
        ApplyLocalSort();
        LocalPathLabel.Text = folder.DisplayPath;
    }

    private async Task RestoreLocalFolderAsync()
    {
        try
        {
            var json = Preferences.Default.Get(LocalRootPreference, "");
            if (string.IsNullOrWhiteSpace(json)) return;
            var folder = System.Text.Json.JsonSerializer.Deserialize<LocalFolderLocation>(json);
            if (folder is not null) await NavigateLocalAsync(folder, true);
        }
        catch
        {
            Preferences.Default.Remove(LocalRootPreference);
            LocalPathLabel.Text = "Välj startmapp igen";
        }
    }

    private async void OnUpload(object sender, EventArgs e)
    {
        var selected = LocalFiles.SelectedItems.Cast<LocalTransferFile>().ToList();
        if (selected.Count == 0)
        {
            await DisplayAlert("Uppladdning", "Markera en eller flera filer/mappar.", "OK");
            return;
        }
        await RunAsync(async token =>
        {
            var files = new List<LocalTransferFile>();
            foreach (var entry in selected)
                files.AddRange(await AndroidFolderPicker.ExpandAsync(entry, token));
            var createdDirectories = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in files)
            {
                var destination = CombineRemote(RemotePath.Text, file.RelativePath);
                await EnsureRemoteDirectoriesAsync(RemoteParent(destination), createdDirectories, token);
                await UploadWithRecoveryAsync(file, destination, token);
            }
            await RefreshRemoteAsync(token);
            LocalFiles.SelectedItems.Clear();
        }, $"{selected.Count} objekt uppladdade");
    }

    private async void OnDownload(object sender, EventArgs e)
    {
        var entries = RemoteFiles.SelectedItems.Cast<RemoteEntryView>().ToList();
        if (entries.Count == 0)
        {
            await DisplayAlert("Nedladdning", "Markera en eller flera filer/mappar.", "OK");
            return;
        }
        await RunAsync(async token =>
        {
            if (entries.Count == 1 && !entries[0].IsDirectory)
            {
                var file = entries[0];
                var output = Path.Combine(FileSystem.CacheDirectory, file.Name);
                await using (var destination = File.Create(output))
                    await _remote.DownloadAsync(file.FullPath, destination,
                        Progress(file.Size ?? 0, $"Laddar ner {file.Name}"), token);
                await Share.Default.RequestAsync(new ShareFileRequest(
                    "Spara eller dela den nedladdade filen", new ShareFile(output)));
                return;
            }
            var batchRoot = Path.Combine(FileSystem.CacheDirectory, $"FluxFTP-{DateTime.Now:yyyyMMdd-HHmmss}");
            Directory.CreateDirectory(batchRoot);
            foreach (var entry in entries)
                await DownloadEntryAsync(entry, batchRoot, token);
            var archive = batchRoot + ".zip";
            ZipFile.CreateFromDirectory(batchRoot, archive, CompressionLevel.Fastest, false);
            await Share.Default.RequestAsync(new ShareFileRequest(
                "Spara eller dela de nedladdade filerna", new ShareFile(archive)));
        }, $"{entries.Count} objekt nedladdade");
    }

    private async Task RefreshRemoteAsync(CancellationToken token)
    {
        var entries = await _remote.ListAsync(NormalizePath(RemotePath.Text), token);
        _remoteFiles.Clear();
        foreach (var entry in entries)
            _remoteFiles.Add(new RemoteEntryView(entry));
        ApplyRemoteSort();
    }

    private void ApplyLocalSort()
    {
        IEnumerable<LocalTransferFile> SortGroup(IEnumerable<LocalTransferFile> group) =>
            (_localSort, _localSortDescending) switch
        {
            (BrowseSort.Size, true) => group.OrderByDescending(item => item.Size),
            (BrowseSort.Size, false) => group.OrderBy(item => item.Size),
            (BrowseSort.Modified, true) => group.OrderByDescending(item => item.ModifiedAt),
            (BrowseSort.Modified, false) => group.OrderBy(item => item.ModifiedAt),
            (_, true) => group.OrderByDescending(item => item.FileName, StringComparer.CurrentCultureIgnoreCase),
            _ => group.OrderBy(item => item.FileName, StringComparer.CurrentCultureIgnoreCase)
        };
        var folders = SortGroup(_localFiles.Where(item => item.IsDirectory));
        var files = SortGroup(_localFiles.Where(item => !item.IsDirectory));
        var ordered = _localFoldersFirst ? folders.Concat(files) : files.Concat(folders);
        Replace(_localFiles, ordered.ToArray());
    }

    private void ApplyRemoteSort()
    {
        IEnumerable<RemoteEntryView> SortGroup(IEnumerable<RemoteEntryView> group) =>
            (_remoteSort, _remoteSortDescending) switch
        {
            (BrowseSort.Size, true) => group.OrderByDescending(item => item.Size ?? 0),
            (BrowseSort.Size, false) => group.OrderBy(item => item.Size ?? 0),
            (BrowseSort.Modified, true) => group.OrderByDescending(item => item.ModifiedAt),
            (BrowseSort.Modified, false) => group.OrderBy(item => item.ModifiedAt),
            (_, true) => group.OrderByDescending(item => item.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => group.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
        };
        var folders = SortGroup(_remoteFiles.Where(item => item.IsDirectory));
        var files = SortGroup(_remoteFiles.Where(item => !item.IsDirectory));
        var ordered = _remoteFoldersFirst ? folders.Concat(files) : files.Concat(folders);
        Replace(_remoteFiles, ordered.ToArray());
    }

    private static void Replace<T>(ObservableCollection<T> target, IReadOnlyList<T> values)
    {
        target.Clear();
        foreach (var value in values) target.Add(value);
    }

    private IProgress<long> Progress(long total, string activity)
    {
        BeginTransfer(0, activity);
        return new Progress<long>(bytes => ReportTransferProgress(bytes, total, activity));
    }

    private async Task UploadWithRecoveryAsync(
        LocalTransferFile file, string destination, CancellationToken token)
    {
        const int maximumAttempts = 3;
        long offset = 0;
        var activity = $"Laddar upp {file.FileName}";

        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            long bytesReported = offset;
            var progress = new InlineProgress<long>(bytes =>
            {
                bytesReported = bytes;
                ReportTransferProgress(bytes, file.Size, activity);
            });

            try
            {
                await using var source = await (file.OpenReadAsync?.Invoke() ??
                    throw new IOException($"Kan inte öppna {file.DisplayPath}."));
                if (offset > 0)
                {
                    if (!source.CanSeek)
                    {
                        offset = 0;
                    }
                    else
                    {
                        source.Seek(offset, SeekOrigin.Begin);
                    }
                }
                BeginTransfer(offset, activity);
                await _remote.UploadAsync(destination, source, offset, progress, token);
                return;
            }
            catch (Exception exception) when (
                attempt < maximumAttempts &&
                exception is IOException or FtpCommandException)
            {
                await _remote.ReconnectAsync(token);
                var remoteSize = await _remote.GetSizeAsync(destination, token);

                // SIZE alone is not enough: ioFTPD can preallocate the complete
                // file before all bytes arrive. Only accept it as completed when
                // this attempt actually reported that every source byte was sent.
                if (remoteSize == file.Size && bytesReported >= file.Size)
                    return;

                offset = remoteSize is > 0 && remoteSize < file.Size
                    ? remoteSize.Value
                    : 0;
            }
        }
    }

    private async Task RunAsync(Func<CancellationToken, Task> action, string? success = null)
    {
        _operation?.Cancel();
        _operation = new CancellationTokenSource();
        TransferProgress.Progress = 0;
        SpeedText.Text = "0 B/s";
        StatusText.Text = "Arbetar…";
        StatusActivity.IsRunning = true;
        try
        {
            await action(_operation.Token);
            if (success is not null) await DisplayAlert("FluxFTP", success, "OK");
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { await DisplayAlert("FluxFTP", exception.Message, "OK"); }
        finally
        {
            StatusActivity.IsRunning = false;
            StatusText.Text = _connected
                ? $"Ansluten • {_selectedProfile?.Name} • {_connectionSecurity}"
                : "Inte ansluten";
            if (!_connected) TimingText.Text = "Förfluten: —  •  Kvar: —";
        }
    }

    private static string NormalizePath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? "/" : path.StartsWith('/') ? path : "/" + path;
    private static string ParentPath(string? path)
    {
        var normalized = NormalizePath(path).TrimEnd('/');
        var separator = normalized.LastIndexOf('/');
        return separator <= 0 ? "/" : normalized[..separator];
    }
    private static string CombineRemote(string? path, string name) =>
        NormalizePath(path).TrimEnd('/') + "/" + name.Replace('\\', '/').TrimStart('/');

    private async Task EnsureRemoteDirectoriesAsync(
        string path, HashSet<string> created, CancellationToken token)
    {
        var current = "";
        foreach (var segment in NormalizePath(path).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current += "/" + segment;
            if (created.Add(current)) await _remote.CreateDirectoryAsync(current, token);
        }
    }

    private async Task DownloadEntryAsync(
        RemoteEntryView entry, string localParent, CancellationToken token)
    {
        var safeName = string.Concat(entry.Name.Select(ch =>
            Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        var localPath = Path.Combine(localParent, safeName);
        if (!entry.IsDirectory)
        {
            await using var destination = File.Create(localPath);
            await _remote.DownloadAsync(entry.FullPath, destination,
                Progress(entry.Size ?? 0, $"Laddar ner {entry.Name}"), token);
            return;
        }
        Directory.CreateDirectory(localPath);
        foreach (var child in await _remote.ListAsync(entry.FullPath, token))
            await DownloadEntryAsync(new RemoteEntryView(child), localPath, token);
    }

    private static string RemoteParent(string path)
    {
        var normalized = NormalizePath(path).TrimEnd('/');
        var separator = normalized.LastIndexOf('/');
        return separator <= 0 ? "/" : normalized[..separator];
    }

    private void BeginTransfer(long startBytes, string activity)
    {
        _transferStartBytes = startBytes;
        _lastSampleBytes = startBytes;
        _lastSampleAt = TimeSpan.Zero;
        _smoothedBytesPerSecond = 0;
        _transferTimer.Restart();
        MainThread.BeginInvokeOnMainThread(() =>
        {
            StatusText.Text = activity;
            SpeedText.Text = "0 B/s";
            TransferProgress.Progress = 0;
            TimingText.Text = "Förfluten: 00:00  •  Kvar: —";
        });
    }

    private void ReportTransferProgress(long bytes, long total, string activity)
    {
        var elapsed = _transferTimer.Elapsed;
        var sampleSeconds = (elapsed - _lastSampleAt).TotalSeconds;
        if (sampleSeconds >= 0.2)
        {
            var instantSpeed = Math.Max(0, (bytes - _lastSampleBytes) / sampleSeconds);
            _smoothedBytesPerSecond = _smoothedBytesPerSecond <= 0
                ? instantSpeed
                : (_smoothedBytesPerSecond * 0.7) + (instantSpeed * 0.3);
            _lastSampleBytes = bytes;
            _lastSampleAt = elapsed;
        }
        var speed = Math.Max(0, (long)_smoothedBytesPerSecond);
        var fraction = total > 0 ? Math.Clamp((double)bytes / total, 0, 1) : 0;
        var detail = total > 0
            ? $"{activity} • {FormatBytes(bytes)} / {FormatBytes(total)} • {fraction:P0}"
            : $"{activity} • {FormatBytes(bytes)}";
        MainThread.BeginInvokeOnMainThread(() =>
        {
            TransferProgress.Progress = fraction;
            StatusText.Text = detail;
            SpeedText.Text = $"{FormatBytes(speed)}/s";
            var remainingBytes = Math.Max(0, total - bytes);
            var remaining = speed > 0 && total > 0
                ? FormatDuration(TimeSpan.FromSeconds(remainingBytes / (double)speed))
                : "—";
            TimingText.Text = $"Förfluten: {FormatDuration(elapsed)}  •  Kvar: {remaining}";
        });
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = Math.Max(0, (double)bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.0} {units[unit]}";
    }

    private static string FormatDuration(TimeSpan value)
    {
        if (value < TimeSpan.Zero || !double.IsFinite(value.TotalSeconds)) return "—";
        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{value.Minutes:00}:{value.Seconds:00}";
    }
}

public sealed class RemoteEntryView(IoFtp.Core.Abstractions.RemoteEntry entry)
{
    public string Name { get; } = entry.Name;
    public string FullPath { get; } = entry.FullPath;
    public bool IsDirectory { get; } = entry.IsDirectory;
    public long? Size { get; } = entry.Size;
    public DateTimeOffset? ModifiedAt { get; } = entry.ModifiedAt;
    public string Icon => IsDirectory ? "📁" : "📄";
    public string SizeText => IsDirectory ? "Mapp" : Size is long value ? $"{value:N0} byte" : "Fil";
    public string ModifiedText => ModifiedAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm") ?? "";
}

internal enum BrowseSort { Name, Size, Modified }

internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
