using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace IoFtp.Mobile;

[Activity(
    Label = "FluxFTP Mobile",
    Icon = "@mipmap/appicon",
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    Exported = true,
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation |
        ConfigChanges.UiMode | ConfigChanges.ScreenLayout |
        ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    internal const int FolderPickerRequest = 4201;
    internal static TaskCompletionSource<Android.Net.Uri?>? FolderPickerCompletion { get; set; }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode != FolderPickerRequest) return;
        var uri = resultCode == Result.Ok ? data?.Data : null;
        if (uri is not null && data is not null)
        {
            var flags = data.Flags &
                (ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission);
            ContentResolver?.TakePersistableUriPermission(uri, flags);
        }
        FolderPickerCompletion?.TrySetResult(uri);
        FolderPickerCompletion = null;
    }
}
