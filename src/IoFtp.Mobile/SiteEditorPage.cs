using IoFtp.Core.Models;
using ConnectionProfile = IoFtp.Core.Models.ConnectionProfile;

namespace IoFtp.Mobile;

public sealed class SiteEditorPage : ContentPage
{
    private readonly Guid _id;
    private readonly SiteOptions _originalOptions;
    private readonly Entry _name = new() { Placeholder = "Namn" };
    private readonly Picker _protocol = new() { Title = "Protokoll" };
    private readonly Picker _tlsPolicy = new() { Title = "TLS-version" };
    private readonly Entry _host = new() { Placeholder = "Server" };
    private readonly Entry _port = new() { Placeholder = "Port", Keyboard = Keyboard.Numeric };
    private readonly Entry _username = new() { Placeholder = "Användarnamn" };
    private readonly Entry _password = new() { Placeholder = "Lösenord", IsPassword = true };
    private readonly Entry _startPath = new() { Placeholder = "Startmapp", Text = "/" };
    private readonly Entry _hostKey = new() { Placeholder = "SSH host key (SHA256), valfritt" };
    private readonly Switch _invalidCertificate = new();
    private readonly Switch _brokenPasv = new();
    private readonly Picker _proxyType = new() { Title = "Proxy" };
    private readonly Entry _proxyHost = new() { Placeholder = "Proxyserver" };
    private readonly Entry _proxyPort = new() { Placeholder = "Proxyport", Keyboard = Keyboard.Numeric };
    private readonly Entry _proxyUsername = new() { Placeholder = "Proxyanvändare" };
    private readonly Entry _proxyPassword = new() { Placeholder = "Proxylösenord", IsPassword = true };
    private readonly Switch _proxyDns = new();
    private readonly Switch _proxyData = new();

    public event Func<ConnectionProfile, Task>? Saved;

