using System.Net;
using System.Net.Sockets;
using System.Text;

namespace EmiConverter;

internal static class RedirectInspector
{
    private sealed record NetworkPrefix(byte[] Network, int Bits)
    {
        internal static NetworkPrefix Parse(string cidr)
        {
            var parts = cidr.Split('/');
            return new(IPAddress.Parse(parts[0]).GetAddressBytes(), int.Parse(parts[1]));
        }

        internal bool Contains(IPAddress address)
        {
            var value = address.GetAddressBytes();
            if (value.Length != Network.Length) return false;
            int whole = Bits / 8, remainder = Bits % 8;
            if (!value.AsSpan(0, whole).SequenceEqual(Network.AsSpan(0, whole))) return false;
            return remainder == 0 || ((value[whole] ^ Network[whole]) >> (8 - remainder)) == 0;
        }
    }
    private const int MaxRedirects = 6;
    private static readonly NetworkPrefix[] ReservedV4 =
    [
        NetworkPrefix.Parse("0.0.0.0/8"), NetworkPrefix.Parse("10.0.0.0/8"),
        NetworkPrefix.Parse("100.64.0.0/10"), NetworkPrefix.Parse("127.0.0.0/8"),
        NetworkPrefix.Parse("169.254.0.0/16"), NetworkPrefix.Parse("172.16.0.0/12"),
        NetworkPrefix.Parse("192.0.0.0/24"), NetworkPrefix.Parse("192.0.2.0/24"),
        NetworkPrefix.Parse("192.88.99.0/24"), NetworkPrefix.Parse("192.168.0.0/16"),
        NetworkPrefix.Parse("198.18.0.0/15"), NetworkPrefix.Parse("198.51.100.0/24"),
        NetworkPrefix.Parse("203.0.113.0/24"), NetworkPrefix.Parse("224.0.0.0/3"),
    ];
    private static readonly NetworkPrefix GlobalV6 = NetworkPrefix.Parse("2000::/3");
    private static readonly NetworkPrefix[] ReservedV6 =
    [
        NetworkPrefix.Parse("2001::/23"), NetworkPrefix.Parse("2001:db8::/32"),
        NetworkPrefix.Parse("2002::/16"), NetworkPrefix.Parse("3fff::/20"),
    ];

    private static Uri? ParseLink(string value)
    {
        if (value.StartsWith("//", StringComparison.Ordinal))
            value = "https:" + value;
        else if (value.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            value = "https://" + value;
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null;
    }

    internal static bool IsWebLink(string value) => ParseLink(value)?.Scheme is "http" or "https";

    internal static async Task<string> InspectAsync(string value, CancellationToken token)
    {
        IPAddress? peer = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        using var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(4), PooledConnectionLifetime = TimeSpan.Zero,
            MaxResponseHeadersLength = 24, MaxResponseDrainSize = 0,
            ConnectCallback = async (context, ct) =>
            {
                var host = context.DnsEndPoint.Host;
                var addresses = await Dns.GetHostAddressesAsync(host).WaitAsync(ct);
                if (addresses.Length == 0)
                    throw new HttpRequestException("DNS не вернул адрес сервера.");
                if (addresses.Any(address => !IsPublicAddress(address)))
                    throw new HttpRequestException("DNS вернул локальный или зарезервированный IP. Подключение заблокировано.");

                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct);
                    peer = ((IPEndPoint?)socket.RemoteEndPoint)?.Address;
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        using var client = new HttpClient(handler);

        return await FollowAsync(value, async (uri, ct) =>
        {
            peer = null;
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.ConnectionClose = true;
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            return new RedirectReply((int)response.StatusCode, response.Headers.Location?.OriginalString, peer?.ToString());
        }, deadline.Token, token);
    }

    internal static async Task<string> FollowAsync(string value, Func<Uri, CancellationToken, Task<RedirectReply>> request,
        CancellationToken deadline, CancellationToken userToken = default)
    {
        var current = ParseLink(value);
        if (current is null)
            return "Не удалось распознать адрес ссылки.";
        var path = new StringBuilder();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (int step = 0; step <= MaxRedirects; step++)
        {
            userToken.ThrowIfCancellationRequested();
            path.AppendLine($"{step + 1}. {current.AbsoluteUri}");
            if (BlockedReason(current) is { } blocked)
                return path.AppendLine().Append("Проверка остановлена. ").Append(blocked).ToString();
            if (!visited.Add(current.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped)))
                return path.AppendLine().Append("Проверка остановлена. Обнаружен цикл перенаправлений.").ToString();

            RedirectReply response;
            try
            {
                response = await request(current, deadline);
            }
            catch (OperationCanceledException) when (!userToken.IsCancellationRequested)
            {
                return path.AppendLine().Append("Время ожидания ответа истекло.").ToString();
            }
            catch (Exception ex) when (ex is HttpRequestException or SocketException)
            {
                return path.AppendLine().Append("Ошибка подключения. ").Append(ex.Message).ToString();
            }

            path.Append($"   HTTP {response.Status}");
            if (response.Peer is { } peer)
                path.Append($" · IP сервера {peer}");
            path.AppendLine();
            if (response.Status is not (301 or 302 or 303 or 307 or 308))
                return path.AppendLine().Append("Конечный адрес").AppendLine().Append(current.AbsoluteUri).ToString();
            if (string.IsNullOrWhiteSpace(response.Location) || !Uri.TryCreate(current, response.Location, out var next))
                return path.AppendLine().Append("Сервер вернул перенаправление без корректного адреса назначения.").ToString();
            if (step == MaxRedirects)
            {
                return path.AppendLine().Append("Следующий адрес").AppendLine().AppendLine(next.AbsoluteUri)
                    .Append("Проверка остановлена. Достигнут лимит перенаправлений.").ToString();
            }
            current = next;
        }
        return path.ToString();
    }

    private static string? BlockedReason(Uri uri)
    {
        if (uri.Scheme is not ("https" or "http"))
            return "Разрешены только HTTP и HTTPS.";
        if (uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6 || IPAddress.TryParse(uri.IdnHost, out _))
            return "Переход напрямую на IP-адрес заблокирован.";
        if (uri.HostNameType != UriHostNameType.Dns || !uri.IdnHost.TrimEnd('.').Contains('.') ||
            uri.IdnHost.TrimEnd('.').EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            uri.IdnHost.TrimEnd('.').EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            uri.IdnHost.TrimEnd('.').EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
            return "Переход разрешён только на публичные домены.";
        if (!uri.IsDefaultPort || uri.UserInfo.Length > 0)
            return "Нестандартные порты и учётные данные в URL заблокированы.";
        return null;
    }

    internal static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        return address.AddressFamily == AddressFamily.InterNetwork
            ? !ReservedV4.Any(range => range.Contains(address))
            : GlobalV6.Contains(address) && !ReservedV6.Any(range => range.Contains(address));
    }

    internal readonly record struct RedirectReply(int Status, string? Location, string? Peer);
}
