using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace DesktopFolders;

/// <summary>
/// Menu de contexto NATIVO do Windows para um arquivo/atalho (Abrir, Abrir local do arquivo, Propriedades,
/// itens de outros programas...), com as opções do DesktopFolders acrescentadas no mesmo menu.
/// </summary>
public static class ShellMenu
{
    // ---------- COM ----------
    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        void ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string name, ref uint eaten, out IntPtr pidl, ref uint attrs);
        void EnumObjects(IntPtr hwnd, int flags, out IntPtr enumIdList);
        void BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        void BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int CompareIDs(IntPtr lparam, IntPtr pidl1, IntPtr pidl2);
        void CreateViewObject(IntPtr hwnd, ref Guid riid, out IntPtr ppv);
        void GetAttributesOf(uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref uint attrs);
        void GetUIObjectOf(IntPtr hwnd, uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref Guid riid, IntPtr reserved, out IntPtr ppv);
        void GetDisplayNameOf(IntPtr pidl, uint flags, IntPtr strret);
        void SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string name, uint flags, out IntPtr pidlOut);
    }

    [ComImport, Guid("000214E4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint index, uint idFirst, uint idLast, uint flags);
        [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
        [PreserveSig] int GetCommandString(UIntPtr idCmd, uint type, IntPtr reserved, IntPtr name, uint cch);
    }

    [ComImport, Guid("000214F4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu2 : IContextMenu
    {
        [PreserveSig] new int QueryContextMenu(IntPtr hmenu, uint index, uint idFirst, uint idLast, uint flags);
        [PreserveSig] new int InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
        [PreserveSig] new int GetCommandString(UIntPtr idCmd, uint type, IntPtr reserved, IntPtr name, uint cch);
        [PreserveSig] int HandleMenuMsg(uint msg, IntPtr wParam, IntPtr lParam);
    }

    [ComImport, Guid("000214E8-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellExtInit
    {
        [PreserveSig] int Initialize(IntPtr pidlFolder, IntPtr dataObject, IntPtr hkeyProgId);
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CMINVOKECOMMANDINFOEX
    {
        public int cbSize, fMask;
        public IntPtr hwnd, lpVerb, lpParameters, lpDirectory;
        public int nShow, dwHotKey;
        public IntPtr hIcon, lpTitle, lpVerbW, lpParametersW, lpDirectoryW, lpTitleW;
        public POINT ptInvoke;
    }

    // ---------- Win32 ----------
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHParseDisplayName(string name, IntPtr bc, out IntPtr pidl, uint attrIn, out uint attrOut);
    [DllImport("shell32.dll")] private static extern int SHBindToParent(IntPtr pidl, ref Guid riid, out IntPtr ppv, out IntPtr pidlLast);
    [DllImport("shell32.dll")] private static extern int SHGetSpecialFolderLocation(IntPtr hwnd, int csidl, out IntPtr pidl);
    [DllImport("ole32.dll")] private static extern void CoTaskMemFree(IntPtr p);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool InsertMenu(IntPtr h, uint pos, uint flags, UIntPtr id, string? text);
    [DllImport("user32.dll")] private static extern bool DeleteMenu(IntPtr h, uint pos, uint flags);
    [DllImport("user32.dll")] private static extern int GetMenuItemCount(IntPtr h);
    [DllImport("user32.dll")] private static extern uint GetMenuItemID(IntPtr h, int pos);
    [DllImport("user32.dll")] private static extern uint GetMenuState(IntPtr h, uint pos, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetSubMenu(IntPtr h, int pos);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetMenuString(IntPtr h, uint pos, StringBuilder sb, int max, uint flags);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenuEx(IntPtr h, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern short GetKeyState(int key);
    [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();

    private const uint MF_STRING = 0, MF_SEPARATOR = 0x800, MF_BYPOSITION = 0x400;
    private const uint TPM_RETURNCMD = 0x100, TPM_RIGHTBUTTON = 0x2;
    private const uint FirstShellId = 1000, LastShellId = 0x7FFF;
    private const int WM_INITMENUPOPUP = 0x117, WM_DRAWITEM = 0x2B, WM_MEASUREITEM = 0x2C;

    private static readonly Guid IID_IShellFolder = new("000214E6-0000-0000-C000-000000000046");
    private static readonly Guid IID_IContextMenu = new("000214E4-0000-0000-C000-000000000046");

    private static IContextMenu2? _cm2;

    private static IntPtr Hook(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (_cm2 != null && (msg == WM_INITMENUPOPUP || msg == WM_DRAWITEM || msg == WM_MEASUREITEM))
        {
            try { _cm2.HandleMenuMsg((uint)msg, w, l); } catch { }
            handled = true;
            return msg == WM_INITMENUPOPUP ? IntPtr.Zero : (IntPtr)1;
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Mostra o menu do Windows para <paramref name="path"/> (se houver) seguido das opções extras.
    /// A posição é a do mouse. As opções que mexem no arquivo real (excluir, recortar, renomear) são ocultadas.
    /// </summary>
    public static void Show(Window owner, string? path, IList<(string Text, Action Run)> extras)
    {
        IntPtr hwnd = new WindowInteropHelper(owner).Handle;
        IntPtr hmenu = CreatePopupMenu();
        IntPtr pidl = IntPtr.Zero;
        IContextMenu? cm = null;
        HwndSource? src = HwndSource.FromHwnd(hwnd);
        HwndSourceHook? hook = null;
        Action? run = null;
        try
        {
            if (!string.IsNullOrEmpty(path)) cm = GetContextMenu(path, hwnd, out pidl);
            if (cm != null)
            {
                bool shift = (GetKeyState(0x10) & 0x8000) != 0;   // Shift = menu estendido (como no Explorer)
                cm.QueryContextMenu(hmenu, 0, FirstShellId, LastShellId, shift ? 0x100u : 0u);
                RemoveVerbs(cm, hmenu);
                CleanSeparators(hmenu);
            }
            if (extras.Count > 0)
            {
                if (GetMenuItemCount(hmenu) > 0) InsertMenu(hmenu, 0xFFFFFFFF, MF_BYPOSITION | MF_SEPARATOR, UIntPtr.Zero, null);
                for (int i = 0; i < extras.Count; i++)
                    InsertMenu(hmenu, 0xFFFFFFFF, MF_BYPOSITION | MF_STRING, (UIntPtr)(uint)(i + 1), extras[i].Text);
            }
            if (GetMenuItemCount(hmenu) == 0) return;

            if (cm is IContextMenu2 cm2 && src != null) { _cm2 = cm2; hook = Hook; src.AddHook(hook); }

            SetForegroundWindow(hwnd);
            GetCursorPos(out POINT pt);
            uint cmd = TrackPopupMenuEx(hmenu, TPM_RETURNCMD | TPM_RIGHTBUTTON, pt.x, pt.y, hwnd, IntPtr.Zero);
            PostMessage(hwnd, 0, IntPtr.Zero, IntPtr.Zero);

            if (cmd >= FirstShellId && cm != null) InvokeShell(cm, cmd - FirstShellId, hwnd, pt);
            else if (cmd >= 1 && cmd <= extras.Count) run = extras[(int)cmd - 1].Run;
        }
        catch (Exception)
        {
            // algum item do shell falhou: não derruba o programa; o menu simplesmente não abre
        }
        finally
        {
            if (hook != null) src?.RemoveHook(hook);
            _cm2 = null;
            DestroyMenu(hmenu);
            if (pidl != IntPtr.Zero) CoTaskMemFree(pidl);
        }
        run?.Invoke();   // só depois de fechar o menu (pode abrir janelas)
    }

    private static IContextMenu? GetContextMenu(string path, IntPtr hwnd, out IntPtr pidl)
    {
        pidl = IntPtr.Zero;
        try
        {
            if (SHParseDisplayName(path, IntPtr.Zero, out pidl, 0, out _) != 0 || pidl == IntPtr.Zero) return null;
            Guid iidSf = IID_IShellFolder;
            if (SHBindToParent(pidl, ref iidSf, out IntPtr psf, out IntPtr last) != 0) return null;
            var sf = (IShellFolder)Marshal.GetObjectForIUnknown(psf);
            Marshal.Release(psf);
            Guid iidCm = IID_IContextMenu;
            sf.GetUIObjectOf(hwnd, 1, new[] { last }, ref iidCm, IntPtr.Zero, out IntPtr pcm);
            var cm = (IContextMenu)Marshal.GetObjectForIUnknown(pcm);
            Marshal.Release(pcm);
            return cm;
        }
        catch { return null; }
    }

    private static void InvokeShell(IContextMenu cm, uint offset, IntPtr hwnd, POINT pt)
    {
        var info = new CMINVOKECOMMANDINFOEX
        {
            cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
            fMask = 0x4000 /*UNICODE*/ | 0x20000000 /*PTINVOKE*/,
            hwnd = hwnd,
            lpVerb = (IntPtr)offset,
            lpVerbW = (IntPtr)offset,
            nShow = 1,
            ptInvoke = pt
        };
        cm.InvokeCommand(ref info);
    }

    private static readonly HashSet<string> HiddenVerbs = new(StringComparer.OrdinalIgnoreCase)
        { "delete", "cut", "rename", "paste", "pastelink" };

    private static void RemoveVerbs(IContextMenu cm, IntPtr hmenu)
    {
        IntPtr buf = Marshal.AllocHGlobal(512);
        try
        {
            for (int pos = GetMenuItemCount(hmenu) - 1; pos >= 0; pos--)
            {
                uint id = GetMenuItemID(hmenu, pos);
                if (id < FirstShellId || id > LastShellId) continue;
                if (cm.GetCommandString((UIntPtr)(id - FirstShellId), 4 /*GCS_VERBW*/, IntPtr.Zero, buf, 256) != 0) continue;
                string? verb = Marshal.PtrToStringUni(buf);
                if (verb != null && HiddenVerbs.Contains(verb)) DeleteMenu(hmenu, (uint)pos, MF_BYPOSITION);
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static bool IsSeparator(IntPtr m, int pos) => (GetMenuState(m, (uint)pos, MF_BYPOSITION) & MF_SEPARATOR) != 0;

    private static void CleanSeparators(IntPtr m)
    {
        bool prevSep = true;                                   // remove separador no início e separadores duplicados
        for (int i = 0; i < GetMenuItemCount(m);)
        {
            bool sep = IsSeparator(m, i);
            if (sep && prevSep) { DeleteMenu(m, (uint)i, MF_BYPOSITION); continue; }
            prevSep = sep; i++;
        }
        int c = GetMenuItemCount(m);
        if (c > 0 && IsSeparator(m, c - 1)) DeleteMenu(m, (uint)(c - 1), MF_BYPOSITION);
    }

    // ---------- Diagnóstico: itens do menu nativo de um arquivo ----------
    public static List<string> ListItemMenu(string path)
    {
        var result = new List<string>();
        IntPtr hmenu = CreatePopupMenu();
        IntPtr pidl = IntPtr.Zero;
        try
        {
            var cm = GetContextMenu(path, GetDesktopWindow(), out pidl);
            if (cm == null) { result.Add("sem menu de contexto para: " + path); return result; }
            int hr = cm.QueryContextMenu(hmenu, 0, FirstShellId, LastShellId, 0);
            result.Add($"QueryContextMenu hr=0x{hr:X}, itens={GetMenuItemCount(hmenu)}");
            RemoveVerbs(cm, hmenu);
            CleanSeparators(hmenu);
            IntPtr verbBuf = Marshal.AllocHGlobal(512);
            for (int i = 0; i < GetMenuItemCount(hmenu); i++)
            {
                var sb = new StringBuilder(256);
                GetMenuString(hmenu, (uint)i, sb, 256, MF_BYPOSITION);
                uint id = GetMenuItemID(hmenu, i);
                string verb = "";
                if (id >= FirstShellId && id <= LastShellId &&
                    cm.GetCommandString((UIntPtr)(id - FirstShellId), 4, IntPtr.Zero, verbBuf, 256) == 0)
                    verb = Marshal.PtrToStringUni(verbBuf) ?? "";
                result.Add(IsSeparator(hmenu, i) ? "  ----------" : $"  [{i}] {sb}   (verbo: {verb})");
            }
            Marshal.FreeHGlobal(verbBuf);
        }
        catch (Exception ex) { result.Add("erro: " + ex.Message); }
        finally { DestroyMenu(hmenu); if (pidl != IntPtr.Zero) CoTaskMemFree(pidl); }
        return result;
    }

    // ---------- Diagnóstico: o que o Windows mostra no menu "Novo" da Área de Trabalho ----------
    public static List<string> ListNewMenu()
    {
        var result = new List<string>();
        IntPtr hmenu = CreatePopupMenu();
        IntPtr pidl = IntPtr.Zero;
        try
        {
            var t = Type.GetTypeFromCLSID(new Guid("D969A300-E7FF-11d0-A93B-00A0C90F2719")); // CLSID_NewMenu
            object obj = Activator.CreateInstance(t!)!;
            SHGetSpecialFolderLocation(IntPtr.Zero, 0x10 /*CSIDL_DESKTOPDIRECTORY*/, out pidl);
            ((IShellExtInit)obj).Initialize(pidl, IntPtr.Zero, IntPtr.Zero);
            var cm2 = (IContextMenu2)obj;
            cm2.QueryContextMenu(hmenu, 0, 1, 0x7FFF, 0);
            int n = GetMenuItemCount(hmenu);
            result.Add($"itens no menu raiz do Novo: {n}");
            for (int i = 0; i < n; i++)
            {
                IntPtr sub = GetSubMenu(hmenu, i);
                if (sub == IntPtr.Zero) continue;
                cm2.HandleMenuMsg(WM_INITMENUPOPUP, sub, (IntPtr)i);
                int sn = GetMenuItemCount(sub);
                result.Add($"submenu com {sn} itens:");
                for (int j = 0; j < sn; j++)
                {
                    var sb = new StringBuilder(256);
                    GetMenuString(sub, (uint)j, sb, 256, MF_BYPOSITION);
                    result.Add($"  [{j}] {(sb.Length > 0 ? sb.ToString() : "(sem texto/separador)")}");
                }
            }
        }
        catch (Exception ex) { result.Add("erro: " + ex.Message); }
        finally { DestroyMenu(hmenu); if (pidl != IntPtr.Zero) CoTaskMemFree(pidl); }
        return result;
    }
}
