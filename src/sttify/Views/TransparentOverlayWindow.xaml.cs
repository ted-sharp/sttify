using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Vanara.PInvoke;
using static Vanara.PInvoke.User32;
using MediaColor = System.Windows.Media.Color;

namespace Sttify.Views;

public partial class TransparentOverlayWindow : Window
{
    private DoubleAnimation? _fadeIn;
    private DoubleAnimation? _fadeOut;

    public TransparentOverlayWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public void SetText(string text)
    {
        OverlayText.Text = text ?? string.Empty;
    }

    public void ApplyAppearance(string fontFamily, double fontSize, string fg, string bg,
        string hAlign, string vAlign, int marginX, int marginY, double opacity)
    {
        OverlayText.FontFamily = new System.Windows.Media.FontFamily(fontFamily);
        OverlayText.FontSize = fontSize;
        if (System.Windows.Media.ColorConverter.ConvertFromString(fg) is MediaColor fgc)
        {
            OverlayText.Fill = new SolidColorBrush(fgc);
        }
        if (System.Windows.Media.ColorConverter.ConvertFromString(bg) is MediaColor bgc)
        {
            OverlayContainer.Background = new SolidColorBrush(bgc);
        }

        OverlayContainer.HorizontalAlignment = Enum.TryParse<System.Windows.HorizontalAlignment>(hAlign, out var h) ? h : System.Windows.HorizontalAlignment.Center;
        OverlayContainer.VerticalAlignment = Enum.TryParse<VerticalAlignment>(vAlign, out var v) ? v : VerticalAlignment.Bottom;
        OverlayContainer.Margin = new Thickness(marginX, marginY, marginX, marginY);
        Opacity = opacity;
    }

    public void ApplyOutline(bool enabled, string color, double thickness)
    {
        if (TryFindResource("OutlineShadow") is not null)
        {
            // Name scope lookup for the element
        }
        var effect = OverlayText.Effect as System.Windows.Media.Effects.DropShadowEffect;
        // For OutlinedText, map outline to Stroke/Thickness
        if (!enabled)
        {
            OverlayText.StrokeThickness = 0;
            return;
        }
        if (System.Windows.Media.ColorConverter.ConvertFromString(color) is MediaColor oc)
        {
            OverlayText.Stroke = new SolidColorBrush(oc);
        }
        OverlayText.StrokeThickness = Math.Max(0, thickness);
    }

    public void SetTopmost(bool topmost)
    {
        Topmost = topmost;
    }

    public void ConfigureFade(bool enable, int fadeInMs, int fadeOutMs, string easing = "Cubic", string easeMode = "Out")
    {
        if (!enable)
        {
            _fadeIn = null;
            _fadeOut = null;
            return;
        }
        var ease = CreateEase(easing, easeMode);
        _fadeIn = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(Math.Max(0, fadeInMs))))
        {
            FillBehavior = FillBehavior.HoldEnd,
            EasingFunction = ease
        };
        _fadeOut = new DoubleAnimation(1, 0, new Duration(TimeSpan.FromMilliseconds(Math.Max(0, fadeOutMs))))
        {
            FillBehavior = FillBehavior.Stop,
            EasingFunction = ease
        };
    }

    private IEasingFunction? CreateEase(string name, string mode)
    {
        EasingMode em = mode?.ToLowerInvariant() switch
        {
            "in" => EasingMode.EaseIn,
            "inout" => EasingMode.EaseInOut,
            _ => EasingMode.EaseOut
        };
        return name?.ToLowerInvariant() switch
        {
            "quadratic" => new QuadraticEase { EasingMode = em },
            "sine" => new SineEase { EasingMode = em },
            "circle" => new CircleEase { EasingMode = em },
            "quartic" => new QuarticEase { EasingMode = em },
            "quintic" => new QuinticEase { EasingMode = em },
            _ => new CubicEase { EasingMode = em }
        };
    }

    public void FadeInIfConfigured()
    {
        if (_fadeIn == null)
        {
            Opacity = 1.0;
            return;
        }
        BeginAnimation(OpacityProperty, _fadeIn);
    }

    public void FadeOutIfConfigured()
    {
        if (_fadeOut == null)
        {
            Opacity = 0.0;
            return;
        }
        BeginAnimation(OpacityProperty, _fadeOut);
    }

    public void SetClickThrough(bool isClickThrough)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
            return;
        var exStyle = (int)GetWindowLong(new HWND(hwnd), WindowLongFlags.GWL_EXSTYLE);
        if (isClickThrough)
        {
            exStyle |= (int)(WindowStylesEx.WS_EX_TRANSPARENT | WindowStylesEx.WS_EX_LAYERED);
        }
        else
        {
            exStyle &= ~(int)WindowStylesEx.WS_EX_TRANSPARENT;
            exStyle |= (int)WindowStylesEx.WS_EX_LAYERED;
        }
        SetWindowLong(new HWND(hwnd), WindowLongFlags.GWL_EXSTYLE, exStyle);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            var exStyle = (int)GetWindowLong(new HWND(hwnd), WindowLongFlags.GWL_EXSTYLE);
            exStyle |= (int)WindowStylesEx.WS_EX_LAYERED;
            SetWindowLong(new HWND(hwnd), WindowLongFlags.GWL_EXSTYLE, exStyle);
        }
    }

    // Win32 APIs now provided by Vanara.PInvoke
}


