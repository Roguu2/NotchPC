using System;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NotchPC.Setup;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        bool Has(string arg) => args.Any(a => string.Equals(a, arg, StringComparison.OrdinalIgnoreCase));

        if (Has("--uninstall"))
        {
            // The uninstaller is the copy inside the install folder: that folder is the one to remove.
            Installer.InstallDir = Application.StartupPath;
            return Uninstall();
        }

        if (!Installer.HasPayload)
        {
            MessageBox.Show(Strings.NoPayload, "NotchPC", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        if (Has("--update"))
        {
            return SilentUpdate(relaunch: Has("--relaunch"));
        }

        using var form = new SetupForm();
        Application.Run(form);
        return 0;
    }

    /// <summary>
    /// Automatic update started by NotchPC: no window, same folder and shortcuts as before, NotchPC started again
    /// afterwards when asked to (--relaunch). Errors are shown, since nobody would notice otherwise.
    /// </summary>
    private static int SilentUpdate(bool relaunch)
    {
        try
        {
            Installer.InstallDir = Installer.ExistingInstallDir() ?? Installer.DefaultInstallDir;
            Installer.Install(Installer.HasDesktopShortcut, _ => { });
            if (relaunch)
            {
                Installer.Launch(autostart: null);
            }

            return 0;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageBox.Show(Strings.Failed(ex.Message), "NotchPC", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private static int Uninstall()
    {
        if (MessageBox.Show(Strings.UninstallAsk, "NotchPC", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return 1;
        }

        var removeData = MessageBox.Show(Strings.UninstallData, "NotchPC", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        Installer.Uninstall(removeData);
        MessageBox.Show(Strings.UninstallDone, "NotchPC", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return 0;
    }
}

/// <summary>The setup window: what will happen, three choices, one button.</summary>
internal sealed class SetupForm : Form
{
    private static readonly Color Back = Color.FromArgb(24, 24, 27);
    private static readonly Color Fore = Color.FromArgb(245, 245, 247);
    private static readonly Color Muted = Color.FromArgb(160, 160, 168);
    private static readonly Color Accent = Color.FromArgb(10, 132, 255);

    private readonly TextBox _folder;
    private readonly Button _browse;
    private readonly CheckBox _autostart;
    private readonly CheckBox _desktop;
    private readonly CheckBox _launch;
    private readonly ProgressBar _progress;
    private readonly Label _status;
    private readonly Button _install;
    private readonly Button _cancel;
    private bool _done;

    public SetupForm()
    {
        Text = $"NotchPC {Installer.Version}";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        ClientSize = new Size(520, 392);
        BackColor = Back;
        ForeColor = Fore;
        Font = new Font("Segoe UI", 10f);

        var logo = new PictureBox
        {
            Image = Icon.ToBitmap(),
            SizeMode = PictureBoxSizeMode.Zoom,
            Bounds = new Rectangle(28, 26, 48, 48),
        };
        var title = new Label { Text = "NotchPC", Font = new Font("Segoe UI Semibold", 18f), AutoSize = true, Location = new Point(88, 24) };
        var version = new Label { Text = Strings.Subtitle(Installer.Version), ForeColor = Muted, AutoSize = true, Location = new Point(90, 58) };
        var info = new Label
        {
            Text = Installer.IsInstalled ? Strings.Update : Strings.Info,
            ForeColor = Muted,
            Bounds = new Rectangle(28, 92, 464, 44),
        };

        // Install folder (an update keeps the current one).
        var folderLabel = new Label { Text = Strings.Folder, AutoSize = true, Location = new Point(28, 142), ForeColor = Fore };
        _folder = new TextBox
        {
            Text = Installer.InstallDir,
            ReadOnly = true,
            Bounds = new Rectangle(28, 166, 352, 28),
            BackColor = Color.FromArgb(36, 36, 40),
            ForeColor = Fore,
            BorderStyle = BorderStyle.FixedSingle,
        };
        _browse = Button(Strings.Browse, new Point(388, 164), primary: false);
        _browse.Size = new Size(104, 30);
        _browse.Click += (_, _) => PickFolder();
        _browse.Enabled = !Installer.IsInstalled;

        _autostart = Check(Strings.Autostart, 212, true);
        _desktop = Check(Strings.Desktop, 242, Installer.IsInstalled ? Installer.HasDesktopShortcut : true);
        _launch = Check(Strings.Launch, 272, true);
        _progress = new ProgressBar { Bounds = new Rectangle(28, 310, 464, 8), Visible = false };
        _status = new Label { ForeColor = Muted, Bounds = new Rectangle(28, 324, 220, 44) };

        _install = Button(Installer.IsInstalled ? Strings.UpdateButton : Strings.InstallButton, new Point(372, 340), primary: true);
        _cancel = Button(Strings.Cancel, new Point(252, 340), primary: false);
        _install.Click += async (_, _) => await InstallAsync();
        _cancel.Click += (_, _) => Close();
        AcceptButton = _install;
        CancelButton = _cancel;

        Controls.AddRange([logo, title, version, info, folderLabel, _folder, _browse, _autostart, _desktop, _launch, _progress, _status, _install, _cancel]);
    }

    private void PickFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = Strings.PickFolder,
            ShowNewFolderButton = true,
            SelectedPath = System.IO.Path.GetDirectoryName(Installer.InstallDir) ?? Installer.InstallDir,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            return;
        }

        var target = Installer.Normalize(dialog.SelectedPath);
        if (Installer.CheckWritable(target) is { } problem)
        {
            MessageBox.Show(this, Strings.NotWritable(target, problem), "NotchPC", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Installer.InstallDir = target;
        _folder.Text = target;
    }

    private static CheckBox Check(string text, int y, bool on) => new()
    {
        Text = text,
        Checked = on,
        AutoSize = true,
        Location = new Point(30, y),
        ForeColor = Fore,
    };

    private static Button Button(string text, Point at, bool primary)
    {
        var b = new Button
        {
            Text = text,
            Bounds = new Rectangle(at, new Size(120, 34)),
            FlatStyle = FlatStyle.Flat,
            BackColor = primary ? Accent : Color.FromArgb(44, 44, 48),
            ForeColor = Color.White,
            Cursor = Cursors.Hand,
        };
        b.FlatAppearance.BorderSize = 0;
        return b;
    }

    private async Task InstallAsync()
    {
        if (_done)
        {
            Close();
            return;
        }

        if (Installer.RunningApp().Count > 0 &&
            MessageBox.Show(this, Strings.CloseRunning, "NotchPC", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK)
        {
            return;
        }

        _install.Enabled = false;
        _cancel.Enabled = false;
        _autostart.Enabled = _desktop.Enabled = _launch.Enabled = _browse.Enabled = false;
        _progress.Visible = true;
        _status.Text = Strings.Working;
        var desktop = _desktop.Checked;
        try
        {
            await Task.Run(() => Installer.Install(desktop, p => BeginInvoke(new Action(() => _progress.Value = Math.Min(100, p)))));
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MessageBox.Show(this, Strings.Failed(ex.Message), "NotchPC", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _install.Enabled = _cancel.Enabled = true;
            _status.Text = "";
            return;
        }

        if (_launch.Checked)
        {
            Installer.Launch(_autostart.Checked);
        }

        _done = true;
        _status.Text = _launch.Checked ? Strings.DoneRunning : Strings.Done;
        _install.Text = Strings.Close;
        _install.Enabled = true;
    }
}

/// <summary>Polish when Windows is in Polish, English otherwise.</summary>
internal static class Strings
{
    private static readonly bool Pl = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "pl";

    public static string Subtitle(string version) => Pl ? $"wersja {version} · notch, widgety i pilot dla Windows" : $"version {version} · notch, widgets and remote for Windows";

    public static string Info => Pl
        ? $"Zostanie zainstalowany dla Ciebie w {Installer.InstallDir}. Nie potrzeba uprawnień administratora ani instalowania .NET."
        : $"It will be installed for you in {Installer.InstallDir}. No administrator rights and no .NET install needed.";

    public static string Update => Pl
        ? "NotchPC jest już zainstalowany – zostanie zaktualizowany. Twoje ustawienia zostaną."
        : "NotchPC is already installed – it will be updated. Your settings are kept.";

    public static string Autostart => Pl ? "Uruchamiaj razem z Windows" : "Start with Windows";

    public static string Folder => Pl ? "Folder instalacji" : "Install folder";

    public static string Browse => Pl ? "Zmień…" : "Change…";

    public static string PickFolder => Pl
        ? "Wybierz, gdzie zainstalować NotchPC (powstanie w nim folder NotchPC)."
        : "Choose where to install NotchPC (a NotchPC folder is created inside).";

    public static string NotWritable(string dir, string why) => Pl
        ? $"Nie można zapisywać w {dir}.\n{why}\n\nFoldery takie jak Program Files wymagają uprawnień administratora – wybierz inny, np. na swoim dysku albo w folderze użytkownika."
        : $"Cannot write to {dir}.\n{why}\n\nFolders such as Program Files need administrator rights – pick another one, e.g. on your own drive or in your user folder.";

    public static string Desktop => Pl ? "Skrót na pulpicie" : "Desktop shortcut";

    public static string Launch => Pl ? "Uruchom NotchPC po instalacji" : "Start NotchPC when done";

    public static string InstallButton => Pl ? "Zainstaluj" : "Install";

    public static string UpdateButton => Pl ? "Zaktualizuj" : "Update";

    public static string Cancel => Pl ? "Anuluj" : "Cancel";

    public static string Close => Pl ? "Zamknij" : "Close";

    public static string Working => Pl ? "Instalowanie…" : "Installing…";

    public static string Done => Pl ? "Gotowe. NotchPC jest w menu Start." : "Done. NotchPC is in the Start menu.";

    public static string DoneRunning => Pl ? "Gotowe. NotchPC działa – notch jest u góry ekranu." : "Done. NotchPC is running – the notch is at the top of the screen.";

    public static string CloseRunning => Pl ? "NotchPC jest uruchomiony – zostanie zamknięty na czas instalacji." : "NotchPC is running – it will be closed during the install.";

    public static string NoPayload => Pl ? "Ten instalator jest pusty (zbudowany bez programu)." : "This installer is empty (built without the program).";

    public static string UninstallAsk => Pl ? "Odinstalować NotchPC?" : "Uninstall NotchPC?";

    public static string UninstallData => Pl
        ? "Usunąć też Twoje ustawienia i dane NotchPC (układ widgetów, historia schowka, zakładki i logowania w panelu web)?\n\nWybierz „Nie”, jeśli zamierzasz zainstalować go ponownie."
        : "Also remove your NotchPC settings and data (widget layout, clipboard history, web panel bookmarks and logins)?\n\nChoose \"No\" if you plan to install it again.";

    public static string UninstallDone => Pl ? "NotchPC został odinstalowany." : "NotchPC was uninstalled.";

    public static string Failed(string why) => Pl ? $"Instalacja nie powiodła się:\n{why}" : $"The install failed:\n{why}";
}
