using System.Diagnostics;
using DataverseToSql.Sync;

namespace DataverseToSql.UI;

/// <summary>
/// "Check for updates" dialog: compares the running version with the latest GitHub release, shows the
/// release notes and downloads and applies the update. <see cref="DialogResult.OK"/> means the app must
/// close so the update can finish.
/// </summary>
internal sealed class UpdateForm : Form
{
    private readonly Label _state = new()
    {
        AutoSize = false, Dock = DockStyle.Top, Height = 44, Font = Theme.CardTitle, ForeColor = Theme.Text,
        TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false, Padding = new Padding(0, 0, 0, 4),
    };

    private readonly Label _detail = new()
    {
        AutoSize = false, Dock = DockStyle.Top, Height = 40, ForeColor = Theme.TextMuted,
        TextAlign = ContentAlignment.TopLeft, UseMnemonic = false,
    };

    private readonly TextBox _notes = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, Dock = DockStyle.Fill,
        BorderStyle = BorderStyle.None, BackColor = Color.White, ForeColor = Theme.Text, TabStop = false,
    };

    private readonly Panel _notesHost = new() { Dock = DockStyle.Fill, Visible = false, Margin = Padding.Empty };
    private readonly ProgressStrip _progress = new() { Dock = DockStyle.Top, Height = 8, Visible = false, Margin = new Padding(0, 6, 0, 6) };
    private readonly LinkLabel _openPage = new() { AutoSize = true, Text = "Open the releases page", Anchor = AnchorStyles.Left, Visible = false, Margin = new Padding(0, 9, 3, 3) };
    private readonly LinkLabel _openChangelog = new() { AutoSize = true, Text = "Full changelog", Anchor = AnchorStyles.Left, Margin = new Padding(14, 9, 3, 3) };

    private readonly Button _action = Theme.Primary(WizardLayout.MakeButton("&Download and install", 170));
    private readonly Button _recheck = WizardLayout.MakeButton("Check &again", 110);
    private readonly Button _close = WizardLayout.MakeButton("Close", 95);

    private readonly CancellationTokenSource _cts = new();
    private ReleaseInfo? _release;
    private bool _busy;

    /// <summary>Creates the dialog. Pass a release that a background check already found to skip the first check.</summary>
    public UpdateForm(ReleaseInfo? known = null)
    {
        Text = "Updates";
        Font = Theme.Base;
        ForeColor = Theme.Text;
        BackColor = Theme.PageBack;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(620, 430);
        _release = known;

        var card = new Card("Dataverse to SQL Sync") { Dock = DockStyle.Fill, AutoSize = false, Margin = Padding.Empty };
        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 4, 0, 0) };
        var notesCard = new Card("What is new in this version") { Dock = DockStyle.Fill, AutoSize = false, Margin = Padding.Empty };
        notesCard.Controls.Add(_notes);
        _notesHost.Controls.Add(notesCard);
        body.Controls.Add(_notesHost);
        body.Controls.Add(_progress);
        body.Controls.Add(_detail);
        body.Controls.Add(_state);
        card.Controls.Add(body);

        var buttons = new TableLayoutPanel
        {
            Dock = DockStyle.Bottom, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 5, RowCount = 1,
            Padding = new Padding(16, 10, 16, 12), BackColor = Theme.PageBack,
        };
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        buttons.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _action.Margin = new Padding(3, 3, 3, 3);
        _close.Margin = new Padding(12, 3, 3, 3);
        var links = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, Anchor = AnchorStyles.Left, Margin = Padding.Empty };
        links.Controls.AddRange(new Control[] { _openPage, _openChangelog });
        buttons.Controls.Add(links, 0, 0);
        buttons.Controls.Add(new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty }, 1, 0);
        buttons.Controls.Add(_recheck, 2, 0);
        buttons.Controls.Add(_action, 3, 0);
        buttons.Controls.Add(_close, 4, 0);

        var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 14, 16, 0), BackColor = Theme.PageBack };
        host.Controls.Add(card);
        Controls.Add(host);
        Controls.Add(buttons);

        _action.Click += async (_, _) => await StartUpdateAsync();
        _recheck.Click += async (_, _) => await CheckAsync();
        _close.Click += (_, _) => Close();
        _openPage.LinkClicked += (_, _) => OpenUrl(_release?.HtmlUrl ?? UpdateService.ReleasesPageUrl);
        _openChangelog.LinkClicked += (_, _) => OpenUrl(UpdateService.ChangelogUrl);
        CancelButton = _close;

        Theme.ApplyTree(this);

        Shown += async (_, _) =>
        {
            if (_release is null)
            {
                await CheckAsync();
            }
            else
            {
                ShowRelease(_release);
            }
        };
        FormClosing += (_, e) =>
        {
            if (_busy)
            {
                e.Cancel = true; // a download is running; Cancel stops it first
                return;
            }
            _cts.Cancel();
        };
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _recheck.Enabled = !busy;
        _close.Enabled = !busy;
        UseWaitCursor = busy;
    }

    private async Task CheckAsync()
    {
        SetBusy(true);
        _action.Visible = false;
        _notesHost.Visible = false;
        _openPage.Visible = false;
        _progress.Visible = true;
        _progress.SetValue(null, animate: true);
        _state.Text = "Looking for a newer version...";
        _detail.Text = $"Current version {UpdateService.CurrentVersionText}  ·  {UpdateService.Repository}";
        try
        {
            _release = await UpdateService.GetLatestReleaseAsync(_cts.Token);
            _progress.Visible = false;
            if (_release is null)
            {
                _state.ForeColor = Theme.TextMuted;
                _state.Text = "No release has been published yet.";
                _detail.Text = $"Current version {UpdateService.CurrentVersionText}. Nothing to update to.";
                _openPage.Visible = true;
            }
            else
            {
                ShowRelease(_release);
            }
        }
        catch (OperationCanceledException)
        {
            _progress.Visible = false;
        }
        catch (Exception ex)
        {
            _progress.Visible = false;
            _state.ForeColor = Theme.DangerText;
            _state.Text = "The update check failed.";
            _detail.Text = ex.Message;
            _openPage.Visible = true;
            AppLog.Error("Update check failed.", ex);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ShowRelease(ReleaseInfo release)
    {
        var kind = UpdateService.Kind;
        var (url, size, isInstaller) = release.AssetFor(kind);
        _openPage.Visible = true;

        if (!UpdateService.IsNewer(release))
        {
            _state.ForeColor = Theme.SuccessText;
            _state.Text = $"You are up to date (version {UpdateService.CurrentVersionText}).";
            _detail.Text = $"The newest release is {release.Version.ToString(3)}"
                           + (release.Published is { } when ? $", published {when.LocalDateTime:d}." : ".");
            _action.Visible = false;
            _notesHost.Visible = false;
            return;
        }

        _state.ForeColor = Theme.Accent;
        _state.Text = $"Version {release.Version.ToString(3)} is available.";
        var how = kind == InstallKind.Installed
            ? "This copy is installed, so the update runs the new setup program and starts the app again."
            : "This copy is portable, so the update replaces the executable in place and starts the app again.";
        _detail.Text = $"You have {UpdateService.CurrentVersionText}"
                       + (release.Published is { } p ? $"  ·  published {p.LocalDateTime:d}" : "")
                       + (size > 0 ? $"  ·  download {size / 1024d / 1024d:N0} MB" : "")
                       + Environment.NewLine + how;
        _notes.Text = string.IsNullOrWhiteSpace(release.Notes)
            ? "(no release notes)"
            : release.Notes.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
        _notes.Select(0, 0);
        _notesHost.Visible = true;

        if (url is null)
        {
            _state.ForeColor = Theme.WarningText;
            _detail.Text = $"Release {release.Tag} has no downloadable file. Open the releases page to get it by hand.";
            _action.Visible = false;
            return;
        }
        _action.Text = isInstaller ? "&Download and install" : "&Download and replace";
        _action.Visible = true;
        _action.Focus();
    }

    private async Task StartUpdateAsync()
    {
        if (_release is null)
        {
            return;
        }
        var (url, size, isInstaller) = _release.AssetFor(UpdateService.Kind);
        if (url is null)
        {
            return;
        }

        SetBusy(true);
        _action.Enabled = false;
        _progress.Visible = true;
        _progress.SetValue(0, animate: false);
        _state.ForeColor = Theme.Text;
        _state.Text = $"Downloading version {_release.Version.ToString(3)}...";
        var progress = new Progress<(double? Fraction, long Bytes, long Total)>(p =>
        {
            _progress.SetValue(p.Fraction, animate: p.Fraction is null);
            _detail.Text = p.Total > 0
                ? $"{p.Bytes / 1024d / 1024d:N0} MB of {p.Total / 1024d / 1024d:N0} MB"
                : $"{p.Bytes / 1024d / 1024d:N0} MB";
        });

        try
        {
            var file = await UpdateService.DownloadAsync(url, size, progress, _cts.Token);
            _state.Text = "Applying the update...";
            _detail.Text = "The app closes now and starts again on the new version.";
            var mustExit = await Task.Run(() => UpdateService.Apply(file, isInstaller), _cts.Token);
            SetBusy(false);
            if (mustExit)
            {
                DialogResult = DialogResult.OK; // MainForm closes the app
                Close();
            }
        }
        catch (OperationCanceledException)
        {
            SetBusy(false);
            _progress.Visible = false;
            _action.Enabled = true;
            _state.Text = "The update was cancelled.";
            _detail.Text = "";
        }
        catch (Exception ex)
        {
            SetBusy(false);
            _progress.Visible = false;
            _action.Enabled = true;
            _state.ForeColor = Theme.DangerText;
            _state.Text = "The update failed.";
            _detail.Text = ex.Message;
            _openPage.Visible = true;
            AppLog.Error("Update failed.", ex);
        }
    }

    /// <summary>Opens <paramref name="url"/> in the default browser.</summary>
    public static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Error($"Could not open {url}.", ex);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts.Cancel();
            _cts.Dispose();
        }
        base.Dispose(disposing);
    }
}
