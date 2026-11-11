// LiveDesk 호스트: app/index.html(렌더러)을 WebView2로 띄워 바탕화면 아이콘 뒤(WorkerW)에 붙인다.
// 메인 창(보관함 + 편집기)과 트레이 아이콘에서 바탕화면 켜기/끄기, 자동 시작, 종료를 제어한다.
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace LiveDesk;

static class Program
{
    public const string Origin = "https://livedesk.app/";

    [STAThread]
    static void Main(string[] args)
    {
        using var mutex = new Mutex(true, "LiveDesk.SingleInstance", out bool first);
        // 이미 실행 중이면 기존 인스턴스의 창을 띄우게 하고 끝낸다.
        using var show = new EventWaitHandle(false, EventResetMode.AutoReset, "LiveDesk.ShowWindow");
        if (!first) { show.Set(); return; }
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext(show, startHidden: args.Contains("--tray")));
    }
}

// %LocalAppData%\LiveDesk\settings.json
static class Settings
{
    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiveDesk");
    static readonly string File = Path.Combine(Dir, "settings.json");

    public static bool LoadEnabled()
    {
        try
        {
            var root = JsonDocument.Parse(System.IO.File.ReadAllText(File)).RootElement;
            return !root.TryGetProperty("enabled", out var v) || v.ValueKind != JsonValueKind.False;
        }
        catch { return true; }
    }

    public static void SaveEnabled(bool enabled)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            System.IO.File.WriteAllText(File, "{\"enabled\":" + (enabled ? "true" : "false") + "}");
        }
        catch { }
    }
}

