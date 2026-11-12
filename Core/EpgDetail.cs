using System.Globalization;
using System.Text;
namespace MilookDeck;

/// <summary>番組の詳細表示用の文章を作る(日時・番組名・番組概要・拡張情報・映像・音声・ジャンル)。</summary>
public static class EpgDetail
{
    static readonly string[] Dow = { "日", "月", "火", "水", "木", "金", "土" };
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Build(EpgEvent e)
    {
        string title = e.Name, shortText = "", extText = "";
        (int content, int type)? video = null;
        var audios = new List<(int type, string lang1, string? lang2)>();
        var genres = new List<(int n1, int n2, int u1, int u2)>();
        var ext = new List<(int number, byte[] data)>();

        var r = e.Raw ?? Array.Empty<byte>();
        int i = 0;
        while (i + 2 <= r.Length)
        {
            byte tag = r[i];
            int len = r[i + 1];
            int b = i + 2;
            if (b + len > r.Length) break;
            var d = r.AsSpan(b, len);
            try
            {
                switch (tag)
                {
                    case 0x4D:   // 短形式イベント: 言語(3) 番組名長 番組名 概要長 概要
                    {
                        int nl = d[3];
                        title = AribDecoder.Decode(d.Slice(4, nl));
                        int tl = d[4 + nl];
                        shortText = AribDecoder.Decode(d.Slice(5 + nl, tl), true);
                        break;
                    }
                    case 0x4E:   // 拡張形式イベント
                        ext.Add((d[0] >> 4, d.ToArray()));
                        break;
                    case 0x50:   // コンポーネント(映像)
                    {
                        int sc = d[0] & 0x0F;
                        if (video == null && (sc == 0x01 || sc == 0x05)) video = (sc, d[1]);
                        break;
                    }
                    case 0xC4:   // 音声コンポーネント
                    {
                        int multi = (d[5] >> 7) & 1;
                        string l1 = Lang(d.Slice(6, 3));
                        string? l2 = multi == 1 ? Lang(d.Slice(9, 3)) : null;
                        audios.Add((d[1], l1, l2));
                        break;
                    }
                    case 0x54:   // ジャンル
                        for (int k = 0; k + 1 < len; k += 2)
                            genres.Add((d[k] >> 4, d[k] & 0x0F, d[k + 1] >> 4, d[k + 1] & 0x0F));
                        break;
                }
            }
            catch { /* 壊れたデスクリプタは無視 */ }
            i = b + len;
        }

        // 拡張形式イベント: 項目(見出し+本文)。項目が複数のデスクリプタにまたがる場合は、続きを前の項目に連結する
        var items = new List<(List<byte> desc, List<byte> body)>();
        var extTextBytes = new List<byte>();
        foreach (var (_, data) in ext.OrderBy(x => x.number))
        {
            try
            {
                int itemsLen = data[4];
                int p = 5, end = 5 + itemsLen;
                while (p < end)
                {
                    int dl = data[p++];
                    var desc = data.AsSpan(p, dl).ToArray(); p += dl;
                    int il = data[p++];
                    var body = data.AsSpan(p, il).ToArray(); p += il;
                    if (dl == 0 && items.Count > 0) items[^1].body.AddRange(body);
                    else items.Add((new List<byte>(desc), new List<byte>(body)));
                }
                int tl = data[end];
                extTextBytes.AddRange(data.AsSpan(end + 1, tl).ToArray());
            }
            catch { }
        }
        var extSb = new StringBuilder();
        foreach (var (desc, body) in items)
        {
            if (desc.Count > 0) extSb.Append(AribDecoder.Decode(desc.ToArray(), true)).Append('\n');
            extSb.Append(AribDecoder.Decode(body.ToArray(), true)).Append('\n');
        }
        if (extTextBytes.Count > 0) extSb.Append(AribDecoder.Decode(extTextBytes.ToArray(), true)).Append('\n');
        extText = extSb.ToString();

        // ---- 組み立て ----
        var sb = new StringBuilder();
        sb.Append(e.Start.ToString("yyyy/M/d", Inv)).Append('(').Append(Dow[(int)e.Start.DayOfWeek]).Append(") ")
          .Append(e.Start.ToString("H:mm", Inv)).Append('~').Append(e.End.ToString("H:mm", Inv)).Append('\n');
        sb.Append(Clean(title)).Append('\n');
        if (Clean(shortText).Length > 0) sb.Append('\n').Append(Clean(shortText)).Append('\n');
        if (Clean(extText).Length > 0) sb.Append('\n').Append(Clean(extText)).Append('\n');

        var foot = new StringBuilder();
        if (video != null) foot.Append("■ 映像： ").Append(VideoName(video.Value.type)).Append('\n');
        if (audios.Count > 0) foot.Append("■ 音声： ").Append(string.Join(" / ", audios.Select(a => AudioName(a.type, a.lang1, a.lang2)))).Append('\n');
        if (genres.Count > 0) foot.Append("■ ジャンル： ").Append(string.Join(" / ", genres.Select(g => GenreName(g.n1, g.n2, g.u1, g.u2)))).Append('\n');
        if (foot.Length > 0) sb.Append('\n').Append(foot);
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>改行を整える(先頭・末尾の空行を除き、連続する空行は1つに)。</summary>
    static string Clean(string s)
    {
        s = s.Replace("\r\n", "\n").Replace('\r', '\n').Trim('\n', ' ', '　');
        while (s.Contains("\n\n\n")) s = s.Replace("\n\n\n", "\n\n");
        return s;
    }

    static string Lang(ReadOnlySpan<byte> code)
    {
        var c = Encoding.ASCII.GetString(code);
        return c switch
        {
            "jpn" => "日本語", "eng" => "英語", "deu" => "ドイツ語", "fra" => "フランス語", "ita" => "イタリア語",
            "rus" => "ロシア語", "zho" => "中国語", "kor" => "韓国語", "spa" => "スペイン語", "etc" => "その他",
            _ => c
        };
    }

    /// <summary>映像の種類 → 「1080i[16:9]」のような表記。</summary>
    static string VideoName(int type)
    {
        string res = (type >> 4) switch
        {
            0x0 => "480i", 0x9 => "2160p", 0xA => "480p", 0xB => "1080i", 0xC => "720p", 0xD => "240p", 0xE => "1080p",
            _ => ""
        };
        string aspect = (type & 0x0F) switch { 1 => "4:3", 2 => "16:9", 3 => "16:9", 4 => ">16:9", _ => "" };
        if (res.Length == 0 || aspect.Length == 0) return $"(0x{type:X2})";
        return $"{res}[{aspect}]";
    }

    /// <summary>音声の種類 → 「Stereo [日本語]」のような表記。</summary>
    static string AudioName(int type, string lang1, string? lang2)
    {
        string mode = type switch
        {
            0x01 => "Mono", 0x02 => "デュアルモノ", 0x03 => "Stereo", 0x04 => "2/1ch", 0x05 => "3/0ch",
            0x06 => "2/2ch", 0x07 => "3/1ch", 0x08 => "3/2ch", 0x09 => "5.1ch",
            0x40 => "視覚障害者用音声解説", 0x41 => "聴覚障害者用音声",
            _ => $"(0x{type:X2})"
        };
        var lang = lang2 != null ? $"{lang1}/{lang2}" : lang1;
        return $"{mode} [{lang}]";
    }

    static string GenreName(int n1, int n2, int u1, int u2)
    {
        if (n1 == 0x0E && n2 <= 0x01)   // 番組付属情報 / CS拡張用情報
        {
            n1 = u1 | (0x60 + n2 * 16);
            n2 = u2;
        }
        if (!EpgTables.ContentKinds.TryGetValue((n1 << 8) | 0xFF, out var name1))
            return $"(0x{n1:X2}) - (0x{n2:X2})";
        if (EpgTables.ContentKinds.TryGetValue((n1 << 8) | n2, out var name2)) return $"{name1} - {name2}";
        return n1 != 0x0F ? $"{name1} - (0x{n2:X2})" : name1;
    }
}
