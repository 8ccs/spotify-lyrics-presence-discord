using System.Text.RegularExpressions;
using SpotifyLyricsPresence.Core;

namespace SpotifyLyricsPresence.App;

public sealed class MainForm : Form
{
    private readonly string _settingsPath = Path.Combine(AppSettings.DefaultDirectory, "settings.json");
    private readonly AppSettings _settings;
    private readonly LyricsService _lyrics;
    private readonly DiscordIpcClient _discord = new();
    private readonly SharingEngine _engine;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private readonly Label _song = new() { AutoSize = false, Height = 26, Dock = DockStyle.Top, Font = new Font("Segoe UI Semibold", 12f) };
    private readonly Label _lyric = new() { AutoSize = false, Height = 90, Dock = DockStyle.Top, Font = new Font("Segoe UI", 16f), TextAlign = ContentAlignment.MiddleCenter };
    private readonly Label _lyricStatus = new() { AutoSize = false, Height = 48, Dock = DockStyle.Top, ForeColor = SystemColors.GrayText };
    private readonly Label _conn = new() { AutoSize = false, Height = 22, Dock = DockStyle.Top };
    private readonly TextBox _appId = new() { Width = 220 };
    private readonly TextBox _activityName = new() { Width = 220 };
    private readonly TextBox _imageUrl = new() { Width = 300 };
    private readonly NumericUpDown _offset = new() { Minimum = -10000, Maximum = 10000, Increment = 100, Width = 80 };
    private readonly CheckBox _autoStart = new() { Text = "Start sharing when the app opens", AutoSize = true };
    private readonly Label _hint = new() { AutoSize = false, Dock = DockStyle.Top, ForeColor = SystemColors.GrayText,
        Text = "Positive offset shows lyrics later; negative shows them earlier. Paused playback clears the card." };
    private readonly Button _toggle = new() { Text = "Start sharing", Width = 120, Height = 32 };
    private readonly Button _import = new() { Text = "Import LRC…", Width = 100, Height = 32 };
    private readonly NotifyIcon _tray = new() { Icon = SystemIcons.Application, Text = "Spotify Lyrics Presence" };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 250 };
    private readonly SmtcPlaybackSource _source = new();
    private bool _ticking, _quitting;

    public MainForm()
    {
        _settings = AppSettings.Load(_settingsPath);
        _lyrics = new LyricsService(new LrcLibClient(_http), Path.Combine(AppSettings.DefaultDirectory, "lyrics-cache"));
        if (Environment.GetEnvironmentVariable("SLP_TRACE") is { Length: > 0 } tracePath)
            _discord.Trace = line => { try { File.AppendAllText(tracePath, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}"); } catch (IOException) { } };
        _engine = new SharingEngine(_source, _lyrics, _discord, () => _appId.Text.Trim());
        if (Environment.GetEnvironmentVariable("SLP_TRACE") is { Length: > 0 } lyrTrace)
            _engine.Log = line => { try { File.AppendAllText(lyrTrace, $"{DateTime.Now:HH:mm:ss.fff} LYR {line}{Environment.NewLine}"); } catch (IOException) { } };

        AutoScaleDimensions = new SizeF(96F, 96F); AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Spotify Lyrics Presence"; ClientSize = new Size(560, 520); MinimumSize = new Size(500, 520);
        StartPosition = FormStartPosition.CenterScreen;

        _appId.Text = _settings.ApplicationId;
        _offset.Value = Math.Clamp(_settings.OffsetMs, -10000, 10000);
        _engine.Offset = TimeSpan.FromMilliseconds((int)_offset.Value);

        _autoStart.Checked = _settings.StartSharingOnLaunch;
        _activityName.Text = _settings.ActivityName;
        _discord.ActivityName = _settings.ActivityName;
        _imageUrl.Text = _settings.ImageUrl;
        _discord.ImageUrl = _settings.ImageUrl;

        // Each setting sits on its own auto-sized row, so nothing is clipped at any DPI scale.
        static FlowLayoutPanel Row(params Control[] c)
        {
            var r = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Padding = new Padding(0, 3, 0, 3) };
            r.Controls.AddRange(c);
            return r;
        }
        Label Caption(string t) => new() { Text = t, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 8, 0) };
        var idRow = Row(Caption("Discord application ID:"), _appId);
        var nameRow = Row(Caption("Activity name (optional):"), _activityName);
        var imageRow = Row(Caption("Image URL (optional, https):"), _imageUrl);
        var offsetRow = Row(Caption("Offset (ms):"), _offset);
        var autoRow = Row(_autoStart);
        var buttons = Row(_toggle, _import);
        _offset.Width = 90;
        _hint.Resize += (_, _) => FitHint();

        // Dock=Top stacks in reverse add order, so add bottom-most first.
        Controls.Add(_hint); Controls.Add(buttons); Controls.Add(autoRow); Controls.Add(offsetRow); Controls.Add(nameRow); Controls.Add(imageRow); Controls.Add(idRow);
        Controls.Add(_conn); Controls.Add(_lyricStatus); Controls.Add(_lyric); Controls.Add(_song);
        Padding = new Padding(14);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Show", null, (_, _) => ShowWindow());
        menu.Items.Add("Start / stop sharing", null, async (_, _) => await ToggleAsync());
        menu.Items.Add("Quit", null, async (_, _) => await QuitAsync());
        _tray.ContextMenuStrip = menu; _tray.Visible = true;
        _tray.DoubleClick += (_, _) => ShowWindow();

        _toggle.Click += async (_, _) => await ToggleAsync();
        _import.Click += (_, _) => ImportLrc();
        _offset.ValueChanged += (_, _) => { _engine.Offset = TimeSpan.FromMilliseconds((int)_offset.Value); SaveSettings(); };
        _appId.Leave += (_, _) => SaveSettings();
        _autoStart.CheckedChanged += (_, _) => SaveSettings();
        _activityName.TextChanged += (_, _) => SaveSettings();
        _imageUrl.TextChanged += (_, _) => SaveSettings();
        _timer.Tick += async (_, _) => await TickAsync();
        _timer.Start();

        if (_settings.StartSharingOnLaunch) _ = ToggleAsync();
    }

    /// <summary>Grow the help label to fit its wrapped text at the current width and DPI.</summary>
    private void FitHint()
    {
        var h = TextRenderer.MeasureText(_hint.Text, _hint.Font, new Size(Math.Max(_hint.Width, 50), 0), TextFormatFlags.WordBreak).Height + 6;
        if (_hint.Height != h) _hint.Height = h;
    }

    private void SaveSettings()
    {
        _settings.ApplicationId = _appId.Text.Trim();
        _settings.OffsetMs = (int)_offset.Value;
        _settings.StartSharingOnLaunch = _autoStart.Checked;
        _settings.ActivityName = _activityName.Text.Trim();
        _settings.ImageUrl = _imageUrl.Text.Trim();
        _discord.ImageUrl = _settings.ImageUrl;
        _discord.ActivityName = _settings.ActivityName;
        try { _settings.Save(_settingsPath); } catch (IOException) { }
    }

    private void ShowWindow() { Show(); WindowState = FormWindowState.Normal; Activate(); }

    private async Task ToggleAsync()
    {
        if (_engine.Sharing) { await _engine.StopAsync(DateTimeOffset.UtcNow, CancellationToken.None); }
        else
        {
            if (!Regex.IsMatch(_appId.Text.Trim(), @"^\d{15,25}$"))
            {
                MessageBox.Show(this, "Enter your Discord application ID (a 17–20 digit number from the Developer Portal).", Text);
                return;
            }
            SaveSettings();
            _engine.Start(DateTimeOffset.UtcNow);
        }
        _toggle.Text = _engine.Sharing ? "Stop sharing" : "Start sharing";
    }

    private void ImportLrc()
    {
        var track = _engine.State.Track;
        if (track is null) { MessageBox.Show(this, "Play a Spotify track first; the file is attached to the current track.", Text); return; }
        using var dlg = new OpenFileDialog { Filter = "LRC lyrics (*.lrc)|*.lrc|All files|*.*" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var n = _lyrics.ImportManual(track, File.ReadAllText(dlg.FileName));
        if (n == 0) MessageBox.Show(this, "No timestamped lines found in that file.", Text);
        else { _engine.ReloadLyrics(); MessageBox.Show(this, $"Imported {n} lines for “{track.Title}”.", Text); }
    }

    private async Task TickAsync()
    {
        if (_ticking) return;
        _ticking = true;
        try
        {
            await _engine.TickAsync(DateTimeOffset.UtcNow, CancellationToken.None);
            var s = _engine.State;
            _song.Text = s.IsAd ? "Advertisement" : s.Track is null ? "Spotify: nothing playing" : $"{s.Track.Title} — {s.Track.Artist}{(s.IsPlaying ? "" : "  (paused)")}";
            _lyric.Text = s.LyricText;
            _lyric.RightToLeft = Regex.IsMatch(s.LyricText, @"[؀-ۿ]") ? RightToLeft.Yes : RightToLeft.No;
            _lyricStatus.Text = "Lyrics: " + s.LyricsStatusText;
            _conn.Text = "Discord: " + s.ConnectionText;
            _toggle.Text = s.Sharing ? "Stop sharing" : "Start sharing";
        }
        catch (Exception ex) { _conn.Text = "Error: " + ex.Message; }
        finally { _ticking = false; }
    }

    private async Task QuitAsync()
    {
        _quitting = true; _timer.Stop();
        try { await _engine.StopAsync(DateTimeOffset.UtcNow, CancellationToken.None); } catch { }
        await _discord.DisposeAsync();
        _tray.Visible = false;
        Application.Exit();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_quitting && _settings.MinimizeToTray && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true; Hide(); return;
        }
        base.OnFormClosing(e);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized && _settings.MinimizeToTray) Hide();
    }
}
