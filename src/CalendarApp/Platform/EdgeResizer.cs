using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace CalendarApp.Platform;

/// <summary>
/// Enables edge/corner resize for a borderless layered window, driven from the
/// WINDOW level (not per-grip elements).
///
/// The panel is AllowsTransparency=true (WS_EX_LAYERED) + WS_EX_NOACTIVATE:
/// WindowChrome.ResizeBorderThickness, a WM_NCHITTEST border hit-test and the
/// WM_SYSCOMMAND SC_SIZE sizing loop all fail or misbehave on such windows, and
/// per-element grips turned out to be unreliable on the left edge for reasons
/// that are hard to pin down (element hit-testing asymmetry). So this class
/// behaves like a WPF-side WM_NCHITTEST:
///
///  * A full-window rim overlay (1/255 alpha) in MainWindow.xaml makes every
///    transparent margin pixel hit-testable — clicks in the rim always reach
///    the window, no per-element routing involved.
///  * PreviewMouseLeftButtonDown detects the edge from the cursor position
///    against GetWindowRect (a 10 DIP band, mirroring the panel's margin).
///  * During the drag the window is repositioned/resized with a single
///    SetWindowPos call in physical pixels — atomic, no WPF Left/Width
///    property coercion involved.
///
/// Enable from XAML:  &lt;Window platform:EdgeResizer.Enabled="True" …&gt;
/// </summary>
public static class EdgeResizer
{
    /// <summary>Edge drag band in DIPs — equals the panel's margin, so grabbing
    /// the panel's outer border also counts as an edge press.</summary>
    private const double BandDip = 10;

    private const int SWP_NOZORDER = 0x0004;
    private const int SWP_NOACTIVATE = 0x0010;

    private enum Edge
    {
        Left,
        Right,
        Top,
        Bottom,
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight,
    }

    private sealed class DragState
    {
        public required IntPtr Hwnd { get; init; }
        public required Edge Edge { get; init; }
        public required double Scale { get; init; }
        public required double MinWidth { get; init; }
        public required double MinHeight { get; init; }
        public required RECT StartRect { get; init; }
        public required POINT StartCursor { get; init; }
    }

