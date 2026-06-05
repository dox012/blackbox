using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Drawing;

// 伪黑屏托盘程序：常驻右下角，单击托盘图标或菜单开启黑屏，
// 黑屏时盖黑所有显示器、隐藏鼠标、阻止休眠；按 Esc 收回托盘（程序不退出）。
static class Program
{
    [STAThread]
    static void Main()
    {
        try { SetProcessDPIAware(); } catch { }
        Application.EnableVisualStyles();
        Application.Run(new TrayApp());
    }

    [DllImport("user32.dll")]
    static extern bool SetProcessDPIAware();
}

static class NativeMethods
{
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyIcon(IntPtr hIcon);

    [StructLayout(LayoutKind.Sequential)]
    public struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    public static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    public const uint SWP_NOSIZE     = 0x0001;
    public const uint SWP_NOMOVE     = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;

    public delegate void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax,
        IntPtr hmodWinEventProc, WinEventProc lpfnWinEventProc,
        uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    public const uint EVENT_SYSTEM_FOREGROUND  = 0x0003;
    public const uint EVENT_OBJECT_SHOW        = 0x8002;
    public const uint WINEVENT_OUTOFCONTEXT    = 0x0000;
    public const uint WINEVENT_SKIPOWNPROCESS  = 0x0002;

    // 返回距上次键鼠输入的毫秒数；失败返回 0。
    public static long IdleMilliseconds()
    {
        LASTINPUTINFO lii = new LASTINPUTINFO();
        lii.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO));
        if (!GetLastInputInfo(ref lii)) return 0;
        // 无符号相减天然处理 TickCount 回绕（约 49.7 天）。
        uint elapsed = (uint)Environment.TickCount - lii.dwTime;
        return elapsed;
    }
}

class TrayApp : ApplicationContext
{
    [DllImport("kernel32.dll")]
    static extern uint SetThreadExecutionState(uint esFlags);
    const uint ES_CONTINUOUS       = 0x80000000;
    const uint ES_SYSTEM_REQUIRED  = 0x00000001;
    const uint ES_DISPLAY_REQUIRED = 0x00000002;

    readonly NotifyIcon tray;
    readonly Icon trayIcon;
    readonly ToolStripMenuItem toggleItem;
    readonly ToolStripMenuItem autoItem;
    readonly Timer pollTimer;
    readonly List<Form> blackForms = new List<Form>();
    bool isBlack = false;

    // WinEvent hook：黑屏期间实时监听“有窗口显示 / 切到前台”，弹窗一冒出来就立刻
    // 盖回去，避免轮询带来的闪现。委托必须用字段持有，否则会被 GC 回收导致回调崩溃。
    readonly NativeMethods.WinEventProc winEventProc;
    IntPtr hookShow = IntPtr.Zero;
    IntPtr hookForeground = IntPtr.Zero;

    // 定时设置（仅存内存，重启不保留）：
    //   0  = 关闭（交还系统自身的休眠/息屏）
    //   <0 = 常亮（阻止系统休眠/息屏，永不自动黑屏）
    //   >0 = 空闲 N 分钟后自动黑屏（期间阻止系统先行休眠）
    int autoMinutes = 0;

    public TrayApp()
    {
        winEventProc = OnWinEvent;

        toggleItem = new ToolStripMenuItem("黑屏", null, (s, e) => Toggle());

        autoItem = new ToolStripMenuItem("定时黑屏…", null, (s, e) => CustomAuto());

        var quitItem = new ToolStripMenuItem("退出", null, (s, e) => Quit());

        var menu = new ContextMenuStrip();
        menu.Items.Add(toggleItem);
        menu.Items.Add(autoItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(quitItem);

        trayIcon = MakeIcon();
        tray = new NotifyIcon();
        tray.Icon = trayIcon;
        tray.Visible = true;
        tray.ContextMenuStrip = menu;
        tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) Toggle(); };

