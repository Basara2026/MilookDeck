using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
namespace MilookDeck.App;

/// <summary>
/// 色の選択(透明度つき)。Windows 標準の色ダイアログの代わりに使う WPF 版で、テーマ(ライト/ダーク)に自動で従う。
/// 色の四角形・色相・透明度のスライダー、基本色、#AARRGGBB と A/R/G/B の入力欄で色を決める。
/// </summary>
public partial class ColorPickerWindow : Window
{
    const double SvW = 214, SvH = 137, BarW = 214;   // 色の四角形・スライダーの大きさ(XAML と同じ)

    double h, s, v;     // 色相 0〜360、彩度・明度 0〜1
    byte a = 255;       // 透明度 0〜255
    bool updating;

    /// <summary>選ばれた色(透明度つき)。</summary>
    public Color SelectedColor { get; private set; }

    static readonly string[] Basic =
    {
        "#FF8080", "#FFFF80", "#80FF80", "#00FF80", "#80FFFF", "#0080FF", "#FF80C0", "#FF80FF",
        "#FF0000", "#FFFF00", "#80FF00", "#00FF40", "#00FFFF", "#0080C0", "#8080C0", "#FF00FF",
    };

    public ColorPickerWindow(Color initial, Window? owner)
    {
        InitializeComponent();
        if (owner != null) Owner = owner;
        OldSwatch.Background = new SolidColorBrush(initial);
        BuildSwatches();
        SetColor(initial);
    }

