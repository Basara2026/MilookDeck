using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
namespace MilookDeck;

/// <summary>EDCB の予約1件(Reserve.txt の内容)。</summary>
public record EdcbReserve(uint Id, DateTime Start, DateTime End, DateTime EffStart, DateTime EffEnd, int RecMode, string Title,
                          ushort Onid = 0, ushort Tsid = 0, ushort Sid = 0, ushort Eid = 0);

/// <summary>ある時点の EDCB の状態。</summary>
public class EdcbSnapshot
{
    public bool PipeOk { get; init; }   // EpgTimerSrv と通信できたか
    public HashSet<string> BusyDrivers { get; init; } = new(StringComparer.OrdinalIgnoreCase);   // いま EDCB が使用中の BonDriver
    public Dictionary<string, List<EdcbReserve>> ByDriver { get; init; } = new(StringComparer.OrdinalIgnoreCase);   // BonDriver ごとの録画予定
    public static readonly EdcbSnapshot Empty = new();
}

public enum LaunchBlock { None, Recording, Reserved }

/// <summary>
/// EDCB との連携。
/// ・どの予約がどの BonDriver で録画されるかは EpgTimerSrv が内部で決めるため Reserve.txt には無い
///   → EpgTimerSrv のパイプ(CtrlCmd)の「チューナーごとの予約ID一覧」(1016)で取得
/// ・いま使用中のチューナーは「起動中チューナーの一覧」(1066)で取得
/// ・予約の日時・マージンは Setting\Reserve.txt から取得
/// </summary>
public static class Edcb
{
    const string ConnectEvent = @"Global\EpgTimerSrvConnect";
    const string PipeName = "EpgTimerSrvPipe";
    const uint CmdEnumTunerReserve = 1016;   // CMD2_EPG_SRV_ENUM_TUNER_RESERVE
    const uint CmdEnumTunerProcess = 1066;   // CMD2_EPG_SRV_ENUM_TUNER_PROCESS
    const uint CmdSuccess = 1;
    const uint NoTunerId = 0xFFFFFFFF;       // 「チューナー不足」の予約

    /// <summary>この時間以内に録画予定の BonDriver では、TVTest を起動させない。この時間になったら視聴中の TVTest を閉じる。</summary>
    public static readonly TimeSpan CloseBefore = TimeSpan.FromMinutes(3);
    /// <summary>この時間前から、終了までのカウントダウンを表示する。</summary>
    public static readonly TimeSpan CountdownStart = TimeSpan.FromMinutes(4);

    // ---------------- 取得(ブロッキング。Task.Run で呼ぶ) ----------------
    public static EdcbSnapshot Fetch(string edcbDir, int timeoutMs = 1500)
    {
        var tr = Send(CmdEnumTunerReserve, timeoutMs);
        if (tr == null) return new EdcbSnapshot { PipeOk = false };
        List<(uint id, string name, List<uint> reserves)> tuners;
        try { tuners = ParseTunerReserve(tr); } catch { return new EdcbSnapshot { PipeOk = false }; }

        var busyIds = new HashSet<uint>();
        var tp = Send(CmdEnumTunerProcess, timeoutMs);
        if (tp != null) { try { busyIds = ParseTunerProcess(tp); } catch { } }

        var snap = new EdcbSnapshot { PipeOk = true };
        var reserves = ReadReserveTxt(edcbDir);
        foreach (var (id, name, ids) in tuners)
        {
            if (id == NoTunerId) continue;
            if (busyIds.Contains(id)) snap.BusyDrivers.Add(name);
            var list = new List<EdcbReserve>();
            foreach (var rid in ids)
                if (reserves.TryGetValue(rid, out var r) && r.RecMode != 5)   // 5=無効
                    list.Add(r);
            snap.ByDriver[name] = list.OrderBy(r => r.EffStart).ToList();
        }
        return snap;
    }

    // ---------------- 判定 ----------------
    /// <summary>その BonDriver の、まだ終わっていない最も早い録画予定。</summary>
    public static EdcbReserve? NextReserve(EdcbSnapshot s, string driver, DateTime now) =>
        s.ByDriver.TryGetValue(driver, out var l) ? l.FirstOrDefault(r => r.EffEnd > now) : null;

    public static LaunchBlock CheckLaunch(EdcbSnapshot s, string driver, DateTime now)
    {
        if (!s.PipeOk) return LaunchBlock.None;
        if (s.BusyDrivers.Contains(driver)) return LaunchBlock.Recording;
        var r = NextReserve(s, driver, now);
        if (r != null && r.EffStart - now <= CloseBefore) return LaunchBlock.Reserved;   // すでに開始済みの予約も含む
        return LaunchBlock.None;
    }

    // ---------------- EpgTimerSrv のパイプ通信 ----------------
    static byte[]? Send(uint cmd, int timeoutMs)
    {
        try
        {
            if (!WaitConnectEvent(timeoutMs)) return null;
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.None);
            pipe.Connect(timeoutMs);
            var req = new byte[8];
            BitConverter.TryWriteBytes(req.AsSpan(0), cmd);   // コマンドID + データサイズ(0)
            pipe.Write(req, 0, 8);
            pipe.Flush();
            var head = ReadExact(pipe, 8);
            if (head == null || BitConverter.ToUInt32(head, 0) != CmdSuccess) return null;
            return ReadExact(pipe, (int)BitConverter.ToUInt32(head, 4));
        }
        catch { return null; }
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr OpenEvent(uint desiredAccess, bool inheritHandle, string name);
    [DllImport("kernel32", SetLastError = true)]
    static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32", SetLastError = true)]
    static extern bool CloseHandle(IntPtr handle);

