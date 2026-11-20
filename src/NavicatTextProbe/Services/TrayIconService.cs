using NavicatTextProbe.Models;

namespace NavicatTextProbe.Services;

internal sealed class TrayIconService : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _icon;
    private readonly System.Windows.Forms.ToolStripMenuItem _assistantItem;
    private readonly System.Windows.Forms.ToolStripMenuItem _taskbarItem;
    private readonly System.Windows.Forms.ToolStripMenuItem _mobileItem;

    public TrayIconService()
    {
        _assistantItem = new System.Windows.Forms.ToolStripMenuItem("启用 Assistant（Ctrl+Alt+S）");
        _taskbarItem = new System.Windows.Forms.ToolStripMenuItem("任务栏内双行建议");
        _mobileItem = new System.Windows.Forms.ToolStripMenuItem("手机局域网页");
        var openItem = new System.Windows.Forms.ToolStripMenuItem("打开主界面");
        var settingsItem = new System.Windows.Forms.ToolStripMenuItem("设置…");
        var exitItem = new System.Windows.Forms.ToolStripMenuItem("退出");

        _assistantItem.Click += (_, _) => ToggleAssistantRequested?.Invoke(this, EventArgs.Empty);
        _taskbarItem.Click += (_, _) => ToggleTaskbarRequested?.Invoke(this, EventArgs.Empty);
        _mobileItem.Click += (_, _) => ToggleMobileRequested?.Invoke(this, EventArgs.Empty);
        openItem.Click += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
        settingsItem.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.AddRange([
            openItem,
            settingsItem,
            new System.Windows.Forms.ToolStripSeparator(),
            _assistantItem,
            _taskbarItem,
            _mobileItem,
            new System.Windows.Forms.ToolStripSeparator(),
            exitItem
        ]);

        _icon = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "Assistant",
            ContextMenuStrip = menu,
            Visible = true
        };
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? OpenRequested;
    public event EventHandler? SettingsRequested;
    public event EventHandler? ToggleAssistantRequested;
    public event EventHandler? ToggleTaskbarRequested;
    public event EventHandler? ToggleMobileRequested;
    public event EventHandler? ExitRequested;

    public void UpdateState(AppSettings settings)
    {
        _assistantItem.Checked = settings.AssistantEnabled;
        _taskbarItem.Checked = settings.TaskbarPanelEnabled;
        _mobileItem.Checked = settings.MobileServerEnabled;
    }

    public void ShowBalloon(string title, string message)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = message;
        _icon.ShowBalloonTip(2500);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
    }
}
