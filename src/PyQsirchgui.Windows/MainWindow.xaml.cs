using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PyQsirchgui.Windows.Models;
using PyQsirchgui.Windows.Services;

namespace PyQsirchgui.Windows;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly AppConfig _config = ConfigStore.Load();
    private readonly BulkObservableCollection<SearchResult> _allResults = [];
    private readonly HistoryStore _history;
    private readonly PathMapper _mapper;
    private readonly ShellActions _shell;
    private TrayIconService? _trayIcon;
    private ResultRules _rules;
    private string _query = "";
    private string _statusText = "Ready";
    private string _countText = "";
    private string _resultLocationText = "";
    private string _sortSummaryText = "Recentness desc";
    private string _previewText = "Select a result to preview.";
    private bool _isBusy;
    private bool _initializing = true;
    private bool _loadingTab;
    private int _viewRefreshVersion;
    private double _iconGlyphSize = 54;
    private double _iconGlyphFontSize = 44;
    private double _iconTileWidth = 150;
    private double _iconTileHeight = 118;
    private string _sortColumn = "recent";
    private bool _sortDescending = true;
    private List<ResultSortKey> _sortKeys = [new("recent", true)];
    private CancellationTokenSource? _paintCts;
    private bool _favoritesVisible = true;
    private GridLength _favoritesWidth = new(190);
    private GridLength _previewWidth = new(320);
    private readonly Dictionary<string, ImageSource> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _iconLoadsInFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _iconLoadGate = new(2);
    private readonly Dictionary<string, GridViewColumn> _detailColumns = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _detailColumnWidths = new(StringComparer.OrdinalIgnoreCase)
    {
        ["location"] = 330,
        ["name"] = 280,
        ["modified"] = 150,
        ["size"] = 80,
        ["type"] = 90,
    };
    private SearchTabState? _selectedSearchTab;
    private bool _isExiting;

    public MainWindow()
    {
        InitializeComponent();
        StateChanged += (_, _) => UpdateCaptionButtons();
        StateChanged += MainWindowStateChanged;
        Closing += MainWindowClosing;
        Closed += (_, _) =>
        {
            _trayIcon?.Dispose();
            _trayIcon = null;
        };
        AppLogger.Info("app", "main window initializing");
        _history = new HistoryStore(_config);
        _mapper = new PathMapper(_config);
        _shell = new ShellActions(_mapper);
        _trayIcon = new TrayIconService(ShowFromTray, ExitApplication);
        _rules = new ResultRules(_config);
        RegisterDetailColumns();
        SetupDetailColumnMenu();
        SearchTabs.Add(new SearchTabState { Title = "Search 1", ViewKey = configuredViewOrDefault(), SortValue = configuredSortOrDefault() });
        foreach (var pinned in _config.PinnedTabs)
        {
            SearchTabs.Add(new SearchTabState
            {
                Title = pinned.Title,
                Query = pinned.Query,
                ViewKey = pinned.ViewKey,
                SortValue = pinned.SortValue,
                TypeIndex = pinned.TypeIndex,
                IsPinned = true,
                SearchOnFirstFocus = !string.IsNullOrWhiteSpace(pinned.Query),
            });
        }
        _selectedSearchTab = SearchTabs[0];
        DataContext = this;
        TypeBox.SelectedIndex = 0;
        var configuredView = configuredViewOrDefault();
        ViewBox.SelectedItem = ViewModes.FirstOrDefault(x => x.Key.Equals(configuredView, StringComparison.OrdinalIgnoreCase)) ?? ViewModes[^1];
        var configuredSort = configuredSortOrDefault();
        SortBox.SelectedItem = SortModes.FirstOrDefault(x => x.Key.Equals(configuredSort, StringComparison.OrdinalIgnoreCase)) ?? SortModes[0];
        ApplySortMode(configuredSort);
        ApplyTheme();
        ApplyViewMode();
        ApplyDetailColumnVisibility();
        ApplyBehavior();
        SearchContentsToggle.IsChecked = _config.Behavior.SearchContents;
        AppLogger.Info("app", $"main window ready config={ConfigStore.ConfigPath} log={AppLogger.LogPath} searchTimeout={Math.Clamp(_config.Behavior.SearchTimeoutSeconds, 15, 300)}s");
        if (_config.Behavior.RefreshCacheOnStartup)
        {
            _ = RefreshCacheInBackgroundAsync();
        }
        _initializing = false;
    }

    private void TitleBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximizeRestore();
            return;
        }

        try
        {
            DragMove();
        }
        catch
        {
        }
    }

    private void MinimizeClicked(object sender, RoutedEventArgs e)
    {
        if (_config.Behavior.MinimizeToTray)
        {
            HideToTray();
            return;
        }

        WindowState = WindowState.Minimized;
    }

    private void MaximizeRestoreClicked(object sender, RoutedEventArgs e)
    {
        ToggleMaximizeRestore();
    }

    private void CloseClicked(object sender, RoutedEventArgs e)
    {
        HideToTray();
    }

    private void ToggleMaximizeRestore()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        UpdateCaptionButtons();
    }

    private void UpdateCaptionButtons()
    {
        if (MaximizeButton == null)
        {
            return;
        }
        MaximizeButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        MaximizeButton.ToolTip = WindowState == WindowState.Maximized ? "Restore" : "Maximize";
    }

    public BulkObservableCollection<SearchResult> VisibleResults { get; } = [];
    public BulkObservableCollection<SearchResult> FavoriteResults { get; } = [];
    public ObservableCollection<SearchTabState> SearchTabs { get; } = [];

    public SearchTabState? SelectedSearchTab
    {
        get => _selectedSearchTab;
        set
        {
            if (ReferenceEquals(_selectedSearchTab, value) || value == null)
            {
                return;
            }
            SaveCurrentTabState();
            _selectedSearchTab = value;
            OnPropertyChanged();
            LoadTabState(value);
            if (value.SearchOnFirstFocus && !string.IsNullOrWhiteSpace(value.Query))
            {
                value.SearchOnFirstFocus = false;
                _ = SearchAsync();
            }
        }
    }

    public IReadOnlyList<FileTypeFilter> TypeFilters { get; } =
    [
        new() { Name = "All types", Category = "All" },
        new() { Name = "Word", Extensions = ["doc", "docx", "docm", "dot", "dotx", "rtf"] },
        new() { Name = "Excel", Extensions = ["xls", "xlsx", "xlsm", "xlsb", "csv"] },
        new() { Name = "PowerPoint", Extensions = ["ppt", "pptx", "pptm", "pps", "ppsx"] },
        new() { Name = "PDF", Extensions = ["pdf"] },
        new() { Name = "OneNote", Extensions = ["one"] },
        new() { Name = "Email", Category = "Email", Extensions = ["eml", "msg"] },
        new() { Name = "Text", Extensions = ["txt", "md", "log", "ini", "cfg"] },
        new() { Name = "Images", Extensions = ["jpg", "jpeg", "png", "gif", "bmp", "webp", "tif", "tiff"] },
        new() { Name = "Videos", Extensions = ["mp4", "mov", "mkv", "avi", "wmv", "m4v"] },
        new() { Name = "Music", Extensions = ["mp3", "wav", "flac", "m4a", "aac", "wma"] },
        new() { Name = "Archives", Extensions = ["zip", "7z", "rar", "tar", "gz"] },
        new() { Name = "Code", Extensions = ["py", "js", "ts", "html", "css", "sql", "ps1", "bat", "cmd"] },
    ];

    public IReadOnlyList<ResultViewMode> ViewModes { get; } =
    [
        new() { Name = "Large icons", Key = "large_icons" },
        new() { Name = "Small icons", Key = "small_icons" },
        new() { Name = "List", Key = "list" },
        new() { Name = "Details", Key = "details" },
    ];

    public IReadOnlyList<ResultSortMode> SortModes { get; } =
    [
        new() { Name = "Recentness", Key = "recent" },
        new() { Name = "Date modified", Key = "modified" },
        new() { Name = "Name", Key = "name" },
        new() { Name = "Location", Key = "location" },
        new() { Name = "Type", Key = "type" },
        new() { Name = "Size", Key = "size" },
    ];

    public string Query
    {
        get => _query;
        set
        {
            if (!SetField(ref _query, value))
            {
                return;
            }
            if (_selectedSearchTab != null && !_loadingTab)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    _selectedSearchTab.Query = value;
                    _selectedSearchTab.Title = value.Trim();
                }
                else if (!_selectedSearchTab.IsPinned)
                {
                    _selectedSearchTab.Query = value;
                    _selectedSearchTab.Title = $"Search {SearchTabs.IndexOf(_selectedSearchTab) + 1}";
                }
            }
            if (!_loadingTab && _config.Behavior.ClearResultsWithQuery && string.IsNullOrWhiteSpace(value))
            {
                ClearSearchResults(cancelActiveSearch: true);
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        set => SetField(ref _statusText, value);
    }

    public string CountText
    {
        get => _countText;
        set => SetField(ref _countText, value);
    }

    public string ResultLocationText
    {
        get => _resultLocationText;
        set => SetField(ref _resultLocationText, value);
    }

    public Visibility ResultLocationVisibility => ShouldShowResultLocationBar() ? Visibility.Visible : Visibility.Collapsed;

    public string SortSummaryText
    {
        get => _sortSummaryText;
        set => SetField(ref _sortSummaryText, value);
    }

    public string PreviewText
    {
        get => _previewText;
        set => SetField(ref _previewText, value);
    }

    public Visibility BusyVisibility => _isBusy ? Visibility.Visible : Visibility.Collapsed;

    public double IconGlyphSize
    {
        get => _iconGlyphSize;
        set => SetField(ref _iconGlyphSize, value);
    }

    public double IconGlyphFontSize
    {
        get => _iconGlyphFontSize;
        set => SetField(ref _iconGlyphFontSize, value);
    }

    public double IconTileWidth
    {
        get => _iconTileWidth;
        set => SetField(ref _iconTileWidth, value);
    }

    public double IconTileHeight
    {
        get => _iconTileHeight;
        set => SetField(ref _iconTileHeight, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private async void SearchClicked(object sender, RoutedEventArgs e)
    {
        await SearchAsync();
    }

    private void StopClicked(object sender, RoutedEventArgs e)
    {
        CancelActiveSearch("stop button");
    }

    private void NewTabClicked(object sender, RoutedEventArgs e)
    {
        SaveCurrentTabState();
        var tab = new SearchTabState
        {
            Title = $"Search {SearchTabs.Count + 1}",
            ViewKey = (ViewBox.SelectedItem as ResultViewMode)?.Key ?? configuredViewOrDefault(),
            SortValue = SerializeSortKeys(),
            TypeIndex = TypeBox.SelectedIndex,
        };
        SearchTabs.Add(tab);
        SelectedSearchTab = tab;
        SearchText.Focus();
    }

    private void CloseTabClicked(object sender, RoutedEventArgs e)
    {
        CloseTab(_selectedSearchTab);
    }

    private void CloseTabButtonClicked(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { Tag: SearchTabState tab })
        {
            CloseTab(tab);
        }
    }

    private void ToggleTabPinClicked(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { Tag: SearchTabState tab })
        {
            tab.IsPinned = !tab.IsPinned;
            SavePinnedTabs();
        }
    }

    private void CloseTab(SearchTabState? tab)
    {
        if (tab == null)
        {
            return;
        }

        if (tab.IsPinned)
        {
            if (ReferenceEquals(tab, _selectedSearchTab))
            {
                StatusText = "Unpin this tab before closing it";
            }
            return;
        }

        if (SearchTabs.Count == 1)
        {
            if (ReferenceEquals(tab, _selectedSearchTab))
            {
                Query = "";
                ClearSearchResults(cancelActiveSearch: true);
            }
            return;
        }

        tab.CancelSearch("close tab");
        var wasSelected = ReferenceEquals(tab, _selectedSearchTab);
        var index = SearchTabs.IndexOf(tab);
        var nextIndex = Math.Clamp(index, 0, SearchTabs.Count - 2);
        SearchTabs.Remove(tab);
        SavePinnedTabs();
        if (wasSelected)
        {
            _selectedSearchTab = null;
            SelectedSearchTab = SearchTabs[nextIndex];
        }
    }

    private void SaveCurrentTabState()
    {
        if (_selectedSearchTab == null || _loadingTab)
        {
            return;
        }

        if (!_selectedSearchTab.IsPinned || !string.IsNullOrWhiteSpace(Query))
        {
            _selectedSearchTab.Query = Query;
        }
        _selectedSearchTab.AllResults = _allResults.ToList();
        _selectedSearchTab.VisibleResults = VisibleResults.ToList();
        _selectedSearchTab.StatusText = StatusText;
        _selectedSearchTab.CountText = CountText;
        _selectedSearchTab.ResultLocationText = ResultLocationText;
        _selectedSearchTab.ViewKey = (ViewBox.SelectedItem as ResultViewMode)?.Key ?? configuredViewOrDefault();
        _selectedSearchTab.SortValue = SerializeSortKeys();
        _selectedSearchTab.TypeIndex = TypeBox.SelectedIndex;
        if (!string.IsNullOrWhiteSpace(Query))
        {
            _selectedSearchTab.Title = Query.Trim();
        }
        if (_selectedSearchTab.IsPinned)
        {
            SavePinnedTabs();
        }
    }

    private void SavePinnedTabs()
    {
        _config.PinnedTabs = SearchTabs
            .Where(tab => tab.IsPinned)
            .Select(tab => new PinnedTabConfig
            {
                Title = tab.Title,
                Query = tab.Query,
                ViewKey = tab.ViewKey,
                SortValue = tab.SortValue,
                TypeIndex = tab.TypeIndex,
            })
            .ToList();
        ConfigStore.Save(_config);
    }

    private void LoadTabState(SearchTabState tab)
    {
        _loadingTab = true;
        try
        {
            Query = tab.Query;
            _allResults.ReplaceAll(tab.AllResults);
            VisibleResults.ReplaceAll(tab.VisibleResults);
            StatusText = string.IsNullOrWhiteSpace(tab.StatusText) ? "Ready" : tab.StatusText;
            CountText = tab.CountText;
            ResultLocationText = tab.ResultLocationText;
            TypeBox.SelectedIndex = Math.Clamp(tab.TypeIndex, 0, Math.Max(0, TypeBox.Items.Count - 1));
            ViewBox.SelectedItem = ViewModes.FirstOrDefault(x => x.Key.Equals(tab.ViewKey, StringComparison.OrdinalIgnoreCase)) ?? ViewModes[^1];
            ApplySortMode(string.IsNullOrWhiteSpace(tab.SortValue) ? configuredSortOrDefault() : tab.SortValue);
            SortBox.SelectedItem = SortModes.FirstOrDefault(x => x.Key.Equals(_sortKeys[0].Key, StringComparison.OrdinalIgnoreCase)) ?? SortModes[0];
            ApplyViewMode();
            UpdateResultLocationBar();
            SetBusy(tab.IsBusy);
            SearchText.Focus();
        }
        finally
        {
            _loadingTab = false;
        }
    }

    private string configuredViewOrDefault() => string.IsNullOrWhiteSpace(_config.Behavior.ResultView) ? "details" : _config.Behavior.ResultView;

    private string configuredSortOrDefault() => string.IsNullOrWhiteSpace(_config.Behavior.ResultSort) ? "recent" : _config.Behavior.ResultSort;

    private async void SearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await SearchAsync();
        }
    }

    private async void WindowLoaded(object sender, RoutedEventArgs e)
    {
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        LoadFavorites();
        AppLogger.Info("app", $"deferred favorites loaded count={FavoriteResults.Count}");
    }

    private void ClearClicked(object sender, RoutedEventArgs e)
    {
        Query = "";
        SearchText.Focus();
    }

    private void ClearSearchResults(bool cancelActiveSearch)
    {
        var tab = _selectedSearchTab;
        if (tab?.IsPinned == true)
        {
            return;
        }
        if (cancelActiveSearch)
        {
            tab?.CancelSearch("clear");
            _paintCts?.Cancel();
        }
        _allResults.Clear();
        VisibleResults.Clear();
        if (tab != null)
        {
            tab.AllResults.Clear();
            tab.VisibleResults.Clear();
            tab.StatusText = "Ready";
            tab.CountText = "";
            tab.ResultLocationText = "";
        }
        PreviewText = "Select a result to preview.";
        StatusText = "Ready";
        CountText = "";
        ResultLocationText = "";
        UpdateResultLocationBar();
        SaveCurrentTabState();
        AppLogger.Info("search", $"cleared results cancelActive={cancelActiveSearch}");
    }

    private async Task SearchAsync()
    {
        var tab = _selectedSearchTab;
        if (tab == null)
        {
            return;
        }
        var query = Query.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            AppLogger.Info("search", "ignored empty query");
            return;
        }
        var serverQuery = BuildServerQuery(query);

        tab.CancelSearch("new search");
        tab.SearchCts = new CancellationTokenSource();
        var searchVersion = ++tab.SearchVersion;
        var searchToken = tab.SearchCts.Token;
        SetTabBusy(tab, true);
        SetTabStatus(tab, "Searching...", "");
        AppLogger.Info("search", $"version={searchVersion} query=\"{query}\" serverQuery=\"{serverQuery}\" scope=\"{(_config.Behavior.SearchContents ? "content" : "name")}\" type=\"{SelectedTypeFilter().Name}\" started");

        NasStreamPaintState? streamState = null;
        try
        {
            var cached = _history.CachedResults(query)
                .Where(result => !_rules.IsHidden(result))
                .ToList();
            var showedCached = false;
            AppLogger.Info("search", $"version={searchVersion} globalCachedVisible={cached.Count} historyFilter=\"{_config.History.SourceFilter}\"");
            if (cached.Count > 0)
            {
                await ReplaceResultsAsync(tab, cached, searchToken, "Saved results");
                if (!IsCurrentSearch(tab, searchVersion))
                {
                    return;
                }
                SetTabStatus(tab, "Saved results; checking NAS...", tab.CountText);
                showedCached = true;
                AppLogger.Info("search", $"version={searchVersion} painted cached results");
            }

            using var client = new QsirchClient(_config);
            var typeFilter = SelectedTypeFilter();
            var nasStreamState = new NasStreamPaintState(await Task.Run(_history.StarredKeys, searchToken));
            streamState = nasStreamState;
            var results = await SearchNasWithSettlingRetryAsync(
                tab,
                client,
                serverQuery,
                typeFilter,
                showedCached,
                searchToken,
                searchVersion,
                batch => PaintNasBatchAsync(tab, batch, typeFilter, nasStreamState, searchToken, searchVersion));
            if (!IsCurrentSearch(tab, searchVersion))
            {
                return;
            }
            if (showedCached && results.Count == 0)
            {
                SetTabStatus(tab, "Saved results; NAS returned none", tab.CountText);
                AppLogger.Warn("search", $"version={searchVersion} nas returned none after cached results");
                return;
            }
            var visible = await Task.Run(() => results.Where(result => !_rules.IsHidden(result)).ToList());
            AppLogger.Info("search", $"version={searchVersion} nasResults={results.Count} visibleAfterRules={visible.Count} hiddenByRules={results.Count - visible.Count}");
            await Task.Run(() => _history.AddResults(results));
            if (!IsCurrentSearch(tab, searchVersion))
            {
                AppLogger.Info("search", $"version={searchVersion} stale after history save");
                return;
            }
            if (streamState.Started)
            {
                await PaintVisibleResultsAsync(tab, visible, searchToken, "Sorting results");
            }
            else
            {
                await ReplaceResultsAsync(tab, visible, searchToken, "Painting results");
            }
            if (!IsCurrentSearch(tab, searchVersion))
            {
                AppLogger.Info("search", $"version={searchVersion} stale after painting");
                return;
            }
            LoadFavorites();
            SetTabStatus(tab, visible.Count == 0 && results.Count > 0 ? "No visible results" : "Ready", tab.CountText);
            if (IsActiveTab(tab))
            {
                SaveCurrentTabState();
            }
            AppLogger.Info("search", $"version={searchVersion} completed status=\"{StatusText}\" visible={visible.Count}");
        }
        catch (OperationCanceledException)
        {
            AppLogger.Info("search", $"version={searchVersion} canceled");
        }
        catch (TimeoutException ex) when (streamState?.Started == true)
        {
            SetTabStatus(tab, "Timed out; showing partial results", tab.CountText);
            await Task.Run(() => _history.AddResults(streamState.Received), searchToken);
            if (IsActiveTab(tab))
            {
                SaveCurrentTabState();
            }
            AppLogger.Warn("search", $"version={searchVersion} timed out after partial results visible={VisibleResults.Count} message=\"{ex.Message}\"");
        }
        catch (Exception ex)
        {
            SetTabStatus(tab, "Error", tab.CountText);
            AppLogger.Error("search", ex, $"version={searchVersion} failed query=\"{query}\"");
            MessageBox.Show(this, ex.Message, "PyQsirchgui", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (IsCurrentSearch(tab, searchVersion))
            {
                SetTabBusy(tab, false);
            }
            AppLogger.Info("search", $"version={searchVersion} finished busy={_isBusy}");
        }
    }

    private async Task<IReadOnlyList<SearchResult>> SearchNasWithSettlingRetryAsync(
        SearchTabState tab,
        QsirchClient client,
        string serverQuery,
        FileTypeFilter typeFilter,
        bool showedCached,
        CancellationToken token,
        int searchVersion,
        Func<IReadOnlyList<SearchResult>, Task> batchReceived)
    {
        using var timer = AppLogger.Measure("search", $"version={searchVersion} nas query=\"{serverQuery}\" type=\"{typeFilter.Name}\"");
        var firstPageLimit = Math.Clamp(_config.Behavior.FirstPageSize, 5, 500);
        var nextPageLimit = Math.Clamp(_config.Behavior.NextPageSize, 10, 500);
        var results = new List<SearchResult>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var recentCutoff = DateTime.Today.AddDays(-30);

        SetTabStatus(tab, "Loading recent results...", tab.CountText);
        var recentPage = await client.SearchAsync(
            serverQuery,
            typeFilter,
            firstPageLimit,
            0,
            "modified",
            "desc",
            batch => batchReceived(RecentResults(batch, recentCutoff)),
            token);
        var recentAdded = AddUniqueResults(results, seen, RecentResults(recentPage, recentCutoff));
        AppLogger.Info("search", $"version={searchVersion} nas recent page offset=0 limit={firstPageLimit} count={recentPage.Count} recentAdded={recentAdded} total={results.Count}");
        if (!IsCurrentSearch(tab, searchVersion))
        {
            return results;
        }

        SetTabStatus(tab, "Loading all results...", tab.CountText);
        var firstPage = await client.SearchAsync(serverQuery, typeFilter, firstPageLimit, 0, batchReceived, token);
        var firstAdded = AddUniqueResults(results, seen, firstPage);
        AppLogger.Info("search", $"version={searchVersion} nas page offset=0 limit={firstPageLimit} count={firstPage.Count} added={firstAdded} total={results.Count}");
        if (!IsCurrentSearch(tab, searchVersion))
        {
            return results;
        }

        if (firstPage.Count == 0 && typeFilter.Extensions.Length == 0)
        {
            SetTabStatus(tab, showedCached ? "Saved results; rechecking NAS..." : "No results yet; rechecking...", tab.CountText);
            AppLogger.Warn("search", $"version={searchVersion} nas first page empty; retrying after 450ms showedCached={showedCached}");
            await Task.Delay(450, token);
            if (!IsCurrentSearch(tab, searchVersion))
            {
                AppLogger.Info("search", $"version={searchVersion} stale before nas retry");
                return results;
            }

            var retryResults = await client.SearchAsync(serverQuery, typeFilter, firstPageLimit, 0, batchReceived, token);
            var retryAdded = AddUniqueResults(results, seen, retryResults);
            AppLogger.Info("search", $"version={searchVersion} nas retry offset=0 limit={firstPageLimit} count={retryResults.Count} added={retryAdded} total={results.Count}");
            if (retryResults.Count == 0)
            {
                return results;
            }
        }

        SetTabStatus(tab, "Loading more results...", tab.CountText);
        for (var offset = firstPageLimit; ; offset += nextPageLimit)
        {
            token.ThrowIfCancellationRequested();
            if (!IsCurrentSearch(tab, searchVersion))
            {
                return results;
            }

            var before = results.Count;
            var page = await client.SearchAsync(serverQuery, typeFilter, nextPageLimit, offset, batchReceived, token);
            AddUniqueResults(results, seen, page);
            var added = results.Count - before;
            AppLogger.Info("search", $"version={searchVersion} nas page offset={offset} limit={nextPageLimit} count={page.Count} added={added} total={results.Count}");
            if (page.Count == 0)
            {
                AppLogger.Info("search", $"version={searchVersion} nas paging complete offset={offset} total={results.Count}");
                break;
            }
            if (added == 0)
            {
                AppLogger.Warn("search", $"version={searchVersion} nas paging stopped because offset returned only duplicate results offset={offset} count={page.Count}");
                break;
            }
        }

        return results;
    }

    private static int AddUniqueResults(List<SearchResult> results, HashSet<string> seen, IEnumerable<SearchResult> page)
    {
        var added = 0;
        foreach (var result in page)
        {
            if (seen.Add(HistoryStore.ResultKey(result)))
            {
                results.Add(result);
                added++;
            }
        }
        return added;
    }

    private static List<SearchResult> RecentResults(IEnumerable<SearchResult> results, DateTime cutoff)
    {
        return results
            .Where(result => result.ModifiedDate != null && result.ModifiedDate.Value.Date >= cutoff)
            .ToList();
    }

    private async Task RefreshCacheInBackgroundAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15));
            if (_isBusy || string.IsNullOrWhiteSpace(_config.Host) || string.IsNullOrWhiteSpace(_config.User) || string.IsNullOrWhiteSpace(_config.Password))
            {
                AppLogger.Info("history", "background cache refresh skipped");
                return;
            }

            using var client = new QsirchClient(_config);
            var typeFilter = TypeFilters[0];
            var pageSize = Math.Clamp(_config.Behavior.NextPageSize, 25, 500);
            var offset = 0;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new List<SearchResult>();
            AppLogger.Info("history", $"background cache refresh started pageSize={pageSize}");

            while (!_isBusy)
            {
                var page = await client.SearchAsync(".", typeFilter, pageSize, offset, null, CancellationToken.None);
                var added = 0;
                foreach (var result in page)
                {
                    if (seen.Add(HistoryStore.ResultKey(result)))
                    {
                        pending.Add(result);
                        added++;
                    }
                }

                AppLogger.Info("history", $"background cache page offset={offset} count={page.Count} added={added} pending={pending.Count}");
                if (pending.Count >= 500)
                {
                    await Task.Run(() => _history.AddResults(pending.ToList()));
                    pending.Clear();
                }
                if (page.Count == 0 || added == 0)
                {
                    break;
                }

                offset += pageSize;
                await Task.Delay(150);
            }

            if (pending.Count > 0)
            {
                await Task.Run(() => _history.AddResults(pending));
            }
            AppLogger.Info("history", $"background cache refresh finished offset={offset}");
        }
        catch (Exception ex)
        {
            AppLogger.Error("history", ex, "background cache refresh failed");
        }
    }

    private async Task PaintNasBatchAsync(
        SearchTabState tab,
        IReadOnlyList<SearchResult> batch,
        FileTypeFilter typeFilter,
        NasStreamPaintState streamState,
        CancellationToken token,
        int searchVersion)
    {
        token.ThrowIfCancellationRequested();
        if (!IsCurrentSearch(tab, searchVersion))
        {
            return;
        }

        streamState.AddReceived(batch);
        var visible = await Task.Run(() => batch
            .Where(result => streamState.Seen.Add(HistoryStore.ResultKey(result)))
            .Where(result => !_rules.IsHidden(result))
            .ToList(), token);
        if (visible.Count == 0)
        {
            return;
        }

        var visibleMatches = new List<SearchResult>();
        foreach (var result in visible)
        {
            result.IsFavorite = streamState.Starred.Contains(HistoryStore.ResultKey(result));
            if (MatchesType(result, typeFilter))
            {
                visibleMatches.Add(result);
            }
        }

        tab.AllResults.AddRange(visible);
        tab.VisibleResults.AddRange(visibleMatches);
        streamState.VisibleCount = tab.VisibleResults.Count;
        SetTabStatus(tab, streamState.Started ? tab.StatusText : "Receiving results...", ResultCountText(tab.VisibleResults.Count));
        streamState.Started = true;
        if (!IsActiveTab(tab))
        {
            AppLogger.Info("paint", $"stream background tab version={searchVersion} batch={visible.Count} visible={tab.VisibleResults.Count}");
            return;
        }

        await Dispatcher.InvokeAsync(() =>
        {
            token.ThrowIfCancellationRequested();
            if (!IsCurrentSearch(tab, searchVersion) || !IsActiveTab(tab))
            {
                return;
            }

            if (_allResults.Count == 0 && VisibleResults.Count == 0)
            {
                CountText = "";
                StatusText = "Receiving results...";
                AppLogger.Info("paint", $"stream started version={searchVersion}");
            }

            _allResults.AddRange(visible);
            VisibleResults.AddRange(visibleMatches);

            CountText = ResultCountText(VisibleResults.Count);
            tab.CountText = CountText;
            _ = LoadResultIconsAsync(visible, token);
            AppLogger.Info("paint", $"stream batch version={searchVersion} batch={visible.Count} visible={VisibleResults.Count}");
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private async void TypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingTab || _initializing)
        {
            return;
        }
        try
        {
            await ApplyLocalFiltersAsync("Filtering results");
            SaveCurrentTabState();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ViewChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingTab || _initializing)
        {
            ApplyViewMode();
            UpdateResultLocationBar();
            return;
        }

        if (ViewBox.SelectedItem is ResultViewMode mode)
        {
            _config.Behavior.ResultView = mode.Key;
            ConfigStore.Save(_config);
        }

        var refreshVersion = ++_viewRefreshVersion;
        Dispatcher.BeginInvoke(() =>
        {
            if (refreshVersion != _viewRefreshVersion)
            {
                return;
            }

            ApplyViewMode();
            UpdateResultLocationBar();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private async void SortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingTab || _initializing)
        {
            return;
        }
        if (SortBox.SelectedItem is not ResultSortMode mode)
        {
            return;
        }

        ApplySortMode(mode.Key);
        if (_selectedSearchTab != null)
        {
            _selectedSearchTab.SortValue = SerializeSortKeys();
        }
        try
        {
            await ApplyLocalFiltersAsync("Sorting results");
            SaveCurrentTabState();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async void DetailsHeaderClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not GridViewColumnHeader header || header.Tag is not string column)
        {
            return;
        }
        var extendSort = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        ApplyHeaderSort(column, extendSort);
        try
        {
            await ApplyLocalFiltersAsync("Sorting results");
            SaveCurrentTabState();
        }
        catch (OperationCanceledException)
        {
            return;
        }
        StatusText = $"Sorted by {SortSummaryText}";
    }

    private void DetailColumnMenuOpening(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu)
        {
            return;
        }

        var visibleCount = _detailColumns.Count(x => x.Value.Width > 0);
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            var key = item.Tag?.ToString() ?? "";
            item.IsChecked = IsDetailColumnVisible(key);
            item.IsEnabled = item.IsChecked ? visibleCount > 1 : true;
        }
    }

    private void DetailColumnVisibilityClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || item.Tag is not string key || !_detailColumns.ContainsKey(key))
        {
            return;
        }

        var shouldShow = !IsDetailColumnVisible(key);
        if (!shouldShow && _detailColumns.Count(x => x.Value.Width > 0) <= 1)
        {
            return;
        }

        SetDetailColumnVisible(key, shouldShow);
        SaveVisibleDetailColumns();
        AppLogger.Info("ui", $"detail column key=\"{key}\" visible={shouldShow}");
    }

    private void RegisterDetailColumns()
    {
        _detailColumns["location"] = LocationColumn;
        _detailColumns["name"] = NameColumn;
        _detailColumns["modified"] = DateColumn;
        _detailColumns["size"] = SizeColumn;
        _detailColumns["type"] = TypeColumn;
    }

    private void SetupDetailColumnMenu()
    {
        var headers = new[] { LocationHeader, NameHeader, DateHeader, SizeHeader, TypeHeader };
        foreach (var header in headers)
        {
            header.ContextMenu = CreateDetailColumnMenu();
        }
    }

    private ContextMenu CreateDetailColumnMenu()
    {
        var menu = new ContextMenu();
        menu.Opened += DetailColumnMenuOpening;
        AddColumnMenuItem(menu, "Location", "location");
        AddColumnMenuItem(menu, "Name", "name");
        AddColumnMenuItem(menu, "Date", "modified");
        AddColumnMenuItem(menu, "Size", "size");
        AddColumnMenuItem(menu, "Type", "type");
        return menu;
    }

    private void AddColumnMenuItem(ContextMenu menu, string label, string key)
    {
        var item = new MenuItem
        {
            Header = label,
            Tag = key,
            IsCheckable = true,
        };
        item.Click += DetailColumnVisibilityClicked;
        menu.Items.Add(item);
    }

    private void ApplyDetailColumnVisibility()
    {
        var visible = _config.Behavior.VisibleDetailColumns.Count == 0
            ? new HashSet<string>(_detailColumns.Keys, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(_config.Behavior.VisibleDetailColumns, StringComparer.OrdinalIgnoreCase);
        if (!visible.Any(key => _detailColumns.ContainsKey(key)))
        {
            visible.Add("name");
        }

        foreach (var key in _detailColumns.Keys)
        {
            SetDetailColumnVisible(key, visible.Contains(key), saveWidth: false);
        }
        SaveVisibleDetailColumns(saveConfig: false);
    }

    private bool IsDetailColumnVisible(string key)
    {
        return _detailColumns.TryGetValue(key, out var column) && column.Width > 0;
    }

    private void SetDetailColumnVisible(string key, bool visible, bool saveWidth = true)
    {
        if (!_detailColumns.TryGetValue(key, out var column))
        {
            return;
        }

        if (!visible)
        {
            if (saveWidth && column.Width > 0)
            {
                _detailColumnWidths[key] = column.Width;
            }
            column.Width = 0;
            return;
        }

        column.Width = _detailColumnWidths.TryGetValue(key, out var width) ? width : DefaultDetailColumnWidth(key);
    }

    private void SaveVisibleDetailColumns(bool saveConfig = true)
    {
        _config.Behavior.VisibleDetailColumns = _detailColumns
            .Where(x => x.Value.Width > 0)
            .Select(x => x.Key)
            .ToList();
        if (saveConfig)
        {
            ConfigStore.Save(_config);
        }
    }

    private static double DefaultDetailColumnWidth(string key)
    {
        return key switch
        {
            "location" => 330,
            "name" => 280,
            "modified" => 150,
            "size" => 80,
            "type" => 90,
            _ => 120,
        };
    }

    private void ResultSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var result = SelectedResult();
        UpdateResultLocationBar(result);
        ShowPreviewSummary(result);
        if (_config.Behavior.PreviewPane)
        {
            _ = LoadPreviewAsync(result);
        }
    }

    private void ResultDoubleClicked(object sender, MouseButtonEventArgs e)
    {
        OpenSelected();
    }

    private void ResultRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        DependencyObject? current = e.OriginalSource as DependencyObject;
        while (current != null && current is not ListBoxItem && current is not ListViewItem)
        {
            current = VisualTreeHelper.GetParent(current);
        }
        if (current is ListBoxItem item)
        {
            item.IsSelected = true;
            item.Focus();
        }
        else if (current is ListViewItem listViewItem)
        {
            listViewItem.IsSelected = true;
            listViewItem.Focus();
        }
    }

    private void FavoriteSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var result = FavoritesList.SelectedItem as SearchResult;
        UpdateResultLocationBar(result);
        ShowPreviewSummary(result);
        if (_config.Behavior.PreviewPane)
        {
            _ = LoadPreviewAsync(result);
        }
    }

    private void FavoriteDoubleClicked(object sender, MouseButtonEventArgs e)
    {
        OpenFavorite();
    }

    private void OpenFavoriteClicked(object sender, RoutedEventArgs e)
    {
        OpenFavorite();
    }

    private void ShowFavoriteClicked(object sender, RoutedEventArgs e)
    {
        var result = FavoritesList.SelectedItem as SearchResult;
        if (result == null)
        {
            return;
        }
        try
        {
            _shell.Show(result);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Show favorite", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void UnfavoriteClicked(object sender, RoutedEventArgs e)
    {
        if (FavoritesList.SelectedItem is not SearchResult result)
        {
            return;
        }
        SetFavorite(result, false);
    }

    private void OpenFavorite()
    {
        if (FavoritesList.SelectedItem is not SearchResult result)
        {
            return;
        }
        CancelActiveSearch("open favorite");
        try
        {
            _shell.Open(result);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Open favorite", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            FavoritesList.SelectedItem = null;
        }
    }

    private void OpenClicked(object sender, RoutedEventArgs e) => OpenSelected();

    private void ShowClicked(object sender, RoutedEventArgs e)
    {
        var result = SelectedResult();
        if (result == null)
        {
            return;
        }
        try
        {
            _shell.Show(result);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Show", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void FavoriteClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedResult() is SearchResult result)
        {
            SetFavorite(result, true);
        }
    }

    private void ToggleFavoriteClicked(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { Tag: SearchResult result })
        {
            SetFavorite(result, !result.IsFavorite);
        }
    }

    private void SetFavorite(SearchResult result, bool isFavorite)
    {
        _history.SetStarred(result, isFavorite);
        foreach (var match in _allResults.Concat(VisibleResults).Concat(FavoriteResults)
                     .Where(item => item.Path.Equals(result.Path, StringComparison.OrdinalIgnoreCase) &&
                                    item.FileName.Equals(result.FileName, StringComparison.OrdinalIgnoreCase)))
        {
            match.IsFavorite = isFavorite;
        }

        if (isFavorite)
        {
            if (!FavoriteResults.Any(item => item.Path.Equals(result.Path, StringComparison.OrdinalIgnoreCase) &&
                                              item.FileName.Equals(result.FileName, StringComparison.OrdinalIgnoreCase)))
            {
                result.IsFavorite = true;
                FavoriteResults.Insert(0, result);
            }
            StatusText = "Added to Favorites";
            return;
        }

        foreach (var match in FavoriteResults
                     .Where(item => item.Path.Equals(result.Path, StringComparison.OrdinalIgnoreCase) &&
                                    item.FileName.Equals(result.FileName, StringComparison.OrdinalIgnoreCase))
                     .ToList())
        {
            FavoriteResults.Remove(match);
        }
        StatusText = "Removed from Favorites";
    }

    private void SearchContentsChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }
        _config.Behavior.SearchContents = SearchContentsToggle.IsChecked == true;
        ConfigStore.Save(_config);
    }

    private void PreviewToggleClicked(object sender, RoutedEventArgs e)
    {
        if (_config.Behavior.PreviewPane)
        {
            RememberPreviewWidth();
        }
        _config.Behavior.PreviewPane = !_config.Behavior.PreviewPane;
        ApplyBehavior();
        ConfigStore.Save(_config);
        if (_config.Behavior.PreviewPane)
        {
            _ = LoadPreviewAsync(CurrentPreviewResult());
        }
    }

    private void HidePreviewClicked(object sender, RoutedEventArgs e)
    {
        RememberPreviewWidth();
        _config.Behavior.PreviewPane = false;
        ApplyBehavior();
        ConfigStore.Save(_config);
    }

    private void FavoritesToggleClicked(object sender, RoutedEventArgs e)
    {
        if (_favoritesVisible)
        {
            RememberFavoritesWidth();
        }
        _favoritesVisible = !_favoritesVisible;
        ApplyPaneLayout();
    }

    private void HideFavoritesClicked(object sender, RoutedEventArgs e)
    {
        RememberFavoritesWidth();
        _favoritesVisible = false;
        ApplyPaneLayout();
    }

    private void ExitClicked(object sender, RoutedEventArgs e)
    {
        ExitApplication();
    }

    private void MainWindowStateChanged(object? sender, EventArgs e)
    {
        if (_config.Behavior.MinimizeToTray && WindowState == WindowState.Minimized)
        {
            HideToTray();
        }
    }

    private void MainWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_isExiting)
        {
            return;
        }

        e.Cancel = true;
        HideToTray();
    }

    private void HideToTray()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(HideToTray);
            return;
        }

        if (!IsVisible)
        {
            return;
        }

        Hide();
        AppLogger.Info("app", "main window hidden to tray");
    }

    private void ShowFromTray()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(ShowFromTray);
            return;
        }

        if (!IsVisible)
        {
            Show();
        }

        WindowState = WindowState.Normal;
        Activate();
        Focus();
        AppLogger.Info("app", "main window restored from tray");
    }

    private void ExitApplication()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(ExitApplication);
            return;
        }

        _isExiting = true;
        _trayIcon?.Dispose();
        _trayIcon = null;
        Application.Current.Shutdown();
    }

    private void SettingsClicked(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_config) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        ConfigStore.Save(_config);
        _rules = new ResultRules(_config);
        if (dialog.ClearHistoryRequested)
        {
            _history.ClearCurrentMachine(dialog.ClearStarredRequested);
        }
        LoadFavorites();
        ViewBox.SelectedItem = ViewModes.FirstOrDefault(x => x.Key.Equals(_config.Behavior.ResultView, StringComparison.OrdinalIgnoreCase)) ?? ViewModes[^1];
        SortBox.SelectedItem = SortModes.FirstOrDefault(x => x.Key.Equals(_config.Behavior.ResultSort, StringComparison.OrdinalIgnoreCase)) ?? SortModes[0];
        ApplySortMode(_config.Behavior.ResultSort);
        ApplyViewMode();
        ClearResultIcons();
        _ = ApplyLocalFiltersAsync("Filtering results");
        ApplyBehavior();
        SearchContentsToggle.IsChecked = _config.Behavior.SearchContents;
        StatusText = "Settings saved";
    }

    private void OpenSelected()
    {
        var result = SelectedResult();
        if (result == null)
        {
            return;
        }
        CancelActiveSearch("open result");
        try
        {
            _shell.Open(result);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Open", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private SearchResult? SelectedResult()
    {
        if (DetailsList.Visibility == Visibility.Visible)
        {
            return DetailsList.SelectedItem as SearchResult;
        }
        if (IconGrid.Visibility == Visibility.Visible)
        {
            return IconGrid.SelectedItem as SearchResult;
        }
        return ExplorerList.SelectedItem as SearchResult;
    }

    private string BuildServerQuery(string query)
    {
        if (query == "*")
        {
            return ".";
        }
        if (_config.Behavior.SearchContents || query.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
        {
            return query;
        }

        var escaped = query.Replace("\"", "\\\"", StringComparison.Ordinal);
        return $"name:\"{escaped}\"";
    }

    private void UpdateResultLocationBar(SearchResult? result = null)
    {
        result ??= SelectedResult();
        ResultLocationText = result?.DisplayPath ?? "";
        OnPropertyChanged(nameof(ResultLocationVisibility));
    }

    private bool ShouldShowResultLocationBar()
    {
        var mode = (ViewBox.SelectedItem as ResultViewMode)?.Key ?? "details";
        return mode != "details" && !string.IsNullOrWhiteSpace(ResultLocationText);
    }

    private async Task ApplyLocalFiltersAsync(string statusText)
    {
        var typeFilter = SelectedTypeFilter();
        var snapshot = _allResults.ToList();
        AppLogger.Info("filter", $"status=\"{statusText}\" snapshot={snapshot.Count} type=\"{typeFilter.Name}\" sort=\"{_sortColumn}\" descending={_sortDescending}");
        var filtered = await Task.Run(() => snapshot.Where(result => MatchesType(result, typeFilter)).ToList());
        if (_selectedSearchTab != null)
        {
            await PaintVisibleResultsAsync(_selectedSearchTab, filtered, NextPaintToken(), statusText);
        }
    }

    private void ApplyLocalFilters()
    {
        var typeFilter = SelectedTypeFilter();
        var filtered = _allResults.Where(result => MatchesType(result, typeFilter));
        VisibleResults.ReplaceAll(SortResults(filtered));
        CountText = ResultCountText(VisibleResults.Count);
        SaveCurrentTabState();
    }

    private static bool IsCurrentSearch(SearchTabState tab, int version) => version == tab.SearchVersion;

    private bool IsActiveTab(SearchTabState tab) => ReferenceEquals(tab, _selectedSearchTab) && !_loadingTab;

    private static string ResultCountText(int count) => count == 0 ? "" : $"{count:n0} result{(count == 1 ? "" : "s")}";

    private void CancelActiveSearch(string reason)
    {
        var tab = _selectedSearchTab;
        tab?.CancelSearch(reason);
        _paintCts?.Cancel();
        SetBusy(false);
        StatusText = "Ready";
        if (tab != null)
        {
            tab.StatusText = StatusText;
            tab.CountText = CountText;
        }
        AppLogger.Info("search", $"active search canceled reason=\"{reason}\"");
    }

    private async Task ReplaceResultsAsync(SearchTabState tab, IEnumerable<SearchResult> results, CancellationToken token, string statusText)
    {
        var starred = await Task.Run(_history.StarredKeys, token);
        var sorted = await Task.Run(() => SortResults(results).ToList(), token);
        var typeFilter = SelectedTypeFilter();
        var paintToken = IsActiveTab(tab) ? NextPaintToken(token) : token;
        tab.AllResults.Clear();
        tab.VisibleResults.Clear();
        SetTabStatus(tab, statusText, "");

        if (IsActiveTab(tab))
        {
            _allResults.Clear();
            VisibleResults.Clear();
            CountText = "";
            StatusText = statusText;
        }

        foreach (var batch in Batches(sorted, 10))
        {
            paintToken.ThrowIfCancellationRequested();
            var visibleBatch = new List<SearchResult>();
            foreach (var result in batch)
            {
                result.IsFavorite = starred.Contains(HistoryStore.ResultKey(result));
                if (MatchesType(result, typeFilter))
                {
                    visibleBatch.Add(result);
                }
            }
            tab.AllResults.AddRange(batch);
            tab.VisibleResults.AddRange(visibleBatch);
            SetTabStatus(tab, statusText, ResultCountText(tab.VisibleResults.Count));

            if (IsActiveTab(tab))
            {
                _allResults.AddRange(batch);
                VisibleResults.AddRange(visibleBatch);
                CountText = ResultCountText(VisibleResults.Count);
                _ = LoadResultIconsAsync(batch.ToList(), paintToken);
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            }
        }
        SetTabStatus(tab, "Ready", ResultCountText(tab.VisibleResults.Count));
        if (IsActiveTab(tab))
        {
            await RefreshResultViewsAsync("replace");
        }
        AppLogger.Info("paint", $"replace completed all={tab.AllResults.Count} visible={tab.VisibleResults.Count} active={IsActiveTab(tab)}");
    }

    private async Task PaintVisibleResultsAsync(SearchTabState tab, IEnumerable<SearchResult> results, CancellationToken token, string statusText)
    {
        var sorted = await Task.Run(() => SortResults(results).ToList(), token);
        tab.VisibleResults.Clear();
        SetTabStatus(tab, statusText, "");
        if (IsActiveTab(tab))
        {
            VisibleResults.Clear();
            CountText = "";
            StatusText = statusText;
        }
        foreach (var batch in Batches(sorted, 10))
        {
            token.ThrowIfCancellationRequested();
            tab.VisibleResults.AddRange(batch);
            SetTabStatus(tab, statusText, ResultCountText(tab.VisibleResults.Count));
            if (IsActiveTab(tab))
            {
                VisibleResults.AddRange(batch);
                CountText = ResultCountText(VisibleResults.Count);
                _ = LoadResultIconsAsync(batch.ToList(), token);
                await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            }
        }
        SetTabStatus(tab, "Ready", ResultCountText(tab.VisibleResults.Count));
        if (IsActiveTab(tab))
        {
            await RefreshResultViewsAsync("filter");
        }
        AppLogger.Info("paint", $"filter paint completed visible={tab.VisibleResults.Count} active={IsActiveTab(tab)}");
    }

    private async Task RefreshResultViewsAsync(string reason)
    {
        DetailsList.Items.Refresh();
        IconGrid.Items.Refresh();
        ExplorerList.Items.Refresh();
        await Dispatcher.InvokeAsync(() =>
        {
            DetailsList.InvalidateVisual();
            IconGrid.InvalidateVisual();
            ExplorerList.InvalidateVisual();
            AppLogger.Info("paint", $"refresh reason=\"{reason}\" details={DetailsList.Visibility} iconGrid={IconGrid.Visibility} list={ExplorerList.Visibility} visible={VisibleResults.Count}");
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private CancellationToken NextPaintToken(CancellationToken outerToken = default)
    {
        _paintCts?.Cancel();
        _paintCts = outerToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(outerToken)
            : new CancellationTokenSource();
        return _paintCts.Token;
    }

    private static bool MatchesType(SearchResult result, FileTypeFilter typeFilter)
    {
        return typeFilter.Extensions.Length == 0 ||
               typeFilter.Extensions.Contains(result.Extension, StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<IReadOnlyList<T>> Batches<T>(IReadOnlyList<T> items, int size)
    {
        for (var i = 0; i < items.Count; i += size)
        {
            var batch = new List<T>(Math.Min(size, items.Count - i));
            for (var j = i; j < items.Count && j < i + size; j++)
            {
                batch.Add(items[j]);
            }
            yield return batch;
        }
    }

    private IEnumerable<SearchResult> SortResults(IEnumerable<SearchResult> results)
    {
        var indexed = results.Select((item, index) => (item, index));
        IOrderedEnumerable<(SearchResult item, int index)> ordered = _config.Behavior.FoldersFirst
            ? indexed.OrderBy(x => x.item.IsFolder ? 0 : 1)
            : indexed.OrderBy(x => 0);
        foreach (var sort in _sortKeys)
        {
            ordered = ApplySortKey(ordered, sort);
        }
        ordered = ordered.ThenBy(x => x.index);
        return ordered.Select(x => x.item).ToList();
    }

    private static IOrderedEnumerable<(SearchResult item, int index)> ApplySortKey(
        IOrderedEnumerable<(SearchResult item, int index)> ordered,
        ResultSortKey sort)
    {
        return sort.Key switch
        {
            "recent" => sort.Descending
                ? ordered.ThenBy(x => ModifiedBucketRank(x.item)).ThenByDescending(x => x.item.ModifiedDate ?? DateTime.MinValue)
                : ordered.ThenByDescending(x => ModifiedBucketRank(x.item)).ThenBy(x => x.item.ModifiedDate ?? DateTime.MaxValue),
            "modified" => sort.Descending
                ? ordered.ThenByDescending(x => x.item.ModifiedDate ?? DateTime.MinValue)
                : ordered.ThenBy(x => x.item.ModifiedDate ?? DateTime.MaxValue),
            "size" => sort.Descending ? ordered.ThenByDescending(x => x.item.Size) : ordered.ThenBy(x => x.item.Size),
            _ => sort.Descending
                ? ordered.ThenByDescending(x => SortValue(x.item, sort.Key))
                : ordered.ThenBy(x => SortValue(x.item, sort.Key)),
        };
    }

    private static object SortValue(SearchResult result, string column)
    {
        return column switch
        {
            "location" => result.DisplayPath,
            "type" => result.Kind,
            "size" => result.Size,
            "modified" => result.ModifiedDate ?? DateTime.MaxValue,
            _ => result.FileName,
        };
    }

    private static int ModifiedBucketRank(SearchResult result)
    {
        return result.ModifiedGroup switch
        {
            "Today" => 0,
            "Yesterday" => 1,
            "Earlier this week" => 2,
            "Last week" => 3,
            "Earlier this month" => 4,
            "Last month" => 5,
            "Older" => 6,
            _ => 7,
        };
    }

    private void ApplySortMode(string key)
    {
        _sortKeys = ParseSortKeys(key);
        var first = _sortKeys[0];
        _sortColumn = first.Key;
        _sortDescending = first.Descending;
        UpdateSortSummary();
    }

    private void SelectSortMode(string key)
    {
        var normalized = key;
        var mode = SortModes.FirstOrDefault(x => x.Key.Equals(normalized, StringComparison.OrdinalIgnoreCase));
        if (mode != null && SortBox.SelectedItem != mode)
        {
            SortBox.SelectionChanged -= SortChanged;
            SortBox.SelectedItem = mode;
            SortBox.SelectionChanged += SortChanged;
        }
    }

    private void ApplyHeaderSort(string key, bool extendSort)
    {
        var existing = _sortKeys.FindIndex(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (!extendSort)
        {
            var descending = existing == 0 ? !_sortKeys[0].Descending : DefaultSortDescending(key);
            _sortKeys = [new ResultSortKey(key, descending)];
        }
        else if (existing >= 0)
        {
            _sortKeys[existing] = _sortKeys[existing] with { Descending = !_sortKeys[existing].Descending };
        }
        else
        {
            _sortKeys.Add(new ResultSortKey(key, DefaultSortDescending(key)));
        }

        var first = _sortKeys[0];
        _sortColumn = first.Key;
        _sortDescending = first.Descending;
        SelectSortMode(first.Key);
        UpdateSortSummary();
        if (_selectedSearchTab != null)
        {
            _selectedSearchTab.SortValue = SerializeSortKeys();
        }
    }

    private List<ResultSortKey> ParseSortKeys(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [new ResultSortKey("recent", true)];
        }

        var keys = new List<ResultSortKey>();
        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pieces = part.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var key = NormalizeSortKey(pieces.Length > 0 ? pieces[0] : "");
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }
            var descending = pieces.Length > 1
                ? pieces[1].Equals("desc", StringComparison.OrdinalIgnoreCase) || pieces[1].Equals("descending", StringComparison.OrdinalIgnoreCase)
                : DefaultSortDescending(key);
            if (keys.All(x => !x.Key.Equals(key, StringComparison.OrdinalIgnoreCase)))
            {
                keys.Add(new ResultSortKey(key, descending));
            }
        }

        return keys.Count == 0 ? [new ResultSortKey("recent", true)] : keys;
    }

    private string SerializeSortKeys()
    {
        return string.Join(",", _sortKeys.Select(x => $"{x.Key}:{(x.Descending ? "desc" : "asc")}"));
    }

    private void UpdateSortSummary()
    {
        SortSummaryText = string.Join(" + ", _sortKeys.Select(x => $"{SortDisplayName(x.Key)} {(x.Descending ? "desc" : "asc")}"));
    }

    private static string NormalizeSortKey(string key)
    {
        return key.ToLowerInvariant() switch
        {
            "date" => "modified",
            "date modified" => "modified",
            "recentness" => "recent",
            "path" => "location",
            "location" or "name" or "modified" or "recent" or "type" or "size" => key.ToLowerInvariant(),
            _ => "",
        };
    }

    private static bool DefaultSortDescending(string key)
    {
        return key.Equals("recent", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("modified", StringComparison.OrdinalIgnoreCase) ||
               key.Equals("size", StringComparison.OrdinalIgnoreCase);
    }

    private static string SortDisplayName(string key)
    {
        return key switch
        {
            "recent" => "Recentness",
            "modified" => "Date",
            "location" => "Location",
            "type" => "Type",
            "size" => "Size",
            _ => "Name",
        };
    }

    private FileTypeFilter SelectedTypeFilter()
    {
        return TypeBox.SelectedItem as FileTypeFilter ?? TypeFilters[0];
    }

    private Task LoadResultIconsAsync(IReadOnlyList<SearchResult> results, CancellationToken token)
    {
        try
        {
            foreach (var result in results)
            {
                if (token.IsCancellationRequested || result.IconSource != null)
                {
                    continue;
                }

                var key = IconCacheKey(result);
                if (_iconCache.TryGetValue(key, out var cached))
                {
                    result.IconSource = cached;
                    continue;
                }

                lock (_iconLoadsInFlight)
                {
                    if (!_iconLoadsInFlight.Add(key))
                    {
                        continue;
                    }
                }

                _ = LoadResultIconAsync(result, key, token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        return Task.CompletedTask;
    }

    private async Task LoadResultIconAsync(SearchResult result, string key, CancellationToken token)
    {
        var enteredGate = false;
        try
        {
            await _iconLoadGate.WaitAsync(token);
            enteredGate = true;
            ImageSource? icon = null;
            if (_config.Behavior.UseQsirchThumbnails && result.HasThumbnailAction)
            {
                try
                {
                    using var client = new QsirchClient(_config);
                    var thumbnail = await client.ThumbnailAsync(result, token);
                    if (thumbnail is { Length: > 0 })
                    {
                        icon = BitmapFromBytes(thumbnail);
                    }
                }
                catch
                {
                    icon = null;
                }
            }

            if (icon == null)
            {
                icon = await Task.Run(() => ShellPreviewService.FileTypeIcon(result.Extension, result.IsFolder), token);
            }

            if (icon != null)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    _iconCache[key] = icon;
                    result.IconSource = icon;
                });
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            lock (_iconLoadsInFlight)
            {
                _iconLoadsInFlight.Remove(key);
            }
            if (enteredGate)
            {
                _iconLoadGate.Release();
            }
        }
    }

    private string IconCacheKey(SearchResult result)
    {
        if (_config.Behavior.UseQsirchThumbnails && result.HasThumbnailAction)
        {
            return $"thumbnail|{result.Path}|{result.FileName}";
        }

        return result.IsFolder
            ? "__folder__"
            : string.IsNullOrWhiteSpace(result.Extension) ? "__file__" : "." + result.Extension.TrimStart('.').ToLowerInvariant();
    }

    private void ClearResultIcons()
    {
        _iconCache.Clear();
        lock (_iconLoadsInFlight)
        {
            _iconLoadsInFlight.Clear();
        }
        foreach (var result in _allResults.Concat(VisibleResults).Concat(FavoriteResults).Distinct())
        {
            result.IconSource = null;
        }
    }

    private void ApplyViewMode()
    {
        var mode = (ViewBox.SelectedItem as ResultViewMode)?.Key ?? "details";
        DetailsList.Visibility = mode == "details" ? Visibility.Visible : Visibility.Collapsed;
        IconGrid.Visibility = mode is "large_icons" or "small_icons" ? Visibility.Visible : Visibility.Collapsed;
        ExplorerList.Visibility = mode == "list" ? Visibility.Visible : Visibility.Collapsed;

        switch (mode)
        {
            case "large_icons":
                IconGlyphSize = 62;
                IconGlyphFontSize = 44;
                IconTileWidth = 150;
                IconTileHeight = 122;
                break;
            case "small_icons":
                IconGlyphSize = 38;
                IconGlyphFontSize = 25;
                IconTileWidth = 104;
                IconTileHeight = 82;
                break;
            case "list":
                IconGlyphSize = 18;
                IconGlyphFontSize = 16;
                IconTileWidth = 260;
                IconTileHeight = 28;
                break;
        }
    }

    private void ApplyBehavior()
    {
        ApplyTheme();
        Topmost = _config.AlwaysOnTop;
        ShowInTaskbar = _config.Behavior.ShowInTaskbar;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        PreviewToggle.FontWeight = _config.Behavior.PreviewPane ? FontWeights.SemiBold : FontWeights.Normal;
        ApplyPaneLayout();
    }

    private void ApplyTheme()
    {
        ThemePalette.Apply(Resources, string.IsNullOrWhiteSpace(_config.Behavior.Theme) ? "system" : _config.Behavior.Theme);
    }

    private void ApplyPaneLayout()
    {
        FavoritesPane.Visibility = _favoritesVisible ? Visibility.Visible : Visibility.Collapsed;
        FavoritesSplitter.Visibility = _favoritesVisible ? Visibility.Visible : Visibility.Collapsed;
        FavoritesColumn.MinWidth = _favoritesVisible ? 140 : 0;
        FavoritesColumn.Width = _favoritesVisible ? _favoritesWidth : new GridLength(0);
        FavoritesSplitterColumn.Width = _favoritesVisible ? new GridLength(5) : new GridLength(0);
        FavoritesToggle.FontWeight = _favoritesVisible ? FontWeights.SemiBold : FontWeights.Normal;

        PreviewPane.Visibility = _config.Behavior.PreviewPane ? Visibility.Visible : Visibility.Collapsed;
        PreviewSplitter.Visibility = _config.Behavior.PreviewPane ? Visibility.Visible : Visibility.Collapsed;
        PreviewColumn.MinWidth = _config.Behavior.PreviewPane ? 220 : 0;
        PreviewColumn.Width = _config.Behavior.PreviewPane ? _previewWidth : new GridLength(0);
        PreviewSplitterColumn.Width = _config.Behavior.PreviewPane ? new GridLength(5) : new GridLength(0);
    }

    private void RememberFavoritesWidth()
    {
        if (FavoritesPane.ActualWidth > 0)
        {
            _favoritesWidth = new GridLength(Math.Max(140, FavoritesPane.ActualWidth));
        }
    }

    private void RememberPreviewWidth()
    {
        if (PreviewPane.ActualWidth > 0)
        {
            _previewWidth = new GridLength(Math.Max(220, PreviewPane.ActualWidth));
        }
    }

    private SearchResult? CurrentPreviewResult()
    {
        return SelectedResult() ?? FavoritesList.SelectedItem as SearchResult;
    }

    private void ShowPreviewSummary(SearchResult? result)
    {
        if (result == null)
        {
            PreviewTitle.Text = "Preview";
            PreviewText = "Select a result to preview.";
            ShowPreviewText();
            return;
        }
        PreviewTitle.Text = result.FileName;
        PreviewText = $"{result.FileName}\r\n{result.DisplayPath}\r\n{result.Kind} {result.SizeText}".Trim();
        ShowPreviewText();
    }

    private async Task LoadPreviewAsync(SearchResult? result)
    {
        if (result == null)
        {
            ShowPreviewSummary(null);
            return;
        }

        PreviewTitle.Text = result.FileName;
        PreviewText = "Loading preview...";
        ShowPreviewText();
        var preview = await BuildPreviewAsync(result);
        if (CurrentPreviewResult() == result && _config.Behavior.PreviewPane)
        {
            RenderPreview(preview);
        }
    }

    private async Task<PreviewContent> BuildPreviewAsync(SearchResult result)
    {
        var header = $"{result.FileName}\r\n{result.DisplayPath}\r\n{result.Kind} {result.SizeText}".Trim();
        var shellPreview = await Task.Run(() => BuildShellPreview(result));
        if (shellPreview != null)
        {
            return PreviewContent.ForImage(shellPreview, result.FileName, true);
        }

        return PreviewContent.ForText($"{header}\r\n\r\nPreview unavailable.");
    }

    private byte[]? BuildShellPreview(SearchResult result)
    {
        if (result.IsFolder)
        {
            return null;
        }

        try
        {
            var path = _mapper.Resolve(result);
            return ShellPreviewService.RenderPreview(path);
        }
        catch
        {
            return null;
        }
    }

    private void RenderPreview(PreviewContent preview)
    {
        if (preview.ImageBytes != null)
        {
            PreviewImage.Source = BitmapFromBytes(preview.ImageBytes);
            PreviewImage.Stretch = preview.FitToPane ? Stretch.Uniform : Stretch.None;
            PreviewTitle.Text = preview.Title;
            PreviewText = "";
            PreviewTextBox.Visibility = Visibility.Collapsed;
            PreviewImageHost.Visibility = Visibility.Visible;
            return;
        }

        PreviewImage.Source = null;
        PreviewText = preview.Text;
        ShowPreviewText();
    }

    private void ShowPreviewText()
    {
        PreviewImage.Source = null;
        PreviewImageHost.Visibility = Visibility.Collapsed;
        PreviewTextBox.Visibility = Visibility.Visible;
    }

    private static BitmapImage BitmapFromBytes(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void LoadFavorites()
    {
        FavoriteResults.ReplaceAll(_history.Favorites());
    }

    private void SetTabBusy(SearchTabState tab, bool busy)
    {
        tab.IsBusy = busy;
        if (IsActiveTab(tab))
        {
            SetBusy(busy);
        }
    }

    private void SetTabStatus(SearchTabState tab, string statusText, string countText)
    {
        tab.StatusText = statusText;
        tab.CountText = countText;
        if (!IsActiveTab(tab))
        {
            return;
        }

        StatusText = statusText;
        CountText = countText;
    }

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        OnPropertyChanged(nameof(BusyVisibility));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

internal sealed class PreviewContent
{
    public string Text { get; private init; } = "";
    public string Title { get; private init; } = "Preview";
    public bool FitToPane { get; private init; }
    public byte[]? ImageBytes { get; private init; }

    public static PreviewContent ForText(string text) => new() { Text = text };

    public static PreviewContent ForImage(byte[] bytes, string title, bool fitToPane) => new() { ImageBytes = bytes, Title = title, FitToPane = fitToPane };
}

internal sealed record ResultSortKey(string Key, bool Descending);

internal sealed class NasStreamPaintState(HashSet<string> starred)
{
    public HashSet<string> Starred { get; } = starred;
    public HashSet<string> Seen { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<SearchResult> Received { get; } = [];
    private readonly HashSet<string> _receivedKeys = new(StringComparer.OrdinalIgnoreCase);
    public bool Started { get; set; }
    public int VisibleCount { get; set; }

    public void AddReceived(IEnumerable<SearchResult> results)
    {
        foreach (var result in results)
        {
            if (_receivedKeys.Add(HistoryStore.ResultKey(result)))
            {
                Received.Add(result);
            }
        }
    }
}

public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void AddRange(IEnumerable<T> items)
    {
        var added = false;
        foreach (var item in items)
        {
            Items.Add(item);
            added = true;
        }
        if (added)
        {
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }

    public void ReplaceAll(IEnumerable<T> items)
    {
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

public sealed class SearchTabState : INotifyPropertyChanged
{
    private string _title = "Search";
    private bool _isPinned;

    public string Title
    {
        get => _title;
        set
        {
            if (_title == value)
            {
                return;
            }
            _title = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title)));
        }
    }

    public string Query { get; set; } = "";
    public bool IsPinned
    {
        get => _isPinned;
        set
        {
            if (_isPinned == value)
            {
                return;
            }
            _isPinned = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPinned)));
        }
    }
    public List<SearchResult> AllResults { get; set; } = [];
    public List<SearchResult> VisibleResults { get; set; } = [];
    public string StatusText { get; set; } = "Ready";
    public string CountText { get; set; } = "";
    public string ResultLocationText { get; set; } = "";
    public string ViewKey { get; set; } = "details";
    public string SortValue { get; set; } = "recent:desc";
    public int TypeIndex { get; set; }
    public bool SearchOnFirstFocus { get; set; }
    public CancellationTokenSource? SearchCts { get; set; }
    public int SearchVersion { get; set; }
    public bool IsBusy { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void CancelSearch(string reason)
    {
        SearchVersion++;
        IsBusy = false;
        try
        {
            SearchCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        SearchCts = null;
        AppLogger.Info("search", $"tab=\"{Title}\" canceled reason=\"{reason}\"");
    }
}
