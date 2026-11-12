namespace MilookDeck;

/// <summary>EpgDataCap_Bon の EpgData フォルダ(*_epg.dat = EIT を含む生TS)を読む。</summary>
public static class EpgReader
{
    public static EpgSnapshot Load(string dir, DateTime now, EpgSnapshot? previous = null)
    {
        var from = now.AddHours(-2);
        var to = now.AddHours(48);
        var rawTo = now.AddHours(48);   // 番組内容(生データ)を保持する範囲: 現在から48時間先まで
        var map = new Dictionary<(ushort, ushort, ushort, ushort), EpgEvent>();
        foreach (var file in Directory.EnumerateFiles(dir, "*_epg.dat"))
        {
            byte[] data;
            try { data = File.ReadAllBytes(file); } catch { continue; }   // EDCB が書き込み中の場合はスキップ
            foreach (var sec in Sections(data))
                foreach (var e in ParseEit(sec, from, rawTo))
                    if (e.End > from && e.Start < to)
                    {
                        var key = (e.Nid, e.Tsid, e.Sid, e.Eid);
                        // 同じ番組の名前なしセクションで、名前ありのデータを上書きしない
                        var ev = e;
                        if (map.TryGetValue(key, out var old) && old.Start == ev.Start)
                        {
                            if (ev.Name.Length == 0 && old.Name.Length > 0) ev = ev with { Name = old.Name };
                            if (old.Raw != null && (ev.Raw == null || old.Raw.Length > ev.Raw.Length)) ev = ev with { Raw = old.Raw };
                        }
                        map[key] = ev;
                    }
        }
        if (previous != null)   // それでも名前が空なら、前回読込分から補う
            foreach (var k in map.Keys.ToList())
            {
                var e = map[k];
                if (e.Name.Length > 0) continue;
                var n = previous.NameOf(e);
                if (!string.IsNullOrEmpty(n)) map[k] = e with { Name = n };
            }
        var byService = map.Values.GroupBy(e => (e.Nid, e.Tsid, e.Sid))
                           .ToDictionary(g => g.Key, g => g.OrderBy(e => e.Start).ToList());
        return new EpgSnapshot(byService);
    }

    public static DateTime LatestWrite(string dir) =>
        Directory.EnumerateFiles(dir, "*_epg.dat").Select(File.GetLastWriteTime).DefaultIfEmpty(DateTime.MinValue).Max();

    static List<byte[]> Drain(List<byte> buf)
    {
        var r = new List<byte[]>();
        while (buf.Count >= 3 && buf[0] != 0xFF)
        {
            int len = 3 + (((buf[1] & 0x0F) << 8) | buf[2]);
            if (buf.Count < len) break;
            r.Add(buf.GetRange(0, len).ToArray());
            buf.RemoveRange(0, len);
        }
        return r;
    }

    static IEnumerable<byte[]> Sections(byte[] d)
    {
        var buf = new List<byte>();
        for (int p = 0; p + 188 <= d.Length; p += 188)
        {
            if (d[p] != 0x47) continue;
            int pid = ((d[p + 1] & 0x1F) << 8) | d[p + 2];
            if (pid != 0x12) continue;
            bool pusi = (d[p + 1] & 0x40) != 0;
            int af = (d[p + 3] >> 4) & 3;
            if ((af & 1) == 0) continue;
            int off = p + 4;
            if ((af & 2) != 0) off += 1 + d[p + 4];
            int end = p + 188;
            if (off >= end) continue;
            if (pusi)
            {
                int ptr = d[off]; int start = off + 1;
                if (start + ptr > end) continue;
                if (buf.Count > 0 && ptr > 0) buf.AddRange(new ArraySegment<byte>(d, start, ptr));
                foreach (var s in Drain(buf)) yield return s;
                buf.Clear();
                buf.AddRange(new ArraySegment<byte>(d, start + ptr, end - start - ptr));
            }
            else buf.AddRange(new ArraySegment<byte>(d, off, end - off));
            foreach (var s in Drain(buf)) yield return s;
        }
    }

