using System.Text.Json;
using System.Text.Json.Serialization;
namespace MilookDeck;

/// <summary>色設定1つぶん(既定値は共通)。</summary>
public class ColorSet
{
    public string TextColor { get; set; } = "#FF00AAAA";          // 文字色
    public string ListBackColor { get; set; } = "#FF000000";      // 一覧の背景色
    public string HoverColor { get; set; } = "#3BAAAAAA";         // マウスオーバー時の色
    public string SelectColor { get; set; } = "#80004080";        // 項目選択時の色
    public string WindowColor { get; set; } = "#FF3B3B3B";        // ソフト本体の色
    public string DetailTextColor { get; set; } = "#FFDDDDDD";    // 番組内容(ホバー表示)の文字色
}

/// <summary>起動ボタン1個ぶんの設定。</summary>
public class LaunchButton
{
    public string Exe { get; set; } = "";
    public string Args { get; set; } = "";   // 起動オプション
    public bool Show { get; set; }           // ボタンを表示するか
}

/// <summary>設定(exe と同じフォルダの MilookDeck.settings.json)。</summary>
public class Settings
{
    public string TvTestExe { get; set; } = "";
    public string TvTestArgs { get; set; } = "";   // TVTest 起動時に追加で渡すコマンドラインオプション
    public string EdcbDir { get; set; } = "";   // EDCB フォルダ(EPG データと Reserve.txt は、この中の Setting にある)
    [JsonIgnore] public string EpgDataFolder => string.IsNullOrWhiteSpace(EdcbDir) ? "" : Path.Combine(EdcbDir, "Setting", "EpgData");

    // 起動ボタン(最大5個。ボタン1〜5)。任意のソフトを起動できる
    public const int MaxButtons = 5;
    public List<LaunchButton> Buttons { get; set; } = DefaultButtons();

    static List<LaunchButton> DefaultButtons()
    {
        var l = new List<LaunchButton>();
        for (int i = 0; i < MaxButtons; i++) l.Add(new LaunchButton());
        return l;
    }

    /// <summary>ボタンの数をちょうど5個にそろえる(古い設定ファイル・手編集の対策)。</summary>
    public void Normalize()
    {
        if (TunerAlign != "Center" && TunerAlign != "Right") TunerAlign = "Left";
        DetailDelay = double.IsFinite(DetailDelay) ? Math.Round(Math.Clamp(DetailDelay, 0.5, 5.0), 1) : 1.0;
        ColorSets ??= new();
        while (ColorSets.Count < 3) ColorSets.Add(new ColorSet());
        if (ColorSets.Count > 3) ColorSets.RemoveRange(3, ColorSets.Count - 3);
        ActiveColorSet = Math.Clamp(ActiveColorSet, 0, 2);
        Buttons ??= new();
        while (Buttons.Count < MaxButtons) Buttons.Add(new LaunchButton());
        if (Buttons.Count > MaxButtons) Buttons.RemoveRange(MaxButtons, Buttons.Count - MaxButtons);
    }

    public bool RunAtStartup { get; set; } = false;      // Windows のサインイン時に自動で起動する(スタートアップへの登録)。実際の状態はレジストリが正
    public bool TrayResident { get; set; } = false;
    public List<TunerDef> Tuners { get; set; } = TunerDef.Defaults();
    public Dictionary<string, string> StationNames { get; set; } = new();   // 局キー(nid:tsid:sid) → 表示上の局名(内部の局名は変わらない)

    public string ButtonStyle { get; set; } = "Vertical";   // Vertical(縦ボタンレイアウト) / Horizontal(横ボタンレイアウト) / Mix(ミックスレイアウト)
    public string Theme { get; set; } = "System";   // System(Windows に合わせる) / Light / Dark
    public string FontFamily { get; set; } = "Yu Gothic UI";
    public double FontSize { get; set; } = 16;
    public int FontWeight { get; set; } = 400;   // フォントの太さ(100=細い〜400=標準〜700=太字〜900=極太)
    public double DetailWidth { get; set; } = 720;       // 番組内容(ホバー表示)の幅
    public bool UseBackImage { get; set; } = false;      // 「ソフト本体の色」の代わりに画像を使う
    public string BackImagePath { get; set; } = "";      // 背景画像のファイル(PC 内のパス)
    public string TunerAlign { get; set; } = "Left";     // チューナー欄の文字の位置(Left / Center / Right)
    public bool ShowDetail { get; set; } = true;         // 番組内容(ホバー表示)を表示する
    public double DetailDelay { get; set; } = 1.0;       // 番組内容を表示するまでの時間(秒。0.5〜5.0、0.1刻み)
    public double TunerListWidth { get; set; } = 150;

    // 色設定(3つ保存できる。色設定1が元々の色設定。「既定値に戻す」で戻せるのは色設定1のみ)
    public List<ColorSet> ColorSets { get; set; } = new() { new ColorSet(), new ColorSet(), new ColorSet() };
    public int ActiveColorSet { get; set; } = 0;   // 使用中の色設定(0〜2)
    [JsonIgnore] public ColorSet Colors { get { Normalize(); return ColorSets[ActiveColorSet]; } }

    /// <summary>設定ファイルがなく、既定値で起動した(初回起動)。設定画面を自動表示するために使う。保存はしない。</summary>
    [JsonIgnore] public bool IsFirstRun { get; private set; }

    [JsonIgnore] public string TvTestDir => Path.GetDirectoryName(TvTestExe) ?? "";
    [JsonIgnore] public string BonDriverDir => Path.Combine(TvTestDir, "BonDriver");

    const string FileName = "MilookDeck.settings.json";
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public Settings Clone()
    {
        var s = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(this, Json))!;
        s.Normalize();
        return s;
    }

    public static Settings Load(string dir)
    {
        var path = Path.Combine(dir, FileName);
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? new();
                loaded.Normalize();
                return loaded;
            }
        }
        catch { }

        // 設定ファイルがなければ既定値(初回起動)。設定画面で保存されるまでファイルは作らない
        return new Settings { IsFirstRun = !File.Exists(path) };
    }

    public void Save(string dir)
    {
        try { File.WriteAllText(Path.Combine(dir, FileName), JsonSerializer.Serialize(this, Json)); } catch { }
    }
}

/// <summary>非表示の局、ウィンドウ(本体・設定画面)の位置・サイズ。(前回終了時の局は TVTest 側の設定に任せる)</summary>
public class AppState
{
    public HashSet<string> Hidden { get; set; } = new();            // 非表示の局キー
    public double? WinLeft { get; set; }
    public double? WinTop { get; set; }
    public double? WinWidth { get; set; }
    public double? WinHeight { get; set; }
    public double? SetLeft { get; set; }      // 設定画面
    public double? SetTop { get; set; }
    public double? SetWidth { get; set; }
    public double? SetHeight { get; set; }

    public static AppState Load(string path)
    {
        try { return JsonSerializer.Deserialize<AppState>(File.ReadAllText(path)) ?? new(); }
        catch { return new(); }
    }
    public void Save(string path)
    {
        try { File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); }
        catch { }
    }
}
