using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
namespace MilookDeck.App;

/// <summary>
/// Windows へのサインイン時に MilookDeck を自動で起動する設定(スタートアップへの登録)。
/// 「スタートアップ」フォルダ(スタートメニュー内。現在のユーザー用)に、この exe へのショートカットを作る/消すことで行う。
/// 管理者権限は不要。登録の有無は、設定ファイルではなく、ショートカットがあるかどうかを正とする。
/// </summary>
static class StartupRegistration
{
    const string ShortcutName = "MilookDeck.lnk";

    // 以前の版が使っていた、レジストリ(Run)での登録。引き継ぎと後始末のために残している
    const string LegacyRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string LegacyValueName = "MilookDeck";

    static string ShortcutPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), ShortcutName);

    /// <summary>スタートアップに登録されているか。以前の版のレジストリでの登録も、登録済みとして扱う(「OK」「適用」でショートカットに切り替わる)。</summary>
    public static bool IsRegistered()
    {
        try { if (File.Exists(ShortcutPath)) return true; } catch { }
        return LegacyRegistered();
    }

    /// <summary>登録する/解除する。登録時は、いま動いている exe を指すショートカットを作り直す(exe を移動した後でも、新しい場所に直る)。</summary>
    public static void Set(bool enable)
    {
        RemoveLegacy();   // 二重に起動しないよう、以前の版の登録は、いつも消す
        try
        {
            if (enable) CreateShortcut();
            else if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);
        }
        catch { /* 作れない・消せない環境では何もしない */ }
    }

    static void CreateShortcut()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;
        var type = Type.GetTypeFromProgID("WScript.Shell");   // Windows 標準のショートカット作成機能
        if (type == null) return;
        object? shell = null, link = null;
        try
        {
            shell = Activator.CreateInstance(type);
            dynamic sh = shell!;
            link = sh.CreateShortcut(ShortcutPath);
            dynamic sc = link!;
            sc.TargetPath = exe;
            sc.WorkingDirectory = Path.GetDirectoryName(exe) ?? "";
            sc.Description = "MilookDeck";
            sc.IconLocation = exe + ",0";
            sc.Save();
        }
        finally
        {
            if (link != null) Marshal.FinalReleaseComObject(link);
            if (shell != null) Marshal.FinalReleaseComObject(shell);
        }
    }

    static bool LegacyRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(LegacyRunKey, false);
            return key?.GetValue(LegacyValueName) != null;
        }
        catch { return false; }
    }

    static void RemoveLegacy()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(LegacyRunKey, true);
            key?.DeleteValue(LegacyValueName, false);
        }
        catch { }
    }
}
