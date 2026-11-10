using System;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace DesktopFolders;

/// <summary>Verifica no GitHub se há uma versão mais nova. Só avisa e abre a página; não instala nada sozinho.</summary>
public static class Updater
{
    public const string Repo = "RFplanejamentofinanceiro-hub/MultiFolder-";

    public static string CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.0.0";

    /// <summary>Retorna (tag, url) da última versão publicada, ou (null, null) se ainda não houver nenhuma.</summary>
    public static async Task<(string? tag, string? url)> LatestAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DesktopFolders-update-check");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        using var resp = await http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest");
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return (null, null);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        string? tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        string? url = doc.RootElement.TryGetProperty("html_url", out var u) ? u.GetString() : null;
        return (tag, url);
    }

    public static bool IsNewer(string tag, string current)
    {
        return Version.TryParse(tag.TrimStart('v', 'V'), out var a) &&
               Version.TryParse(current, out var b) && a > b;
    }
}