sealed class TrayContext : ApplicationContext
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    readonly NotifyIcon tray;
    readonly ToolStripMenuItem onItem, autoItem;
    readonly List<WallpaperForm> walls = new();
    readonly System.Windows.Forms.Timer stateTimer = new() { Interval = 500 };
    readonly System.Windows.Forms.Timer mouseTimer = new() { Interval = 33 };
    readonly Control ui = new();          // 다른 스레드에서 UI 스레드로 넘어오기 위한 핸들
    readonly bool startHidden;
    CoreWebView2Environment? env;
    MainForm? main;
    bool enabled = Settings.LoadEnabled();
    bool hideTipShown;

    public TrayContext(EventWaitHandle show, bool startHidden)
    {
        this.startHidden = startHidden;
        _ = ui.Handle;
        onItem = new ToolStripMenuItem("바탕화면 켜기", null, (_, _) => SetEnabled(!enabled)) { Checked = enabled };
        autoItem = new ToolStripMenuItem("Windows 시작 시 자동 실행", null, (_, _) => SetAutoStart(!IsAutoStart())) { Checked = IsAutoStart() };
        var menu = new ContextMenuStrip();
        menu.Items.Add("LiveDesk 열기", null, (_, _) => OpenMain());
        menu.Items.Add(onItem);
        menu.Items.Add(autoItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("종료", null, (_, _) => Quit());
        tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "LiveDesk", Visible = true, ContextMenuStrip = menu };
        tray.DoubleClick += (_, _) => OpenMain();

        stateTimer.Tick += (_, _) => UpdateState();
        mouseTimer.Tick += (_, _) => SendMouse();
        SystemEvents.DisplaySettingsChanged += (_, _) => BuildWalls();
        ThreadPool.RegisterWaitForSingleObject(show, (_, _) => { try { ui.BeginInvoke(OpenMain); } catch { } }, null, -1, false);
        _ = InitAsync();
    }

    async Task InitAsync()
    {
        try
        {
            string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiveDesk", "WebView2");
            env = await CoreWebView2Environment.CreateAsync(null, data);
        }
        catch (Exception ex)
        {
            MessageBox.Show("WebView2 런타임을 시작할 수 없습니다.\nMicrosoft Edge WebView2 Runtime을 설치한 뒤 다시 실행해 주세요.\n\n" + ex.Message, "LiveDesk");
            Quit();
            return;
        }
        BuildWalls();
        stateTimer.Start();
        if (!startHidden) OpenMain();
    }

    void BuildWalls()
    {
        if (env == null) return;
        foreach (var w in walls) w.Close();
        walls.Clear();
        if (!enabled) return;
        IntPtr workerW = Native.GetWorkerW();
        if (workerW == IntPtr.Zero)
        {
            tray.ShowBalloonTip(4000, "LiveDesk", "바탕화면 창을 찾지 못해 배경화면을 붙일 수 없습니다.", ToolTipIcon.Warning);
            return;
        }
        foreach (var screen in Screen.AllScreens)
        {
            var w = new WallpaperForm(env, screen);
            w.ConfigChanged += () => mouseTimer.Enabled = walls.Any(x => x.NeedsMouse && !x.Paused);
            walls.Add(w);
            w.AttachTo(workerW);
        }
        UpdateState();
    }

    // 전체화면/최대화 창이 덮은 모니터의 배경화면은 그리기를 멈춘다.
    void UpdateState()
    {
        Screen? covered = Native.CoveredScreen();
        foreach (var w in walls)
            w.SetPaused((covered != null && covered.DeviceName == w.Screen.DeviceName));
        mouseTimer.Enabled = walls.Any(x => x.NeedsMouse && !x.Paused);
    }

    void SendMouse()
    {
        if (!Native.GetCursorPos(out var p)) return;
        foreach (var w in walls)
        {
            if (!w.NeedsMouse || w.Paused) continue;
            var b = w.Screen.Bounds;
            if (!b.Contains(p.X, p.Y)) continue;
            string x = ((p.X - b.X) / (double)b.Width).ToString("0.####", CultureInfo.InvariantCulture);
            string y = ((p.Y - b.Y) / (double)b.Height).ToString("0.####", CultureInfo.InvariantCulture);
            w.Post("{\"type\":\"mouse\",\"x\":" + x + ",\"y\":" + y + "}");
        }
    }

    // 바탕화면 켜기/끄기: 끄면 배경 창을 모두 닫고 원래 Windows 배경화면으로 되돌린다.
    void SetEnabled(bool on)
    {
        if (on != enabled)
        {
            enabled = on;
            Settings.SaveEnabled(on);
            onItem.Checked = on;
            if (on) BuildWalls();
            else
            {
                foreach (var w in walls) w.Close();
                walls.Clear();
                mouseTimer.Stop();
                Native.RefreshWallpaper();
            }
        }
        PushState();
    }

    void PushState() =>
        main?.Post("{\"type\":\"state\",\"enabled\":" + (enabled ? "true" : "false") + ",\"autostart\":" + (IsAutoStart() ? "true" : "false") + "}");

    void OpenMain()
    {
        if (env == null) return;
        if (main == null || main.IsDisposed)
        {
            main = new MainForm(env, OnMainMessage);
            main.HiddenToTray += () =>
            {
                if (hideTipShown) return;
                hideTipShown = true;
                tray.ShowBalloonTip(3000, "LiveDesk", "트레이에서 계속 실행 중입니다. 아이콘을 더블클릭하면 다시 열립니다.", ToolTipIcon.Info);
            };
        }
        main.Show();
        if (main.WindowState == FormWindowState.Minimized) main.WindowState = FormWindowState.Normal;
        main.Activate();
    }

    void OnMainMessage(string type, JsonElement root)
    {
        static bool On(JsonElement r) => r.TryGetProperty("on", out var v) && v.ValueKind == JsonValueKind.True;
        switch (type)
        {
            case "ready": PushState(); break;
            case "apply": walls.ForEach(x => x.Post("{\"type\":\"reload\"}")); break;
            case "setEnabled": SetEnabled(On(root)); break;
            case "setAutoStart": SetAutoStart(On(root)); break;
        }
    }

    static bool IsAutoStart()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue("LiveDesk") != null;
    }

    // 자동 실행으로 켜질 때는 창 없이 트레이로만 시작한다.
    void SetAutoStart(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on) k.SetValue("LiveDesk", "\"" + Application.ExecutablePath + "\" --tray");
            else k.DeleteValue("LiveDesk", false);
        }
        catch { }
        autoItem.Checked = IsAutoStart();
        PushState();
    }

    void Quit()
    {
        stateTimer.Stop();
        mouseTimer.Stop();
        foreach (var w in walls) w.Close();
        walls.Clear();
        if (main != null && !main.IsDisposed) { main.AllowClose = true; main.Close(); }
        tray.Visible = false;
        Native.RefreshWallpaper();
        ExitThread();
    }
}

static class Web
{
    public static async Task<CoreWebView2> InitAsync(WebView2 wv, CoreWebView2Environment env, string query)
    {
        await wv.EnsureCoreWebView2Async(env);
        var core = wv.CoreWebView2;
        core.SetVirtualHostNameToFolderMapping("livedesk.app", Path.Combine(AppContext.BaseDirectory, "app"), CoreWebView2HostResourceAccessKind.Allow);
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Navigate(Program.Origin + "index.html" + query);
        return core;
    }

    public static string? MessageType(CoreWebView2WebMessageReceivedEventArgs e, out JsonElement root)
    {
        root = default;
        try
        {
            root = JsonDocument.Parse(e.WebMessageAsJson).RootElement;
            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("type", out var t) ? t.GetString() : null;
        }
        catch { return null; }
    }
}