    // ---------- 基本色 ----------
    void BuildSwatches()
    {
        foreach (var hex in Basic)
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            var sw = new Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 5.43, 6),
                Background = new SolidColorBrush(c), BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 128, 128, 128)),
                Cursor = Cursors.Hand, ToolTip = hex, Tag = c,
            };
            sw.MouseLeftButtonUp += (_, _) => SetRgb(c.R, c.G, c.B);   // 透明度は今の値を維持
            Swatches.Children.Add(sw);
        }
    }

    // ---------- 色の設定 ----------
    void SetColor(Color c)
    {
        a = c.A;
        RgbToHsv(c.R, c.G, c.B);
        UpdateUi(true);
    }

    void SetRgb(byte r, byte g, byte b)
    {
        RgbToHsv(r, g, b);
        UpdateUi(true);
    }

    /// <summary>RGB から色相・彩度・明度を求める。無彩色(色相が決まらない)のときは、今の色相を保つ。</summary>
    void RgbToHsv(byte r8, byte g8, byte b8)
    {
        double r = r8 / 255.0, g = g8 / 255.0, b = b8 / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        v = max;
        s = max <= 0 ? 0 : d / max;
        if (d <= 0) return;
        double hue;
        if (max == r) hue = ((g - b) / d) % 6;
        else if (max == g) hue = (b - r) / d + 2;
        else hue = (r - g) / d + 4;
        hue *= 60;
        if (hue < 0) hue += 360;
        h = hue;
    }

    static Color HsvToRgb(double h, double s, double v)
    {
        h %= 360; if (h < 0) h += 360;
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        double r, g, b;
        if (h < 60) { r = c; g = x; b = 0; }
        else if (h < 120) { r = x; g = c; b = 0; }
        else if (h < 180) { r = 0; g = c; b = x; }
        else if (h < 240) { r = 0; g = x; b = c; }
        else if (h < 300) { r = x; g = 0; b = c; }
        else { r = c; g = 0; b = x; }
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    /// <summary>画面の表示(つまみの位置・見本・入力欄)を、今の色に合わせる。</summary>
    void UpdateUi(bool updateTexts)
    {
        updating = true;
        var rgb = HsvToRgb(h, s, v);
        var col = Color.FromArgb(a, rgb.R, rgb.G, rgb.B);
        SelectedColor = col;

        SvHueStop.Color = HsvToRgb(h, 1, 1);
        Canvas.SetLeft(SvThumb, s * SvW - 8);
        Canvas.SetTop(SvThumb, (1 - v) * SvH - 8);
        Canvas.SetLeft(HueThumb, h / 360 * BarW - 10.5);
        Canvas.SetLeft(AlphaThumb, a / 255.0 * BarW - 10.5);
        AlphaStop0.Color = Color.FromArgb(0, rgb.R, rgb.G, rgb.B);
        AlphaStop1.Color = Color.FromArgb(255, rgb.R, rgb.G, rgb.B);
        NewSwatch.Background = new SolidColorBrush(col);
        SampleText.Foreground = new SolidColorBrush(col);

        if (updateTexts)
        {
            HexBox.Text = $"#{a:X2}{rgb.R:X2}{rgb.G:X2}{rgb.B:X2}";
            ABox.Text = a.ToString(CultureInfo.InvariantCulture);
            RBox.Text = rgb.R.ToString(CultureInfo.InvariantCulture);
            GBox.Text = rgb.G.ToString(CultureInfo.InvariantCulture);
            BBox.Text = rgb.B.ToString(CultureInfo.InvariantCulture);
        }
        updating = false;
    }

    // ---------- マウス操作(色の四角形・色相・透明度) ----------
    static double Clamp01(double x) => x < 0 ? 0 : x > 1 ? 1 : x;

    void Sv_Down(object sender, MouseButtonEventArgs e) { ((UIElement)sender).CaptureMouse(); SvPick(e.GetPosition((IInputElement)sender)); }
    void Sv_Move(object sender, MouseEventArgs e) { if (((UIElement)sender).IsMouseCaptured) SvPick(e.GetPosition((IInputElement)sender)); }
    void SvPick(Point p) { s = Clamp01(p.X / SvW); v = 1 - Clamp01(p.Y / SvH); UpdateUi(true); }

    void Hue_Down(object sender, MouseButtonEventArgs e) { ((UIElement)sender).CaptureMouse(); HuePick(e.GetPosition((IInputElement)sender)); }
    void Hue_Move(object sender, MouseEventArgs e) { if (((UIElement)sender).IsMouseCaptured) HuePick(e.GetPosition((IInputElement)sender)); }
    void HuePick(Point p) { h = Math.Min(Clamp01(p.X / BarW) * 360, 359.999); UpdateUi(true); }

    void Alpha_Down(object sender, MouseButtonEventArgs e) { ((UIElement)sender).CaptureMouse(); AlphaPick(e.GetPosition((IInputElement)sender)); }
    void Alpha_Move(object sender, MouseEventArgs e) { if (((UIElement)sender).IsMouseCaptured) AlphaPick(e.GetPosition((IInputElement)sender)); }
    void AlphaPick(Point p) { a = (byte)Math.Round(Clamp01(p.X / BarW) * 255); UpdateUi(true); }

    void Drag_Up(object sender, MouseButtonEventArgs e) => ((UIElement)sender).ReleaseMouseCapture();

    // ---------- 入力欄 ----------
    void Box_GotFocus(object sender, KeyboardFocusChangedEventArgs e) => ((TextBox)sender).SelectAll();

    void Hex_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        CommitHex();
        e.Handled = true;
    }
    void Hex_Commit(object sender, RoutedEventArgs e) => CommitHex();

    /// <summary>#AARRGGBB または #RRGGBB(透明度は今の値を維持)。読めないときは元の表示に戻す。</summary>
    void CommitHex()
    {
        if (updating) return;
        var t = HexBox.Text.Trim().TrimStart('#');
        bool ok = (t.Length == 8 || t.Length == 6) && uint.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _);
        if (!ok) { UpdateUi(true); return; }
        int i = 0;
        byte alpha = a;
        if (t.Length == 8) { alpha = Convert.ToByte(t.Substring(0, 2), 16); i = 2; }
        a = alpha;
        SetRgb(Convert.ToByte(t.Substring(i, 2), 16), Convert.ToByte(t.Substring(i + 2, 2), 16), Convert.ToByte(t.Substring(i + 4, 2), 16));
    }

    void Num_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitNum((TextBox)sender, 0); e.Handled = true; }
        else if (e.Key == Key.Up) { CommitNum((TextBox)sender, +1); e.Handled = true; }
        else if (e.Key == Key.Down) { CommitNum((TextBox)sender, -1); e.Handled = true; }
    }
    void Num_Commit(object sender, RoutedEventArgs e) => CommitNum((TextBox)sender, 0);

    /// <summary>A/R/G/B の入力欄を確定する(delta は ↑↓キーでの増減)。数字でなければ元の表示に戻す。</summary>
    void CommitNum(TextBox box, int delta)
    {
        if (updating) return;
        if (!int.TryParse(box.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) { UpdateUi(true); return; }
        n = Math.Clamp(n + delta, 0, 255);
        var rgb = HsvToRgb(h, s, v);
        byte r = rgb.R, g = rgb.G, b = rgb.B;
        switch (box.Tag as string)
        {
            case "A": a = (byte)n; UpdateUi(true); return;
            case "R": r = (byte)n; break;
            case "G": g = (byte)n; break;
            case "B": b = (byte)n; break;
        }
        SetRgb(r, g, b);
    }

    void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
