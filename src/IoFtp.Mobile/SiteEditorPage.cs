using IoFtp.Core.Models;
using ConnectionProfile = IoFtp.Core.Models.ConnectionProfile;

namespace IoFtp.Mobile;

public sealed class SiteEditorPage : ContentPage
{
    private readonly Guid _id;
    private readonly SiteOptions _originalOptions;
    private readonly Entry _name = new() { Placeholder = "Namn" };
    private readonly Picker _protocol = new() { Title = "Protokoll" };
    private readonly Entry _host = new() { Placeholder = "Server" };
    private readonly Entry _port = new() { Placeholder = "Port", Keyboard = Keyboard.Numeric };
    private readonly Entry _username = new() { Placeholder = "Användarnamn" };
    private readonly Entry _password = new() { Placeholder = "Lösenord", IsPassword = true };
    private readonly Entry _startPath = new() { Placeholder = "Startmapp", Text = "/" };
    private readonly Entry _hostKey = new() { Placeholder = "SSH host key (SHA256), valfritt" };
    private readonly Switch _invalidCertificate = new();
    private readonly Switch _brokenPasv = new();

    public event Func<ConnectionProfile, Task>? Saved;

    public SiteEditorPage(ConnectionProfile? profile)
    {
        _id = profile?.Id ?? Guid.NewGuid();
        _originalOptions = profile?.EffectiveOptions ?? new SiteOptions();
        Title = profile is null ? "Ny site" : "Redigera site";
        _protocol.ItemsSource = Enum.GetValues<TransferProtocol>().Select(TransferProtocolNames.Display).ToList();
        if (profile is not null)
        {
            _name.Text = profile.Name;
            _protocol.SelectedIndex = (int)profile.Protocol;
            _host.Text = profile.Host;
            _port.Text = profile.Port.ToString();
            _username.Text = profile.Username;
            _password.Text = profile.Password;
            _startPath.Text = profile.EffectiveOptions.BasePath;
            _hostKey.Text = profile.SshHostKeyFingerprint;
            _invalidCertificate.IsToggled = profile.AllowInvalidCertificate;
            _brokenPasv.IsToggled = profile.EffectiveOptions.BrokenPasv;
        }
        else
        {
            _protocol.SelectedIndex = (int)TransferProtocol.FtpsExplicit;
            _port.Text = "21";
        }
        _protocol.SelectedIndexChanged += (_, _) =>
        {
            _port.Text = ((TransferProtocol)Math.Max(0, _protocol.SelectedIndex)) switch
            {
                TransferProtocol.Sftp => "22",
                TransferProtocol.FtpsImplicit => "990",
                _ => "21"
            };
        };
        var save = new Button { Text = "Spara" };
        save.Clicked += OnSave;
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 18,
                Spacing = 12,
                Children =
                {
                    _name, _protocol, _host, _port, _username, _password, _startPath, _hostKey,
                    new HorizontalStackLayout
                    {
                        Children =
                        {
                            new Label
                            {
                                Text = "Acceptera ogiltigt certifikat/okänd SSH-nyckel",
                                VerticalOptions = LayoutOptions.Center
                            },
                            _invalidCertificate
                        }
                    },
                    new Label
                    {
                        Text = "Varning: använd endast ogiltiga certifikat på servrar du litar på.",
                        TextColor = Colors.OrangeRed,
                        FontSize = 12
                    },
                    new HorizontalStackLayout
                    {
                        Children =
                        {
                            new Label
                            {
                                Text = "Broken PASV (använd PORT/aktiv FTP direkt)",
                                VerticalOptions = LayoutOptions.Center
                            },
                            _brokenPasv
                        }
                    },
                    new Label
                    {
                        Text = "Använd endast för FTP/FTPS-servrar där EPSV/PASV inte fungerar. Inställningen påverkar inte SFTP.",
                        TextColor = Color.FromArgb("#91A2B1"),
                        FontSize = 12
                    },
                    save
                }
            }
        };
    }

    private async void OnSave(object? sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_name.Text) || string.IsNullOrWhiteSpace(_host.Text) ||
            !int.TryParse(_port.Text, out var port) || port is < 1 or > 65535)
        {
            await DisplayAlert("Site", "Ange namn, server och en giltig port.", "OK");
            return;
        }
        var protocol = (TransferProtocol)Math.Max(0, _protocol.SelectedIndex);
        var options = _originalOptions with
        {
            BasePath = string.IsNullOrWhiteSpace(_startPath.Text) ? "/" : _startPath.Text.Trim(),
            BrokenPasv = protocol != TransferProtocol.Sftp && _brokenPasv.IsToggled
        };
        var profile = new ConnectionProfile(
            _id, _name.Text.Trim(), _host.Text.Trim(), port, _username.Text?.Trim() ?? "",
            protocol, _password.Text ?? "", _invalidCertificate.IsToggled,
            Options: options,
            SshHostKeyFingerprint: _hostKey.Text?.Trim() ?? "");
        if (Saved is not null) await Saved(profile);
    }
}
