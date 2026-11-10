using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace DesktopFolders;

/// <summary>Clique direito num atalho → "Adicionar à pasta inteligente...": escolhe a pasta e adiciona.</summary>
public static class Chooser
{
    public static Window Create(string[] itemPaths)
    {
        var dirs = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
        }.Where(d => !string.IsNullOrEmpty(d) && Directory.Exists(d)).Distinct();

        var folders = dirs.SelectMany(d => Directory.GetFiles(d, "*" + SmartFolder.Ext)).OrderBy(f => f).ToList();
        string itemName = itemPaths.Length == 1
            ? $"\"{Path.GetFileNameWithoutExtension(itemPaths[0])}\""
            : $"{itemPaths.Length} itens";

        var win = new Window
        {
            Title = "Adicionar à pasta inteligente",
            Width = 380, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize, Topmost = true
        };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            Text = folders.Count == 0
                ? "Nenhuma pasta inteligente na Área de Trabalho.\nCrie uma em: clique direito → Novo → Pasta inteligente."
                : $"Em qual pasta adicionar {itemName}?",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10)
        });

        var list = new ListBox { MinHeight = 90, MaxHeight = 240, Margin = new Thickness(0, 0, 0, 12) };
        foreach (var f in folders) list.Items.Add(new ListBoxItem { Content = Path.GetFileNameWithoutExtension(f), Tag = f, Padding = new Thickness(6, 4, 6, 4) });
        if (list.Items.Count > 0) list.SelectedIndex = 0;

        var ok = new Button { Content = "Adicionar", IsDefault = true, Width = 90, Padding = new Thickness(0, 4, 0, 4), HorizontalAlignment = HorizontalAlignment.Right };
        void Add()
        {
            if (list.SelectedItem is not ListBoxItem sel) return;
            string file = (string)sel.Tag;
            var data = SmartFolder.Load(file);
            foreach (var itemPath in itemPaths)
            {
                string path = MainWindow.AdoptFromDesktop(itemPath, out string? original); // sai da Área de Trabalho
                data.Items.Add(new Entry { Name = Path.GetFileNameWithoutExtension(itemPath), Path = path, OriginalPath = original });
            }
            SmartFolder.Save(file, data);
            win.Close();
        }
        ok.Click += (_, _) => Add();
        list.MouseDoubleClick += (_, _) => Add();
        win.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) win.Close(); };

        if (folders.Count > 0) { panel.Children.Add(list); panel.Children.Add(ok); }
        else
        {
            var close = new Button { Content = "Fechar", IsDefault = true, Width = 90, HorizontalAlignment = HorizontalAlignment.Right };
            close.Click += (_, _) => win.Close();
            panel.Children.Add(close);
        }
        win.Content = panel;
        return win;
    }
}
