using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using SteamStopper.Core;

namespace SteamStopper;

/// <summary>
/// Fullscreen couch view of the Games library: one row of square tiles, played with arrow keys or an Xbox-style
/// controller (XInput, no extra library). Drawn in code like <see cref="Dialog"/>; playing goes through the Games tab.
/// </summary>
public sealed class ConsoleMode : Window
{
    private const double TileSize = 300, TileGap = 22, Edge = 72;

    private readonly List<Rom> _games;
    private readonly Func<Rom, Task> _play;
    private readonly Func<Rom, bool> _busy;
    private readonly Func<string> _status;
    private readonly List<Border> _tiles = [];
    private readonly StackPanel _row = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(Edge, 24, Edge, 24) };
    private readonly ScrollViewer _scroll;
    private readonly TextBlock _title = new() { FontSize = 30, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _detail = new() { FontSize = 15, Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _clock = new() { FontSize = 22, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _statusLine = new() { FontSize = 14, Margin = new Thickness(Edge, 0, Edge, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _pad = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private int _index;

    public static void Open(Window owner, IEnumerable<Rom> games, Func<Rom, Task> play, Func<Rom, bool> busy, Func<string> status) =>
        new ConsoleMode(owner, games, play, busy, status).Show();

    private ConsoleMode(Window owner, IEnumerable<Rom> games, Func<Rom, Task> play, Func<Rom, bool> busy, Func<string> status)
    {
        _games = games.OrderByDescending(g => g.LastPlayed ?? DateTime.MinValue).ThenByDescending(g => g.Added).ToList();
        (_play, _busy, _status) = (play, busy, status);
        Owner = owner;
        Title = "Console mode";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        SourceInitialized += (_, _) => FillScreen();
        ShowInTaskbar = false;
        Background = Res("Bg");
        Foreground = Res("Text");
        FontFamily = (FontFamily)FindResource("TextFont");
        _clock.Foreground = _title.Foreground = Res("Text");
        _detail.Foreground = _statusLine.Foreground = Res("Muted");

        var top = new DockPanel { Margin = new Thickness(Edge, 40, Edge, 0) };
        DockPanel.SetDock(_clock, Dock.Right);
        top.Children.Add(_clock);
        top.Children.Add(new TextBlock { Text = "Steam Multitool", FontSize = 15, Foreground = Res("Faint"), VerticalAlignment = VerticalAlignment.Center });

        var heading = new StackPanel { Margin = new Thickness(Edge, 0, Edge, 0) };
        heading.Children.Add(_title);
        heading.Children.Add(_detail);

        _scroll = new ScrollViewer
        {
            Content = _row, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false, CanContentScroll = false
        };
        foreach (var game in _games) _row.Children.Add(Tile(game));

        var middle = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        middle.Children.Add(heading);
        middle.Children.Add(_scroll);
        middle.Children.Add(_statusLine);
        if (_games.Count == 0)
        {
            _title.Text = "No games yet";
            _detail.Text = "Add a games folder on the Games tab, then come back.";
        }

        var hints = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(Edge, 18, Edge, 26) };
        if (_games.Count > 0) hints.Children.Add(Hint("A", "Play"));
        hints.Children.Add(Hint("B", "Back"));
        var bottom = new Border { BorderBrush = Res("Line"), BorderThickness = new Thickness(0, 1, 0, 0), Child = hints, Margin = new Thickness(Edge / 2, 0, Edge / 2, 0) };

        var layout = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(bottom, Dock.Bottom);
        layout.Children.Add(top);
        layout.Children.Add(bottom);
        layout.Children.Add(middle);
        Content = layout;

        KeyDown += OnKey;
        _tick.Tick += (_, _) => Refresh();
        _pad.Tick += (_, _) => PollPad();
        Loaded += (_, _) => { Select(0); Refresh(); _tick.Start(); _pad.Start(); };
        Closed += (_, _) => { _tick.Stop(); _pad.Stop(); Owner?.Activate(); };
    }

    private Brush Res(string key) => (Brush)FindResource(key);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32 { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public Rect32 Monitor, Work; public uint Flags; }

    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    /// <summary>Cover the whole monitor the app is on, taskbar included (Maximized stops at the taskbar for some setups).</summary>
    private void FillScreen()
    {
        var owner = new System.Windows.Interop.WindowInteropHelper(Owner ?? this).Handle;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(owner, 2 /* nearest */), ref info) || PresentationSource.FromVisual(this)?.CompositionTarget is not { } target)
        {
            WindowState = WindowState.Maximized;
            return;
        }
        var toDip = target.TransformFromDevice;
        var topLeft = toDip.Transform(new Point(info.Monitor.Left, info.Monitor.Top));
        var bottomRight = toDip.Transform(new Point(info.Monitor.Right, info.Monitor.Bottom));
        (Left, Top, Width, Height) = (topLeft.X, topLeft.Y, bottomRight.X - topLeft.X, bottomRight.Y - topLeft.Y);
    }

    private Border Tile(Rom game)
    {
        var art = new Border { CornerRadius = new CornerRadius(12), Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(game.Color)) };
        var initials = new TextBlock
        {
            Text = game.Initials, FontSize = 64, FontWeight = FontWeights.Black, Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        var badge = new Border
        {
            Background = Res("Accent"), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(12),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed,
            Child = new TextBlock { Text = "Running", Foreground = Brushes.White, FontSize = 13, FontWeight = FontWeights.SemiBold }
        };
        var face = new Grid { Children = { art, initials, badge } };
        void Paint()
        {
            if (game.Cover is not { } path || !File.Exists(path)) return;
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 600;
                image.UriSource = new Uri(path);
                image.EndInit();
                art.Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
                initials.Visibility = Visibility.Collapsed;
            }
            catch { } // a broken image keeps the console colour
        }
        Paint();
        // Covers can still be downloading when console mode opens.
        PropertyChangedEventHandler changed = (_, e) => { if (e.PropertyName == nameof(Rom.Cover)) Dispatcher.Invoke(Paint); };
        game.PropertyChanged += changed;
        Closed += (_, _) => game.PropertyChanged -= changed;

        var tile = new Border
        {
            Width = TileSize, Height = TileSize, Margin = new Thickness(0, 0, TileGap, 0), Padding = new Thickness(4),
            CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(4), BorderBrush = Brushes.Transparent,
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(1, 1), Child = face, Tag = badge, Cursor = Cursors.Hand
        };
        tile.MouseLeftButtonUp += async (_, _) =>
        {
            var at = _tiles.IndexOf(tile);
            if (at == _index) await PlaySelected();
            else Select(at);
        };
        _tiles.Add(tile);
        return tile;
    }

    private UIElement Hint(string button, string label)
    {
        var circle = new Grid { Width = 28, Height = 28, Margin = new Thickness(0, 0, 8, 0) };
        circle.Children.Add(new Ellipse { Fill = Res("Text") });
        circle.Children.Add(new TextBlock { Text = button, Foreground = Res("Bg"), FontWeight = FontWeights.Bold, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(28, 0, 0, 0) };
        row.Children.Add(circle);
        row.Children.Add(new TextBlock { Text = label, FontSize = 16, VerticalAlignment = VerticalAlignment.Center, Foreground = Res("Text") });
        return row;
    }

    private void Select(int index)
    {
        if (_games.Count == 0) return;
        _index = Math.Clamp(index, 0, _games.Count - 1);
        for (var i = 0; i < _tiles.Count; i++)
        {
            var on = i == _index;
            _tiles[i].BorderBrush = on ? Res("Accent") : Brushes.Transparent;
            var grow = new DoubleAnimation(on ? 1.06 : 1, TimeSpan.FromMilliseconds(140)) { EasingFunction = new CubicEase() };
            var scale = (ScaleTransform)_tiles[i].RenderTransform;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        }
        var game = _games[_index];
        _title.Text = game.Title;
        _detail.Text = $"{game.ConsoleName}  ·  {(game.LastPlayed is null ? "Never played" : $"Played {game.LastPlayedText.ToLowerInvariant()}  ·  {game.PlayTimeText}")}";
        // Keep the selected tile in view with one tile of the next showing.
        var left = Edge + _index * (TileSize + TileGap);
        var view = _scroll.ViewportWidth > 0 ? _scroll.ViewportWidth : ActualWidth;
        if (left - TileSize < _scroll.HorizontalOffset) _scroll.ScrollToHorizontalOffset(Math.Max(0, left - Edge - TileSize));
        else if (left + 2 * TileSize + TileGap > _scroll.HorizontalOffset + view) _scroll.ScrollToHorizontalOffset(left + 2 * TileSize + TileGap - view);
    }

    private void Refresh()
    {
        _clock.Text = DateTime.Now.ToString("t");
        _statusLine.Text = _status();
        for (var i = 0; i < _tiles.Count; i++) ((Border)_tiles[i].Tag).Visibility = _busy(_games[i]) ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task PlaySelected()
    {
        if (_games.Count == 0) return;
        var game = _games[_index];
        var play = _play(game);
        Refresh();
        await play;
        Refresh();
    }

    private async void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left: Select(_index - 1); break;
            case Key.Right: Select(_index + 1); break;
            case Key.Home: Select(0); break;
            case Key.End: Select(_games.Count - 1); break;
            case Key.Enter or Key.Space: e.Handled = true; await PlaySelected(); return;
            case Key.Escape or Key.Back or Key.F11: Close(); break;
            default: return;
        }
        e.Handled = true;
    }

    // XInput, polled only while this window is in front so a game's own controls don't move the row.
    [StructLayout(LayoutKind.Sequential)]
    private struct PadState { public uint Packet; public ushort Buttons; public byte LeftTrigger, RightTrigger; public short LX, LY, RX, RY; }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern int XInputGetState(int user, out PadState state);

    private const ushort PadLeft = 0x0004, PadRight = 0x0008, ButtonA = 0x1000, ButtonB = 0x2000;
    private ushort _held;
    private DateTime _repeatAt;
    private bool _noPad;

    private async void PollPad()
    {
        if (_noPad || !IsActive) return;
        ushort buttons = 0;
        try
        {
            for (var user = 0; user < 4; user++)
                if (XInputGetState(user, out var state) == 0)
                {
                    buttons = state.Buttons;
                    // The left stick counts as the d-pad.
                    if (state.LX < -16000) buttons |= PadLeft;
                    if (state.LX > 16000) buttons |= PadRight;
                    break;
                }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { _noPad = true; return; }

        var pressed = (ushort)(buttons & ~_held);
        var move = buttons & (PadLeft | PadRight);
        // Holding a direction repeats after a short wait, like a keyboard.
        if ((pressed & (PadLeft | PadRight)) != 0) _repeatAt = DateTime.UtcNow.AddMilliseconds(380);
        else if (move != 0 && DateTime.UtcNow >= _repeatAt) { pressed |= (ushort)move; _repeatAt = DateTime.UtcNow.AddMilliseconds(110); }
        _held = buttons;

        if ((pressed & PadLeft) != 0) Select(_index - 1);
        if ((pressed & PadRight) != 0) Select(_index + 1);
        if ((pressed & ButtonB) != 0) Close();
        else if ((pressed & ButtonA) != 0) await PlaySelected();
    }
}