        // 轮询：判断空闲是否到达自动黑屏阈值；并作为置顶的兜底（实时抢占由 WinEvent hook 负责）。
        pollTimer = new Timer();
        pollTimer.Interval = 1000;
        pollTimer.Tick += OnPollTick;
        pollTimer.Start();

        UpdateAuto(); // 初始化菜单文字、tray 提示和执行状态
    }

    void Toggle()
    {
        if (isBlack) BlackOff();
        else BlackOn();
    }

    void BlackOn()
    {
        if (isBlack) return;
        isBlack = true;

        RefreshExecutionState();
        Cursor.Hide();

        Form main = null;
        foreach (Screen scr in Screen.AllScreens)
        {
            Form f = new Form();
            f.FormBorderStyle = FormBorderStyle.None;
            f.BackColor       = Color.Black;
            f.StartPosition   = FormStartPosition.Manual;
            f.Bounds          = scr.Bounds;
            f.TopMost         = true;
            f.ShowInTaskbar   = false;
            f.KeyPreview      = true;
            f.KeyDown        += (s, e) => { if (e.KeyCode == Keys.Escape) BlackOff(); };
            blackForms.Add(f);
            if (main == null) main = f;
            f.Show();
        }
        if (main != null) { main.Activate(); main.Focus(); }

        HookWindowEvents();
        toggleItem.Text = "关闭黑屏";
    }

    void BlackOff()
    {
        if (!isBlack) return;
        isBlack = false;

        UnhookWindowEvents();

        foreach (Form f in blackForms)
        {
            f.Close();
            f.Dispose();
        }
        blackForms.Clear();

        Cursor.Show();
        RefreshExecutionState(); // 根据定时设置决定是否仍需阻止休眠

        toggleItem.Text = "黑屏";
    }

    // 按当前状态设置线程执行标志：黑屏中、常亮、或正在倒计时自动黑屏时，
    // 都阻止系统休眠/息屏；定时关闭且未黑屏时交还系统默认行为。
    void RefreshExecutionState()
    {
        uint flags = ES_CONTINUOUS;
        if (isBlack || autoMinutes != 0)
            flags |= ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED;
        SetThreadExecutionState(flags);
    }

    void OnPollTick(object sender, EventArgs e)
    {
        if (isBlack)
        {
            ReassertTopmost();
            return;
        }
        if (autoMinutes > 0)
        {
            long thresholdMs = (long)autoMinutes * 60000L;
            if (NativeMethods.IdleMilliseconds() >= thresholdMs)
                BlackOn();
        }
    }

    // 系统 toast 处于更高的窗口层级、无法被普通程序盖住；但程序自身的置顶
    // 弹窗（微信 / QQ 等）与黑屏窗口同层级，后弹出就会压在上面。这里周期性
    // 把黑屏窗口重新抬到置顶最前（不抢焦点），即可重新盖住这类弹窗。
    void ReassertTopmost()
    {
        foreach (Form f in blackForms)
        {
            if (f.IsHandleCreated && !f.IsDisposed)
                NativeMethods.SetWindowPos(f.Handle, NativeMethods.HWND_TOPMOST,
                    0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }
    }

    // 别的窗口一显示或抢到前台，立刻把黑屏抬回最前（几十毫秒内，肉眼基本无闪现）。
    void OnWinEvent(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (!isBlack) return;
        if (idObject != 0) return; // 仅关心顶层窗口本身（OBJID_WINDOW == 0），忽略控件级事件
        ReassertTopmost();
    }

    void HookWindowEvents()
    {
        uint flags = NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS;
        if (hookShow == IntPtr.Zero)
            hookShow = NativeMethods.SetWinEventHook(
                NativeMethods.EVENT_OBJECT_SHOW, NativeMethods.EVENT_OBJECT_SHOW,
                IntPtr.Zero, winEventProc, 0, 0, flags);
        if (hookForeground == IntPtr.Zero)
            hookForeground = NativeMethods.SetWinEventHook(
                NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero, winEventProc, 0, 0, flags);
    }

    void UnhookWindowEvents()
    {
        if (hookShow != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(hookShow);
            hookShow = IntPtr.Zero;
        }
        if (hookForeground != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(hookForeground);
            hookForeground = IntPtr.Zero;
        }
    }

    void SetAuto(int minutes)
    {
        autoMinutes = (minutes < 0) ? -1 : minutes; // 任意负数统一视为常亮
        UpdateAuto();
    }

    void CustomAuto()
    {
        int value;
        if (TryPromptMinutes(autoMinutes, out value))
            SetAuto(value);
    }

    // 刷新菜单文字、托盘提示，并应用执行状态。
    void UpdateAuto()
    {
        string status;
        if (autoMinutes < 0)       status = "常亮，永不黑屏";
        else if (autoMinutes == 0) status = "关闭";
        else                       status = "空闲 " + autoMinutes + " 分钟后黑屏";

        autoItem.Text = "定时黑屏：" + status + "…";
        tray.Text = "blackbox — 单击开/关黑屏（" + status + "）";

        RefreshExecutionState();
    }

    // 弹出一个小窗输入分钟数：负数=常亮，0=关闭，正数=空闲后黑屏。
    static bool TryPromptMinutes(int current, out int minutes)
    {
        minutes = current;
        using (Form dlg = new Form())
        {
            dlg.Text = "定时黑屏";
            dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
            dlg.StartPosition = FormStartPosition.CenterScreen;
            dlg.MinimizeBox = false;
            dlg.MaximizeBox = false;
            dlg.ShowInTaskbar = false;
            dlg.ClientSize = new Size(300, 120);

            Label lbl = new Label();
            lbl.Text = "空闲多少分钟后自动黑屏：\r\n负数 = 常亮，0 = 关闭（用系统默认）";
            lbl.SetBounds(12, 12, 276, 40);

            NumericUpDown num = new NumericUpDown();
            num.Minimum = -1;
            num.Maximum = 1440; // 上限 24 小时
            int clamped = current < -1 ? -1 : (current > 1440 ? 1440 : current);
            num.Value = clamped;
            num.SetBounds(12, 56, 90, 26);

            Button ok = new Button();
            ok.Text = "确定";
            ok.DialogResult = DialogResult.OK;
            ok.SetBounds(122, 84, 75, 26);

            Button cancel = new Button();
            cancel.Text = "取消";
            cancel.DialogResult = DialogResult.Cancel;
            cancel.SetBounds(207, 84, 75, 26);

            dlg.Controls.Add(lbl);
            dlg.Controls.Add(num);
            dlg.Controls.Add(ok);
            dlg.Controls.Add(cancel);
            dlg.AcceptButton = ok;
            dlg.CancelButton = cancel;

            if (dlg.ShowDialog() == DialogResult.OK)
            {
                minutes = (int)num.Value;
                return true;
            }
            return false;
        }
    }

    void Quit()
    {
        pollTimer.Stop();
        pollTimer.Dispose();
        autoMinutes = 0;       // 退出前清除常亮/定时，恢复系统默认休眠
        BlackOff();            // BlackOff 内的 RefreshExecutionState 会还原执行状态
        if (!isBlack) RefreshExecutionState();
        tray.Visible = false;
        tray.Dispose();
        if (trayIcon != null) trayIcon.Dispose();
        Application.Exit();
    }

    // 程序内生成一个纯黑小方块图标，无需外部 ico 文件
    static Icon MakeIcon()
    {
        using (Bitmap bmp = new Bitmap(16, 16))
        {
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Black);
            }
            // GetHicon 返回的 HICON 不归 Icon 管理，需手动 DestroyIcon。
            // 先 Clone 出一个独立托管副本，再销毁原生句柄，避免泄漏。
            IntPtr hIcon = bmp.GetHicon();
            try
            {
                using (Icon tmp = Icon.FromHandle(hIcon))
                {
                    return (Icon)tmp.Clone();
                }
            }
            finally
            {
                NativeMethods.DestroyIcon(hIcon);
            }
        }
    }
}
