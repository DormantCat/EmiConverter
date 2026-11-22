using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace EmiConverter;

internal enum DossierSection { DomainWhois, Dns, NetworkWhois, Traceroute, ServiceScan }

internal sealed class DossierLookup(HttpClient http)
{
    private static readonly string[] DnsTypes = ["A", "AAAA", "CNAME", "MX", "NS", "SOA", "TXT", "CAA"];
    private static readonly Dictionary<int, string> DnsNames = new()
    {
        [1] = "A", [2] = "NS", [5] = "CNAME", [6] = "SOA", [12] = "PTR",
        [15] = "MX", [16] = "TXT", [28] = "AAAA", [257] = "CAA",
    };
    private static readonly (int Port, string Label)[] Services =
        [(21, "FTP"), (25, "SMTP"), (80, "HTTP"), (110, "POP3"), (143, "IMAP"), (443, "HTTPS")];

    public static string? ParseTarget(string input)
    {
        input = input.Trim();
        if (IPAddress.TryParse(input, out var ip))
            return ip.ToString();
        var address = input.StartsWith("//", StringComparison.Ordinal) ? "https:" + input : input;
        if (Uri.TryCreate(address, UriKind.Absolute, out var url) &&
            url.Scheme is "http" or "https" or "ftp" && IPAddress.TryParse(url.Host, out ip))
            return ip.ToString();
        return DomainLookup.FromInput(input);
    }

    public async Task<string> QueryAsync(DossierSection section, string target, CancellationToken token)
    {
        if (section == DossierSection.Dns)
            return await DnsAsync(target, token);

        var ip = IPAddress.TryParse(target, out var literal)
            ? literal : (await Dns.GetHostAddressesAsync(target).WaitAsync(token)).FirstOrDefault();
        if (ip is null)
            return $"Не удалось найти IP-адрес для {target}.";

        var heading = target == ip.ToString() ? $"IP: {ip}" : $"{target} → {ip}";
        var result = section switch
        {
            DossierSection.NetworkWhois => await NetworkWhoisAsync(ip, token),
            DossierSection.Traceroute => await TraceAsync(ip, token),
            DossierSection.ServiceScan => await CheckServicesAsync(ip, token),
            _ => throw new ArgumentOutOfRangeException(nameof(section)),
        };
        return heading + "\r\n\r\n" + result;
    }

    private async Task<string> DnsAsync(string target, CancellationToken token)
    {
        bool reverse = IPAddress.TryParse(target, out var ip);
        string name = reverse ? ReverseName(ip!) : target;
        string[] types = reverse ? ["PTR"] : DnsTypes;
        var results = await Task.WhenAll(types.Select(type => DnsTypeAsync(name, type, token)));
        var records = results.SelectMany(result => result.Records).Distinct().ToArray();
        var errors = results.Select(result => result.Error).OfType<string>().Distinct();
        var heading = $"DNS: {name}\r\nРезолвер: Google Public DNS";
        var body = records.Length == 0 ? "Записи не получены." : string.Join("\r\n", records);
        return string.Join("\r\n", new[] { heading, "", body }.Concat(errors));
    }

    private async Task<(IReadOnlyList<string> Records, string? Error)> DnsTypeAsync(string name, string type, CancellationToken token)
    {
        try
        {
            var url = "https://dns.google/resolve?name=" + Uri.EscapeDataString(name) + "&type=" + type;
            using var document = JsonDocument.Parse(await http.GetStringAsync(url, token));
            var root = document.RootElement;
            var status = root.GetProperty("Status").GetInt32();
            if (status != 0)
                return ([], status == 3 ? "Имя не найдено (NXDOMAIN)." : $"{type}: ошибка DNS (код {status}).");
            if (!root.TryGetProperty("Answer", out var answers))
                return ([], null);

            var records = new List<string>();
            foreach (var answer in answers.EnumerateArray())
            {
                int code = answer.GetProperty("type").GetInt32();
                records.Add($"{answer.GetProperty("name").GetString()}  {answer.GetProperty("TTL").GetInt32()}  "
                    + $"{DnsNames.GetValueOrDefault(code, code.ToString())}  {answer.GetProperty("data").GetString()}");
            }
            return (records, null);
        }
        catch (Exception ex) when (!token.IsCancellationRequested &&
            ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return ([], $"{type}: ответ резолвера недоступен.");
        }
    }

    internal static string ReverseName(IPAddress ip)
    {
        var bytes = ip.GetAddressBytes();
        return ip.AddressFamily == AddressFamily.InterNetwork
            ? string.Join('.', Enumerable.Reverse(bytes)) + ".in-addr.arpa"
            : string.Join('.', Enumerable.Reverse(Convert.ToHexString(bytes).ToLowerInvariant())) + ".ip6.arpa";
    }

    private async Task<string> NetworkWhoisAsync(IPAddress ip, CancellationToken token)
    {
        var address = new Uri("https://rdap-bootstrap.arin.net/bootstrap/ip/" + Uri.EscapeDataString(ip.ToString()));
        using var response = await http.GetAsync(address, token);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return "Реестр не вернул запись для этого IP-адреса.";
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        return DomainLookup.FormatRegistration(json.RootElement, response.RequestMessage?.RequestUri?.Host ?? address.Host);
    }

    private static async Task<string> TraceAsync(IPAddress ip, CancellationToken token)
    {
        if (OperatingSystem.IsLinux()) return await LinuxTrace.RunAsync(ip, token);
        using var ping = new System.Net.NetworkInformation.Ping();
        var rows = new List<string>();
        for (int ttl = 1; ttl <= 20; ttl++)
        {
            token.ThrowIfCancellationRequested();
            var reply = await ping.SendPingAsync(ip, 1000, new byte[16],
                new System.Net.NetworkInformation.PingOptions(ttl, false));
            // A hop that did not answer reports an unspecified address (0.0.0.0 or ::).
            var hop = reply.Address is { } address && !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any)
                ? address.ToString() : "*";
            rows.Add($"{ttl,2}   {hop}   {reply.Status}");
            if (reply.Status == System.Net.NetworkInformation.IPStatus.Success) break;
        }
        return string.Join(Environment.NewLine, rows);
    }
    private static async Task<string> CheckServicesAsync(IPAddress ip, CancellationToken token)
    {
        var checks = Services.Select(async service =>
        {
            using var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(2));
            string status;
            try
            {
                await socket.ConnectAsync(new IPEndPoint(ip, service.Port), limit.Token);
                status = "соединение установлено";
            }
            catch (SocketException ex)
            {
                status = ex.SocketErrorCode == SocketError.ConnectionRefused ? "соединение отклонено" : ex.SocketErrorCode.ToString();
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { status = "нет ответа"; }
            return $"{service.Port,3}/TCP  {service.Label,-5}  {status}";
        });

        return string.Join("\r\n", await Task.WhenAll(checks));
    }



}
