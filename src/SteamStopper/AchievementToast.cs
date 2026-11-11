using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using SteamStopper.Core;

namespace SteamStopper;

/// <summary>
/// iOS-style "Achievement unlocked" banner in the bottom-right corner: slides in, stays six seconds, fades out.
/// Never takes focus from the game. Shows over windowed and borderless games, not exclusive fullscreen.
/// </summary>
public sealed class AchievementToast : Window
{
    private static int _open;

    public AchievementToast(Cheevo cheevo, string game)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        Left = Top = -10000; // placed in Loaded, once its size is known
        SizeToContent = SizeToContent.WidthAndHeight;
        var badge = new Image { Width = 52, Height = 52, Stretch = Stretch.UniformToFill };
        try { badge.Source = new BitmapImage(new Uri(cheevo.BadgeUrl)); } catch { } // offline: no picture
        Content = new Border
        {
            Background = (Brush)FindResource("Popup"),
            BorderBrush = (Brush)FindResource("Line"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(12),
            Margin = new Thickness(12),
            Width = 360,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.45 },
            Child = new DockPanel
            {
                Children =
                {
                    new Border { CornerRadius = new CornerRadius(10), ClipToBounds = true, Child = badge, Margin = new Thickness(0, 0, 12, 0) },
                    new StackPanel
                    {
                        VerticalAlignment = VerticalAlignment.Center,
                        Children =
                        {
                            new TextBlock { Text = $"ACHIEVEMENT UNLOCKED  ·  {cheevo.Points} pts", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("AccentText") },
                            new TextBlock { Text = cheevo.Title, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("Text"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) },
                            new TextBlock { Text = string.IsNullOrEmpty(cheevo.Description) ? game : cheevo.Description, FontSize = 12, Foreground = (Brush)FindResource("Muted"), TextTrimming = TextTrimming.CharacterEllipsis }
                        }
                    }
                }
            }
        };
        Loaded += (_, _) =>
        {
            // Stack above any banner still showing.
            var area = SystemParameters.WorkArea;
            var slot = _open++;
            Left = area.Right - ActualWidth;
            Top = area.Bottom - ActualHeight * (slot + 1);
            Opacity = 0;
            BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(250)));
            BeginAnimation(LeftProperty, new DoubleAnimation(Left + 60, Left, TimeSpan.FromMilliseconds(350)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            var close = new DoubleAnimation(0, TimeSpan.FromMilliseconds(400)) { BeginTime = TimeSpan.FromSeconds(6) };
            close.Completed += (_, _) => Close();
            BeginAnimation(OpacityProperty, close, HandoffBehavior.Compose);
        };
        Closed += (_, _) => _open = Math.Max(0, _open - 1);
    }
}
