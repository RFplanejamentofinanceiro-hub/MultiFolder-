using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

namespace DesktopFolders;

public partial class MainWindow : Window
{
    private readonly Layout _layout;
    private ObservableCollection<Entry> Items => _layout.Items;

    private Entry? _openFolder;   // pasta aberta no popup
    private Entry? _pressed;      // item sobre o qual o mouse foi pressionado
    private Point _pressPos;
    private bool _syncing;

    private System.Windows.Forms.NotifyIcon? _tray;
    private System.Windows.Forms.ToolStripMenuItem? _trayHide;

    public MainWindow()
    {
        InitializeComponent();

        var wa = SystemParameters.WorkArea;
        Left = wa.Left; Top = wa.Top; Width = wa.Width; Height = wa.Height;

        _layout = Store.Load();
        DataContext = _layout;

        Items.CollectionChanged += (_, args) =>
        {
            // item novo sem posição vai para a primeira célula livre
            if (args.NewItems != null)
                foreach (Entry n in args.NewItems) if (n.X < 0) PlaceFirstFree(n);
            Save();
        };
        foreach (var e in Items) HookFolder(e);
        foreach (var e in Items.ToList()) if (e.X < 0) PlaceFirstFree(e);
        foreach (var e in Items) AdoptExisting(e);
        Save();

        SetupTray();
        Loaded += (_, _) =>
        {
            SetHideNative(_layout.HideNative, save: false);
            _dragWatcher = new DesktopDragWatcher(this) { Enabled = _layout.DragGroup };
            _dragWatcher.GroupRequested += OnDesktopGroup;
            _dragWatcher.Start();
            if (_layout.AutoUpdateCheck && DateTime.UtcNow - _layout.LastUpdateCheck > TimeSpan.FromHours(24))
                CheckUpdates(manual: false);
            // opção de teste: abre a primeira pasta ao iniciar
            if (Environment.GetCommandLineArgs().Contains("--abrir-primeira-pasta") && Items.FirstOrDefault(i => i.IsFolder) is { } f)
                OpenFolder(f);
        };
        Closed += (_, _) => Cleanup();
    }

