using System.Globalization;
using System.Net;
using System.Net.Mail;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EmiConverter;

internal sealed record DomainRecord(
    string Domain, string? Registrar, IReadOnlyList<string> Abuse, string Raw, string? Message = null, string? AbuseSource = null)
{
    internal string? RdapSummary { get; init; }
    internal string Report => RdapSummary ?? Raw;

    public static DomainRecord Unavailable(string domain, string message, string? source = null) =>
        new(domain, null, [], string.Join("\r\n", new[] { $"Домен: {domain}", source, message }.OfType<string>()), message);
}

internal sealed class DomainLookup(HttpClient http)
{
    private const string BootstrapUrl = "https://data.iana.org/rdap/dns.json";
    private const string SuffixUrl = "https://publicsuffix.org/list/public_suffix_list.dat";
    private const int MaxWhoisChars = 512 * 1024;
    private static readonly char[] Dots = ['\u3002', '\uff0e', '\uff61'];
    private static readonly IdnMapping Idn = new() { UseStd3AsciiRules = true };
    private static readonly Regex EmailDomain = new("""@([\p{L}\p{N}.\-\u3002\uff0e\uff61]+)""");
    private static readonly Dictionary<string, string> CommonWhois = new(StringComparer.Ordinal)
    {
        ["ru"] = "whois.tcinet.ru", ["su"] = "whois.tcinet.ru", ["xn--p1ai"] = "whois.tcinet.ru",
        ["de"] = "whois.denic.de", ["uk"] = "whois.nic.uk", ["io"] = "whois.nic.io",
        ["com"] = "whois.verisign-grs.com", ["net"] = "whois.verisign-grs.com",
        ["org"] = "whois.publicinterestregistry.org",
    };
    private static readonly HashSet<string> CommonSuffixes = new(
        ("ac.uk co.uk gov.uk ltd.uk me.uk net.uk nhs.uk org.uk plc.uk police.uk sch.uk " +
        "com.au net.au org.au edu.au gov.au asn.au id.au co.nz net.nz org.nz govt.nz ac.nz " +
        "com.br net.br org.br com.cn net.cn org.cn gov.cn com.hk com.tw co.jp ne.jp or.jp " +
        "co.kr or.kr co.in firm.in net.in org.in gen.in ind.in co.za org.za net.za com.sg " +
        "com.mx com.tr com.ua net.ua org.ua com.pl com.ru com.by com.kz").Split(' '), StringComparer.Ordinal);
    private string? suffixList;
    private JsonElement? bootstrap;