sealed class WallpaperForm : Form
{
    readonly WebView2 wv = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.Black };
    readonly CoreWebView2Environment env;
    CoreWebView2? core;
    public Screen Screen { get; }
    public bool NeedsMouse { get; private set; }
    public bool Paused { get; private set; }
    public event Action? ConfigChanged;

    public WallpaperForm(CoreWebView2Environment env, Screen screen)
    {
        this.env = env; Screen = screen;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;
        Bounds = screen.Bounds;
        Controls.Add(wv);
    }

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x80 /*TOOLWINDOW*/ | 0x08000000 /*NOACTIVATE*/; return cp; }
    }

    public void AttachTo(IntPtr workerW)
    {
        Show();
        Native.SetParent(Handle, workerW);
        var pt = new Native.POINT { X = Screen.Bounds.X, Y = Screen.Bounds.Y };
        Native.ScreenToClient(workerW, ref pt);
        Native.SetWindowPos(Handle, IntPtr.Zero, pt.X, pt.Y, Screen.Bounds.Width, Screen.Bounds.Height, 0x0004 | 0x0010 | 0x0040);
        _ = InitAsync();
    }

    async Task InitAsync()
    {
        try
        {
            core = await Web.InitAsync(wv, env, "?mode=wallpaper");
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.WebMessageReceived += (_, e) =>
            {
                string? type = Web.MessageType(e, out var root);
                if (type == "config")
                {
                    NeedsMouse = root.TryGetProperty("mouse", out var m) && m.ValueKind == JsonValueKind.True;
                    ConfigChanged?.Invoke();
                }
            };
            core.NavigationCompleted += (_, _) => { if (Paused) Post("{\"type\":\"pause\"}"); };
        }
        catch { /* 창이 먼저 닫힌 경우 */ }
    }

    public void Post(string json)
    {
        try { core?.PostWebMessageAsJson(json); } catch { }
    }

    public void SetPaused(bool paused)
    {
        if (paused == Paused) return;
        Paused = paused;
        Post(paused ? "{\"type\":\"pause\"}" : "{\"type\":\"resume\"}");
        try { if (core != null) core.MemoryUsageTargetLevel = paused ? CoreWebView2MemoryUsageTargetLevel.Low : CoreWebView2MemoryUsageTargetLevel.Normal; } catch { }
    }
}

// 메인 창: 보관함(저장해 둔 바탕화면 목록)과 편집기를 한 WebView2 안에서 보여 준다. 닫으면 트레이로 숨는다.
sealed class MainForm : Form
{
    readonly WebView2 wv = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(20, 22, 28) };
    CoreWebView2? core;
    public bool AllowClose;
    public event Action? HiddenToTray;

    public MainForm(CoreWebView2Environment env, Action<string, JsonElement> onMessage)
    {
        Text = "LiveDesk";
        Icon = SystemIcons.Application;
        StartPosition = FormStartPosition.CenterScreen;
        var wa = Screen.PrimaryScreen!.WorkingArea;
        Size = new Size(Math.Min(1500, wa.Width - 80), Math.Min(920, wa.Height - 80));
        MinimumSize = new Size(960, 600);
        Controls.Add(wv);
        Load += async (_, _) =>
        {
            try
            {
                core = await Web.InitAsync(wv, env, "");
                core.WebMessageReceived += (_, e) =>
                {
                    string? type = Web.MessageType(e, out JsonElement root);
                    if (type != null) onMessage(type, root);
                };
            }
            catch { }
        };
        FormClosing += (_, e) =>
        {
            if (AllowClose || e.CloseReason != CloseReason.UserClosing) return;
            e.Cancel = true;
            Hide();
            HiddenToTray?.Invoke();
        };
    }

    public void Post(string json)
    {
        try { core?.PostWebMessageAsJson(json); } catch { }
    }
}

static class Native
{
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? name);
    [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool SystemParametersInfo(uint action, uint p, string? v, uint flags);

    // Progman에 0x052C를 보내면 아이콘 뒤에 그릴 수 있는 WorkerW 창이 만들어진다.
    public static IntPtr GetWorkerW()
    {
        IntPtr progman = FindWindow("Progman", null);
        if (progman == IntPtr.Zero) return IntPtr.Zero;
        SendMessageTimeout(progman, 0x052C, new IntPtr(0xD), new IntPtr(0x1), 0, 1000, out _);
        IntPtr workerW = IntPtr.Zero;
        EnumWindows((top, _) =>
        {
            if (FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                workerW = FindWindowEx(IntPtr.Zero, top, "WorkerW", null);
            return true;
        }, IntPtr.Zero);
        // Windows 11 24H2 이후: WorkerW가 Progman의 자식으로 들어간다.
        if (workerW == IntPtr.Zero) workerW = FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);
        return workerW;
    }

    // 포그라운드 창이 모니터를 가득 덮고 있으면(전체화면·최대화) 그 모니터를 돌려준다.
    public static Screen? CoveredScreen()
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || !IsWindowVisible(fg)) return null;
        var sb = new StringBuilder(64);
        GetClassName(fg, sb, 64);
        string cls = sb.ToString();
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return null;
        var scr = Screen.FromHandle(fg);
        if (IsZoomed(fg)) return scr;
        if (!GetWindowRect(fg, out var r)) return null;
        var b = scr.Bounds;
        return r.L <= b.Left && r.T <= b.Top && r.R >= b.Right && r.B >= b.Bottom ? scr : null;
    }

    public static void RefreshWallpaper() => SystemParametersInfo(0x0014, 0, null, 0x01);
}
