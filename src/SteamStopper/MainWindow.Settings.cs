using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SteamStopper.Core;

namespace SteamStopper;

// Settings page (gear in the title bar): theme, menu order, start page, close button.
public partial class MainWindow
{
    private static readonly Dictionary<string, string> CloseActions = new()
    {
        ["ask"] = "Ask every time", ["tray"] = "Keep running in the tray", ["quit"] = "Quit Steam Multitool"
    };
    private List<string>? _defaultNavOrder;
    private RadioButton? _navDragged;
    private Point _navDragStart;
    private bool _settingsFilling;
    private Panel? _navSideHost;

    private IEnumerable<RadioButton> NavItems => NavList.Children.OfType<RadioButton>();

    private void InitSettings()
    {
        _defaultNavOrder = NavItems.Select(n => (string)n.Tag).ToList();
        _navSideHost = (Panel)NavList.Parent;
        // An order saved before the menu became six groups names pages, not groups: start fresh.
        if (_settings.NavOrder.Any(k => NavGroups.All(g => g.Key != k))) _settings.NavOrder = [];
        ApplyNavOrder(_settings.NavOrder);
        ApplyNavPosition(_settings.NavPosition == "top");
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
                CustomThemeBox.SelectedItem = null;
                WatchTheme();
                ApplyTheme();
                // Colours the code picked itself (status chips, headlines) are set once; paint them again.
                RefreshFirewallAsync();
                RefreshAdmin();
                RefreshProcesses();
            };
            ThemeBar.Children.Add(button);
        }
        FillCustomThemes();
        WatchTheme();

        InitCheevos();
        BoostBox.IsChecked = _settings.GameBoost;
        BoostAppsBox.Text = string.Join(Environment.NewLine, _settings.GameBoostApps);
        PaintUnpackFolder();
        SettingsAbout.Text = $"Steam Multitool {Core.Updater.Version}  ·  Steam folder: {_steamRoot ?? "not found"}";
        _settingsFilling = true;
        StartPageBox.ItemsSource = NavGroups.SelectMany(g => g.Pages).Select(k => PageTitles[k].Title).ToList();
        StartPageBox.SelectedItem = PageTitles.GetValueOrDefault(_settings.StartPage, PageTitles["home"]).Title;
        CloseActionBox.ItemsSource = CloseActions.Values.ToList();
        CloseActionBox.SelectedItem = CloseActions.GetValueOrDefault(_settings.CloseAction, CloseActions["ask"]);
        _settingsFilling = false;

        if (_settings.StartPage != "home" && PageTitles.ContainsKey(_settings.StartPage)) GoTo(_settings.StartPage);
    }

    private void PaintUnpackFolder() =>
        UnpackFolderText.Text = RomLibrary.UnpackRoot.Length > 0 && Directory.Exists(RomLibrary.UnpackRoot) ? RomLibrary.UnpackRoot : "Next to each image";

    private void UnpackFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose where PS5 games are unpacked" };
        if (dialog.ShowDialog(this) != true) return;
        _settings.Ps5UnpackFolder = RomLibrary.UnpackRoot = dialog.FolderName;
        _settings.Save();
        PaintUnpackFolder();
    }

    private void UnpackFolderReset_Click(object sender, RoutedEventArgs e)
    {
        _settings.Ps5UnpackFolder = RomLibrary.UnpackRoot = "";
        _settings.Save();
        PaintUnpackFolder();
    }

    /// <summary>Apply the chosen theme; "From my game" picks its colour from the most played game's cover.</summary>
    private void ApplyTheme()
    {
        if (_settings.Theme != "game")
        {
            Theme.Apply(_settings.Theme);
            ApplyBackdrop();
            ThemeHint.Text = DefaultThemeHint;
            return;
        }
        if (_library is null) RefreshGamesUi();
        var game = _library!.Games.Where(g => g.Cover is not null)
            .OrderByDescending(g => g.PlaySeconds).ThenByDescending(g => g.LastPlayed ?? g.Added).FirstOrDefault();
        var accent = game is null ? null : Theme.AccentOf(game.Cover!);
        Theme.Apply("game", accent);
        ApplyBackdrop();
        ThemeHint.Text = accent is null
            ? "From my game: no game cover with colour yet. Add games on the Games tab and it follows the one you play most."
            : $"From my game: coloured by {game!.Title}, {(game.PlaySeconds > 0 ? "your most played game" : "your newest game until you play one")}.";
    }

    private void ApplyBackdrop()
    {
        // Glass needs Windows 11; elsewhere the see-through background becomes solid instead of black.
        if (!DarkTitleBar.SetBackdrop(this, Theme.Backdrop)) Theme.Opaque();
    }

    private System.IO.FileSystemWatcher? _themeWatcher;
    private const string DefaultThemeHint = "Steam uses the Steam client's navy and blue. Liquid Glass is iOS-style see-through glass (Windows 11). From my game takes its colour from the cover of the game you play most.";

    private void FillCustomThemes()
    {
        _settingsFilling = true;
        var files = Theme.Files();
        CustomThemeBox.ItemsSource = files;
        CustomThemeBox.SelectedItem = _settings.Theme.StartsWith("css:") ? _settings.Theme[4..] : null;
        CustomThemeBox.IsEnabled = files.Count > 0;
        _settingsFilling = false;
    }

    private void CustomTheme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_settingsFilling || CustomThemeBox.SelectedItem is not string file) return;
        foreach (var b in ThemeBar.Children.OfType<RadioButton>()) b.IsChecked = false;
        _settings.Theme = "css:" + file;
        _settings.Save();
        ApplyTheme();
        WatchTheme();
    }

    /// <summary>Re-apply a CSS theme whenever its file is saved, so editing it shows the result at once.</summary>
    private void WatchTheme()
    {
        _themeWatcher?.Dispose();
        _themeWatcher = null;
        if (!_settings.Theme.StartsWith("css:") || !System.IO.Directory.Exists(Theme.Folder)) return;
        _themeWatcher = new System.IO.FileSystemWatcher(Theme.Folder, _settings.Theme[4..])
        {
            NotifyFilter = System.IO.NotifyFilters.LastWrite | System.IO.NotifyFilters.Size | System.IO.NotifyFilters.FileName,
            EnableRaisingEvents = true
        };
        // Editors save in bursts, some by writing a new file and renaming it over; apply a moment after.
        System.IO.FileSystemEventHandler reload = (_, _) => Dispatcher.BeginInvoke(async () =>
        {
            await Task.Delay(150);
            ApplyTheme();
        });
        _themeWatcher.Changed += reload;
        _themeWatcher.Created += reload;
        _themeWatcher.Renamed += (s, e) => reload(s, e);
    }

    private void NewTheme_Click(object sender, RoutedEventArgs e)
    {
        System.IO.Directory.CreateDirectory(Theme.Folder);
        var n = 1;
        string path;
        do path = System.IO.Path.Combine(Theme.Folder, n == 1 ? "My theme.css" : $"My theme {n}.css"); while (System.IO.File.Exists(path) && ++n > 0);
        System.IO.File.WriteAllText(path, Theme.Template());
        FillCustomThemes();
        CustomThemeBox.SelectedItem = System.IO.Path.GetFileName(path); // applies it and starts watching
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true })?.Dispose(); }
        catch { }
        StatusText.Text = $"Made {System.IO.Path.GetFileName(path)} from the current colours. Edit and save it; the app updates as you save.";
    }

    private void ThemesFolder_Click(object sender, RoutedEventArgs e)
    {
        System.IO.Directory.CreateDirectory(Theme.Folder);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{Theme.Folder}\"") { UseShellExecute = true })?.Dispose();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        foreach (var nav in NavItems) nav.IsChecked = false;
        SubTabBar.Visibility = Visibility.Collapsed;
        // The close choice can change from the close dialog or the tray too.
        _settingsFilling = true;
        CloseActionBox.SelectedItem = CloseActions.GetValueOrDefault(_settings.CloseAction, CloseActions["ask"]);
        _settingsFilling = false;
        RefreshSaves();
        PaintBoost();
        FillCustomThemes(); // files may have been added in the folder meanwhile
        ShowPage("settings");
    }

    private void Boost_Click(object sender, RoutedEventArgs e)
    {
        _settings.GameBoost = BoostBox.IsChecked == true;
        _settings.Save();
    }

    private void BoostApps_Changed(object sender, RoutedEventArgs e)
    {
        _settings.GameBoostApps = BoostAppsBox.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        _settings.Save();
        PaintBoost();
    }

    private void PaintBoost()
    {
        var running = _settings.GameBoostApps.Where(n => System.Diagnostics.Process.GetProcessesByName(System.IO.Path.GetFileNameWithoutExtension(n)).Length > 0).ToList();
        BoostRunning.Text = running.Count == 0 ? "None of these is running right now." : "Running now: " + string.Join(", ", running);
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

    private void NavPosition_Click(object sender, RoutedEventArgs e)
    {
        _settings.NavPosition = NavTopButton.IsChecked == true ? "top" : "side";
        _settings.Save();
        ApplyNavPosition(_settings.NavPosition == "top");
    }

    /// <summary>Move the one menu between the sidebar and the tab bar under the title, restyling its items.</summary>
    private void ApplyNavPosition(bool top)
    {
        NavTopButton.IsChecked = top;
        NavSideButton.IsChecked = !top;
        if (NavList.Parent is Panel panel) panel.Children.Remove(NavList);
        else if (NavList.Parent is ScrollViewer viewer) viewer.Content = null;
        if (top) TopNavHost.Content = NavList;
        else _navSideHost!.Children.Add(NavList);
        NavList.Orientation = top ? Orientation.Horizontal : Orientation.Vertical;
        var style = (Style)FindResource(top ? "NavTab" : "NavItem");
        foreach (var item in NavItems)
        {
            item.Style = style;
            // Tabs are text only, like Steam's; the sidebar keeps its icons.
            if (item.Content is StackPanel { Children.Count: > 1 } content) content.Children[0].Visibility = top ? Visibility.Collapsed : Visibility.Visible;
        }
        // The sidebar holds nothing else that matters on top: updates are on Home, the rest in Settings.
        SidebarPanel.Visibility = top ? Visibility.Collapsed : Visibility.Visible;
        TopNav.Visibility = top ? Visibility.Visible : Visibility.Collapsed;
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
        var moved = e.GetPosition(NavList) - _navDragStart;
        if (Math.Max(Math.Abs(moved.X), Math.Abs(moved.Y)) < SystemParameters.MinimumVerticalDragDistance * 2) return;
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
