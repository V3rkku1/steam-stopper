using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using SteamStopper.Core;

namespace SteamStopper;

/// <summary>
/// Fullscreen couch view of the Games library: Assets/console.html in WebView2, played with keys, mouse or a controller
/// (the page reads the browser Gamepad API). The page asks to play or exit; playing goes through the Games tab.
/// </summary>
public sealed class ConsoleMode : Window
{
    private readonly List<Rom> _games;
    private readonly Func<Rom, Task> _play;
    private readonly Func<Rom, bool> _busy;
    private readonly Func<string> _status;
    private readonly WebView2 _web = new() { DefaultBackgroundColor = System.Drawing.Color.Black };
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private string _sent = "";

    public static void Open(Window owner, IEnumerable<Rom> games, Func<Rom, Task> play, Func<Rom, bool> busy, Func<string> status) =>
        new ConsoleMode(owner, games, play, busy, status).Show();

    private ConsoleMode(Window owner, IEnumerable<Rom> games, Func<Rom, Task> play, Func<Rom, bool> busy, Func<string> status)
    {
        _games = games.Where(g => g.Kind.Length == 0).OrderByDescending(g => g.LastPlayed ?? DateTime.MinValue).ThenByDescending(g => g.Added).ToList();
        (_play, _busy, _status) = (play, busy, status);
        Owner = owner;
        Title = "Console mode";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Background = System.Windows.Media.Brushes.Black;
        SourceInitialized += (_, _) => FillScreen();
        Content = _web;
        Loaded += async (_, _) => await Start();
        _tick.Tick += async (_, _) => await SendState();
        Closed += (_, _) => { _tick.Stop(); _web.Dispose(); Owner?.Activate(); };
        foreach (var (game, id) in _games.Select((g, i) => (g, i)))
        {
            System.ComponentModel.PropertyChangedEventHandler changed = (_, e) =>
            {
                if (e.PropertyName == nameof(Rom.Cover) && _tick.IsEnabled)
                    Dispatcher.InvokeAsync(() => _web.CoreWebView2?.ExecuteScriptAsync($"setCover({id},{JsonSerializer.Serialize(CoverUrl(game))})"));
            };
            game.PropertyChanged += changed;
            Closed += (_, _) => game.PropertyChanged -= changed;
        }
    }

    private async Task Start()
    {
        try
        {
            // Program Files isn't writable, so WebView2 keeps its profile with our other data.
            var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamStopper");
            var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(data, "WebView2"));
            await _web.EnsureCoreWebView2Async(env);
            // Served from a file, not NavigateToString, so the page may show covers from file:/// paths.
            var page = Path.Combine(data, "console.html");
            using (var res = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/console.html")).Stream)
            using (var file = File.Create(page))
                res.CopyTo(file);
            var core = _web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.WebMessageReceived += OnMessage;
            core.NavigationCompleted += async (_, _) =>
            {
                await core.ExecuteScriptAsync($"init({JsonSerializer.Serialize(new { games = _games.Select((g, i) => Describe(g, i)), status = _status(), running = Running() })})");
                _tick.Start();
                _web.Focus();
            };
            core.Navigate(new Uri(page).AbsoluteUri);
        }
        catch (Exception ex)
        {
            Dialog.Show(Owner, $"Console mode needs the Microsoft Edge WebView2 Runtime, which comes with Windows 10 and 11.\n\nGet it from microsoft.com/edge/webview2, then try again.\n\n{ex.Message}");
            Close();
        }
    }

    private object Describe(Rom game, int id) => new
    {
        id, title = game.Title, console = game.ConsoleName, color = game.Color,
        cover = CoverUrl(game),
        played = game.LastPlayed is null ? "Never played" : "Played " + game.LastPlayedText.ToLowerInvariant(),
        time = game.PlayTimeText, size = game.SizeText, path = game.IsLink ? "" : game.Path
    };

    private static string CoverUrl(Rom game) => game.Cover is { } c && File.Exists(c) ? new Uri(c).AbsoluteUri : "";

    private int[] Running() => Enumerable.Range(0, _games.Count).Where(i => _busy(_games[i])).ToArray();

    private async Task SendState()
    {
        var json = JsonSerializer.Serialize(new { status = _status(), running = Running() });
        if (json == _sent || _web.CoreWebView2 is null) return;
        _sent = json;
        await _web.CoreWebView2.ExecuteScriptAsync($"setState({json})");
    }

    private async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        using var msg = JsonDocument.Parse(e.WebMessageAsJson);
        var type = msg.RootElement.GetProperty("type").GetString();
        if (type == "exit") { Close(); return; }
        // The page is ours, but the id still comes from script: check it.
        if (type == "play" && msg.RootElement.TryGetProperty("id", out var id) && id.TryGetInt32(out var i) && i >= 0 && i < _games.Count)
        {
            var play = _play(_games[i]);
            await SendState();
            await play;
            await SendState();
        }
    }

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
}
