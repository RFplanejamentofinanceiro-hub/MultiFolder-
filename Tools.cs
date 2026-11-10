using System;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace DesktopFolders;

/// <summary>Iniciar com o Windows (somente a camada de ícones livres). Liga/desliga, começa desligado.</summary>
public static class Startup
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DesktopFolders";

    public static bool IsEnabled
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(KeyPath);
            return k?.GetValue(ValueName) != null;
        }
    }

    public static void Set(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(KeyPath);
            if (on && !string.IsNullOrEmpty(Environment.ProcessPath)) k.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
            else k.DeleteValue(ValueName, false);
        }
        catch { }
    }
}

/// <summary>
/// Interruptor do menu de botão direito CLÁSSICO do Windows 11 (somente usuário atual, sem administrador, reversível).
/// No menu clássico, "Novo → Pasta inteligente" aparece direto. Vale depois de reiniciar o Explorador de Arquivos.
/// </summary>
public static class ClassicMenu
{
    private const string KeyPath = @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}";

    public static bool IsEnabled
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(KeyPath + @"\InprocServer32");
            return k != null;
        }
    }

    public static void Set(bool on)
    {
        try
        {
            if (on)
            {
                using var k = Registry.CurrentUser.CreateSubKey(KeyPath + @"\InprocServer32");
                k.SetValue("", "");   // valor padrão vazio
            }
            else Registry.CurrentUser.DeleteSubKeyTree(KeyPath, false);
        }
        catch { }
    }
}

/// <summary>Backup/restauro para a pasta DesktopFolders-Backup do OneDrive.</summary>
public static class Backup
{
    private static readonly string AppData = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopFolders");

    public static string? BackupDir
    {
        get
        {
            // prefere o OneDrive pessoal; senão usa o que houver
            foreach (var v in new[] { "OneDriveConsumer", "OneDrive", "OneDriveCommercial" })
            {
                var od = Environment.GetEnvironmentVariable(v);
                if (!string.IsNullOrEmpty(od) && Directory.Exists(od)) return Path.Combine(od, "DesktopFolders-Backup");
            }
            return null;
        }
    }

    private static string[] DesktopDirs => new[]
    {
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
    }.Where(d => !string.IsNullOrEmpty(d) && Directory.Exists(d)).Distinct().ToArray();

    private static int CopyDir(string from, string to, string pattern = "*")
    {
        if (!Directory.Exists(from)) return 0;
        Directory.CreateDirectory(to);
        int n = 0;
        foreach (var f in Directory.GetFiles(from, pattern, SearchOption.TopDirectoryOnly))
        {
            File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
            n++;
        }
        return n;
    }

    /// <summary>Copia layout, atalhos movidos e as pastas inteligentes da Área de Trabalho.</summary>
    public static string Run()
    {
        var dir = BackupDir ?? throw new InvalidOperationException("OneDrive não encontrado neste computador.");
        Directory.CreateDirectory(dir);
        int layout = 0, shortcuts, smart = 0;
        var lf = Path.Combine(AppData, "layout.json");
        if (File.Exists(lf)) { File.Copy(lf, Path.Combine(dir, "layout.json"), true); layout = 1; }
        shortcuts = CopyDir(Path.Combine(AppData, "Atalhos"), Path.Combine(dir, "Atalhos"));
        foreach (var d in DesktopDirs) smart += CopyDir(d, Path.Combine(dir, "Pastas"), "*" + SmartFolder.Ext);
        File.WriteAllText(Path.Combine(dir, "ultimo-backup.txt"), DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss"));
        return $"Backup salvo em:\n{dir}\n\n• Layout: {layout}\n• Atalhos movidos: {shortcuts}\n• Pastas inteligentes: {smart}";
    }

    /// <summary>Traz de volta o que está no backup (sobrescreve os arquivos de mesmo nome).</summary>
    public static string Restore()
    {
        var dir = BackupDir;
        if (dir == null || !Directory.Exists(dir)) throw new InvalidOperationException("Nenhum backup encontrado no OneDrive.");
        Directory.CreateDirectory(AppData);
        int layout = 0;
        var lf = Path.Combine(dir, "layout.json");
        if (File.Exists(lf)) { File.Copy(lf, Path.Combine(AppData, "layout.json"), true); layout = 1; }
        int shortcuts = CopyDir(Path.Combine(dir, "Atalhos"), Path.Combine(AppData, "Atalhos"));
        int smart = CopyDir(Path.Combine(dir, "Pastas"), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        return $"Backup restaurado.\n\n• Layout: {layout}\n• Atalhos: {shortcuts}\n• Pastas inteligentes: {smart}\n\nFeche e abra o DesktopFolders para carregar o layout restaurado.";
    }
}
