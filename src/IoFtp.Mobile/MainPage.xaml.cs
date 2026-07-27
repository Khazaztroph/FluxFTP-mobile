using System.Collections.ObjectModel;
using System.IO.Compression;
using IoFtp.Core.Models;
using IoFtp.Mobile.Services;
using IoFtp.Core.Transport;
using ConnectionProfile = IoFtp.Core.Models.ConnectionProfile;

namespace IoFtp.Mobile;

public partial class MainPage : ContentPage
{
    private readonly SiteStore _sites;
    private readonly RemoteBrowserService _remote;
    private readonly ObservableCollection<LocalTransferFile> _localFiles = [];
    private readonly ObservableCollection<RemoteEntryView> _remoteFiles = [];
    private CancellationTokenSource? _operation;

    public MainPage()
    {
        InitializeComponent();
        _sites = new SiteStore();
        _remote = new RemoteBrowserService();
        LocalFiles.ItemsSource = _localFiles;
        RemoteFiles.ItemsSource = _remoteFiles;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        var selectedId = (SitePicker.SelectedItem as ConnectionProfile)?.Id;
        var profiles = (await _sites.LoadAsync()).ToList();
        SitePicker.ItemsSource = profiles;
        SitePicker.SelectedItem = profiles.FirstOrDefault(x => x.Id == selectedId) ?? profiles.FirstOrDefault();
    }

    private async void OnManageSites(object sender, EventArgs e) =>
        await Navigation.PushAsync(new SiteManagerPage(_sites));

    private void OnSiteSelected(object sender, EventArgs e) =>
        ConnectButton.IsEnabled = SitePicker.SelectedItem is ConnectionProfile;

    private async void OnConnect(object sender, EventArgs e)
    {
        if (SitePicker.SelectedItem is not ConnectionProfile profile) return;
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
            await RefreshRemoteAsync(token);
            ConnectButton.Text = "Återanslut";
        }, $"Ansluten till {profile.Name}");
    }

    private async void OnRemotePathCompleted(object sender, EventArgs e) =>
        await RunAsync(RefreshRemoteAsync);

    private async void OnRemoteUp(object sender, EventArgs e)
    {
        RemotePath.Text = ParentPath(RemotePath.Text);
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
            var files = await AndroidFolderPicker.PickFolderAsync();
            foreach (var file in files)
                if (_localFiles.All(x => x.DisplayPath != file.DisplayPath)) _localFiles.Add(file);
        }
        catch (Exception exception)
        {
            await DisplayAlert("Mappväljare", exception.Message, "OK");
        }
    }

    private async void OnUpload(object sender, EventArgs e)
    {
        var files = LocalFiles.SelectedItems.Cast<LocalTransferFile>().ToList();
        if (files.Count == 0)
        {
            await DisplayAlert("Uppladdning", "Markera en eller flera filer/mappar.", "OK");
            return;
        }
        await RunAsync(async token =>
        {
            var createdDirectories = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in files)
            {
                var destination = CombineRemote(RemotePath.Text, file.RelativePath);
                await EnsureRemoteDirectoriesAsync(RemoteParent(destination), createdDirectories, token);
                await using var source = await file.OpenReadAsync();
                await _remote.UploadAsync(destination, source, Progress(file.Size), token);
            }
            await RefreshRemoteAsync(token);
            LocalFiles.SelectedItems.Clear();
        }, $"{files.Count} fil(er) uppladdade");
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
                    await _remote.DownloadAsync(file.FullPath, destination, Progress(file.Size ?? 0), token);
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
        foreach (var entry in entries.OrderByDescending(x => x.IsDirectory).ThenBy(x => x.Name))
            _remoteFiles.Add(new RemoteEntryView(entry));
    }

    private IProgress<long> Progress(long total) => new Progress<long>(bytes =>
        TransferProgress.Progress = total > 0 ? Math.Clamp((double)bytes / total, 0, 1) : 0);

    private async Task RunAsync(Func<CancellationToken, Task> action, string? success = null)
    {
        _operation?.Cancel();
        _operation = new CancellationTokenSource();
        TransferProgress.Progress = 0;
        try
        {
            await action(_operation.Token);
            if (success is not null) await DisplayAlert("FluxFTP", success, "OK");
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { await DisplayAlert("FluxFTP", exception.Message, "OK"); }
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
            await _remote.DownloadAsync(entry.FullPath, destination, Progress(entry.Size ?? 0), token);
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
}

public sealed class RemoteEntryView(IoFtp.Core.Abstractions.RemoteEntry entry)
{
    public string Name { get; } = entry.Name;
    public string FullPath { get; } = entry.FullPath;
    public bool IsDirectory { get; } = entry.IsDirectory;
    public long? Size { get; } = entry.Size;
    public string Icon => IsDirectory ? "📁" : "📄";
    public string SizeText => IsDirectory ? "Mapp" : Size is long value ? $"{value:N0} byte" : "Fil";
}
