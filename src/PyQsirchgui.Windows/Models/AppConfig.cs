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

    [JsonPropertyName("pinned_tabs")]
    public List<PinnedTabConfig> PinnedTabs { get; set; } = [];

    [JsonPropertyName("hosts")]
    public Dictionary<string, HostConfig> Hosts { get; set; } = [];

    public static string CurrentHostKey => Environment.MachineName.ToUpperInvariant();

    public void ApplyCurrentHost()
    {
        NormalizeRules(Exclude, global: true);
        foreach (var rule in VisibilityRules)
        {
            rule.IsGlobal = true;
        }

        if (!Hosts.TryGetValue(CurrentHostKey, out var host))
        {
            Exclude.Folders = Exclude.FolderRules.Select(x => x.Pattern).ToList();
            Exclude.Files = Exclude.FileRules.Select(x => x.Pattern).ToList();
            return;
        }

        var globalExclude = CloneExclude(Exclude);
        var globalVisibility = VisibilityRules.Select(CloneVisibilityRule).ToList();

        Host = host.Host;
        Port = host.Port;
        Ssl = host.Ssl;
        SslVerify = host.SslVerify;
        User = host.User;
        Password = host.Password;
        PathMappings = host.PathMappings.Select(ClonePathMapping).ToList();
        Behavior = CloneBehavior(host.Behavior);
        History = CloneHistory(host.History);
        AlwaysOnTop = host.AlwaysOnTop;
        PinnedTabs = host.PinnedTabs.Select(ClonePinnedTab).ToList();

        NormalizeRules(host.Exclude, global: false);
        Exclude = new ExcludeConfig
        {
            FolderRules = globalExclude.FolderRules.Where(x => x.IsGlobal).Select(CloneTextRule).Concat(host.Exclude.FolderRules.Where(x => !x.IsGlobal).Select(CloneTextRule)).ToList(),
            FileRules = globalExclude.FileRules.Where(x => x.IsGlobal).Select(CloneTextRule).Concat(host.Exclude.FileRules.Where(x => !x.IsGlobal).Select(CloneTextRule)).ToList(),
        };
        Exclude.Folders = Exclude.FolderRules.Select(x => x.Pattern).ToList();
        Exclude.Files = Exclude.FileRules.Select(x => x.Pattern).ToList();

        VisibilityRules = globalVisibility.Where(x => x.IsGlobal).Select(CloneVisibilityRule)
            .Concat(host.VisibilityRules.Where(x => !x.IsGlobal).Select(CloneVisibilityRule))
            .ToList();
    }

    public void CaptureCurrentHost()
    {
        NormalizeRules(Exclude, global: false);
        var globalExclude = new ExcludeConfig
        {
            FolderRules = Exclude.FolderRules.Where(x => x.IsGlobal).Select(CloneTextRule).ToList(),
            FileRules = Exclude.FileRules.Where(x => x.IsGlobal).Select(CloneTextRule).ToList(),
        };
        globalExclude.Folders = globalExclude.FolderRules.Select(x => x.Pattern).ToList();
        globalExclude.Files = globalExclude.FileRules.Select(x => x.Pattern).ToList();

        var localExclude = new ExcludeConfig
        {
            FolderRules = Exclude.FolderRules.Where(x => !x.IsGlobal).Select(CloneTextRule).ToList(),
            FileRules = Exclude.FileRules.Where(x => !x.IsGlobal).Select(CloneTextRule).ToList(),
        };
        localExclude.Folders = localExclude.FolderRules.Select(x => x.Pattern).ToList();
        localExclude.Files = localExclude.FileRules.Select(x => x.Pattern).ToList();

        Hosts[CurrentHostKey] = new HostConfig
        {
            Host = Host,
            Port = Port,
            Ssl = Ssl,
            SslVerify = SslVerify,
            User = User,
            Password = Password,
            PathMappings = PathMappings.Select(ClonePathMapping).ToList(),
            Behavior = CloneBehavior(Behavior),
            History = CloneHistory(History),
            Exclude = localExclude,
            VisibilityRules = VisibilityRules.Where(x => !x.IsGlobal).Select(CloneVisibilityRule).ToList(),
            AlwaysOnTop = AlwaysOnTop,
            PinnedTabs = PinnedTabs.Select(ClonePinnedTab).ToList(),
        };

        ClearRootMachineSettings();
        Exclude = globalExclude;
        VisibilityRules = VisibilityRules.Where(x => x.IsGlobal).Select(CloneVisibilityRule).ToList();
    }

    public void ClearRootMachineSettings()
    {
        // The root NAS endpoint is a shared deployment default. Other live settings belong to a host record.
        User = "";
        Password = "";
        PathMappings = [];
        Behavior = new BehaviorConfig();
        History = new HistoryConfig();
        AlwaysOnTop = true;
        PinnedTabs = [];
    }

    private static void NormalizeRules(ExcludeConfig exclude, bool global)
    {
        if (exclude.FolderRules.Count == 0 && exclude.Folders.Count > 0)
        {
            exclude.FolderRules = exclude.Folders.Select(x => new ScopedTextRule { Pattern = x, IsGlobal = global }).ToList();
        }
        if (exclude.FileRules.Count == 0 && exclude.Files.Count > 0)
        {
            exclude.FileRules = exclude.Files.Select(x => new ScopedTextRule { Pattern = x, IsGlobal = global }).ToList();
        }
        exclude.Folders = exclude.FolderRules.Select(x => x.Pattern).ToList();
        exclude.Files = exclude.FileRules.Select(x => x.Pattern).ToList();
    }

    private static PathMapping ClonePathMapping(PathMapping mapping) => new() { ShareRoot = mapping.ShareRoot, MappedRoot = mapping.MappedRoot };
    private static ScopedTextRule CloneTextRule(ScopedTextRule rule) => new() { Pattern = rule.Pattern, IsGlobal = rule.IsGlobal };
    private static VisibilityRule CloneVisibilityRule(VisibilityRule rule) => new() { Access = rule.Access, Identity = rule.Identity, Pattern = rule.Pattern, IsGlobal = rule.IsGlobal };
    private static PinnedTabConfig ClonePinnedTab(PinnedTabConfig tab) => new() { Title = tab.Title, Query = tab.Query, ViewKey = tab.ViewKey, SortValue = tab.SortValue, TypeIndex = tab.TypeIndex };
    private static ExcludeConfig CloneExclude(ExcludeConfig exclude) => new()
    {
        Folders = exclude.Folders.ToList(),
        Files = exclude.Files.ToList(),
        FolderRules = exclude.FolderRules.Select(CloneTextRule).ToList(),
        FileRules = exclude.FileRules.Select(CloneTextRule).ToList(),
    };
    private static BehaviorConfig CloneBehavior(BehaviorConfig behavior) => new()
    {
        ShowInTaskbar = behavior.ShowInTaskbar,
        MinimizeToTray = behavior.MinimizeToTray,
        ClearResultsWithQuery = behavior.ClearResultsWithQuery,
        GlobalHotkey = behavior.GlobalHotkey,
        Theme = behavior.Theme,
        HighlightMatches = behavior.HighlightMatches,
        UseQsirchThumbnails = behavior.UseQsirchThumbnails,
        SearchContents = behavior.SearchContents,
        PreviewPane = behavior.PreviewPane,
        AllowDownload = behavior.AllowDownload,
        FoldersFirst = behavior.FoldersFirst,
        ResultView = behavior.ResultView,
        ResultSort = behavior.ResultSort,
        VisibleDetailColumns = behavior.VisibleDetailColumns.ToList(),
        SearchTimeoutSeconds = behavior.SearchTimeoutSeconds,
        FirstPageSize = behavior.FirstPageSize,
        NextPageSize = behavior.NextPageSize,
        MaxSearchResults = behavior.MaxSearchResults,
        RefreshCacheOnStartup = behavior.RefreshCacheOnStartup,
    };
    private static HistoryConfig CloneHistory(HistoryConfig history) => new()
    {
        Enabled = history.Enabled,
        File = history.File,
        MaxEntries = history.MaxEntries,
        SourceFilter = history.SourceFilter,
    };
}

