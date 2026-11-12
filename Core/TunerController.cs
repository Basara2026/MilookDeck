using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
namespace MilookDeck;

/// <summary>TVTest 1個(=チューナー1個)の制御。起動はコマンドライン、切替はプラグイン(名前付きパイプ)。</summary>
public class TunerController
{
    public TunerDef Def { get; }
    public Process? Proc { get; set; }
    readonly Settings cfg;

    public TunerController(TunerDef def, Settings cfg) { Def = def; this.cfg = cfg; }

    public bool IsRunning
    {
        get { try { return Proc != null && !Proc.HasExited; } catch { return false; } }
    }

    /// <summary>TVTest を起動する(/s は付けない=多重起動可)。
    /// 局を指定しなければ BonDriver だけ渡し、初期チャンネルは TVTest の設定(前回終了時のチャンネル等)に任せる。</summary>
    public void Launch(Station? s = null)
    {
        var args = $"/d {Def.Driver}";
        if (s != null) args += $" /chspace {s.Space} /ch {s.Channel} /sid {s.Sid} /nid {s.Nid} /tsid {s.Tsid}";
        if (!string.IsNullOrWhiteSpace(cfg.TvTestArgs)) args += " " + cfg.TvTestArgs.Trim();   // 設定の起動オプションを最後に付ける
        Proc = Process.Start(new ProcessStartInfo(cfg.TvTestExe, args)
        {
            WorkingDirectory = cfg.TvTestDir,
            UseShellExecute = false
        });
    }

    /// <summary>プラグイン経由で局を切り替える。成功なら null、失敗ならエラー文字列。</summary>
    public async Task<string?> SwitchAsync(Station s)
    {
        if (!IsRunning) return $"{Path.GetFileNameWithoutExtension(cfg.TvTestExe)} が起動していません";
        var r = await PipeClient.SendAsync(Proc!.Id, $"SETSVC {s.Nid} {s.Tsid} {s.Sid}");
        return r != null && r.StartsWith("OK") ? null : (r ?? "プラグインに接続できません(MilookDeckBridge.tvtp を有効にしてください)");
    }

    /// <summary>
    /// 起動直後の TVTest が、プラグインのパイプを開くのを待ってから局を切り替える。
    /// (起動時のコマンドラインでの局指定は、TVTest 側で選局されず映像が出ないことがあるため、起動後にプラグインで切り替える)
    /// 成功なら null、失敗ならエラー文字列。
    /// </summary>
    public async Task<string?> SwitchWhenReadyAsync(Station s, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        DateTime? errDeadline = null;   // プラグインは応答したが、選局に失敗した場合の再試行の期限(ドライバの準備待ち)
        string? last = null;
        while (DateTime.UtcNow < deadline)
        {
            if (!IsRunning) return $"{Path.GetFileNameWithoutExtension(cfg.TvTestExe)} が終了しました";
            var r = await PipeClient.SendAsync(Proc!.Id, $"SETSVC {s.Nid} {s.Tsid} {s.Sid}");
            if (r != null && r.StartsWith("OK")) return null;
            if (r != null)   // "ERR ..." : BonDriver の準備ができるまで、少しの間だけ再試行する
            {
                last = r;
                errDeadline ??= DateTime.UtcNow + TimeSpan.FromSeconds(8);
                if (DateTime.UtcNow >= errDeadline) return r;
            }
            await Task.Delay(500);
        }
        return last ?? "プラグインに接続できません(MilookDeckBridge.tvtp を有効にしてください)";
    }

    public void Close()
    {
        if (IsRunning) Proc!.CloseMainWindow();   // WM_CLOSE 相当。TVTest が通常終了する
    }

    /// <summary>既に起動している TVTest を、プラグインの GETSTATE で BonDriver 名を聞いて対応付ける。</summary>
    public static async Task AttachExistingAsync(IEnumerable<TunerController> tuners, string processName)
    {
        foreach (var p in Process.GetProcessesByName(processName))
        {
            var r = await PipeClient.SendAsync(p.Id, "GETSTATE");
            if (r == null || !r.StartsWith("OK driver=")) continue;
            var driver = r.Split(' ')[1]["driver=".Length..];
            var t = tuners.FirstOrDefault(x => !x.IsRunning &&
                string.Equals(x.Def.Driver, driver, StringComparison.OrdinalIgnoreCase));
            if (t != null) t.Proc = p;
        }
    }
}

public static class PipeClient
{
    /// <summary>1行コマンドを送り、1行の応答を受ける。接続できなければ null。</summary>
    public static async Task<string?> SendAsync(int pid, string cmd)
    {
        try
        {
            using var c = new NamedPipeClientStream(".", $"MilookDeck_{pid}", PipeDirection.InOut);
            await c.ConnectAsync(1500);
            var enc = new UTF8Encoding(false);
            var data = enc.GetBytes(cmd + "\n");
            await c.WriteAsync(data);
            await c.FlushAsync();
            using var r = new StreamReader(c, enc);
            return await r.ReadLineAsync();
        }
        catch { return null; }
    }
}
