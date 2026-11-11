using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using SteamStopper.Core;

namespace SteamStopper;

// Save backups (Settings, and "Back up saves" on a game): zip an emulator's save folders, restore one.
public partial class MainWindow
{
    public sealed record SaveRow(string Name, string AppDir, List<(string Place, string Dir)> Found, List<string> Zips)
    {
        public bool HasSaves => Found.Count > 0;
        public bool HasBackups => Zips.Count > 0;
        public string Folders => HasSaves ? string.Join("\n", Found.Select(f => f.Dir)) : "No save folder found yet. Play once, then back up.";
        public string Detail => (HasSaves ? $"{Found.Count} save folder{(Found.Count == 1 ? "" : "s")}" : "No saves found yet")
            + (HasBackups ? $"  ·  last backup {File.GetLastWriteTime(Zips[0]):d MMM HH:mm}" : "  ·  never backed up");
    }

    private void RefreshSaves()
    {
        // Installed emulators and ports that we know where to look for; their folder is where {app} points.
        var rows = AppHub.Snapshot(AppHub.Emulators)
            .Where(e => e.LaunchPath is not null && SaveBackup.Places.ContainsKey(e.Name))
            .Select(e => SaveRowFor(e.Name, Path.GetDirectoryName(e.LaunchPath!)!))
            .ToList();
        SavesList.ItemsSource = rows;
        SavesEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SavesAllButton.IsEnabled = rows.Any(r => r.HasSaves);
    }

    private static SaveRow SaveRowFor(string name, string appDir) =>
        new(name, appDir, SaveBackup.Found(name, appDir), SaveBackup.Backups(name));

    private async Task<string?> BackUp(string name, string appDir)
    {
        StatusText.Text = $"Backing up {name} saves…";
        try
        {
            var zip = await Task.Run(() => SaveBackup.Create(name, appDir));
            StatusText.Text = zip is null ? $"No {name} saves found to back up." : $"Backed up {name} saves to {zip}.";
            return zip;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not back up {name} saves: {ex.Message}";
            return null;
        }
    }

    private async void SaveBackup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SaveRow row } button) return;
        button.IsEnabled = false;
        await BackUp(row.Name, row.AppDir);
        RefreshSaves();
    }

    private async void SavesAll_Click(object sender, RoutedEventArgs e)
    {
        SavesAllButton.IsEnabled = false;
        var rows = (SavesList.ItemsSource as List<SaveRow> ?? []).Where(r => r.HasSaves).ToList();
        var done = 0;
        foreach (var row in rows)
            if (await BackUp(row.Name, row.AppDir) is not null) done++;
        StatusText.Text = $"Backed up saves for {done} of {rows.Count}.";
        RefreshSaves();
    }

    private async void SaveRestore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: SaveRow row }) return;
        if (EmulatorRunning(row.Name))
        {
            StatusText.Text = $"Close {row.Name} first, so it doesn't overwrite the restored saves.";
            return;
        }
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"Choose a {row.Name} backup", Filter = "Save backups|*.zip", InitialDirectory = SaveBackup.FolderFor(row.Name)
        };
        if (dialog.ShowDialog(this) != true) return;
        if (MessageBox.Show(this,
                $"Restore {row.Name} saves from {Path.GetFileName(dialog.FileName)}?\n\nSaves with the same name are replaced. Your current saves are zipped into the \"Before restore\" folder first, so you can undo this.",
                "Restore saves", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        try
        {
            var count = await Task.Run(() => SaveBackup.Restore(row.Name, row.AppDir, dialog.FileName));
            StatusText.Text = $"Restored {count} {row.Name} save file{(count == 1 ? "" : "s")}.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not restore {row.Name} saves: {ex.Message}";
        }
        RefreshSaves();
    }

    private void SavesOpen_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(SaveBackup.Root);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{SaveBackup.Root}\"") { UseShellExecute = true })?.Dispose();
    }

    private async void GameSaves_Click(object sender, RoutedEventArgs e)
    {
        if (GameDetail.DataContext is not Rom rom) return;
        if (EmulatorsFor(rom).FirstOrDefault() is not { LaunchPath: { } exe } emu)
        {
            StatusText.Text = $"No emulator for {rom.ConsoleName} is installed, so there are no saves to back up.";
            return;
        }
        if (!SaveBackup.Places.ContainsKey(emu.Name))
        {
            // mGBA, melonDS and others keep a .sav next to the game file itself.
            StatusText.Text = $"{emu.Name} keeps saves next to your game files. Back up {Path.GetDirectoryName(rom.Path)} instead.";
            return;
        }
        await BackUp(emu.Name, Path.GetDirectoryName(exe)!);
    }
}
