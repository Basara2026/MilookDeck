using System.Text;
namespace MilookDeck;

/// <summary>
/// iEPG(デジタル用 Version 2 の .tvpid)ファイルを作る。EpgTimer.exe にこのファイルを渡すと、その番組が予約登録される。
/// (TVTest の「iEPG関連付け実行」と同じ方式。EpgTimer 側の修正は不要)
/// </summary>
public static class Iepg
{
    /// <summary>iEPG の station 欄の値(地上=DFS+サービスID(16進4桁)、BS=BSDT+ID、CS=CSDT+ID)。対応しない放送は null。</summary>
    public static string? StationCode(ushort onid, ushort sid)
    {
        if (onid >= 0x7880 && onid <= 0x7FE8) return $"DFS{sid:X4}";   // 地上デジタル
        if (onid == 4) return $"BSDT{sid}";                              // BS
        if (onid == 6 || onid == 7) return $"CSDT{sid}";                 // 110度CS
        return null;
    }

    public static string Build(EpgEvent ev, string station)
    {
        var title = (ev.Name ?? "").Replace("\r", " ").Replace("\n", " ");
        var sb = new StringBuilder();
        sb.Append("Content-type: application/x-tv-program-digital-info; charset=shift_jis\r\n");
        sb.Append("version: 2\r\n");
        sb.Append("station: ").Append(station).Append("\r\n");
        sb.Append("year: ").Append(ev.Start.Year).Append("\r\n");
        sb.Append("month: ").Append(ev.Start.Month).Append("\r\n");
        sb.Append("date: ").Append(ev.Start.Day).Append("\r\n");
        sb.Append("start: ").Append(ev.Start.ToString("HH:mm")).Append("\r\n");
        sb.Append("end: ").Append(ev.End.ToString("HH:mm")).Append("\r\n");
        sb.Append("program-id: ").Append(ev.Eid).Append("\r\n");   // イベントID(EPG予約になる。なければプログラム予約)
        sb.Append("program-title: ").Append(title).Append("\r\n");
        sb.Append("\r\n");   // ヘッダの終わり(空行)
        return sb.ToString();
    }

    /// <summary>一時フォルダに .tvpid を書き出してパスを返す。古い一時ファイルは削除する。</summary>
    public static string WriteTempFile(EpgEvent ev, string station)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var dir = Path.GetTempPath();
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "MilookDeck_*.tvpid"))
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-1)) File.Delete(f);
        }
        catch { }
        var path = Path.Combine(dir, $"MilookDeck_{Guid.NewGuid():N}.tvpid");
        File.WriteAllText(path, Build(ev, station), Encoding.GetEncoding(932));   // EpgTimer は Shift_JIS で読む
        return path;
    }
}
