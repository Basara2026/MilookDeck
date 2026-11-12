using System.IO;
using System.Windows;
namespace MilookDeck.App;

public partial class App : Application
{
    public App()
    {
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        // 起動時に落ちた原因を表示し、MilookDeck.crash.log にも残す
        DispatcherUnhandledException += (_, e) => { Log(e.Exception); e.Handled = true; Shutdown(1); };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log(e.Exception); e.SetObserved(); };
    }

    Mutex? instanceMutex;
    EventWaitHandle? showEvent;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 二重起動の禁止。すでに起動中なら、起動中のウィンドウを前面に出して、自分は終了する
        instanceMutex = new Mutex(true, @"Local\MilookDeck_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            try { using var ev = EventWaitHandle.OpenExisting(@"Local\MilookDeck_Show"); ev.Set(); } catch { }
            Shutdown();
            return;
        }
        showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\MilookDeck_Show");

        var w = new global::MilookDeck.App.MainWindow();   // Application.MainWindow プロパティと名前が衝突するため完全修飾
        MainWindow = w;
        if (!w.StartInTray) w.Show();   // 「タスクトレイに常駐する」が有効ならウィンドウを出さずにトレイから起動

        var th = new Thread(() =>
        {
            while (true)
            {
                showEvent.WaitOne();
                Dispatcher.BeginInvoke(new Action(() => w.BringToFront()));
            }
        }) { IsBackground = true };
        th.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { instanceMutex?.ReleaseMutex(); } catch { }
        base.OnExit(e);
    }

    static void Log(Exception? ex)
    {
        var text = ex?.ToString() ?? "unknown error";
        try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "MilookDeck.crash.log"), $"{DateTime.Now:s}\r\n{text}\r\n\r\n"); } catch { }
        try { MessageBox.Show(text, "MilookDeck - エラー"); } catch { }
    }
}
