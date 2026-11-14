using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using SD = System.Drawing;
using WF = System.Windows.Forms;
namespace MilookDeck.App;

/// <summary>
/// タスクトレイ常駐アイコン。左クリック=表示/収納の切替、右クリック=「閉じる」「設定」「スリープ」。
/// 右クリックのメニューは、Windows フォームの旧式のメニューではなく WPF のメニューにして、
/// Windows 11 らしい見た目と画面の表示倍率への追従を、本体の画面と同じにしている。
/// </summary>
public sealed class TrayHost : IDisposable
{
    readonly WF.NotifyIcon icon;
    readonly Action exit, settings;
    Window? menuHost;                  // メニューを前面で開くための、見えない小さなウィンドウ
    SleepConfirmWindow? sleepDialog;   // スリープの確認(表示中は二重に出さない)

    public TrayHost(Action toggle, Action settings, Action exit)
    {
        this.exit = exit;
        this.settings = settings;
        icon = new WF.NotifyIcon { Text = "MilookDeck", Icon = LoadIcon(), Visible = true };
        icon.MouseClick += (_, e) => { if (e.Button == WF.MouseButtons.Left) toggle(); };
        icon.MouseUp += (_, e) => { if (e.Button == WF.MouseButtons.Right) ShowMenu(); };
    }

    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>右クリックのメニュー(閉じる / 設定 / 区切り線 / スリープ)を、マウスの位置に開く。</summary>
    void ShowMenu()
    {
        // タスクトレイからメニューを開くときは、アプリが前面になっていないと、メニュー以外をクリックしても閉じない。
        // そこで、見えない小さなウィンドウを前面にしてから開く。
        menuHost ??= new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            ShowInTaskbar = false, Width = 1, Height = 1, Left = -10000, Top = -10000,
        };
        var host = menuHost;
        host.Show();
        SetForegroundWindow(new WindowInteropHelper(host).Handle);

        var menu = new ContextMenu { Placement = PlacementMode.MousePoint, PlacementTarget = host, FontFamily = new FontFamily("Yu Gothic UI, Meiryo UI") };
        void Add(string text, Action action)
        {
            var item = new MenuItem { Header = text };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        Add("閉じる", exit);
        Add("設定", settings);
        menu.Items.Add(new Separator());
        Add("スリープ", AskSleep);
        menu.Closed += (_, _) => host.Hide();
        menu.IsOpen = true;
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

    public void Dispose() { sleepDialog?.Close(); menuHost?.Close(); icon.Visible = false; icon.Dispose(); }
}
