using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Sttify.Corelib.Config;
using Sttify.Corelib.Diagnostics;
using Sttify.Views;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Sttify.Services;

public class OverlayService : IDisposable
{
    private readonly SettingsProvider _settingsProvider;
    private TransparentOverlayWindow? _window;
    private DateTime _lastUpdate = DateTime.MinValue;
    private string _lastText = string.Empty;
    private bool _initialized;
    private System.Timers.Timer? _autoHideTimer;

    public OverlayService(SettingsProvider settingsProvider)
    {
        _settingsProvider = settingsProvider ?? throw new ArgumentNullException(nameof(settingsProvider));
    }

    public void Initialize()
    {
        if (_initialized) return;
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            _window = new TransparentOverlayWindow();
            _window.Hide();
        });
        _initialized = true;
        Telemetry.LogEvent("OverlayInitialized");
    }

    public async Task UpdateTextAsync(string? text)
    {
        var settings = await _settingsProvider.GetSettingsAsync();
        var overlay = settings.Overlay;
        if (!overlay.Enabled)
        {
            await HideAsync();
            return;
        }

        if (!_initialized)
        {
            Initialize();
        }

        // Throttle updates
        var minIntervalMs = overlay.UpdatePerSec <= 0 ? 0 : (int)Math.Round(1000.0 / overlay.UpdatePerSec);
        if (minIntervalMs > 0 && (DateTime.UtcNow - _lastUpdate).TotalMilliseconds < minIntervalMs)
        {
            return;
        }

        _lastUpdate = DateTime.UtcNow;

        var displayText = (text ?? string.Empty).Replace("\r\n", " \u23CE ").Replace("\n", " \u23CE ");
        if (overlay.MaxChars > 0 && displayText.Length > overlay.MaxChars)
        {
            displayText = displayText[..overlay.MaxChars];
        }

        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            if (_window == null) return;

            _window.ApplyAppearance(
                overlay.FontFamily,
                overlay.FontSize,
                overlay.Foreground,
                overlay.Background,
                overlay.HorizontalAlignment,
                overlay.VerticalAlignment,
                overlay.MarginX,
                overlay.MarginY,
                overlay.Opacity);
            _window.ApplyOutline(overlay.OutlineEnabled, overlay.OutlineColor, overlay.OutlineThickness);

            _window.SetTopmost(overlay.Topmost);
            _window.SetClickThrough(overlay.IsClickThrough);
            _window.ConfigureFade(overlay.EnableFade, overlay.FadeInMs, overlay.FadeOutMs, overlay.FadeEasing, overlay.FadeEaseMode);
            _window.SetText(displayText);

            if (!string.IsNullOrEmpty(displayText))
            {
                if (_window.Visibility != System.Windows.Visibility.Visible)
                {
                    _window.Show();
                    _window.FadeInIfConfigured();
                }
                _window.UpdateLayout();
                PositionWindow(_window, overlay);
                ScheduleAutoHide(overlay);
            }
            else
            {
                if (_window.Visibility == System.Windows.Visibility.Visible)
                {
                    _window.FadeOutIfConfigured();
                    _window.Hide();
                }
            }
        });

        _lastText = displayText;
    }

    public async Task ApplySettingsAsync()
    {
        var settings = await _settingsProvider.GetSettingsAsync();
        var overlay = settings.Overlay;
        if (!_initialized)
        {
            Initialize();
        }
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            if (_window == null) return;
            _window.ApplyAppearance(
                overlay.FontFamily,
                overlay.FontSize,
                overlay.Foreground,
                overlay.Background,
                overlay.HorizontalAlignment,
                overlay.VerticalAlignment,
                overlay.MarginX,
                overlay.MarginY,
                overlay.Opacity);
            _window.ApplyOutline(overlay.OutlineEnabled, overlay.OutlineColor, overlay.OutlineThickness);
            _window.SetTopmost(overlay.Topmost);
            _window.SetClickThrough(overlay.IsClickThrough);
            _window.ConfigureFade(overlay.EnableFade, overlay.FadeInMs, overlay.FadeOutMs, overlay.FadeEasing, overlay.FadeEaseMode);
            if (!string.IsNullOrEmpty(_lastText))
            {
                _window.SetText(_lastText);
                if (_window.Visibility != System.Windows.Visibility.Visible)
                {
                    _window.Show();
                    _window.FadeInIfConfigured();
                }
                _window.UpdateLayout();
                PositionWindow(_window, overlay);
                ScheduleAutoHide(overlay);
            }
            else
            {
                _window.FadeOutIfConfigured();
                _window.Hide();
            }
        });
    }

    private void PositionWindow(TransparentOverlayWindow window, OverlaySettings overlay)
    {
        // Determine target screen working area (in pixels)
        var settings = _settingsProvider.GetSettingsSync();
        System.Drawing.Rectangle workingPx;
        if (overlay.TargetMonitorIndex >= 0)
        {
            var screens = System.Windows.Forms.Screen.AllScreens;
            var idx = Math.Min(overlay.TargetMonitorIndex, screens.Length - 1);
            idx = Math.Max(0, idx);
            workingPx = screens[idx].WorkingArea;
        }
        else if (overlay.TargetMonitorIndex == -2 || settings.Application.AlwaysOnPrimaryMonitor)
        {
            workingPx = System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea ?? Rectangle.Empty;
        }
        else
        {
            var cursorPos = System.Windows.Forms.Cursor.Position;
            var screen = System.Windows.Forms.Screen.FromPoint(cursorPos);
            workingPx = screen.WorkingArea;
        }

        // Convert pixel rect to WPF DIPs
        var source = PresentationSource.FromVisual(window);
        var ct = source?.CompositionTarget;
        if (ct is null)
        {
            return; // cannot position reliably yet
        }
        var m = ct.TransformFromDevice;
        var topLeftDip = m.Transform(new System.Windows.Point(workingPx.Left, workingPx.Top));
        var bottomRightDip = m.Transform(new System.Windows.Point(workingPx.Right, workingPx.Bottom));
        double areaLeft = topLeftDip.X;
        double areaTop = topLeftDip.Y;
        double areaWidth = bottomRightDip.X - topLeftDip.X;
        double areaHeight = bottomRightDip.Y - topLeftDip.Y;

        // Limit width by working area ratio if configured
        double width = window.ActualWidth;
        if (overlay.MaxWidthRatio > 0 && overlay.MaxWidthRatio <= 1.0)
        {
            double maxAllowed = areaWidth * overlay.MaxWidthRatio;
            if (width > maxAllowed)
            {
                width = maxAllowed;
                window.Width = width;
                window.UpdateLayout();
                var newHeight = window.ActualHeight;
                // Assign back to local height variable after declaration below
            }
        }
        double height = window.ActualHeight;

        // Horizontal
        double left;
        switch (overlay.HorizontalAlignment?.ToLowerInvariant())
        {
            case "left":
                left = areaLeft + overlay.MarginX;
                break;
            case "right":
                left = areaLeft + Math.Max(0, areaWidth - width - overlay.MarginX);
                break;
            case "stretch":
            case "center":
            default:
                left = areaLeft + Math.Max(0, (areaWidth - width) / 2.0);
                break;
        }

        // Vertical
        double top;
        switch (overlay.VerticalAlignment?.ToLowerInvariant())
        {
            case "top":
                top = areaTop + overlay.MarginY;
                break;
            case "stretch":
            case "center":
                top = areaTop + Math.Max(0, (areaHeight - height) / 2.0);
                break;
            case "bottom":
            default:
                top = areaTop + Math.Max(0, areaHeight - height - overlay.MarginY);
                break;
        }

        // Apply
        window.Left = Math.Round(left);
        window.Top = Math.Round(top);
    }

    private void ScheduleAutoHide(OverlaySettings overlay)
    {
        if (overlay.AutoHideMs <= 0)
        {
            _autoHideTimer?.Stop();
            return;
        }

        _autoHideTimer ??= new System.Timers.Timer();
        _autoHideTimer.AutoReset = false;
        _autoHideTimer.Interval = Math.Max(100, overlay.AutoHideMs);
        _autoHideTimer.Elapsed -= OnAutoHideElapsed;
        _autoHideTimer.Elapsed += OnAutoHideElapsed;
        _autoHideTimer.Start();
    }

    private void OnAutoHideElapsed(object? sender, System.Timers.ElapsedEventArgs e)
    {
        try
        {
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                _window?.FadeOutIfConfigured();
                _window?.Hide();
            }));
        }
        catch { }
    }

    public Task HideAsync()
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            _window?.Hide();
        });
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            _window?.Close();
            _window = null;
        });
    }
}


