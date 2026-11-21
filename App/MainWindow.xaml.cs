using System.Diagnostics;
using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MilookDeck.App;

public class Row : INotifyPropertyChanged
{
    string display = "", time = "", station = "", title = "";
    public string Display { get => display; set => Set(ref display, value); }   // チューナー行用
    public string Time { get => time; set => Set(ref time, value); }            // 番組一覧用
    public string Station { get => station; set => Set(ref station, value); }
    public string Title { get => title; set => Set(ref title, value); }
    public object? Tag { get; init; }
    public event PropertyChangedEventHandler? PropertyChanged;
    void Set(ref string f, string v, [CallerMemberName] string? n = null)
    {
        if (f == v) return;
        f = v;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}

public partial class MainWindow : Window
{
    readonly string baseDir = AppContext.BaseDirectory;
    Settings cfg;
    readonly AppState state;
    readonly string statePath;
    readonly List<TunerController> tuners = new();
    readonly ObservableCollection<Row> stationRows = new();
    readonly ObservableCollection<Row> tunerRows = new();
    List<Station> stations = new();
    EpgSnapshot epg = EpgSnapshot.Empty;
    DateTime epgStamp = DateTime.MinValue;
    bool epgLoading;
    TrayHost? tray;
    bool exiting;
    readonly DispatcherTimer statusTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    Station? lastStation;        // 本ソフトで最後に切り替えた局(ステータス枠に番組情報を常時表示)
    Station? scheduleStation;    // 局ごとの番組一覧(今から48時間先まで)を表示中ならその局。null なら通常の「今の番組」一覧
    double scheduleReturnOffset;   // 番組一覧に切り替える前の、局の一覧のスクロール位置(戻るときに元の位置へ戻す)
    DateTime scheduleExitedAt = DateTime.MinValue;   // 番組一覧から戻った時刻(直後のダブルクリックによる誤切替を防ぐ)
    bool messageShown;           // メッセージ表示中(5秒間)か
    string statusMessage = "";   // 5秒間表示するメッセージ
    NoticeOverlay? overlay;      // 全画面の TVTest の上に出す、カウントダウンの重ね表示
    string? countdownText;       // EDCB の録画予定による視聴終了のカウントダウン(表示中のみ)
    EdcbSnapshot edcbSnap = EdcbSnapshot.Empty;
    DateTime edcbFetched = DateTime.MinValue;
    bool edcbFetching;
    readonly Dictionary<TunerController, DateTime> closeSent = new();   // 録画予定で終了を要求した時刻(連続して要求しない)

    readonly bool firstRun;   // 初回起動(設定ファイルなし)

    string ProcName => Path.GetFileNameWithoutExtension(cfg.TvTestExe);

    public MainWindow()
    {
        InitializeComponent();
        cfg = Settings.Load(baseDir);
        firstRun = cfg.IsFirstRun || (string.IsNullOrWhiteSpace(cfg.TvTestExe) && string.IsNullOrWhiteSpace(cfg.EdcbDir));
        statePath = Path.Combine(baseDir, "MilookDeck.state.json");
        state = AppState.Load(statePath);
        ApplyBounds();
        ApplyAppearance();
        statusTimer.Tick += (_, _) => { statusTimer.Stop(); messageShown = false; UpdateProgramInfo(); };

        StationList.ItemsSource = stationRows;
        TunerList.ItemsSource = tunerRows;
        TunerList.SelectionChanged += (_, _) => BuildStationRows();   // 地デジ/BS・CS で一覧を切り替える
        BuildTuners();
        UpdateTray();
        Application.Current.SessionEnding += (_, _) => exiting = true;

        Dispatcher.BeginInvoke(new Action(async () =>
        {
            // 初回起動時(設定ファイルがない / TVTest・EDCB とも未設定)は、設定画面を自動的に表示する
            if (firstRun) OpenSettings();

            LoadStations();
            await TunerController.AttachExistingAsync(tuners, ProcName);
            await RefreshEpgAsync();
            RefreshTexts();

            // EPG の更新確認と一覧の表示更新は、時計の00秒に合わせて60秒ごと(番組が切り替わる時刻にすぐ新しい番組を出すため)
            var epgTimer = new DispatcherTimer();
            void ScheduleNextMinute()
            {
                var now = DateTime.Now;
                var ms = 60000 - (now.Second * 1000 + now.Millisecond) + 30;   // 次の00秒まで(+30ms: 00秒を確実に過ぎてから動かす)
                epgTimer.Interval = TimeSpan.FromMilliseconds(ms);
            }
            ScheduleNextMinute();
            epgTimer.Tick += async (_, _) => { ScheduleNextMinute(); await RefreshEpgAsync(); RefreshTexts(); };
            epgTimer.Start();

            // EDCB の録画予定の監視(視聴中の TVTest のカウントダウンと自動終了)
            var secTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            secTimer.Tick += async (_, _) => await EdcbTickAsync();
            secTimer.Start();
        }));
    }

