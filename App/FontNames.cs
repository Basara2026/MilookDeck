using System.IO;
using System.Text;
namespace MilookDeck.App;

/// <summary>
/// フォントのファイルから、日本語のファミリ名を直接読み取る。
/// WPF のフォント一覧(Fonts.SystemFontFamilies)は、バリアブルフォントなど一部のフォントで日本語名を返さないことがある
/// (Windows の「設定」のフォント画面には日本語名で出るのに、一覧には英語名しか出ない)。
/// その補いとして、フォントのファイルの name テーブルから「英語名 → 日本語名」の対応を作る。
/// </summary>
public static class FontNames
{
    static Dictionary<string, string>? cache;

    /// <summary>英語のファミリ名 → 日本語のファミリ名(英語名と日本語名が別にあるフォントだけ)。最初の1回だけ読み取る。</summary>
    public static Dictionary<string, string> JapaneseFamilyNames()
    {
        if (cache != null) return cache;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var dirs = new List<string>();
        try { dirs.Add(Environment.GetFolderPath(Environment.SpecialFolder.Fonts)); } catch { }
        try { dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Fonts")); } catch { }
        foreach (var dir in dirs)
        {
            List<string> files;
            try
            {
                if (!Directory.Exists(dir)) continue;
                files = Directory.EnumerateFiles(dir)
                    .Where(f => f.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".otf", StringComparison.OrdinalIgnoreCase)
                             || f.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".otc", StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
            catch { continue; }
            foreach (var file in files)
            {
                try { foreach (var (en, ja) in ReadFamilyPairs(file)) map.TryAdd(en, ja); }
                catch { /* 読めないファイルは飛ばす */ }
            }
        }
        cache = map;
        return map;
    }

    static List<(string en, string ja)> ReadFamilyPairs(string path)
    {
        var result = new List<(string, string)>();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096);
        var head = new byte[12];
        if (fs.Read(head, 0, 12) < 12) return result;
        var offsets = new List<long>();
        if (head[0] == 't' && head[1] == 't' && head[2] == 'c' && head[3] == 'f')   // フォントコレクション
        {
            int n = (int)Math.Min(U32(head, 8), 16u);
            var buf = new byte[4 * n];
            fs.Seek(12, SeekOrigin.Begin);
            if (fs.Read(buf, 0, buf.Length) < buf.Length) return result;
            for (int i = 0; i < n; i++) offsets.Add(U32(buf, i * 4));
        }
        else offsets.Add(0);
        foreach (var off in offsets)
        {
            var pair = ReadOne(fs, off);
            if (pair is { } p) result.Add(p);
        }
        return result;
    }

    /// <summary>1つのフォントの name テーブルから、英語(0x409)と日本語(0x411)のファミリ名を読む。</summary>
    static (string en, string ja)? ReadOne(FileStream fs, long offset)
    {
        var h = new byte[12];
        fs.Seek(offset, SeekOrigin.Begin);
        if (fs.Read(h, 0, 12) < 12) return null;
        int numTables = U16(h, 4);
        if (numTables == 0 || numTables > 200) return null;
        var dir = new byte[numTables * 16];
        if (fs.Read(dir, 0, dir.Length) < dir.Length) return null;

        long nameOffset = -1; long nameLength = 0;
        for (int i = 0; i < numTables; i++)
        {
            int r = i * 16;
            if (dir[r] == 'n' && dir[r + 1] == 'a' && dir[r + 2] == 'm' && dir[r + 3] == 'e')
            {
                nameOffset = U32(dir, r + 8);
                nameLength = U32(dir, r + 12);
                break;
            }
        }
        if (nameOffset < 0 || nameLength < 6 || nameLength > 1_000_000) return null;

        var t = new byte[nameLength];
        fs.Seek(nameOffset, SeekOrigin.Begin);
        if (fs.Read(t, 0, t.Length) < t.Length) return null;

        int count = U16(t, 2), strOff = U16(t, 4);
        string? en16 = null, en1 = null, ja16 = null, ja1 = null;
        for (int i = 0; i < count; i++)
        {
            int r = 6 + i * 12;
            if (r + 12 > t.Length) break;
            int platform = U16(t, r), lang = U16(t, r + 4), id = U16(t, r + 6), len = U16(t, r + 8), o = U16(t, r + 10);
            if (platform != 3 || (id != 1 && id != 16) || (lang != 0x409 && lang != 0x411)) continue;
            int s = strOff + o;
            if (s < 0 || s + len > t.Length) continue;
            var text = Encoding.BigEndianUnicode.GetString(t, s, len).Trim();
            if (text.Length == 0) continue;
            if (lang == 0x409) { if (id == 16) en16 = text; else en1 = text; }
            else { if (id == 16) ja16 = text; else ja1 = text; }
        }
        // 「タイポグラフィのファミリ名」(ID 16)があればそれを、なければ ID 1 を使う
        var en = en16 ?? en1;
        var ja = ja16 ?? ja1;
        if (string.IsNullOrEmpty(en) || string.IsNullOrEmpty(ja) || string.Equals(en, ja, StringComparison.Ordinal)) return null;
        return (en, ja);
    }

    static int U16(byte[] b, int i) => (b[i] << 8) | b[i + 1];
    static uint U32(byte[] b, int i) => ((uint)b[i] << 24) | ((uint)b[i + 1] << 16) | ((uint)b[i + 2] << 8) | b[i + 3];
}
