using System.Text.Json.Serialization;

namespace PyQsirchgui.Windows.Models;

public sealed class AppConfig
{
    [JsonPropertyName("host")]
    public string Host { get; set; } = "";

    [JsonPropertyName("port")]
    public int Port { get; set; } = 8080;

    [JsonPropertyName("ssl")]
    public bool Ssl { get; set; }

    [JsonPropertyName("ssl_verify")]
    public bool SslVerify { get; set; }

    [JsonPropertyName("user")]
    public string User { get; set; } = "";

    [JsonPropertyName("password")]
    public string Password { get; set; } = "";

    [JsonPropertyName("path_mappings")]
    public List<PathMapping> PathMappings { get; set; } = [];

    [JsonPropertyName("behavior")]
    public BehaviorConfig Behavior { get; set; } = new();

    [JsonPropertyName("history")]
    public HistoryConfig History { get; set; } = new();
}

public sealed class PathMapping
{
    [JsonPropertyName("share_root")]
    public string ShareRoot { get; set; } = "";

    [JsonPropertyName("mapped_root")]
    public string MappedRoot { get; set; } = "";
}

public sealed class BehaviorConfig
{
    [JsonPropertyName("folders_first")]
    public bool FoldersFirst { get; set; } = true;

    [JsonPropertyName("result_view")]
    public string ResultView { get; set; } = "details";
}

public sealed class HistoryConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("file")]
    public string File { get; set; } = "history.json";
}
