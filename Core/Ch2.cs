using System.Text;
namespace MilookDeck;

public static class Ch2
{
    /// <summary>TVTest の .ch2 を読む。形式: 名称,空間,チャンネル,リモコン,サービスタイプ,SID,NID,TSID,状態</summary>
    public static List<Station> Load(string path, bool satellite)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var text = File.ReadAllText(path, Encoding.UTF8);
        if (text.Contains('\uFFFD')) text = File.ReadAllText(path, Encoding.GetEncoding(932));
        var list = new List<Station>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == ';') continue;
            var f = line.Split(',');
            if (f.Length < 9) continue;
            try
            {
                list.Add(new Station(f[0], int.Parse(f[1]), int.Parse(f[2]), int.Parse(f[3]), int.Parse(f[4]),
                    ushort.Parse(f[5]), ushort.Parse(f[6]), ushort.Parse(f[7]), f[8].Trim() == "1", satellite));
            }
            catch { /* 壊れた行は無視 */ }
        }
        return list;
    }

    /// <summary>一覧表示用: 設定のチューナーのうち、種別ごとに先頭の BonDriver の .ch2 を読む(有効なテレビの局のみ)。</summary>
    public static List<Station> LoadAll(Settings cfg)
    {
        var all = new List<Station>();
        foreach (var satellite in new[] { false, true })
        {
            var t = cfg.Tuners.FirstOrDefault(x => x.Satellite == satellite);
            if (t == null) continue;
            var path = Path.Combine(cfg.BonDriverDir, Path.GetFileNameWithoutExtension(t.Driver) + ".ch2");
            if (File.Exists(path)) all.AddRange(Load(path, satellite));
        }
        return all.Where(s => s.Enabled && s.Type == 1).ToList();
    }
}
