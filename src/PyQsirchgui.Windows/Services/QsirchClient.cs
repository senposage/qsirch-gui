using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using PyQsirchgui.Windows.Models;

namespace PyQsirchgui.Windows.Services;

public sealed class QsirchClient(AppConfig config) : IDisposable
{
    private readonly HttpClient _http = CreateHttpClient(config);
    private readonly string _baseUrl = $"{(config.Ssl ? "https" : "http")}://{config.Host}:{config.Port}";
    private bool _loggedIn;
    private string _sid = "";

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, FileTypeFilter typeFilter, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(config.Host) || string.IsNullOrWhiteSpace(config.User) || string.IsNullOrWhiteSpace(config.Password))
        {
            throw new InvalidOperationException("Configure the NAS connection in config.json first.");
        }

        await LoginAsync(cancellationToken);
        const int limit = 500;
        var url = $"{_baseUrl}/qsirch/latest/api/search?q={Uri.EscapeDataString(query)}&limit={limit}&offset=0&advanced_mode=0";
        using var request = new HttpRequestMessage(typeFilter.Category.Equals("All", StringComparison.OrdinalIgnoreCase) ? HttpMethod.Get : HttpMethod.Post, url);
        if (!typeFilter.Category.Equals("All", StringComparison.OrdinalIgnoreCase))
        {
            request.Content = new StringContent(JsonSerializer.Serialize(new { tools = typeFilter.Category, limit }), Encoding.UTF8, "application/json");
        }

        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var results = new List<SearchResult>();
        if (!doc.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return results;
        }

        foreach (var element in items.EnumerateArray())
        {
            var result = ResultFromJson(element.Clone());
            if (typeFilter.Extensions.Length > 0 && !typeFilter.Extensions.Contains(result.Extension, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }
            results.Add(result);
        }
        return results;
    }

    public async Task<QsirchPreview> PreviewAsync(SearchResult result, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(config.Host) || string.IsNullOrWhiteSpace(config.User) || string.IsNullOrWhiteSpace(config.Password))
        {
            throw new InvalidOperationException("Configure the NAS connection in Settings first.");
        }

        await LoginAsync(cancellationToken);
        var action = PreviewAction(result);
        using var response = await _http.GetAsync(action, cancellationToken);
        response.EnsureSuccessStatusCode();
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (contentType.Contains("html", StringComparison.OrdinalIgnoreCase) || text.TrimStart().StartsWith("<", StringComparison.Ordinal))
        {
            return new QsirchPreview { Summary = NormalizeWhitespace(StripHtml(text)) };
        }
        using var doc = JsonDocument.Parse(text);
        return PreviewFromJson(doc.RootElement, action);
    }

    public async Task<byte[]?> ThumbnailAsync(SearchResult result, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(config.Host) || string.IsNullOrWhiteSpace(config.User) || string.IsNullOrWhiteSpace(config.Password))
        {
            return null;
        }

        await LoginAsync(cancellationToken);
        var action = ThumbnailAction(result);
        if (string.IsNullOrWhiteSpace(action))
        {
            return null;
        }

        using var response = await _http.GetAsync(action, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
        if (!contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    private async Task LoginAsync(CancellationToken cancellationToken)
    {
        if (_loggedIn)
        {
            return;
        }

        var encodedPassword = Convert.ToBase64String(Encoding.UTF8.GetBytes(config.Password));
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["user"] = config.User,
            ["pwd"] = encodedPassword,
        });
        using var response = await _http.PostAsync($"{_baseUrl}/cgi-bin/authLogin.cgi", content, cancellationToken);
        response.EnsureSuccessStatusCode();
        var xml = XDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (xml.Root?.Element("authPassed")?.Value != "1")
        {
            throw new InvalidOperationException("QNAP authentication failed.");
        }
        _sid = xml.Root?.Element("authSid")?.Value ?? "";
        if (string.IsNullOrWhiteSpace(_sid))
        {
            throw new InvalidOperationException("QNAP did not return an authSid.");
        }
        _http.DefaultRequestHeaders.Remove("Cookie");
        _http.DefaultRequestHeaders.Add("Cookie", $"NAS_SID={_sid}");
        _loggedIn = true;
    }

    public static SearchResult ResultFromJson(JsonElement element)
    {
        var name = GetString(element, "name");
        var ext = GetString(element, "extension").TrimStart('.');
        var path = GetPath(element);
        var type = GetString(element, "type");
        var result = new SearchResult
        {
            Name = name,
            Extension = ext,
            Path = path,
            Type = type,
            Size = GetLong(element, "size"),
            Modified = GetString(element, "modified"),
            Raw = element,
        };
        result.IsFolder = IsFolder(result, element);
        return result;
    }

    private static HttpClient CreateHttpClient(AppConfig config)
    {
        var handler = new HttpClientHandler { CookieContainer = new CookieContainer() };
        if (config.Ssl && !config.SslVerify)
        {
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
    }

    private string PreviewAction(SearchResult result)
    {
        if (result.Raw.ValueKind == JsonValueKind.Object &&
            result.Raw.TryGetProperty("actions", out var actions) &&
            actions.TryGetProperty("preview", out var preview) &&
            preview.ValueKind != JsonValueKind.Null &&
            !string.IsNullOrWhiteSpace(preview.ToString()))
        {
            var action = preview.ToString() ?? "";
            return action.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? action : _baseUrl + action;
        }

        var path = Uri.EscapeDataString(result.Path);
        var name = Uri.EscapeDataString(result.Name);
        return $"{_baseUrl}/qsirch/latest/api/qusion-item?action=preview&path={path}&name={name}&app_id=badguy";
    }

    private string ThumbnailAction(SearchResult result)
    {
        if (result.Raw.ValueKind != JsonValueKind.Object ||
            !result.Raw.TryGetProperty("actions", out var actions) ||
            !actions.TryGetProperty("thumbnail", out var thumbnail) ||
            thumbnail.ValueKind == JsonValueKind.Null ||
            string.IsNullOrWhiteSpace(thumbnail.ToString()))
        {
            return "";
        }

        var action = thumbnail.ToString() ?? "";
        return action.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? action : _baseUrl + action;
    }

    private QsirchPreview PreviewFromJson(JsonElement data, string actionUrl)
    {
        var summary = PreviewSummary(data);
        var container = GetString(data, "container_type");
        if (string.IsNullOrWhiteSpace(container))
        {
            container = GetString(data, "type");
        }
        return new QsirchPreview
        {
            Summary = summary,
            IsOnlineViewer = container.Equals("online_viewer", StringComparison.OrdinalIgnoreCase),
        };
    }

    private static string PreviewSummary(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object)
        {
            return "";
        }

        var lines = new List<string>();
        var container = GetString(data, "container_type");
        if (string.IsNullOrWhiteSpace(container))
        {
            container = GetString(data, "type");
        }
        if (!string.IsNullOrWhiteSpace(container))
        {
            lines.Add($"Preview type: {container}");
        }

        foreach (var key in new[] { "title", "subject", "from", "to", "date", "modified", "created" })
        {
            var value = GetString(data, key);
            if (!string.IsNullOrWhiteSpace(value))
            {
                lines.Add($"{char.ToUpperInvariant(key[0]) + key[1..]}: {value}");
            }
        }

        var body = StripHtml(GetString(data, "html"));
        if (string.IsNullOrWhiteSpace(body))
        {
            body = GetString(data, "text");
        }
        if (string.IsNullOrWhiteSpace(body))
        {
            body = GetString(data, "content");
        }
        body = NormalizeWhitespace(body);
        if (!string.IsNullOrWhiteSpace(body))
        {
            lines.Add("");
            lines.Add(body.Length > 1800 ? body[..1800] : body);
        }

        if (data.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in info.EnumerateArray().Take(12))
            {
                var key = GetString(entry, "key");
                var value = GetString(entry, "value");
                if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
                {
                    lines.Add($"{key}: {value}");
                }
            }
        }

        return string.Join(Environment.NewLine, lines).Trim();
    }

    private static string StripHtml(string value)
    {
        return Regex.Replace(value ?? "", "<[^>]+>", " ");
    }

    private static string NormalizeWhitespace(string value)
    {
        return Regex.Replace(value ?? "", "\\s+", " ").Trim();
    }

    private static bool IsFolder(SearchResult result, JsonElement element)
    {
        var metadata = new[] { result.Type, GetString(element, "kind"), GetString(element, "file_type"), GetString(element, "container_type") };
        if (metadata.Any(x => x.Equals("folder", StringComparison.OrdinalIgnoreCase) || x.Equals("directory", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }
        if (!string.IsNullOrWhiteSpace(result.Extension))
        {
            return false;
        }
        return result.Path.EndsWith('\\') || result.Path.EndsWith('/') || (!string.IsNullOrWhiteSpace(result.Name) && !result.Name.Contains('.'));
    }

    private static string GetPath(JsonElement element)
    {
        if (element.TryGetProperty("preview", out var preview) &&
            preview.TryGetProperty("info", out var info) &&
            info.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in info.EnumerateArray())
            {
                if (GetString(item, "key").Equals("path", StringComparison.OrdinalIgnoreCase))
                {
                    return GetString(item, "value");
                }
            }
        }
        return GetString(element, "path");
    }

    private static string GetString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null ? value.ToString() ?? "" : "";
    }

    private static long GetLong(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return 0;
        }
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
        {
            return number;
        }
        return long.TryParse(value.ToString(), out var parsed) ? parsed : 0;
    }

    public void Dispose() => _http.Dispose();
}

public sealed class QsirchPreview
{
    public string Summary { get; init; } = "";
    public bool IsOnlineViewer { get; init; }
}
