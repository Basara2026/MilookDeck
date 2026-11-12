using System.Text.Json.Serialization;
namespace MilookDeck;

/// <summary>.ch2 の1行(局)</summary>
public record Station(string Name, int Space, int Channel, int Remote, int Type,
                      ushort Sid, ushort Nid, ushort Tsid, bool Enabled, bool Satellite)
{
    public string Key => $"{Nid}:{Tsid}:{Sid}";
}

/// <summary>Raw = イベントのデスクリプタ(拡張情報・映像・音声・ジャンルなど)。ホバー表示で必要になったときに解析する。</summary>
public record EpgEvent(ushort Nid, ushort Tsid, ushort Sid, ushort Eid, DateTime Start, TimeSpan Duration, string Name, byte[]? Raw = null)
{
    public DateTime End => Start + Duration;
}

/// <summary>チューナー定義(UIのボタン1つ=BonDriver 1つ)。設定画面で編集できる。</summary>
public class TunerDef
{
    public const string KindTerrestrial = "地デジ";
    public const string KindSatellite = "BS/CS";

    public string Label { get; set; } = "";
    public string Driver { get; set; } = "";
    public string Kind { get; set; } = KindTerrestrial;   // 地デジ / BS/CS(局一覧の絞り込みに使う)

    [JsonIgnore] public bool Satellite => Kind == KindSatellite;
    [JsonIgnore] public int No { get; set; }               // 設定画面の表示用

    /// <summary>初期状態: 空欄のチューナーが1つ。</summary>
    public static List<TunerDef> Defaults() => new() { new TunerDef() };
}