    /// <summary>EpgTimerSrv が用意する接続用イベント(Global\EpgTimerSrvConnect)が通知状態になるのを待つ。同期の権限(SYNCHRONIZE)だけで開く。</summary>
    static bool WaitConnectEvent(int timeoutMs)
    {
        const uint Synchronize = 0x00100000;
        var h = OpenEvent(Synchronize, false, ConnectEvent);
        if (h == IntPtr.Zero) return false;   // EpgTimerSrv が起動していない
        try { return WaitForSingleObject(h, (uint)timeoutMs) == 0; }   // 0 = WAIT_OBJECT_0
        finally { CloseHandle(h); }
    }

    static byte[]? ReadExact(Stream s, int n)
    {
        var buf = new byte[n];
        int got = 0;
        while (got < n)
        {
            int m = s.Read(buf, got, n - got);
            if (m <= 0) return null;
            got += m;
        }
        return buf;
    }

    static uint U32(byte[] d, int p) => BitConverter.ToUInt32(d, p);

    // [全体サイズ][個数] + 各要素 [要素サイズ][tunerID][tunerName(文字列)][予約IDの配列]
    static List<(uint, string, List<uint>)> ParseTunerReserve(byte[] d)
    {
        var res = new List<(uint, string, List<uint>)>();
        uint count = U32(d, 4);
        int p = 8;
        for (uint i = 0; i < count; i++)
        {
            int itemEnd = p + (int)U32(d, p);
            int q = p + 4;
            uint id = U32(d, q); q += 4;
            int ssz = (int)U32(d, q);                                   // 文字列: [サイズ(自身を含む)][UTF-16 + NUL]
            var name = Encoding.Unicode.GetString(d, q + 4, ssz - 4).TrimEnd('\0'); q += ssz;
            int vsz = (int)U32(d, q);                                   // 配列: [サイズ][個数][DWORD...]
            uint vc = U32(d, q + 4);
            var ids = new List<uint>();
            for (uint j = 0; j < vc; j++) ids.Add(U32(d, q + 8 + (int)j * 4));
            res.Add((id, name, ids));
            p = itemEnd;
        }
        return res;
    }

    // 各要素 [要素サイズ][tunerID][processID][...]。processID が 0 以外=起動中
    static HashSet<uint> ParseTunerProcess(byte[] d)
    {
        var set = new HashSet<uint>();
        uint count = U32(d, 4);
        int p = 8;
        for (uint i = 0; i < count; i++)
        {
            int itemEnd = p + (int)U32(d, p);
            uint tunerId = U32(d, p + 4);
            uint pid = U32(d, p + 8);
            if (pid != 0) set.Add(tunerId);
            p = itemEnd;
        }
        return set;
    }

    // ---------------- Reserve.txt ----------------
    [DllImport("kernel32", CharSet = CharSet.Unicode)]
    static extern int GetPrivateProfileInt(string section, string key, int def, string file);

    /// <summary>EpgTimerSrv.ini [SET] の既定の録画マージン(秒)。見つからなければ EDCB の既定値(開始5/終了2)。</summary>
    static (int start, int end) DefaultMargins(string edcbDir)
    {
        foreach (var p in new[] { Path.Combine(edcbDir, "EpgTimerSrv.ini"), Path.Combine(edcbDir, "Setting", "EpgTimerSrv.ini") })
            if (File.Exists(p))
                return (GetPrivateProfileInt("SET", "StartMargin", 5, p), GetPrivateProfileInt("SET", "EndMargin", 2, p));
        return (5, 2);
    }

    public static Dictionary<uint, EdcbReserve> ReadReserveTxt(string edcbDir)
    {
        var map = new Dictionary<uint, EdcbReserve>();
        var path = Path.Combine(edcbDir, "Setting", "Reserve.txt");
        if (!File.Exists(path)) return map;

        string text;
        try
        {
            byte[] bytes;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var ms = new MemoryStream()) { fs.CopyTo(ms); bytes = ms.ToArray(); }
            try { text = new UTF8Encoding(false, true).GetString(bytes); }
            catch { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); text = Encoding.GetEncoding(932).GetString(bytes); }
        }
        catch { return map; }
        text = text.TrimStart('\uFEFF');   // Reserve.txt は BOM 付きの UTF-8 のことがある

        var (defStart, defEnd) = DefaultMargins(edcbDir);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line[0] == ';') continue;
            var f = line.Split('\t');
            if (f.Length < 25) continue;
            try
            {
                var start = DateTime.ParseExact(f[0] + " " + f[1], "yyyy/MM/dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
                var dp = f[2].Split(':');
                int dur = int.Parse(dp[0]) * 3600 + int.Parse(dp[1]) * 60 + int.Parse(dp[2]);
                uint id = uint.Parse(f[11]);
                int recMode = int.Parse(f[12]);
                bool useMargin = int.Parse(f[21]) != 0;
                int startMargin = useMargin ? int.Parse(f[22]) : defStart;
                int endMargin = useMargin ? int.Parse(f[23]) : defEnd;
                // 録画開始 = 開始 - 開始マージン(ただし元の終了時刻を超えて遅くしない)。録画終了 = 終了 + 終了マージン
                var effStart = start.AddSeconds(-Math.Max(startMargin, -dur));
                var end = start.AddSeconds(dur);
                var effEnd = end.AddSeconds(Math.Max(endMargin, -dur));
                map[id] = new EdcbReserve(id, start, end, effStart, effEnd, recMode, f[3],
                    ushort.Parse(f[5]), ushort.Parse(f[6]), ushort.Parse(f[7]), ushort.Parse(f[8]));
            }
            catch { /* 壊れた行は無視 */ }
        }
        return map;
    }
}
