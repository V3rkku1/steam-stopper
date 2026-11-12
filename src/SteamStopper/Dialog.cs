using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SteamStopper;

/// <summary>
/// The app's own message box: same calls and results as MessageBox, drawn in the current theme
/// (colours, corner radius, buttons) instead of Windows' white box.
/// </summary>
public static class Dialog
{
    public static MessageBoxResult Show(string text, string caption = "Steam Multitool",
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None) =>
        Show(null, text, caption, buttons, icon);

    /// <summary>Ask for a line of text. Null when cancelled or left empty.</summary>
    public static string? Ask(Window? owner, string text, string caption, string value)
    {
        var field = new TextBox { Text = value, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 14, 0, 0) };
        field.Loaded += (_, _) => { field.Focus(); field.SelectAll(); };
        return Show(owner, text, caption, MessageBoxButton.OKCancel, MessageBoxImage.None, field) == MessageBoxResult.OK && field.Text.Trim() is { Length: > 0 } t ? t : null;
    }

    public static MessageBoxResult Show(Window? owner, string text, string caption = "Steam Multitool",
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None, TextBox? field = null)
    {
        var result = buttons is MessageBoxButton.YesNo ? MessageBoxResult.No : buttons is MessageBoxButton.OK ? MessageBoxResult.OK : MessageBoxResult.Cancel;
        owner ??= Application.Current?.MainWindow is { IsVisible: true } main ? main : null;
        var window = new Window
        {
            Title = caption,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            ShowInTaskbar = owner is null,
            Topmost = owner is null,
            Owner = owner,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            FontFamily = (FontFamily)Application.Current!.FindResource("TextFont"),
            FontSize = 13
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        void Add(string label, MessageBoxResult value, bool primary)
        {
            var button = new Button
            {
                Content = label,
                MinWidth = 96,
                Margin = new Thickness(8, 0, 0, 0),
                Style = (Style)window.FindResource(primary ? "SafeButton" : "GhostButton"),
                IsDefault = primary,
                IsCancel = value is MessageBoxResult.Cancel or MessageBoxResult.No || buttons == MessageBoxButton.OK
            };
            button.Click += (_, _) => { result = value; window.Close(); };
            row.Children.Add(button);
        }
        switch (buttons)
        {
            case MessageBoxButton.YesNo: Add("No", MessageBoxResult.No, false); Add("Yes", MessageBoxResult.Yes, true); break;
            case MessageBoxButton.YesNoCancel: Add("Cancel", MessageBoxResult.Cancel, false); Add("No", MessageBoxResult.No, false); Add("Yes", MessageBoxResult.Yes, true); break;
            case MessageBoxButton.OKCancel: Add("Cancel", MessageBoxResult.Cancel, false); Add("OK", MessageBoxResult.OK, true); break;
            default: Add("OK", MessageBoxResult.OK, true); break;
        }

        // A coloured round badge instead of Windows' system icons.
        var (glyph, brush) = icon switch
        {
            MessageBoxImage.Warning => ("!", "Amber"),
            MessageBoxImage.Error => ("×", "Red"),
            MessageBoxImage.Question => ("?", "Accent"),
            MessageBoxImage.Information => ("i", "Accent"),
            _ => ("", "")
        };
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        if (glyph.Length > 0)
            header.Children.Add(new Border
            {
                Width = 28, Height = 28, CornerRadius = new CornerRadius(14), Margin = new Thickness(0, 0, 12, 0),
                Background = (Brush)window.FindResource(brush),
                Child = new TextBlock { Text = glyph, Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            });
        header.Children.Add(new TextBlock
        {
            Text = caption, FontSize = 16, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
            FontFamily = (FontFamily)window.FindResource("DisplayFont"), Foreground = (Brush)window.FindResource("Text")
        });

        var card = new Border
        {
            Margin = new Thickness(16),
            Padding = new Thickness(24, 20, 24, 20),
            MinWidth = 340,
            MaxWidth = 520,
            Background = (Brush)window.FindResource("Popup"),
            BorderBrush = (Brush)window.FindResource("Line"),
            BorderThickness = new Thickness(1),
            CornerRadius = (CornerRadius)window.FindResource("CardRadius"),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 28, ShadowDepth = 6, Opacity = 0.5 },
            Child = new StackPanel
            {
                Children =
                {
                    header,
                    new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)window.FindResource("Muted"), LineHeight = 19 },
                    (UIElement?)field ?? new Border(),
                    row
                }
            }
        };
        // Drag by the card, like a title bar.
        card.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) window.DragMove(); };
        window.Content = card;
        window.ShowDialog();
        return result;
    }
}
