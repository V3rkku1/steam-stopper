using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SteamStopper;

// Motion: the start-up animation and the slide-in when switching pages. Both follow Windows'
// "Animation effects" setting (off: no movement at all).
public partial class MainWindow
{
    private static bool Animate => SystemParameters.ClientAreaAnimation;

    private static DoubleAnimation Tween(double? from, double to, int ms, int delayMs = 0, IEasingFunction? ease = null) => new()
    {
        From = from, To = to,
        Duration = TimeSpan.FromMilliseconds(ms),
        BeginTime = TimeSpan.FromMilliseconds(delayMs),
        EasingFunction = ease ?? new CubicEase { EasingMode = EasingMode.EaseOut }
    };

    /// <summary>Gear turns into place, play button pops in, then the overlay fades to reveal the app.</summary>
    private void PlayIntro()
    {
        if (!Animate)
        {
            IntroOverlay.Visibility = Visibility.Collapsed;
            return;
        }
        var tile = (ScaleTransform)IntroTile.RenderTransform;
        tile.BeginAnimation(ScaleTransform.ScaleXProperty, Tween(0.85, 1, 420));
        tile.BeginAnimation(ScaleTransform.ScaleYProperty, Tween(0.85, 1, 420));
        IntroGear.RenderTransform.BeginAnimation(RotateTransform.AngleProperty, Tween(-150, 0, 700));
        IntroGear.BeginAnimation(OpacityProperty, Tween(0, 1, 300));
        var play = (ScaleTransform)IntroPlay.RenderTransform;
        var pop = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 };
        play.BeginAnimation(ScaleTransform.ScaleXProperty, Tween(0, 1, 380, 320, pop));
        play.BeginAnimation(ScaleTransform.ScaleYProperty, Tween(0, 1, 380, 320, pop));
        var fade = Tween(1, 0, 320, 900);
        fade.Completed += (_, _) => IntroOverlay.Visibility = Visibility.Collapsed;
        IntroOverlay.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>The page fades in while sliding up a few pixels, like iOS.</summary>
    private static void SlideIn(UIElement page)
    {
        if (!Animate) return;
        var shift = new TranslateTransform();
        page.RenderTransform = shift;
        shift.BeginAnimation(TranslateTransform.YProperty, Tween(14, 0, 240));
        page.BeginAnimation(OpacityProperty, Tween(0, 1, 200));
    }
}
