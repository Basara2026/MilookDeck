using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;
namespace MilookDeck.App;

/// <summary>
/// 実行ファイル(exe など)のアイコンを取り出して、WPF で表示できる画像にする。
/// まず指定の大きさに近いアイコンを取り出し(4K など高い表示倍率でもぼやけないように大きめ)、取れなければ、Windows が関連付けたアイコンにする。
/// </summary>
static class IconLoader
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern uint PrivateExtractIcons(string file, int index, int cx, int cy, IntPtr[] icons, uint[] ids, uint count, uint flags);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr hIcon);

    static readonly Dictionary<string, ImageSource?> cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>取り出したアイコンの記憶を捨てる(設定を適用するたびに、取り出し直すため)。</summary>
    public static void Clear() => cache.Clear();

    /// <summary>実行ファイルのアイコンを返す。ファイルがない・アイコンが取れないときは null。</summary>
    public static ImageSource? Load(string path, int sizePx)
    {
        path = path.Trim();
        if (path.Length == 0) return null;
        if (cache.TryGetValue(path, out var hit)) return hit;
        ImageSource? img = null;
        try { if (File.Exists(path)) img = FromPrivateExtract(path, sizePx) ?? FromAssociated(path); }
        catch { /* 取れないときは、アイコンなし */ }
        cache[path] = img;
        return img;
    }

    static ImageSource? FromPrivateExtract(string path, int sizePx)
    {
        var icons = new IntPtr[1];
        var ids = new uint[1];
        uint n = PrivateExtractIcons(path, 0, sizePx, sizePx, icons, ids, 1, 0);
        if (n == 0 || n == 0xFFFFFFFF || icons[0] == IntPtr.Zero) return null;
        try
        {
            var src = Imaging.CreateBitmapSourceFromHIcon(icons[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        finally { DestroyIcon(icons[0]); }
    }

    static ImageSource? FromAssociated(string path)
    {
        using var ico = System.Drawing.Icon.ExtractAssociatedIcon(path);
        if (ico == null) return null;
        var src = Imaging.CreateBitmapSourceFromHIcon(ico.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        src.Freeze();
        return src;
    }
}
