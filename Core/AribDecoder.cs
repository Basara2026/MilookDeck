using System.Text;
namespace MilookDeck;

/// <summary>ARIB STD-B24 8単位符号 → string(番組名用の簡易実装)。
/// 外字(追加記号・追加漢字)は AribGaiji の表で「[字]」「[デ]」などの文字に置き換えます。</summary>
public static class AribDecoder
{
    enum G { Kanji, Alnum, Hira, Kata, HalfKata, Symbols, Other }
    static readonly Encoding Sjis;
    static AribDecoder()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Sjis = Encoding.GetEncoding(932);
    }

    static G F1(byte f) => f switch { 0x4A => G.Alnum, 0x30 => G.Hira, 0x31 => G.Kata, 0x49 => G.HalfKata, _ => G.Other };
    static G F2(byte f) => f switch { 0x42 or 0x39 => G.Kanji, 0x3B => G.Symbols, _ => G.Other };
    static bool TwoByte(G g) => g is G.Kanji or G.Symbols;

    /// <param name="lineBreaks">true なら復帰(0x0D)を改行として残す(番組内容の表示用)。false なら空白にする(番組名用)。</param>
    public static string Decode(ReadOnlySpan<byte> b, bool lineBreaks = false)
    {
        var g = new[] { G.Kanji, G.Alnum, G.Hira, G.Kata };
        int gl = 0, gr = 2, ss = -1;
        var sb = new StringBuilder();
        int i = 0;
        while (i < b.Length)
        {
            byte c = b[i++];
            if (c == 0x1B)
            {
                if (i >= b.Length) break;
                byte c2 = b[i++];
                switch (c2)
                {
                    case 0x6E: gl = 2; break;
                    case 0x6F: gl = 3; break;
                    case 0x7E: gr = 1; break;
                    case 0x7D: gr = 2; break;
                    case 0x7C: gr = 3; break;
                    case 0x28: case 0x29: case 0x2A: case 0x2B:
                    {
                        if (i >= b.Length) return sb.ToString();
                        int idx = c2 - 0x28; byte f = b[i++];
                        if (f == 0x20) { i++; g[idx] = G.Other; } else g[idx] = F1(f);
                        break;
                    }
                    case 0x24:
                    {
                        if (i >= b.Length) return sb.ToString();
                        byte f = b[i++]; int idx = 0;
                        if (f >= 0x29 && f <= 0x2B)
                        {
                            idx = f - 0x28;
                            if (i >= b.Length) return sb.ToString();
                            f = b[i++];
                        }
                        if (f == 0x20) { i++; g[idx] = G.Other; } else g[idx] = F2(f);
                        break;
                    }
                }
                continue;
            }
            switch (c)
            {
                case 0x0F: gl = 0; continue;
                case 0x0E: gl = 1; continue;
                case 0x19: ss = 2; continue;
                case 0x1D: ss = 3; continue;
                case 0x0D: sb.Append(lineBreaks ? '\n' : ' '); continue;
                case 0x0A: if (!lineBreaks) sb.Append(' '); continue;
                case 0x20: case 0xA0: sb.Append(' '); continue;
                case 0x16: i++; continue;
                case 0x1C: i += 2; continue;
                case 0x9B:
                    while (i < b.Length && b[i] >= 0x20 && b[i] <= 0x3F) i++;
                    i++; continue;
            }
            if (c < 0x21 || (c >= 0x7F && c <= 0xA0) || c == 0xFF) continue;

            G set; int code = c;
            if (c < 0x7F) set = g[ss >= 0 ? ss : gl];
            else { set = g[gr]; code = c & 0x7F; }
            ss = -1;

            if (TwoByte(set))
            {
                if (i >= b.Length) break;
                int c2 = b[i++] & 0x7F;
                sb.Append(Kanji(code, c2));   // 漢字・追加記号(外字は「[字]」などの文字に置き換え)
                continue;
            }
            switch (set)
            {
                case G.Alnum: sb.Append((char)code); break;
                case G.Hira: sb.Append(Kana(code, 0x3041)); break;
                case G.Kata: sb.Append(Kana(code, 0x30A1)); break;
                case G.HalfKata: if (code >= 0x21 && code <= 0x5F) sb.Append((char)(0xFF61 + code - 0x21)); break;
            }
        }
        return sb.ToString();
    }

    static string Kana(int c, int basePoint) => c switch
    {
        >= 0x21 and <= 0x76 => ((char)(basePoint + c - 0x21)).ToString(),
        0x79 => "ー", 0x7A => "。", 0x7B => "「", 0x7C => "」", 0x7D => "、", 0x7E => "・",
        _ => ""
    };

    static string Kanji(int j1, int j2)
    {
        if (j1 >= 0x75 || j2 < 0x21 || j2 >= 0x7F) return AribGaiji.Lookup((j1 << 8) | j2);   // 85区以降=外字
        int s1 = ((j1 + 1) >> 1) + (j1 <= 0x5E ? 0x70 : 0xB0);
        int s2 = (j1 & 1) != 0 ? j2 + (j2 <= 0x5F ? 0x1F : 0x20) : j2 + 0x7E;
        try { return Sjis.GetString(new[] { (byte)s1, (byte)s2 }); } catch { return ""; }
    }
}
