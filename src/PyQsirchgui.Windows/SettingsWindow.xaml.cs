using System.Collections.ObjectModel;
using System.Windows.Controls;
using System.Windows;
using PyQsirchgui.Windows.Models;

namespace PyQsirchgui.Windows;

public partial class SettingsWindow : Window
{
    private readonly AppConfig _config;

    public SettingsWindow(AppConfig config)
    {
        InitializeComponent();
        _config = config;
        Mappings = new ObservableCollection<PathMapping>(_config.PathMappings.Select(x => new PathMapping { ShareRoot = x.ShareRoot, MappedRoot = x.MappedRoot }));
        FolderRules = new ObservableCollection<TextRule>(_config.Exclude.Folders.Select(x => new TextRule { Pattern = x }));
        FileRules = new ObservableCollection<TextRule>(_config.Exclude.Files.Select(x => new TextRule { Pattern = x }));
        VisibilityRules = new ObservableCollection<VisibilityRule>(_config.VisibilityRules.Select(CloneVisibilityRule));
        MappingsGrid.ItemsSource = Mappings;
        FolderRulesGrid.ItemsSource = FolderRules;
        FileRulesGrid.ItemsSource = FileRules;
        VisibilityRulesGrid.ItemsSource = VisibilityRules;
        LoadValues();
    }

    public ObservableCollection<PathMapping> Mappings { get; }
    public ObservableCollection<TextRule> FolderRules { get; }
    public ObservableCollection<TextRule> FileRules { get; }
    public ObservableCollection<VisibilityRule> VisibilityRules { get; }
    public bool ClearHistoryRequested { get; private set; }
    public bool ClearStarredRequested { get; private set; }

    private void LoadValues()
    {
        HostBox.Text = _config.Host;
        PortBox.Text = _config.Port.ToString();
        UserBox.Text = _config.User;
        PasswordBox.Password = _config.Password;
        SslBox.IsChecked = _config.Ssl;
        SslVerifyBox.IsChecked = _config.SslVerify;
        TaskbarBox.IsChecked = _config.Behavior.ShowInTaskbar;
        StandardWindowBox.IsChecked = _config.Behavior.StandardWindow;
        AlwaysOnTopBox.IsChecked = _config.AlwaysOnTop;
        FoldersFirstBox.IsChecked = _config.Behavior.FoldersFirst;
        HighlightMatchesBox.IsChecked = _config.Behavior.HighlightMatches;
        PreviewPaneBox.IsChecked = _config.Behavior.PreviewPane;
        AllowDownloadBox.IsChecked = _config.Behavior.AllowDownload;
        SelectTaggedItem(ThemeBox, string.IsNullOrWhiteSpace(_config.Behavior.Theme) ? "system" : _config.Behavior.Theme);
        SelectTaggedItem(ResultViewBox, string.IsNullOrWhiteSpace(_config.Behavior.ResultView) ? "details" : _config.Behavior.ResultView);
        HotkeyBox.Text = string.IsNullOrWhiteSpace(_config.Behavior.GlobalHotkey) ? "Ctrl+Space" : _config.Behavior.GlobalHotkey;
        HistoryEnabledBox.IsChecked = _config.History.Enabled;
        HistoryFileBox.Text = _config.History.File;
        HistoryMaxBox.Text = _config.History.MaxEntries.ToString();
        SelectTaggedItem(HistorySourceBox, string.IsNullOrWhiteSpace(_config.History.SourceFilter) ? "__this__" : _config.History.SourceFilter);
    }

    private void SaveClicked(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            MessageBox.Show(this, "Port must be a number from 1 to 65535.", "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!int.TryParse(HistoryMaxBox.Text.Trim(), out var maxEntries) || maxEntries < 1)
        {
            MessageBox.Show(this, "Maximum history entries must be at least 1.", "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
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
        _config.Behavior.StandardWindow = StandardWindowBox.IsChecked == true;
        _config.AlwaysOnTop = AlwaysOnTopBox.IsChecked == true;
        _config.Behavior.FoldersFirst = FoldersFirstBox.IsChecked == true;
        _config.Behavior.HighlightMatches = HighlightMatchesBox.IsChecked == true;
        _config.Behavior.PreviewPane = PreviewPaneBox.IsChecked == true;
        _config.Behavior.AllowDownload = AllowDownloadBox.IsChecked == true;
        _config.Behavior.Theme = SelectedTag(ThemeBox, "system");
        _config.Behavior.ResultView = SelectedTag(ResultViewBox, "details");
        _config.Behavior.GlobalHotkey = string.IsNullOrWhiteSpace(HotkeyBox.Text) ? "Ctrl+Space" : HotkeyBox.Text.Trim();
        _config.History.Enabled = HistoryEnabledBox.IsChecked == true;
        _config.History.File = string.IsNullOrWhiteSpace(HistoryFileBox.Text) ? "history.json" : HistoryFileBox.Text.Trim();
        _config.History.MaxEntries = maxEntries;
        _config.History.SourceFilter = SelectedTag(HistorySourceBox, "__this__");
        _config.PathMappings = Mappings.Where(x => !string.IsNullOrWhiteSpace(x.ShareRoot) && !string.IsNullOrWhiteSpace(x.MappedRoot)).ToList();
        _config.Exclude.Folders = FolderRules.Select(x => x.Pattern.Trim()).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _config.Exclude.Files = FileRules.Select(x => x.Pattern.Trim()).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _config.VisibilityRules = VisibilityRules
            .Where(x => !string.IsNullOrWhiteSpace(x.Pattern))
            .Select(x => new VisibilityRule
            {
                Access = NormalizeAccess(x.Access),
                Identity = string.IsNullOrWhiteSpace(x.Identity) ? "*" : x.Identity.Trim(),
                Pattern = x.Pattern.Trim(),
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

    private void AddFolderRuleClicked(object sender, RoutedEventArgs e)
    {
        FolderRules.Add(new TextRule { Pattern = "*" });
        FolderRulesGrid.SelectedIndex = FolderRules.Count - 1;
        FolderRulesGrid.ScrollIntoView(FolderRulesGrid.SelectedItem);
    }

    private void RemoveFolderRuleClicked(object sender, RoutedEventArgs e)
    {
        RemoveSelected(FolderRulesGrid, FolderRules);
    }

    private void AddFileRuleClicked(object sender, RoutedEventArgs e)
    {
        FileRules.Add(new TextRule { Pattern = "*" });
        FileRulesGrid.SelectedIndex = FileRules.Count - 1;
        FileRulesGrid.ScrollIntoView(FileRulesGrid.SelectedItem);
    }

    private void RemoveFileRuleClicked(object sender, RoutedEventArgs e)
    {
        RemoveSelected(FileRulesGrid, FileRules);
    }

    private void AddVisibilityRuleClicked(object sender, RoutedEventArgs e)
    {
        VisibilityRules.Add(new VisibilityRule { Access = "deny", Identity = "*", Pattern = "*" });
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
}

public sealed class TextRule
{
    public string Pattern { get; set; } = "";
}
