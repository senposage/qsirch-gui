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

    [JsonPropertyName("exclude")]
    public ExcludeConfig Exclude { get; set; } = new();

    [JsonPropertyName("visibility_rules")]
    public List<VisibilityRule> VisibilityRules { get; set; } = [];

    [JsonPropertyName("always_on_top")]
    public bool AlwaysOnTop { get; set; } = true;
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
    [JsonPropertyName("show_in_taskbar")]
    public bool ShowInTaskbar { get; set; } = true;

    [JsonPropertyName("global_hotkey")]
    public string GlobalHotkey { get; set; } = "Ctrl+Space";

    [JsonPropertyName("theme")]
    public string Theme { get; set; } = "system";

    [JsonPropertyName("highlight_matches")]
    public bool HighlightMatches { get; set; } = true;

    [JsonPropertyName("preview_pane")]
    public bool PreviewPane { get; set; }

    [JsonPropertyName("standard_window")]
    public bool StandardWindow { get; set; } = true;

    [JsonPropertyName("allow_download")]
    public bool AllowDownload { get; set; }

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

    [JsonPropertyName("max_entries")]
    public int MaxEntries { get; set; } = 200;

    [JsonPropertyName("source_filter")]
    public string SourceFilter { get; set; } = "__this__";
}

public sealed class ExcludeConfig
{
    [JsonPropertyName("folders")]
    public List<string> Folders { get; set; } =
    [
        "@Recently-Snapshot\\*",
        "@Recycle\\*",
        "#recycle\\*",
        ".sync\\*",
        ".qsync\\*",
        ".qsync_sn\\*",
    ];

    [JsonPropertyName("files")]
    public List<string> Files { get; set; } =
    [
        "Thumbs.db",
        "desktop.ini",
        "*.tmp",
        "~$*",
        ".DS_Store",
        "*.qsync",
        "*.qsync_tmp",
        "*.syncing",
        "*_conflict_*",
        "*conflicted copy*",
    ];
}

public sealed class VisibilityRule
{
    [JsonPropertyName("access")]
    public string Access { get; set; } = "deny";

    [JsonPropertyName("identity")]
    public string Identity { get; set; } = "*";

    [JsonPropertyName("pattern")]
    public string Pattern { get; set; } = "";
}