public sealed class HostConfig
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
    public ExcludeConfig Exclude { get; set; } = new() { Folders = [], Files = [] };

    [JsonPropertyName("visibility_rules")]
    public List<VisibilityRule> VisibilityRules { get; set; } = [];

    [JsonPropertyName("always_on_top")]
    public bool AlwaysOnTop { get; set; } = true;

    [JsonPropertyName("pinned_tabs")]
    public List<PinnedTabConfig> PinnedTabs { get; set; } = [];
}

public sealed class PinnedTabConfig
{
    [JsonPropertyName("title")]
    public string Title { get; set; } = "Search";

    [JsonPropertyName("query")]
    public string Query { get; set; } = "";

    [JsonPropertyName("view")]
    public string ViewKey { get; set; } = "details";

    [JsonPropertyName("sort")]
    public string SortValue { get; set; } = "recent:desc";

    [JsonPropertyName("type_index")]
    public int TypeIndex { get; set; }
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

    [JsonPropertyName("minimize_to_tray")]
    public bool MinimizeToTray { get; set; }

    [JsonPropertyName("clear_results_with_query")]
    public bool ClearResultsWithQuery { get; set; }

    [JsonPropertyName("global_hotkey")]
    public string GlobalHotkey { get; set; } = "Ctrl+Space";

