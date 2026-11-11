using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using SteamStopper.Core;

namespace SteamStopper;

// Network page: live download/upload per app from the kernel's network events, with a firewall Block button.
public partial class MainWindow
{
    public sealed class NetRow : INotifyPropertyChanged
    {
        public string Name { get; init; } = "";
        public string Path { get; init; } = "";
        public double Down { get; set; }
        public double Up { get; set; }
        public long Total { get; set; }
        public bool Blocked { get; set; }
        public string DownText => Blocked ? "blocked" : Down < 1024 ? "—" : Format.Speed(Down);
        public string UpText => Blocked ? "" : Up < 1024 ? "—" : Format.Speed(Up);
        public string TotalText => Format.Bytes(Total);
        public string Detail => Path.Length > 0 ? System.IO.Path.GetDirectoryName(Path) ?? "" : "System";
        public string ActionText => Blocked ? "Unblock" : "Block";
        // Windows itself (no path, or in the Windows folder) isn't something to cut off.
        public bool CanBlock => Path.Length > 0 && !Path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase);
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    private NetMonitor? _net;
    private readonly ObservableCollection<NetRow> _netRows = [];
    private readonly Dictionary<int, (string Name, string Path)> _netPids = [];
    private Dictionary<int, (long Received, long Sent)> _netLast = [];

    private void StartNetwork()
    {
        NetGrid.ItemsSource ??= _netRows;
        // Blocked apps stay listed, so they can be unblocked even when they're quiet.
        foreach (var path in _settings.BlockedApps.Where(p => _netRows.All(r => r.Path != p)))
            _netRows.Add(new NetRow { Name = System.IO.Path.GetFileNameWithoutExtension(path), Path = path, Blocked = true });
        if (_net?.Running == true) return;
        _net ??= new NetMonitor();
        _netLast = [];
        var error = _net.Start();
        NetHeadline.Text = error is null ? "Listening…" : "Not available";
        if (error is not null) NetHint.Text = error;
        PaintNetwork();
    }

    private void StopNetwork() => _net?.Stop();

    private (string Name, string Path) NetProcess(int pid)
    {
        if (_netPids.TryGetValue(pid, out var known)) return known;
        (string, string) info = pid switch { 0 => ("Idle", ""), 4 => ("Windows", ""), _ => ("Process " + pid, "") };
        try
        {
            using var p = Process.GetProcessById(pid);
            var path = "";
            try { path = p.MainModule?.FileName ?? ""; } catch { } // protected system processes
            info = (p.ProcessName, path);
        }
        catch { } // already exited: keep the id
        return _netPids[pid] = info;
    }

    private void PaintNetwork()
    {
        if (_net?.Running != true) return;
        var now = _net.Snapshot();
        var seconds = _pollTimer.Interval.TotalSeconds;
        // Per app, not per process: Chrome's twenty helpers are one Chrome.
        var apps = now.GroupBy(p => NetProcess(p.Key)).Select(g => (App: g.Key,
            Down: g.Sum(p => p.Value.Received - _netLast.GetValueOrDefault(p.Key).Received) / seconds,
            Up: g.Sum(p => p.Value.Sent - _netLast.GetValueOrDefault(p.Key).Sent) / seconds,
            Total: g.Sum(p => p.Value.Received + p.Value.Sent))).ToList();
        _netLast = now;
        foreach (var (app, down, up, total) in apps)
        {
            var row = _netRows.FirstOrDefault(r => r.Name == app.Name && r.Path == app.Path);
            if (row is null) _netRows.Add(row = new NetRow { Name = app.Name, Path = app.Path, Blocked = _settings.BlockedApps.Contains(app.Path, StringComparer.OrdinalIgnoreCase) });
            (row.Down, row.Up, row.Total) = (down, up, total);
        }
        foreach (var row in _netRows.Where(r => apps.All(a => a.App.Name != r.Name || a.App.Path != r.Path))) row.Down = row.Up = 0;
        // Busiest first; moved in place so the row under the mouse doesn't flicker away.
        var order = _netRows.OrderByDescending(r => r.Down + r.Up).ThenByDescending(r => r.Total).ToList();
        for (var i = 0; i < order.Count; i++)
        {
            var at = _netRows.IndexOf(order[i]);
            if (at != i) _netRows.Move(at, i);
            order[i].Refresh();
        }
        var totalDown = _netRows.Sum(r => r.Down);
        var totalUp = _netRows.Sum(r => r.Up);
        NetDown.Text = Format.Speed(totalDown);
        NetUp.Text = Format.Speed(totalUp);
        var top = order.FirstOrDefault(r => r.Down + r.Up >= 1024);
        NetHeadline.Text = top is null ? "Quiet" : $"{top.Name} is busiest";
        NetEmpty.Visibility = _netRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void NetBlock_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: NetRow row } button || row.Path.Length == 0) return;
        if (!row.Blocked && MessageBox.Show(this,
                $"Block {row.Name} from the internet?\n\n{row.Path}\n\nIt stays blocked, even after a restart, until you press Unblock here.",
                "Block app", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        button.IsEnabled = false;
        var block = !row.Blocked;
        try
        {
            await Task.Run(() => { if (block) Firewall.BlockApp(row.Path); else Firewall.UnblockApp(row.Path); });
            row.Blocked = block;
            _settings.BlockedApps.RemoveAll(p => p.Equals(row.Path, StringComparison.OrdinalIgnoreCase));
            if (block) _settings.BlockedApps.Add(row.Path);
            _settings.Save();
            StatusText.Text = block ? $"Blocked {row.Name} from the internet." : $"{row.Name} can use the internet again.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not change {row.Name}: {ex.Message}";
        }
        finally
        {
            button.IsEnabled = true;
            row.Refresh();
        }
    }
}
