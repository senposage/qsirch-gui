using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
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
        FolderRules = new ObservableCollection<string>(_config.Exclude.Folders);
        FileRules = new ObservableCollection<string>(_config.Exclude.Files);
        MappingsGrid.ItemsSource = Mappings;
        FolderRulesList.ItemsSource = FolderRules;
        FileRulesList.ItemsSource = FileRules;
        LoadValues();
    }

    public ObservableCollection<PathMapping> Mappings { get; }
    public ObservableCollection<string> FolderRules { get; }
    public ObservableCollection<string> FileRules { get; }
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
        AlwaysOnTopBox.IsChecked = _config.AlwaysOnTop;
        FoldersFirstBox.IsChecked = _config.Behavior.FoldersFirst;
        PreviewPaneBox.IsChecked = _config.Behavior.PreviewPane;
        HotkeyBox.Text = string.IsNullOrWhiteSpace(_config.Behavior.GlobalHotkey) ? "Ctrl+Space" : _config.Behavior.GlobalHotkey;
        HistoryEnabledBox.IsChecked = _config.History.Enabled;
        HistoryFileBox.Text = _config.History.File;
        HistoryMaxBox.Text = _config.History.MaxEntries.ToString();
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

        _config.Host = HostBox.Text.Trim();
        _config.Port = port;
        _config.User = UserBox.Text;
        _config.Password = PasswordBox.Password;
        _config.Ssl = SslBox.IsChecked == true;
        _config.SslVerify = SslVerifyBox.IsChecked == true;
        _config.Behavior.ShowInTaskbar = TaskbarBox.IsChecked == true;
        _config.AlwaysOnTop = AlwaysOnTopBox.IsChecked == true;
        _config.Behavior.FoldersFirst = FoldersFirstBox.IsChecked == true;
        _config.Behavior.PreviewPane = PreviewPaneBox.IsChecked == true;
        _config.Behavior.GlobalHotkey = string.IsNullOrWhiteSpace(HotkeyBox.Text) ? "Ctrl+Space" : HotkeyBox.Text.Trim();
        _config.History.Enabled = HistoryEnabledBox.IsChecked == true;
        _config.History.File = string.IsNullOrWhiteSpace(HistoryFileBox.Text) ? "history.json" : HistoryFileBox.Text.Trim();
        _config.History.MaxEntries = maxEntries;
        _config.PathMappings = Mappings.Where(x => !string.IsNullOrWhiteSpace(x.ShareRoot) && !string.IsNullOrWhiteSpace(x.MappedRoot)).ToList();
        _config.Exclude.Folders = FolderRules.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _config.Exclude.Files = FileRules.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        ClearHistoryRequested = ClearHistoryBox.IsChecked == true;
        ClearStarredRequested = ClearStarredBox.IsChecked == true;
        DialogResult = true;
    }

    private void CancelClicked(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void FolderRuleKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || string.IsNullOrWhiteSpace(FolderRuleBox.Text))
        {
            return;
        }
        FolderRules.Add(FolderRuleBox.Text.Trim());
        FolderRuleBox.Clear();
        e.Handled = true;
    }

    private void FileRuleKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || string.IsNullOrWhiteSpace(FileRuleBox.Text))
        {
            return;
        }
        FileRules.Add(FileRuleBox.Text.Trim());
        FileRuleBox.Clear();
        e.Handled = true;
    }
}
