using System.Windows;
using SD = System.Drawing;
using WF = System.Windows.Forms;
namespace MilookDeck.App;

/// <summary>タスクトレイ常駐アイコン。左クリック=表示/収納の切替、右クリック=「閉じる」「設定」「スリープ」。</summary>
public sealed class TrayHost : IDisposable
{
    readonly WF.NotifyIcon icon;
    SleepConfirmWindow? sleepDialog;   // スリープの確認(表示中は二重に出さない)

    public TrayHost(Action toggle, Action settings, Action exit)
    {
        var menu = new WF.ContextMenuStrip { ShowImageMargin = false };   // 画像の余白を出さない(区切り線が左端から引かれる)
        menu.Items.Add("閉じる", null, (_, _) => exit());
        menu.Items.Add("設定", null, (_, _) => settings());
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add("スリープ", null, (_, _) => AskSleep());
        icon = new WF.NotifyIcon { Text = "MilookDeck", Icon = LoadIcon(), ContextMenuStrip = menu, Visible = true };
        icon.MouseClick += (_, e) => { if (e.Button == WF.MouseButtons.Left) toggle(); };
    }

    /// <summary>スリープの確認(30秒のカウントダウン)を出す。「すぐにスリープ」かカウントダウンの終了でスリープする。</summary>
    void AskSleep()
    {
        if (sleepDialog != null) { sleepDialog.Activate(); return; }
        var w = new SleepConfirmWindow(30);
        sleepDialog = w;
        w.Closed += (_, _) =>
        {
            sleepDialog = null;
            if (w.Confirmed) Sleep();
        };
        w.Show();
        w.Activate();
    }

    /// <summary>パソコンをスリープさせる(復帰するまで戻らないので、画面操作を止めないように別のスレッドで呼ぶ)。</summary>
    static void Sleep() => Task.Run(() => WF.Application.SetSuspendState(WF.PowerState.Suspend, false, false));

    static SD.Icon LoadIcon()
    {
        try
        {
            var s = Application.GetResourceStream(new Uri("pack://application:,,,/MilookDeck.ico"));
            return new SD.Icon(s.Stream, WF.SystemInformation.SmallIconSize);
        }
        catch { return SD.SystemIcons.Application; }
    }

    public void Dispose() { sleepDialog?.Close(); icon.Visible = false; icon.Dispose(); }
}
