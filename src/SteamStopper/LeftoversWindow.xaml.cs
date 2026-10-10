using System.Windows;
using SteamStopper.Core;

namespace SteamStopper;

/// <summary>Lists leftover folders and lets the user pick which to delete. Nothing is removed without this confirmation.</summary>
public partial class LeftoversWindow : Window
{
    private readonly List<LeftoverItem> _items;

    public LeftoversWindow(List<LeftoverItem> items, string heading)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => DarkTitleBar.Apply(this);
        _items = items;
        HeadingText.Text = heading;
        SubText.Text = $"{items.Count} folder(s) found";
        Grid.ItemsSource = items;
        foreach (var item in items)
            item.PropertyChanged += (_, _) => PaintTotal();
        PaintTotal();
    }

    public (int Removed, int Failed, long Bytes)? Result { get; private set; }

    private void PaintTotal()
    {
        var picked = _items.Where(i => i.Selected).ToList();
        TotalText.Text = $"{picked.Count} selected  ·  {Format.Bytes(picked.Sum(i => i.Bytes))}";
        DeleteButton.IsEnabled = picked.Count > 0;
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        DeleteButton.IsEnabled = false;
        TotalText.Text = "Deleting…";
        var picked = _items.Where(i => i.Selected).ToList();
        Result = await Task.Run(() => AppHub.DeleteLeftovers(picked));
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