    // ===================== Janela presa ao fundo =====================
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var h = new WindowInteropHelper(this).Handle;
        Native.SetWindowLong(h, Native.GWL_EXSTYLE, Native.GetWindowLong(h, Native.GWL_EXSTYLE) | Native.WS_EX_TOOLWINDOW);
        HwndSource.FromHwnd(h).AddHook(WndProc);
        PinToDesktop(h);
    }

    // A camada fica logo ACIMA do papel de parede e ABAIXO de qualquer outra janela.
    private static void PinToDesktop(IntPtr h)
    {
        var above = Native.WindowAboveWallpaper();
        if (above != h) Native.SetWindowPos(h, above, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); // NOSIZE|NOMOVE|NOACTIVATE
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_WINDOWPOSCHANGING)
        {
            var p = Marshal.PtrToStructure<Native.WINDOWPOS>(lParam);
            var above = Native.WindowAboveWallpaper();
            if (above == hwnd)
            {
                p.flags |= 0x0004u; // já estamos no lugar certo: não mexe na ordem (SWP_NOZORDER)
            }
            else
            {
                p.hwndInsertAfter = above;
                p.flags &= ~0x0004u;
            }
            Marshal.StructureToPtr(p, lParam, false);
        }
        return IntPtr.Zero;
    }

    // ===================== Bandeja e ícones do Windows =====================
    private void SetupTray()
    {
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "DesktopFolders",
            Visible = true
        };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        _trayHide = new System.Windows.Forms.ToolStripMenuItem("Ocultar ícones do Windows")
        {
            CheckOnClick = true,
            Checked = _layout.HideNative
        };
        _trayHide.CheckedChanged += (_, _) => { if (!_syncing) SetHideNative(_trayHide.Checked); };
        menu.Items.Add("Nova pasta inteligente (Área de Trabalho)...", null, (_, _) => Dispatcher.Invoke(NewSmartFolder));
        menu.Items.Add("Nova pasta (camada)...", null, (_, _) => Dispatcher.Invoke(() => NewFolder_Click(this, new RoutedEventArgs())));
        menu.Items.Add("Adicionar programa...", null, (_, _) => Dispatcher.Invoke(() => AddProgram_Click(this, new RoutedEventArgs())));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(_trayHide);
        var dragGroup = new System.Windows.Forms.ToolStripMenuItem("Agrupar ao soltar um ícone sobre outro (Área de Trabalho)")
        {
            CheckOnClick = true,
            Checked = _layout.DragGroup
        };
        dragGroup.CheckedChanged += (_, _) =>
        {
            _layout.DragGroup = dragGroup.Checked;
            if (_dragWatcher != null) _dragWatcher.Enabled = dragGroup.Checked;
            Save();
        };
        menu.Items.Add(dragGroup);
        var startup = new System.Windows.Forms.ToolStripMenuItem("Iniciar com o Windows")
        {
            CheckOnClick = true,
            Checked = Startup.IsEnabled
        };
        startup.CheckedChanged += (_, _) => Startup.Set(startup.Checked);
        menu.Items.Add(startup);
        var classic = new System.Windows.Forms.ToolStripMenuItem("Usar o menu de botão direito clássico (Novo → Pasta inteligente)")
        {
            CheckOnClick = true,
            Checked = ClassicMenu.IsEnabled
        };
        classic.CheckedChanged += (_, _) =>
        {
            ClassicMenu.Set(classic.Checked);
            MessageBox.Show(
                (classic.Checked ? "Menu clássico ativado." : "Menu clássico desativado (volta o menu moderno).") +
                "\n\nPara valer, reinicie o Explorador de Arquivos: Gerenciador de Tarefas → Explorador do Windows → Reiniciar.",
                "DesktopFolders", MessageBoxButton.OK, MessageBoxImage.Information);
        };
        menu.Items.Add(classic);
        var autoBackup = new System.Windows.Forms.ToolStripMenuItem("Backup automático ao sair")
        {
            CheckOnClick = true,
            Checked = _layout.AutoBackup
        };
        autoBackup.CheckedChanged += (_, _) => { _layout.AutoBackup = autoBackup.Checked; Save(); };
        menu.Items.Add(autoBackup);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Importar pasta inteligente (.dfpasta)...", null, (_, _) => Dispatcher.Invoke(ImportPackage));
        var autoUpdate = new System.Windows.Forms.ToolStripMenuItem("Verificar atualizações automaticamente")
        {
            CheckOnClick = true,
            Checked = _layout.AutoUpdateCheck
        };
        autoUpdate.CheckedChanged += (_, _) => { _layout.AutoUpdateCheck = autoUpdate.Checked; Save(); };
        menu.Items.Add(autoUpdate);
        menu.Items.Add("Verificar atualizações agora", null, (_, _) => Dispatcher.Invoke(() => CheckUpdates(manual: true)));
        _tray.BalloonTipClicked += (_, _) => { if (_updateUrl != null) OpenUrl(_updateUrl); };
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Backup para o OneDrive agora", null, (_, _) => Dispatcher.Invoke(() => RunBackup(restore: false)));
        menu.Items.Add("Restaurar backup do OneDrive...", null, (_, _) => Dispatcher.Invoke(() => RunBackup(restore: true)));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => Dispatcher.Invoke(ExitApp));
        _tray.ContextMenuStrip = menu;
    }

    /// <summary>Cria uma pasta inteligente (arquivo nativo) vazia na Área de Trabalho, sem depender do menu Novo.</summary>
    private void NewSmartFolder()
    {
        SmartFolder.Register();
        var name = InputDialog.Ask(this, "Nova pasta inteligente", "Pasta inteligente");
        if (string.IsNullOrWhiteSpace(name)) return;
        string dir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string safe = string.Concat(name.Split(System.IO.Path.GetInvalidFileNameChars())).Trim();
        if (safe.Length == 0) safe = "Pasta inteligente";
        string file = System.IO.Path.Combine(dir, safe + SmartFolder.Ext);
        for (int i = 2; File.Exists(file); i++) file = System.IO.Path.Combine(dir, $"{safe} ({i}){SmartFolder.Ext}");
        SmartFolder.Save(file, new Layout());
        MessageBox.Show(this, $"Pasta inteligente \"{System.IO.Path.GetFileNameWithoutExtension(file)}\" criada na Área de Trabalho.\n\nDê duplo clique para abrir.",
            "DesktopFolders", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ===================== Importar pacote / atualizações =====================
    private string? _updateUrl;

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    private void ImportPackage()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Importar pasta inteligente",
            Filter = "Pacote de pasta inteligente (*.dfpasta)|*.dfpasta|Todos os arquivos|*.*"
        };
        if (dlg.ShowDialog() != true) return;
        try { MessageBox.Show(Package.Import(dlg.FileName).resumo, "DesktopFolders", MessageBoxButton.OK, MessageBoxImage.Information); }
        catch (Exception ex) { MessageBox.Show("Não foi possível importar: " + ex.Message, "DesktopFolders", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private async void CheckUpdates(bool manual)
    {
        try
        {
            var (tag, url) = await Updater.LatestAsync();
            string cur = Updater.CurrentVersion;
            if (tag == null)
            {
                if (manual) MessageBox.Show("Ainda não há versões publicadas.", "DesktopFolders", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else if (Updater.IsNewer(tag, cur))
            {
                _updateUrl = url;
                if (manual)
                {
                    if (MessageBox.Show($"Nova versão {tag} disponível (a sua é {cur}).\n\nAbrir a página de download?", "DesktopFolders",
                            MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes && url != null) OpenUrl(url);
                }
                else _tray?.ShowBalloonTip(10000, "DesktopFolders", $"Nova versão {tag} disponível. Clique para baixar.", System.Windows.Forms.ToolTipIcon.Info);
            }
            else if (manual) MessageBox.Show($"Você está na versão mais recente ({cur}).", "DesktopFolders", MessageBoxButton.OK, MessageBoxImage.Information);
            _layout.LastUpdateCheck = DateTime.UtcNow;
            Save();
        }
        catch
        {
            if (manual) MessageBox.Show("Não foi possível verificar agora (sem internet ou repositório indisponível).", "DesktopFolders",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RunBackup(bool restore)
    {
        try
        {
            if (restore &&
                MessageBox.Show("Restaurar o backup do OneDrive? Arquivos com o mesmo nome serão sobrescritos.",
                    "DesktopFolders", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            string msg = restore ? Backup.Restore() : Backup.Run();
            MessageBox.Show(msg, "DesktopFolders", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "DesktopFolders", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SetHideNative(bool hide, bool save = true)
    {
        _layout.HideNative = hide;
        Native.SetDesktopIcons(!hide);
        _syncing = true;
        if (_trayHide != null) _trayHide.Checked = hide;
        _syncing = false;
        if (save) Save();
    }


    private void ExitApp()
    {
        Save();
        if (_layout.AutoBackup)
        {
            try { Backup.Run(); } catch { /* sem OneDrive ou sem permissão: sai mesmo assim */ }
        }
        Cleanup();
        Application.Current.Shutdown();
    }

    private void Cleanup()
    {
        _dragWatcher?.Stop();
        Native.SetDesktopIcons(true); // sempre devolve os ícones padrão ao sair
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
    }

    // ===================== Persistência =====================
    private void Save() => Store.Save(_layout);

    private void HookFolder(Entry e)
    {
        if (!e.IsFolder) return;
        e.Items.CollectionChanged += (_, _) => { e.RefreshPreview(); Save(); };
    }

    // ===================== Localizar / mover itens =====================
    private (ObservableCollection<Entry>? col, Entry? folder) FindOwner(Entry e)
    {
        if (Items.Contains(e)) return (Items, null);
        foreach (var f in Items)
            if (f.IsFolder && f.Items.Contains(e)) return (f.Items, f);
        return (null, null);
    }

    private void RemoveFrom(Entry e)
    {
        var (col, folder) = FindOwner(e);
        col?.Remove(e);
        if (folder != null && folder.Items.Count == 0)
        {
            Items.Remove(folder);
            if (_openFolder == folder) CloseOverlay();
        }
    }

    private List<Entry> AddPaths(string[] paths, ObservableCollection<Entry> target)
    {
        var added = new List<Entry>();
        foreach (var raw in paths)
        {
            if (!File.Exists(raw) && !Directory.Exists(raw)) continue;
            string name = Directory.Exists(raw) ? System.IO.Path.GetFileName(raw.TrimEnd('\\'))
                                                : System.IO.Path.GetFileNameWithoutExtension(raw);
            if (string.IsNullOrEmpty(name)) name = raw;

            // Atalho que está na Área de Trabalho: move para dentro do app, para sumir de lá.
            string path = AdoptFromDesktop(raw, out string? original);
            var entry = new Entry { Name = name, Path = path, OriginalPath = original };
            target.Add(entry);
            added.Add(entry);
        }
        return added;
    }

    private static readonly string ShortcutsDir = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopFolders", "Atalhos");

    private static bool IsOnDesktop(string path)
    {
        string? dir = System.IO.Path.GetDirectoryName(path)?.TrimEnd('\\');
        if (dir == null) return false;
        foreach (var d in new[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                                  Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory) })
            if (!string.IsNullOrEmpty(d) && string.Equals(dir, d.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Move um .lnk/.url da Área de Trabalho para a pasta do app. Se não der (ex.: Área Pública), mantém onde está.</summary>
    internal static string AdoptFromDesktop(string path, out string? original)
    {
        original = null;
        string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
        if (!File.Exists(path) || (ext != ".lnk" && ext != ".url") || !IsOnDesktop(path)) return path;
        try
        {
            Directory.CreateDirectory(ShortcutsDir);
            string dest = System.IO.Path.Combine(ShortcutsDir, System.IO.Path.GetFileName(path));
            for (int i = 2; File.Exists(dest); i++)
                dest = System.IO.Path.Combine(ShortcutsDir,
                    System.IO.Path.GetFileNameWithoutExtension(path) + $" ({i})" + ext);
            File.Move(path, dest);
            original = path;
            return dest;
        }
        catch
        {
            return path; // sem permissão: o atalho continua na Área de Trabalho
        }
    }

    /// <summary>Itens salvos antes desta regra: se o atalho ainda está na Área de Trabalho, move agora.</summary>
    private static void AdoptExisting(Entry e)
    {
        foreach (var child in e.Items) AdoptExisting(child);
        if (e.Path == null || e.OriginalPath != null) return;
        string moved = AdoptFromDesktop(e.Path, out string? original);
        if (original != null) { e.Path = moved; e.OriginalPath = original; }
    }

    /// <summary>Devolve à Área de Trabalho um atalho que tinha sido movido.</summary>
    internal static void RestoreOriginal(Entry e)
    {
        foreach (var child in e.Items) RestoreOriginal(child);
        try
        {
            if (e.OriginalPath != null && e.Path != null && File.Exists(e.Path) && !File.Exists(e.OriginalPath))
                File.Move(e.Path, e.OriginalPath);
        }
        catch { /* mantém na pasta do app */ }
    }

    // ===================== Grade da área de trabalho =====================
    private const double CW = 104, CH = 116; // célula = ícone (92x104) + margens

    private int Cols => Math.Max(1, (int)((Desktop.ActualWidth > 0 ? Desktop.ActualWidth : Width - 16) / CW));
    private int Rows => Math.Max(1, (int)((Desktop.ActualHeight > 0 ? Desktop.ActualHeight : Height - 16) / CH));

    private bool Occupied(int c, int r, Entry? except) =>
        Items.Any(i => i != except && i.X >= 0 && (int)Math.Round(i.X / CW) == c && (int)Math.Round(i.Y / CH) == r);

    private static void SetCell(Entry e, int c, int r) { e.X = c * CW; e.Y = r * CH; }

    private void PlaceFirstFree(Entry e)
    {
        for (int c = 0; c < Cols; c++)
            for (int r = 0; r < Rows; r++)
                if (!Occupied(c, r, e)) { SetCell(e, c, r); return; }
        SetCell(e, 0, 0);
    }

    /// <summary>Coloca o item na célula livre mais próxima do ponto (em DIPs, relativo à área de ícones).</summary>
    private void PlaceNear(Entry e, Point p)
    {
        int c0 = Math.Clamp((int)(p.X / CW), 0, Cols - 1);
        int r0 = Math.Clamp((int)(p.Y / CH), 0, Rows - 1);
        int bestC = -1, bestR = -1; double best = double.MaxValue;
        for (int c = 0; c < Cols; c++)
            for (int r = 0; r < Rows; r++)
            {
                if (Occupied(c, r, e)) continue;
                double d = (c - c0) * (c - c0) + (r - r0) * (r - r0);
                if (d < best) { best = d; bestC = c; bestR = r; }
            }
        if (bestC < 0) { PlaceFirstFree(e); return; }
        SetCell(e, bestC, bestR);
        Save();
    }

    // ===================== Abrir pasta / programa =====================
    private void Activate(Entry e)
    {
        if (e.IsFolder) OpenFolder(e);
        else Launch(e);
    }

    private void Launch(Entry e)
    {
        CloseOverlay();
        try
        {
            Process.Start(new ProcessStartInfo(e.Path!) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Não foi possível abrir \"{e.Name}\".\n\n{ex.Message}", "DesktopFolders",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenFolder(Entry f)
    {
        _openFolder = f;
        FolderTitle.Text = f.Name;
        FolderItems.ItemsSource = f.Items;
        Overlay.Visibility = Visibility.Visible;
        Focus();
    }

    private void CloseOverlay()
    {
        _openFolder = null;
        FolderItems.ItemsSource = null;
        Overlay.Visibility = Visibility.Collapsed;
    }

    private void Window_KeyDown(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Overlay.Visibility == Visibility.Visible) CloseOverlay();
    }

    private void Overlay_Click(object s, MouseButtonEventArgs e) => CloseOverlay();
    private void Panel_Click(object s, MouseButtonEventArgs e) => e.Handled = true; // clicar no painel não fecha

    // ===================== Clique e arrastar nos itens =====================
    private static Entry? EntryOf(object s) => (s as FrameworkElement)?.DataContext as Entry;

    private void Tile_Down(object s, MouseButtonEventArgs e)
    {
        _pressed = EntryOf(s);
        _pressPos = e.GetPosition(this);
    }

    private void Tile_Move(object s, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _pressed == null) return;
        var d = e.GetPosition(this) - _pressPos;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var en = _pressed;
        _pressed = null;

        // Durante o arraste a camada passa a "pegar" o mouse (fundo quase invisível), para poder
        // receber o item solto em qualquer lugar vazio; depois volta a ser transparente ao mouse.
        Root.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(1, 0, 0, 0));
        Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        try { DragDrop.DoDragDrop((DependencyObject)s, new DataObject("entry", en), DragDropEffects.Move); }
        finally { Root.Background = null; }
    }

    private void Tile_Up(object s, MouseButtonEventArgs e)
    {
        var en = EntryOf(s);
        if (_pressed != null && _pressed == en)
        {
            _pressed = null;
            Activate(en!);
        }
    }

    // ===================== Soltar (drag & drop) =====================
    private void Tile_Drop(object s, DragEventArgs e)
    {
        e.Handled = true;
        var target = EntryOf(s);
        if (target == null) return;

        if (e.Data.GetData("entry") is Entry src)
        {
            if (src != target) DropEntry(src, target);
        }
        else if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            AddPaths(files, target.IsFolder ? target.Items : Items);
        }
    }

    private void DropEntry(Entry src, Entry target)
    {
        var (tcol, tfolder) = FindOwner(target);
        var (scol, _) = FindOwner(src);
        if (tcol == null || scol == null) return;

        // Alvo dentro de uma pasta aberta: reordenar ou trazer um programa para dentro.
        if (tfolder != null)
        {
            if (scol == tcol) tcol.Move(tcol.IndexOf(src), tcol.IndexOf(target));
            else if (!src.IsFolder)
            {
                RemoveFrom(src);
                tcol.Insert(Math.Max(0, tcol.IndexOf(target)), src);
            }
            return;
        }

        if (src.IsFolder)
        {
            return; // pasta não entra em outra: para mover, solte num espaço vazio
        }
        else if (target.IsFolder)
        {
            var (_, sf) = FindOwner(src);
            if (sf == target) return;
            RemoveFrom(src);
            target.Items.Add(src);
        }
        else
        {
            // programa sobre programa: cria uma PASTA INTELIGENTE (arquivo nativo na Área de Trabalho)
            // com os dois e pede o nome. Se cancelar, nada muda.
            var name = InputDialog.Ask(this, "Nova pasta inteligente", "Pasta inteligente");
            if (string.IsNullOrWhiteSpace(name)) return;
            SmartFolder.Register();
            var data = new Layout();
            data.Items.Add(target);
            data.Items.Add(src);
            string file = SmartFolder.CreateNew(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), name.Trim(), data);
            RemoveFrom(src);
            Items.Remove(target);   // os dois passam a viver dentro do arquivo da pasta

            // oferece desfazer por alguns segundos: apaga a pasta e devolve os dois à camada, nos mesmos lugares
            ShowUndoToast($"Pasta inteligente \"{System.IO.Path.GetFileNameWithoutExtension(file)}\" criada na Área de Trabalho.", () =>
            {
                try { File.Delete(file); } catch { }
                Items.Add(target);
                Items.Add(src);
            });
        }
    }

    // ===================== Soltar ícone sobre ícone na Área de Trabalho do Windows =====================
    private DesktopDragWatcher? _dragWatcher;

    private static string? ResolveDesktopItem(string name)
    {
        foreach (var d in new[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                                  Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory) })
        {
            if (string.IsNullOrEmpty(d) || !Directory.Exists(d)) continue;
            foreach (var f in Directory.GetFileSystemEntries(d))
                if (string.Equals(System.IO.Path.GetFileNameWithoutExtension(f), name, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(System.IO.Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase)) return f;
        }
        return null;
    }

    private static bool IsShortcutFile(string p)
    {
        string e = System.IO.Path.GetExtension(p).ToLowerInvariant();
        return e == ".lnk" || e == ".url";
    }

    /// <summary>O usuário soltou ícone(s) sobre outro ícone da Área de Trabalho: cria a pasta inteligente e pede o nome.</summary>
    private void OnDesktopGroup(string[] dragged, string targetName)
    {
        string? target = ResolveDesktopItem(targetName);
        var items = dragged.Where(p => !string.Equals(p, target, StringComparison.OrdinalIgnoreCase)).ToList();
        if (target == null || items.Count == 0) return;

        var all = new List<string> { target };
        all.AddRange(items);
        if (all.Any(p => !IsShortcutFile(p)))
        {
            ShowUndoToast("Só atalhos (.lnk/.url) podem ser agrupados em uma pasta inteligente.", null);
            return;
        }

        DesktopDragWatcher.Log($"agrupar: alvo='{target}' arrastados={string.Join(" | ", items)}");
        // sem janela-dona: com "Mostrar Área de Trabalho" (Win+D) a janela principal fica oculta e a caixa sumiria junto
        var name = InputDialog.Ask(null!, "Nova pasta inteligente", "Pasta inteligente");
        DesktopDragWatcher.Log($"agrupar: nome informado='{name}'");
        if (string.IsNullOrWhiteSpace(name)) return;   // cancelou: nada muda

        SmartFolder.Register();
        var data = new Layout();
        var entries = new List<Entry>();
        foreach (var p in all)
        {
            string moved = AdoptFromDesktop(p, out string? original);   // o atalho sai da Área de Trabalho
            var en = new Entry { Name = System.IO.Path.GetFileNameWithoutExtension(p), Path = moved, OriginalPath = original };
            data.Items.Add(en);
            entries.Add(en);
        }
        string file = SmartFolder.CreateNew(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), name.Trim(), data);

        ShowUndoToast($"Pasta inteligente \"{System.IO.Path.GetFileNameWithoutExtension(file)}\" criada com {all.Count} atalhos.", () =>
        {
            try { File.Delete(file); } catch { }
            foreach (var en in entries) RestoreOriginal(en);   // os atalhos voltam à Área de Trabalho
        });
    }

    /// <summary>Aviso discreto no canto da tela com o botão Desfazer (some sozinho em 10 segundos).</summary>
    private void ShowUndoToast(string text, Action? undo)
    {
        var toast = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = System.Windows.Media.Brushes.Transparent,
            ShowInTaskbar = false, Topmost = true, ShowActivated = false, ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight
        };
        var undoBtn = new Button { Content = "Desfazer", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var label = new TextBlock
        {
            Text = text, Foreground = System.Windows.Media.Brushes.White, FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
            FontSize = 13, MaxWidth = 300, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(label);
        if (undo != null) row.Children.Add(undoBtn);
        toast.Content = new Border
        {
            CornerRadius = new CornerRadius(14), Padding = new Thickness(16, 12, 16, 12), Child = row,
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xF2, 0x1E, 0x22, 0x30)),
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x33, 255, 255, 255)),
            BorderThickness = new Thickness(1)
        };
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        timer.Tick += (_, _) => { timer.Stop(); toast.Close(); };
        undoBtn.Click += (_, _) => { timer.Stop(); toast.Close(); undo?.Invoke(); };
        toast.Loaded += (_, _) =>
        {
            var wa = SystemParameters.WorkArea;
            toast.Left = wa.Right - toast.ActualWidth - 20;
            toast.Top = wa.Bottom - toast.ActualHeight - 20;
            timer.Start();
        };
        toast.Show();
    }

    private void Panel_Drop(object s, DragEventArgs e)
    {
        e.Handled = true;
        if (_openFolder == null) return;

        if (e.Data.GetData("entry") is Entry src && !src.IsFolder)
        {
            if (_openFolder.Items.Contains(src)) return;
            RemoveFrom(src);
            _openFolder?.Items.Add(src);
        }
        else if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            AddPaths(files, _openFolder.Items);
        }
    }

    private void Overlay_Drop(object s, DragEventArgs e)
    {
        // soltar fora do painel: tira o programa da pasta e leva para a área de trabalho
        e.Handled = true;
        DropOnDesktop(e);
    }

    private void Root_Drop(object s, DragEventArgs e) => DropOnDesktop(e);

    private void DropOnDesktop(DragEventArgs e)
    {
        var pos = e.GetPosition(Desktop);

        if (e.Data.GetData("entry") is Entry src)
        {
            var (_, folder) = FindOwner(src);
            if (folder != null)
            {
                RemoveFrom(src);   // sai da pasta...
                Items.Add(src);
            }
            PlaceNear(src, pos);   // ...e vai para onde foi solto
        }
        else if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            var added = AddPaths(files, Items);
            if (added.Count > 0) PlaceNear(added[0], pos);
        }
    }

    // ===================== Menus =====================
    private static Entry? MenuEntry(object s) => (s as FrameworkElement)?.DataContext as Entry;

    /// <summary>
    /// Botão direito num app: abre o menu NATIVO do Windows desse item (Abrir, Abrir local do arquivo,
    /// Propriedades...) com as opções do DesktopFolders no final. Em pastas da camada (que não são arquivos),
    /// mostra só as opções do DesktopFolders.
    /// </summary>
    private void Tile_RightClick(object s, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _pressed = null;
        if (EntryOf(s) is not { } en) return;

        Action Call(Action<object, RoutedEventArgs> handler) =>
            () => handler(new FrameworkElement { DataContext = en }, new RoutedEventArgs());

        var extras = new List<(string, Action)>();
        if (en.IsFolder)
        {
            extras.Add(("Abrir", Call(Menu_Open)));
            extras.Add(("Adicionar programa aqui...", Call(Menu_AddHere)));
            extras.Add(("Converter em pasta inteligente (nativa)", Call(Menu_Convert)));
        }
        extras.Add(("Renomear", Call(Menu_Rename)));
        if (FindOwner(en).folder != null) extras.Add(("Tirar da pasta", Call(Menu_MoveOut)));
        extras.Add(("Remover daqui", Call(Menu_Remove)));

        ShellMenu.Show(this, en.IsFolder ? null : en.Path, extras);
    }

    private void Menu_Open(object s, RoutedEventArgs e) { if (MenuEntry(s) is { } en) Activate(en); }

    private void Menu_AddHere(object s, RoutedEventArgs e)
    {
        if (MenuEntry(s) is { IsFolder: true } f) PickPrograms(f.Items);
    }

    private void Menu_Rename(object s, RoutedEventArgs e)
    {
        if (MenuEntry(s) is not { } en) return;
        var name = InputDialog.Ask(this, "Renomear", en.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        en.Name = name.Trim();
        if (_openFolder == en) FolderTitle.Text = en.Name;
        Save();
    }

    /// <summary>Transforma a pasta numa "Pasta inteligente": um arquivo nativo na Área de Trabalho.</summary>
    private void Menu_Convert(object s, RoutedEventArgs e)
    {
        if (MenuEntry(s) is not { IsFolder: true } f) return;
        SmartFolder.Register();

        string dir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string name = string.Concat(f.Name.Split(System.IO.Path.GetInvalidFileNameChars())).Trim();
        if (name.Length == 0) name = "Pasta inteligente";
        string file = System.IO.Path.Combine(dir, name + SmartFolder.Ext);
        for (int i = 2; File.Exists(file); i++)
            file = System.IO.Path.Combine(dir, $"{name} ({i}){SmartFolder.Ext}");

        var data = new Layout();
        foreach (var it in f.Items) data.Items.Add(it);
        SmartFolder.Save(file, data);

        CloseOverlay();
        Items.Remove(f); // os atalhos agora pertencem ao arquivo; não devolve nada à Área de Trabalho
        MessageBox.Show(this, $"Pasta inteligente criada na Área de Trabalho:\n{System.IO.Path.GetFileNameWithoutExtension(file)}\n\nDê duplo clique nela para abrir.",
            "DesktopFolders", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Menu_MoveOut(object s, RoutedEventArgs e)
    {
        if (MenuEntry(s) is not { } en || FindOwner(en).folder == null) return;
        RemoveFrom(en);
        Items.Add(en);
    }

    private void Menu_Remove(object s, RoutedEventArgs e)
    {
        if (MenuEntry(s) is not { } en) return;
        string msg = en.IsFolder
            ? $"Remover a pasta \"{en.Name}\" e seus {en.Items.Count} item(ns) daqui? Os programas em si não são apagados."
            : $"Remover \"{en.Name}\" daqui? O programa em si não é apagado.";
        if (MessageBox.Show(this, msg, "DesktopFolders", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        RestoreOriginal(en); // atalhos que vieram da Área de Trabalho voltam para lá
        RemoveFrom(en);
    }

    private void NewFolder_Click(object s, RoutedEventArgs e)
    {
        var name = InputDialog.Ask(this, "Nova pasta", "Nova pasta");
        if (string.IsNullOrWhiteSpace(name)) return;
        var f = new Entry { Name = name.Trim() };
        HookFolder(f);
        Items.Add(f);
    }

    private void AddProgram_Click(object s, RoutedEventArgs e) => PickPrograms(Items);

    private void PickPrograms(ObservableCollection<Entry> target)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Escolha programas ou atalhos",
            Multiselect = true,
            Filter = "Programas e atalhos|*.exe;*.lnk;*.url;*.bat;*.cmd|Todos os arquivos|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms)
        };
        if (dlg.ShowDialog(this) == true) AddPaths(dlg.FileNames, target);
    }
}

/// <summary>Caixa simples para pedir um texto.</summary>
public static class InputDialog
{
    public static string? Ask(Window owner, string title, string initial)
    {
        string? result = null;
        var win = new Window
        {
            Title = title, Owner = owner, Width = 340, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, Topmost = true
        };
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 0, 0, 12), Padding = new Thickness(4) };
        var ok = new Button { Content = "OK", Width = 80, IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right };
        ok.Click += (_, _) => { result = box.Text; win.DialogResult = true; };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(box);
        panel.Children.Add(ok);
        win.Content = panel;
        win.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return win.ShowDialog() == true ? result : null;
    }
}
