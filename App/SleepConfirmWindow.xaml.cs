using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
namespace MilookDeck.App;

/// <summary>
/// スリープの確認。指定した秒数のカウントダウンと、だんだん短くなるバーを表示し、0 になったらスリープする(Confirmed = true で閉じる)。
/// 「すぐにスリープ」で待たずに実行、「キャンセル」(Esc・閉じるボタンも同じ)で中止する。
/// </summary>
public partial class SleepConfirmWindow : Window
{
    readonly double seconds;
    readonly Stopwatch watch = Stopwatch.StartNew();
    readonly DispatcherTimer timer;

    /// <summary>スリープを実行してよいか(「すぐにスリープ」を押した、またはカウントダウンが 0 になった)。</summary>
    public bool Confirmed { get; private set; }

    public SleepConfirmWindow(int seconds)
    {
        InitializeComponent();
        this.seconds = seconds;
        CountBar.Maximum = seconds;
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) => UpdateCount();
        UpdateCount();
        timer.Start();
        Closed += (_, _) => timer.Stop();
    }

    void UpdateCount()
    {
        double remain = Math.Max(0, seconds - watch.Elapsed.TotalSeconds);
        CountBar.Value = remain;
        CountText.Text = $"{(int)Math.Ceiling(remain)} 秒後にスリープします";
        if (remain <= 0) Finish(true);
    }

    void Finish(bool confirmed)
    {
        if (!IsLoaded && !IsVisible) return;
        timer.Stop();
        Confirmed = confirmed;
        Close();
    }

    void SleepNow_Click(object sender, RoutedEventArgs e) => Finish(true);
    void Cancel_Click(object sender, RoutedEventArgs e) => Finish(false);
}
