using System.Text.Json;
using IoFtp.Core.Models;
using ConnectionProfile = IoFtp.Core.Models.ConnectionProfile;

namespace IoFtp.Mobile.Services;

public sealed class SiteStore
{
    private const string SitesKey = "fluxftp.sites.v1";
    private static string PasswordKey(Guid id) => $"fluxftp.password.{id:N}";
    private static string ProxyPasswordKey(Guid id) => $"fluxftp.proxy-password.{id:N}";

    public async Task<IReadOnlyList<ConnectionProfile>> LoadAsync()
    {
        var json = Preferences.Default.Get(SitesKey, "[]");
        var profiles = JsonSerializer.Deserialize<List<ConnectionProfile>>(json) ?? [];
        var result = new List<ConnectionProfile>(profiles.Count);
        foreach (var profile in profiles)
        {
            string password;
            string proxyPassword;
            try { password = await SecureStorage.Default.GetAsync(PasswordKey(profile.Id)) ?? ""; }
            catch { password = ""; }
            try { proxyPassword = await SecureStorage.Default.GetAsync(ProxyPasswordKey(profile.Id)) ?? ""; }
            catch { proxyPassword = ""; }
            result.Add(profile with
            {
                Password = password,
                Proxy = profile.Proxy is null ? null : profile.Proxy with { Password = proxyPassword }
            });
        }
        return result.OrderBy(x => x.Name).ToList();
    }

    public async Task SaveAsync(IEnumerable<ConnectionProfile> profiles)
    {
        var list = profiles.ToList();
        foreach (var profile in list)
        {
            if (string.IsNullOrEmpty(profile.Password))
                SecureStorage.Default.Remove(PasswordKey(profile.Id));
            else
                await SecureStorage.Default.SetAsync(PasswordKey(profile.Id), profile.Password);

            if (string.IsNullOrEmpty(profile.Proxy?.Password))
                SecureStorage.Default.Remove(ProxyPasswordKey(profile.Id));
            else
                await SecureStorage.Default.SetAsync(ProxyPasswordKey(profile.Id), profile.Proxy.Password);
        }
        Preferences.Default.Set(SitesKey, JsonSerializer.Serialize(list));
    }

    public async Task DeleteAsync(ConnectionProfile profile)
    {
        var profiles = (await LoadAsync()).Where(x => x.Id != profile.Id).ToList();
        SecureStorage.Default.Remove(PasswordKey(profile.Id));
        SecureStorage.Default.Remove(ProxyPasswordKey(profile.Id));
        await SaveAsync(profiles);
    }
}
