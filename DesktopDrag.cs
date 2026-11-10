using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace DesktopFolders;

[StructLayout(LayoutKind.Sequential)]
internal struct PT { public int x, y; }

/// <summary>Coordenadas físicas (por monitor) ao conversar com o Explorador, que é "per-monitor aware".</summary>
internal static class DpiScope
{
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
    public static IntPtr Enter() => SetThreadDpiAwarenessContext((IntPtr)(-4)); // PER_MONITOR_AWARE_V2
    public static void Leave(IntPtr old) { if (old != IntPtr.Zero) SetThreadDpiAwarenessContext(old); }
}

/// <summary>
/// Lê os ícones da Área de Trabalho do Windows (a lista SysListView32 do Explorador): nome, posição, seleção
/// e qual ícone está sob o mouse. Usa mensagens da lista com memória no processo do Explorador (técnica padrão).
/// </summary>
internal sealed class DesktopList : IDisposable
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr p, IntPtr a, string? c, string? t);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] private static extern bool GetPhysicalCursorPos(out PT p);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr h, ref PT p);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr h, ref PT p);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(PT p);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder sb, int n);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern IntPtr VirtualAllocEx(IntPtr p, IntPtr addr, UIntPtr size, uint type, uint protect);
    [DllImport("kernel32.dll")] private static extern bool VirtualFreeEx(IntPtr p, IntPtr addr, UIntPtr size, uint type);
    [DllImport("kernel32.dll")] private static extern bool WriteProcessMemory(IntPtr p, IntPtr addr, byte[] buf, UIntPtr size, out UIntPtr written);
    [DllImport("kernel32.dll")] private static extern bool ReadProcessMemory(IntPtr p, IntPtr addr, byte[] buf, UIntPtr size, out UIntPtr read);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

    // posições dentro do bloco de memória alocado no Explorador
    private const int OffItem = 0, OffText = 128, OffRect = 640, OffHit = 656, BlockSize = 1024;

    private readonly IntPtr _lv, _proc, _mem;

    private DesktopList(IntPtr lv, IntPtr proc, IntPtr mem) { _lv = lv; _proc = proc; _mem = mem; }

    public static DesktopList? Open()
    {
        try
        {
            IntPtr def = Native.FindDefView();
            if (def == IntPtr.Zero) return null;
            IntPtr lv = FindWindowEx(def, IntPtr.Zero, "SysListView32", null);
            if (lv == IntPtr.Zero) return null;
            GetWindowThreadProcessId(lv, out uint pid);
            IntPtr proc = OpenProcess(0x0008 | 0x0010 | 0x0020, false, pid); // VM_OPERATION | VM_READ | VM_WRITE
            if (proc == IntPtr.Zero) return null;
            IntPtr mem = VirtualAllocEx(proc, IntPtr.Zero, (UIntPtr)BlockSize, 0x3000, 0x04);
            if (mem == IntPtr.Zero) { CloseHandle(proc); return null; }
            return new DesktopList(lv, proc, mem);
        }
        catch { return null; }
    }

    public bool IsAlive => IsWindow(_lv);

    private IntPtr Send(uint msg, IntPtr w, IntPtr l)
    {
        SendMessageTimeout(_lv, msg, w, l, 0x0002 /*SMTO_ABORTIFHUNG*/, 300, out IntPtr r);
        return r;
    }

    private void Write(int offset, byte[] data) => WriteProcessMemory(_proc, _mem + offset, data, (UIntPtr)data.Length, out _);

    private byte[] Read(int offset, int len)
    {
        var buf = new byte[len];
        ReadProcessMemory(_proc, _mem + offset, buf, (UIntPtr)len, out _);
        return buf;
    }

    /// <summary>Posição do mouse (física) e índice do ícone sob ele (-1 se não houver).</summary>
    public int HitTestCursor(out PT screen)
    {
        IntPtr old = DpiScope.Enter();
        try
        {
            GetPhysicalCursorPos(out screen);
            PT c = screen;
            ScreenToClient(_lv, ref c);
            var hit = new byte[24];
            BitConverter.GetBytes(c.x).CopyTo(hit, 0);
            BitConverter.GetBytes(c.y).CopyTo(hit, 4);
            Write(OffHit, hit);
            Send(0x1012 /*LVM_HITTEST*/, IntPtr.Zero, _mem + OffHit);
            var res = Read(OffHit, 24);
            int flags = BitConverter.ToInt32(res, 8), item = BitConverter.ToInt32(res, 12);
            return (flags & 0x000E) != 0 /*LVHT_ONITEM*/ ? item : -1;
        }
        finally { DpiScope.Leave(old); }
    }

    /// <summary>A janela de cima, nesse ponto, é a Área de Trabalho (e não outro programa na frente)?</summary>
    public static bool DesktopIsOnTopAt(PT screen) => DesktopIsOnTopAt(screen, out _);

    public static bool DesktopIsOnTopAt(PT screen, out string cls)
    {
        cls = "";
        IntPtr old = DpiScope.Enter();
        try
        {
            IntPtr h = WindowFromPoint(screen);
            if (h == IntPtr.Zero) return false;
            IntPtr root = GetAncestor(h, 2 /*GA_ROOT*/);
            var sb = new StringBuilder(64);
            GetClassName(root == IntPtr.Zero ? h : root, sb, 64);
            cls = sb.ToString();
            return cls == "Progman" || cls == "WorkerW";
        }
        finally { DpiScope.Leave(old); }
    }

    public string? ItemText(int idx)
    {
        var lvi = new byte[88];
        BitConverter.GetBytes(0x0001).CopyTo(lvi, 0);                       // mask = LVIF_TEXT
        BitConverter.GetBytes(idx).CopyTo(lvi, 4);                          // iItem
        BitConverter.GetBytes((long)(_mem + OffText)).CopyTo(lvi, 24);      // pszText
        BitConverter.GetBytes(255).CopyTo(lvi, 32);                         // cchTextMax
        Write(OffItem, lvi);
        Write(OffText, new byte[512]);
        Send(0x1073 /*LVM_GETITEMTEXTW*/, (IntPtr)idx, _mem + OffItem);
        string s = Encoding.Unicode.GetString(Read(OffText, 512));
        int z = s.IndexOf('\0');
        return z >= 0 ? s.Substring(0, z) : s;
    }

    /// <summary>Retângulo do ícone (com o nome) em pixels físicos de tela.</summary>
    public (int x, int y, int w, int h)? ItemRect(int idx)
    {
        IntPtr old = DpiScope.Enter();
        try
        {
            Write(OffRect, new byte[16]);                                    // left = LVIR_BOUNDS (0)
            if (Send(0x100E /*LVM_GETITEMRECT*/, (IntPtr)idx, _mem + OffRect) == IntPtr.Zero) return null;
            var r = Read(OffRect, 16);
            var tl = new PT { x = BitConverter.ToInt32(r, 0), y = BitConverter.ToInt32(r, 4) };
            var br = new PT { x = BitConverter.ToInt32(r, 8), y = BitConverter.ToInt32(r, 12) };
            ClientToScreen(_lv, ref tl);
            ClientToScreen(_lv, ref br);
            return (tl.x, tl.y, br.x - tl.x, br.y - tl.y);
        }
        finally { DpiScope.Leave(old); }
    }

    public List<int> SelectedItems()
    {
        var list = new List<int>();
        int n = (int)Send(0x1004 /*LVM_GETITEMCOUNT*/, IntPtr.Zero, IntPtr.Zero);
        for (int i = 0; i < n; i++)
            if (((int)Send(0x102C /*LVM_GETITEMSTATE*/, (IntPtr)i, (IntPtr)0x0002 /*LVIS_SELECTED*/) & 2) != 0) list.Add(i);
        return list;
    }

    public int Count => (int)Send(0x1004, IntPtr.Zero, IntPtr.Zero);

    /// <summary>Posição do ícone na lista (coordenadas da própria lista do Explorador).</summary>
    public PT? GetItemPosition(int idx)
    {
        Write(OffRect, new byte[16]);
        if (Send(0x1010 /*LVM_GETITEMPOSITION*/, (IntPtr)idx, _mem + OffRect) == IntPtr.Zero) return null;
        var r = Read(OffRect, 8);
        return new PT { x = BitConverter.ToInt32(r, 0), y = BitConverter.ToInt32(r, 4) };
    }

    public void SetItemPosition(int idx, int x, int y) =>
        Send(0x100F /*LVM_SETITEMPOSITION*/, (IntPtr)idx, (IntPtr)((y << 16) | (x & 0xFFFF)));

    public int FindByText(string text)
    {
        int n = Count;
        for (int i = 0; i < n; i++)
            if (string.Equals(ItemText(i), text, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    public void Dispose()
    {
        try { VirtualFreeEx(_proc, _mem, UIntPtr.Zero, 0x8000); CloseHandle(_proc); } catch { }
    }
}

/// <summary>Janela transparente do tamanho do ícone alvo, que recebe o "soltar" no lugar do Explorador.</summary>
internal sealed class CatcherWindow : Window
{
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr h, int i, int v);

    public event Action<string[]>? Dropped;

    public CatcherWindow()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        AllowDrop = true;
        Background = new SolidColorBrush(Color.FromArgb(0x38, 0x3B, 0x82, 0xF6));
        Content = new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(8)
        };
        DragEnter += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
            e.Handled = true;
            DesktopDragWatcher.Log($"receptora: DragEnter (FileDrop={e.Data.GetDataPresent(DataFormats.FileDrop)}; formatos={string.Join("|", e.Data.GetFormats())})");
        };
        DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None; e.Handled = true; };
        Drop += (_, e) =>
        {
            e.Effects = DragDropEffects.Link;   // não mexe no ícone de origem
            e.Handled = true;
            DesktopDragWatcher.Log("receptora: Drop");
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files) { DesktopDragWatcher.Log("receptora: arquivos=" + string.Join(" | ", files)); Dropped?.Invoke(files); }
        };
        DragLeave += (_, _) => DesktopDragWatcher.Log("receptora: DragLeave");
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        IntPtr h = new WindowInteropHelper(this).Handle;
        SetWindowLong(h, -20, GetWindowLong(h, -20) | 0x08000000 /*NOACTIVATE*/ | 0x80 /*TOOLWINDOW*/);
    }
}

