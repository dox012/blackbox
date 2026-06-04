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

class TrayApp : ApplicationContext
{
    [DllImport("kernel32.dll")]
    static extern uint SetThreadExecutionState(uint esFlags);
    const uint ES_CONTINUOUS       = 0x80000000;
    const uint ES_SYSTEM_REQUIRED  = 0x00000001;
    const uint ES_DISPLAY_REQUIRED = 0x00000002;

    readonly NotifyIcon tray;
    readonly ToolStripMenuItem toggleItem;
    readonly List<Form> blackForms = new List<Form>();
    bool isBlack = false;

    public TrayApp()
    {
        toggleItem = new ToolStripMenuItem("黑屏", null, (s, e) => Toggle());
        var quitItem = new ToolStripMenuItem("退出", null, (s, e) => Quit());

        var menu = new ContextMenuStrip();
        menu.Items.Add(toggleItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(quitItem);

        tray = new NotifyIcon();
        tray.Icon = MakeIcon();
        tray.Text = "blackbox — 单击开启 / 关闭";
        tray.Visible = true;
        tray.ContextMenuStrip = menu;
        tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) Toggle(); };
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

        SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED);
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

        toggleItem.Text = "关闭黑屏";
    }

    void BlackOff()
    {
        if (!isBlack) return;
        isBlack = false;

        foreach (Form f in blackForms)
        {
            f.Close();
            f.Dispose();
        }
        blackForms.Clear();

        Cursor.Show();
        SetThreadExecutionState(ES_CONTINUOUS); // 恢复正常休眠

        toggleItem.Text = "黑屏";
    }

    void Quit()
    {
        BlackOff();
        tray.Visible = false;
        tray.Dispose();
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
            return Icon.FromHandle(bmp.GetHicon());
        }
    }
}
