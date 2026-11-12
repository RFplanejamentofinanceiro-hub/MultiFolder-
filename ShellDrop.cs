using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows;
using IDataObject = System.Windows.IDataObject;

namespace DesktopFolders;

/// <summary>
/// Itens arrastados do Menu Iniciar (e outros locais do Shell) não trazem arquivo, só "Shell IDList Array".
/// Aqui viram caminhos: arquivo real quando existe, ou um atalho .lnk criado para o item (ex.: app da Store).
/// </summary>
internal static class ShellDrop
{
    public const string Format = "Shell IDList Array";

    public static bool HasItems(IDataObject d) =>
        d.GetDataPresent(DataFormats.FileDrop) || d.GetDataPresent(Format);

    /// <summary>Caminhos para adicionar à pasta. Atalhos novos são criados em <paramref name="shortcutDir"/>.</summary>
    public static string[] GetPaths(IDataObject d, string shortcutDir)
    {
        if (d.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0) return files;
        var result = new List<string>();
        if (d.GetData(Format) is not MemoryStream ms) return result.ToArray();
        byte[] b = ms.ToArray();
        if (b.Length < 8) return result.ToArray();
        int count = BitConverter.ToInt32(b, 0);
        if (count <= 0 || b.Length < 4 * (count + 2)) return result.ToArray();

        IntPtr buf = Marshal.AllocHGlobal(b.Length);
        try
        {
            Marshal.Copy(b, 0, buf, b.Length);
            IntPtr parent = buf + BitConverter.ToInt32(b, 4);
            for (int i = 0; i < count; i++)
            {
                IntPtr child = buf + BitConverter.ToInt32(b, 4 * (i + 2));
                IntPtr abs = ILCombine(parent, child);
                if (abs == IntPtr.Zero) continue;
                try
                {
                    string? p = PathFor(abs, shortcutDir);
                    if (p != null) result.Add(p);
                }
                catch { }
                finally { ILFree(abs); }
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
        return result.ToArray();
    }

    private static string? PathFor(IntPtr pidl, string shortcutDir)
    {
        // item do sistema de arquivos (ex.: atalho da pasta do Menu Iniciar): usa o próprio arquivo
        var sb = new System.Text.StringBuilder(1024);
        if (SHGetPathFromIDListW(pidl, sb) && (File.Exists(sb.ToString()) || Directory.Exists(sb.ToString())))
        {
            string fs = sb.ToString();
            // atalho que já está no Menu Iniciar: usa o próprio (copiar duplicaria o app na busca)
            if (SmartFolder.IsInStartMenu(fs)) return fs;
            // outro atalho solto: copia, para não tirar do lugar de origem
            if (fs.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || fs.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
                return CopyUnique(fs, shortcutDir);
            return fs;
        }

        // app virtual (Store, "Todos os aplicativos"): cria um atalho para o item
        string name = "App";
        if (SHGetNameFromIDList(pidl, 0 /*SIGDN_NORMALDISPLAY*/, out IntPtr pName) == 0 && pName != IntPtr.Zero)
        {
            name = Marshal.PtrToStringUni(pName) ?? name;
            Marshal.FreeCoTaskMem(pName);
        }
        Directory.CreateDirectory(shortcutDir);
        string dest = Unique(Path.Combine(shortcutDir, Safe(name) + ".lnk"));
        var link = (IShellLinkW)new ShellLink();
        link.SetIDList(pidl);
        ((IPersistFile)link).Save(dest, true);
        Marshal.ReleaseComObject(link);
        return File.Exists(dest) ? dest : null;
    }

    private static string CopyUnique(string src, string dir)
    {
        Directory.CreateDirectory(dir);
        string dest = Unique(Path.Combine(dir, Path.GetFileName(src)));
        File.Copy(src, dest);
        return dest;
    }

    private static string Unique(string path)
    {
        string dir = Path.GetDirectoryName(path)!, n = Path.GetFileNameWithoutExtension(path), e = Path.GetExtension(path);
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(dir, $"{n} ({i}){e}");
        return path;
    }

    private static string Safe(string n)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
        return string.IsNullOrWhiteSpace(n) ? "App" : n.Trim();
    }

    /// <summary>Diagnóstico: monta um "Shell IDList Array" como o do Menu Iniciar e converte em caminhos.</summary>
    public static string[] SelfTest(string[] parsingNames, string dir)
    {
        var pidls = new List<byte[]>();
        foreach (var n in parsingNames)
        {
            if (SHParseDisplayName(n, IntPtr.Zero, out IntPtr p, 0, out _) != 0 || p == IntPtr.Zero) continue;
            int size = (int)ILGetSize(p);
            var bytes = new byte[size];
            Marshal.Copy(p, bytes, 0, size);
            pidls.Add(bytes);
            Marshal.FreeCoTaskMem(p);
        }
        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        int header = 4 * (pidls.Count + 2);
        w.Write(pidls.Count);
        int off = header; w.Write(off); off += 2;          // pasta-mãe = Área de Trabalho (pidl vazio)
        foreach (var b in pidls) { w.Write(off); off += b.Length; }
        w.Write((short)0);
        foreach (var b in pidls) w.Write(b);
        var data = new DataObject();
        data.SetData(Format, new MemoryStream(ms.ToArray()));
        return GetPaths(data, dir);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHParseDisplayName(string name, IntPtr bc, out IntPtr pidl, uint sfgaoIn, out uint sfgaoOut);
    [DllImport("shell32.dll")] private static extern uint ILGetSize(IntPtr pidl);
    [DllImport("shell32.dll")] private static extern IntPtr ILCombine(IntPtr a, IntPtr b);
    [DllImport("shell32.dll")] private static extern void ILFree(IntPtr p);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool SHGetPathFromIDListW(IntPtr pidl, System.Text.StringBuilder path);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHGetNameFromIDList(IntPtr pidl, int sigdn, out IntPtr name);

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")] private class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath(IntPtr a, int b, IntPtr c, int d);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        // demais métodos não usados
    }
}
