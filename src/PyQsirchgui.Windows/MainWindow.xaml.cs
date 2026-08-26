using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
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
    private readonly ShellActions _shell;
    private string _query = "";
    private string _statusText = "Ready";
    private string _countText = "";
    private string _previewText = "Select a result to preview.";
    private bool _isBusy;
    private double _iconGlyphSize = 54;
    private double _iconTileWidth = 150;
    private double _iconTileHeight = 118;
    private CancellationTokenSource? _searchCts;

    public MainWindow()
    {
        InitializeComponent();
        _history = new HistoryStore(_config);
        _shell = new ShellActions(new PathMapper(_config));
        DataContext = this;
        LoadFavorites();
        TypeBox.SelectedIndex = 0;
        var configuredView = string.IsNullOrWhiteSpace(_config.Behavior.ResultView) ? "details" : _config.Behavior.ResultView;
        ViewBox.SelectedItem = ViewModes.FirstOrDefault(x => x.Key.Equals(configuredView, StringComparison.OrdinalIgnoreCase)) ?? ViewModes[^1];
        ApplyViewMode();
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
        new() { Name = "Email", Category = "Email", Extensions = ["eml", "msg"] },
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

        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        SetBusy(true);
        StatusText = "Searching...";
        CountText = "";

        try
        {
            using var client = new QsirchClient(_config);
            var typeFilter = SelectedTypeFilter();
            var results = await client.SearchAsync(query, typeFilter, _searchCts.Token);
            _allResults.Clear();
            foreach (var result in SortResults(results))
            {
                _allResults.Add(result);
            }
            ApplyLocalFilters();
            StatusText = "Ready";
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
    }

    private void ResultSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var result = SelectedResult();
        PreviewText = result == null ? "Select a result to preview." : $"{result.FileName}\r\n{result.DisplayPath}\r\n{result.Kind} {result.SizeText}".Trim();
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
        if (!FavoriteResults.Any(x => x.Path.Equals(result.Path, StringComparison.OrdinalIgnoreCase) && x.FileName.Equals(result.FileName, StringComparison.OrdinalIgnoreCase)))
        {
            FavoriteResults.Insert(0, result);
        }
    }

    private void SettingsClicked(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this, "Settings will move into the native UI after the Explorer view is stable. For now, edit config.json or use the Python settings window.", "Settings");
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

    private IEnumerable<SearchResult> SortResults(IEnumerable<SearchResult> results)
    {
        if (!_config.Behavior.FoldersFirst)
        {
            return results;
        }
        return results.Select((item, index) => new { item, index })
            .OrderBy(x => x.item.IsFolder ? 0 : 1)
            .ThenBy(x => x.index)
            .Select(x => x.item)
            .ToList();
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
