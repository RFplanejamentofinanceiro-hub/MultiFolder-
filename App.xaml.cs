using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;

namespace DesktopFolders;

public partial class App : Application
{
    private Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Comandos de manutenção
        if (e.Args.Contains("--registrar")) { SmartFolder.Register(); Shutdown(); return; }
        if (e.Args.Contains("--remover-registro")) { SmartFolder.Unregister(); Shutdown(); return; }
        int ui = Array.IndexOf(e.Args, "--listar-icones");   // diagnóstico: ícones da Área de Trabalho (nome e posição)
        if (ui >= 0 && ui + 1 < e.Args.Length)
        {
            var lines = new System.Collections.Generic.List<string>();
            using var dl = DesktopList.Open();
            if (dl == null) lines.Add("lista da Área de Trabalho não encontrada");
            else
            {
                int n = dl.Count;
                var sel = dl.SelectedItems();
                lines.Add($"ícones: {n}  selecionados: {sel.Count}");
                for (int i = 0; i < n; i++)
                {
                    var r = dl.ItemRect(i);
                    lines.Add($"[{i}] '{dl.ItemText(i)}' {(r == null ? "?" : $"{r.Value.x},{r.Value.y} {r.Value.w}x{r.Value.h}")}{(sel.Contains(i) ? " (selecionado)" : "")}");
                }
            }
            File.WriteAllLines(e.Args[ui + 1], lines);
            Shutdown();
            return;
        }
        int ci = Array.IndexOf(e.Args, "--criar-aqui");   // botão direito no fundo de uma pasta: Nova pasta inteligente
        if (ci >= 0 && ci + 1 < e.Args.Length && Directory.Exists(e.Args[ci + 1]))
        {
            var nome = InputDialog.Ask(null!, "Nova pasta inteligente", "Pasta inteligente");
            if (!string.IsNullOrWhiteSpace(nome)) SmartFolder.CreateNew(e.Args[ci + 1], nome.Trim());
            Shutdown();
            return;
        }
        int xi = Array.IndexOf(e.Args, "--exportar");   // --exportar <pasta.0pasta> <pacote.dfpasta>
        if (xi >= 0 && xi + 2 < e.Args.Length)
        {
            string src = e.Args[xi + 1];
            Package.Export(System.IO.Path.GetFileNameWithoutExtension(src), SmartFolder.Load(src), e.Args[xi + 2]);
            Shutdown();
            return;
        }
        int ii = Array.IndexOf(e.Args, "--importar");   // duplo clique num pacote .dfpasta
        if (ii >= 0 && ii + 1 < e.Args.Length)
        {
            try
            {
                var (_, resumo) = Package.Import(e.Args[ii + 1]);
                if (!e.Args.Contains("--silencioso")) MessageBox.Show(resumo, "DesktopFolders", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex) { MessageBox.Show("Não foi possível importar: " + ex.Message, "DesktopFolders", MessageBoxButton.OK, MessageBoxImage.Warning); }
            Shutdown();
            return;
        }
        int li = Array.IndexOf(e.Args, "--listar-novo");   // diagnóstico: o que o Windows lista em Novo
        if (li >= 0 && li + 1 < e.Args.Length)
        {
            File.WriteAllLines(e.Args[li + 1], ShellMenu.ListNewMenu());
            Shutdown();
            return;
        }
        int mi = Array.IndexOf(e.Args, "--listar-menu");   // diagnóstico: --listar-menu <arquivo> <saida.txt>
        if (mi >= 0 && mi + 2 < e.Args.Length)
        {
            File.WriteAllLines(e.Args[mi + 2], ShellMenu.ListItemMenu(e.Args[mi + 1]));
            Shutdown();
            return;
        }
        if (e.Args.Contains("--registrar-miniatura") || e.Args.Contains("--remover-miniatura"))
        {
            string r = e.Args.Contains("--registrar-miniatura") ? SmartFolder.RegisterThumbnailMachine() : SmartFolder.UnregisterThumbnailMachine();
            if (!e.Args.Contains("--silencioso")) MessageBox.Show(r, "DesktopFolders - menu Novo e miniatura");
            Shutdown();
            return;
        }

        // Clique direito num atalho → "Adicionar à pasta inteligente..."
        int ai = Array.IndexOf(e.Args, "--adicionar");
        if (ai >= 0 && ai + 1 < e.Args.Length && (File.Exists(e.Args[ai + 1]) || Directory.Exists(e.Args[ai + 1])))
        {
            // Cada atalho selecionado abre um processo: todos deixam o caminho numa fila e só o
            // primeiro (dono do mutex) espera um instante, junta a fila e mostra UMA lista.
            string qdir = Path.Combine(Path.GetTempPath(), "DesktopFoldersFila");
            Directory.CreateDirectory(qdir);
            File.WriteAllText(Path.Combine(qdir, Guid.NewGuid().ToString("N") + ".txt"), e.Args[ai + 1]);
            _mutex = new Mutex(true, "DesktopFolders.Seletor", out bool primeiro);
            if (!primeiro) { Shutdown(); return; }
            Thread.Sleep(1000);

            var itens = new System.Collections.Generic.List<string>();
            foreach (var q in Directory.GetFiles(qdir, "*.txt"))
            {
                try
                {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(q) < TimeSpan.FromSeconds(10)) // ignora sobras antigas
                    {
                        string p = File.ReadAllText(q).Trim();
                        if ((File.Exists(p) || Directory.Exists(p)) && !itens.Contains(p, StringComparer.OrdinalIgnoreCase)) itens.Add(p);
                    }
                    File.Delete(q);
                }
                catch { }
            }
            if (itens.Count == 0) { Shutdown(); return; }

            var cw = Chooser.Create(itens.ToArray());
            MainWindow = cw;
            cw.Show();
            return;
        }

        // Duplo clique em uma pasta inteligente: abre só o popup (não precisa do app principal)
        string? file = e.Args.FirstOrDefault(a => !a.StartsWith("--") && File.Exists(a)
                                                  && a.EndsWith(SmartFolder.Ext, StringComparison.OrdinalIgnoreCase));
        if (file != null)
        {
            // os demais argumentos são arquivos soltos sobre o ícone da pasta
            var dropped = e.Args.Where(a => !a.StartsWith("--") && a != file).ToArray();
            var w = new FolderWindow(file, dropped);
            MainWindow = w;
            w.Show();
            return;
        }

        // Modo camada na área de trabalho: uma instância só
        _mutex = new Mutex(true, "DesktopFolders.SingleInstance", out bool created);
        if (!created) { Shutdown(); return; }

        // Se algo quebrar, devolve os ícones padrão do Windows antes de fechar.
        DispatcherUnhandledException += (_, args) =>
        {
            Native.SetDesktopIcons(true);
            MessageBox.Show(args.Exception.Message, "DesktopFolders", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
            Shutdown();
        };

        SmartFolder.Register(); // garante Novo → Pasta inteligente apontando para este programa
        new MainWindow().Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
