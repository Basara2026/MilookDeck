using Microsoft.Win32;
namespace MilookDeck.App;

/// <summary>
/// Windows へのサインイン時に MilookDeck を自動で起動する設定(スタートアップへの登録)。
/// 現在のユーザーの「Run」のレジストリに、この exe のパスを書き込む(管理者権限は不要)。
/// 登録の有無は、設定ファイルではなく、レジストリの状態を正とする。
/// </summary>
static class StartupRegistration
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "MilookDeck";

    /// <summary>スタートアップに登録されているか。</summary>
    public static bool IsRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            return key?.GetValue(ValueName) != null;
        }
        catch { return false; }
    }

    /// <summary>登録する/解除する。登録時は、いま動いている exe のパスで書き込む(exe を移動した後でも、新しい場所に直る)。</summary>
    public static void Set(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
            if (key == null) return;
            if (enable)
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe)) key.SetValue(ValueName, $"\"{exe}\"", RegistryValueKind.String);
            }
            else key.DeleteValue(ValueName, false);
        }
        catch { /* 書き込めない環境では何もしない */ }
    }
}
