// Provedor de miniaturas da "Pasta inteligente" (.pastainteligente).
// Compilado com o .NET Framework 4 (csc.exe do Windows), roda no processo isolado do Windows (dllhost).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Web.Script.Serialization;

namespace DfThumb
{
    [ComImport, Guid("B824B49D-22AC-4161-AC8A-9916E8FA3F7F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IInitializeWithStream { void Initialize(IStream pstream, uint grfMode); }

    [ComImport, Guid("E357FCCD-A995-4576-B01F-234630154E96"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IThumbnailProvider { void GetThumbnail(uint cx, out IntPtr phbmp, out uint pdwAlpha); }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory { [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm); }

    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public short bmPlanes, bmBitsPixel; public IntPtr bmBits; }
    [StructLayout(LayoutKind.Sequential)] struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [ComVisible(true), Guid("7B3F1E52-9A0C-4D6B-8E21-5C4D7A9E1F30"), ClassInterface(ClassInterfaceType.None), ProgId("DesktopFolders.Thumb")]
    public class Provider : IInitializeWithStream, IThumbnailProvider
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);
        [DllImport("gdi32.dll")] static extern int GetObject(IntPtr h, int c, ref BITMAP bm);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
        [DllImport("msimg32.dll")] static extern bool AlphaBlend(IntPtr dst, int x, int y, int w, int h,
            IntPtr src, int sx, int sy, int sw, int sh, BLENDFUNCTION bf);

        List<string> _paths = new List<string>();
        Color _accent = Color.FromArgb(255, 58, 70, 102);   // cor da pasta (campo "Accent" do arquivo)

        public void Initialize(IStream pstream, uint grfMode)
        {
            try
            {
                var ms = new MemoryStream();
                var buf = new byte[65536];
                IntPtr pRead = Marshal.AllocCoTaskMem(4);
                try
                {
                    while (true)
                    {
                        pstream.Read(buf, buf.Length, pRead);
                        int n = Marshal.ReadInt32(pRead);
                        if (n <= 0) break;
                        ms.Write(buf, 0, n);
                    }
                }
                finally { Marshal.FreeCoTaskMem(pRead); }

                string json = Encoding.UTF8.GetString(ms.ToArray()).TrimStart('﻿').Trim();
                if (json.Length == 0) return;
                var root = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
                if (root == null) return;
                if (root.ContainsKey("Accent") && root["Accent"] != null)
                {
                    try { _accent = ColorTranslator.FromHtml(root["Accent"].ToString()); } catch { }
                }
                if (!root.ContainsKey("Items")) return;
                foreach (object o in (IEnumerable)root["Items"])
                {
                    var d = o as Dictionary<string, object>;
                    if (d != null && d.ContainsKey("Path") && d["Path"] != null) _paths.Add(d["Path"].ToString());
                    if (_paths.Count >= 4) break;
                }
            }
            catch { /* arquivo vazio/inválido: miniatura só com o fundo */ }
        }

        public void GetThumbnail(uint cx, out IntPtr phbmp, out uint pdwAlpha)
        {
            int s = (int)Math.Max(16, Math.Min(cx, 512));
            using (var bmp = new Bitmap(s, s, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);

                    float m = s * 0.04f, w = s - 2 * m;
                    using (var path = RoundRect(m, m, w, w, s * 0.22f))
                    using (var br = new LinearGradientBrush(new PointF(0, 0), new PointF(s, s),
                               Color.FromArgb(255, _accent.R, _accent.G, _accent.B),
                               Color.FromArgb(255, (int)(_accent.R * 0.52), (int)(_accent.G * 0.52), (int)(_accent.B * 0.52))))
                    using (var pen = new Pen(Color.FromArgb(110, 255, 255, 255), Math.Max(1f, s / 64f)))
                    {
                        g.FillPath(br, path);
                        g.DrawPath(pen, path);
                    }

                    float pad = s * 0.17f, gap = s * 0.06f, cell = (s - 2 * pad - gap) / 2f;
                    for (int i = 0; i < 4; i++)
                    {
                        float x = pad + (i % 2) * (cell + gap), y = pad + (i / 2) * (cell + gap);
                        using (var cp = RoundRect(x, y, cell, cell, cell * 0.24f))
                        using (var cb = new SolidBrush(Color.FromArgb(i < _paths.Count ? 46 : 22, 255, 255, 255)))
                            g.FillPath(cb, cp);
                    }
                    for (int i = 0; i < _paths.Count && i < 4; i++)
                    {
                        float cellX = pad + (i % 2) * (cell + gap), cellY = pad + (i / 2) * (cell + gap);
                        int isz = (int)(cell * 0.82f);
                        DrawIcon(g, _paths[i], (int)(cellX + (cell - isz) / 2f), (int)(cellY + (cell - isz) / 2f), isz);
                    }
                }
                phbmp = bmp.GetHbitmap(Color.FromArgb(0, 0, 0, 0));
                pdwAlpha = 2; // WTSAT_ARGB
            }
        }

        static GraphicsPath RoundRect(float x, float y, float w, float h, float r)
        {
            var p = new GraphicsPath(); float d = 2 * r;
            p.AddArc(x, y, d, d, 180, 90); p.AddArc(x + w - d, y, d, d, 270, 90);
            p.AddArc(x + w - d, y + h - d, d, d, 0, 90); p.AddArc(x, y + h - d, d, d, 90, 90);
            p.CloseFigure(); return p;
        }

        static string ResolveIconSource(string path)
        {
            try
            {
                if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    Type t = Type.GetTypeFromProgID("WScript.Shell");
                    object sh = Activator.CreateInstance(t);
                    object lnk = t.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, sh, new object[] { path });
                    string target = (string)lnk.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, lnk, null);
                    if (!string.IsNullOrEmpty(target) && (File.Exists(target) || Directory.Exists(target))) return target;
                }
            }
            catch { }
            return path;
        }

        void DrawIcon(Graphics g, string path, int x, int y, int size)
        {
            IntPtr hbm = IntPtr.Zero;
            try
            {
                Guid iid = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");
                IShellItemImageFactory f;
                SHCreateItemFromParsingName(ResolveIconSource(path), IntPtr.Zero, ref iid, out f);
                SIZE sz; sz.cx = size; sz.cy = size;
                if (f.GetImage(sz, 0x4 /*ICONONLY*/, out hbm) != 0 || hbm == IntPtr.Zero) return;

                var bm = new BITMAP();
                GetObject(hbm, Marshal.SizeOf(typeof(BITMAP)), ref bm);
                IntPtr dst = g.GetHdc();
                try
                {
                    IntPtr mem = CreateCompatibleDC(dst);
                    IntPtr old = SelectObject(mem, hbm);
                    BLENDFUNCTION bf; bf.BlendOp = 0; bf.BlendFlags = 0; bf.SourceConstantAlpha = 255; bf.AlphaFormat = 1;
                    AlphaBlend(dst, x, y, size, size, mem, 0, 0, bm.bmWidth, bm.bmHeight, bf);
                    SelectObject(mem, old);
                    DeleteDC(mem);
                }
                finally { g.ReleaseHdc(dst); }
            }
            catch { }
            finally { if (hbm != IntPtr.Zero) DeleteObject(hbm); }
        }
    }
}
