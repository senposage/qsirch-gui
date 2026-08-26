using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using PyQsirchgui.Windows.Models;

namespace PyQsirchgui.Windows.Services;

public sealed class QsirchClient(AppConfig config) : IDisposable
{
    private readonly HttpClient _http = CreateHttpClient(config);
    private readonly string _baseUrl = $"{(config.Ssl ? "https" : "http")}://{config.Host}:{config.Port}";
    private bool _loggedIn;

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, FileTypeFilter typeFilter, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(config.Host) || string.IsNullOrWhiteSpace(config.User) || string.IsNullOrWhiteSpace(config.Password))
        {
            throw new InvalidOperationException("Configure the NAS connection in config.json first.");
        }

        await LoginAsync(cancellationToken);
        var url = $"{_baseUrl}/qsirch/latest/api/search?q={Uri.EscapeDataString(query)}&limit=100&offset=0&advanced_mode=0";
        using var request = new HttpRequestMessage(typeFilter.Category.Equals("All", StringComparison.OrdinalIgnoreCase) ? HttpMethod.Get : HttpMethod.Post, url);
        if (!typeFilter.Category.Equals("All", StringComparison.OrdinalIgnoreCase))
        {
            request.Content = new StringContent(JsonSerializer.Serialize(new { tools = typeFilter.Category, limit = 100 }), Encoding.UTF8, "application/json");
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
        var sid = xml.Root?.Element("authSid")?.Value;
        if (string.IsNullOrWhiteSpace(sid))
        {
            throw new InvalidOperationException("QNAP did not return an authSid.");
        }
        _http.DefaultRequestHeaders.Remove("Cookie");
        _http.DefaultRequestHeaders.Add("Cookie", $"NAS_SID={sid}");
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
