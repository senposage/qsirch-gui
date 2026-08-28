using System.Collections.ObjectModel;
using System.Windows.Controls;
using System.Windows;
using System.Windows.Input;
using PyQsirchgui.Windows.Models;
using PyQsirchgui.Windows.Services;

namespace PyQsirchgui.Windows;

public partial class SettingsWindow : Window
{
    private readonly AppConfig _config;

    public SettingsWindow(AppConfig config)
    {
        InitializeComponent();
        StateChanged += (_, _) => UpdateCaptionButtons();
        _config = config;
        ThemePalette.Apply(Resources, string.IsNullOrWhiteSpace(_config.Behavior.Theme) ? "system" : _config.Behavior.Theme);
        Mappings = new ObservableCollection<PathMapping>(_config.PathMappings.Select(x => new PathMapping { ShareRoot = x.ShareRoot, MappedRoot = x.MappedRoot }));
        FolderRules = new ObservableCollection<TextRule>(_config.Exclude.FolderRules.Select(x => new TextRule { Pattern = x.Pattern, IsGlobal = x.IsGlobal }));
        FileRules = new ObservableCollection<TextRule>(_config.Exclude.FileRules.Select(x => new TextRule { Pattern = x.Pattern, IsGlobal = x.IsGlobal }));
        VisibilityRules = new ObservableCollection<VisibilityRule>(_config.VisibilityRules.Select(CloneVisibilityRule));
        MappingsGrid.ItemsSource = Mappings;
        FolderRulesGrid.ItemsSource = FolderRules;
        FileRulesGrid.ItemsSource = FileRules;
        VisibilityRulesGrid.ItemsSource = VisibilityRules;
        LoadValues();
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

    public ObservableCollection<PathMapping> Mappings { get; }
    public ObservableCollection<TextRule> FolderRules { get; }
    public ObservableCollection<TextRule> FileRules { get; }
    public ObservableCollection<VisibilityRule> VisibilityRules { get; }
    public bool ClearHistoryRequested { get; private set; }
    public bool ClearStarredRequested { get; private set; }
    public bool ResetDatabaseRequested { get; private set; }

    private void LoadValues()
    {
        HostBox.Text = _config.Host;
        PortBox.Text = _config.Port.ToString();
        UserBox.Text = _config.User;
        PasswordBox.Password = _config.Password;
        SslBox.IsChecked = _config.Ssl;
        SslVerifyBox.IsChecked = _config.SslVerify;
        TaskbarBox.IsChecked = _config.Behavior.ShowInTaskbar;
        MinimizeToTrayBox.IsChecked = _config.Behavior.MinimizeToTray;
        ExitToTrayBox.IsChecked = _config.Behavior.ExitToTray;
        ClearResultsWithQueryBox.IsChecked = _config.Behavior.ClearResultsWithQuery;
        AlwaysOnTopBox.IsChecked = _config.AlwaysOnTop;
        FoldersFirstBox.IsChecked = _config.Behavior.FoldersFirst;
        MatchingFoldersBox.IsChecked = _config.Behavior.ShowMatchingParentFolders;
        CollapseMatchingFoldersBox.IsChecked = _config.Behavior.CollapseMatchingFolderResults;
        SearchContentsBox.IsChecked = _config.Behavior.SearchContents;
        HighlightMatchesBox.IsChecked = _config.Behavior.HighlightMatches;
        ShowInternalPathsBox.IsChecked = _config.Behavior.ShowQsirchInternalPaths;
        QsirchThumbnailsBox.IsChecked = _config.Behavior.UseQsirchThumbnails;
        PreviewPaneBox.IsChecked = _config.Behavior.PreviewPane;
        AllowDownloadBox.IsChecked = _config.Behavior.AllowDownload;
        SelectTaggedItem(ThemeBox, string.IsNullOrWhiteSpace(_config.Behavior.Theme) ? "system" : _config.Behavior.Theme);
        SelectTaggedItem(ResultViewBox, string.IsNullOrWhiteSpace(_config.Behavior.ResultView) ? "details" : _config.Behavior.ResultView);
        SelectTaggedItem(ResultSortBox, FirstSortKey(_config.Behavior.ResultSort));
        HotkeyBox.Text = string.IsNullOrWhiteSpace(_config.Behavior.GlobalHotkey) ? "Ctrl+S" : _config.Behavior.GlobalHotkey;
        SearchTimeoutBox.Text = Math.Clamp(_config.Behavior.SearchTimeoutSeconds, 15, 300).ToString();
        FirstPageSizeBox.Text = Math.Clamp(_config.Behavior.FirstPageSize, 5, 500).ToString();
        NextPageSizeBox.Text = Math.Clamp(_config.Behavior.NextPageSize, 10, 500).ToString();
        ResultLimitBox.Text = Math.Clamp(_config.Behavior.MaxSearchResults, 50, 5000).ToString();
        HistoryEnabledBox.IsChecked = _config.History.Enabled;
    }

    private void SaveClicked(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            MessageBox.Show(this, "Port must be a number from 1 to 65535.", "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!int.TryParse(SearchTimeoutBox.Text.Trim(), out var searchTimeout) || searchTimeout is < 15 or > 300)
        {
            MessageBox.Show(this, "Qsirch timeout must be from 15 to 300 seconds.", "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!int.TryParse(FirstPageSizeBox.Text.Trim(), out var firstPageSize) || firstPageSize is < 5 or > 500)
        {
            MessageBox.Show(this, "First result page size must be from 5 to 500.", "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!int.TryParse(NextPageSizeBox.Text.Trim(), out var nextPageSize) || nextPageSize is < 10 or > 500)
        {
            MessageBox.Show(this, "Next result page size must be from 10 to 500.", "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!int.TryParse(ResultLimitBox.Text.Trim(), out var resultLimit) || resultLimit is < 50 or > 5000)
        {
            MessageBox.Show(this, "Initial result limit must be from 50 to 5000.", "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        CommitTableEdits();
        _config.Host = HostBox.Text.Trim();
        _config.Port = port;
        _config.User = UserBox.Text;
        _config.Password = PasswordBox.Password;
        _config.Ssl = SslBox.IsChecked == true;
        _config.SslVerify = SslVerifyBox.IsChecked == true;
        _config.Behavior.ShowInTaskbar = TaskbarBox.IsChecked == true;
        _config.Behavior.MinimizeToTray = MinimizeToTrayBox.IsChecked == true;
        _config.Behavior.ExitToTray = ExitToTrayBox.IsChecked == true;
        _config.Behavior.ClearResultsWithQuery = ClearResultsWithQueryBox.IsChecked == true;
        _config.AlwaysOnTop = AlwaysOnTopBox.IsChecked == true;
        _config.Behavior.FoldersFirst = FoldersFirstBox.IsChecked == true;
        _config.Behavior.ShowMatchingParentFolders = MatchingFoldersBox.IsChecked == true;
        _config.Behavior.CollapseMatchingFolderResults = CollapseMatchingFoldersBox.IsChecked == true;
        _config.Behavior.SearchContents = SearchContentsBox.IsChecked == true;
        _config.Behavior.HighlightMatches = HighlightMatchesBox.IsChecked == true;
        _config.Behavior.ShowQsirchInternalPaths = ShowInternalPathsBox.IsChecked == true;
        _config.Behavior.UseQsirchThumbnails = QsirchThumbnailsBox.IsChecked == true;
        _config.Behavior.PreviewPane = PreviewPaneBox.IsChecked == true;
        _config.Behavior.AllowDownload = AllowDownloadBox.IsChecked == true;
        _config.Behavior.Theme = SelectedTag(ThemeBox, "system");
        _config.Behavior.ResultView = SelectedTag(ResultViewBox, "details");
        _config.Behavior.ResultSort = SelectedTag(ResultSortBox, "recent");
        _config.Behavior.GlobalHotkey = string.IsNullOrWhiteSpace(HotkeyBox.Text) ? "Ctrl+S" : HotkeyBox.Text.Trim();
        _config.Behavior.SearchTimeoutSeconds = searchTimeout;
        _config.Behavior.FirstPageSize = firstPageSize;
        _config.Behavior.NextPageSize = nextPageSize;
        _config.Behavior.MaxSearchResults = resultLimit;
        _config.History.Enabled = HistoryEnabledBox.IsChecked == true;
        _config.PathMappings = Mappings.Where(x => !string.IsNullOrWhiteSpace(x.ShareRoot) && !string.IsNullOrWhiteSpace(x.MappedRoot)).ToList();
        _config.Exclude.FolderRules = FolderRules
            .Select(x => new ScopedTextRule { Pattern = x.Pattern.Trim(), IsGlobal = x.IsGlobal })
            .Where(x => !string.IsNullOrWhiteSpace(x.Pattern))
            .GroupBy(x => x.Pattern, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .ToList();
        _config.Exclude.FileRules = FileRules
            .Select(x => new ScopedTextRule { Pattern = x.Pattern.Trim(), IsGlobal = x.IsGlobal })
            .Where(x => !string.IsNullOrWhiteSpace(x.Pattern))
            .GroupBy(x => x.Pattern, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .ToList();
        _config.VisibilityRules = VisibilityRules
            .Where(x => !string.IsNullOrWhiteSpace(x.Pattern))
            .Select(x => new VisibilityRule
            {
                Access = NormalizeAccess(x.Access),
                Identity = string.IsNullOrWhiteSpace(x.Identity) ? "*" : x.Identity.Trim(),
                Pattern = x.Pattern.Trim(),
                IsGlobal = x.IsGlobal,
            })
            .ToList();
        ClearHistoryRequested = ClearHistoryBox.IsChecked == true;
        ClearStarredRequested = ClearStarredBox.IsChecked == true;
        DialogResult = true;
    }

    private void CancelClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void ResetDatabaseClicked(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Clear this user's saved Favorites, folders, recent searches, and saved searches?", "Reset local saved data", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
        {
            ResetDatabaseRequested = true;
        }
    }

    private void AddFolderRuleClicked(object sender, RoutedEventArgs e)
    {
        FolderRules.Add(new TextRule { Pattern = "*", IsGlobal = false });
        FolderRulesGrid.SelectedIndex = FolderRules.Count - 1;
        FolderRulesGrid.ScrollIntoView(FolderRulesGrid.SelectedItem);
    }

    private void RemoveFolderRuleClicked(object sender, RoutedEventArgs e)
    {
        RemoveSelected(FolderRulesGrid, FolderRules);
    }

    private void AddFileRuleClicked(object sender, RoutedEventArgs e)
    {
        FileRules.Add(new TextRule { Pattern = "*", IsGlobal = false });
        FileRulesGrid.SelectedIndex = FileRules.Count - 1;
        FileRulesGrid.ScrollIntoView(FileRulesGrid.SelectedItem);
    }

    private void RemoveFileRuleClicked(object sender, RoutedEventArgs e)
    {
        RemoveSelected(FileRulesGrid, FileRules);
    }

    private void AddVisibilityRuleClicked(object sender, RoutedEventArgs e)
    {
        AddVisibilityRule("deny");
    }

    private void AddVisibilityAllowClicked(object sender, RoutedEventArgs e)
    {
        AddVisibilityRule("allow");
    }

    private void AddVisibilityRule(string access)
    {
        var identity = VisibilityThisUserBox.IsChecked == true
            ? $@"{Environment.UserDomainName}\{Environment.UserName}"
            : "*";
        VisibilityRules.Add(new VisibilityRule
        {
            Access = access,
            Identity = identity,
            Pattern = "*",
            IsGlobal = VisibilityThisMachineBox.IsChecked != true,
        });
        VisibilityRulesGrid.SelectedIndex = VisibilityRules.Count - 1;
        VisibilityRulesGrid.ScrollIntoView(VisibilityRulesGrid.SelectedItem);
    }

    private void RemoveVisibilityRuleClicked(object sender, RoutedEventArgs e)
    {
        RemoveSelected(VisibilityRulesGrid, VisibilityRules);
    }

    private void CommitTableEdits()
    {
        foreach (var grid in new[] { MappingsGrid, FolderRulesGrid, FileRulesGrid, VisibilityRulesGrid })
        {
            grid.CommitEdit(DataGridEditingUnit.Cell, true);
            grid.CommitEdit(DataGridEditingUnit.Row, true);
        }
    }

    private static void RemoveSelected<T>(DataGrid grid, ObservableCollection<T> items)
    {
        if (grid.SelectedItem is T item)
        {
            items.Remove(item);
        }
    }

    private static VisibilityRule CloneVisibilityRule(VisibilityRule rule)
    {
        return new VisibilityRule
        {
            Access = NormalizeAccess(rule.Access),
            Identity = string.IsNullOrWhiteSpace(rule.Identity) ? "*" : rule.Identity,
            Pattern = rule.Pattern,
            IsGlobal = rule.IsGlobal,
        };
    }

    private static string NormalizeAccess(string access)
    {
        return access.Equals("allow", StringComparison.OrdinalIgnoreCase) ? "allow" : "deny";
    }

    private static void SelectTaggedItem(ComboBox box, string tag)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
        {
            if ((item.Tag?.ToString() ?? "").Equals(tag, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedItem = item;
                return;
            }
        }
        box.SelectedIndex = 0;
    }

    private static string SelectedTag(ComboBox box, string fallback)
    {
        return (box.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? fallback;
    }

    private static string FirstSortKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "recent";
        }
        var first = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "recent";
        return first.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "recent";
    }
}

public sealed class TextRule
{
    public string Pattern { get; set; } = "";
    public bool IsGlobal { get; set; }
}
