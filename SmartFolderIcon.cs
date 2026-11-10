using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DesktopFolders;

/// <summary>
/// Gera o ícone (.ico) de uma pasta inteligente: quadrado na cor da pasta com até 4 apps (os ícones reais dos
/// atalhos). O atalho da pasta na Área de Trabalho aponta para esse .ico, então funciona em qualquer lugar,
/// inclusive no OneDrive e sem componentes instalados no Explorador.
/// </summary>
internal static class SmartFolderIcon
{
    private static readonly int[] Sizes = { 256, 64, 48, 32, 16 };

    public static void Create(IList<Entry> items, string accentHex, string icoPath)
    {
        Color accent;
        try { accent = (Color)ColorConverter.ConvertFromString(string.IsNullOrWhiteSpace(accentHex) ? "#3A4666" : accentHex); }
        catch { accent = Color.FromRgb(0x3A, 0x46, 0x66); }

        var icons = new List<ImageSource?>();
        foreach (var it in items.Take(4))
        {
            ImageSource? img = null;
            try { img = it.Icon; } catch { }
            icons.Add(img);
        }

        BitmapSource big = Render(icons, accent, 256);
        var frames = Sizes.Select(s => s == 256 ? big : Scale(big, s)).ToList();
        WriteIco(icoPath, frames);
    }

    private static BitmapSource Render(List<ImageSource?> icons, Color accent, int s)
    {
        var dv = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
        using (var dc = dv.RenderOpen())
        {
            double m = s * 0.04, w = s - 2 * m;
            Color dark = Color.FromRgb((byte)(accent.R * 0.52), (byte)(accent.G * 0.52), (byte)(accent.B * 0.52));
            var bg = new LinearGradientBrush(accent, dark, new Point(0, 0), new Point(1, 1));
            var border = new Pen(new SolidColorBrush(Color.FromArgb(110, 255, 255, 255)), Math.Max(1, s / 64.0));
            dc.DrawRoundedRectangle(bg, border, new Rect(m, m, w, w), s * 0.22, s * 0.22);

            double pad = s * 0.17, gap = s * 0.06, cell = (s - 2 * pad - gap) / 2;
            for (int i = 0; i < 4; i++)
            {
                double x = pad + (i % 2) * (cell + gap), y = pad + (i / 2) * (cell + gap);
                var fill = new SolidColorBrush(Color.FromArgb((byte)(i < icons.Count ? 46 : 22), 255, 255, 255));
                dc.DrawRoundedRectangle(fill, null, new Rect(x, y, cell, cell), cell * 0.24, cell * 0.24);
                if (i < icons.Count && icons[i] != null)
                {
                    double isz = cell * 0.82;
                    dc.DrawImage(icons[i], new Rect(x + (cell - isz) / 2, y + (cell - isz) / 2, isz, isz));
                }
            }
        }
        var rtb = new RenderTargetBitmap(s, s, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    private static BitmapSource Scale(BitmapSource src, int s)
    {
        var dv = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
        using (var dc = dv.RenderOpen()) dc.DrawImage(src, new Rect(0, 0, s, s));
        var rtb = new RenderTargetBitmap(s, s, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>
    /// ICO no formato que a Área de Trabalho aceita: quadros pequenos no formato clássico (DIB 32 bits) e só o de
    /// 256 px em PNG. (Com todos os quadros em PNG, o Explorador mostra uma página em branco.)
    /// </summary>
    private static void WriteIco(string path, List<BitmapSource> frames)
    {
        var data = new List<byte[]>();
        foreach (var f in frames) data.Add(f.PixelWidth >= 256 ? ToPng(f) : ToDib(f));

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        bw.Write((ushort)0);
        bw.Write((ushort)1);
        bw.Write((ushort)frames.Count);
        int offset = 6 + 16 * frames.Count;
        for (int i = 0; i < frames.Count; i++)
        {
            int px = frames[i].PixelWidth;
            bw.Write((byte)(px >= 256 ? 0 : px));
            bw.Write((byte)(px >= 256 ? 0 : px));
            bw.Write((byte)0);
            bw.Write((byte)0);
            bw.Write((ushort)1);
            bw.Write((ushort)32);
            bw.Write((uint)data[i].Length);
            bw.Write((uint)offset);
            offset += data[i].Length;
        }
        foreach (var d in data) bw.Write(d);
    }

    private static byte[] ToPng(BitmapSource f)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(f));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>Quadro ICO clássico: cabeçalho BITMAPINFOHEADER + pixels BGRA de baixo para cima + máscara AND.</summary>
    private static byte[] ToDib(BitmapSource src)
    {
        int s = src.PixelWidth;
        var pix = new byte[s * s * 4];
        src.CopyPixels(pix, s * 4, 0);   // Pbgra32 (pré-multiplicado), de cima para baixo

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(40);                    // biSize
        bw.Write(s);                     // biWidth
        bw.Write(s * 2);                 // biHeight (imagem + máscara)
        bw.Write((ushort)1);             // biPlanes
        bw.Write((ushort)32);            // biBitCount
        bw.Write(0);                     // biCompression = BI_RGB
        bw.Write(0);                     // biSizeImage
        bw.Write(0); bw.Write(0);        // resolução
        bw.Write(0); bw.Write(0);        // cores

        for (int y = s - 1; y >= 0; y--)
            for (int x = 0; x < s; x++)
            {
                int i = (y * s + x) * 4;
                byte b = pix[i], g = pix[i + 1], r = pix[i + 2], a = pix[i + 3];
                if (a > 0 && a < 255)    // desfaz o pré-multiplicado
                {
                    b = (byte)Math.Min(255, b * 255 / a);
                    g = (byte)Math.Min(255, g * 255 / a);
                    r = (byte)Math.Min(255, r * 255 / a);
                }
                bw.Write(b); bw.Write(g); bw.Write(r); bw.Write(a);
            }

        int maskRow = ((s + 31) / 32) * 4;
        bw.Write(new byte[maskRow * s]); // máscara AND toda zero: a transparência vem do canal alfa
        return ms.ToArray();
    }
}