    /// <summary>「タスクトレイに常駐する」が有効なら、ウィンドウを出さずに起動する。</summary>
    public bool StartInTray => cfg.TrayResident;

    // ---------- 設定の反映 ----------
    static SolidColorBrush Br(string hex, string fallback)
    {
        try { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
        catch { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallback)); }
    }

    /// <summary>本体の背景。画像を使う設定でファイルが読めるときは、ウィンドウの大きさに合わせて拡大・縮小した画像。そうでなければ「ソフト本体の色」。</summary>
    Brush WindowBackground()
    {
        if (cfg.UseBackImage && !string.IsNullOrWhiteSpace(cfg.BackImagePath))
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;                    // 読み込んだらファイルを手放す(画像を差し替えられるように)
                bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bmp.UriSource = new Uri(Path.GetFullPath(cfg.BackImagePath.Trim()));
                bmp.EndInit();
                bmp.Freeze();
                var brush = new ImageBrush(bmp) { Stretch = Stretch.Fill };    // ウィンドウいっぱいに拡大・縮小
                brush.Freeze();
                return brush;
            }
            catch { /* 読めないときは色にする */ }
        }
        return Br(cfg.Colors.WindowColor, "#FF3B3B3B");
    }

    static void ApplyTheme(string theme)
    {
        // Fluent テーマ(.NET 9 以降)。System=Windows のライト/ダーク設定に追従
        Application.Current.ThemeMode = theme switch
        {
            "Light" => ThemeMode.Light,
            "Dark" => ThemeMode.Dark,
            _ => ThemeMode.System
        };
    }

    /// <summary>
    /// ボタンのレイアウト切替。
    /// 縦=左にタイトル画像(縦書き)・右にボタンを縦並び、横=上部にタイトル画像とボタンを横並び、
    /// ミックス=上部にタイトル画像(横書き)・右にボタンを縦並び。
    /// </summary>
    void ApplyLayout()
    {
        bool mix = cfg.ButtonStyle == "Mix";
        bool horizontal = cfg.ButtonStyle == "Horizontal";
        bool v = !horizontal;               // ボタンを右に縦並びにする(縦・ミックス)
        bool titleLeft = !horizontal && !mix;   // タイトル画像を左の帯に縦書きで置く(縦のみ)
        LeftCol.Width = new GridLength(titleLeft ? 58 : mix ? 12 : 0);
        RightCol.Width = new GridLength(v ? 56 : 0);
        DragLeft.Visibility = titleLeft ? Visibility.Visible : Visibility.Collapsed;

        // ソフト名(画像)
        Grid.SetRow(TitleImage, 0);
        Grid.SetColumn(TitleImage, 0);
        if (titleLeft)
        {
            Grid.SetRowSpan(TitleImage, 2);
            Grid.SetColumnSpan(TitleImage, 1);
            var img = new BitmapImage(new Uri("pack://application:,,,/TitleVertical.png"));
            TitleImage.Source = img;
            TitleImage.Width = img.PixelWidth;     // 画像の大きさのまま表示
            TitleImage.Height = img.PixelHeight;
            TitleImage.HorizontalAlignment = HorizontalAlignment.Center;   // 左の帯(幅58)の中央
            TitleImage.VerticalAlignment = VerticalAlignment.Top;
            TitleImage.Margin = new Thickness(0, 13, 0, 0);
        }
        else
        {
            Grid.SetRowSpan(TitleImage, 1);
            Grid.SetColumnSpan(TitleImage, 3);
            var img = new BitmapImage(new Uri("pack://application:,,,/TitleHorizontal.png"));
            TitleImage.Source = img;
            TitleImage.Width = img.PixelWidth;
            TitleImage.Height = img.PixelHeight;
            TitleImage.HorizontalAlignment = HorizontalAlignment.Left;
            TitleImage.VerticalAlignment = VerticalAlignment.Top;
            TitleImage.Margin = new Thickness(17, 9, 0, 0);
        }

        // 起動ボタン(ボタン1〜5)。縦=上からボタン1,2…、横=右からボタン1,2…(StackPanel は左から並ぶので、横では逆順に入れる)
        var order = v ? AppButtons : Enumerable.Reverse(AppButtons).ToArray();
        AppBtns.Children.Clear();
        foreach (var btn in order) AppBtns.Children.Add(btn);
        if (v)
        {
            AppBtns.Orientation = Orientation.Vertical;
            Grid.SetRow(AppBtns, 1);
            Grid.SetRowSpan(AppBtns, 2);   // ステータス枠の高さぶんも使って、5個並べても収まりやすくする
            Grid.SetColumn(AppBtns, 2);
            Grid.SetColumnSpan(AppBtns, 1);
            AppBtns.HorizontalAlignment = HorizontalAlignment.Center;
            AppBtns.VerticalAlignment = VerticalAlignment.Top;
            AppBtns.Margin = new Thickness(0, 6, 0, 0);
            foreach (var btn in AppButtons) btn.Margin = new Thickness(0, 0, 0, 8);
        }
        else
        {
            AppBtns.Orientation = Orientation.Horizontal;
            Grid.SetRow(AppBtns, 0);
            Grid.SetRowSpan(AppBtns, 1);
            Grid.SetColumn(AppBtns, 0);
            Grid.SetColumnSpan(AppBtns, 3);
            AppBtns.HorizontalAlignment = HorizontalAlignment.Right;
            AppBtns.VerticalAlignment = VerticalAlignment.Stretch;
            AppBtns.Margin = new Thickness(0, 0, 72, 0);   // 「–」「X」(合計72)の左に並べる
            foreach (var btn in AppButtons) btn.Margin = new Thickness(0, 0, 6, 0);
        }

        // 番組一覧・チューナー欄・ステータス枠
        if (v)
        {
            Grid.SetColumn(ContentGrid, 1);
            Grid.SetColumnSpan(ContentGrid, 1);
            ContentGrid.Margin = new Thickness(0);
            Grid.SetColumn(StatusBorder, 1);
            Grid.SetColumnSpan(StatusBorder, 1);
            StatusBorder.Margin = new Thickness(0, 10, 0, 0);
        }
        else
        {
            Grid.SetColumn(ContentGrid, 0);
            Grid.SetColumnSpan(ContentGrid, 3);
            ContentGrid.Margin = new Thickness(12, 0, 12, 0);
            Grid.SetColumn(StatusBorder, 0);
            Grid.SetColumnSpan(StatusBorder, 3);
            StatusBorder.Margin = new Thickness(12, 10, 12, 0);
        }
    }

    void ApplyAppearance()
    {
        ApplyLayout();
        ApplyTheme(cfg.Theme);   // Fluent テーマ(Windows に合わせる/ライト/ダーク)
        Resources["WindowBrush"] = WindowBackground();   // ソフト本体の色(画像を使う設定のときは画像)
        Resources["TextBrush"] = Br(cfg.Colors.TextColor, "#FF00AAAA");
        Resources["ListBackBrush"] = Br(cfg.Colors.ListBackColor, "#FF000000");
        Resources["HoverBrush"] = Br(cfg.Colors.HoverColor, "#3BAAAAAA");
        Resources["SelectBrush"] = Br(cfg.Colors.SelectColor, "#80004080");
        Resources["DetailDelayMs"] = (int)Math.Round(cfg.DetailDelay * 1000);   // 番組内容を表示するまでの時間
        Resources["DetailEnabled"] = cfg.ShowDetail;                            // 番組内容を表示する/しない
        Resources["TunerAlign"] = cfg.TunerAlign switch                         // チューナー欄の文字の位置
        {
            "Center" => TextAlignment.Center,
            "Right" => TextAlignment.Right,
            _ => TextAlignment.Left,
        };
        FontFamily ff;
        try { ff = new FontFamily(cfg.FontFamily); } catch { ff = new FontFamily("Yu Gothic UI"); }
        StationList.FontFamily = ff;
        StationList.FontSize = cfg.FontSize;
        TunerList.FontFamily = ff;
        TunerList.FontSize = cfg.FontSize + 2;   // チューナー名は番組一覧より少し大きく
        var weight = FontWeight.FromOpenTypeWeight(Math.Clamp(cfg.FontWeight, 1, 999));
        StationList.FontWeight = weight;
        TunerList.FontWeight = weight;
        // ステータス枠: 番組一覧と同じフォント。高さはフォントサイズに連動(1行ぶん+余白)
        TextElement.SetFontFamily(StatusBorder, ff);
        TextElement.SetFontSize(StatusBorder, cfg.FontSize);
        TextElement.SetFontWeight(StatusBorder, weight);
        StatusBorder.MinHeight = Math.Ceiling(ff.LineSpacing * cfg.FontSize) + 4;
        TunerCol.Width = new GridLength(Math.Clamp(cfg.TunerListWidth, 60, 600));
        IconLoader.Clear();   // アイコンは、設定を適用するたびに取り出し直す
        for (int i = 0; i < AppButtons.Length && i < cfg.Buttons.Count; i++)
        {
            var def = cfg.Buttons[i];
            AppButtons[i].Visibility = def.Show ? Visibility.Visible : Visibility.Collapsed;
            AppButtons[i].ToolTip = ButtonTip(def.Exe, i + 1);
            SetButtonIcon(i, def);
        }
        CloseTunerMenu.Header = $"{ProcName} を終了";   // 設定した実行ファイル名(.exe なし)
    }

    void BuildTuners()
    {
        var old = tuners.ToList();
        tuners.Clear();
        tunerRows.Clear();
        foreach (var d in cfg.Tuners)
        {
            var t = new TunerController(d, cfg);
            // 設定変更前から起動中の TVTest は引き継ぐ
            var prev = old.FirstOrDefault(o => o.IsRunning && string.Equals(o.Def.Driver, d.Driver, StringComparison.OrdinalIgnoreCase));
            if (prev != null) t.Proc = prev.Proc;
            tuners.Add(t);
            tunerRows.Add(new Row { Tag = t, Display = d.Label });
        }
        if (tunerRows.Count > 0) TunerList.SelectedIndex = 0;
    }

    void LoadStations()
    {
        try { stations = Ch2.LoadAll(cfg); }
        catch (Exception ex) { stations = new(); SetStatus($".ch2 の読込に失敗: {ex.Message}"); }
        BuildStationRows();
    }

    void UpdateTray()
    {
        if (cfg.TrayResident) tray ??= new TrayHost(ToggleFromTray, OpenSettingsFromTray, ExitApp);
        else { tray?.Dispose(); tray = null; }
    }

    /// <summary>トレイアイコン左クリック: 本体が表示されていれば収納、表示されていなければ表示。</summary>
    void ToggleFromTray()
    {
        if (IsVisible && WindowState != WindowState.Minimized) { SaveBounds(); Hide(); }
        else ShowFromTray();
    }

    void OpenSettingsFromTray() => OpenSettings();

    /// <summary>二重起動された(2つ目の起動が要求した)ときに、起動中の本体を前面に出す。</summary>
    public void BringToFront() => ShowFromTray();

    void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    void ExitApp()
    {
        exiting = true;
        overlay?.Close();
        Close();
    }

    bool settingsOpen;

    void OpenSettings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    void OpenSettings()
    {
        if (settingsOpen) return;
        settingsOpen = true;
        try
        {
            var w = new SettingsWindow(cfg.Clone(), state, stations);
            if (IsVisible) w.Owner = this;
            else if (w.WindowStartupLocation == WindowStartupLocation.CenterOwner)
                w.WindowStartupLocation = WindowStartupLocation.CenterScreen;   // 本体が収納中(トレイから)の場合
            w.ApplyHandler = ApplySettings;                  // 「適用」ボタン
            var ok = w.ShowDialog() == true;
            state.Save(statePath);   // 設定画面の位置・サイズ(キャンセル時も)を保存
            if (ok) ApplySettings(w.Result);
        }
        finally { settingsOpen = false; }
    }

    /// <summary>設定を本体へ反映する。反映後の局一覧を返す。</summary>
    List<Station> ApplySettings(Settings s)
    {
        cfg = s;
        cfg.Save(baseDir);
        StartupRegistration.Set(s.RunAtStartup);   // スタートアップへの登録/解除(スタートアップフォルダのショートカット。「OK」「適用」で反映)
        state.Save(statePath);   // 非表示の局
        ApplyAppearance();
        BuildTuners();
        LoadStations();
        epg = EpgSnapshot.Empty;
        epgStamp = DateTime.MinValue;
        UpdateTray();
        _ = RefreshAfterApplyAsync();
        return stations;
    }

    async Task RefreshAfterApplyAsync()
    {
        await TunerController.AttachExistingAsync(tuners, ProcName);
        await RefreshEpgAsync();
        RefreshTexts();
        SetStatus("設定を反映しました");
    }

    // ---------- ウィンドウの位置・サイズ ----------
    void ApplyBounds()
    {
        if (state.WinLeft is not double l || state.WinTop is not double t ||
            state.WinWidth is not double w || state.WinHeight is not double h) return;
        var r = new Rect(l, t, w, h);
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                              SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var inter = Rect.Intersect(r, screen);
        if (inter.IsEmpty || inter.Width < 100 || inter.Height < 50) return;   // 画面外なら無視
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = l; Top = t;
        Width = Math.Max(w, MinWidth); Height = Math.Max(h, MinHeight);
    }

    void SaveBounds()
    {
        var b = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        state.WinLeft = b.Left; state.WinTop = b.Top; state.WinWidth = b.Width; state.WinHeight = b.Height;
        state.Save(statePath);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        SaveBounds();

        if (cfg.TrayResident && !exiting)   // 閉じるボタン → タスクトレイに常駐
        {
            e.Cancel = true;
            Hide();
            return;
        }
        tray?.Dispose();
        tray = null;
        base.OnClosing(e);
    }

    // ---------- 表示 ----------
    TunerController? SelectedTuner => (TunerList.SelectedItem as Row)?.Tag as TunerController;

    /// <summary>選択中のチューナーの種別(地デジ/BS・CS)の局だけを並べる。</summary>
    void BuildStationRows()
    {
        scheduleStation = null;   // 通常の一覧に戻す
        stationRows.Clear();
        var t = SelectedTuner;
        if (t == null) return;
        foreach (var s in stations.Where(s => s.Satellite == t.Def.Satellite && !state.Hidden.Contains(s.Key)))
            stationRows.Add(new Row { Tag = s, Station = DisplayName(s) });
        RefreshTexts();
    }

    async Task RefreshEpgAsync()
    {
        if (epgLoading || !Directory.Exists(cfg.EpgDataFolder)) return;
        try
        {
            var stamp = EpgReader.LatestWrite(cfg.EpgDataFolder);
            if (stamp <= epgStamp) return;
            epgLoading = true;
            var prev = epg;
            var dir = cfg.EpgDataFolder;
            epg = await Task.Run(() => EpgReader.Load(dir, DateTime.Now, prev));
            epgStamp = stamp;
        }
        catch (Exception ex) { SetStatus($"EPG 読込失敗: {ex.Message}"); }
        finally { epgLoading = false; }
    }

    void RefreshTexts()
    {
        var now = DateTime.Now;
        if (scheduleStation != null) RebuildSchedule(false);
        else
        foreach (var r in stationRows)
        {
            var s = (Station)r.Tag!;
            var e = epg.Current(s, now);
            r.Time = e == null ? "--:--" : $"{e.Start:HH:mm}-{e.End:HH:mm}";
            r.Title = e?.Name ?? "";
        }
        foreach (var r in tunerRows)
        {
            var t = (TunerController)r.Tag!;
            r.Display = (t.IsRunning ? "● " : "　") + t.Def.Label;
        }
        UpdateProgramInfo();
    }

    /// <summary>ステータス枠のメッセージ。5秒で消え、消えた後は番組情報の表示に戻る。</summary>
    void SetStatus(string msg)
    {
        statusTimer.Stop();
        messageShown = msg.Length > 0;
        statusMessage = msg;
        if (messageShown) statusTimer.Start();
        UpdateProgramInfo();
    }

    /// <summary>ステータス枠に、本ソフトで切り替えた局の現在の番組(時間・局名・番組名)を表示する。</summary>
    void UpdateProgramInfo()
    {
        // 優先順: ① 5秒間のメッセージ ② 録画予定のカウントダウン ③ 切り替えた局の番組情報
        string? text = messageShown ? statusMessage : countdownText;
        Status.Visibility = text != null ? Visibility.Visible : Visibility.Collapsed;
        var info = text != null ? Visibility.Collapsed : Visibility.Visible;
        StatusTime.Visibility = StatusStation.Visibility = StatusTitle.Visibility = info;
        if (text != null) { Status.Text = text; return; }

        if (lastStation == null) { StatusTime.Text = StatusStation.Text = StatusTitle.Text = ""; return; }
        var e = epg.Current(lastStation, DateTime.Now);
        StatusTime.Text = e == null ? "--:--" : $"{e.Start:HH:mm}-{e.End:HH:mm}";
        StatusStation.Text = DisplayName(lastStation);
        StatusTitle.Text = e?.Name ?? "";
    }

    // ---------- EDCB との連携 ----------
    async Task<EdcbSnapshot> FetchEdcbAsync()
    {
        edcbFetching = true;
        try
        {
            var dir = cfg.EdcbDir;
            var snap = await Task.Run(() => Edcb.Fetch(dir));
            edcbSnap = snap;
            edcbFetched = DateTime.Now;
            return snap;
        }
        finally { edcbFetching = false; }
    }

    /// <summary>EDCB が使用中、または3分以内に使用予定の BonDriver なら、TVTest を起動させない。</summary>
    async Task<bool> BlockedByEdcbAsync(TunerController t)
    {
        var snap = await FetchEdcbAsync();   // 起動の直前なので最新の状態を取得
        switch (Edcb.CheckLaunch(snap, t.Def.Driver, DateTime.Now))
        {
            case LaunchBlock.Recording: SetStatus("EDCBにて録画中のため起動できません"); return true;
            case LaunchBlock.Reserved: SetStatus("EDCBにて録画予定のため起動できません"); return true;
        }
        return false;
    }

    /// <summary>毎秒: 視聴中の TVTest に録画予定が近づいていれば、カウントダウンを表示し、3分前に終了する。</summary>
    async Task EdcbTickAsync()
    {
        var running = tuners.Where(t => t.IsRunning).ToList();
        if (running.Count == 0)
        {
            if (countdownText != null) { countdownText = null; UpdateProgramInfo(); }
            overlay?.HideNotice();
            return;
        }
        if (!edcbFetching && (DateTime.Now - edcbFetched).TotalSeconds >= 10) await FetchEdcbAsync();

        var now = DateTime.Now;
        var counting = new List<(TunerController t, int sec)>();
        foreach (var t in running)
        {
            var r = Edcb.NextReserve(edcbSnap, t.Def.Driver, now);
            if (r == null) continue;
            var remain = r.EffStart - now;
            if (remain <= Edcb.CloseBefore)
            {
                if (!closeSent.TryGetValue(t, out var sent) || (now - sent).TotalSeconds > 20)
                {
                    closeSent[t] = now;
                    t.Close();
                    SetStatus($"{t.Def.Label}: EDCBにて録画予定のため {ProcName} を終了しました");
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
                    timer.Tick += (_, _) => { timer.Stop(); RefreshTexts(); };
                    timer.Start();
                }
            }
            else if (remain <= Edcb.CountdownStart)
            {
                counting.Add((t, (int)Math.Ceiling((remain - Edcb.CloseBefore).TotalSeconds)));
            }
        }

        string? text = null;
        if (counting.Count > 0)
        {
            var first = counting.OrderBy(x => x.sec).First();
            var prefix = counting.Count > 1 ? $"{first.t.Def.Label}: " : "";   // 複数のチューナーが対象のときは、最も早いものを表示
            text = $"{prefix}EDCBにて録画予定のため {first.sec} 秒後に視聴を終了します";
        }
        UpdateFullscreenNotice(counting);
        if (text != countdownText) { countdownText = text; UpdateProgramInfo(); }
    }

    /// <summary>
    /// カウントダウン中のチューナーの TVTest が全画面なら、その画面の上の中央に、同じ文面を重ねて表示する。
    /// 全画面でなければ(ウィンドウ表示なら)、これまでどおり本体の表示だけ。
    /// </summary>
    void UpdateFullscreenNotice(List<(TunerController t, int sec)> counting)
    {
        foreach (var (t, sec) in counting.OrderBy(x => x.sec))
        {
            if (t.Proc == null || !FullscreenProbe.TryGetMonitor(t.Proc.Id, out var monitor)) continue;
            var prefix = counting.Count > 1 ? $"{t.Def.Label}: " : "";
            overlay ??= new NoticeOverlay(StationList.FontFamily, FontWeights.Bold);
            overlay.ShowNotice($"{prefix}EDCBにて録画予定のため {sec} 秒後に視聴を終了します", monitor);
            return;
        }
        overlay?.HideNotice();
    }

    // ---------- 操作 ----------
    async void Tuner_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        var t = (TunerController)((Row)((ListBoxItem)sender).DataContext).Tag!;
        if (t.IsRunning) { CloseTuner(t); return; }   // 起動済み → もう一度ダブルクリックで終了
        if (await BlockedByEdcbAsync(t)) return;      // EDCB が使用中/使用予定なら起動しない

        try { t.Launch(); SetStatus($"{t.Def.Label}: {ProcName} を起動しました"); }   // 初期チャンネルは TVTest の設定に従う
        catch (Exception ex) { SetStatus($"起動失敗: {ex.Message}"); }
        RefreshTexts();
    }

    void CloseTuner(TunerController t)
    {
        t.Close();
        SetStatus($"{t.Def.Label}: {ProcName} を終了します");
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        timer.Tick += (_, _) => { timer.Stop(); RefreshTexts(); };
        timer.Start();
    }

    /// <summary>番組内容のポップアップ: 表示する直前に、その局の現在の番組の内容を組み立てる。番組情報がなければ表示しない。</summary>
    void Station_ToolTipOpening(object sender, ToolTipEventArgs e)
    {
        if (sender is not ListBoxItem item || item.DataContext is not Row row || item.ToolTip is not ToolTip tt)
        {
            e.Handled = true;
            return;
        }
        EpgEvent? ev = row.Tag switch
        {
            EpgEvent x => x,                                       // 局ごとの番組一覧: その行の番組
            Station st => epg.Current(st, DateTime.Now),            // 通常の一覧: 現在放送中の番組
            _ => null
        };
        if (ev == null) { e.Handled = true; return; }

        var work = SystemParameters.WorkArea;
        // 幅は設定値(画面の幅を超えない範囲)。ToolTip 自体の MaxWidth を指定しないと、テーマ(Fluent)の既定の幅(320)で折り返される
        double width = Math.Min(Math.Max(200, cfg.DetailWidth), Math.Max(300, work.Width - 40));
        tt.Background = (Brush)FindResource("ListBackBrush");
        tt.Foreground = Br(cfg.Colors.DetailTextColor, "#FF00AAAA");
        tt.FontFamily = StationList.FontFamily;
        tt.FontSize = StationList.FontSize;
        tt.FontWeight = StationList.FontWeight;
        tt.MaxWidth = width;
        tt.MaxHeight = Math.Max(200, work.Height - 20);
        tt.Content = new TextBlock
        {
            Text = EpgDetail.Build(ev),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = Math.Max(100, width - 16)   // 余白(Padding 6 + 枠 1)の分を引く
        };
    }

    // ---------- 局ごとの番組一覧(今から48時間先まで) ----------
    /// <summary>右クリックした局の、現在から48時間先までの番組一覧に切り替える。</summary>
    void ShowSchedule(Station st)
    {
        scheduleReturnOffset = StationScroller()?.VerticalOffset ?? 0;   // 戻るときのために、今のスクロール位置を控える
        scheduleStation = st;
        RebuildSchedule(true);
        SetStationScroll(0);   // 番組一覧は先頭(いまの番組)から表示する
    }

    /// <summary>局の一覧のスクロール部分(ScrollViewer)を返す。</summary>
    ScrollViewer? StationScroller()
    {
        static ScrollViewer? Find(DependencyObject d)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            {
                var c = VisualTreeHelper.GetChild(d, i);
                if (c is ScrollViewer sv) return sv;
                var r = Find(c);
                if (r != null) return r;
            }
            return null;
        }
        return Find(StationList);
    }

    /// <summary>局の一覧を指定の位置までスクロールする。一覧の作り直し直後は表示が追いついていないので、配置を確定してから動かし、念のため少し後にもう一度合わせる。</summary>
    void SetStationScroll(double offset)
    {
        void Apply() => StationScroller()?.ScrollToVerticalOffset(offset);
        StationList.UpdateLayout();
        Apply();
        Dispatcher.BeginInvoke(new Action(Apply), DispatcherPriority.Loaded);
    }

    void RebuildSchedule(bool force)
    {
        if (scheduleStation == null) return;
        var now = DateTime.Now;
        var list = epg.Between(scheduleStation, now, now.AddHours(48));
        // 内容が変わらなければ作り直さない(スクロール位置を保つ)
        if (!force && stationRows.Count == list.Count &&
            stationRows.Zip(list).All(p => p.First.Tag is EpgEvent a && a.Eid == p.Second.Eid && a.Start == p.Second.Start))
            return;
        stationRows.Clear();
        if (list.Count == 0)   // 番組情報がなくても、左クリックで戻れるように1行出す
        {
            stationRows.Add(new Row { Station = DisplayName(scheduleStation), Title = "番組情報がありません" });
            return;
        }
        foreach (var e in list)
            stationRows.Add(new Row
            {
                Tag = e,
                Time = $"{e.Start:HH:mm}-{e.End:HH:mm}",
                Station = DisplayName(scheduleStation),
                Title = e.Name
            });
    }

    /// <summary>左クリック: 番組一覧の表示中なら、元の「今の番組」一覧に戻る。</summary>
    void Station_LeftClick(object sender, MouseButtonEventArgs e)
    {
        if (scheduleStation == null) return;
        scheduleExitedAt = DateTime.Now;
        BuildStationRows();
        SetStationScroll(scheduleReturnOffset);   // 切り替える前のスクロール位置に戻す
    }

    /// <summary>右クリック: 通常の一覧ではその局の番組一覧に切り替え、番組一覧では「iEPG関連付け録画予約」メニューを出す。</summary>
    void Station_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBoxItem item || item.DataContext is not Row row) return;
        e.Handled = true;
        if (scheduleStation == null)
        {
            if (row.Tag is Station st) ShowSchedule(st);
            return;
        }
        if (row.Tag is not EpgEvent ev) return;
        var menu = new ContextMenu();
        var mi = new MenuItem { Header = "iEPG関連付け録画予約" };
        mi.Click += async (_, _) => await ReserveByIepgAsync(ev);
        menu.Items.Add(mi);
        menu.PlacementTarget = item;
        menu.IsOpen = true;
    }

    /// <summary>
    /// iEPG(.tvpid)ファイルを作って EpgTimer.exe に渡し、その番組を録画予約する(TVTest の「iEPG関連付け実行」と同じ方式)。
    /// EpgTimer は、予約が成功したときはメッセージを出さずに予約を登録する。
    /// </summary>
    async Task ReserveByIepgAsync(EpgEvent ev)
    {
        var exe = Path.Combine(cfg.EdcbDir, "EpgTimer.exe");
        if (!File.Exists(exe)) { SetStatus("EpgTimer.exe が見つかりません(EDCB フォルダを確認してください)"); return; }
        var station = Iepg.StationCode(ev.Nid, ev.Sid);
        if (station == null) { SetStatus("この放送(局)は iEPG での予約に対応していません"); return; }

        var dir = cfg.EdcbDir;
        var stationName = scheduleStation != null ? DisplayName(scheduleStation) : "";   // 結果の表示用(待っている間に一覧が切り替わってもよいように、先に控える)
        bool Reserved(Dictionary<uint, EdcbReserve> m) =>
            m.Values.Any(r => r.Onid == ev.Nid && r.Tsid == ev.Tsid && r.Sid == ev.Sid && r.Eid == ev.Eid);

        // すでに予約済みなら、何もしない
        if (Reserved(await Task.Run(() => Edcb.ReadReserveTxt(dir))))
        {
            SetStatus($"「{ev.Name}」はすでに録画予約されています");
            return;
        }

        string file;
        try { file = Iepg.WriteTempFile(ev, station); }
        catch (Exception ex) { SetStatus($"iEPG ファイルを作れませんでした: {ex.Message}"); return; }

        Process? p;
        try { p = Process.Start(new ProcessStartInfo(exe, $"\"{file}\"") { WorkingDirectory = dir, UseShellExecute = false }); }
        catch (Exception ex) { SetStatus($"起動失敗: {ex.Message}"); return; }
        SetStatus($"{Path.GetFileNameWithoutExtension(exe)} に録画予約を依頼しました…");

        // EpgTimer が登録して終了する(または起動中の EpgTimer なら二重起動として終了する)のを待つ
        if (p != null) await Task.Run(() => p.WaitForExit(10000));
        try { File.Delete(file); } catch { }

        // 予約が登録されたか、Reserve.txt で確認する(EpgTimerSrv の保存を少し待つ)
        for (int i = 0; i < 8; i++)
        {
            if (Reserved(await Task.Run(() => Edcb.ReadReserveTxt(dir))))
            {
                SetStatus($"録画予約しました: {stationName} {ev.Name}");
                return;
            }
            await Task.Delay(1000);
        }
        SetStatus("録画予約を確認できませんでした(EpgTimer のメッセージを確認してください)");
    }

    async void Station_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (scheduleStation != null || (DateTime.Now - scheduleExitedAt).TotalMilliseconds < 600) return;   // 番組一覧の表示中・戻った直後は切り替えない
        if (((Row)((ListBoxItem)sender).DataContext).Tag is not Station st) return;
        var t = SelectedTuner;
        if (t == null) return;
        if (!t.IsRunning)
        {
            if (await BlockedByEdcbAsync(t)) return;   // EDCB が使用中/使用予定なら起動しない
            lastStation = st;   // ステータス枠に表示する局
            try { t.Launch(); }   // 局は指定せずに起動し、起動後にプラグインで切り替える
            catch (Exception ex) { SetStatus($"起動失敗: {ex.Message}"); RefreshTexts(); return; }
            SetStatus($"{t.Def.Label}: {DisplayName(st)} で起動");
            RefreshTexts();
            var err = await t.SwitchWhenReadyAsync(st, TimeSpan.FromSeconds(30));
            if (err != null) SetStatus($"切替失敗: {err}");
        }
        else
        {
            lastStation = st;
            var err = await t.SwitchAsync(st);
            SetStatus(err == null ? $"{t.Def.Label}: {DisplayName(st)} に切り替え" : $"切替失敗: {err}");
        }
        RefreshTexts();
    }

    void CloseTuner_Click(object sender, RoutedEventArgs e)
    {
        if (TunerList.SelectedItem is not Row row) return;
        CloseTuner((TunerController)row.Tag!);
    }

    /// <summary>表示上の局名(変更があればそれ、なければ元の局名)。内部の局名・EPG との対応は変わらない。</summary>
    string DisplayName(Station s) =>
        cfg.StationNames.TryGetValue(s.Key, out var n) && !string.IsNullOrWhiteSpace(n) ? n : s.Name;

    // 右クリックした行を選択状態にしてからメニューを出す
    void List_RightDown(object sender, MouseButtonEventArgs e)
    {
        DependencyObject? d = e.OriginalSource as DependencyObject;
        while (d != null)
        {
            if (d is ListBoxItem item) { item.IsSelected = true; return; }
            d = d is Visual || d is System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
    }

    // ---------- 起動ボタン(ボタン1・ボタン2) ----------
    static string ButtonTip(string exe, int n) =>
        string.IsNullOrWhiteSpace(exe) ? $"ボタン{n}(未設定)" : $"{Path.GetFileNameWithoutExtension(exe)} を起動";

    readonly double[] iconBaseOpacity = new double[5];   // 起動ボタンのアイコンの、通常時の不透明度(マウスを重ねると1)

    /// <summary>起動ボタンに、実行ファイルのアイコンを入れる(設定の「アイコンを表示する」のパターンに従う)。</summary>
    void SetButtonIcon(int i, LaunchButton def)
    {
        var btn = AppButtons[i];
        ImageSource? src = def.IconMode is 1 or 2 ? IconLoader.Load(def.Exe, 64) : null;
        if (src == null) { btn.Content = null; return; }   // アイコンなし(空のボタン)
        iconBaseOpacity[i] = def.IconMode == 1 ? 0.5 : 0.0;
        var img = new Image
        {
            Source = src, Width = 26, Height = 26, Stretch = Stretch.Uniform, IsHitTestVisible = false,
            Opacity = btn.IsMouseOver ? 1.0 : iconBaseOpacity[i],
        };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        btn.Content = img;
    }

    void AppBtn_MouseEnter(object sender, MouseEventArgs e)
    {
        if (sender is Button { Content: Image img }) img.Opacity = 1.0;
    }

    void AppBtn_MouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Button { Content: Image img, Tag: string t } && int.TryParse(t, out var i) && i >= 0 && i < iconBaseOpacity.Length)
            img.Opacity = iconBaseOpacity[i];
    }

    Button[] AppButtons => new[] { AppBtn1, AppBtn2, AppBtn3, AppBtn4, AppBtn5 };

    void AppBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && int.TryParse(b.Tag as string, out var i) && i >= 0 && i < cfg.Buttons.Count)
            LaunchApp(cfg.Buttons[i].Exe, cfg.Buttons[i].Args, i + 1);
    }

    void LaunchApp(string exe, string args, int n)
    {
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
        {
            SetStatus($"ボタン{n}: 実行ファイルが見つかりません(設定で指定してください)");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(exe) { Arguments = args ?? "", WorkingDirectory = Path.GetDirectoryName(exe)!, UseShellExecute = false });
            SetStatus($"{Path.GetFileNameWithoutExtension(exe)} を起動しました");
        }
        catch (Exception ex) { SetStatus($"起動失敗: {ex.Message}"); }
    }

    // ---------- ウィンドウ ----------
    void Splitter_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        cfg.TunerListWidth = Math.Round(TunerCol.ActualWidth);   // ドラッグした幅を保存
        cfg.Save(baseDir);
    }

    void TitleBar_Drag(object sender, MouseButtonEventArgs e) { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); }
    void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
