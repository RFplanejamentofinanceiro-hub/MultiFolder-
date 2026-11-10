using System;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DesktopFolders;

/// <summary>Popup de uma pasta inteligente: abre ao dar duplo clique no arquivo, sem precisar do app principal.</summary>
public partial class FolderWindow : Window
{
    private readonly string _file;
    private readonly Layout _data;

    public FolderWindow(string file, string[]? dropped = null)
    {
        InitializeComponent();
        _file = file;
        _data = SmartFolder.Load(file);
        FolderTitle.Text = System.IO.Path.GetFileNameWithoutExtension(file);
        List.ItemsSource = _data.Items;
        BuildDots();
        ApplyTheme();
        UpdateHint();
        _data.Items.CollectionChanged += (_, _) => { SmartFolder.Save(_file, _data); UpdateHint(); };
        if (dropped is { Length: > 0 }) AddPaths(dropped); // arquivos soltos sobre o ícone da pasta

        // cobre o monitor onde está o mouse
        SourceInitialized += (_, _) =>
        {
            var scr = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
            var dpi = VisualTreeHelper.GetDpi(this);
            Left = scr.Bounds.X / dpi.DpiScaleX;
            Top = scr.Bounds.Y / dpi.DpiScaleY;
            Width = scr.Bounds.Width / dpi.DpiScaleX;
            Height = scr.Bounds.Height / dpi.DpiScaleY;
        };
        Loaded += (_, _) => Activate();
    }

    // ===================== Tema e cor da pasta =====================
    private static readonly (string Nome, string Hex)[] Cores =
    {
        ("Padrão", "#3A4666"), ("Azul", "#3B82F6"), ("Dourado", "#B8892A"),
        ("Verde", "#22A06B"), ("Vermelho", "#D14343"), ("Roxo", "#8B5CF6"), ("Cinza", "#64748B")
    };

