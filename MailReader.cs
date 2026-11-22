using System.Net;
using System.Net.Mail;
using System.Text;
using System.Text.RegularExpressions;

namespace EmiConverter;

internal sealed record MailDocument(
    string Subject, string Date, string From, string To, string Cc, string Bcc, string ReplyTo,
    string Body, string Source, IReadOnlyList<string> Links, string? Html);

internal static class MailReader
{
    public const int MaxFileBytes = 32 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(4);
    private static readonly Regex EncodedWord = Pattern(@"=\?([^?\s]+)\?([bq])\?([^?]*)\?=");
    private static readonly Regex HeaderLine = Pattern(@"^[!-9;-~]+:");
    private static readonly Regex Parameter = Pattern(""";\s*([^;=\s]+)\s*=\s*(?:"((?:\\.|[^"\\])*)"|([^;\s]*))""");
    private static readonly Regex TextUrl = Pattern("""(?<![\w@])(?:https?://|ftp://|www\.)[^\s<>"'\u0000-\u001f]+""");
    private static readonly Regex HtmlToken = Pattern("""
        <!--[\s\S]*?(?:-->|$)|<(script|style|head|template|title)\b(?:"[^"]*"|'[^']*'|[^"'<>])*>(?:[\s\S]*?</\1\s*>|[\s\S]*$)|<![^>]*>|</?([a-z][a-z0-9:-]*)\b((?:"[^"]*"|'[^']*'|[^"'<>])*)>
        """);
    private static readonly Regex Attribute = Pattern("""([^\s=/'"<>]+)(?:\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s>]+)))?""");

    static MailReader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static MailDocument Parse(byte[] data)
    {
        var source = DecodeBytes(data, null).TrimStart('\ufeff');
        if (data.AsSpan().StartsWith(new byte[] { 0xff, 0xfe }) || data.AsSpan().StartsWith(new byte[] { 0xfe, 0xff }))
            data = Encoding.UTF8.GetBytes(source);
        var raw = Encoding.Latin1.GetString(data);
        if (raw.StartsWith("\u00ef\u00bb\u00bf", StringComparison.Ordinal))
            raw = raw[3..];
        raw = raw.Replace("\r\n", "\n").Replace('\r', '\n');
        var links = new LinkCollector();
        var root = SplitEntity(raw);
        var body = ReadBody(root, links, 0, primary: true);
        var headers = root.Headers;
        return new MailDocument(
            Header(headers, "Subject"), Header(headers, "Date"), Address(headers, "From", "Sender"),
            Address(headers, "To"), Address(headers, "Cc"), Address(headers, "Bcc"),
            Address(headers, "Reply-To"), body.Plain.Trim(), source,
            links.Items, InlineImages(body.Html, links.Images));
    }

    private static (Dictionary<string, string> Headers, string Body) SplitEntity(string raw)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int separator = raw.StartsWith('\n') ? 0 : raw.IndexOf("\n\n", StringComparison.Ordinal);
        int firstNewline = raw.IndexOf('\n');
        var firstLine = firstNewline >= 0 ? raw[..firstNewline] : raw;
        int schemeEnd = firstLine.IndexOf("://", StringComparison.Ordinal);
        bool startsWithUrl = schemeEnd >= 0 && firstLine.IndexOf(':') == schemeEnd;
        if (separator != 0 && (!HeaderLine.IsMatch(firstLine) || startsWithUrl))
            return (headers, raw);

        var headerBlock = separator < 0 ? raw : raw[..separator];
        string? key = null;
        var value = new StringBuilder();
        void Commit()
        {
            if (key is not null)
                headers[key] = headers.TryGetValue(key, out var previous) ? previous + ", " + value : value.ToString();
        }

        foreach (var line in headerBlock.Split('\n'))
        {
            if (line.Length > 0 && line[0] is ' ' or '\t' && key is not null)
            {
                value.Append(' ').Append(line.Trim());
                continue;
            }

            Commit();
            key = null;
            value.Clear();
            if (!HeaderLine.IsMatch(line))
                continue;
            int colon = line.IndexOf(':');
            key = line[..colon];
            value.Append(line.AsSpan(colon + 1).Trim());
        }