    public static IEnumerable<string> FromAddresses(string addresses)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in addresses.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (MailAddress.TryCreate(item, out var address) && Normalize(address.Host) is { } domain)
                result.Add(domain);
            else
            {
                foreach (Match match in EmailDomain.Matches(item))
                    if (Normalize(match.Groups[1].Value) is { } candidate)
                        result.Add(candidate);
            }
        }
        return result;
    }

    public static string? FromInput(string input)
    {
        input = input.Trim();
        if (input.Length == 0)
            return null;
        if (input.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            input = Uri.UnescapeDataString(input[7..].Split('?', 2)[0]);
        if (!input.Contains("://", StringComparison.Ordinal) && MailAddress.TryCreate(input, out var address))
            return Normalize(address.Host);
        if (input.StartsWith("//", StringComparison.Ordinal))
            input = "https:" + input;
        else if (!input.Contains("://", StringComparison.Ordinal))
            input = "https://" + input;
        return Uri.TryCreate(input, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "ftp"
            ? Normalize(uri.Host) : null;
    }

    public async Task<DomainRecord> QueryAsync(string host, CancellationToken cancellationToken)
    {
        using var detailsTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        detailsTimeout.CancelAfter(TimeSpan.FromSeconds(7));
        var suffixTask = LoadSuffixesAsync(detailsTimeout.Token);
        var bootstrapTask = LoadBootstrapAsync(detailsTimeout.Token);
        await Task.WhenAll(suffixTask, bootstrapTask);
        cancellationToken.ThrowIfCancellationRequested();

        var registrationDomain = RegistrableDomain(host, suffixTask.Result);
        if (registrationDomain is null)
            return DomainRecord.Unavailable(host, "Для этого суффикса нет отдельной записи о регистрации.");

        if (bootstrapTask.Result is { } services && FindRdap(services, registrationDomain) is { } endpoint)
        {
            try
            {
                using var rdapTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                rdapTimeout.CancelAfter(TimeSpan.FromSeconds(9));
                var url = new Uri(endpoint.TrimEnd('/') + "/domain/" + Uri.EscapeDataString(registrationDomain));
                using var response = await http.GetAsync(url, rdapTimeout.Token);
                if (response.StatusCode == HttpStatusCode.NotFound)
                    return DomainRecord.Unavailable(registrationDomain, "Реестр не вернул запись (HTTP 404).", $"RDAP: {url.Host}");
                response.EnsureSuccessStatusCode();
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(rdapTimeout.Token));
                return ReadRdap(registrationDomain, response.RequestMessage?.RequestUri?.Host ?? url.Host, json.RootElement);
            }
            catch (Exception ex) when (IsLookupFailure(ex) && !cancellationToken.IsCancellationRequested) { }
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var whoisTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        whoisTimeout.CancelAfter(TimeSpan.FromSeconds(12));
        var tld = registrationDomain.Split('.')[^1];
        try
        {
            if (!CommonWhois.TryGetValue(tld, out var server))
            {
                var referral = await QueryWhoisAsync("whois.iana.org", tld, whoisTimeout.Token);
                server = Regex.Match(referral, @"^(?:whois|refer):[ \t]*([a-z0-9.-]+)\b",
                    RegexOptions.Multiline | RegexOptions.IgnoreCase).Groups[1].Value;
            }
            if (server.Length == 0 || server.Equals("whois.iana.org", StringComparison.OrdinalIgnoreCase))
                return DomainRecord.Unavailable(registrationDomain, "Для этой зоны не найден сервер WHOIS. Ответ RDAP недоступен.");

            var query = tld == "de" ? "-T dn " + registrationDomain : registrationDomain;
            var result = await QueryWhoisAsync(server, query, whoisTimeout.Token);
            return string.IsNullOrWhiteSpace(result)
                ? DomainRecord.Unavailable(registrationDomain, "Сервер вернул пустой ответ.", $"WHOIS: {server}")
                : ReadWhois(registrationDomain, server, result);
        }
        catch (Exception ex) when (IsLookupFailure(ex) && !cancellationToken.IsCancellationRequested)
        {
            return DomainRecord.Unavailable(registrationDomain, "Не удалось получить данные от RDAP или WHOIS.\r\n"
                + (ex is OperationCanceledException ? "Время ожидания ответа истекло." : ex.Message));
        }
    }

    private async Task<string?> LoadSuffixesAsync(CancellationToken token)
    {
        try
        {
            return suffixList ??= await http.GetStringAsync(SuffixUrl, token);
        }
        catch (Exception ex) when (IsLookupFailure(ex)) { return null; }
    }

    private async Task<JsonElement?> LoadBootstrapAsync(CancellationToken token)
    {
        if (bootstrap is not null)
            return bootstrap;
        try
        {
            using var json = JsonDocument.Parse(await http.GetStringAsync(BootstrapUrl, token));
            return bootstrap = json.RootElement.GetProperty("services").Clone();
        }
        catch (Exception ex) when (IsLookupFailure(ex)) { return null; }
    }

    internal static string? RegistrableDomain(string host, string? suffixes)
    {
        int publicLabels = 1;
        var labels = host.Split('.');
        if (suffixes is null)
        {
            if (labels.Length >= 2 && CommonSuffixes.Contains(string.Join('.', labels[^2..])))
                publicLabels = 2;
        }
        else
        {
            foreach (var rawRule in suffixes.Split('\n'))
            {
                var rule = rawRule.Trim();
                if (rule == "// ===END ICANN DOMAINS===")
                    break;
                if (rule.Length == 0 || rule.StartsWith("//", StringComparison.Ordinal))
                    continue;
                bool exception = rule.StartsWith('!');
                bool wildcard = rule.StartsWith("*.", StringComparison.Ordinal);
                string? suffix = Ascii(rule[(exception ? 1 : wildcard ? 2 : 0)..]);
                if (suffix is null || !(host == suffix || host.EndsWith("." + suffix, StringComparison.Ordinal)))
                    continue;

                int count = suffix.Count(c => c == '.') + 1;
                if (exception)
                {
                    publicLabels = count - 1;
                    break;
                }
                if (!wildcard || labels.Length > count)
                    publicLabels = Math.Max(publicLabels, count + (wildcard ? 1 : 0));
            }
        }
        return labels.Length > publicLabels ? string.Join('.', labels[^(publicLabels + 1)..]) : null;
    }

    private static string? Normalize(string host)
    {
        var domain = Ascii(host.Trim().TrimEnd('.', Dots[0], Dots[1], Dots[2]));
        return domain is { Length: <= 253 } && domain.Contains('.') &&
            Uri.CheckHostName(domain) == UriHostNameType.Dns && !IPAddress.TryParse(domain, out _)
            ? domain : null;
    }

    private static string? Ascii(string input)
    {
        try { return Idn.GetAscii(input).ToLowerInvariant(); }
        catch (ArgumentException) { return null; }
    }

    private static string? FindRdap(JsonElement services, string host)
    {
        string? endpoint = null;
        int longest = -1;
        if (services.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var service in services.EnumerateArray())
        {
            if (service.ValueKind != JsonValueKind.Array || service.GetArrayLength() < 2 ||
                service[0].ValueKind != JsonValueKind.Array || service[1].ValueKind != JsonValueKind.Array)
                continue;
            foreach (var entry in service[0].EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.String)
                    continue;
                var suffix = entry.GetString() ?? "";
                if (suffix.Length <= longest || !(suffix.Length == 0 || host == suffix ||
                    host.EndsWith("." + suffix, StringComparison.Ordinal)))
                    continue;
                var address = service[1].EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString())
                    .FirstOrDefault(url => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https");
                if (address is not null)
                {
                    endpoint = address;
                    longest = suffix.Length;
                }
            }
        }
        return endpoint;
    }

    private static async Task<string> QueryWhoisAsync(string server, string query, CancellationToken token)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(server, 43, token);
        using var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(query + "\r\n"), token);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, false));
        var output = new StringBuilder();
        var buffer = new char[4096];
        while (output.Length < MaxWhoisChars)
        {
            int length = await reader.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, MaxWhoisChars - output.Length)), token);
            if (length == 0)
                return output.ToString();
            output.Append(buffer, 0, length);
        }
        return output.AppendLine().Append("[Ответ сокращён]").ToString();
    }

    internal static DomainRecord ReadRdap(string domain, string server, JsonElement record)
    {
        var registrar = Items(record, "entities").FirstOrDefault(entity => HasRole(entity, "registrar"));
        var handle = Value(registrar, "handle");
        var name = VcardValues(registrar, "fn").FirstOrDefault() ?? handle;
        var abuse = AbuseEntities(Items(record, "entities")).ToList();
        abuse.AddRange(ExplicitAbuseLinks(registrar));
        abuse.AddRange(ExplicitAbuseLinks(record));
        var raw = $"Домен: {domain}\r\nRDAP: {server}\r\n\r\n" + PrettyJson(record);
        var result = WithFallback(domain, name, handle, abuse, raw);
        return result with { RdapSummary = FormatRegistration(record, server, result) };
    }

    internal static DomainRecord ReadWhois(string domain, string server, string response)
    {
        string? registrar = null;
        var contacts = new List<string>();
        foreach (var line in response.Split('\n'))
        {
            int colon = line.IndexOf(':');
            if (colon < 0)
                continue;
            string key = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();
            if (key.Equals("registrar", StringComparison.OrdinalIgnoreCase) && value.Length > 0)
                registrar ??= value;

            if (key.Contains("abuse", StringComparison.OrdinalIgnoreCase))
            {
                foreach (Match match in ContactPattern.Matches(value))
                {
                    if (CleanContact(match.Value) is { } contact)
                        contacts.Add(contact);
                }
            }
        }

        var raw = $"Домен: {domain}\r\nWHOIS: {server}\r\n\r\n" + response;
        return WithFallback(domain, registrar, null, contacts, raw);
    }

    private static DomainRecord WithFallback(string domain, string? registrar, string? handle, IEnumerable<string> contacts, string raw)
    {
        var abuse = contacts.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (abuse.Length > 0)
            return new(domain, registrar, abuse, raw);

        var official = KnownContact(handle) ?? KnownContact(registrar);
        return official is { } item
            ? new(domain, registrar, item.Contacts, raw, AbuseSource: item.Source)
            : new(domain, registrar, abuse, raw);
    }

    private static (string[] Contacts, string Source)? KnownContact(string? registrar) =>
        registrar?.Trim().ToUpperInvariant() switch
        {
            "REGRU-RU" or "REG.RU LLC" or "REGISTRAR OF DOMAIN NAMES REG.RU LLC" =>
                (["abuse@reg.ru", "https://www.reg.ru/legal/abuse/"], "https://www.reg.ru/legal/abuse/"),
            "RU-CENTER-RU" or "RU-CENTER" or "REGIONAL NETWORK INFORMATION CENTER, JSC DBA RU-CENTER" =>
                (["tld-abuse@nic.ru"], "https://www.nic.ru/help/inye-zhaloby-po-domenam_12583.html"),
            "BEGET-RU" or "BEGET LLC" =>
                (["abuse@beget.com"], "https://beget.com/ru/contacts"),
            _ => null,
        };

    private static IEnumerable<string> AbuseEntities(IEnumerable<JsonElement> entities, int depth = 0)
    {
        if (depth >= 12)
            yield break;
        foreach (var entity in entities)
        {
            if (HasRole(entity, "abuse"))
            {
                foreach (var item in VcardValues(entity, "email").Concat(VcardValues(entity, "url")))
                    if (CleanContact(item) is { } contact)
                        yield return contact;

                foreach (var link in Items(entity, "links"))
                {
                    // RDAP object references point to an entity record, not a complaint form.
                    if (Value(link, "rel") is "self" or "related" ||
                        Value(link, "type")?.Contains("rdap", StringComparison.OrdinalIgnoreCase) == true)
                        continue;
                    if (Value(link, "href") is { } href && CleanContact(href) is { } contact)
                        yield return contact;
                }
            }

            foreach (var item in AbuseEntities(Items(entity, "entities"), depth + 1))
                yield return item;
        }
    }

    private static IEnumerable<string> ExplicitAbuseLinks(JsonElement record) =>
        Items(record, "links").Concat(Items(record, "remarks").SelectMany(remark => Items(remark, "links")))
            .Where(link => Value(link, "rel")?.Contains("abuse", StringComparison.OrdinalIgnoreCase) == true ||
                Value(link, "title")?.Contains("abuse", StringComparison.OrdinalIgnoreCase) == true)
            .Select(link => Value(link, "href")).OfType<string>().Select(CleanContact).OfType<string>();

    private static readonly Regex ContactPattern = new(
        """https?://[^\s<>"']+|[\w.!#$%&'*+/=?^{}|~\-]+@[\w.\-]+\.[\p{L}]{2,}""", RegexOptions.IgnoreCase);

    private static string? CleanContact(string value)
    {
        value = value.Trim().TrimEnd('.', ',', ')', '>', ';');
        if (value.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            value = value[7..].Split('?', 2)[0];
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Length > 0)
            return value;
        return MailAddress.TryCreate(value, out var address) && address.Address == value && address.Host.Contains('.')
            ? value : null;
    }

    private static bool HasRole(JsonElement entity, string role) => Items(entity, "roles").Any(
        item => item.ValueKind == JsonValueKind.String && string.Equals(item.GetString(), role, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> VcardValues(JsonElement entity, string key)
    {
        if (entity.ValueKind != JsonValueKind.Object || !entity.TryGetProperty("vcardArray", out var card) ||
            card.ValueKind != JsonValueKind.Array || card.GetArrayLength() < 2 || card[1].ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var field in card[1].EnumerateArray())
            if (field.ValueKind == JsonValueKind.Array && field.GetArrayLength() >= 4 &&
                field[0].ValueKind == JsonValueKind.String && field[0].GetString() == key &&
                field[3].ValueKind == JsonValueKind.String && field[3].GetString() is { Length: > 0 } val)
                yield return val;
    }

    internal static string FormatRegistration(JsonElement record, string server, DomainRecord? domain = null)
    {
        var rows = new List<string>();
        void Field(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) rows.Add($"{label}: {value.Trim()}");
        }
        if (domain != null)
        {
            Field("Домен", domain.Domain);
            Field("Регистратор", domain.Registrar);
            Field("Контакт для жалоб", string.Join(", ", domain.Abuse));
            Field("Источник контакта", domain.AbuseSource);
            var names = Items(record, "nameservers").Select(ns => Value(ns, "unicodeName") ?? Value(ns, "ldhName")).OfType<string>();
            Field("DNS-серверы", string.Join(", ", names.Distinct(StringComparer.OrdinalIgnoreCase)));
        }
        else
        {
            Field("Название сети", Value(record, "name"));
            var start = Value(record, "startAddress");
            var end = Value(record, "endAddress");
            if (start != null && end != null) Field("Диапазон", $"{start} – {end}");
            else { Field("Первый адрес", start); Field("Последний адрес", end); }
            foreach (var block in Items(record, "cidr0_cidrs"))
            {
                var prefix = Value(block, "v4prefix") ?? Value(block, "v6prefix");
                if (prefix != null && block.TryGetProperty("length", out var length) && length.ValueKind == JsonValueKind.Number)
                    Field("Префикс CIDR", $"{prefix}/{length}");
            }
            Field("Страна по реестру", Value(record, "country"));
            Field("Тип сети", Value(record, "type"));
            foreach (var owner in Items(record, "entities").Where(e => HasRole(e, "registrant")))
                Field("Владелец по реестру", VcardValues(owner, "fn").FirstOrDefault() ?? Value(owner, "handle"));
            Field("Контакт для жалоб", string.Join(", ", AbuseEntities(Items(record, "entities")).Concat(ExplicitAbuseLinks(record)).Distinct(StringComparer.OrdinalIgnoreCase)));
        }
        Field("Идентификатор", Value(record, "handle"));
        Field("Статус", string.Join(", ", Items(record, "status").Where(s => s.ValueKind == JsonValueKind.String).Select(s => s.GetString())));
        foreach (var item in Items(record, "events"))
        {
            var label = Value(item, "eventAction") switch
            {
                "registration" => "Дата регистрации", "expiration" => "Срок регистрации истекает",
                "last changed" => "Последнее изменение", "transfer" => "Дата переноса",
                "last update of RDAP database" => "Обновление реестра", var other => other
            };
            var date = Value(item, "eventDate");
            if (label == null || date == null) continue;
            if (DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time))
                date = time.UtcDateTime.ToString("dd.MM.yyyy HH:mm 'UTC'", CultureInfo.InvariantCulture);
            Field(label, date);
        }
        if (rows.Count == 0) rows.Add("Реестр не указал сведения о регистрации.");
        Field("Источник RDAP", server);
        return string.Join("\r\n", rows);
    }

    internal static string PrettyJson(JsonElement record) => JsonSerializer.Serialize(record, new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    private static IEnumerable<JsonElement> Items(JsonElement record, string name) =>
        record.ValueKind == JsonValueKind.Object && record.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray() : [];

    private static string? Value(JsonElement record, string name) =>
        record.ValueKind == JsonValueKind.Object && record.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static bool IsLookupFailure(Exception ex) =>
        ex is HttpRequestException or SocketException or IOException or JsonException or InvalidOperationException
            or KeyNotFoundException or OperationCanceledException or ArgumentException;
}
