using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SteamStopper.Core;

namespace SteamStopper;

/// <summary>
/// Search box art for a game and pick one, or take an image file. Drawn in code like <see cref="Dialog"/>.
/// Returns the chosen image's path, or null when cancelled.
/// </summary>
public static class CoverPicker
{
    /// <param name="step">For one-after-another picking, like "3 of 12"; adds a Stop button that calls <paramref name="stop"/>.</param>
    public static string? Show(Window owner, Rom rom, HttpClient http, string? step = null, Action? stop = null)
    {
        string? result = null;
        var window = new Window
        {
            Title = "Choose a cover",
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            Width = 820,
            Height = 640,
            ShowInTaskbar = false,
            Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            FontFamily = (FontFamily)Application.Current!.FindResource("TextFont"),
            FontSize = 13
        };
        Brush Res(string key) => (Brush)window.FindResource(key);

        var query = new TextBox { Text = rom.Title, Padding = new Thickness(12, 8, 12, 8), VerticalContentAlignment = VerticalAlignment.Center };
        var search = new Button { Content = "Search", MinWidth = 96, Margin = new Thickness(8, 0, 0, 0), Style = (Style)window.FindResource("SafeButton"), IsDefault = true };
        var status = new TextBlock { Foreground = Res("Muted"), Margin = new Thickness(0, 10, 0, 10), TextWrapping = TextWrapping.Wrap };
        var grid = new WrapPanel();
        var file = new Button { Content = "From a file…", MinWidth = 96, Style = (Style)window.FindResource("GhostButton") };
        var cancel = new Button { Content = "Cancel", MinWidth = 96, Margin = new Thickness(8, 0, 0, 0), Style = (Style)window.FindResource("GhostButton"), IsCancel = true };
        cancel.Click += (_, _) => window.Close();
        file.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Choose a cover image", Filter = "Images|*.png;*.jpg;*.jpeg;*.webp;*.bmp" };
            if (dialog.ShowDialog(window) != true) return;
            result = dialog.FileName;
            window.Close();
        };

        var searchRow = new DockPanel();
        DockPanel.SetDock(search, Dock.Right);
        searchRow.Children.Add(search);
        searchRow.Children.Add(query);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(file);
        if (stop is not null)
        {
            var halt = new Button { Content = "Stop", MinWidth = 96, Margin = new Thickness(8, 0, 0, 0), Style = (Style)window.FindResource("GhostButton") };
            halt.Click += (_, _) => { stop(); window.Close(); };
            buttons.Children.Add(halt);
            cancel.Content = "Skip";
        }
        buttons.Children.Add(cancel);
        var top = new StackPanel();
        top.Children.Add(new TextBlock
        {
            Text = $"Cover for {rom.Title}{(step is null ? "" : "  ·  " + step)}", FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12),
            FontFamily = (FontFamily)window.FindResource("DisplayFont"), Foreground = Res("Text"), TextTrimming = TextTrimming.CharacterEllipsis
        });
        top.Children.Add(searchRow);
        top.Children.Add(status);
        var body = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        body.Children.Add(top);
        body.Children.Add(buttons);
        body.Children.Add(new ScrollViewer { Content = grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });

        var card = new Border
        {
            Margin = new Thickness(16),
            Padding = new Thickness(24, 20, 24, 20),
            Background = Res("Popup"),
            BorderBrush = Res("Line"),
            BorderThickness = new Thickness(1),
            CornerRadius = (CornerRadius)window.FindResource("CardRadius"),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 28, ShadowDepth = 6, Opacity = 0.5 },
            Child = body
        };
        card.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is Border or TextBlock) window.DragMove(); };
        window.Content = card;

        var run = 0; // a newer search makes older ones stop filling the grid
        async Task Search()
        {
            var mine = ++run;
            grid.Children.Clear();
            if (!RomLibrary.HasCoverSource(rom.Console))
            {
                status.Text = rom.Console.Length == 0 ? "Pick this game's console first, then search again." : "There is no cover source for this console. Use an image file.";
                return;
            }
            status.Text = "Searching…";
            List<RomLibrary.CoverChoice> found;
            try { found = await RomLibrary.SearchCovers(rom.Console, query.Text, http); }
            catch { found = []; }
            if (mine != run) return;
            status.Text = found.Count == 0 ? "No covers found. Try fewer or shorter words, or the game's English name." : $"{found.Count} found. Click one to use it.";
            foreach (var choice in found)
            {
                var image = new Image { Height = 200, Stretch = Stretch.Uniform };
                var tile = new Border
                {
                    Width = 150, Margin = new Thickness(0, 0, 10, 10), Padding = new Thickness(6), Cursor = Cursors.Hand,
                    CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(2), BorderBrush = Brushes.Transparent, Background = Res("Raised"),
                    ToolTip = $"{choice.Name}\n{choice.Source}",
                    Child = new StackPanel
                    {
                        Children =
                        {
                            image,
                            new TextBlock { Text = choice.Name, Foreground = Res("Muted"), FontSize = 11, TextWrapping = TextWrapping.Wrap, MaxHeight = 30, Margin = new Thickness(0, 6, 0, 0) }
                        }
                    }
                };
                tile.MouseEnter += (_, _) => tile.BorderBrush = Res("Accent");
                tile.MouseLeave += (_, _) => tile.BorderBrush = Brushes.Transparent;
                tile.MouseLeftButtonUp += async (_, _) =>
                {
                    status.Text = "Saving cover…";
                    if (await RomLibrary.SaveChosenCover(rom, choice, http) is { } path)
                    {
                        result = path;
                        window.Close();
                    }
                    else status.Text = "That cover didn't download. Pick another.";
                };
                grid.Children.Add(tile);
                _ = Load(image, tile, choice, mine);
            }
        }

        // Thumbnails load while the list is already shown; ones that fail drop out.
        async Task Load(Image image, Border tile, RomLibrary.CoverChoice choice, int mine)
        {
            var bytes = await RomLibrary.CoverBytes(choice, http);
            if (mine != run) return;
            try
            {
                if (bytes is null) throw new InvalidDataException();
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 276;
                bitmap.StreamSource = new MemoryStream(bytes);
                bitmap.EndInit();
                image.Source = bitmap;
            }
            catch { grid.Children.Remove(tile); }
        }

        search.Click += async (_, _) => await Search();
        window.Loaded += async (_, _) => { query.Focus(); query.SelectAll(); await Search(); };
        window.ShowDialog();
        return result;
    }
}