    private static DragState? _drag;

    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(EdgeResizer),
        new PropertyMetadata(false, OnEnabledChanged));

    public static void SetEnabled(DependencyObject element, bool value) =>
        element.SetValue(EnabledProperty, value);

    public static bool GetEnabled(DependencyObject element) =>
        (bool)element.GetValue(EnabledProperty);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Window window)
        {
            return;
        }

        if ((bool)e.NewValue)
        {
            window.PreviewMouseLeftButtonDown += OnPressed;
            window.PreviewMouseMove += OnMove;
            window.PreviewMouseLeftButtonUp += OnReleased;
            window.MouseLeave += OnMouseLeave;
            window.IsVisibleChanged += OnIsVisibleChanged;
            window.LostMouseCapture += OnLostMouseCapture;
        }
        else
        {
            window.PreviewMouseLeftButtonDown -= OnPressed;
            window.PreviewMouseMove -= OnMove;
            window.PreviewMouseLeftButtonUp -= OnReleased;
            window.MouseLeave -= OnMouseLeave;
            window.IsVisibleChanged -= OnIsVisibleChanged;
            window.LostMouseCapture -= OnLostMouseCapture;
        }
    }

    /// <summary>
    /// Mouse.OverrideCursor 是应用级全局状态：指针离开面板后若不清空，托盘
    /// 右键菜单等其他窗口会继承 ↔ / ↕ 缩放光标（用户看到的“菜单里变横向箭头”）。
    /// 指针离开面板、面板隐藏或捕获丢失时都必须还原。
    /// </summary>
    private static void OnMouseLeave(object? sender, MouseEventArgs e)
    {
        if (_drag is null)
        {
            SetOverrideCursor(null);
        }
    }

    private static void OnIsVisibleChanged(object? sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is Window { IsVisible: false })
        {
            SetOverrideCursor(null);
        }
    }

    private static void OnLostMouseCapture(object? sender, MouseEventArgs e)
    {
        if (_drag is null)
        {
            SetOverrideCursor(null);
        }
    }

    private static void OnPressed(object? sender, MouseButtonEventArgs e)
    {
        if (sender is not Window window || _drag is not null)
        {
            return;
        }

        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var rect))
        {
            return;
        }

        var edge = HitTest(rect, GetCursorPosition(), BandPx(hwnd));
        if (edge is null)
        {
            return; // interior — normal click, let the content handle it
        }

        var dpi = GetDpiForWindow(hwnd);
        _drag = new DragState
        {
            Hwnd = hwnd,
            Edge = edge.Value,
            Scale = dpi == 0 ? 1.0 : dpi / 96.0,
            MinWidth = window.MinWidth > 0 ? window.MinWidth : 240,
            MinHeight = window.MinHeight > 0 ? window.MinHeight : 280,
            StartRect = rect,
            StartCursor = GetCursorPosition(),
        };

        Mouse.Capture(window);
        Mouse.OverrideCursor = CursorFor(edge.Value);
        e.Handled = true;
    }

    private static void OnMove(object? sender, MouseEventArgs e)
    {
        if (sender is not Window window)
        {
            return;
        }

        if (_drag is { } drag)
        {
            Resize(drag);
            return;
        }

        // Hover feedback: swap the cursor while the pointer is in the band.
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, out var rect))
        {
            SetOverrideCursor(CursorFor(HitTest(rect, GetCursorPosition(), BandPx(hwnd))));
        }
    }

    private static void OnReleased(object? sender, MouseButtonEventArgs e)
    {
        if (_drag is null)
        {
            return;
        }

        _drag = null;
        if (sender is Window window)
        {
            Mouse.Capture(null);
            // Restore the hover cursor for the current position.
            OnMove(window, e);
        }
        else
        {
            Mouse.OverrideCursor = null;
        }
    }

    private static void Resize(DragState drag)
    {
        var cursor = GetCursorPosition();
        int dx = cursor.X - drag.StartCursor.X;
        int dy = cursor.Y - drag.StartCursor.Y;

        var start = drag.StartRect;
        int left = start.Left, top = start.Top;
        int width = start.Right - start.Left;
        int height = start.Bottom - start.Top;

        bool movesLeft = drag.Edge is Edge.Left or Edge.TopLeft or Edge.BottomLeft;
        bool movesRight = drag.Edge is Edge.Right or Edge.TopRight or Edge.BottomRight;
        bool movesTop = drag.Edge is Edge.Top or Edge.TopLeft or Edge.TopRight;
        bool movesBottom = drag.Edge is Edge.Bottom or Edge.BottomLeft or Edge.BottomRight;

        if (movesLeft)
        {
            left += dx;
            width -= dx;
        }

        if (movesRight)
        {
            width += dx;
        }

        if (movesTop)
        {
            top += dy;
            height -= dy;
        }

        if (movesBottom)
        {
            height += dy;
        }

        // Enforce the window's minimums in physical pixels; when the moving
        // edge is clamped, the opposite (fixed) edge stays put.
        int minW = (int)Math.Round(drag.MinWidth * drag.Scale);
        int minH = (int)Math.Round(drag.MinHeight * drag.Scale);
        if (width < minW)
        {
            width = minW;
            if (movesLeft)
            {
                left = start.Right - minW;
            }
        }

        if (height < minH)
        {
            height = minH;
            if (movesTop)
            {
                top = start.Bottom - minH;
            }
        }

        SetWindowPos(drag.Hwnd, IntPtr.Zero, left, top, width, height,
            SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>Maps a screen point to the edge band it falls in, or null for the interior.</summary>
    private static Edge? HitTest(RECT rect, POINT cursor, int band)
    {
        bool left = cursor.X >= rect.Left && cursor.X <= rect.Left + band;
        bool right = cursor.X >= rect.Right - band && cursor.X <= rect.Right;
        bool top = cursor.Y >= rect.Top && cursor.Y <= rect.Top + band;
        bool bottom = cursor.Y >= rect.Bottom - band && cursor.Y <= rect.Bottom;

        if (top && left) return Edge.TopLeft;
        if (top && right) return Edge.TopRight;
        if (bottom && left) return Edge.BottomLeft;
        if (bottom && right) return Edge.BottomRight;
        if (left) return Edge.Left;
        if (right) return Edge.Right;
        if (top) return Edge.Top;
        if (bottom) return Edge.Bottom;
        return null;
    }

    private static int BandPx(IntPtr hwnd)
    {
        var dpi = GetDpiForWindow(hwnd);
        return Math.Max(1, (int)Math.Round(BandDip * (dpi == 0 ? 96 : dpi) / 96.0));
    }

    private static void SetOverrideCursor(Cursor? cursor)
    {
        if (!ReferenceEquals(Mouse.OverrideCursor, cursor))
        {
            Mouse.OverrideCursor = cursor;
        }
    }

    private static Cursor? CursorFor(Edge? edge) => edge switch
    {
        Edge.Left or Edge.Right => Cursors.SizeWE,
        Edge.Top or Edge.Bottom => Cursors.SizeNS,
        Edge.TopLeft or Edge.BottomRight => Cursors.SizeNWSE,
        Edge.TopRight or Edge.BottomLeft => Cursors.SizeNESW,
        _ => null,
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    private static POINT GetCursorPosition()
    {
        GetCursorPos(out var point);
        return point;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);
}
