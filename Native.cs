using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopFolders;

public static class Native
{
    // ---------- Ícones (mesmo mecanismo do Explorer) ----------
    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx; public int cy; }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        string pszPath, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);

    public static ImageSource? GetIcon(string path, int size = 96)
    {
        try
        {
            string src = path;

            // Atalho (.lnk): usa o ícone do alvo para não aparecer a setinha de atalho.
            if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var t = Type.GetTypeFromProgID("WScript.Shell");
                    dynamic sh = Activator.CreateInstance(t!)!;
                    dynamic lnk = sh.CreateShortcut(path);
                    string target = lnk.TargetPath;
                    if (!string.IsNullOrEmpty(target) && (File.Exists(target) || Directory.Exists(target)))
                        src = target;
                }
                catch { /* mantém o .lnk */ }
            }

            var iid = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");
            SHCreateItemFromParsingName(src, IntPtr.Zero, ref iid, out var factory);
            // 0x1 = BIGGERSIZEOK, 0x4 = ICONONLY
            int hr = factory.GetImage(new SIZE { cx = size, cy = size }, 0x1 | 0x4, out IntPtr hbm);
            if (hr != 0 || hbm == IntPtr.Zero) return FallbackIcon(src);
            try
            {
                var bmp = Imaging.CreateBitmapSourceFromHBitmap(hbm, IntPtr.Zero, Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                bmp.Freeze();
                return bmp;
            }
            finally { DeleteObject(hbm); }
        }
        catch
        {
            return FallbackIcon(path);
        }
    }

    /// <summary>Plano B: ícone associado clássico (quando o Shell não entrega a imagem grande).</summary>
    private static ImageSource? FallbackIcon(string path)
    {
        try
        {
            using var ico = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (ico == null) return null;
            var bmp = Imaging.CreateBitmapSourceFromHIcon(ico.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    // ---------- Ícones padrão da área de trabalho (mostrar/ocultar) ----------
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);

    public static IntPtr FindDefView()
    {
        IntPtr progman = FindProgman();
        IntPtr def = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (def == IntPtr.Zero)
        {
            IntPtr w = IntPtr.Zero;
            do
            {
                w = FindWindowEx(IntPtr.Zero, w, "WorkerW", null);
                if (w != IntPtr.Zero) def = FindWindowEx(w, IntPtr.Zero, "SHELLDLL_DefView", null);
            } while (def == IntPtr.Zero && w != IntPtr.Zero);
        }
        return def;
    }

    public static void SetDesktopIcons(bool show)
    {
        IntPtr def = FindDefView();
        if (def == IntPtr.Zero) return;
        IntPtr lv = FindWindowEx(def, IntPtr.Zero, "SysListView32", null);
        bool visible = lv != IntPtr.Zero && IsWindowVisible(lv);
        if (visible != show)
            SendMessage(def, 0x0111 /*WM_COMMAND*/, (IntPtr)0x7402 /*alternar ícones*/, IntPtr.Zero);
    }

    // ---------- Janela fixa no fundo ----------
    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWPOS
    {
        public IntPtr hwnd, hwndInsertAfter;
        public int x, y, cx, cy;
        public uint flags;
    }

    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr h, int index, int value);
    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
    private delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr l);

    /// <summary>Acha a janela do papel de parede (Progman); se FindWindow falhar, varre as janelas.</summary>
    public static IntPtr FindProgman()
    {
        IntPtr p = FindWindow("Progman", null);
        if (p != IntPtr.Zero) return p;
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            var sb = new System.Text.StringBuilder(64);
            GetClassName(h, sb, 64);
            if (sb.ToString() == "Progman") { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Janela imediatamente acima do papel de parede na pilha (onde nossa camada deve ficar logo acima).</summary>
    public static IntPtr WindowAboveWallpaper()
    {
        IntPtr prog = FindProgman();
        return prog == IntPtr.Zero ? HWND_BOTTOM : GetWindow(prog, 3 /*GW_HWNDPREV*/);
    }

    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x80;
    public const int WM_WINDOWPOSCHANGING = 0x46;
    public static readonly IntPtr HWND_BOTTOM = (IntPtr)1;
}
