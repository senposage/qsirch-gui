using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PyQsirchgui.Windows.Models;
using PyQsirchgui.Windows.Services;

namespace PyQsirchgui.Windows;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly AppConfig _config = ConfigStore.Load();
    private readonly ObservableCollection<SearchResult> _allResults = [];
    private readonly HistoryStore _history;
    private readonly PathMapper _mapper;
    private readonly ShellActions _shell;
    private ResultRules _rules;
    private string _query = "";
    private string _statusText = "Ready";
    private string _countText = "";
    private string _previewText = "Select a result to preview.";
    private bool _isBusy;
    private double _iconGlyphSize = 54;
    private double _iconTileWidth = 150;
    private double _iconTileHeight = 118;
    private string _sortColumn = "name";
    private bool _sortDescending;
    private CancellationTokenSource? _searchCts;

    public MainWindow()
    {
        InitializeComponent();
        _history = new HistoryStore(_config);
        _mapper = new PathMapper(_config);
        _shell = new ShellActions(_mapper);
        _rules = new ResultRules(_config);
        DataContext = this;
        LoadFavorites();
        TypeBox.SelectedIndex = 0;
        var configuredView = string.IsNullOrWhiteSpace(_config.Behavior.ResultView) ? "details" : _config.Behavior.ResultView;
        ViewBox.SelectedItem = ViewModes.FirstOrDefault(x => x.Key.Equals(configuredView, StringComparison.OrdinalIgnoreCase)) ?? ViewModes[^1];
        ApplyViewMode();
        ApplyBehavior();
        AlwaysOnTopToggle.IsChecked = _config.AlwaysOnTop;
    }

    public ObservableCollection<SearchResult> VisibleResults { get; } = [];
    public ObservableCollection<SearchResult> FavoriteResults { get; } = [];

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

    public string Query
    {
        get => _query;
        set => SetField(ref _query, value);
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

    private async void SearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await SearchAsync();
        }
    }

    private void ClearClicked(object sender, RoutedEventArgs e)
    {
        Query = "";
        _allResults.Clear();
        VisibleResults.Clear();
        PreviewText = "Select a result to preview.";
        StatusText = "Ready";
        CountText = "";
        SearchText.Focus();
    }

    private async Task SearchAsync()
    {
        var query = Query.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            return;
        }
        var serverQuery = query == "*" ? "." : query;

        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        SetBusy(true);
        StatusText = "Searching...";
        CountText = "";

        try
        {
            var cached = _history.SearchResults(query, _config.History.SourceFilter)
                .Where(result => !_rules.IsHidden(result))
                .ToList();
            var showedCached = false;
            if (cached.Count > 0)
            {
                ReplaceResults(cached);
                StatusText = "Saved results; checking NAS...";
                showedCached = true;
            }

            using var client = new QsirchClient(_config);
            var typeFilter = SelectedTypeFilter();
            var results = await client.SearchAsync(serverQuery, typeFilter, _searchCts.Token);
            if (showedCached && results.Count == 0)
            {
                StatusText = "Saved results; NAS returned none";
                return;
            }
            var visible = await Task.Run(() => results.Where(result => !_rules.IsHidden(result)).ToList());
            await Task.Run(() => _history.AddResults(results));
            ReplaceResults(visible);
            LoadFavorites();
            StatusText = visible.Count == 0 && results.Count > 0 ? "No visible results" : "Ready";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusText = "Error";
            MessageBox.Show(this, ex.Message, "PyQsirchgui", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void TypeChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyLocalFilters();
    }

    private void ViewChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyViewMode();
        if (ViewBox.SelectedItem is ResultViewMode mode)
        {
            _config.Behavior.ResultView = mode.Key;
            ConfigStore.Save(_config);
        }
    }

    private void DetailsHeaderClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not GridViewColumnHeader header || header.Tag is not string column)
        {
            return;
        }
        if (_sortColumn.Equals(column, StringComparison.OrdinalIgnoreCase))
        {
            _sortDescending = !_sortDescending;
        }
        else
        {
            _sortColumn = column;
            _sortDescending = false;
        }
        ApplyLocalFilters();
        StatusText = $"Sorted by {header.Content}{(_sortDescending ? " descending" : "")}";
    }

    private void ResultSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var result = SelectedResult();
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
        while (current != null && current is not ListBoxItem)
        {
            current = VisualTreeHelper.GetParent(current);
        }
        if (current is ListBoxItem item)
        {
            item.IsSelected = true;
            item.Focus();
        }
    }

    private void FavoriteSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var result = FavoritesList.SelectedItem as SearchResult;
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
        _history.SetStarred(result, false);
        FavoriteResults.Remove(result);
        var visible = VisibleResults.FirstOrDefault(x => x.Path.Equals(result.Path, StringComparison.OrdinalIgnoreCase) && x.FileName.Equals(result.FileName, StringComparison.OrdinalIgnoreCase));
        if (visible != null)
        {
            visible.IsFavorite = false;
        }
        StatusText = "Removed from Favorites";
    }

    private void OpenFavorite()
    {
        if (FavoritesList.SelectedItem is not SearchResult result)
        {
            return;
        }
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
        var result = SelectedResult();
        if (result == null)
        {
            return;
        }
        result.IsFavorite = true;
        _history.SetStarred(result, true);
        if (!FavoriteResults.Any(x => x.Path.Equals(result.Path, StringComparison.OrdinalIgnoreCase) && x.FileName.Equals(result.FileName, StringComparison.OrdinalIgnoreCase)))
        {
            FavoriteResults.Insert(0, result);
        }
        StatusText = "Added to Favorites";
    }

    private void AlwaysOnTopChanged(object sender, RoutedEventArgs e)
    {
        _config.AlwaysOnTop = AlwaysOnTopToggle.IsChecked == true;
        ApplyBehavior();
        ConfigStore.Save(_config);
    }

    private void PreviewToggleClicked(object sender, RoutedEventArgs e)
    {
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
        _config.Behavior.PreviewPane = false;
        ApplyBehavior();
        ConfigStore.Save(_config);
    }

    private void HideClicked(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    private void ExitClicked(object sender, RoutedEventArgs e)
    {
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
        ApplyViewMode();
        ApplyLocalFilters();
        ApplyBehavior();
        AlwaysOnTopToggle.IsChecked = _config.AlwaysOnTop;
        StatusText = "Settings saved";
    }

    private void OpenSelected()
    {
        var result = SelectedResult();
        if (result == null)
        {
            return;
        }
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
        return DetailsList.Visibility == Visibility.Visible
            ? DetailsList.SelectedItem as SearchResult
            : ExplorerList.SelectedItem as SearchResult;
    }

    private void ApplyLocalFilters()
    {
        var typeFilter = SelectedTypeFilter();
        var filtered = _allResults.Where(result =>
            typeFilter.Extensions.Length == 0 ||
            typeFilter.Extensions.Contains(result.Extension, StringComparer.OrdinalIgnoreCase));
        VisibleResults.Clear();
        foreach (var result in SortResults(filtered))
        {
            VisibleResults.Add(result);
        }
        CountText = VisibleResults.Count == 0 ? "" : $"{VisibleResults.Count:n0} result{(VisibleResults.Count == 1 ? "" : "s")}";
    }

    private void ReplaceResults(IEnumerable<SearchResult> results)
    {
        var starred = _history.StarredKeys();
        _allResults.Clear();
        foreach (var result in SortResults(results))
        {
            result.IsFavorite = starred.Contains(HistoryStore.ResultKey(result));
            _allResults.Add(result);
        }
        ApplyLocalFilters();
    }

    private IEnumerable<SearchResult> SortResults(IEnumerable<SearchResult> results)
    {
        var indexed = results.Select((item, index) => (item, index));
        IOrderedEnumerable<(SearchResult item, int index)> ordered = _config.Behavior.FoldersFirst
            ? indexed.OrderBy(x => x.item.IsFolder ? 0 : 1)
            : indexed.OrderBy(x => 0);
        ordered = _sortDescending
            ? ordered.ThenByDescending(x => SortValue(x.item, _sortColumn)).ThenBy(x => x.index)
            : ordered.ThenBy(x => SortValue(x.item, _sortColumn)).ThenBy(x => x.index);
        return ordered.Select(x => x.item).ToList();
    }

    private static object SortValue(SearchResult result, string column)
    {
        return column switch
        {
            "location" => result.DisplayPath,
            "type" => result.Kind,
            "size" => result.Size,
            "modified" => result.Modified,
            _ => result.FileName,
        };
    }

    private FileTypeFilter SelectedTypeFilter()
    {
        return TypeBox.SelectedItem as FileTypeFilter ?? TypeFilters[0];
    }

    private void ApplyViewMode()
    {
        var mode = (ViewBox.SelectedItem as ResultViewMode)?.Key ?? "details";
        DetailsList.Visibility = mode == "details" ? Visibility.Visible : Visibility.Collapsed;
        ExplorerList.Visibility = mode == "details" ? Visibility.Collapsed : Visibility.Visible;
        ExplorerList.ItemTemplate = mode == "list"
            ? (DataTemplate)FindResource("ExplorerListTemplate")
            : (DataTemplate)FindResource("ExplorerIconTemplate");

        switch (mode)
        {
            case "large_icons":
                IconGlyphSize = 62;
                IconTileWidth = 150;
                IconTileHeight = 122;
                break;
            case "small_icons":
                IconGlyphSize = 28;
                IconTileWidth = 126;
                IconTileHeight = 70;
                break;
            case "list":
                IconGlyphSize = 18;
                IconTileWidth = 260;
                IconTileHeight = 28;
                break;
        }
    }

    private void ApplyBehavior()
    {
        Topmost = _config.AlwaysOnTop;
        ShowInTaskbar = _config.Behavior.ShowInTaskbar;
        PreviewPane.Visibility = _config.Behavior.PreviewPane ? Visibility.Visible : Visibility.Collapsed;
        PreviewSplitter.Visibility = _config.Behavior.PreviewPane ? Visibility.Visible : Visibility.Collapsed;
        PreviewToggle.FontWeight = _config.Behavior.PreviewPane ? FontWeights.SemiBold : FontWeights.Normal;
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
            return;
        }
        PreviewTitle.Text = result.FileName;
        PreviewText = $"{result.FileName}\r\n{result.DisplayPath}\r\n{result.Kind} {result.SizeText}".Trim();
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
        var preview = await BuildPreviewAsync(result);
        if (CurrentPreviewResult() == result && _config.Behavior.PreviewPane)
        {
            PreviewText = preview;
        }
    }

    private async Task<string> BuildPreviewAsync(SearchResult result)
    {
        var header = $"{result.FileName}\r\n{result.DisplayPath}\r\n{result.Kind} {result.SizeText}".Trim();
        if (result.IsFolder)
        {
            return header;
        }

        var local = await Task.Run(() => BuildLocalTextPreview(result, header));
        if (!string.IsNullOrWhiteSpace(local))
        {
            return local;
        }

        try
        {
            using var client = new QsirchClient(_config);
            var summary = await client.PreviewSummaryAsync(result, _searchCts?.Token ?? CancellationToken.None);
            if (!string.IsNullOrWhiteSpace(summary))
            {
                return $"{header}\r\n\r\n{summary}";
            }
        }
        catch (Exception ex)
        {
            return $"{header}\r\n\r\nPreview unavailable: {ex.Message}";
        }

        return $"{header}\r\n\r\nPreview unavailable.";
    }

    private string BuildLocalTextPreview(SearchResult result, string header)
    {
        if (!IsTextPreviewType(result.Extension))
        {
            return "";
        }

        string path;
        try
        {
            path = _mapper.Resolve(result);
        }
        catch
        {
            return "";
        }

        if (!File.Exists(path))
        {
            return "";
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var builder = new StringBuilder();
            for (var i = 0; i < 180 && !reader.EndOfStream; i++)
            {
                builder.AppendLine(reader.ReadLine());
                if (builder.Length > 24000)
                {
                    break;
                }
            }
            return $"{header}\r\n\r\n{builder}".TrimEnd();
        }
        catch
        {
            return "";
        }
    }

    private static bool IsTextPreviewType(string extension)
    {
        return new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "txt", "csv", "log", "md", "json", "xml", "html", "htm", "css", "js", "ts", "py", "ps1", "bat", "cmd", "sql"
        }.Contains(extension.TrimStart('.'));
    }

    private void LoadFavorites()
    {
        FavoriteResults.Clear();
        foreach (var result in _history.Favorites())
        {
            FavoriteResults.Add(result);
        }
    }

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        OnPropertyChanged(nameof(BusyVisibility));
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }
        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