    public SiteEditorPage(ConnectionProfile? profile)
    {
        _id = profile?.Id ?? Guid.NewGuid();
        _originalOptions = profile?.EffectiveOptions ?? new SiteOptions();
        Title = profile is null ? "Ny site" : "Redigera site";
        _protocol.ItemsSource = Enum.GetValues<TransferProtocol>().Select(TransferProtocolNames.Display).ToList();
        _tlsPolicy.ItemsSource = new[]
        {
            "Automatiskt (TLS 1.3 eller 1.2)",
            "Kräv TLS 1.3",
            "Endast TLS 1.2"
        };
        _proxyType.ItemsSource = new[] { "Ingen proxy", "SOCKS4", "SOCKS5", "HTTP CONNECT" };
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
            _tlsPolicy.SelectedIndex = (int)profile.TlsPolicy;
            _proxyType.SelectedIndex = (int)(profile.Proxy?.Type ?? ProxyType.None);
            _proxyHost.Text = profile.Proxy?.Host ?? "";
            _proxyPort.Text = profile.Proxy?.Port > 0 ? profile.Proxy.Port.ToString() : "";
            _proxyUsername.Text = profile.Proxy?.Username ?? "";
            _proxyPassword.Text = profile.Proxy?.Password ?? "";
            _proxyDns.IsToggled = profile.Proxy?.ProxyDns ?? true;
            _proxyData.IsToggled = profile.Proxy?.UseForData ?? true;
        }
        else
        {
            _protocol.SelectedIndex = (int)TransferProtocol.FtpsExplicit;
            _port.Text = "21";
            _tlsPolicy.SelectedIndex = (int)TlsPolicy.Automatic;
            _proxyType.SelectedIndex = (int)ProxyType.None;
            _proxyDns.IsToggled = true;
            _proxyData.IsToggled = true;
        }
        _protocol.SelectedIndexChanged += (_, _) =>
        {
            var selectedProtocol = (TransferProtocol)Math.Max(0, _protocol.SelectedIndex);
            _port.Text = selectedProtocol switch
            {
                TransferProtocol.Sftp => "22",
                TransferProtocol.FtpsImplicit => "990",
                _ => "21"
            };
            _tlsPolicy.IsEnabled = selectedProtocol is TransferProtocol.FtpsExplicit or TransferProtocol.FtpsImplicit;
        };
        _tlsPolicy.IsEnabled = (TransferProtocol)Math.Max(0, _protocol.SelectedIndex)
            is TransferProtocol.FtpsExplicit or TransferProtocol.FtpsImplicit;
        _proxyType.SelectedIndexChanged += (_, _) => UpdateProxyFields();
        UpdateProxyFields();
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
                    _name, _protocol, _tlsPolicy, _host, _port, _username, _password, _startPath, _hostKey,
                    new Label
                    {
                        Text = "Automatiskt väljer högsta gemensamma version. TLS 1.0 och 1.1 tillåts aldrig.",
                        TextColor = Color.FromArgb("#91A2B1"),
                        FontSize = 12
                    },
                    new Label
                    {
                        Text = "PROXY PER SITE",
                        FontAttributes = FontAttributes.Bold,
                        TextColor = Color.FromArgb("#42C9B4")
                    },
                    _proxyType, _proxyHost, _proxyPort, _proxyUsername, _proxyPassword,
                    new HorizontalStackLayout
                    {
                        Children =
                        {
                            new Label { Text = "Lös servernamn genom proxyn", VerticalOptions = LayoutOptions.Center },
                            _proxyDns
                        }
                    },
                    new HorizontalStackLayout
                    {
                        Children =
                        {
                            new Label { Text = "Använd proxy för PASV/EPSV-data", VerticalOptions = LayoutOptions.Center },
                            _proxyData
                        }
                    },
                    new Label
                    {
                        Text = "Proxylösenord lagras i Android Secure Storage. Aktiv FTP/PORT och SFTP använder inte proxyn.",
                        TextColor = Color.FromArgb("#91A2B1"),
                        FontSize = 12
                    },
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
        var proxyType = (ProxyType)Math.Max(0, _proxyType.SelectedIndex);
        if (proxyType != ProxyType.None &&
            (string.IsNullOrWhiteSpace(_proxyHost.Text) ||
             !int.TryParse(_proxyPort.Text, out var proxyPort) || proxyPort is < 1 or > 65535))
        {
            await DisplayAlert("Proxy", "Ange proxyserver och en giltig proxyport.", "OK");
            return;
        }
        var proxy = proxyType == ProxyType.None
            ? null
            : new ProxyConfiguration(proxyType, _proxyHost.Text.Trim(), int.Parse(_proxyPort.Text!),
                _proxyUsername.Text?.Trim() ?? "", _proxyPassword.Text ?? "",
                _proxyDns.IsToggled, _proxyData.IsToggled);
        var options = _originalOptions with
        {
            BasePath = string.IsNullOrWhiteSpace(_startPath.Text) ? "/" : _startPath.Text.Trim(),
            BrokenPasv = protocol != TransferProtocol.Sftp && _brokenPasv.IsToggled
        };
        var profile = new ConnectionProfile(
            _id, _name.Text.Trim(), _host.Text.Trim(), port, _username.Text?.Trim() ?? "",
            protocol, _password.Text ?? "", _invalidCertificate.IsToggled,
            Options: options,
            Proxy: proxy,
            SshHostKeyFingerprint: _hostKey.Text?.Trim() ?? "",
            TlsPolicy: protocol is TransferProtocol.FtpsExplicit or TransferProtocol.FtpsImplicit
                ? (TlsPolicy)Math.Max(0, _tlsPolicy.SelectedIndex)
                : TlsPolicy.Automatic);
        if (Saved is not null) await Saved(profile);
    }

    private void UpdateProxyFields()
    {
        var enabled = (ProxyType)Math.Max(0, _proxyType.SelectedIndex) != ProxyType.None;
        _proxyHost.IsEnabled = enabled;
        _proxyPort.IsEnabled = enabled;
        _proxyUsername.IsEnabled = enabled;
        _proxyPassword.IsEnabled = enabled;
        _proxyDns.IsEnabled = enabled;
        _proxyData.IsEnabled = enabled;
    }
}
