using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace DesktopFolders;

/// <summary>
/// "Pasta inteligente": um arquivo (.pastainteligente) que guarda a lista de programas.
/// Registrado no menu oficial Novo do Windows; duplo clique abre o popup.
/// </summary>
public static class SmartFolder
{
    // A lista "Novo" do Windows é ordenada pela extensão: começar com "0" faz a Pasta inteligente
    // aparecer logo abaixo de "Pasta" e "Atalho". A extensão fica escondida (NeverShowExt).
    public const string Ext = ".0pasta";
    private const string LegacyExt = ".pastainteligente";
    private const string ProgId = "DesktopFolders.PastaInteligente";

    [DllImport("shell32.dll")] private static extern void SHChangeNotify(int eventId, uint flags, IntPtr a, IntPtr b);

    /// <summary>Registra o tipo para o usuário atual (sem administrador). Pode ser chamado várias vezes.</summary>
    public static void Register()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;

            using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + Ext))
            {
                k.SetValue("", ProgId);
                using var sn = k.CreateSubKey("ShellNew");   // aparece em Novo → Pasta inteligente
                sn.SetValue("NullFile", "");
                sn.SetValue("ItemName", "Pasta inteligente"); // nome do arquivo recém-criado
            }
            using (var p = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ProgId))
            {
                p.SetValue("", "Pasta inteligente");
                p.SetValue("FriendlyTypeName", "Pasta inteligente");
                p.SetValue("NeverShowExt", "");              // esconde a extensão, como nas pastas
                using (var i = p.CreateSubKey("DefaultIcon")) i.SetValue("", $"\"{exe}\",0");
                using (var c = p.CreateSubKey(@"shell\open\command")) c.SetValue("", $"\"{exe}\" \"%1\"");
            }
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

            // remove o registro da extensão antiga (.pastainteligente), de versões anteriores
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + LegacyExt, false);
            // sem DropHandler: o Windows não permite "soltar sobre o ícone" de um tipo próprio sem extensão nativa
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + ProgId + @"\shellex", false);

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
            // sem acesso ao registro: segue sem o menu Novo; anota o motivo para diagnóstico
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopFolders");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "erro-registro.txt"), $"{DateTime.Now:s} {ex}\n");
            }
            catch { }
        }
    }

    private const string ThumbClsid = "{7B3F1E52-9A0C-4D6B-8E21-5C4D7A9E1F30}";
    private const string ThumbHandlerKey = "{E357FCCD-A995-4576-B01F-234630154E96}"; // IThumbnailProvider

    /// <summary>
    /// Miniatura (mostra os apps de dentro no ícone). O Windows só carrega provedores de miniatura .NET
    /// no processo isolado se estiverem registrados para TODOS os usuários (HKLM) → exige administrador.
    /// </summary>
    /// <summary>
    /// Passo de administrador (uma vez): o menu Novo do Windows 11 só lista tipos registrados na MÁQUINA,
    /// e o mesmo vale para a miniatura. Registra a extensão em HKLM (o resto continua por usuário).
    /// </summary>
    public static string RegisterThumbnailMachine()
    {
        string? exe = Environment.ProcessPath;
        string dll = Path.Combine(Path.GetDirectoryName(exe ?? "") ?? "", "DfThumb.dll");
        try
        {
            using (var k = Registry.LocalMachine.CreateSubKey(@"Software\Classes\" + Ext))
            {
                k.SetValue("", ProgId);
                using var sn = k.CreateSubKey("ShellNew");
                sn.SetValue("NullFile", "");
                sn.SetValue("ItemName", "Pasta inteligente");
            }
            string extra = "";
            if (File.Exists(dll)) RegisterThumbnail(Registry.LocalMachine, dll);
            else extra = " (DfThumb.dll não encontrado: só o menu Novo foi registrado)";
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
            return "Integração registrada: menu Novo e miniatura." + extra +
                   "\n\nSe não aparecer de imediato, reinicie o Explorador de Arquivos (Gerenciador de Tarefas > Explorador do Windows > Reiniciar).";
        }
        catch (UnauthorizedAccessException) { return "Sem permissão: execute como administrador."; }
        catch (System.Security.SecurityException) { return "Sem permissão: execute como administrador."; }
    }

    public static string UnregisterThumbnailMachine()
    {
        try
        {
            Registry.LocalMachine.DeleteSubKeyTree(@"Software\Classes\CLSID\" + ThumbClsid, false);
            Registry.LocalMachine.DeleteSubKeyTree(@"Software\Classes\" + Ext, false);
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
            return "Integração removida (menu Novo e miniatura).";
        }
        catch (UnauthorizedAccessException) { return "Sem permissão: execute como administrador."; }
    }

    private static void RegisterThumbnail(RegistryKey root, string dll)
    {
        using (var c = root.CreateSubKey(@"Software\Classes\CLSID\" + ThumbClsid))
        {
            c.SetValue("", "Pasta inteligente - miniatura");
            using var i = c.CreateSubKey("InprocServer32");
            i.SetValue("", "mscoree.dll");
            i.SetValue("ThreadingModel", "Both");
            i.SetValue("Class", "DfThumb.Provider");
            i.SetValue("Assembly", "DfThumb, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null");
            i.SetValue("RuntimeVersion", "v4.0.30319");
            i.SetValue("CodeBase", new Uri(dll).AbsoluteUri);
        }
        using (var h = root.CreateSubKey(@"Software\Classes\" + Ext + @"\ShellEx\" + ThumbHandlerKey))
            h.SetValue("", ThumbClsid);
    }

    public static void Unregister()
    {
        try
        {
            foreach (var type in new[] { "lnkfile", "exefile" })
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + type + @"\shell\DesktopFolders.Add", false);
            // limpa restos de registros antigos da miniatura no usuário atual
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\CLSID\" + ThumbClsid, false);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\DesktopFolders.Thumb", false);
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\" + Ext + @"\ShellEx\" + ThumbHandlerKey, false);
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

    /// <summary>Cria o arquivo de uma pasta inteligente em <paramref name="dir"/> (nome único) e retorna o caminho.</summary>
    public static string CreateNew(string dir, string name, Layout? data = null)
    {
        string safe = string.Concat(name.Split(Path.GetInvalidFileNameChars())).Trim();
        if (safe.Length == 0) safe = "Pasta inteligente";
        string file = Path.Combine(dir, safe + Ext);
        for (int i = 2; File.Exists(file); i++) file = Path.Combine(dir, $"{safe} ({i}){Ext}");
        Save(file, data ?? new Layout());
        NotifyChanged(file, created: true);   // o ícone já nasce com a miniatura dos apps
        return file;
    }

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

    public static void Save(string file, Layout data)
    {
        try
        {
            File.WriteAllText(file, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
            NotifyChanged(file, created: false);
        }
        catch { }
    }

    /// <summary>Avisa o Explorador que o arquivo mudou, para ele refazer a miniatura (ícone com os apps) na hora.</summary>
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
