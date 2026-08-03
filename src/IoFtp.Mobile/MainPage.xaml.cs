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
    private readonly SiteStore _sites;
    private readonly RemoteBrowserService _remote;
    private readonly ObservableCollection<LocalTransferFile> _localFiles = [];
    private readonly ObservableCollection<RemoteEntryView> _remoteFiles = [];
    private IReadOnlyList<ConnectionProfile> _profiles = [];
    private ConnectionProfile? _selectedProfile;
    private CancellationTokenSource? _operation;
    private bool _dualView;
    private bool _connected;
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
        ApplyViewMode();
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
        PaneGrid.ColumnDefinitions[0].Width = _dualView
            ? new GridLength(1, GridUnitType.Star)
            : new GridLength(0);
        PaneGrid.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);
        Grid.SetColumn(RemotePane, _dualView ? 1 : 0);
        Grid.SetColumnSpan(RemotePane, _dualView ? 1 : 2);
        ViewModeButton.Text = _dualView ? "Singelvy" : "Dualvy";
        ViewModeLabel.Text = _dualView ? "DUAL" : "SINGEL";
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
            await RefreshRemoteAsync(token);
            ConnectButton.Text = "Återanslut";
            _connected = true;
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
                await UploadWithRecoveryAsync(file, destination, token);
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
        foreach (var entry in entries.OrderByDescending(x => x.IsDirectory).ThenBy(x => x.Name))
            _remoteFiles.Add(new RemoteEntryView(entry));
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
                await using var source = await file.OpenReadAsync();
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
                ? $"Ansluten • {_selectedProfile?.Name}"
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
    public string Icon => IsDirectory ? "📁" : "📄";
    public string SizeText => IsDirectory ? "Mapp" : Size is long value ? $"{value:N0} byte" : "Fil";
}

internal sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
