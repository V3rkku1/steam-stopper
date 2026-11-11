using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SteamStopper;

// Settings page (gear in the title bar): theme, menu order, start page, close button.
public partial class MainWindow
{
    private static readonly Dictionary<string, string> CloseActions = new()
    {
        ["ask"] = "Ask every time", ["tray"] = "Keep running in the tray", ["quit"] = "Quit Steam Stopper"
    };
    private List<string>? _defaultNavOrder;
    private RadioButton? _navDragged;
    private Point _navDragStart;
    private bool _settingsFilling;

    private IEnumerable<RadioButton> NavItems => NavList.Children.OfType<RadioButton>();

    private void InitSettings()
    {
        _defaultNavOrder = NavItems.Select(n => (string)n.Tag).ToList();
        ApplyNavOrder(_settings.NavOrder);
        NavList.PreviewMouseLeftButtonDown += NavList_MouseDown;
        NavList.PreviewMouseMove += NavList_MouseMove;
        NavList.DragOver += NavList_DragOver;
        NavList.Drop += (_, _) => SaveNavOrder();

        foreach (var (key, name) in Theme.Names)
        {
            var button = new RadioButton { Content = name, Tag = key, GroupName = "Theme", Style = (Style)FindResource("Segment"), IsChecked = key == _settings.Theme };
            button.Click += (_, _) =>
            {
                _settings.Theme = key;
                _settings.Save();
                Theme.Apply(key);
                // Colours the code picked itself (status chips, headlines) are set once; paint them again.
                RefreshFirewallAsync();
                RefreshAdmin();
                RefreshProcesses();
            };
            ThemeBar.Children.Add(button);
        }

        _settingsFilling = true;
        StartPageBox.ItemsSource = _defaultNavOrder.Select(k => PageTitles[k].Title).ToList();
        StartPageBox.SelectedItem = PageTitles.GetValueOrDefault(_settings.StartPage, PageTitles["home"]).Title;
        CloseActionBox.ItemsSource = CloseActions.Values.ToList();
        CloseActionBox.SelectedItem = CloseActions.GetValueOrDefault(_settings.CloseAction, CloseActions["ask"]);
        _settingsFilling = false;

        if (_settings.StartPage != "home" && NavItems.FirstOrDefault(n => (string)n.Tag == _settings.StartPage) is { } start)
        {
            start.IsChecked = true;
            ShowPage(_settings.StartPage);
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        foreach (var nav in NavItems) nav.IsChecked = false;
        // The close choice can change from the close dialog or the tray too.
        _settingsFilling = true;
        CloseActionBox.SelectedItem = CloseActions.GetValueOrDefault(_settings.CloseAction, CloseActions["ask"]);
        _settingsFilling = false;
        ShowPage("settings");
    }

    private void StartPage_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_settingsFilling || StartPageBox.SelectedItem is not string title) return;
        _settings.StartPage = PageTitles.First(p => p.Value.Title == title).Key;
        _settings.Save();
    }

    private void CloseAction_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_settingsFilling || CloseActionBox.SelectedItem is not string label) return;
        _settings.CloseAction = CloseActions.First(p => p.Value == label).Key;
        _settings.Save();
    }

    private void ApplyNavOrder(List<string> order)
    {
        // Listed pages first in that order, then any the list doesn't know (new in an update) in their usual place.
        var items = NavItems.ToList();
        var sorted = items.OrderBy(n => order.IndexOf((string)n.Tag) is var i and >= 0 ? i : order.Count + items.IndexOf(n)).ToList();
        foreach (var item in items) NavList.Children.Remove(item);
        foreach (var item in sorted) NavList.Children.Add(item);
    }

    private void SaveNavOrder()
    {
        _settings.NavOrder = NavItems.Select(n => (string)n.Tag).ToList();
        _settings.Save();
    }

    private void NavReset_Click(object sender, RoutedEventArgs e)
    {
        ApplyNavOrder(_defaultNavOrder!);
        _settings.NavOrder = [];
        _settings.Save();
    }

    private static RadioButton? NavItemAt(DependencyObject? d)
    {
        for (; d is not null; d = VisualTreeHelper.GetParent(d))
            if (d is RadioButton r) return r;
        return null;
    }

    private void NavList_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _navDragged = NavItemAt(e.OriginalSource as DependencyObject);
        _navDragStart = e.GetPosition(NavList);
    }

    private void NavList_MouseMove(object sender, MouseEventArgs e)
    {
        if (_navDragged is null || e.LeftButton != MouseButtonState.Pressed) return;
        // A real drag, not a click with a shaky hand.
        if (Math.Abs(e.GetPosition(NavList).Y - _navDragStart.Y) < SystemParameters.MinimumVerticalDragDistance * 2) return;
        var item = _navDragged;
        _navDragged = null;
        item.Opacity = 0.5;
        DragDrop.DoDragDrop(item, item, DragDropEffects.Move);
        item.Opacity = 1;
    }

    /// <summary>Move the dragged item live as it passes over the others, so the menu shows where it will land.</summary>
    private void NavList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
        if (e.Data.GetData(typeof(RadioButton)) is not RadioButton item || NavItemAt(e.OriginalSource as DependencyObject) is not { } over || over == item)
            return;
        var to = NavList.Children.IndexOf(over);
        NavList.Children.Remove(item);
        NavList.Children.Insert(to, item);
    }
}