    private static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));

    private bool IsDark => !string.Equals(_data.Theme, "Claro", StringComparison.OrdinalIgnoreCase);

    private void BuildDots()
    {
        foreach (var (nome, hex) in Cores)
        {
            var dot = new Border
            {
                Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Margin = new Thickness(3, 0, 3, 0),
                Background = Brush(hex), BorderThickness = new Thickness(2), Cursor = Cursors.Hand,
                ToolTip = nome, Tag = hex
            };
            dot.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                _data.Accent = hex;
                SmartFolder.Save(_file, _data);
                ApplyTheme();
            };
            Dots.Children.Add(dot);
        }
    }

    private void ApplyTheme()
    {
        bool dark = IsDark;
        Resources["PanelBg"] = Brush(dark ? "#F01E2230" : "#F7FAFBFD");
        Resources["PanelBorder"] = Brush(dark ? "#33FFFFFF" : "#33000000");
        Resources["TextMain"] = Brush(dark ? "#FFFFFFFF" : "#FF1B2433");
        Resources["TextDim"] = Brush(dark ? "#99FFFFFF" : "#99445066");
        Resources["HoverBg"] = Brush(dark ? "#33FFFFFF" : "#1F000000");
        Background = Brush(dark ? "#B0000000" : "#80000000");

        string accent = string.IsNullOrWhiteSpace(_data.Accent) ? Cores[0].Hex : _data.Accent;
        try { AccentBar.Background = Brush(accent); } catch { AccentBar.Background = Brush(Cores[0].Hex); }
        ThemeBtn.Content = dark ? "Tema claro" : "Tema escuro";

        foreach (Border dot in Dots.Children)
            dot.BorderBrush = string.Equals((string)dot.Tag, accent, StringComparison.OrdinalIgnoreCase)
                ? (SolidColorBrush)Resources["TextMain"] : Brushes.Transparent;
    }

    private void ThemeToggle_Click(object s, RoutedEventArgs e)
    {
        _data.Theme = IsDark ? "Claro" : "Escuro";
        SmartFolder.Save(_file, _data);
        ApplyTheme();
    }

    private void Export_Click(object s, RoutedEventArgs e)
    {
        string title = System.IO.Path.GetFileNameWithoutExtension(_file);
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Exportar pasta inteligente",
            FileName = title + Package.Ext,
            Filter = "Pacote de pasta inteligente (*.dfpasta)|*.dfpasta"
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            Package.Export(title, _data, dlg.FileName);
            MessageBox.Show(this, "Pacote criado. Envie esse arquivo; a outra pessoa dá duplo clique para importar.\n\n" + dlg.FileName,
                "Pasta inteligente", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Não foi possível exportar: " + ex.Message, "Pasta inteligente", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void UpdateHint() => EmptyHint.Visibility = _data.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void Window_KeyDown(object s, KeyEventArgs e) { if (e.Key == Key.Escape) Close(); }
    private void Bg_Click(object s, MouseButtonEventArgs e) => Close();
    private void Panel_Click(object s, MouseButtonEventArgs e) => e.Handled = true;

    private static Entry? EntryOf(object s) => (s as FrameworkElement)?.DataContext as Entry;

    // clique abre o app; arrastar um item sobre outro reordena
    private Entry? _pressed;
    private Point _pressPos;

    private void Tile_Down(object s, MouseButtonEventArgs e) { _pressed = EntryOf(s); _pressPos = e.GetPosition(this); }

    /// <summary>Botão direito num app: menu nativo do Windows (Abrir local do arquivo, Propriedades...) + opções da pasta.</summary>
    private void Tile_RightClick(object s, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _pressed = null;
        if (EntryOf(s) is not { } en) return;
        Action Call(Action<object, RoutedEventArgs> handler) =>
            () => handler(new FrameworkElement { DataContext = en }, new RoutedEventArgs());
        var extras = new System.Collections.Generic.List<(string, Action)>
        {
            ("Renomear", Call(Menu_Rename)),
            ("Remover da pasta", Call(Menu_Remove))
        };
        ShellMenu.Show(this, en.Path, extras);
    }

    private void Tile_Move(object s, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _pressed == null) return;
        var d = e.GetPosition(this) - _pressPos;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var en = _pressed;
        _pressed = null;
        DragDrop.DoDragDrop((DependencyObject)s, new DataObject("entry", en), DragDropEffects.Move);
    }

    private void Tile_Up(object s, MouseButtonEventArgs e)
    {
        var en = EntryOf(s);
        if (_pressed != null && _pressed == en) { _pressed = null; Launch(en!); }
    }

    private void Tile_Drop(object s, DragEventArgs e)
    {
        e.Handled = true;
        var target = EntryOf(s);
        if (target == null) return;
        if (e.Data.GetData("entry") is Entry src)
        {
            int from = _data.Items.IndexOf(src), to = _data.Items.IndexOf(target);
            if (from >= 0 && to >= 0 && from != to) _data.Items.Move(from, to);
        }
        else if (e.Data.GetData(DataFormats.FileDrop) is string[] files) AddPaths(files);
    }
    private void Menu_Open(object s, RoutedEventArgs e) { if (EntryOf(s) is { } en) Launch(en); }

    private void Launch(Entry en)
    {
        try
        {
            Process.Start(new ProcessStartInfo(en.Path!) { UseShellExecute = true });
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Não foi possível abrir \"{en.Name}\".\n\n{ex.Message}", "Pasta inteligente",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Menu_Rename(object s, RoutedEventArgs e)
    {
        if (EntryOf(s) is not { } en) return;
        var name = InputDialog.Ask(this, "Renomear", en.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        en.Name = name.Trim();
        SmartFolder.Save(_file, _data);
    }

    private void Menu_Remove(object s, RoutedEventArgs e)
    {
        if (EntryOf(s) is not { } en) return;
        MainWindow.RestoreOriginal(en); // se veio da Área de Trabalho, o atalho volta para lá
        _data.Items.Remove(en);
    }

    private void Add_Click(object s, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Escolha programas ou atalhos",
            Multiselect = true,
            Filter = "Programas e atalhos|*.exe;*.lnk;*.url;*.bat;*.cmd|Todos os arquivos|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms)
        };
        if (dlg.ShowDialog(this) == true) AddPaths(dlg.FileNames);
    }

    private void Win_Drop(object s, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) AddPaths(files);
    }

    private void AddPaths(string[] paths)
    {
        foreach (var raw in paths)
        {
            if (!File.Exists(raw) && !Directory.Exists(raw)) continue;
            if (raw.EndsWith(SmartFolder.Ext, StringComparison.OrdinalIgnoreCase)) continue; // não aninha pastas
            string name = Directory.Exists(raw) ? System.IO.Path.GetFileName(raw.TrimEnd('\\'))
                                                : System.IO.Path.GetFileNameWithoutExtension(raw);
            string path = MainWindow.AdoptFromDesktop(raw, out string? original);
            _data.Items.Add(new Entry { Name = string.IsNullOrEmpty(name) ? raw : name, Path = path, OriginalPath = original });
        }
    }
}