/// <summary>
/// Observa o mouse: quando o usuário arrasta um ícone da Área de Trabalho e passa sobre OUTRO ícone, coloca uma
/// janela receptora sobre o alvo. Soltar ali pede o agrupamento (o Explorador não vê esse "soltar").
/// Fora dos ícones nada muda: o Explorador continua movendo ícones normalmente.
/// </summary>
internal sealed class DesktopDragWatcher
{
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);

    private enum State { Idle, Ignore, Pressed, Dragging }

    private readonly Window _owner;
    private readonly DispatcherTimer _timer;
    private readonly CatcherWindow _catcher = new();
    private DesktopList? _list;
    private State _state = State.Idle;
    private PT _press;
    private int _pressIdx;
    private HashSet<int> _selected = new();
    private int _catcherIdx = -1;
    private int _lastOver = -2;
    private string? _catcherName;
    private DateTime _hideAt = DateTime.MinValue;
    private string? _lastDropKey;
    private DateTime _lastDropAt = DateTime.MinValue;

    /// <summary>(arquivos arrastados, nome do ícone alvo na Área de Trabalho, posição do ícone alvo)</summary>
    public event Action<string[], string, PT?>? GroupRequested;

    public bool Enabled { get; set; } = true;

    // diagnóstico: só grava se existir o arquivo-sinal %APPDATA%\DesktopFolders\debug-drag.flag
    private static readonly string LogDir = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopFolders");
    internal static void Log(string s)
    {
        try
        {
            if (File.Exists(System.IO.Path.Combine(LogDir, "debug-drag.flag")))
                File.AppendAllText(System.IO.Path.Combine(LogDir, "debug-drag.log"), $"{DateTime.Now:HH:mm:ss.fff} {s}\n");
        }
        catch { }
    }

    public DesktopDragWatcher(Window owner)
    {
        _owner = owner;
        _timer = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(30) };
        _timer.Tick += (_, _) => { try { Tick(); } catch { } };
        _catcher.Dropped += files =>
        {
            string? target = _catcherName;
            if (target == null) return;
            // o mesmo "soltar" pode chegar duas vezes: ignora a repetição (senão o atalho entra duplicado na pasta)
            string key = target + "|" + string.Join("|", files);
            if (key == _lastDropKey && DateTime.UtcNow - _lastDropAt < TimeSpan.FromSeconds(3)) { Log("soltar repetido ignorado"); return; }
            _lastDropKey = key; _lastDropAt = DateTime.UtcNow;
            PT? pos = null;
            try { if (_list != null && _catcherIdx >= 0) pos = _list.GetItemPosition(_catcherIdx); } catch { }
            // fora da chamada OLE do Explorador: abrir diálogos aqui travaria a Área de Trabalho
            _owner.Dispatcher.BeginInvoke(new Action(() => GroupRequested?.Invoke(files, target, pos)));
            _hideAt = DateTime.UtcNow;   // some logo depois do soltar
        };
    }

    public void Start() => _timer.Start();
    public void Stop() { _timer.Stop(); HideCatcher(); _list?.Dispose(); _list = null; }

    private bool EnsureList()
    {
        if (_list != null && !_list.IsAlive) { _list.Dispose(); _list = null; }
        _list ??= DesktopList.Open();
        return _list != null;
    }

    private void Tick()
    {
        bool down = (GetAsyncKeyState(0x01) & 0x8000) != 0;

        // esconde a janela receptora depois de um tempo do soltar (dá tempo do "soltar" chegar)
        if (_hideAt != DateTime.MinValue && DateTime.UtcNow - _hideAt > TimeSpan.FromMilliseconds(400)) { _hideAt = DateTime.MinValue; HideCatcher(); }

        if (!Enabled || !down)
        {
            if (_state == State.Dragging && _catcher.IsVisible && _hideAt == DateTime.MinValue) _hideAt = DateTime.UtcNow; // soltou: espera o drop
            _state = State.Idle;
            return;
        }

        switch (_state)
        {
            case State.Idle:
                if (!EnsureList()) { _state = State.Ignore; return; }
                int idx = _list!.HitTestCursor(out PT p);
                bool top = DesktopList.DesktopIsOnTopAt(p, out string cls);
                Log($"botão pressionado em ({p.x},{p.y}) ícone={idx} janelaNoTopo='{cls}' ehDesktop={top}");
                if (idx >= 0 && top) { _state = State.Pressed; _press = p; _pressIdx = idx; }
                else _state = State.Ignore;
                break;

            case State.Pressed:
            {
                if (!EnsureList()) { _state = State.Ignore; return; }
                _list!.HitTestCursor(out PT cur);
                if (Math.Abs(cur.x - _press.x) + Math.Abs(cur.y - _press.y) > 12)
                {
                    _selected = new HashSet<int>(_list.SelectedItems());
                    _selected.Add(_pressIdx);
                    _state = State.Dragging;
                    Log($"arrasto iniciado; selecionados: {string.Join(",", _selected)}");
                }
                break;
            }

            case State.Dragging:
            {
                if (!EnsureList()) return;
                int over = _list!.HitTestCursor(out PT cur);
                bool topNow = DesktopList.DesktopIsOnTopAt(cur, out string clsNow);
                if (over != _lastOver) { Log($"sob o mouse: ícone={over} janelaNoTopo='{clsNow}'"); _lastOver = over; }
                if (over >= 0 && !_selected.Contains(over) && (topNow || clsNow == "" || _catcher.IsVisible))
                {
                    if (over != _catcherIdx) { ShowCatcher(over); Log($"receptora mostrada sobre ícone {over} '{_catcherName}'"); }
                }
                else if (_catcher.IsVisible && _hideAt == DateTime.MinValue) HideCatcher();
                break;
            }
        }
    }

    private void ShowCatcher(int idx)
    {
        var rect = _list!.ItemRect(idx);
        string? name = _list.ItemText(idx);
        if (rect == null || string.IsNullOrEmpty(name)) return;

        var dpi = VisualTreeHelper.GetDpi(_owner);
        _catcher.Left = rect.Value.x / dpi.DpiScaleX;
        _catcher.Top = rect.Value.y / dpi.DpiScaleY;
        _catcher.Width = rect.Value.w / dpi.DpiScaleX;
        _catcher.Height = rect.Value.h / dpi.DpiScaleY;
        _catcherIdx = idx;
        _catcherName = name;
        if (!_catcher.IsVisible) _catcher.Show();
    }

    private void HideCatcher()
    {
        if (_catcher.IsVisible) _catcher.Hide();
        _catcherIdx = -1;
    }
}
