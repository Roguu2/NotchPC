using System.Windows;
using NotchPC.Core.Localization;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace NotchPC.App.Tray;

/// <summary>Notification-area icon with a small dark menu (WinForms NotifyIcon, part of the Windows Desktop runtime).</summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ToolStripMenuItem _settingsItem;
    private readonly WinForms.ToolStripMenuItem _toggleItem;
    private readonly WinForms.ToolStripMenuItem _exitItem;
    private readonly Func<bool> _isNotchHidden;

    public TrayIcon(Func<bool> isNotchHidden)
    {
        _isNotchHidden = isNotchHidden;

        _settingsItem = new WinForms.ToolStripMenuItem();
        _settingsItem.Click += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        _toggleItem = new WinForms.ToolStripMenuItem();
        _toggleItem.Click += (_, _) => ToggleNotchRequested?.Invoke(this, EventArgs.Empty);
        _exitItem = new WinForms.ToolStripMenuItem();
        _exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        var menu = new WinForms.ContextMenuStrip
        {
            Renderer = new WinForms.ToolStripProfessionalRenderer(new DarkMenuColors()) { RoundedEdges = true },
            ShowImageMargin = false,
            BackColor = DarkMenuColors.Background,
            ForeColor = Drawing.Color.White,
        };
        menu.Items.Add(_settingsItem);
        menu.Items.Add(_toggleItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(_exitItem);
        menu.Opening += (_, _) => RefreshTexts();

        var iconStream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/NotchPC.ico"))!.Stream;
        using (iconStream)
        {
            _icon = new WinForms.NotifyIcon
            {
                Icon = new Drawing.Icon(iconStream, WinForms.SystemInformation.SmallIconSize),
                ContextMenuStrip = menu,
                Visible = true,
            };
        }

        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left)
            {
                SettingsRequested?.Invoke(this, EventArgs.Empty);
            }
        };
        RefreshTexts();
    }

    public event EventHandler? SettingsRequested;

    public event EventHandler? ToggleNotchRequested;

    public event EventHandler? ExitRequested;

    public void RefreshTexts()
    {
        _icon.Text = Loc.Get("App.Name");
        _settingsItem.Text = Loc.Get("Tray.Settings");
        _toggleItem.Text = Loc.Get(_isNotchHidden() ? "Tray.ShowNotch" : "Tray.HideNotch");
        _exitItem.Text = Loc.Get("Tray.Exit");
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Icon?.Dispose();
        _icon.Dispose();
    }

    private sealed class DarkMenuColors : WinForms.ProfessionalColorTable
    {
        public static readonly Drawing.Color Background = Drawing.Color.FromArgb(32, 32, 34);
        private static readonly Drawing.Color Hover = Drawing.Color.FromArgb(56, 56, 60);
        private static readonly Drawing.Color Border = Drawing.Color.FromArgb(60, 60, 64);

        public override Drawing.Color ToolStripDropDownBackground => Background;

        public override Drawing.Color MenuBorder => Border;

        public override Drawing.Color MenuItemBorder => Hover;

        public override Drawing.Color MenuItemSelected => Hover;

        public override Drawing.Color MenuItemSelectedGradientBegin => Hover;

        public override Drawing.Color MenuItemSelectedGradientEnd => Hover;

        public override Drawing.Color SeparatorDark => Border;

        public override Drawing.Color SeparatorLight => Background;

        public override Drawing.Color ImageMarginGradientBegin => Background;

        public override Drawing.Color ImageMarginGradientMiddle => Background;

        public override Drawing.Color ImageMarginGradientEnd => Background;
    }
}
