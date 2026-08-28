using System.Collections.ObjectModel;
using System.Windows;
using PyQsirchgui.Windows.Models;
using PyQsirchgui.Windows.Services;

namespace PyQsirchgui.Windows;

public partial class GroupPickerWindow : Window
{
    public ObservableCollection<FavoriteGroupNode> Groups { get; }
    public IReadOnlyList<string> SelectedGroups { get; private set; } = [];
    private FavoriteGroupNode? _selectedNode;

    public GroupPickerWindow(IEnumerable<string> groups, IEnumerable<string> selectedGroups, string theme)
    {
        var selectedPath = selectedGroups.FirstOrDefault() ?? "";
        Groups = new ObservableCollection<FavoriteGroupNode>(BuildFolderTree(groups, selectedPath));
        InitializeComponent();
        ThemePalette.Apply(Resources, string.IsNullOrWhiteSpace(theme) ? "system" : theme);
        DataContext = this;

    }

    private void SaveClicked(object sender, RoutedEventArgs e)
    {
        var newFolder = NewGroupText.Text.Trim().Replace('/', '\\').Trim('\\');
        var folder = string.IsNullOrWhiteSpace(newFolder)
            ? _selectedNode?.Path ?? ""
            : string.IsNullOrWhiteSpace(_selectedNode?.Path) ? newFolder : _selectedNode.Path + "\\" + newFolder;
        SelectedGroups = string.IsNullOrWhiteSpace(folder) ? [] : [folder];
        DialogResult = true;
    }

    private void GroupTreeSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        _selectedNode = e.NewValue as FavoriteGroupNode;
    }

    private static IReadOnlyList<FavoriteGroupNode> BuildFolderTree(IEnumerable<string> groups, string selectedPath)
    {
        var roots = new List<FavoriteGroupNode>();
        var nodes = new Dictionary<string, FavoriteGroupNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups
                     .Where(group => !string.IsNullOrWhiteSpace(group))
                     .Select(group => group.Trim().Replace('/', '\\').Trim('\\'))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group, StringComparer.CurrentCultureIgnoreCase))
        {
            var path = "";
            ICollection<FavoriteGroupNode> siblings = roots;
            foreach (var part in group.Split('\\', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                path = string.IsNullOrWhiteSpace(path) ? part : path + "\\" + part;
                if (!nodes.TryGetValue(path, out var node))
                {
                    node = new FavoriteGroupNode
                    {
                        Name = part,
                        Path = path,
                        IsSelected = path.Equals(selectedPath, StringComparison.OrdinalIgnoreCase),
                    };
                    nodes[path] = node;
                    siblings.Add(node);
                }
                siblings = node.Children;
            }
        }
        return roots;
    }
}
