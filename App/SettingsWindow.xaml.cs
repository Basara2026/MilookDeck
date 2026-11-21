using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using Microsoft.Win32;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace MilookDeck.App;

/// <summary>色の設定1行分(#AARRGGBB)。</summary>
public class ColorEdit : INotifyPropertyChanged
{
    string hex;
    public ColorEdit(string label, string hex, bool showReset = false, string? note = null) { Label = label; this.hex = hex; ShowReset = showReset; Note = note ?? ""; }
    public bool ShowReset { get; }
    public string Note { get; }   // 行の右に出す説明文
    public Visibility NoteVisibility => Note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ResetVisibility => ShowReset ? Visibility.Visible : Visibility.Collapsed;   // 「既定値に戻す」ボタンはこの行だけに出す
    public string Label { get; }
    public string Hex
    {
        get => hex;
        set
        {
            hex = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Hex)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Brush)));
        }
    }
    public Brush Brush => TryParse(hex, out var c) ? new SolidColorBrush(c) : Brushes.Transparent;
    public bool IsValid => TryParse(hex, out _);

    public static bool TryParse(string s, out Color c)
    {
        try { c = (Color)ColorConverter.ConvertFromString(s.Trim()); return true; }
        catch { c = default; return false; }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>局1行分(表示/非表示と表示名)。</summary>
public class StationEdit : INotifyPropertyChanged
{
    bool visible;
    string displayName;
    public StationEdit(Station s, bool visible, string displayName) { Station = s; this.visible = visible; this.displayName = displayName; }
    public Station Station { get; }
    public string Key => Station.Key;
    public string Kind => Station.Satellite ? "BS/CS" : "地デジ";
    public string OriginalName => Station.Name;
    public bool Visible { get => visible; set { visible = value; Raise(nameof(Visible)); } }
    public string DisplayName { get => displayName; set { displayName = value; Raise(nameof(DisplayName)); } }
    void Raise(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class SettingsWindow : Window
{
    static SettingsWindow()
    {
        // 選択式(編集不可)のプルダウンで、右クリックにグレーアウトした「切り取り/コピー/貼り付け」が出ないようにする
        // (編集できるプルダウン=フォント・BonDriver は、そのまま)
        EventManager.RegisterClassHandler(typeof(ComboBox), FrameworkElement.ContextMenuOpeningEvent,
            new ContextMenuEventHandler((s, e) => { if (s is ComboBox cb && !cb.IsEditable) e.Handled = true; }));
    }

    public Settings Result { get; }
    public ObservableCollection<string> Drivers { get; } = new();   // 設定画面の BonDriver 候補
    readonly ObservableCollection<TunerDef> tuners;
    readonly ColorEdit[][] colorEdits = new ColorEdit[3][];   // [色設定][行: 文字色, 一覧の背景色, マウスオーバー時の色, 項目選択時の色, ソフト本体の色, 番組内容の文字色]
    readonly CheckBox[] activeChecks = new CheckBox[3];       // 使用する色設定(どれか1つだけ)
    bool updatingChecks;
    static readonly string[] ColorLabels = { "文字色", "一覧の背景色", "マウスオーバー時の色", "項目選択時の色", "ソフト本体の色", "番組内容の文字色" };
    readonly AppState state;
    List<StationEdit> stationEdits = new();

    /// <summary>「適用」時に呼ばれる。本体に設定を反映し、反映後の局一覧を返す。</summary>
    public Func<Settings, List<Station>>? ApplyHandler { get; set; }

    public SettingsWindow(Settings s, AppState state, IEnumerable<Station> stations)
    {
        Result = s;
        this.state = state;
        InitializeComponent();
        BuildStationEdits(stations);
        ApplyBounds();
        ThemeCombo.SelectedValue = s.Theme;
        if (ThemeCombo.SelectedValue == null) ThemeCombo.SelectedValue = "System";
        ButtonStyleCombo.SelectedValue = s.ButtonStyle;
        if (ButtonStyleCombo.SelectedValue == null) ButtonStyleCombo.SelectedValue = "Vertical";

        TrayCheck.IsChecked = s.TrayResident;
        StartupCheck.IsChecked = StartupRegistration.IsRegistered();   // 登録の有無は、スタートアップフォルダのショートカットの状態を表示する
        ExeBox.Text = s.TvTestExe;
        EdcbBox.Text = s.EdcbDir;
        TvTestArgsBox.Text = s.TvTestArgs;
        for (int i = 0; i < Settings.MaxButtons; i++)
        {
            ButtonBoxes[i].Text = s.Buttons[i].Exe;
            ArgsBoxes[i].Text = s.Buttons[i].Args;
            ShowChecks[i].IsChecked = s.Buttons[i].Show;
            updatingIcons = true;
            IconChecks1[i].IsChecked = s.Buttons[i].IconMode == 1;
            IconChecks2[i].IsChecked = s.Buttons[i].IconMode == 2;
            updatingIcons = false;
        }

        RefreshDrivers();
        tuners = new ObservableCollection<TunerDef>(s.Tuners);
        Renumber();
        TunerItems.ItemsSource = tuners;
        CountBox.Text = tuners.Count.ToString();

        BuildFontList();
        FontCombo.Text = FontDisplayName(s.FontFamily);
        SizeBox.Text = s.FontSize.ToString(CultureInfo.InvariantCulture);
        WeightSlider.Value = Math.Clamp(s.FontWeight, 100, 900);
        TunerWidthBox.Text = s.TunerListWidth.ToString(CultureInfo.InvariantCulture);
        DetailWidthBox.Text = s.DetailWidth.ToString(CultureInfo.InvariantCulture);
        (s.TunerAlign == "Center" ? AlignCenterCheck : s.TunerAlign == "Right" ? AlignRightCheck : AlignLeftCheck).IsChecked = true;
        ShowDetailCheck.IsChecked = s.ShowDetail;
        DetailDelayBox.Text = s.DetailDelay.ToString("0.0", CultureInfo.InvariantCulture);
        BackImageCheck.IsChecked = s.UseBackImage;
        BackImageBox.Text = s.BackImagePath;

        BuildColorGrid(s);

        VersionText.Text = AppInfo.VersionText;
    }

    // ---------- フォント名(日本語名で表示。設定には元の名前を保存) ----------
    readonly Dictionary<string, string> fontMap = new(StringComparer.OrdinalIgnoreCase);   // 表示名 → 元の名前(FontFamily.Source)
    readonly Dictionary<string, string> fontReverse = new(StringComparer.OrdinalIgnoreCase); // どの言語の名前でも → 表示名

    static string LocalName(FontFamily f)
    {
        foreach (var tag in new[] { "ja-jp", "ja" })
            if (f.FamilyNames.TryGetValue(XmlLanguage.GetLanguage(tag), out var n) && !string.IsNullOrWhiteSpace(n)) return n;
        return f.Source;
    }

    void BuildFontList()
    {
        // WPF の一覧が日本語名を返さないフォント(バリアブルフォントなど)は、フォントのファイルから読み取った日本語名で補う
        var jaFromFile = FontNames.JapaneseFamilyNames();
        foreach (var f in Fonts.SystemFontFamilies)
        {
            var display = LocalName(f);
            if (string.Equals(display, f.Source, StringComparison.Ordinal) && jaFromFile.TryGetValue(f.Source, out var ja)) display = ja;
            fontMap[display] = f.Source;
            fontReverse[display] = display;
            fontReverse[f.Source] = display;
            foreach (var n in f.FamilyNames.Values) fontReverse.TryAdd(n, display);
        }
        FontCombo.ItemsSource = fontMap.Keys.OrderBy(x => x, StringComparer.CurrentCulture).ToList();
    }

    string FontDisplayName(string saved) => fontReverse.TryGetValue(saved ?? "", out var d) ? d : saved ?? "";

    string FontSourceName(string typed)
    {
        typed = (typed ?? "").Trim();
        if (typed.Length == 0) return "Yu Gothic UI";
        return fontMap.TryGetValue(typed, out var src) ? src : typed;   // 一覧にない名前は入力のまま
    }

    // ---------- 位置・サイズの記憶 ----------
    void ApplyBounds()
    {
        if (state.SetLeft is not double l || state.SetTop is not double t ||
            state.SetWidth is not double w || state.SetHeight is not double h) return;
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                              SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var inter = Rect.Intersect(new Rect(l, t, w, h), screen);
        if (inter.IsEmpty || inter.Width < 100 || inter.Height < 50) return;   // 画面外なら無視
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = l; Top = t;
        Width = Math.Max(w, MinWidth); Height = Math.Max(h, MinHeight);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        var b = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        state.SetLeft = b.Left; state.SetTop = b.Top; state.SetWidth = b.Width; state.SetHeight = b.Height;
        base.OnClosing(e);
    }

    // ---------- ファイル ----------
    void BrowseExe_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "実行ファイル (*.exe)|*.exe|すべてのファイル (*.*)|*.*",
            FileName = ExeBox.Text
        };
        if (dlg.ShowDialog(this) == true) { ExeBox.Text = dlg.FileName; RefreshDrivers(); }
    }

    /// <summary>局名の表の高さを、8行ぶん(局が8未満ならその数)が見える高さにする。文字の大きさで行の高さが変わっても、見える行数は変わらない。</summary>
    void StationScroll_Loaded(object sender, RoutedEventArgs e) => QueueFitStationRows();
    void StationScroll_VisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => QueueFitStationRows();
    void QueueFitStationRows() => Dispatcher.BeginInvoke(new Action(FitStationRows), System.Windows.Threading.DispatcherPriority.Loaded);

    void FitStationRows()
    {
        if (stationEdits.Count == 0) return;
        if (StationItems.ItemContainerGenerator.ContainerFromIndex(0) is not FrameworkElement row || row.ActualHeight <= 0) return;
        double h = row.ActualHeight * Math.Min(8, stationEdits.Count) + 2;
        if (Math.Abs(StationScroll.Height - h) > 0.5) StationScroll.Height = h;
    }

    void BrowseBackImage_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "背景画像",
            Filter = "画像ファイル (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|すべてのファイル (*.*)|*.*",
            FileName = BackImageBox.Text
        };
        if (dlg.ShowDialog(this) == true) BackImageBox.Text = dlg.FileName;
    }

    void BrowseEdcb_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "EDCB フォルダ", InitialDirectory = EdcbBox.Text };
        if (dlg.ShowDialog(this) == true) EdcbBox.Text = dlg.FolderName;
    }

    TextBox[] ButtonBoxes => new[] { Button1Box, Button2Box, Button3Box, Button4Box, Button5Box };
    TextBox[] ArgsBoxes => new[] { Button1ArgsBox, Button2ArgsBox, Button3ArgsBox, Button4ArgsBox, Button5ArgsBox };
    // アイコンの表示パターン(ボタンごとに、パターン1・パターン2・どちらも選ばない=アイコンなし。同時には1つだけ)
    CheckBox[] IconChecks1 => new[] { IconP1Check1, IconP1Check2, IconP1Check3, IconP1Check4, IconP1Check5 };
    CheckBox[] IconChecks2 => new[] { IconP2Check1, IconP2Check2, IconP2Check3, IconP2Check4, IconP2Check5 };
    bool updatingIcons;

    void IconPattern_Checked(object sender, RoutedEventArgs e)
    {
        if (updatingIcons || sender is not CheckBox cb || !int.TryParse(cb.Tag as string, out var i)) return;
        updatingIcons = true;
        var other = ReferenceEquals(cb, IconChecks1[i]) ? IconChecks2[i] : IconChecks1[i];
        other.IsChecked = false;
        updatingIcons = false;
    }

    CheckBox[] ShowChecks => new[] { ShowButton1Check, ShowButton2Check, ShowButton3Check, ShowButton4Check, ShowButton5Check };

    void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && int.TryParse(b.Tag as string, out var i) && i >= 0 && i < Settings.MaxButtons)
            BrowseExeInto(ButtonBoxes[i]);
    }

    void BrowseExeInto(TextBox box)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "実行ファイル (*.exe)|*.exe|すべてのファイル (*.*)|*.*",
            FileName = box.Text
        };
        if (dlg.ShowDialog(this) == true) box.Text = dlg.FileName;
    }

    void ExeBox_LostFocus(object sender, RoutedEventArgs e) => RefreshDrivers();

    /// <summary>TVTest フォルダの BonDriver フォルダにある .dll を候補にする(編集中の入力が消えないよう差分で更新)。</summary>
    void RefreshDrivers()
    {
        var list = new List<string>();
        try
        {
            var dir = Path.Combine(Path.GetDirectoryName(ExeBox.Text) ?? "", "BonDriver");
            if (Directory.Exists(dir))
                list = Directory.EnumerateFiles(dir, "*.dll").Select(f => Path.GetFileName(f)!).OrderBy(x => x).ToList();
        }
        catch { }
        foreach (var d in Drivers.Where(d => !list.Contains(d)).ToList()) Drivers.Remove(d);
        foreach (var d in list.Where(d => !Drivers.Contains(d))) Drivers.Add(d);
    }

    // ---------- チューナー ----------
    void Renumber()
    {
        for (int i = 0; i < tuners.Count; i++) tuners[i].No = i + 1;
    }

    void ApplyCount()
    {
        if (!int.TryParse(CountBox.Text.Trim(), out var n)) n = tuners.Count;
        n = Math.Clamp(n, 1, 16);
        while (tuners.Count < n)
        {
            var last = tuners.LastOrDefault();
            tuners.Add(new TunerDef { Label = $"チューナー {tuners.Count + 1}", Kind = last?.Kind ?? TunerDef.KindTerrestrial });
        }
        while (tuners.Count > n) tuners.RemoveAt(tuners.Count - 1);
        Renumber();
        CountBox.Text = n.ToString();
    }

    void CountUp_Click(object sender, RoutedEventArgs e) { Step(CountBox, +1); ApplyCount(); }
    void CountDown_Click(object sender, RoutedEventArgs e) { Step(CountBox, -1); ApplyCount(); }
    void CountBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Up || e.Key == Key.Down)
        {
            Step(CountBox, e.Key == Key.Up ? +1 : -1);
            ApplyCount();
            e.Handled = true;
        }
    }
    void CountBox_LostFocus(object sender, RoutedEventArgs e) => ApplyCount();
    void CountBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { ApplyCount(); e.Handled = true; } }

    // ---------- 色 ----------
    /// <summary>色設定1〜3の表を作る(列: 項目名 | 色設定1 | 色設定2 | 色設定3)。</summary>
    void BuildColorGrid(Settings s)
    {
        var g = ColorGrid;
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        for (int k = 0; k < 3; k++)
        {
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(124) });    // 色の値
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });     // 見本
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });        // 「選択…」
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(k < 2 ? 16 : 0) });   // 色設定どうしの間隔
        }
        for (int r = 0; r < 8; r++) g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (int k = 0; k < 3; k++)
        {
            var set = s.ColorSets[k];
            var values = new[] { set.TextColor, set.ListBackColor, set.HoverColor, set.SelectColor, set.WindowColor, set.DetailTextColor };
            colorEdits[k] = new ColorEdit[6];
            for (int r = 0; r < 6; r++)
            {
                var ce = new ColorEdit(ColorLabels[r], values[r]);
                colorEdits[k][r] = ce;
                int col = 1 + k * 4;

                var tb = new TextBox { Padding = new Thickness(2), Margin = new Thickness(0, 2, 0, 2) };
                tb.SetBinding(TextBox.TextProperty, new Binding("Hex") { Source = ce, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
                Grid.SetRow(tb, r); Grid.SetColumn(tb, col); g.Children.Add(tb);

                var sw = new Border { Margin = new Thickness(6, 2, 6, 2), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) };
                sw.SetBinding(Border.BackgroundProperty, new Binding("Brush") { Source = ce });
                Grid.SetRow(sw, r); Grid.SetColumn(sw, col + 1); g.Children.Add(sw);

                var btn = new Button { Content = "選択…", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(0, 2, 0, 2), Tag = ce };
                btn.Click += ColorPick_Click;
                Grid.SetRow(btn, r); Grid.SetColumn(btn, col + 2); g.Children.Add(btn);
            }

            // 使用する色設定(どれか1つ)
            var cb = new CheckBox { Content = $"色設定{k + 1}", Margin = new Thickness(0, 10, 0, 4), HorizontalAlignment = HorizontalAlignment.Center, Tag = k };
            // 文字の大きさ・太さは、他のラベル(「番組内容の幅」)に合わせる
            cb.SetBinding(FontSizeProperty, new Binding("FontSize") { Source = DetailWidthLabel });
            cb.SetBinding(FontWeightProperty, new Binding("FontWeight") { Source = DetailWidthLabel });
            cb.Checked += ActiveCheck_Checked;
            cb.Unchecked += ActiveCheck_Unchecked;
            Grid.SetRow(cb, 6); Grid.SetColumn(cb, 1 + k * 4); Grid.SetColumnSpan(cb, 3); g.Children.Add(cb);
            activeChecks[k] = cb;
        }
        for (int r = 0; r < 6; r++)
        {
            var label = new TextBlock { Text = ColorLabels[r], VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(label, r); Grid.SetColumn(label, 0); g.Children.Add(label);
        }

        // 既定値に戻せるのは色設定1だけ
        var reset = new Button { Content = "既定値に戻す", Padding = new Thickness(12, 3, 12, 3) };
        reset.Click += ResetDisplay_Click;
        var note = new TextBlock { Text = "色設定1の6つの色を既定値に戻します。", Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        // ボタンのすぐ右に説明を並べる
        var resetRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        resetRow.Children.Add(reset);
        resetRow.Children.Add(note);
        Grid.SetRow(resetRow, 7); Grid.SetColumn(resetRow, 1); Grid.SetColumnSpan(resetRow, 11); g.Children.Add(resetRow);

        updatingChecks = true;
        activeChecks[Math.Clamp(s.ActiveColorSet, 0, 2)].IsChecked = true;
        updatingChecks = false;
    }

    // チューナー欄の揃え(左・中央・右)は、必ずどれか1つだけが選ばれた状態にする
    bool updatingAlign;
    CheckBox[] AlignChecks => new[] { AlignLeftCheck, AlignCenterCheck, AlignRightCheck };

    void Align_Checked(object sender, RoutedEventArgs e)
    {
        if (updatingAlign) return;
        updatingAlign = true;
        foreach (var other in AlignChecks) if (!ReferenceEquals(other, sender)) other.IsChecked = false;
        updatingAlign = false;
    }

    void Align_Unchecked(object sender, RoutedEventArgs e)
    {
        if (updatingAlign) return;
        updatingAlign = true;
        if (AlignChecks.All(x => x.IsChecked != true)) ((CheckBox)sender).IsChecked = true;   // 選択中のものは外せない
        updatingAlign = false;
    }

    // 使用する色設定は、必ずどれか1つだけが選ばれた状態にする
    void ActiveCheck_Checked(object sender, RoutedEventArgs e)
    {
        if (updatingChecks) return;
        updatingChecks = true;
        foreach (var other in activeChecks) if (!ReferenceEquals(other, sender)) other.IsChecked = false;
        updatingChecks = false;
    }

    void ActiveCheck_Unchecked(object sender, RoutedEventArgs e)
    {
        if (updatingChecks) return;
        updatingChecks = true;
        if (activeChecks.All(x => x.IsChecked != true)) ((CheckBox)sender).IsChecked = true;   // 選択中のものは外せない
        updatingChecks = false;
    }

    void ColorPick_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not ColorEdit ce) return;
        bool valid = ColorEdit.TryParse(ce.Hex, out var cur);
        var dlg = new ColorPickerWindow(valid ? cur : Color.FromArgb(255, 0, 0, 0), this);   // 透明度も選べる WPF 版の色選択
        if (dlg.ShowDialog() != true) return;
        var c = dlg.SelectedColor;
        ce.Hex = $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
    }

    /// <summary>色設定1の6つの色を既定値に戻す(色設定2・3は変えない)。</summary>
    void ResetDisplay_Click(object sender, RoutedEventArgs e)
    {
        var d = new ColorSet();
        colorEdits[0][0].Hex = d.TextColor;
        colorEdits[0][1].Hex = d.ListBackColor;
        colorEdits[0][2].Hex = d.HoverColor;
        colorEdits[0][3].Hex = d.SelectColor;
        colorEdits[0][4].Hex = d.WindowColor;
        colorEdits[0][5].Hex = d.DetailTextColor;
    }

    // ---------- 数値入力(▲▼・↑↓キーで増減) ----------
    void SizeUp_Click(object sender, RoutedEventArgs e) => Step(SizeBox, +1);
    void SizeDown_Click(object sender, RoutedEventArgs e) => Step(SizeBox, -1);
    void TunerWidthUp_Click(object sender, RoutedEventArgs e) => Step(TunerWidthBox, +1);
    void TunerWidthDown_Click(object sender, RoutedEventArgs e) => Step(TunerWidthBox, -1);
    void DetailWidthUp_Click(object sender, RoutedEventArgs e) => Step(DetailWidthBox, +1);
    void DetailWidthDown_Click(object sender, RoutedEventArgs e) => Step(DetailWidthBox, -1);
    void DetailDelayUp_Click(object sender, RoutedEventArgs e) => Step(DetailDelayBox, +1);
    void DetailDelayDown_Click(object sender, RoutedEventArgs e) => Step(DetailDelayBox, -1);

    void Spin_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is TextBox b && (e.Key == Key.Up || e.Key == Key.Down))
        {
            Step(b, e.Key == Key.Up ? +1 : -1);
            e.Handled = true;
        }
    }

    /// <summary>入力欄の値を増減する。Tag は「最小|最大|きざみ」(4つ目に表示の書式を付けてもよい)。</summary>
    static void Step(TextBox box, int dir)
    {
        var t = ((box.Tag as string) ?? "0|1000|1").Split('|');
        double min = double.Parse(t[0], CultureInfo.InvariantCulture);
        double max = double.Parse(t[1], CultureInfo.InvariantCulture);
        double step = double.Parse(t[2], CultureInfo.InvariantCulture);
        if (!double.TryParse(box.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) v = min;
        v = Math.Round(Math.Clamp(v + dir * step, min, max), 4);
        box.Text = v.ToString(t.Length > 3 ? t[3] : "0.##", CultureInfo.InvariantCulture);
        box.CaretIndex = box.Text.Length;
    }

    // ---------- 局(表示/非表示・表示名) ----------
    void BuildStationEdits(IEnumerable<Station> stations)
    {
        stationEdits = stations.Select(st => new StationEdit(st, !state.Hidden.Contains(st.Key),
            Result.StationNames.TryGetValue(st.Key, out var n) && !string.IsNullOrWhiteSpace(n) ? n : st.Name)).ToList();
        StationItems.ItemsSource = stationEdits;
    }

    void ShowAllStations_Click(object sender, RoutedEventArgs e)
    {
        foreach (var x in stationEdits) x.Visible = true;
    }

    void ResetNames_Click(object sender, RoutedEventArgs e)
    {
        foreach (var x in stationEdits) x.DisplayName = x.OriginalName;
    }

    // ---------- OK / 適用 ----------
    void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!Collect()) return;
        DialogResult = true;
    }

    void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (!Collect()) return;
        var list = ApplyHandler?.Invoke(Result.Clone());   // 本体へ反映(ウィンドウは閉じない)
        if (list != null) BuildStationEdits(list);          // 局一覧を反映後の内容に更新
    }

    /// <summary>入力を検証して Result に書き戻す。エラーなら false。</summary>
    bool Collect()
    {
        ApplyCount();
        if (string.IsNullOrWhiteSpace(ExeBox.Text))
        { MessageBox.Show(this, "TVTest 実行ファイルを指定してください。", "設定"); return false; }
        if (string.IsNullOrWhiteSpace(EdcbBox.Text))
        { MessageBox.Show(this, "EDCB フォルダを指定してください。", "設定"); return false; }
        if (!double.TryParse(SizeBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var size) || size < 6 || size > 72)
        { MessageBox.Show(this, "サイズは 6〜72 の数値で指定してください。", "設定"); return false; }
        if (!double.TryParse(TunerWidthBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var tunerWidth) || tunerWidth < 60 || tunerWidth > 600)
        { MessageBox.Show(this, "チューナー欄の幅は 60〜600 の数値で指定してください。", "設定"); return false; }
        if (!double.TryParse(DetailWidthBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var detailWidth) || detailWidth < 200 || detailWidth > 1600)
        { MessageBox.Show(this, "番組内容の幅は 200〜1600 の数値で指定してください。", "設定"); return false; }
        bool useBackImage = BackImageCheck.IsChecked == true;
        string backImagePath = BackImageBox.Text.Trim();
        if (useBackImage)
        {
            if (backImagePath.Length == 0 || !File.Exists(backImagePath))
            { MessageBox.Show(this, "背景画像のファイルが見つかりません。画像ファイルを指定してください。", "設定"); return false; }
            try
            {
                var test = new System.Windows.Media.Imaging.BitmapImage();
                test.BeginInit();
                test.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                test.UriSource = new Uri(Path.GetFullPath(backImagePath));
                test.EndInit();
            }
            catch
            { MessageBox.Show(this, "背景画像を読み込めません。画像ファイル(png / jpg / bmp / gif など)を指定してください。", "設定"); return false; }
        }
        if (!double.TryParse(DetailDelayBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var detailDelay) || detailDelay < 0.5 || detailDelay > 5.0)
        { MessageBox.Show(this, "番組内容を表示するまでの時間は 0.5〜5.0(秒)の数値で指定してください。", "設定"); return false; }
        for (int k = 0; k < 3; k++)
        {
            var bad = colorEdits[k].FirstOrDefault(x => !x.IsValid);
            if (bad != null)
            { MessageBox.Show(this, $"色設定{k + 1}の「{bad.Label}」の色の指定が正しくありません。(例: #80004080)", "設定"); return false; }
        }
        var noDriver = tuners.FirstOrDefault(t => string.IsNullOrWhiteSpace(t.Driver));
        if (noDriver != null)
        { MessageBox.Show(this, $"チューナー {noDriver.No} の BonDriver を指定してください。", "設定"); return false; }
        foreach (var t in tuners)
            if (string.IsNullOrWhiteSpace(t.Label)) t.Label = $"チューナー {t.No}";

        Result.TrayResident = TrayCheck.IsChecked == true;
        Result.RunAtStartup = StartupCheck.IsChecked == true;
        Result.TvTestExe = ExeBox.Text.Trim();
        Result.EdcbDir = EdcbBox.Text.Trim();
        Result.TvTestArgs = TvTestArgsBox.Text.Trim();
        for (int i = 0; i < Settings.MaxButtons; i++)
        {
            var b = Result.Buttons[i];
            b.Exe = ButtonBoxes[i].Text.Trim();
            b.Args = ArgsBoxes[i].Text.Trim();
            b.Show = ShowChecks[i].IsChecked == true;
            b.IconMode = IconChecks1[i].IsChecked == true ? 1 : IconChecks2[i].IsChecked == true ? 2 : 0;
        }
        Result.Tuners = tuners.Select(t => new TunerDef { Label = t.Label.Trim(), Driver = t.Driver.Trim(), Kind = t.Kind }).ToList();
        Result.Theme = ThemeCombo.SelectedValue as string ?? "System";
        Result.ButtonStyle = ButtonStyleCombo.SelectedValue as string ?? "Vertical";
        Result.FontFamily = FontSourceName(FontCombo.Text);
        Result.FontSize = size;
        Result.FontWeight = (int)Math.Round(WeightSlider.Value / 100.0) * 100;
        Result.TunerListWidth = tunerWidth;
        for (int k = 0; k < 3; k++)
        {
            var set = Result.ColorSets[k];
            var e = colorEdits[k];
            set.TextColor = e[0].Hex.Trim();
            set.ListBackColor = e[1].Hex.Trim();
            set.HoverColor = e[2].Hex.Trim();
            set.SelectColor = e[3].Hex.Trim();
            set.WindowColor = e[4].Hex.Trim();
            set.DetailTextColor = e[5].Hex.Trim();
        }
        Result.ActiveColorSet = Math.Max(0, Array.FindIndex(activeChecks, x => x.IsChecked == true));
        Result.DetailWidth = detailWidth;
        Result.TunerAlign = AlignCenterCheck.IsChecked == true ? "Center" : AlignRightCheck.IsChecked == true ? "Right" : "Left";
        Result.UseBackImage = useBackImage;
        Result.BackImagePath = backImagePath;
        Result.ShowDetail = ShowDetailCheck.IsChecked == true;
        Result.DetailDelay = Math.Round(detailDelay, 1);

        // 局の表示名(元の名前と同じ/空なら対応表から外す)と、非表示の局
        foreach (var x in stationEdits)
        {
            var name = (x.DisplayName ?? "").Trim();
            if (name.Length == 0 || name == x.OriginalName) Result.StationNames.Remove(x.Key);
            else Result.StationNames[x.Key] = name;
        }
        var listed = stationEdits.Select(x => x.Key).ToHashSet();
        var hidden = state.Hidden.Where(k => !listed.Contains(k)).ToHashSet();   // 一覧にない局の設定は維持
        foreach (var x in stationEdits.Where(x => !x.Visible)) hidden.Add(x.Key);
        state.Hidden = hidden;
        return true;
    }
}
