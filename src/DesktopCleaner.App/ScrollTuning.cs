using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DesktopCleaner.App;

/// <summary>
/// Makes mouse-wheel and trackpad scrolling proportional. WPF scrolls a fixed step for every wheel message no matter how
/// small the movement, and a precision trackpad sends many tiny messages, so pages flew past. Here each message scrolls
/// by its own size (a classic 120-unit mouse notch is still 48 pixels), and lists scroll by pixels instead of whole rows.
/// </summary>
public static class ScrollTuning
{
    private const double PixelsPerWheelUnit = 48.0 / 120.0;

    public static void Install()
    {
        EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnWheel));
        EventManager.RegisterClassHandler(typeof(ItemsControl), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnItemsControlLoaded));
    }

    private static void OnItemsControlLoaded(object sender, RoutedEventArgs e)
    {
        // Keeps row virtualization (fast with long lists) but scrolls in pixels rather than a whole row at a time.
        if (sender is ItemsControl ic) VirtualizingPanel.SetScrollUnit(ic, ScrollUnit.Pixel);
    }

    private static void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || Keyboard.Modifiers != ModifierKeys.None || sender is not ScrollViewer sv || sv.ScrollableHeight <= 0) return;
        sv.ScrollToVerticalOffset(Math.Clamp(sv.VerticalOffset - e.Delta * PixelsPerWheelUnit, 0, sv.ScrollableHeight));
        e.Handled = true;
    }
}
