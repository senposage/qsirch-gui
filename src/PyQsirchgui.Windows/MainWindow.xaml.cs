using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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
    private QsirchClient _qsirchClient;
    private readonly List<QsirchClient> _retiredQsirchClients = [];
    private TrayIconService? _trayIcon;
    private ResultRules _rules;
    private string _query = "";
    private string _statusText = "Ready";
    private string _countText = "";
    private string _resultLocationText = "";
    private string _sortSummaryText = "Recentness desc";
    private string _previewText = "Select a result to preview.";
    private string _emptyStateText = "";
    private bool _isBusy;
    private bool _initializing = true;
    private bool _loadingTab;
    private bool _clearButtonActive;
    private int _viewRefreshVersion;
    private double _iconGlyphSize = 54;
    private double _iconGlyphFontSize = 44;
    private double _iconTileWidth = 150;
    private double _iconTileHeight = 118;
    private string _sortColumn = "recent";
    private bool _sortDescending = true;
    private List<ResultSortKey> _sortKeys = [new("recent", true)];
    private CancellationTokenSource? _paintCts;
    private CancellationTokenSource? _previewCts;
    private int _favoritesLoadVersion;
    private ShellPreviewHost? _nativePreviewHost;
    private bool _favoritesVisible = true;
    private GridLength _favoritesWidth = new(190);
    private GridLength _previewWidth = new(320);
    private readonly Dictionary<string, ImageSource> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _iconLoadsInFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _iconLoadGate = new(2);
    private SearchResult? _openFolderIconResult;
    private readonly SemaphoreSlim _favoriteWriteGate = new(1, 1);
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

    public ICollectionView GroupedDetailsResults { get; }

    public MainWindow()
    {
        InitializeComponent();
        VisibleResults.CollectionChanged += (_, _) => OnPropertyChanged(nameof(EmptyStateVisibility));
        GroupedDetailsResults = new ListCollectionView(VisibleResults);
        StateChanged += (_, _) => UpdateCaptionButtons();
        StateChanged += MainWindowStateChanged;
        Closing += MainWindowClosing;
        AppLogger.Info("app", "main window initializing");
        _history = new HistoryStore(_config);
        _mapper = new PathMapper(_config);
        _shell = new ShellActions(_mapper);
        _qsirchClient = new QsirchClient(_config);
        _trayIcon = new TrayIconService(ShowFromTray, ExitApplication);
        _rules = new ResultRules(_config);
        Closed += (_, _) =>
        {
            _previewCts?.Cancel();
            ClearNativePreview();
            _qsirchClient.Dispose();
            foreach (var client in _retiredQsirchClients)
            {
                client.Dispose();
            }
            _retiredQsirchClients.Clear();
            _trayIcon?.Dispose();
            _trayIcon = null;
        };
        RegisterDetailColumns();
        SetupDetailColumnMenu();
        SearchTabs.Add(new SearchTabState { Title = "Search 1", ViewKey = configuredViewOrDefault(), SortValue = configuredSortOrDefault(), ResultLimit = configuredResultLimit() });
        foreach (var pinned in _config.PinnedTabs)
        {
            SearchTabs.Add(new SearchTabState
            {
                Title = pinned.Title,
                Query = pinned.Query,
                ViewKey = pinned.ViewKey,
                SortValue = pinned.SortValue,
                TypeIndex = pinned.TypeIndex,
                TypeNames = pinned.TypeNames.ToList(),
                IsPinned = true,
                ResultLimit = configuredResultLimit(),
                SearchOnFirstFocus = !string.IsNullOrWhiteSpace(pinned.Query),
            });
        }
        _selectedSearchTab = SearchTabs[0];
        TypeFilterOptions = new ObservableCollection<FileTypeFilterOption>(TypeFilters
            .Skip(1)
            .Select(filter => new FileTypeFilterOption { Filter = filter }));
        DataContext = this;
        ScopeBox.SelectedItem = SearchScopes[0];
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
        ExactMatchToggle.IsChecked = _config.Behavior.ExactMatch;
        AppLogger.Info("app", $"main window ready config={ConfigStore.ConfigPath} log={AppLogger.LogPath} searchTimeout={Math.Clamp(_config.Behavior.SearchTimeoutSeconds, 15, 300)}s");
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
        Close();
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
    public ObservableCollection<string> RecentSearches { get; } = [];
    public ObservableCollection<SavedSearch> SavedSearches { get; } = [];
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
            QueueInitialResultIcons(value, value.VisibleResults, CancellationToken.None);
            if (value.SearchOnFirstFocus && !string.IsNullOrWhiteSpace(value.Query))
            {
                value.SearchOnFirstFocus = false;
                _ = SearchAsync();
            }
        }
    }

    public IReadOnlyList<FileTypeFilter> TypeFilters { get; } =
    [
        new() { Name = "All types", Category = "All", IncludeAllFiles = true, IncludeFolders = true },
        new() { Name = "Folders", IncludeFolders = true },
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

    public ObservableCollection<FileTypeFilterOption> TypeFilterOptions { get; private set; } = [];

    public string TypeFilterSummary
    {
        get
        {
            var selected = TypeFilterOptions.Where(option => option.IsSelected).Select(option => option.Name).ToList();
            return selected.Count switch
            {
                0 => "All types",
                1 => selected[0],
                2 => string.Join(" + ", selected),
                _ => $"{selected[0]} + {selected.Count - 1}",
            };
        }
    }

    public IReadOnlyList<ResultViewMode> ViewModes { get; } =
    [
        new() { Name = "Large icons", Key = "large_icons" },
        new() { Name = "Small icons", Key = "small_icons" },
        new() { Name = "List", Key = "list" },
        new() { Name = "Details", Key = "details" },
    ];

    public IReadOnlyList<ResultSortMode> SortModes { get; } =
    [
        new() { Name = "Folder groups", Key = "folder" },
        new() { Name = "Relevance", Key = "relevance" },
        new() { Name = "Recentness", Key = "recent" },
        new() { Name = "Date modified", Key = "modified" },
        new() { Name = "Name", Key = "name" },
        new() { Name = "Location", Key = "location" },
        new() { Name = "Type", Key = "type" },
        new() { Name = "Size", Key = "size" },
    ];

    public IReadOnlyList<SearchScope> SearchScopes { get; } =
    [
        new() { Name = "All folders", Key = "all" },
        new() { Name = "This folder", Key = "folder" },
        new() { Name = "Modified recently", Key = "recent" },
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
            if (!_loadingTab && !_clearButtonActive && _config.Behavior.ClearResultsWithQuery && string.IsNullOrWhiteSpace(value))
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

    public string EmptyStateText
    {
        get => _emptyStateText;
        set
        {
            if (SetField(ref _emptyStateText, value))
            {
                OnPropertyChanged(nameof(EmptyStateVisibility));
            }
        }
    }

    public Visibility BusyVisibility => _isBusy ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EmptyStateVisibility => !_isBusy && VisibleResults.Count == 0 && !string.IsNullOrWhiteSpace(EmptyStateText)
        ? Visibility.Visible
        : Visibility.Collapsed;

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

    private async void LoadMoreClicked(object sender, RoutedEventArgs e)
    {
        if (_selectedSearchTab == null || string.IsNullOrWhiteSpace(Query))
        {
            StatusText = "Run a search first";
            return;
        }
        _selectedSearchTab.ResultLimit = Math.Min(5000, Math.Max(configuredResultLimit(), _selectedSearchTab.ResultLimit) + configuredResultLimit());
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
            TypeNames = SelectedTypeNames().ToList(),
            ResultLimit = configuredResultLimit(),
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
        _selectedSearchTab.TypeNames = SelectedTypeNames().ToList();
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
                TypeNames = tab.TypeNames.ToList(),
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
            ApplyTypeSelection(tab.TypeNames, tab.TypeIndex);
            ScopeBox.SelectedItem = SearchScopes.FirstOrDefault(scope => scope.Key.Equals(tab.ScopeKey, StringComparison.OrdinalIgnoreCase)) ?? SearchScopes[0];
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

    private int configuredResultLimit() => Math.Clamp(_config.Behavior.MaxSearchResults, 50, 5000);

    private async void SearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await SearchAsync();
        }
    }

    private async void WindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.L)
        {
            SearchText.Focus();
            SearchText.SelectAll();
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.N)
        {
            NewTabClicked(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.W)
        {
            CloseTab(_selectedSearchTab);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.F5)
        {
            await SearchAsync();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && _isBusy)
        {
            CancelActiveSearch("escape key");
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Enter && e.OriginalSource is not TextBox)
        {
            if (Keyboard.Modifiers == ModifierKeys.Shift)
            {
                if (SelectedFavoriteResult() != null)
                {
                    ShowFavoriteClicked(this, new RoutedEventArgs());
                }
                else
                {
                    ShowClicked(this, new RoutedEventArgs());
                }
            }
            else
            {
                if (SelectedFavoriteNode()?.SavedSearch is { } savedSearch)
                {
                    Query = savedSearch.Query;
                    await SearchAsync();
                }
                else if (SelectedFavoriteResult() != null)
                {
                    OpenFavorite();
                }
                else
                {
                    OpenSelected();
                }
            }
            e.Handled = true;
        }
    }

    private async void WindowLoaded(object sender, RoutedEventArgs e)
    {
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        await LoadFavoritesAsync();
        await LoadRecentSearchesAsync();
        AppLogger.Info("app", $"deferred favorites loaded count={FavoriteResults.Count}");
    }

    private void ClearClicked(object sender, RoutedEventArgs e)
    {
        _clearButtonActive = true;
        try
        {
            Query = "";
        }
        finally
        {
            _clearButtonActive = false;
        }
        ClearSearchResults(cancelActiveSearch: true, respectPinnedTab: false);
        SearchText.Focus();
    }

    private void ClearSearchResults(bool cancelActiveSearch, bool respectPinnedTab = true)
    {
        var tab = _selectedSearchTab;
        if (respectPinnedTab && tab?.IsPinned == true)
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
            tab.ResultQuery = "";
            tab.StatusText = "Ready";
            tab.CountText = "";
            tab.ResultLocationText = "";
        }
        PreviewText = "Select a result to preview.";
        EmptyStateText = "";
        StatusText = "Ready";
        CountText = "";
        ResultLocationText = "";
        UpdateResultLocationBar();
        SaveCurrentTabState();
        AppLogger.Info("search", $"cleared results cancelActive={cancelActiveSearch}");
    }

    private void ClearResultsForNewQuery(SearchTabState tab)
    {
        _paintCts?.Cancel();
        tab.AllResults.Clear();
        tab.VisibleResults.Clear();
        tab.ResultQuery = "";
        tab.ResultLimitReached = false;
        tab.CountText = "";
        tab.ResultLocationText = "";

        if (IsActiveTab(tab))
        {
            _allResults.Clear();
            VisibleResults.Clear();
            CountText = "";
            ResultLocationText = "";
            PreviewText = "Select a result to preview.";
            UpdateResultLocationBar();
        }

        AppLogger.Info("search", "cleared previous results for new query");
    }

    private async Task SearchAsync(bool clearExistingResults = false)
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
        _ = RecordRecentSearchAsync(query);

        var isNewQuery = !string.Equals(tab.ResultQuery, query, StringComparison.OrdinalIgnoreCase);
        tab.CancelSearch("new search");
        if (isNewQuery || clearExistingResults)
        {
            ClearResultsForNewQuery(tab);
            tab.ResultQuery = query;
        }
        tab.Query = query;
        tab.SearchCts = new CancellationTokenSource();
        tab.IconLoadRequests = 0;
        var searchVersion = ++tab.SearchVersion;
        var searchToken = tab.SearchCts.Token;
        SetTabBusy(tab, true);
        EmptyStateText = "";
        SetTabStatus(tab, "Searching...", "");
        AppLogger.Info("search", $"version={searchVersion} query=\"{query}\" serverQuery=\"{serverQuery}\" scope=\"{(_config.Behavior.SearchContents ? "content" : "name")}\" type=\"{SelectedTypeFilter().Name}\" started");

        NasStreamPaintState? streamState = null;
        try
        {
            var showedCached = false;
            AppLogger.Info("search", $"version={searchVersion} local result cache disabled; querying NAS directly");

            var typeFilter = SelectedTypeFilter();
            var resultLimit = Math.Max(configuredResultLimit(), tab.ResultLimit);
            var collapsedFoldersForLimit = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool CountsTowardResultLimit(SearchResult result) =>
                !_rules.IsHidden(result) &&
                MatchesExactQuery(result, query) &&
                MatchesType(result, typeFilter) &&
                MatchesScope(result, tab) &&
                (!_config.Behavior.CollapseMatchingFolderResults ||
                 MatchingParentFolderPath(result, query) is not { } folderPath ||
                 collapsedFoldersForLimit.Add(folderPath));
            var nasStreamState = new NasStreamPaintState(await Task.Run(_history.StarredKeys, searchToken));
            streamState = nasStreamState;
            var results = await SearchNasWithSettlingRetryAsync(
                tab,
                _qsirchClient,
                serverQuery,
                typeFilter,
                showedCached,
                searchToken,
                searchVersion,
                batch => PaintNasBatchAsync(tab, batch, typeFilter, query, nasStreamState, searchToken, searchVersion),
                resultLimit,
                CountsTowardResultLimit);
            if (!IsCurrentSearch(tab, searchVersion))
            {
                return;
            }
            var visible = await Task.Run(() =>
            {
                PopulateDisplayPaths(results);
                return results
                    .Where(result => !_rules.IsHidden(result))
                    .Where(result => MatchesExactQuery(result, query))
                    .Where(result => MatchesType(result, typeFilter))
                    .Where(result => MatchesScope(result, tab))
                    .ToList();
            });
            var displayed = LimitRawResultsForDisplay(visible, resultLimit, query);
            AppLogger.Info("search", $"version={searchVersion} nasResults={results.Count} visibleAfterFilters={visible.Count} filteredOut={results.Count - visible.Count} resultLimit={resultLimit}");
            if (streamState.Started)
            {
                await PaintVisibleResultsAsync(tab, displayed, searchToken, "Sorting results");
            }
            else
            {
                await ReplaceResultsAsync(tab, displayed, searchToken, "Painting results");
            }
            if (!IsCurrentSearch(tab, searchVersion))
            {
                AppLogger.Info("search", $"version={searchVersion} stale after painting");
                return;
            }
            _ = LoadFavoritesAsync();
            tab.ResultLimitReached = visible.Count >= resultLimit;
            EmptyStateText = displayed.Count > 0
                ? ""
                : results.Count == 0
                    ? "No results found"
                    : results.All(result => _rules.IsHidden(result))
                        ? "Matching items are hidden by access rules"
                        : "No results match the current filters";
            SetTabStatus(tab, displayed.Count == 0 && results.Count > 0 ? "No visible results" : tab.ResultLimitReached ? "Result limit reached; load more for additional results" : "Ready", tab.CountText);
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
            if (VisibleResults.Count == 0)
            {
                EmptyStateText = "Search timed out before results arrived";
            }
            if (IsActiveTab(tab))
            {
                SaveCurrentTabState();
            }
            AppLogger.Warn("search", $"version={searchVersion} timed out after partial results visible={VisibleResults.Count} message=\"{ex.Message}\"");
        }
        catch (Exception ex)
        {
            SetTabStatus(tab, "Error", tab.CountText);
            if (VisibleResults.Count == 0)
            {
                EmptyStateText = "Search could not be completed";
            }
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
        Func<IReadOnlyList<SearchResult>, Task> batchReceived,
        int resultLimit,
        Func<SearchResult, bool> countsTowardResultLimit)
    {
        using var timer = AppLogger.Measure("search", $"version={searchVersion} nas query=\"{serverQuery}\" type=\"{typeFilter.Name}\"");
        var firstPageLimit = Math.Clamp(_config.Behavior.FirstPageSize, 5, 500);
        var nextPageLimit = Math.Clamp(_config.Behavior.NextPageSize, 10, 500);
        var results = new List<SearchResult>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visibleCount = 0;
        var recentCutoff = DateTime.Today.AddDays(-30);

        await ThrottleInactiveTabAsync(tab, token);
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
        var recentAdded = AddUniqueResults(results, seen, RecentResults(recentPage, recentCutoff), countsTowardResultLimit);
        visibleCount += recentAdded.DisplayableAdded;
        AppLogger.Info("search", $"version={searchVersion} nas recent page offset=0 limit={firstPageLimit} count={recentPage.Count} recentAdded={recentAdded.Added} visibleAdded={recentAdded.DisplayableAdded} total={results.Count} visible={visibleCount}");
        if (!IsCurrentSearch(tab, searchVersion))
        {
            return results;
        }

        await ThrottleInactiveTabAsync(tab, token);
        SetTabStatus(tab, "Loading all results...", tab.CountText);
        var firstPage = await client.SearchAsync(serverQuery, typeFilter, firstPageLimit, 0, batchReceived, token);
        var firstAdded = AddUniqueResults(results, seen, firstPage, countsTowardResultLimit);
        visibleCount += firstAdded.DisplayableAdded;
        AppLogger.Info("search", $"version={searchVersion} nas page offset=0 limit={firstPageLimit} count={firstPage.Count} added={firstAdded.Added} visibleAdded={firstAdded.DisplayableAdded} total={results.Count} visible={visibleCount}");
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
            var retryAdded = AddUniqueResults(results, seen, retryResults, countsTowardResultLimit);
            visibleCount += retryAdded.DisplayableAdded;
            AppLogger.Info("search", $"version={searchVersion} nas retry offset=0 limit={firstPageLimit} count={retryResults.Count} added={retryAdded.Added} visibleAdded={retryAdded.DisplayableAdded} total={results.Count} visible={visibleCount}");
            if (retryResults.Count == 0)
            {
                return results;
            }
        }

        SetTabStatus(tab, "Loading more results...", tab.CountText);
        for (var offset = firstPageLimit; visibleCount < resultLimit; offset += nextPageLimit)
        {
            token.ThrowIfCancellationRequested();
            if (!IsCurrentSearch(tab, searchVersion))
            {
                return results;
            }

            await ThrottleInactiveTabAsync(tab, token);
            var page = await client.SearchAsync(serverQuery, typeFilter, nextPageLimit, offset, batchReceived, token);
            var added = AddUniqueResults(results, seen, page, countsTowardResultLimit);
            visibleCount += added.DisplayableAdded;
            AppLogger.Info("search", $"version={searchVersion} nas page offset={offset} limit={nextPageLimit} count={page.Count} added={added.Added} visibleAdded={added.DisplayableAdded} total={results.Count} visible={visibleCount}");
            if (page.Count == 0)
            {
                AppLogger.Info("search", $"version={searchVersion} nas paging complete offset={offset} total={results.Count}");
                break;
            }
            if (added.Added == 0)
            {
                AppLogger.Warn("search", $"version={searchVersion} nas paging stopped because offset returned only duplicate results offset={offset} count={page.Count}");
                break;
            }
        }

        if (visibleCount >= resultLimit)
        {
            AppLogger.Info("search", $"version={searchVersion} stopped at visible result limit={resultLimit} rawResults={results.Count}");
        }
        return results;
    }

    private async Task ThrottleInactiveTabAsync(SearchTabState tab, CancellationToken token)
    {
        if (!IsActiveTab(tab))
        {
            await Task.Delay(150, token);
        }
    }

    private static ResultAddCounts AddUniqueResults(
        List<SearchResult> results,
        HashSet<string> seen,
        IEnumerable<SearchResult> page,
        Func<SearchResult, bool> countsTowardResultLimit)
    {
        var added = 0;
        var displayableAdded = 0;
        foreach (var result in page)
        {
            if (seen.Add(HistoryStore.ResultKey(result)))
            {
                results.Add(result);
                added++;
                if (countsTowardResultLimit(result))
                {
                    displayableAdded++;
                }
            }
        }
        return new ResultAddCounts(added, displayableAdded);
    }

    private static List<SearchResult> RecentResults(IEnumerable<SearchResult> results, DateTime cutoff)
    {
        return results
            .Where(result => result.ModifiedDate != null && result.ModifiedDate.Value.Date >= cutoff)
            .ToList();
    }

    private async Task PaintNasBatchAsync(
        SearchTabState tab,
        IReadOnlyList<SearchResult> batch,
        FileTypeFilter typeFilter,
        string query,
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
        var visible = await Task.Run(() =>
        {
            PopulateDisplayPaths(batch);
            return batch
                .Where(result => streamState.Seen.Add(HistoryStore.ResultKey(result)))
                .Where(result => !_rules.IsHidden(result))
                .Where(result => MatchesExactQuery(result, query))
                .Where(result => MatchesScope(result, tab))
                .ToList();
        }, token);
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

        var displaySlots = Math.Max(0, tab.ResultLimit - streamState.DisplayedResultCount);
        var displayedMatches = LimitRawResultsForDisplay(visibleMatches, displaySlots, query, streamState.CollapsedFolderPaths);
        var presentation = BuildResultPresentation(displayedMatches, query, streamState.EmittedFolderPaths);
        streamState.DisplayedResultCount += displayedMatches.Count;
        tab.AllResults.AddRange(visible);
        tab.VisibleResults.AddRange(presentation);
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
            VisibleResults.AddRange(presentation);

            CountText = ResultCountText(VisibleResults.Count);
            tab.CountText = CountText;
            QueueInitialResultIcons(tab, presentation, token);
            AppLogger.Info("paint", $"stream batch version={searchVersion} batch={visible.Count} rawDisplayed={displayedMatches.Count} presentation={presentation.Count} visible={VisibleResults.Count}");
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private async void TypeFilterChanged(object sender, RoutedEventArgs e)
    {
        OnPropertyChanged(nameof(TypeFilterSummary));
        await ApplyTypeFilterAsync();
    }

    private async void ClearTypeFiltersClicked(object sender, RoutedEventArgs e)
    {
        foreach (var option in TypeFilterOptions)
        {
            option.IsSelected = false;
        }
        OnPropertyChanged(nameof(TypeFilterSummary));
        await ApplyTypeFilterAsync();
    }

    private async Task ApplyTypeFilterAsync()
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

    private async void ScopeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingTab || _initializing || _selectedSearchTab == null || ScopeBox.SelectedItem is not SearchScope scope)
        {
            return;
        }

        if (scope.Key == "folder")
        {
            var selected = SelectedResult();
            if (selected is not { IsFolder: true })
            {
                StatusText = "Select a folder result before choosing This folder";
                ScopeBox.SelectionChanged -= ScopeChanged;
                ScopeBox.SelectedItem = SearchScopes[0];
                ScopeBox.SelectionChanged += ScopeChanged;
                return;
            }

            var folderPath = selected.IsFolder ? ResultItemPath(selected) : ParentPath(ResultItemPath(selected));
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                StatusText = "This result has no folder path";
                return;
            }
            _selectedSearchTab.ScopePath = folderPath;
        }
        else
        {
            _selectedSearchTab.ScopePath = "";
        }

        _selectedSearchTab.ScopeKey = scope.Key;
        _selectedSearchTab.CancelSearch("scope changed");
        SetTabBusy(_selectedSearchTab, false);
        await ApplyLocalFiltersAsync("Filtering results");
        SaveCurrentTabState();
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
        UpdateFolderSelectionIcon(result);
        if (result is { IconSource: null })
        {
            _ = LoadResultIconsAsync([result], CancellationToken.None);
        }
        UpdateResultLocationBar(result);
        ShowPreviewSummary(result);
        if (_config.Behavior.PreviewPane)
        {
            RequestPreview(result);
        }
    }

    private void UpdateFolderSelectionIcon(SearchResult? selected)
    {
        if (ReferenceEquals(_openFolderIconResult, selected))
        {
            return;
        }

        if (_openFolderIconResult != null)
        {
            if (_iconCache.TryGetValue("__folder__", out var closedFolderIcon))
            {
                _openFolderIconResult.IconSource = closedFolderIcon;
            }
            else
            {
                _openFolderIconResult.IconSource = null;
                _ = LoadResultIconsAsync([_openFolderIconResult], CancellationToken.None);
            }
        }

        _openFolderIconResult = selected is { IsFolder: true } ? selected : null;
        if (_openFolderIconResult == null)
        {
            return;
        }

        const string openFolderKey = "__folder_open__";
        if (_iconCache.TryGetValue(openFolderKey, out var openFolderIcon))
        {
            _openFolderIconResult.IconSource = openFolderIcon;
            return;
        }

        var folder = _openFolderIconResult;
        _ = Task.Run(() => ShellPreviewService.FileTypeIcon("", isFolder: true, openFolder: true))
            .ContinueWith(task =>
            {
                if (task.Status != TaskStatus.RanToCompletion || task.Result == null)
                {
                    return;
                }

                Dispatcher.BeginInvoke(() =>
                {
                    _iconCache[openFolderKey] = task.Result;
                    if (ReferenceEquals(_openFolderIconResult, folder))
                    {
                        folder.IconSource = task.Result;
                    }
                });
            }, TaskScheduler.Default);
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

    private SearchResult? SelectedFavoriteResult() => (FavoritesTree.SelectedItem as FavoriteTreeNode)?.Result;

    private FavoriteTreeNode? SelectedFavoriteNode() => FavoritesTree.SelectedItem as FavoriteTreeNode;

    private void FavoritesTreeSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        var result = (e.NewValue as FavoriteTreeNode)?.Result;
        UpdateResultLocationBar(result);
        ShowPreviewSummary(result);
        if (_config.Behavior.PreviewPane)
        {
            RequestPreview(result);
        }
    }

    private async void FavoritesTreeDoubleClicked(object sender, MouseButtonEventArgs e)
    {
        if (SelectedFavoriteNode()?.SavedSearch is { } savedSearch)
        {
            Query = savedSearch.Query;
            await SearchAsync();
            return;
        }
        OpenFavorite();
    }

    private void FavoriteTreeRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        DependencyObject? current = e.OriginalSource as DependencyObject;
        while (current != null && current is not TreeViewItem)
        {
            current = VisualTreeHelper.GetParent(current);
        }
        if (current is TreeViewItem item)
        {
            item.IsSelected = true;
            item.Focus();
        }
    }

    private void FavoriteContextMenuOpened(object sender, RoutedEventArgs e)
    {
        var node = SelectedFavoriteNode();
        var isResult = node?.Result != null;
        var isSavedSearch = node?.SavedSearch != null;
        var isDeletableFolder = node is { IsFolder: true } &&
            !node.FolderPath.Equals("__unfiled__", StringComparison.OrdinalIgnoreCase) &&
            !node.FolderPath.Equals("__saved_searches__", StringComparison.OrdinalIgnoreCase);

        OpenFavoriteMenuItem.Visibility = isResult ? Visibility.Visible : Visibility.Collapsed;
        ShowFavoriteMenuItem.Visibility = isResult ? Visibility.Visible : Visibility.Collapsed;
        CopyFavoritePathMenuItem.Visibility = isResult ? Visibility.Visible : Visibility.Collapsed;
        AddFavoriteToGroupMenuItem.Visibility = isResult ? Visibility.Visible : Visibility.Collapsed;
        RemoveFavoriteMenuItem.Visibility = isResult ? Visibility.Visible : Visibility.Collapsed;
        RunSavedSearchMenuItem.Visibility = isSavedSearch ? Visibility.Visible : Visibility.Collapsed;
        DeleteSavedSearchMenuItem.Visibility = isSavedSearch ? Visibility.Visible : Visibility.Collapsed;
        DeleteFavoriteFolderMenuItem.Visibility = isDeletableFolder ? Visibility.Visible : Visibility.Collapsed;
        FavoriteActionSeparator.Visibility = isResult || isSavedSearch ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OpenFavoriteClicked(object sender, RoutedEventArgs e)
    {
        OpenFavorite();
    }

    private async void RunSavedSearchClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedFavoriteNode()?.SavedSearch is not { } savedSearch)
        {
            return;
        }

        Query = savedSearch.Query;
        await SearchAsync();
    }

    private void ShowFavoriteClicked(object sender, RoutedEventArgs e)
    {
        var result = SelectedFavoriteResult();
        if (result == null)
        {
            return;
        }
        try
        {
            PersistResolvedWindowsPath(result);
            _shell.Show(result);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Show favorite", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void UnfavoriteClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedFavoriteResult() is not SearchResult result)
        {
            return;
        }
        SetFavorite(result, false);
    }

    private async void DeleteFavoriteFolderClicked(object sender, RoutedEventArgs e)
    {
        var folder = SelectedFavoriteNode();
        if (folder is not { IsFolder: true } ||
            folder.FolderPath == "__unfiled__" ||
            folder.FolderPath == "__saved_searches__")
        {
            StatusText = "Select a Favorites folder first";
            return;
        }

        var prefix = folder.FolderPath + "\\";
        var results = FavoriteResults
            .Where(result => result.Groups.Any(group =>
                group.Equals(folder.FolderPath, StringComparison.OrdinalIgnoreCase) ||
                group.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (results.Count == 0)
        {
            return;
        }

        var confirmation = MessageBox.Show(
            this,
            $"Remove the Favorites folder \"{folder.Name}\" and its {results.Count} saved item(s)?\n\nThe files on the NAS will not be deleted.",
            "Delete Favorites folder",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        foreach (var result in results)
        {
            var remainingGroups = result.Groups
                .Where(group =>
                    !group.Equals(folder.FolderPath, StringComparison.OrdinalIgnoreCase) &&
                    !group.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ToList();
            UpdateFavoritePresentation(result, remainingGroups.Count > 0, remainingGroups, refreshTree: false);
        }
        RefreshFavoritesTree();
        StatusText = "Deleting Favorites folder...";
        await WriteFavoriteChangeAsync("delete folder", () => _history.SaveFavoriteStates(results));
        StatusText = $"Deleted Favorites folder {folder.Name}";
    }

    private async void DeleteSavedSearchClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedFavoriteNode()?.SavedSearch is not { } savedSearch)
        {
            StatusText = "Select a saved search first";
            return;
        }
        await Task.Run(() => _history.DeleteSavedSearch(savedSearch.Id));
        await LoadFavoritesAsync();
        StatusText = "Saved search deleted";
    }

    private void OpenFavorite()
    {
        if (SelectedFavoriteResult() is not SearchResult result)
        {
            return;
        }
        CancelActiveSearch("open favorite");
        try
        {
            PersistResolvedWindowsPath(result);
            _shell.Open(result);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Open favorite", MessageBoxButton.OK, MessageBoxImage.Warning);
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

    private async void CopyPathClicked(object sender, RoutedEventArgs e)
    {
        await CopyPathAsync(SelectedResult());
    }

    private async void OpenInNewTabClicked(object sender, RoutedEventArgs e)
    {
        var result = SelectedResult();
        if (result == null)
        {
            return;
        }

        var folderPath = result.IsFolder ? ResultItemPath(result) : ParentPath(ResultItemPath(result));
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            StatusText = "This result has no folder path";
            return;
        }

        var currentQuery = Query;
        NewTabClicked(this, new RoutedEventArgs());
        var tab = _selectedSearchTab!;
        tab.ScopeKey = "folder";
        tab.ScopePath = folderPath;
        _loadingTab = true;
        try
        {
            ScopeBox.SelectedItem = SearchScopes.First(scope => scope.Key == "folder");
        }
        finally
        {
            _loadingTab = false;
        }
        Query = string.IsNullOrWhiteSpace(currentQuery) ? result.FileName : currentQuery;
        await SearchAsync();
    }

    private void PropertiesClicked(object sender, RoutedEventArgs e)
    {
        var result = SelectedResult();
        if (result == null)
        {
            return;
        }
        try
        {
            _shell.ShowProperties(result);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Properties", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void CopyFavoritePathClicked(object sender, RoutedEventArgs e)
    {
        await CopyPathAsync(SelectedFavoriteResult());
    }

    private async Task CopyPathAsync(SearchResult? result)
    {
        if (result == null)
        {
            return;
        }

        try
        {
            if (result.IsFavorite)
            {
                PersistResolvedWindowsPath(result);
            }
            var path = _mapper.Resolve(result);
            if (await TrySetClipboardTextAsync(path))
            {
                StatusText = "Path copied";
            }
            else
            {
                StatusText = "Clipboard is busy. Try copying again.";
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Copy path", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static async Task<bool> TrySetClipboardTextAsync(string text)
    {
        const int clipboardBusyHResult = unchecked((int)0x800401D0);
        const int attempts = 8;

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (COMException ex) when (ex.HResult == clipboardBusyHResult && attempt < attempts - 1)
            {
                await Task.Delay(75);
            }
            catch (COMException ex) when (ex.HResult == clipboardBusyHResult)
            {
                AppLogger.Warn("app", "clipboard remained unavailable after retrying copy path");
                return false;
            }
        }

        return false;
    }

    private void FavoriteClicked(object sender, RoutedEventArgs e)
    {
        _ = SetFavoritesAsync(SelectedResults(), true);
    }

    private async void SaveSearchClicked(object sender, RoutedEventArgs e)
    {
        var query = Query.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            StatusText = "Enter a search before saving it";
            return;
        }
        await Task.Run(() => _history.SaveSearch(query, query));
        await LoadFavoritesAsync();
        StatusText = "Search saved in Favorites";
    }

    private async void RecentSearchSelected(object sender, SelectionChangedEventArgs e)
    {
        if (RecentSearchList.SelectedItem is not string query)
        {
            return;
        }
        RecentSearchButton.IsChecked = false;
        RecentSearchList.SelectedItem = null;
        Query = query;
        await SearchAsync();
    }

    private void AddToGroupClicked(object sender, RoutedEventArgs e)
    {
        _ = EditFavoriteGroupsAsync(SelectedResults());
    }

    private void AddFavoriteToGroupClicked(object sender, RoutedEventArgs e)
    {
        _ = EditFavoriteGroupsAsync(SelectedFavoriteResult() is { } result ? [result] : []);
    }

    private async Task EditFavoriteGroupsAsync(IReadOnlyList<SearchResult> results)
    {
        if (results.Count == 0)
        {
            StatusText = "Select a result first";
            return;
        }

        StatusText = "Loading favorite groups...";
        var groupData = await Task.Run(() => new FavoriteGroupDialogData(
            _history.FavoriteGroups(),
            results.Count == 1 ? _history.GroupsFor(results[0]) : []));
        var dialog = new GroupPickerWindow(groupData.Groups, groupData.SelectedGroups, _config.Behavior.Theme)
        {
            Owner = this,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        foreach (var result in results)
        {
            UpdateFavoritePresentation(result, true, dialog.SelectedGroups, refreshTree: false);
        }
        RefreshFavoritesTree(dialog.SelectedGroups);
        StatusText = "Saving favorite groups...";
        try
        {
            await WriteFavoriteChangeAsync("groups", () => _history.SetGroups(results, dialog.SelectedGroups));
        }
        catch (Exception ex)
        {
            AppLogger.Error("history", ex, "failed to save favorite groups");
            StatusText = "Could not save favorite groups";
            return;
        }

        StatusText = results.Count == 1
            ? dialog.SelectedGroups.Count == 0 ? "Favorite updated" : "Favorite groups updated"
            : $"Updated {results.Count} favorites";
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
        _ = SetFavoritesAsync([result], isFavorite);
    }

    private async Task SetFavoritesAsync(IReadOnlyList<SearchResult> results, bool isFavorite)
    {
        if (results.Count == 0)
        {
            StatusText = "Select a result first";
            return;
        }
        foreach (var result in results)
        {
            UpdateFavoritePresentation(result, isFavorite, refreshTree: false);
            if (isFavorite)
            {
                CaptureResolvedWindowsPath(result);
            }
        }
        RefreshFavoritesTree();
        StatusText = isFavorite
            ? results.Count == 1 ? "Added to Favorites" : $"Added {results.Count} to Favorites"
            : results.Count == 1 ? "Removed from Favorites" : $"Removed {results.Count} from Favorites";

        try
        {
            await WriteFavoriteChangeAsync(isFavorite ? "star" : "unstar", () => _history.SetStarred(results, isFavorite));
        }
        catch (Exception ex)
        {
            AppLogger.Error("history", ex, "failed to save favorite");
            StatusText = "Could not save favorite";
        }
    }

    private void CaptureResolvedWindowsPath(SearchResult result)
    {
        var durablePath = _mapper.TryResolveUnc(result) ?? _mapper.TryResolve(result);
        if (!string.IsNullOrWhiteSpace(durablePath))
        {
            result.ResolvedPath = durablePath;
        }
    }

    private void PersistResolvedWindowsPath(SearchResult result)
    {
        var previousPath = result.ResolvedPath;
        CaptureResolvedWindowsPath(result);
        if (!string.Equals(previousPath, result.ResolvedPath, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(result.ResolvedPath))
        {
            _ = Task.Run(() => _history.UpdateResolvedPath(result));
        }
    }

    private void UpdateFavoritePresentation(SearchResult result, bool isFavorite, IReadOnlyList<string>? groups = null, bool refreshTree = true)
    {
        result.IsFavorite = isFavorite;
        if (groups != null)
        {
            result.Groups = groups.ToList();
        }

        var matchingFavorites = FavoriteResults
            .Where(item => item.Path.Equals(result.Path, StringComparison.OrdinalIgnoreCase) &&
                           item.FileName.Equals(result.FileName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (!isFavorite)
        {
            foreach (var match in matchingFavorites)
            {
                FavoriteResults.Remove(match);
            }
            if (refreshTree)
            {
                RefreshFavoritesTree();
            }
            return;
        }

        if (matchingFavorites.Count == 0)
        {
            FavoriteResults.Insert(0, result);
        }
        else
        {
            foreach (var match in matchingFavorites)
            {
                match.IsFavorite = true;
                if (groups != null)
                {
                    match.Groups = groups.ToList();
                }
            }
        }
        if (refreshTree)
        {
            RefreshFavoritesTree();
        }
    }

    private async Task WriteFavoriteChangeAsync(string operation, Action change)
    {
        var stopwatch = Stopwatch.StartNew();
        await _favoriteWriteGate.WaitAsync();
        try
        {
            await Task.Run(change);
        }
        finally
        {
            _favoriteWriteGate.Release();
            AppLogger.Info("favorites", $"{operation} write completed elapsed={stopwatch.ElapsedMilliseconds}ms");
        }
    }

    private async void SearchContentsChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }
        _config.Behavior.SearchContents = SearchContentsToggle.IsChecked == true;
        ConfigStore.Save(_config);
        if (!string.IsNullOrWhiteSpace(Query))
        {
            await SearchAsync(clearExistingResults: true);
        }
    }

    private async void ExactMatchChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }
        _config.Behavior.ExactMatch = ExactMatchToggle.IsChecked == true;
        ConfigStore.Save(_config);
        if (!string.IsNullOrWhiteSpace(Query))
        {
            await SearchAsync(clearExistingResults: true);
        }
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
            RequestPreview(CurrentPreviewResult());
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
        Close();
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

        if (_config.Behavior.ExitToTray)
        {
            e.Cancel = true;
            HideToTray();
        }
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

    public void ActivateFromExternalLaunch() => ShowFromTray();

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
        var connectionBefore = ConnectionSettings.Current(_config);
        var dialog = new SettingsWindow(_config) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        ConfigStore.Save(_config);
        if (connectionBefore != ConnectionSettings.Current(_config))
        {
            RecreateQsirchClient();
        }
        _rules = new ResultRules(_config);
        if (dialog.ClearHistoryRequested)
        {
            _history.ClearCurrentMachine(dialog.ClearStarredRequested);
        }
        if (dialog.ResetDatabaseRequested)
        {
            _history.Reset();
        }
        _ = LoadFavoritesAsync();
        _ = LoadRecentSearchesAsync();
        ViewBox.SelectedItem = ViewModes.FirstOrDefault(x => x.Key.Equals(_config.Behavior.ResultView, StringComparison.OrdinalIgnoreCase)) ?? ViewModes[^1];
        SortBox.SelectedItem = SortModes.FirstOrDefault(x => x.Key.Equals(_config.Behavior.ResultSort, StringComparison.OrdinalIgnoreCase)) ?? SortModes[0];
        ApplySortMode(_config.Behavior.ResultSort);
        ApplyViewMode();
        ClearResultIcons();
        ApplyPathPresentation();
        _ = ApplyLocalFiltersAsync("Filtering results");
        ApplyBehavior();
        SearchContentsToggle.IsChecked = _config.Behavior.SearchContents;
        ExactMatchToggle.IsChecked = _config.Behavior.ExactMatch;
        StatusText = "Settings saved";
    }

    private void HelpClicked(object sender, RoutedEventArgs e)
    {
        var help = new HelpWindow(_config.Behavior.Theme) { Owner = this };
        help.ShowDialog();
    }

    private void RecreateQsirchClient()
    {
        CancelAllSearches("connection settings changed");
        var previous = _qsirchClient;
        _qsirchClient = new QsirchClient(_config);
        _retiredQsirchClients.Add(previous);
        AppLogger.Info("qsirch", "connection settings changed; active searches canceled and previous client retained until exit");
    }

    private void CancelAllSearches(string reason)
    {
        foreach (var tab in SearchTabs)
        {
            tab.CancelSearch(reason);
        }
        _paintCts?.Cancel();
        SetBusy(false);
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

    private IReadOnlyList<SearchResult> SelectedResults()
    {
        var selected = DetailsList.Visibility == Visibility.Visible
            ? DetailsList.SelectedItems.OfType<SearchResult>()
            : IconGrid.Visibility == Visibility.Visible
                ? IconGrid.SelectedItems.OfType<SearchResult>()
                : ExplorerList.SelectedItems.OfType<SearchResult>();
        return selected
            .DistinctBy(result => HistoryStore.ResultKey(result), StringComparer.OrdinalIgnoreCase)
            .ToList();
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

    private bool MatchesExactQuery(SearchResult result, string query)
    {
        if (!_config.Behavior.ExactMatch || _config.Behavior.SearchContents || query.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var terms = System.Text.RegularExpressions.Regex.Matches(query, "[\\p{L}\\p{N}_]+")
            .Select(match => match.Value)
            .Where(term => term.Length > 0);
        return terms.All(term => System.Text.RegularExpressions.Regex.IsMatch(
            result.FileName,
            $"(?<![\\p{{L}}\\p{{N}}_]){System.Text.RegularExpressions.Regex.Escape(term)}(?![\\p{{L}}\\p{{N}}_])",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase));
    }

    private IReadOnlyList<SearchResult> LimitRawResultsForDisplay(
        IEnumerable<SearchResult> results,
        int limit,
        string query,
        ISet<string>? collapsedFolders = null)
    {
        var raw = results.Where(result => !result.IsSearchFolderPresentation).ToList();
        if (!_config.Behavior.CollapseMatchingFolderResults)
        {
            return raw.Take(limit).ToList();
        }

        var displayed = new List<SearchResult>();
        var seen = collapsedFolders ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var result in raw)
        {
            var key = MatchingParentFolderPath(result, query) ?? HistoryStore.ResultKey(result);
            if (!seen.Add(key))
            {
                continue;
            }

            displayed.Add(result);
            if (displayed.Count >= limit)
            {
                break;
            }
        }
        return displayed;
    }

    private IReadOnlyList<SearchResult> BuildResultPresentation(
        IEnumerable<SearchResult> results,
        string query,
        ISet<string>? emittedFolderPaths = null)
    {
        var raw = results.Where(result => !result.IsSearchFolderPresentation).ToList();
        foreach (var result in raw)
        {
            result.IsMatchingSearchFolder = false;
            result.ExplorerGroup = null;
        }
        if (!_config.Behavior.ShowMatchingParentFolders && !_config.Behavior.CollapseMatchingFolderResults)
        {
            return raw;
        }

        var foldersAlreadyReturned = raw
            .Where(result => result.IsFolder)
            .Select(ResultItemPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var emitted = emittedFolderPaths ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var presentation = new List<SearchResult>();
        foreach (var result in raw)
        {
            var matchingFolderPath = MatchingParentFolderPath(result, query);
            if (matchingFolderPath != null)
            {
                var group = new ExplorerResultGroup(
                    matchingFolderPath,
                    Path.GetFileName(matchingFolderPath),
                    ParentPath(matchingFolderPath));
                result.ExplorerGroup = group;
                if (!foldersAlreadyReturned.Contains(matchingFolderPath) && emitted.Add(matchingFolderPath))
                {
                    presentation.Add(CreateSearchFolderResult(matchingFolderPath, result, group));
                }

                if (_config.Behavior.CollapseMatchingFolderResults &&
                    !ResultItemPath(result).Equals(matchingFolderPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (result.IsFolder && ResultItemPath(result).Equals(matchingFolderPath, StringComparison.OrdinalIgnoreCase))
                {
                    result.IsMatchingSearchFolder = true;
                }
            }
            else
            {
                result.ExplorerGroup = new ExplorerResultGroup("__other__", "Other results", "");
            }

            presentation.Add(result);
        }
        return presentation;
    }

    private string? MatchingParentFolderPath(SearchResult result, string query)
    {
        var candidate = result.IsFolder ? ResultItemPath(result) : ParentPath(ResultItemPath(result));
        while (!string.IsNullOrWhiteSpace(candidate))
        {
            if (FolderNameMatchesQuery(Path.GetFileName(candidate), query))
            {
                return candidate;
            }
            candidate = ParentPath(candidate);
        }
        return null;
    }

    private bool FolderNameMatchesQuery(string folderName, string query)
    {
        var searchText = query.StartsWith("name:", StringComparison.OrdinalIgnoreCase)
            ? query[5..].Trim().Trim('"')
            : query.Trim();
        var terms = System.Text.RegularExpressions.Regex.Matches(searchText, "[\\p{L}\\p{N}_]+")
            .Select(match => match.Value)
            .Where(term => term.Length > 0)
            .ToList();
        if (terms.Count == 0 || string.IsNullOrWhiteSpace(folderName))
        {
            return false;
        }

        if (!_config.Behavior.ExactMatch || _config.Behavior.SearchContents)
        {
            return terms.All(term => folderName.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        return terms.All(term => System.Text.RegularExpressions.Regex.IsMatch(
            folderName,
            $"(?<![\\p{{L}}\\p{{N}}_]){System.Text.RegularExpressions.Regex.Escape(term)}(?![\\p{{L}}\\p{{N}}_])",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase));
    }

    private static string ResultItemPath(SearchResult result)
    {
        var path = (result.Path ?? "").Replace('/', '\\').TrimEnd('\\');
        var name = result.FileName.Trim();
        if (!string.IsNullOrWhiteSpace(name) &&
            !path.Equals(name, StringComparison.OrdinalIgnoreCase) &&
            !path.EndsWith("\\" + name, StringComparison.OrdinalIgnoreCase))
        {
            path = string.IsNullOrWhiteSpace(path) ? name : path + "\\" + name;
        }
        return path;
    }

    private static string ParentPath(string path)
    {
        var trimmed = path.TrimEnd('\\');
        var separator = trimmed.LastIndexOf('\\');
        return separator <= 0 ? "" : trimmed[..separator];
    }

    private static bool MatchesScope(SearchResult result, SearchTabState tab)
    {
        return tab.ScopeKey switch
        {
            "recent" => result.ModifiedDate is { } modified && modified.Date >= DateTime.Today.AddDays(-30),
            "folder" when !string.IsNullOrWhiteSpace(tab.ScopePath) =>
                ResultItemPath(result).Equals(tab.ScopePath, StringComparison.OrdinalIgnoreCase) ||
                ResultItemPath(result).StartsWith(tab.ScopePath.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase),
            "folder" => false,
            _ => true,
        };
    }

    private static SearchResult CreateSearchFolderResult(string folderPath, SearchResult source, ExplorerResultGroup group) => new()
    {
        Name = Path.GetFileName(folderPath),
        Path = folderPath,
        ResolvedPath = source.IsFolder ? source.ResolvedPath : ParentPath(source.ResolvedPath),
        WindowsPath = source.IsFolder ? source.WindowsPath : ParentPath(source.WindowsPath),
        ShowInternalPath = source.ShowInternalPath,
        Type = "folder",
        Modified = source.Modified,
        IsFolder = true,
        IsSearchFolderPresentation = true,
        IsMatchingSearchFolder = true,
        ExplorerGroup = group,
    };

    private void UpdateResultLocationBar(SearchResult? result = null)
    {
        result ??= SelectedResult();
        ResultLocationText = result?.DisplayPath ?? "";
        OnPropertyChanged(nameof(ResultLocationVisibility));
    }

    private void PopulateDisplayPaths(IEnumerable<SearchResult> results)
    {
        foreach (var result in results)
        {
            result.ShowInternalPath = _config.Behavior.ShowQsirchInternalPaths;
            if (string.IsNullOrWhiteSpace(result.WindowsPath))
            {
                result.WindowsPath = _mapper.TryResolve(result) ?? "";
            }
        }
    }

    private void ApplyPathPresentation()
    {
        foreach (var result in _allResults.Concat(VisibleResults).Concat(FavoriteResults).Distinct())
        {
            result.ShowInternalPath = _config.Behavior.ShowQsirchInternalPaths;
        }
    }

    private bool ShouldShowResultLocationBar()
    {
        var mode = (ViewBox.SelectedItem as ResultViewMode)?.Key ?? "details";
        return mode != "details" && !string.IsNullOrWhiteSpace(ResultLocationText);
    }

    private async Task ApplyLocalFiltersAsync(string statusText)
    {
        var typeFilter = SelectedTypeFilter();
        var snapshot = _allResults.Where(result => !result.IsSearchFolderPresentation).ToList();
        AppLogger.Info("filter", $"status=\"{statusText}\" snapshot={snapshot.Count} type=\"{typeFilter.Name}\" sort=\"{_sortColumn}\" descending={_sortDescending}");
        var tab = _selectedSearchTab;
        var filtered = await Task.Run(() => snapshot
            .Where(result => MatchesType(result, typeFilter))
            .Where(result => tab == null || MatchesScope(result, tab))
            .ToList());
        if (tab != null)
        {
            var limited = LimitRawResultsForDisplay(filtered, tab.ResultLimit, tab.ResultQuery);
            await PaintVisibleResultsAsync(tab, limited, NextPaintToken(), statusText);
            EmptyStateText = filtered.Count > 0
                ? ""
                : snapshot.Count == 0 ? "No results found" : "No results match the current filters";
        }
    }

    private void ApplyLocalFilters()
    {
        var typeFilter = SelectedTypeFilter();
        var filtered = _allResults
            .Where(result => !result.IsSearchFolderPresentation)
            .Where(result => MatchesType(result, typeFilter));
        var tab = _selectedSearchTab;
        if (tab != null)
        {
            filtered = filtered.Where(result => MatchesScope(result, tab));
        }
        var limited = LimitRawResultsForDisplay(filtered, tab?.ResultLimit ?? configuredResultLimit(), tab?.ResultQuery ?? Query);
        VisibleResults.ReplaceAll(SortResults(BuildResultPresentation(limited, tab?.ResultQuery ?? Query)));
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
        var raw = results.Where(result => !result.IsSearchFolderPresentation).ToList();
        var sorted = await Task.Run(() => SortResults(BuildResultPresentation(raw, tab.ResultQuery)).ToList(), token);
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

        tab.AllResults.AddRange(raw);
        if (IsActiveTab(tab))
        {
            _allResults.AddRange(raw);
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
            tab.VisibleResults.AddRange(visibleBatch);
            SetTabStatus(tab, statusText, ResultCountText(tab.VisibleResults.Count));

            if (IsActiveTab(tab))
            {
                VisibleResults.AddRange(visibleBatch);
                CountText = ResultCountText(VisibleResults.Count);
                QueueInitialResultIcons(tab, batch, paintToken);
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
        var sorted = await Task.Run(() => SortResults(BuildResultPresentation(results, tab.ResultQuery)).ToList(), token);
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
                QueueInitialResultIcons(tab, batch, token);
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
        await Dispatcher.InvokeAsync(() =>
        {
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
        return result.IsFolder
            ? typeFilter.IncludeFolders
            : typeFilter.IncludeAllFiles || typeFilter.Extensions.Contains(result.Extension, StringComparer.OrdinalIgnoreCase);
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
        IOrderedEnumerable<(SearchResult item, int index)> ordered = indexed
            .OrderBy(x => x.item.IsMatchingSearchFolder ? 0 : _config.Behavior.FoldersFirst && x.item.IsFolder ? 1 : 2);
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
            "relevance" => ordered,
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
            "folder" => result.ExplorerGroup?.Name ?? "Other results",
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
        ConfigureFolderGrouping();
    }

    private void ConfigureFolderGrouping()
    {
        GroupedDetailsResults.GroupDescriptions.Clear();
        if (_sortKeys.Any(sort => sort.Key.Equals("folder", StringComparison.OrdinalIgnoreCase)))
        {
            GroupedDetailsResults.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SearchResult.ExplorerGroup)));
        }
        GroupedDetailsResults.Refresh();
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
            "folder" or "folder groups" => "folder",
            "path" => "location",
            "location" or "name" or "modified" or "recent" or "relevance" or "type" or "size" => key.ToLowerInvariant(),
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
            "folder" => "Folder groups",
            "relevance" => "Relevance",
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
        var selected = TypeFilterOptions.Where(option => option.IsSelected).Select(option => option.Filter).ToList();
        if (selected.Count == 0)
        {
            return TypeFilters[0];
        }

        if (selected.Count == 1)
        {
            return selected[0];
        }

        return new FileTypeFilter
        {
            Name = TypeFilterSummary,
            Extensions = selected.SelectMany(filter => filter.Extensions).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            IncludeFolders = selected.Any(filter => filter.IncludeFolders),
        };
    }

    private IEnumerable<string> SelectedTypeNames() => TypeFilterOptions
        .Where(option => option.IsSelected)
        .Select(option => option.Name);

    private void ApplyTypeSelection(IEnumerable<string>? typeNames, int legacyTypeIndex)
    {
        var selected = (typeNames ?? [])
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selected.Count == 0 && legacyTypeIndex > 0 && legacyTypeIndex < TypeFilters.Count)
        {
            selected.Add(TypeFilters[legacyTypeIndex].Name);
        }

        foreach (var option in TypeFilterOptions)
        {
            option.IsSelected = selected.Contains(option.Name);
        }
        OnPropertyChanged(nameof(TypeFilterSummary));
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

    private void QueueInitialResultIcons(SearchTabState tab, IEnumerable<SearchResult> results, CancellationToken token)
    {
        if (!IsActiveTab(tab))
        {
            return;
        }

        var pending = results
            .Where(result => result.IconSource == null)
            .ToList();
        if (pending.Count > 0)
        {
            _ = LoadResultIconsAsync(pending, token);
        }
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
                    var thumbnail = await _qsirchClient.ThumbnailAsync(result, token);
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
                    ApplyCachedIcon(key, result, icon);
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

    private void ApplyCachedIcon(string key, SearchResult result, ImageSource icon)
    {
        result.IconSource = icon;

        // File-type icons are shared intentionally: one Shell lookup fills every matching result.
        if (_config.Behavior.UseQsirchThumbnails && result.HasThumbnailAction)
        {
            return;
        }

        foreach (var candidate in SearchTabs
                     .SelectMany(tab => tab.VisibleResults)
                     .Append(result)
                     .Distinct())
        {
            if (candidate.IconSource == null &&
                (!(_config.Behavior.UseQsirchThumbnails && candidate.HasThumbnailAction)) &&
                string.Equals(IconCacheKey(candidate), key, StringComparison.OrdinalIgnoreCase))
            {
                candidate.IconSource = icon;
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
        _openFolderIconResult = null;
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
        return SelectedResult() ?? SelectedFavoriteResult();
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
        if (TryShowNativePreview(result))
        {
            return;
        }
        var preview = await BuildPreviewAsync(result);
        if (CurrentPreviewResult() == result && _config.Behavior.PreviewPane)
        {
            RenderPreview(preview);
        }
    }

    private void RequestPreview(SearchResult? result)
    {
        _previewCts?.Cancel();
        if (!_config.Behavior.PreviewPane || result == null)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _previewCts = cts;
        _ = LoadPreviewAfterSelectionSettlesAsync(result, cts.Token);
    }

    private async Task LoadPreviewAfterSelectionSettlesAsync(SearchResult result, CancellationToken token)
    {
        try
        {
            await Task.Delay(180, token);
            if (!token.IsCancellationRequested && CurrentPreviewResult() == result)
            {
                await LoadPreviewAsync(result);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task<PreviewContent> BuildPreviewAsync(SearchResult result)
    {
        var header = $"{result.FileName}\r\n{result.DisplayPath}\r\n{result.Kind} {result.SizeText}".Trim();
        if (ShellPreviewService.IsVideoFile(result.Extension))
        {
            return PreviewContent.ForText($"{header}\r\n\r\nVideo preview is unavailable. Open the file to play it.");
        }
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
        ClearNativePreview();
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
        ClearNativePreview();
        PreviewImage.Source = null;
        PreviewImageHost.Visibility = Visibility.Collapsed;
        PreviewTextBox.Visibility = Visibility.Visible;
    }

    private bool TryShowNativePreview(SearchResult result)
    {
        if (result.IsFolder || ShellPreviewService.IsVideoFile(result.Extension))
        {
            return false;
        }

        try
        {
            var mappedPath = _mapper.Resolve(result);
            AppLogger.Info("preview", $"mapped preview path=\"{mappedPath}\" sourcePath=\"{result.Path}\" name=\"{result.Name}\" extension=\"{result.Extension}\"");
            var host = ShellPreviewHost.TryCreate(mappedPath);
            if (host == null)
            {
                return false;
            }

            ClearNativePreview();
            host.PreviewFailed += NativePreviewFailed;
            _nativePreviewHost = host;
            NativePreviewHost.Content = host;
            NativePreviewHost.Visibility = Visibility.Visible;
            PreviewImage.Source = null;
            PreviewImageHost.Visibility = Visibility.Collapsed;
            PreviewTextBox.Visibility = Visibility.Collapsed;
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error("preview", ex, $"native preview could not be created path=\"{result.DisplayPath}\"");
            return false;
        }
    }

    private void ClearNativePreview()
    {
        var host = _nativePreviewHost;
        _nativePreviewHost = null;
        if (host != null)
        {
            host.PreviewFailed -= NativePreviewFailed;
        }
        NativePreviewHost.Content = null;
        NativePreviewHost.Visibility = Visibility.Collapsed;
        host?.Dispose();
    }

    private void NativePreviewFailed(object? sender, PreviewFailureEventArgs args)
    {
        if (!ReferenceEquals(sender, _nativePreviewHost))
        {
            return;
        }

        ClearNativePreview();
        PreviewText = args.Message;
        ShowPreviewText();
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

    private async Task LoadFavoritesAsync()
    {
        var loadVersion = ++_favoritesLoadVersion;
        var snapshot = await Task.Run(() =>
        {
            var loaded = new FavoriteSnapshot(_history.Favorites(), _history.SavedSearches());
            PopulateDisplayPaths(loaded.Results);
            return loaded;
        });
        if (loadVersion != _favoritesLoadVersion || _isExiting)
        {
            return;
        }
        FavoriteResults.ReplaceAll(snapshot.Results);
        SavedSearches.Clear();
        foreach (var savedSearch in snapshot.SavedSearches)
        {
            SavedSearches.Add(savedSearch);
        }
        RefreshFavoritesTree();
    }

    private async Task LoadRecentSearchesAsync()
    {
        var searches = await Task.Run(() => _history.RecentSearches());
        RecentSearches.Clear();
        foreach (var search in searches)
        {
            RecentSearches.Add(search);
        }
    }

    private async Task RecordRecentSearchAsync(string query)
    {
        await Task.Run(() => _history.RecordSearch(query));
        await Dispatcher.InvokeAsync(() => { });
        await LoadRecentSearchesAsync();
    }

    private void RefreshFavoritesTree(IEnumerable<string>? foldersToExpand = null)
    {
        if (FavoritesTree == null)
        {
            return;
        }
        var expandedFolders = FavoriteTreeNodes(FavoritesTree.ItemsSource)
            .Where(node => node.IsFolder && node.IsExpanded)
            .Select(node => node.FolderPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in foldersToExpand ?? [])
        {
            var path = "";
            foreach (var part in folder.Split('\\', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                path = string.IsNullOrWhiteSpace(path) ? part : path + "\\" + part;
                expandedFolders.Add(path);
            }
        }
        var selectedKey = SelectedFavoriteResult() is { } selected
            ? HistoryStore.ResultKey(selected)
            : null;
        FavoritesTree.ItemsSource = BuildFavoritesTree(FavoriteResults, SavedSearches, expandedFolders, selectedKey);
    }

    private sealed record FavoriteSnapshot(IReadOnlyList<SearchResult> Results, IReadOnlyList<SavedSearch> SavedSearches);
    private sealed record FavoriteGroupDialogData(IReadOnlyList<string> Groups, IReadOnlyList<string> SelectedGroups);

    private static IEnumerable<FavoriteTreeNode> FavoriteTreeNodes(object? source)
    {
        if (source is not IEnumerable<FavoriteTreeNode> nodes)
        {
            return [];
        }
        return nodes.SelectMany(node => new[] { node }.Concat(FavoriteTreeNodes(node.Children)));
    }

    private static IReadOnlyList<FavoriteTreeNode> BuildFavoritesTree(
        IEnumerable<SearchResult> favorites,
        IEnumerable<SavedSearch> savedSearches,
        ISet<string> expandedFolders,
        string? selectedKey)
    {
        var roots = new List<FavoriteTreeNode>();
        var folders = new Dictionary<string, FavoriteTreeNode>(StringComparer.OrdinalIgnoreCase);
        FavoriteTreeNode? unfiled = null;

        var saved = savedSearches.OrderBy(search => search.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (saved.Count > 0)
        {
            var savedRoot = new FavoriteTreeNode { Name = "Saved searches", FolderPath = "__saved_searches__", IsExpanded = expandedFolders.Contains("__saved_searches__") || expandedFolders.Count == 0 };
            foreach (var savedSearch in saved)
            {
                savedRoot.Children.Add(new FavoriteTreeNode { Name = savedSearch.Name, SavedSearch = savedSearch });
            }
            roots.Add(savedRoot);
        }

        foreach (var result in favorites
                     .DistinctBy(item => HistoryStore.ResultKey(item), StringComparer.OrdinalIgnoreCase)
                     .OrderBy(item => item.Groups.FirstOrDefault() ?? "\uffff", StringComparer.CurrentCultureIgnoreCase)
                     .ThenBy(item => item.FileName, StringComparer.CurrentCultureIgnoreCase))
        {
            var group = result.Groups.FirstOrDefault(group => !string.IsNullOrWhiteSpace(group))?.Trim().Replace('/', '\\').Trim('\\');
            if (string.IsNullOrWhiteSpace(group))
            {
                unfiled ??= new FavoriteTreeNode
                {
                    Name = "Unfiled favorites",
                    FolderPath = "__unfiled__",
                    IsExpanded = expandedFolders.Contains("__unfiled__") || expandedFolders.Count == 0,
                };
                if (!roots.Contains(unfiled))
                {
                    roots.Add(unfiled);
                }
                unfiled.Children.Add(new FavoriteTreeNode
                {
                    Name = result.FileName,
                    Result = result,
                    IsSelected = selectedKey != null && selectedKey.Equals(HistoryStore.ResultKey(result), StringComparison.OrdinalIgnoreCase),
                });
                continue;
            }

            var path = "";
            ICollection<FavoriteTreeNode> siblings = roots;
            FavoriteTreeNode? parent = null;
            foreach (var part in group.Split('\\', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                path = string.IsNullOrWhiteSpace(path) ? part : path + "\\" + part;
                if (!folders.TryGetValue(path, out var node))
                {
                    node = new FavoriteTreeNode
                    {
                        Name = part,
                        FolderPath = path,
                        IsExpanded = expandedFolders.Contains(path) || (expandedFolders.Count == 0 && !path.Contains('\\')),
                    };
                    folders[path] = node;
                    siblings.Add(node);
                }
                siblings = node.Children;
                parent = node;
            }
            parent!.Children.Add(new FavoriteTreeNode
            {
                Name = result.FileName,
                Result = result,
                IsSelected = selectedKey != null && selectedKey.Equals(HistoryStore.ResultKey(result), StringComparison.OrdinalIgnoreCase),
            });
        }
        return roots;
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
        OnPropertyChanged(nameof(EmptyStateVisibility));
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

internal sealed record ConnectionSettings(string Host, int Port, bool Ssl, bool SslVerify, string User, string Password, int TimeoutSeconds)
{
    public static ConnectionSettings Current(AppConfig config) => new(
        config.Host,
        config.Port,
        config.Ssl,
        config.SslVerify,
        config.User,
        config.Password,
        config.Behavior.SearchTimeoutSeconds);
}

internal sealed class NasStreamPaintState(HashSet<string> starred)
{
    public HashSet<string> Starred { get; } = starred;
    public HashSet<string> Seen { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> EmittedFolderPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> CollapsedFolderPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<SearchResult> Received { get; } = [];
    private readonly HashSet<string> _receivedKeys = new(StringComparer.OrdinalIgnoreCase);
    public bool Started { get; set; }
    public int VisibleCount { get; set; }
    public int DisplayedResultCount { get; set; }

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

internal readonly record struct ResultAddCounts(int Added, int DisplayableAdded);

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
    public string ResultQuery { get; set; } = "";
    public int ResultLimit { get; set; } = 500;
    public bool ResultLimitReached { get; set; }
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
    public List<string> TypeNames { get; set; } = [];
    public string ScopeKey { get; set; } = "all";
    public string ScopePath { get; set; } = "";
    public bool SearchOnFirstFocus { get; set; }
    public int IconLoadRequests { get; set; }
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