        Commit();
        int bodyStart = separator == 0 ? 1 : separator + 2;
        return (headers, separator < 0 ? "" : raw[bodyStart..]);
    }

    // primary: the entity is the message body itself (top level, an alternative representation
    // or the root of multipart/related). Some mail clients add name= or filename= to such parts,
    // so only an explicit "attachment" disposition hides them.
    private static BodyContent ReadBody((Dictionary<string, string> Headers, string Body) entity, LinkCollector links, int depth,
        bool primary = false)
    {
        if (depth >= 30)
            throw new InvalidDataException("Слишком много вложенных MIME-частей.");

        var (headers, raw) = entity;
        var contentType = headers.GetValueOrDefault("Content-Type", "text/plain");
        var type = contentType.Split(';', 2)[0].Trim().ToLowerInvariant();
        var disposition = headers.GetValueOrDefault("Content-Disposition", "");
        if (disposition.Split(';', 2)[0].Trim().Equals("attachment", StringComparison.OrdinalIgnoreCase)
            || !primary && (GetParameter(disposition, "filename", "filename*", "filename*0", "filename*0*") is not null
                || GetParameter(contentType, "name", "name*", "name*0", "name*0*") is not null))
            return new("", null);

        if (type.StartsWith("multipart/", StringComparison.Ordinal))
        {
            var boundary = GetParameter(contentType, "boundary");
            if (string.IsNullOrEmpty(boundary))
                return new("", null);

            var parts = SplitMultipart(raw, boundary).Select(SplitEntity).ToList();
            if (type == "multipart/related" && parts.Count > 0)
            {
                var start = GetParameter(contentType, "start");
                var root = parts.FindIndex(part => start is not null && part.Headers.GetValueOrDefault("Content-ID") == start);
                root = Math.Max(0, root);
                for (int i = 0; i < parts.Count; i++)
                    if (i != root)
                        CollectImage(parts[i], links);
                return ReadBody(parts[root], links, depth + 1, primary);
            }

            // Inline images referenced by cid: may also sit in multipart/mixed (Outlook).
            foreach (var part in parts)
                CollectImage(part, links);
            bool alternative = type == "multipart/alternative";
            // In multipart/mixed the first part is the message text, the rest are attachments.
            var bodies = parts.Select((part, index) => ReadBody(part, links, depth + 1, primary && (alternative || index == 0))).ToArray();
            if (alternative)
            {
                var html = bodies.LastOrDefault(part => part.Html is not null);
                var plain = bodies.LastOrDefault(part => part.Html is null && !string.IsNullOrWhiteSpace(part.Plain));
                return new(plain.Plain ?? html.Plain ?? "", html.Html);
            }
            var text = string.Join("\n\n", bodies.Select(part => part.Plain.Trim()).Where(part => part.Length > 0));
            var visual = bodies.Where(part => part.Html is not null || !string.IsNullOrWhiteSpace(part.Plain)).ToArray();
            var markup = visual.Any(part => part.Html is not null)
                ? string.Join("\n<hr>\n", visual.Select(part => part.Html ??
                    "<div style=\"white-space:pre-wrap\">" + WebUtility.HtmlEncode(part.Plain) + "</div>"))
                : null;
            return new(text, markup);
        }

        if (type is not ("text/plain" or "text/html" or "message/rfc822" or "message/global"))
            return new("", null);

        var bytes = DecodeTransfer(raw, headers.GetValueOrDefault("Content-Transfer-Encoding", ""));
        if (type.StartsWith("message/", StringComparison.Ordinal))
            return ReadBody(SplitEntity(Encoding.Latin1.GetString(bytes).Replace("\r\n", "\n").Replace('\r', '\n')), links, depth + 1, true);

        var content = DecodeBytes(bytes, GetParameter(contentType, "charset"));
        var plainText = type == "text/html" ? ReadHtml(content, links) : content;
        links.AddText(plainText);
        return new(plainText, type == "text/html" ? content : null);
    }

    private readonly record struct BodyContent(string Plain, string? Html);

    private static void CollectImage((Dictionary<string, string> Headers, string Body) part, LinkCollector links)
    {
        var headers = part.Headers;
        var type = headers.GetValueOrDefault("Content-Type", "").Split(';', 2)[0].Trim().ToLowerInvariant();
        if (type == "image/jpg")
            type = "image/jpeg";
        if (type is not ("image/png" or "image/jpeg" or "image/gif" or "image/webp" or "image/bmp" or "image/x-icon"))
            return;
        var id = headers.GetValueOrDefault("Content-ID", "").Trim().Trim('<', '>');
        if (id.Length == 0 || links.Images.ContainsKey(id))
            return;
        byte[] bytes = DecodeTransfer(part.Body, headers.GetValueOrDefault("Content-Transfer-Encoding", ""));
        links.Images[id] = $"data:{type};base64,{Convert.ToBase64String(bytes)}";
    }

    private static string? InlineImages(string? html, Dictionary<string, string> images)
    {
        if (html is null || images.Count == 0)
            return html;

        return Regex.Replace(html, """cid:([^\s"'<>\\)]+)""", match =>
        {
            var id = Uri.UnescapeDataString(WebUtility.HtmlDecode(match.Groups[1].Value)).Trim('<', '>');
            return images.GetValueOrDefault(id, match.Value);
        }, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
    }

    private static IEnumerable<string> SplitMultipart(string raw, string boundary)
    {
        string marker = "--" + boundary;
        int partStart = -1;
        for (int position = 0; position < raw.Length;)
        {
            int newline = raw.IndexOf('\n', position);
            int end = newline < 0 ? raw.Length : newline;
            int next = newline < 0 ? raw.Length : newline + 1;

            if (raw.AsSpan(position).StartsWith(marker, StringComparison.Ordinal))
            {
                var line = raw.AsSpan(position, end - position).TrimEnd();
                bool closing = line.SequenceEqual(marker + "--");
                if (closing || line.SequenceEqual(marker))
                {
                    if (partStart >= 0)
                    {
                        int partEnd = position > partStart && raw[position - 1] == '\n' ? position - 1 : position;
                        yield return raw[partStart..partEnd];
                    }

                    if (closing)
                        yield break;
                    partStart = next;
                }
            }
            position = next;
        }
        if (partStart >= 0 && partStart < raw.Length)
            yield return raw[partStart..];
    }

    private static string? GetParameter(string header, params string[] keys)
    {
        foreach (Match match in Parameter.Matches(header))
        {
            if (keys.Contains(match.Groups[1].Value, StringComparer.OrdinalIgnoreCase))
                return match.Groups[2].Success
                    ? match.Groups[2].Value.Replace("\\\"", "\"").Replace("\\\\", "\\")
                    : match.Groups[3].Value;
        }
        return null;
    }

    private static string Address(Dictionary<string, string> headers, string key, string? fallback = null)
    {
        var raw = DecodeBytes(Encoding.Latin1.GetBytes(headers.GetValueOrDefault(key,
            fallback is null ? "" : headers.GetValueOrDefault(fallback, ""))), null);
        if (raw.Length == 0)
            return "";
        return string.Join(Environment.NewLine, SplitAddresses(raw).Select(item =>
        {
            if (!MailAddress.TryCreate(item, out var address))
                return DecodeWords(item);
            var name = DecodeWords(address.DisplayName);
            return string.IsNullOrWhiteSpace(name) ? address.Address : $"{name} <{address.Address}>";
        }));
    }

    private static readonly Regex AddressToken = Pattern("""=\?[^?\s]+\?[bq]\?[^?]*\?=|"(?:\\.|[^"\\])*"|\\.|[()<>,;:]""");

    private static IEnumerable<string> SplitAddresses(string raw)
    {
        int start = 0;
        int comments = 0;
        bool angle = false;
        bool group = false;
        foreach (Match token in AddressToken.Matches(raw))
        {
            if (token.Length != 1)
                continue;
            char c = raw[token.Index];
            if (c == '(') { comments++; continue; }
            if (c == ')' && comments > 0) { comments--; continue; }
            if (comments > 0)
                continue;
            if (c is '<' or '>') { angle = c == '<'; continue; }
            if (angle)
                continue;
            if (c == ':' && !group && !raw.AsSpan(start, token.Index - start).Contains('@'))
            {
                group = true;
                start = token.Index + 1;
                continue;
            }
            if (c is not (',' or ';'))
                continue;
            var item = raw[start..token.Index].Trim();
            if (item.Length > 0)
                yield return item;
            start = token.Index + 1;
            if (c == ';')
                group = false;
        }
        var last = raw[start..].Trim();
        if (last.Length > 0)
            yield return last;
    }

    private static string Header(Dictionary<string, string> headers, string key) =>
        DecodeWords(DecodeBytes(Encoding.Latin1.GetBytes(headers.GetValueOrDefault(key, "")), null));

    private static string DecodeWords(string raw)
    {
        var output = new StringBuilder();
        int start = 0;
        foreach (Match word in EncodedWord.Matches(raw))
        {
            var gap = raw.AsSpan(start, word.Index - start);
            if (start == 0 || !gap.Trim().IsEmpty)
                output.Append(gap);
            try
            {
                var bytes = word.Groups[2].Value.Equals("b", StringComparison.OrdinalIgnoreCase)
                    ? Convert.FromBase64String(word.Groups[3].Value)
                    : DecodeQuotedPrintable(word.Groups[3].Value.Replace('_', ' '));
                output.Append(DecodeBytes(bytes, word.Groups[1].Value.Split('*', 2)[0]));
            }
            catch (FormatException)
            {
                output.Append(word.Value);
            }
            start = word.Index + word.Length;
        }
        return output.Append(raw.AsSpan(start)).ToString();
    }

    private static byte[] DecodeTransfer(string raw, string transfer)
    {
        if (transfer.Trim().Equals("base64", StringComparison.OrdinalIgnoreCase))
        {
            try { return Convert.FromBase64String(raw); }
            catch (FormatException) { return Encoding.Latin1.GetBytes(raw); }
        }

        return transfer.Trim().Equals("quoted-printable", StringComparison.OrdinalIgnoreCase)
            ? DecodeQuotedPrintable(raw) : Encoding.Latin1.GetBytes(raw);
    }

    private static byte[] DecodeQuotedPrintable(string raw)
    {
        var bytes = new byte[raw.Length];
        int count = 0;
        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i] == '=' && i + 1 < raw.Length)
            {
                if (raw[i + 1] == '\n')
                {
                    i++;
                    continue;
                }
                if (i + 2 < raw.Length && Uri.IsHexDigit(raw[i + 1]) && Uri.IsHexDigit(raw[i + 2]))
                {
                    bytes[count++] = (byte)(16 * Uri.FromHex(raw[i + 1]) + Uri.FromHex(raw[i + 2]));
                    i += 2;
                    continue;
                }
            }
            bytes[count++] = (byte)raw[i];
        }
        return bytes.AsSpan(0, count).ToArray();
    }

    private static string DecodeBytes(byte[] bytes, string? charset)
    {
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try { return Encoding.GetEncoding(charset).GetString(bytes); }
            catch (ArgumentException) { }
        }
        if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe }))
            return Encoding.Unicode.GetString(bytes);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff }))
            return Encoding.BigEndianUnicode.GetString(bytes);
        try { return StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { return Encoding.GetEncoding(1251).GetString(bytes); }
    }

    private static string ReadHtml(string html, LinkCollector links)
    {
        var output = new StringBuilder();
        int offset = 0;
        foreach (Match token in HtmlToken.Matches(html))
        {
            output.Append(WebUtility.HtmlDecode(html[offset..token.Index]));
            offset = token.Index + token.Length;

            var name = token.Groups[2].Value.ToLowerInvariant();
            if (name is "br" or "p" or "div" or "li" or "tr" or "hr" or "section" or "article" or "blockquote"
                or "h1" or "h2" or "h3" or "h4" or "h5" or "h6")
                output.Append('\n');
            if (name is "td" or "th")
                output.Append(' ');
            if (name is not ("a" or "area"))
                continue;

            foreach (Match attribute in Attribute.Matches(token.Groups[3].Value))
            {
                if (!attribute.Groups[1].Value.Equals("href", StringComparison.OrdinalIgnoreCase))
                    continue;
                var href = attribute.Groups.Cast<Group>().Skip(2).FirstOrDefault(group => group.Success)?.Value;
                if (href is not null)
                    links.Add(WebUtility.HtmlDecode(href).Trim());
                break;
            }
        }
        output.Append(WebUtility.HtmlDecode(html[offset..]));
        var compact = Regex.Replace(output.ToString(), @"[ \t]+\n|\n[ \t]+", "\n", RegexOptions.None, RegexTimeout);
        return Regex.Replace(compact, @"\n{3,}", "\n\n", RegexOptions.None, RegexTimeout);
    }

    private static readonly HashSet<string> PreviewTags = Words(
        "div span p table thead tbody tfoot tr td th colgroup col caption ul ol li " +
        "br hr b strong i em u s a blockquote pre code center h1 h2 h3 h4 h5 h6 img font small sup sub");
    private static readonly HashSet<string> LayoutAttributes = Words(
        "class id width height align valign bgcolor border cellpadding cellspacing colspan rowspan color size face dir");
    private static readonly HashSet<string> LayoutProperties = Words(
        "display visibility opacity overflow overflow-x overflow-y color background background-color width height " +
        "max-width max-height min-width min-height padding padding-top padding-bottom padding-left padding-right " +
        "margin margin-top margin-bottom margin-left margin-right text-align vertical-align font font-family font-size " +
        "font-weight font-style line-height letter-spacing text-decoration white-space word-break word-wrap " +
        "border border-top border-left border-right border-bottom border-width border-color border-style border-radius " +
        "border-collapse border-spacing table-layout list-style-type");
    private static readonly Regex StyleElement = Pattern(@"<style\b[^>]*>([\s\S]*?)</style\s*>");
    private static readonly Regex StyleRule = Pattern(@"([^{}]+)\{([^{}]*)\}");
    private static readonly Regex Selector = Pattern(@"\A[a-z0-9_.*#,\s>+-]{1,240}\z");
    private static readonly Regex LiteralCss = Pattern("""\A[a-z0-9#.%,'"\s+-]{1,256}\z""");
    private static readonly Regex ColorCss = Pattern(@"\Argba?\(\s*[\d.%]+(?:\s*,\s*[\d.%]+){2,3}\s*\)\z");
    private static readonly Regex EmbeddedRaster = Pattern(@"\Adata:image/(?:png|jpeg|gif|webp|bmp|x-icon);base64,[a-z0-9+/=\s]+\z");
    private static HashSet<string> Words(string value) => new(value.Split(' '), StringComparer.OrdinalIgnoreCase);

    private static string PreviewStyle(string css)
    {
        var result = new StringBuilder();
        foreach (string declaration in css.Split(';'))
        {
            int colon = declaration.IndexOf(':');
            if (colon <= 0) continue;
            string property = declaration[..colon].Trim().ToLowerInvariant();
            string value = declaration[(colon + 1)..].Trim();
            bool important = value.EndsWith("!important", StringComparison.OrdinalIgnoreCase);
            if (important) value = value[..^10].TrimEnd();
            if (!LayoutProperties.Contains(property) || !(LiteralCss.IsMatch(value) || ColorCss.IsMatch(value))) continue;
            result.Append(property).Append(':').Append(value).Append(important ? "!important;" : ";");
        }
        return result.ToString();
    }

    internal static string PreviewHtml(string? html)
    {
        var page = new StringBuilder("<!doctype html><html><head><meta http-equiv=\"X-UA-Compatible\" content=\"IE=edge\"><style>" +
            "html,body{background:#fff;color:#171717;margin:0}body{padding:12px;font:15px 'Segoe UI',Arial,sans-serif}img{max-width:100%}a{color:#225ea8}");
        html ??= "";
        foreach (Match sheet in StyleElement.Matches(html))
        {
            foreach (Match rule in StyleRule.Matches(sheet.Groups[1].Value))
            {
                string selector = rule.Groups[1].Value.Trim();
                if (!Selector.IsMatch(selector)) continue;
                page.Append(selector).Append('{').Append(PreviewStyle(rule.Groups[2].Value)).Append('}');
            }
        }
        page.Append("</style></head><body>");
        int offset = 0;
        void Text(string value) => page.Append(WebUtility.HtmlEncode(WebUtility.HtmlDecode(value)));
        foreach (Match token in HtmlToken.Matches(html))
        {
            Text(html[offset..token.Index]);
            offset = token.Index + token.Length;
            var name = token.Groups[2].Value.ToLowerInvariant();
            if (name == "body") name = "div";
            if (!PreviewTags.Contains(name)) continue;
            if (token.Value.StartsWith("</", StringComparison.Ordinal))
            {
                page.Append("</").Append(name).Append('>');
                continue;
            }
            page.Append('<').Append(name);
            bool hidden = false;
            foreach (Match attr in Attribute.Matches(token.Groups[3].Value))
            {
                string key = attr.Groups[1].Value.ToLowerInvariant();
                if (key == "hidden") { hidden = true; continue; }
                var raw = attr.Groups.Cast<Group>().Skip(2).FirstOrDefault(g => g.Success)?.Value;
                if (raw is null) continue;
                string value = WebUtility.HtmlDecode(raw);
                if (key == "style") value = PreviewStyle(value);
                else if (name == "img" && key == "src")
                {
                    if (value.Length > 4 * 1024 * 1024 || !EmbeddedRaster.IsMatch(value)) continue;
                }
                else if (!(LayoutAttributes.Contains(key) && LiteralCss.IsMatch(value)) && !(key == "alt" && name == "img")) continue;
                page.Append(' ').Append(key).Append("=\"").Append(WebUtility.HtmlEncode(value)).Append('"');
            }
            if (hidden) page.Append(" hidden=\"hidden\"");
            page.Append('>');
        }
        Text(html[offset..]);
        return page.Append("</body></html>").ToString();
    }

    private static Regex Pattern(string pattern) =>
        new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);

    private sealed class LinkCollector
    {
        private readonly HashSet<string> seen = new(StringComparer.Ordinal);
        public List<string> Items { get; } = [];
        public Dictionary<string, string> Images { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void AddText(string body)
        {
            foreach (Match match in TextUrl.Matches(body))
            {
                var url = match.Value.TrimEnd('.', ',', ';', ':', '!', '?', '»', '”', '’', '…');
                while (url.Length > 0 && (Unbalanced(url, '(', ')') || Unbalanced(url, '[', ']') || Unbalanced(url, '{', '}')))
                    url = url[..^1];
                Add(url);
            }
        }

        public void Add(string url)
        {
            var candidate = url.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "https://" + url
                : url.StartsWith("//", StringComparison.Ordinal) ? "https:" + url : url;
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https" or "ftp" or "mailto"))
                return;
            if (parsed.Host.Length > 0 && seen.Add(url))
                Items.Add(url);
        }

        private static bool Unbalanced(string url, char open, char close) =>
            url[^1] == close && url.Count(c => c == close) > url.Count(c => c == open);
    }
}
