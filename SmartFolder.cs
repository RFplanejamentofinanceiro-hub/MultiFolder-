using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace DesktopFolders;

/// <summary>
/// "Pasta inteligente" = um ATALHO (.lnk) comum na Área de Trabalho + um arquivo de dados na pasta do app.
/// O atalho abre o DesktopFolders com o arquivo de dados (duplo clique sempre funciona, inclusive no OneDrive) e
/// usa um ícone (.ico) gerado pelo programa com os apps de dentro. Nada de tipo de arquivo próprio no Windows.
/// </summary>
public static class SmartFolder
{
    /// <summary>Extensão do ARQUIVO DE DADOS (fica em %APPDATA%\DesktopFolders\Pastas).</summary>
    public const string Ext = ".0pasta";
    private const string LegacyExt = ".pastainteligente";
    private const string ProgId = "DesktopFolders.PastaInteligente";

    [DllImport("shell32.dll")] private static extern void SHChangeNotify(int eventId, uint flags, IntPtr a, IntPtr b);

    // ===================== Pastas do app =====================
    private static string AppDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopFolders");
    public static string DataDir => Path.Combine(AppDir, "Pastas");
    /// <summary>
    /// Ícones gerados. Ficam em ProgramData (e NÃO em AppData): no Windows 11 o processo que desenha os ícones da
    /// Área de Trabalho não consegue ler pastas novas dentro de AppData e mostra uma página em branco.
    /// </summary>
    public static string IconDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DesktopFolders", "Icones");
    private static string OldIconDir => Path.Combine(AppDir, "Icones");   // onde as primeiras versões 1.2 guardavam
    public static string LegacyDir => Path.Combine(DataDir, "legado");

    /// <summary>
    /// Onde ficam os atalhos tirados da Área de Trabalho: uma pasta no Menu Iniciar do usuário, para que
    /// continuem aparecendo no Iniciar e na busca do Windows.
    /// </summary>
    public static string ShortcutsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Pastas inteligentes");
    /// <summary>Onde as versões até a 1.2.1 guardavam os atalhos (fora do alcance da busca).</summary>
    public static string OldShortcutsDir => Path.Combine(AppDir, "Atalhos");