    [JsonPropertyName("theme")]
    public string Theme { get; set; } = "system";

    [JsonPropertyName("highlight_matches")]
    public bool HighlightMatches { get; set; } = true;

    [JsonPropertyName("use_qsirch_thumbnails")]
    public bool UseQsirchThumbnails { get; set; }

    [JsonPropertyName("search_contents")]
    public bool SearchContents { get; set; }

    [JsonPropertyName("preview_pane")]
    public bool PreviewPane { get; set; }

    [JsonPropertyName("allow_download")]
    public bool AllowDownload { get; set; }

    [JsonPropertyName("folders_first")]
    public bool FoldersFirst { get; set; } = true;

    [JsonPropertyName("result_view")]
    public string ResultView { get; set; } = "details";

    [JsonPropertyName("result_sort")]
    public string ResultSort { get; set; } = "recent";

    [JsonPropertyName("visible_detail_columns")]
    public List<string> VisibleDetailColumns { get; set; } = ["location", "name", "modified", "size", "type"];

    [JsonPropertyName("search_timeout_seconds")]
    public int SearchTimeoutSeconds { get; set; } = 90;

    [JsonPropertyName("first_page_size")]
    public int FirstPageSize { get; set; } = 25;

    [JsonPropertyName("next_page_size")]
    public int NextPageSize { get; set; } = 100;

    [JsonPropertyName("max_search_results")]
    public int MaxSearchResults { get; set; } = 500;

    [JsonPropertyName("refresh_cache_on_startup")]
    public bool RefreshCacheOnStartup { get; set; }
}

public sealed class HistoryConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("file")]
    public string File { get; set; } = "data\\history.json";

    [JsonPropertyName("max_entries")]
    public int MaxEntries { get; set; } = 20000;

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

    [JsonPropertyName("folder_rules")]
    public List<ScopedTextRule> FolderRules { get; set; } = [];

    [JsonPropertyName("file_rules")]
    public List<ScopedTextRule> FileRules { get; set; } = [];
}

public sealed class ScopedTextRule
{
    [JsonPropertyName("pattern")]
    public string Pattern { get; set; } = "";

    [JsonPropertyName("global")]
    public bool IsGlobal { get; set; }
}

public sealed class VisibilityRule
{
    [JsonPropertyName("access")]
    public string Access { get; set; } = "deny";

    [JsonPropertyName("identity")]
    public string Identity { get; set; } = "*";

    [JsonPropertyName("pattern")]
    public string Pattern { get; set; } = "";

    [JsonPropertyName("global")]
    public bool IsGlobal { get; set; }
}
