using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace DesktopFolders;

/// <summary>Um item na área de trabalho: um programa (Path preenchido) ou uma pasta (Path nulo).</summary>
public class Entry : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

    private string _name = "";
    public string Name { get => _name; set { _name = value; Raise(); } }

    /// <summary>Caminho do programa/atalho. Nulo quando o item é uma pasta.</summary>
    public string? Path { get; set; }

    /// <summary>Posição na área de trabalho (-1 = ainda sem posição).</summary>
    private double _x = -1, _y = -1;
    public double X { get => _x; set { _x = value; Raise(); } }
    public double Y { get => _y; set { _y = value; Raise(); } }

    /// <summary>Se o atalho foi movido da Área de Trabalho, guarda o lugar original para devolver depois.</summary>
    public string? OriginalPath { get; set; }

    /// <summary>Filhos (só usado quando é pasta).</summary>
    public ObservableCollection<Entry> Items { get; set; } = new();

    [JsonIgnore] public bool IsFolder => Path == null;

    private ImageSource? _icon;
    [JsonIgnore] public ImageSource? Icon => IsFolder ? null : (_icon ??= Native.GetIcon(Path!));

    [JsonIgnore] public IEnumerable<ImageSource?> Preview => Items.Take(4).Select(i => i.Icon).ToList();

    public void RefreshPreview() => Raise(nameof(Preview));
}

public class Layout
{
    public bool HideNative { get; set; }

    /// <summary>Nome da pasta inteligente (usado se o atalho dela não for encontrado).</summary>
    public string? Title { get; set; }

    /// <summary>Tema do popup da pasta inteligente: "Escuro" ou "Claro".</summary>
    public string Theme { get; set; } = "Escuro";

    /// <summary>Cor de destaque da pasta (#RRGGBB); também tinge a miniatura.</summary>
    public string Accent { get; set; } = "#3A4666";

    /// <summary>Soltar um ícone da Área de Trabalho sobre outro cria uma pasta inteligente.</summary>
    public bool DragGroup { get; set; } = true;

    /// <summary>Verifica novas versões no GitHub (no máximo uma vez por dia).</summary>
    public bool AutoUpdateCheck { get; set; } = true;
    public DateTime LastUpdateCheck { get; set; } = DateTime.MinValue;

    /// <summary>Copia o layout para o OneDrive ao sair do programa.</summary>
    public bool AutoBackup { get; set; } = true;
    public ObservableCollection<Entry> Items { get; set; } = new();
}

public static class Store
{
    private static readonly string Dir =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopFolders");

    private static string FilePath => System.IO.Path.Combine(Dir, "layout.json");

    public static Layout Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Layout>(File.ReadAllText(FilePath)) ?? new Layout();
        }
        catch { /* arquivo corrompido: começa vazio */ }
        return new Layout();
    }

    public static void Save(Layout layout)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(layout, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* sem permissão: ignora */ }
    }
}
