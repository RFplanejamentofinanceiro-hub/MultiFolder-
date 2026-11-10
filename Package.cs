using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace DesktopFolders;

/// <summary>
/// Pacote portátil de uma pasta inteligente (.dfpasta = zip): leva os atalhos (.lnk/.url) junto,
/// para compartilhar a pasta com outra pessoa. Programas por caminho (.exe) dependem de existirem no outro PC.
/// </summary>
public static class Package
{
    public const string Ext = ".dfpasta";

    private class Manifest
    {
        public string Titulo { get; set; } = "Pasta inteligente";
        public Layout Dados { get; set; } = new();
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static void Export(string title, Layout data, string zipPath)
    {
        if (File.Exists(zipPath)) File.Delete(zipPath);
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);

        var copy = new Layout { Theme = data.Theme, Accent = data.Accent };
        int n = 0;
        foreach (var it in data.Items)
        {
            var e = new Entry { Name = it.Name, Path = it.Path };
            string ext = System.IO.Path.GetExtension(it.Path ?? "").ToLowerInvariant();
            if (it.Path != null && (ext == ".lnk" || ext == ".url") && File.Exists(it.Path))
            {
                string name = $"atalhos/{n++}{ext}";
                zip.CreateEntryFromFile(it.Path, name);
                e.Path = "pkg:" + name;
            }
            copy.Items.Add(e);
        }

        var entry = zip.CreateEntry("pasta.json");
        using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        w.Write(JsonSerializer.Serialize(new Manifest { Titulo = title, Dados = copy }, Json));
    }

    /// <summary>Importa o pacote criando uma pasta inteligente na Área de Trabalho. Retorna o arquivo criado e o resumo.</summary>
    public static (string file, string resumo) Import(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var me = zip.GetEntry("pasta.json") ?? throw new InvalidDataException("Pacote inválido (sem pasta.json).");
        Manifest man;
        using (var r = new StreamReader(me.Open(), Encoding.UTF8))
            man = JsonSerializer.Deserialize<Manifest>(r.ReadToEnd()) ?? throw new InvalidDataException("Pacote vazio.");

        string appData = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopFolders", "Atalhos", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(appData);

        int total = 0, faltando = 0;
        foreach (var it in man.Dados.Items)
        {
            total++;
            if (it.Path != null && it.Path.StartsWith("pkg:"))
            {
                var z = zip.GetEntry(it.Path.Substring(4));
                if (z == null) { faltando++; continue; }
                // nome seguro: só o nome do arquivo, nunca um caminho vindo do pacote
                string dest = System.IO.Path.Combine(appData, System.IO.Path.GetFileName(z.FullName));
                z.ExtractToFile(dest, true);
                it.Path = dest;
            }
            else if (it.Path != null && !File.Exists(it.Path) && !Directory.Exists(it.Path)) faltando++;
        }

        string desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string file = SmartFolder.CreateNew(desk, man.Titulo, man.Dados);   // atalho na Área de Trabalho + dados

        string resumo = $"\"{man.Titulo}\" importada na Área de Trabalho com {total} item(ns).";
        if (faltando > 0) resumo += $"\n\n{faltando} item(ns) apontam para programas que não existem neste computador e não vão abrir.";
        return (file, resumo);
    }
}
