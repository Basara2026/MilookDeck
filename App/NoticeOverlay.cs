using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
namespace MilookDeck.App;

/// <summary>
/// 全画面で視聴中の TVTest を調べる。
/// TVTest のウィンドウが、画面(モニター)全体を覆っていれば、全画面とみなす。
/// 全画面のウィンドウは移動もサイズ変更もしないので、位置を追い続ける必要はなく、「その画面」が分かれば足りる。
/// </summary>
static class FullscreenProbe
{
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }
    delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);

    /// <summary>指定のプロセス(TVTest)が、画面全体を覆うウィンドウを持っていれば、その画面の範囲(物理ピクセル)を返す。</summary>
    public static bool TryGetMonitor(int pid, out Rect monitorPx)
    {
        Rect found = Rect.Empty;
        bool ok = false;
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var p);
            if (p != (uint)pid || !IsWindowVisible(h) || IsIconic(h)) return true;
            if (!GetWindowRect(h, out var r)) return true;
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            var hm = MonitorFromWindow(h, 2 /* MONITOR_DEFAULTTONEAREST */);
            if (hm == IntPtr.Zero || !GetMonitorInfo(hm, ref mi)) return true;
            var m = mi.rcMonitor;
            if (r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom)
            {
                found = new Rect(m.Left, m.Top, m.Right - m.Left, m.Bottom - m.Top);
                ok = true;
                return false;   // 見つかったので探索を終える
            }
            return true;
        }, IntPtr.Zero);
        monitorPx = found;
        return ok;
    }
}

/// <summary>
/// 全画面の TVTest の上に、短い文字(録画予定による視聴終了のカウントダウンなど)を重ねて出す小さなウィンドウ。
/// 背景は半透明の黒、文字は白。マウス操作は後ろの TVTest にそのまま通し、フォーカスも奪わない。
/// 画面全体ではなく、文字の分だけの大きさなので、描画の負荷はごくわずか。
/// </summary>
public sealed class NoticeOverlay : Window
{
    const int GWL_EXSTYLE = -20;
    const long WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;
    static readonly IntPtr HWND_TOPMOST = new(-1);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    readonly TextBlock text;
    readonly Border pill;

    public NoticeOverlay(FontFamily font, FontWeight weight)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Focusable = false;
        IsHitTestVisible = false;

        text = new TextBlock { Foreground = Brushes.White, FontFamily = font, FontWeight = weight, TextAlignment = TextAlignment.Center };
        pill = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0, 0, 0)),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(28, 14, 28, 14),
            Child = text,
        };
        Content = pill;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));   // クリックを通す・フォーカスを奪わない・タスクバーに出さない
    }

    /// <summary>指定の画面(物理ピクセルの範囲)の、上の中央に文字を表示する。表示中なら文字と位置を更新する。</summary>
    public void ShowNotice(string message, Rect monitorPx)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        double monL = monitorPx.Left / dpi.DpiScaleX, monT = monitorPx.Top / dpi.DpiScaleY;
        double monW = monitorPx.Width / dpi.DpiScaleX, monH = monitorPx.Height / dpi.DpiScaleY;

        text.FontSize = Math.Clamp(monH / 36, 18, 48);   // 画面の高さに比例した文字の大きさ
        text.Text = message;
        pill.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Left = monL + (monW - pill.DesiredSize.Width) / 2;
        Top = monT + monH * 0.08;
        if (!IsVisible) Show();

        // TVTest の全画面ウィンドウが前面に出し直されても隠れないよう、毎回、最前面の中の一番上に置き直す
        SetWindowPos(new WindowInteropHelper(this).Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    public void HideNotice()
    {
        if (IsVisible) Hide();
    }
}
