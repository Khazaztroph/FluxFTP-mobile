using System.Net;
using System.Net.Sockets;
using System.Text;
using IoFtp.Core.Models;

namespace IoFtp.Core.Transport;

public static class ProxyConnector
{
    public static async Task<TcpClient> ConnectAsync(string host, int port, ProxyConfiguration? proxy, CancellationToken cancellationToken)
    {
        if (proxy is null || proxy.Type == ProxyType.None)
        {
            var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
            var address = addresses.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork)
                ?? addresses.First(ip => ip.AddressFamily == AddressFamily.InterNetworkV6);
            var direct = new TcpClient(address.AddressFamily); await direct.ConnectAsync(address, port, cancellationToken); return direct;
        }
        if (string.IsNullOrWhiteSpace(proxy.Host) || proxy.Port is < 1 or > 65535) throw new InvalidOperationException("Proxy host or port is invalid.");
        var proxyAddress = (await Dns.GetHostAddressesAsync(proxy.Host, cancellationToken)).First(ip => ip.AddressFamily == AddressFamily.InterNetwork);
        var client = new TcpClient(AddressFamily.InterNetwork); await client.ConnectAsync(proxyAddress, proxy.Port, cancellationToken);
        try
        {
            var stream = client.GetStream();
            switch (proxy.Type)
            {
                case ProxyType.Socks5: await Socks5Async(stream, host, port, proxy, cancellationToken); break;
                case ProxyType.Socks4: await Socks4Async(stream, host, port, proxy, cancellationToken); break;
                case ProxyType.HttpConnect: await HttpConnectAsync(stream, host, port, proxy, cancellationToken); break;
            }
            return client;
        }
        catch { client.Dispose(); throw; }
    }

    private static async Task Socks5Async(Stream stream, string host, int port, ProxyConfiguration proxy, CancellationToken token)
    {
        var auth = string.IsNullOrEmpty(proxy.Username) ? (byte)0 : (byte)2;
        await stream.WriteAsync(new byte[] { 5, 1, auth }, token); var greeting = await ReadExactAsync(stream, 2, token);
        if (greeting[0] != 5 || greeting[1] == 0xff) throw new IOException("SOCKS5 proxy rejected authentication methods.");
        if (greeting[1] == 2)
        {
            var user = Encoding.UTF8.GetBytes(proxy.Username); var pass = Encoding.UTF8.GetBytes(proxy.Password);
            if (user.Length > 255 || pass.Length > 255) throw new IOException("SOCKS5 credentials are too long.");
            var request = new byte[3 + user.Length + pass.Length]; request[0] = 1; request[1] = (byte)user.Length; user.CopyTo(request, 2); request[2 + user.Length] = (byte)pass.Length; pass.CopyTo(request, 3 + user.Length);
            await stream.WriteAsync(request, token); var response = await ReadExactAsync(stream, 2, token); if (response[1] != 0) throw new IOException("SOCKS5 authentication failed.");
        }
        byte[] address; byte type;
        if (!proxy.ProxyDns && IPAddress.TryParse(host, out var parsed)) { type = 1; address = parsed.GetAddressBytes(); }
        else if (!proxy.ProxyDns) { type = 1; address = (await Dns.GetHostAddressesAsync(host, token)).First(ip => ip.AddressFamily == AddressFamily.InterNetwork).GetAddressBytes(); }
        else { type = 3; var name = Encoding.ASCII.GetBytes(host); address = [(byte)name.Length, .. name]; }
        await stream.WriteAsync(new byte[] { 5, 1, 0, type }.Concat(address).Concat(new byte[] { (byte)(port >> 8), (byte)port }).ToArray(), token);
        var head = await ReadExactAsync(stream, 4, token); if (head[1] != 0) throw new IOException($"SOCKS5 CONNECT failed with code {head[1]}.");
        var length = head[3] switch { 1 => 4, 4 => 16, 3 => (await ReadExactAsync(stream, 1, token))[0], _ => throw new IOException("Invalid SOCKS5 response.") };
        await ReadExactAsync(stream, length + 2, token);
    }

    private static async Task Socks4Async(Stream stream, string host, int port, ProxyConfiguration proxy, CancellationToken token)
    {
        var user = Encoding.ASCII.GetBytes(proxy.Username); byte[] address; byte[] suffix = [];
        if (proxy.ProxyDns) { address = [0, 0, 0, 1]; suffix = [.. Encoding.ASCII.GetBytes(host), 0]; }
        else address = (await Dns.GetHostAddressesAsync(host, token)).First(ip => ip.AddressFamily == AddressFamily.InterNetwork).GetAddressBytes();
        var request = new byte[] { 4, 1, (byte)(port >> 8), (byte)port }.Concat(address).Concat(user).Concat(new byte[] { 0 }).Concat(suffix).ToArray();
        await stream.WriteAsync(request, token); var response = await ReadExactAsync(stream, 8, token); if (response[1] != 90) throw new IOException($"SOCKS4 CONNECT failed with code {response[1]}.");
    }

    private static async Task HttpConnectAsync(Stream stream, string host, int port, ProxyConfiguration proxy, CancellationToken token)
    {
        var target = proxy.ProxyDns ? host : (await Dns.GetHostAddressesAsync(host, token)).First(ip => ip.AddressFamily == AddressFamily.InterNetwork).ToString();
        var auth = string.IsNullOrEmpty(proxy.Username) ? "" : $"Proxy-Authorization: Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes($"{proxy.Username}:{proxy.Password}"))}\r\n";
        var request = Encoding.ASCII.GetBytes($"CONNECT {target}:{port} HTTP/1.1\r\nHost: {target}:{port}\r\n{auth}Proxy-Connection: Keep-Alive\r\n\r\n"); await stream.WriteAsync(request, token);
        var buffer = new List<byte>(); var one = new byte[1];
        while (buffer.Count < 32768) { if (await stream.ReadAsync(one, token) == 0) throw new IOException("HTTP proxy closed the connection."); buffer.Add(one[0]); var count = buffer.Count; if (count >= 4 && buffer[count-4] == 13 && buffer[count-3] == 10 && buffer[count-2] == 13 && buffer[count-1] == 10) break; }
        var status = Encoding.ASCII.GetString(buffer.ToArray()).Split("\r\n", 2)[0]; if (!status.Contains(" 200 ")) throw new IOException($"HTTP CONNECT failed: {status}");
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int count, CancellationToken token)
    {
        var result = new byte[count]; var offset = 0; while (offset < count) { var read = await stream.ReadAsync(result.AsMemory(offset), token); if (read == 0) throw new IOException("Proxy closed the connection."); offset += read; } return result;
    }
}