    static int Bcd(byte x) => (x >> 4) * 10 + (x & 15);

    static IEnumerable<EpgEvent> ParseEit(byte[] s, DateTime rawFrom, DateTime rawTo)
    {
        byte tid = s[0];
        if (tid < 0x4E || tid > 0x6F || s.Length < 18) yield break;
        ushort sid = (ushort)((s[3] << 8) | s[4]);
        ushort tsid = (ushort)((s[8] << 8) | s[9]);
        ushort nid = (ushort)((s[10] << 8) | s[11]);
        int i = 14, end = s.Length - 4;
        while (i + 12 <= end)
        {
            int e0 = i;
            ushort eid = (ushort)((s[e0] << 8) | s[e0 + 1]);
            int mjd = (s[e0 + 2] << 8) | s[e0 + 3];
            int dl = ((s[e0 + 10] & 0x0F) << 8) | s[e0 + 11];
            int j = e0 + 12, jEnd = Math.Min(e0 + 12 + dl, end);
            string name = "";
            while (j + 2 <= jEnd)
            {
                byte tag = s[j], len = s[j + 1];
                if (tag == 0x4D && j + 6 <= jEnd)           // 短形式イベント記述子
                {
                    int nl = s[j + 5];
                    if (j + 6 + nl <= jEnd) name = AribDecoder.Decode(s.AsSpan(j + 6, nl));
                }
                j += 2 + len;
            }
            i = e0 + 12 + dl;
            if (mjd == 0xFFFF || s[e0 + 4] == 0xFF) continue;   // 開始時刻未定
            // EIT の時刻は JST(MJD + BCD)なのでそのまま使う
            var start = new DateTime(1858, 11, 17).AddDays(mjd)
                .AddHours(Bcd(s[e0 + 4])).AddMinutes(Bcd(s[e0 + 5])).AddSeconds(Bcd(s[e0 + 6]));
            var dur = new TimeSpan(Bcd(s[e0 + 7]), Bcd(s[e0 + 8]), Bcd(s[e0 + 9]));
            byte[]? raw = null;
            if (start + dur > rawFrom && start < rawTo && jEnd > e0 + 12)
                raw = s.AsSpan(e0 + 12, jEnd - (e0 + 12)).ToArray();
            yield return new EpgEvent(nid, tsid, sid, eid, start, dur, name, raw);
        }
    }
}

public class EpgSnapshot
{
    readonly Dictionary<(ushort, ushort, ushort), List<EpgEvent>> d;
    public static readonly EpgSnapshot Empty = new(new());
    public EpgSnapshot(Dictionary<(ushort, ushort, ushort), List<EpgEvent>> d) => this.d = d;

    public EpgEvent? Current(Station s, DateTime now) =>
        d.TryGetValue((s.Nid, s.Tsid, s.Sid), out var l) ? l.LastOrDefault(e => e.Start <= now && now < e.End) : null;

    /// <summary>その局の、指定の期間にかかる番組(放送中のものを含む)を、開始順に返す。</summary>
    public List<EpgEvent> Between(Station s, DateTime from, DateTime to) =>
        d.TryGetValue((s.Nid, s.Tsid, s.Sid), out var l)
            ? l.Where(e => e.End > from && e.Start < to).OrderBy(e => e.Start).ToList()
            : new List<EpgEvent>();

    public string? NameOf(EpgEvent e) =>
        d.TryGetValue((e.Nid, e.Tsid, e.Sid), out var l)
            ? l.FirstOrDefault(x => x.Eid == e.Eid && x.Start == e.Start && x.Name.Length > 0)?.Name : null;

    public EpgEvent? Next(Station s, DateTime now) =>
        d.TryGetValue((s.Nid, s.Tsid, s.Sid), out var l) ? l.FirstOrDefault(e => e.Start > now) : null;
}