    /// <summary>Atalho (ou pasta de atalhos) já está no Menu Iniciar do usuário ou de todos?</summary>
    public static bool IsInStartMenu(string path)
    {
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                                     Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms) })
            if (!string.IsNullOrEmpty(root) && path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    public static string UniqueIn(string dir, string fileName)
    {
        Directory.CreateDirectory(dir);
        string dest = Path.Combine(dir, fileName);
        string stem = Path.GetFileNameWithoutExtension(fileName), ext = Path.GetExtension(fileName);
        for (int i = 2; File.Exists(dest); i++) dest = Path.Combine(dir, $"{stem} ({i}){ext}");
        return dest;
    }

    /// <summary>Leva para o Menu Iniciar os atalhos que estavam na pasta antiga do app. Devolve quantos moveu.</summary>
    public static int RelocateOldShortcuts(IEnumerable<Entry> items)
    {
        int moved = 0;
        string old = OldShortcutsDir.TrimEnd('\\') + "\\";
        foreach (var e in items)
        {
            moved += RelocateOldShortcuts(e.Items);
            if (e.Path == null || !e.Path.StartsWith(old, StringComparison.OrdinalIgnoreCase) || !File.Exists(e.Path)) continue;
            try
            {
                string dest = UniqueIn(ShortcutsDir, Path.GetFileName(e.Path));
                File.Move(e.Path, dest);
                e.Path = dest;
                moved++;
            }
            catch { /* fica onde está e continua abrindo pela pasta */ }
        }
        return moved;
    }

    /// <summary>Migra todas as pastas inteligentes salvas. Pode ser chamado várias vezes.</summary>
    public static int RelocateOldShortcutsAll()
    {
        int total = 0;
        if (!Directory.Exists(OldShortcutsDir) || !Directory.Exists(DataDir)) return 0;
        foreach (var f in Directory.GetFiles(DataDir, "*" + Ext))
        {
            try
            {
                var data = Load(f);
                int n = RelocateOldShortcuts(data.Items);
                if (n > 0) { Save(f, data); total += n; }
            }
            catch { }
        }
        if (total > 0) SHChangeNotify(0x08000000 /*SHCNE_ASSOCCHANGED*/, 0, IntPtr.Zero, IntPtr.Zero);
        return total;
    }

    public static string[] DesktopDirs => new[]
    {
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
    }.Where(d => !string.IsNullOrEmpty(d) && Directory.Exists(d)).Distinct().ToArray();

    // ===================== Registro (somente usuário atual, sem administrador) =====================
    /// <summary>Registra os comandos do botão direito e o abridor de arquivos. Pode ser chamado várias vezes.</summary>
    public static void Register()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;

            // arquivo de dados (.0pasta): só para abrir direto, se alguém tiver um
            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + Ext)) k.SetValue("", ProgId);
            using (var p = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ProgId))
            {
                p.SetValue("", "Pasta inteligente (dados)");
                p.SetValue("FriendlyTypeName", "Pasta inteligente (dados)");
                using (var i = p.CreateSubKey("DefaultIcon")) i.SetValue("", $"\"{exe}\",0");
                using (var c = p.CreateSubKey(@"shell\open\command")) c.SetValue("", $"\"{exe}\" \"%1\"");
            }
            // versões antigas registravam o item no menu Novo; o atalho substitui isso
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + Ext + @"\ShellNew", false);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + Ext + @"\ShellEx", false);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + LegacyExt, false);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + ProgId + @"\shellex", false);

            // Botão direito no fundo de qualquer pasta/Área de Trabalho (menu clássico): "Nova pasta inteligente"
            using (var nv = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Directory\Background\shell\DesktopFolders.NovaPasta"))
            {
                nv.SetValue("", "Nova pasta inteligente");
                nv.SetValue("Icon", $"\"{exe}\",0");
                using var nc = nv.CreateSubKey("command");
                nc.SetValue("", $"\"{exe}\" --criar-aqui \"%V\"");
            }

            // Pacote para compartilhar (.dfpasta): duplo clique importa a pasta na Área de Trabalho
            using (var pk = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + Package.Ext)) pk.SetValue("", "DesktopFolders.Pacote");
            using (var pp = Registry.CurrentUser.CreateSubKey(@"Software\Classes\DesktopFolders.Pacote"))
            {
                pp.SetValue("", "Pacote de pasta inteligente");
                pp.SetValue("FriendlyTypeName", "Pacote de pasta inteligente");
                using (var i = pp.CreateSubKey("DefaultIcon")) i.SetValue("", $"\"{exe}\",0");
                using (var c = pp.CreateSubKey(@"shell\open\command")) c.SetValue("", $"\"{exe}\" --importar \"%1\"");
            }

            // Clique direito em atalhos/programas → "Adicionar à pasta inteligente..."
            foreach (var type in new[] { "lnkfile", "exefile" })
            {
                using var v = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + type + @"\shell\DesktopFolders.Add");
                v.SetValue("", "Adicionar à pasta inteligente...");
                v.SetValue("Icon", $"\"{exe}\",0");
                using var cmd = v.CreateSubKey("command");
                cmd.SetValue("", $"\"{exe}\" --adicionar \"%1\"");
            }
            SHChangeNotify(0x08000000 /*SHCNE_ASSOCCHANGED*/, 0, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            try
            {
                Directory.CreateDirectory(AppDir);
                File.AppendAllText(Path.Combine(AppDir, "erro-registro.txt"), $"{DateTime.Now:s} {ex}\n");
            }
            catch { }
        }
    }

    public static void Unregister()
    {
        try
        {
            foreach (var type in new[] { "lnkfile", "exefile" })
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + type + @"\shell\DesktopFolders.Add", false);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + Ext, false);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + LegacyExt, false);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + ProgId, false);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + Package.Ext, false);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\DesktopFolders.Pacote", false);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Directory\Background\shell\DesktopFolders.NovaPasta", false);
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
        }
        catch { }
    }

    /// <summary>
    /// Limpa o registro de máquina (administrador) que versões 1.1.x faziam para o menu Novo e a miniatura.
    /// Não é mais necessário; o atalho da pasta funciona sem nada disso.
    /// </summary>
    public static string UnregisterThumbnailMachine()
    {
        try
        {
            Registry.LocalMachine.DeleteSubKeyTree(@"Software\Classes\CLSID\{7B3F1E52-9A0C-4D6B-8E21-5C4D7A9E1F30}", false);
            Registry.LocalMachine.DeleteSubKeyTree(@"Software\Classes\" + Ext, false);
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
            return "Registro antigo de máquina removido (menu Novo e miniatura).";
        }
        catch (UnauthorizedAccessException) { return "Sem permissão: execute como administrador."; }
    }

    // ===================== Dados (JSON) =====================
    public static Layout Load(string file)
    {
        try
        {
            if (File.Exists(file) && new FileInfo(file).Length > 0)
                return JsonSerializer.Deserialize<Layout>(File.ReadAllText(file)) ?? new Layout();
        }
        catch { /* arquivo vazio/corrompido: abre vazio */ }
        return new Layout();
    }

    /// <summary>Grava os dados; se for uma pasta do app, atualiza o ícone dos atalhos dela.</summary>
    public static void Save(string file, Layout data)
    {
        try { File.WriteAllText(file, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true })); }
        catch { return; }

        if (file.StartsWith(DataDir, StringComparison.OrdinalIgnoreCase)) RefreshLinks(file, data);
        else NotifyChanged(file, created: false);
    }

    // ===================== Atalhos (.lnk) =====================
    private static readonly Dictionary<string, (DateTime mtime, string target, string args)> LinkCache = new(StringComparer.OrdinalIgnoreCase);

    private static dynamic NewShell()
    {
        var t = Type.GetTypeFromProgID("WScript.Shell");
        return Activator.CreateInstance(t!)!;
    }

    private static (string target, string args)? ReadLink(string lnk)
    {
        try
        {
            var mtime = File.GetLastWriteTimeUtc(lnk);
            if (LinkCache.TryGetValue(lnk, out var c) && c.mtime == mtime) return (c.target, c.args);
            dynamic sh = NewShell();
            dynamic l = sh.CreateShortcut(lnk);
            string target = l.TargetPath ?? "", args = l.Arguments ?? "";
            LinkCache[lnk] = (mtime, target, args);
            return (target, args);
        }
        catch { return null; }
    }

    /// <summary>Se <paramref name="path"/> é uma pasta inteligente (atalho nosso, ou arquivo de dados), devolve o arquivo de dados.</summary>
    public static bool TryGetDataFile(string path, out string dataFile)
    {
        dataFile = "";
        if (path.EndsWith(Ext, StringComparison.OrdinalIgnoreCase) && File.Exists(path)) { dataFile = path; return true; }
        if (!path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) return false;
        var info = ReadLink(path);
        if (info == null) return false;
        string arg = info.Value.args.Trim().Trim('"');
        if (arg.EndsWith(Ext, StringComparison.OrdinalIgnoreCase) &&
            info.Value.target.EndsWith("DesktopFolders.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(arg))
        {
            dataFile = arg;
            return true;
        }
        return false;
    }

    public sealed record SmartLink(string Name, string Lnk, string DataFile);

    /// <summary>Pastas inteligentes que estão na Área de Trabalho (atalhos nossos).</summary>
    public static List<SmartLink> ListOnDesktop()
    {
        var list = new List<SmartLink>();
        foreach (var dir in DesktopDirs)
            foreach (var lnk in Directory.GetFiles(dir, "*.lnk"))
                if (TryGetDataFile(lnk, out var data)) list.Add(new SmartLink(Path.GetFileNameWithoutExtension(lnk), lnk, data));
        return list.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static List<string> FindLinks(string dataFile) =>
        ListOnDesktop().Where(x => string.Equals(x.DataFile, dataFile, StringComparison.OrdinalIgnoreCase)).Select(x => x.Lnk).ToList();

    /// <summary>Nome para mostrar: o do atalho na Área de Trabalho (que o usuário pode renomear).</summary>
    public static string DisplayName(string dataFile, Layout data)
    {
        try { var l = FindLinks(dataFile).FirstOrDefault(); if (l != null) return Path.GetFileNameWithoutExtension(l); } catch { }
        return string.IsNullOrWhiteSpace(data.Title) ? "Pasta inteligente" : data.Title!;
    }

    private static string SafeName(string name)
    {
        string safe = string.Concat(name.Split(Path.GetInvalidFileNameChars())).Trim();
        return safe.Length == 0 ? "Pasta inteligente" : safe;
    }

    private static string UniquePath(string dir, string name, string ext)
    {
        string p = Path.Combine(dir, name + ext);
        for (int i = 2; File.Exists(p); i++) p = Path.Combine(dir, $"{name} ({i}){ext}");
        return p;
    }

    private static string MakeIcon(string dataFile, Layout data)
    {
        string id = Path.GetFileNameWithoutExtension(dataFile);
        Directory.CreateDirectory(IconDir);
        foreach (var old in Directory.GetFiles(IconDir, id + "-*.ico")) { try { File.Delete(old); } catch { } }
        if (Directory.Exists(OldIconDir))
            foreach (var old in Directory.GetFiles(OldIconDir, id + "-*.ico")) { try { File.Delete(old); } catch { } }
        string ico = Path.Combine(IconDir, $"{id}-{DateTime.UtcNow.Ticks}.ico");   // nome novo: o Explorador refaz o ícone
        try { SmartFolderIcon.Create(data.Items, data.Accent, ico); }
        catch { /* sem ícone gerado: o atalho usa o ícone do programa */ }
        return File.Exists(ico) ? ico : "";
    }

    private static void WriteLink(string lnk, string dataFile, string ico)
    {
        string exe = Environment.ProcessPath ?? "";
        dynamic sh = NewShell();
        dynamic l = sh.CreateShortcut(lnk);
        l.TargetPath = exe;
        l.Arguments = "\"" + dataFile + "\"";
        l.WorkingDirectory = Path.GetDirectoryName(exe) ?? "";
        l.Description = "Pasta inteligente";
        l.IconLocation = (ico.Length > 0 ? ico : exe) + ",0";
        l.Save();
        LinkCache.Remove(lnk);
    }

    private static void RefreshLinks(string dataFile, Layout data)
    {
        try
        {
            var links = FindLinks(dataFile);
            if (links.Count == 0) return;
            string ico = MakeIcon(dataFile, data);
            foreach (var lnk in links)
            {
                WriteLink(lnk, dataFile, ico);
                NotifyChanged(lnk, created: false);
            }
        }
        catch { }
    }

    /// <summary>Cria uma pasta inteligente (atalho em <paramref name="dir"/> + dados). Retorna o caminho do atalho.</summary>
    public static string CreateNew(string dir, string name, Layout? data = null)
    {
        data ??= new Layout();
        data.Title = name;
        Directory.CreateDirectory(DataDir);
        string dataFile = Path.Combine(DataDir, Guid.NewGuid().ToString("N") + Ext);
        File.WriteAllText(dataFile, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));

        string ico = MakeIcon(dataFile, data);
        string lnk = UniquePath(dir, SafeName(name), ".lnk");
        WriteLink(lnk, dataFile, ico);
        NotifyChanged(lnk, created: true);
        return lnk;
    }

    /// <summary>Apaga a pasta (atalho, dados e ícone). Os atalhos de dentro NÃO são devolvidos aqui.</summary>
    public static void Delete(string lnkOrData)
    {
        try
        {
            if (!TryGetDataFile(lnkOrData, out var data)) return;
            foreach (var l in FindLinks(data)) { try { File.Delete(l); } catch { } }
            string id = Path.GetFileNameWithoutExtension(data);
            try { File.Delete(data); } catch { }
            if (Directory.Exists(IconDir)) foreach (var i in Directory.GetFiles(IconDir, id + "-*.ico")) { try { File.Delete(i); } catch { } }
        }
        catch { }
    }

    /// <summary>
    /// Converte arquivos .0pasta soltos na Área de Trabalho (formato das versões 1.1.x, que o Windows 11 não abre
    /// por duplo clique) em pastas inteligentes novas (atalho + dados). O original vai para Pastas\legado.
    /// </summary>
    public static int MigrateLegacy()
    {
        int n = 0;
        foreach (var dir in DesktopDirs)
        {
            string[] files;
            try { files = Directory.GetFiles(dir, "*" + Ext); } catch { continue; }
            foreach (var f in files)
            {
                try
                {
                    var data = Load(f);
                    string name = Path.GetFileNameWithoutExtension(f);
                    string lnk = CreateNew(dir, name, data);
                    Directory.CreateDirectory(LegacyDir);
                    string keep = UniquePath(LegacyDir, name, Ext);
                    File.Move(f, keep);
                    n++;
                }
                catch { /* tenta de novo na próxima vez */ }
            }
        }
        return n;
    }

    /// <summary>Avisa o Explorador que o item mudou, para ele refazer o ícone na hora.</summary>
    public static void NotifyChanged(string file, bool created)
    {
        IntPtr p = Marshal.StringToHGlobalUni(file);
        try
        {
            if (created) SHChangeNotify(0x00000002 /*SHCNE_CREATE*/, 0x0005 /*SHCNF_PATHW*/, p, IntPtr.Zero);
            SHChangeNotify(0x00002000 /*SHCNE_UPDATEITEM*/, 0x0005, p, IntPtr.Zero);
        }
        finally { Marshal.FreeHGlobal(p); }
    }
}
