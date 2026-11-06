using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Diagnostics;
using Microsoft.Win32;
using System.Security.Cryptography;
using System.Drawing.Drawing2D;

namespace ClipboardHistory.Windows;

internal sealed class Entry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime Created { get; set; } = DateTime.Now;
    public string? Text { get; set; }
    public byte[]? Image { get; set; }
    public string Fingerprint { get; set; } = "";
    public bool Pinned { get; set; }
    public bool Favorite { get; set; }
    public Entry Clone() => (Entry)MemberwiseClone();
    public override string ToString()
    {
        if (Text is null) return $"Image · {Created:g}";
        var title = Text.Replace('\r', ' ').Replace('\n', ' ');
        return title.Length > 90 ? title[..90] + "…" : title;
    }
}

internal sealed class HotkeySettings
{
    public Keys Key { get; set; } = Keys.Space;
    public uint Modifiers { get; set; } = 0x0002 | 0x0004;
    public int MaxEntries { get; set; } = 500;
    public int MaxMegabytes { get; set; } = 250;
    public int RetentionHours { get; set; } = 24;
    public bool RecordingPaused { get; set; }
    public string ExcludedProcesses { get; set; } = "";
    public string Display => ((Modifiers & 0x0002) != 0 ? "Ctrl+" : "")
        + ((Modifiers & 0x0001) != 0 ? "Alt+" : "")
        + ((Modifiers & 0x0004) != 0 ? "Shift+" : "") + Key;
}

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Contains("--self-test"))
        {
            try { WindowsSelfTests.Run(); }
            catch (Exception error) { Console.Error.WriteLine(error); Environment.Exit(1); }
            return;
        }
        var demo = args.Contains("--demo");
        using var mutex = new Mutex(true, "Local\\ClipboardHistoryOpenSource", out var first);
        if (!first && !demo) return;
        ApplicationConfiguration.Initialize();
        var screenshot = args.FirstOrDefault(value => value.StartsWith("--screenshot="))?["--screenshot=".Length..];
        Application.Run(new ClipboardForm(demo, screenshot));
    }
}

internal sealed class ClipboardForm : Form
{
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public INPUTUNION value; }
    [StructLayout(LayoutKind.Explicit)] private struct INPUTUNION {
        [FieldOffset(0)] public KEYBDINPUT keyboard;
        [FieldOffset(0)] public MOUSEINPUT mouse;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int x, y; public uint data, flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort key; public ushort scan; public uint flags; public uint time; public IntPtr extra; }
    internal static int InputStructureSize => Marshal.SizeOf<INPUT>();

    private const int HotkeyId = 1;
    private readonly string historyFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipboardHistory");
    private readonly string settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipboardHistory", "settings.json");
    private readonly List<Entry> entries = [];
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 250 };
    private readonly TextBox search = new() { PlaceholderText = "Search in History…", Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, Font = new Font("Segoe UI", 17) };
    private readonly ListBox list = new() { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, Font = new Font("Segoe UI", 10), IntegralHeight = false, DrawMode = DrawMode.OwnerDrawFixed };
    private readonly TextBox preview = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 11) };
    private readonly PictureBox picture = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, Visible = false };
    private readonly Label info = new() { AutoSize = true, ForeColor = Color.Silver };
    private readonly NotifyIcon tray = new() { Icon = SystemIcons.Application, Text = "Clipboard History", Visible = true };
    private readonly Button paste = new() { Text = "Paste  ↵", Width = 125 };
    private readonly CheckBox favorites = new() { Text = "★ Favorites", Appearance = Appearance.Button, AutoSize = true, Dock = DockStyle.Right };
    private readonly Label storageStatus = new() { Dock = DockStyle.Top, AutoSize = true, ForeColor = Color.DarkOrange, Visible = false, Padding = new Padding(12) };
    private readonly HistoryThumbnailCache thumbnails = new();
    private WindowsHistoryStorage? history;
    private List<Entry> savedEntries = [];
    private bool storageUnavailable;
    private CancellationTokenSource? pasteCancellation;
    private uint lastSequence;
    private IntPtr previousWindow;
    private bool quitting;
    private readonly bool demo;
    private HotkeySettings hotkey = new();
    private DateTime lastPrune = DateTime.MinValue;
    private static readonly HashSet<string> PasswordManagers = new(StringComparer.OrdinalIgnoreCase) {
        "1Password", "Bitwarden", "KeePassXC", "KeePass", "Dashlane", "LastPass"
    };
    private static readonly Regex SensitiveContent = new(
        "-----BEGIN (?:[A-Z0-9 ]+ )?PRIVATE KEY-----|(?i:authorization\\s*:\\s*(?:bearer|basic)\\s+\\S+)|\\b(?:sk-(?:proj-|ant-)?[A-Za-z0-9_-]{20,}|gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|AKIA[A-Z0-9]{16})\\b|(?im:^\\s*(?:api[_-]?key|client[_-]?secret|password|passwd|access[_-]?token)\\s*[:=]\\s*[\"']?[^\\s\"']{8,})",
        RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    public ClipboardForm(bool demo = false, string? screenshotPath = null)
    {
        this.demo = demo;
        Text = "Clipboard History";
        Width = 1000; Height = 650; MinimumSize = new Size(700, 450);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(20, 22, 27); ForeColor = Color.White;
        KeyPreview = true; ShowInTaskbar = false;
        try
        {
            if (!demo && File.Exists(settingsPath)) hotkey = JsonSerializer.Deserialize<HotkeySettings>(File.ReadAllText(settingsPath)) ?? new();
        }
        catch (Exception e) when (e is IOException or JsonException) { hotkey = new(); }
        hotkey.MaxEntries = Math.Clamp(hotkey.MaxEntries, 100, 5000);
        hotkey.MaxMegabytes = Math.Clamp(hotkey.MaxMegabytes, 40, 2048);
        hotkey.RetentionHours = Math.Clamp(hotkey.RetentionHours, 1, 168);
        tray.Text = hotkey.RecordingPaused ? "Clipboard History (paused)" : "Clipboard History";

        var header = new Panel { Dock = DockStyle.Top, Height = 68, Padding = new Padding(20) };
        header.Controls.Add(search); header.Controls.Add(favorites);
        var split = new SplitContainer { Size = new Size(960, 540), Dock = DockStyle.Fill, SplitterDistance = 350, BackColor = Color.FromArgb(30, 33, 39) };
        split.Panel1.Controls.Add(list);
        var details = new Panel { Dock = DockStyle.Fill, Padding = new Padding(18) };
        details.Controls.Add(preview); details.Controls.Add(picture);
        info.Dock = DockStyle.Bottom; info.Height = 28;
        details.Controls.Add(info); split.Panel2.Controls.Add(details);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 58, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(12) };
        var actions = new Button { Text = "Actions", Width = 105 };
        var pin = new Button { Text = "Pin / Unpin", AutoSize = true };
        var favorite = new Button { Text = "★ Favorite", AutoSize = true };
        var delete = new Button { Text = "Delete", AutoSize = true };
        pin.Click += (_, _) => ChangeSelected(entry => entry.Pinned = !entry.Pinned);
        favorite.Click += (_, _) => ChangeSelected(entry => entry.Favorite = !entry.Favorite);
        delete.Click += (_, _) => DeleteSelected();
        footer.Controls.Add(actions); footer.Controls.Add(paste); footer.Controls.Add(delete); footer.Controls.Add(favorite); footer.Controls.Add(pin);
        Controls.Add(split); Controls.Add(footer); Controls.Add(storageStatus); Controls.Add(header);
        list.BackColor = Color.FromArgb(23, 26, 31); list.ForeColor = Color.White;
        list.ItemHeight = (int)Math.Round(78 * DeviceDpi / 96.0);
        list.DrawItem += DrawHistoryRow;
        favorites.CheckedChanged += (_, _) => RefreshList();

        var menu = new ContextMenuStrip();
        var pause = menu.Items.Add(hotkey.RecordingPaused ? "Resume Recording" : "Pause Recording");
        pause.Click += (_, _) => {
            hotkey.RecordingPaused = !hotkey.RecordingPaused;
            lastSequence = GetClipboardSequenceNumber();
            pause.Text = hotkey.RecordingPaused ? "Resume Recording" : "Pause Recording";
            tray.Text = hotkey.RecordingPaused ? "Clipboard History (paused)" : "Clipboard History";
            SaveSettings();
        };
        menu.Items.Add("Privacy Settings…", null, (_, _) => ChangePrivacy());
        menu.Items.Add("Copy to Clipboard", null, (_, _) => CopySelected());
        menu.Items.Add("Paste as Plain Text  Ctrl+Shift+V", null, (_, _) => { if (list.SelectedItem is Entry { Text: not null }) PasteSelected(); });
        menu.Items.Add("Clear History", null, (_, _) => ClearHistory());
        menu.Items.Add("Keyboard Shortcut…", null, (_, _) => ChangeShortcut());
        menu.Items.Add("History Limits…", null, (_, _) => ChangeHistoryLimits());
        var startup = new ToolStripMenuItem("Start with Windows") { Checked = !demo && StartupEnabled(), Enabled = !demo };
        startup.Click += (_, _) => { ToggleStartup(); startup.Checked = StartupEnabled(); };
        menu.Items.Add(startup);
        menu.Items.Add("Retry Encrypted History", null, (_, _) => LoadHistory());
        menu.Items.Add("Quit", null, (_, _) => Quit());
        actions.Click += (_, _) => menu.Show(actions, 0, -menu.PreferredSize.Height);
        tray.ContextMenuStrip = new ContextMenuStrip();
        tray.ContextMenuStrip.Items.Add("Open", null, (_, _) => ShowHistory());
        tray.ContextMenuStrip.Items.Add("Quit", null, (_, _) => Quit());
        tray.DoubleClick += (_, _) => ShowHistory();
        search.TextChanged += (_, _) => RefreshList();
        search.KeyDown += SearchKeyDown;
        list.SelectedIndexChanged += (_, _) => UpdatePreview();
        list.DoubleClick += (_, _) => PasteSelected();
        list.KeyDown += SearchKeyDown;
        var entryMenu = new ContextMenuStrip();
        entryMenu.Items.Add("Pin / Unpin", null, (_, _) => ChangeSelected(entry => entry.Pinned = !entry.Pinned));
        entryMenu.Items.Add("Favorite / Unfavorite", null, (_, _) => ChangeSelected(entry => entry.Favorite = !entry.Favorite));
        entryMenu.Items.Add("Delete Entry", null, (_, _) => DeleteSelected());
        list.ContextMenuStrip = entryMenu;
        list.MouseDown += (_, e) => { if (e.Button == MouseButtons.Right) { var index = list.IndexFromPoint(e.Location); if (index >= 0) list.SelectedIndex = index; } };
        paste.Click += (_, _) => PasteSelected();
        KeyDown += (_, e) => {
            if (e.KeyCode == Keys.Escape) { CancelPaste(); Hide(); }
            if (e.Control && e.Shift && e.KeyCode == Keys.V) {
                e.SuppressKeyPress = true;
                if (list.SelectedItem is Entry { Text: not null }) PasteSelected();
            }
        };
        FormClosing += (_, e) => { CancelPaste(); if (!quitting && !demo) { e.Cancel = true; Hide(); } };
        if (demo)
        {
            tray.Visible = false;
            LoadDemo();
        }
        else
        {
            LoadHistory();
            lastSequence = GetClipboardSequenceNumber();
            timer.Tick += (_, _) => CaptureClipboard();
            timer.Start();
        }
        Shown += (_, _) => {
            if (!demo) { Hide(); return; }
            if (string.IsNullOrEmpty(screenshotPath)) return;
            var capture = new System.Windows.Forms.Timer { Interval = 700 };
            capture.Tick += (_, _) => {
                capture.Stop(); capture.Dispose();
                try
                {
                    using var bitmap = new Bitmap(Width, Height);
                    DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height));
                    bitmap.Save(screenshotPath, ImageFormat.Png);
                    quitting = true; Close();
                }
                catch (Exception error) { Console.Error.WriteLine(error); Environment.Exit(1); }
            };
            capture.Start();
        };
    }

    private void LoadDemo()
    {
        foreach (var text in new[] { "Build a local-first clipboard history with keyboard shortcuts.", "git status --short --branch",
            "Quarterly planning notes — review the draft before Friday.", "SELECT customer_id, SUM(balance) FROM accounts GROUP BY customer_id;" })
            entries.Add(new Entry { Text = text, Created = DateTime.Now.AddMinutes(-entries.Count * 4), Fingerprint = "demo-" + entries.Count });
        entries[0].Pinned = true; entries[0].Favorite = true;
        using var source = typeof(Program).Assembly.GetManifestResourceStream("ClipboardHistory.DemoImage.png")!;
        using var bytes = new MemoryStream(); source.CopyTo(bytes);
        entries.Insert(2, new Entry { Image = bytes.ToArray(), Created = DateTime.Now.AddMinutes(-6), Fingerprint = "demo-image" });
        RefreshList();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (!demo && !RegisterHotKey(Handle, HotkeyId, hotkey.Modifiers | 0x4000, (uint)hotkey.Key))
            tray.ShowBalloonTip(3000, "Clipboard History", hotkey.Display + " is already in use", ToolTipIcon.Warning);
    }
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        list.ItemHeight = (int)Math.Round(78 * DeviceDpi / 96.0);
        list.Invalidate();
    }
    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (!demo) UnregisterHotKey(Handle, HotkeyId);
        base.OnHandleDestroyed(e);
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0312 && m.WParam.ToInt32() == HotkeyId) { ShowHistory(); return; }
        base.WndProc(ref m);
    }

    private void ShowHistory()
    {
        CancelPaste();
        if (Visible) { Hide(); return; }
        var foreground = GetForegroundWindow();
        if (foreground != Handle) previousWindow = foreground;
        CaptureClipboard();
        favorites.Checked = false;
        search.Clear(); RefreshList(); Show(); Activate(); search.Focus();
    }
    private void SearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter) { PasteSelected(); e.SuppressKeyPress = true; }
        if (sender == search && (e.KeyCode is Keys.Down or Keys.Up) && list.Items.Count > 0)
        {
            list.SelectedIndex = Math.Clamp(list.SelectedIndex + (e.KeyCode == Keys.Down ? 1 : -1), 0, list.Items.Count - 1);
            e.SuppressKeyPress = true;
        }
    }
    private void CaptureClipboard()
    {
        if (demo) return;
        if (storageUnavailable || history is null) return;
        if ((DateTime.UtcNow - lastPrune).TotalSeconds >= 30)
        {
            lastPrune = DateTime.UtcNow;
            if (TrimHistory()) { SaveHistory(); RefreshList(); }
        }
        if (storageUnavailable) return;
        var sequence = GetClipboardSequenceNumber();
        if (sequence == lastSequence) return;
        if (hotkey.RecordingPaused) { lastSequence = sequence; return; }
        try
        {
            if (Clipboard.ContainsData("ExcludeClipboardContentFromMonitorProcessing")) { lastSequence = sequence; return; }
            if (Clipboard.ContainsData("CanIncludeInClipboardHistory"))
            {
                if (!Clipboard.TryGetData<object>("CanIncludeInClipboardHistory", out var flag) || HistoryPrivacy.Excludes(flag))
                { lastSequence = sequence; return; }
            }
            if (IsExcludedSource()) { lastSequence = sequence; return; }
            Entry? entry = null;
            if (Clipboard.ContainsImage())
            {
                using var image = Clipboard.GetImage();
                if (image is not null)
                {
                    using var stream = new MemoryStream();
                    image.Save(stream, ImageFormat.Png);
                    if (stream.Length <= 10_000_000) entry = new Entry { Image = stream.ToArray() };
                }
            }
            else if (Clipboard.ContainsText())
            {
                var value = Clipboard.GetText();
                if (value.Length > 0 && System.Text.Encoding.UTF8.GetByteCount(value) <= 1_000_000 && !LooksSensitive(value))
                    entry = new Entry { Text = value };
            }
            if (GetClipboardSequenceNumber() != sequence) return;
            lastSequence = sequence;
            if (entry is null) return;
            var bytes = entry.Image ?? System.Text.Encoding.UTF8.GetBytes(entry.Text!);
            entry.Fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
            var previous = entries.FirstOrDefault(e => e.Fingerprint == entry.Fingerprint && (e.Image is null) == (entry.Image is null));
            if (previous is not null) { entry.Id = previous.Id; entry.Pinned = previous.Pinned; entry.Favorite = previous.Favorite; }
            entries.RemoveAll(e => e.Fingerprint == entry.Fingerprint && (e.Image is null) == (entry.Image is null));
            entries.Insert(0, entry);
            TrimHistory();
            SaveHistory(); RefreshList();
        }
        catch (System.Runtime.InteropServices.ExternalException) { /* Clipboard held by another app. */ }
        catch (Exception error) when (error is ArgumentException or OutOfMemoryException) { lastSequence = sequence; }
    }
    private void RefreshList()
    {
        var selected = list.SelectedItem as Entry;
        list.BeginUpdate(); list.Items.Clear();
        thumbnails.Retain(entries.Select(entry => entry.Id));
        foreach (var entry in entries.Where(e => (!favorites.Checked || e.Favorite) &&
            (e.Text ?? e.ToString()).Contains(search.Text, StringComparison.CurrentCultureIgnoreCase)))
            list.Items.Add(entry);
        if (list.Items.Count > 0) list.SelectedItem = selected is not null && list.Items.Contains(selected) ? selected : list.Items[0];
        list.EndUpdate(); UpdatePreview();
    }

    private void DrawHistoryRow(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || list.Items[e.Index] is not Entry entry) return;
        var scale = DeviceDpi / 96f;
        int D(int value) => (int)Math.Round(value * scale);
        var selected = (e.State & DrawItemState.Selected) != 0;
        using var background = new SolidBrush(selected ? Color.FromArgb(79, 40, 37) : list.BackColor);
        e.Graphics.FillRectangle(background, e.Bounds);
        var thumbnailBounds = new Rectangle(e.Bounds.Left + D(12), e.Bounds.Top + D(13), D(52), D(52));
        using var tile = new SolidBrush(Color.FromArgb(34, 37, 43));
        e.Graphics.FillRectangle(tile, thumbnailBounds);
        if (entry.Image is not null && thumbnails.Get(entry) is Bitmap thumbnail)
        {
            var ratio = Math.Min((thumbnailBounds.Width - D(6)) / (float)thumbnail.Width,
                (thumbnailBounds.Height - D(6)) / (float)thumbnail.Height);
            var width = Math.Max(1, (int)(thumbnail.Width * ratio));
            var height = Math.Max(1, (int)(thumbnail.Height * ratio));
            e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            e.Graphics.DrawImage(thumbnail, thumbnailBounds.Left + (thumbnailBounds.Width - width) / 2,
                thumbnailBounds.Top + (thumbnailBounds.Height - height) / 2, width, height);
        }
        else
        {
            TextRenderer.DrawText(e.Graphics, entry.Image is null ? "Aa" : "IMG", e.Font,
                thumbnailBounds, Color.FromArgb(145, 148, 155), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        var textX = thumbnailBounds.Right + D(12);
        var available = Math.Max(1, e.Bounds.Right - textX - D(28));
        var snippet = entry.Text is null ? "Image" : entry.Text[..Math.Min(400, entry.Text.Length)];
        var text = Regex.Replace(snippet, @"\s+", " ").Trim();
        if (text.Length > 200) text = text[..200];
        var flags = TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
        TextRenderer.DrawText(e.Graphics, text, e.Font, new Rectangle(textX, e.Bounds.Top + D(11), available, D(38)),
            Color.FromArgb(235, 235, 238), flags);
        var size = entry.Text is null ? $"{(entry.Image?.Length ?? 0) / 1024.0:0.#} KB" : $"{entry.Text.Length} chars";
        using var metadataFont = new Font(e.Font!.FontFamily, 8);
        TextRenderer.DrawText(e.Graphics, $"{entry.Created:t}  ·  {size}", metadataFont,
            new Rectangle(textX, e.Bounds.Top + D(55), available, D(18)), Color.FromArgb(140, 143, 150),
            TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        if (entry.Pinned || entry.Favorite)
            TextRenderer.DrawText(e.Graphics, (entry.Pinned ? "●" : "") + (entry.Favorite ? "★" : ""), metadataFont,
                new Rectangle(e.Bounds.Right - D(26), e.Bounds.Top + D(13), D(24), D(48)),
                entry.Favorite ? Color.Gold : Color.Silver, TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        if ((e.State & DrawItemState.Focus) != 0) e.DrawFocusRectangle();
    }

    private void ChangeSelected(Action<Entry> change)
    {
        if (storageUnavailable || list.SelectedItem is not Entry entry) return;
        change(entry); TrimHistory(); SaveHistory(); RefreshList();
    }

    private void DeleteSelected()
    {
        if (storageUnavailable || list.SelectedItem is not Entry entry) return;
        entries.Remove(entry); SaveHistory(); RefreshList();
    }
    private void UpdatePreview()
    {
        picture.Image?.Dispose(); picture.Image = null;
        var entry = list.SelectedItem as Entry;
        preview.Visible = entry?.Text is not null; picture.Visible = entry?.Image is not null;
        preview.Text = entry?.Text ?? "";
        if (entry?.Image is byte[] bytes)
        {
            try
            {
                using var stream = new MemoryStream(bytes);
                using var image = Image.FromStream(stream);
                picture.Image = new Bitmap(image);
            }
            catch (Exception error) when (error is ArgumentException or OutOfMemoryException or ExternalException)
            { info.Text = "Image unavailable — other entries are still usable."; }
        }
        info.Text = entry is null ? "" : $"{(entry.Image is null ? "Text" : "Image")}   ·   {entry.Created:g}";
        paste.Enabled = entry is not null;
    }
    private bool CopySelected()
    {
        if (demo) return false;
        if (list.SelectedItem is not Entry entry) return false;
        try
        {
            if (entry.Text is string value) Clipboard.SetText(value);
            else if (entry.Image is byte[] bytes)
            {
                using var stream = new MemoryStream(bytes);
                using var image = Image.FromStream(stream);
                using var copy = new Bitmap(image);
                Clipboard.SetImage(copy);
            }
            lastSequence = GetClipboardSequenceNumber();
            return true;
        }
        catch (Exception error) when (error is ExternalException or ArgumentException or OutOfMemoryException)
        {
            tray.ShowBalloonTip(3000, "Could not copy entry", "The clipboard or saved image is unavailable. Please try again.", ToolTipIcon.Warning);
            return false;
        }
    }

    private void CancelPaste() { pasteCancellation?.Cancel(); }

    private async void PasteSelected()
    {
        if (list.SelectedItem is not Entry) return;
        CancelPaste();
        if (!CopySelected()) return;
        var sequence = GetClipboardSequenceNumber();
        var destination = previousWindow;
        GetWindowThreadProcessId(destination, out var destinationProcess);
        Hide();
        if (destination == IntPtr.Zero || !IsWindow(destination)) { PasteFallback(); return; }
        SetForegroundWindow(destination);
        var cancellation = new CancellationTokenSource();
        pasteCancellation = cancellation;
        try
        {
            var readiness = new PasteReadiness();
            var clock = Stopwatch.StartNew();
            while (true)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (GetClipboardSequenceNumber() != sequence) return;
                GetWindowThreadProcessId(destination, out var currentProcess);
                var released = new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C, 0x0D, 0x20 }.All(key => (GetAsyncKeyState(key) & 0x8000) == 0);
                switch (readiness.Update(clock.Elapsed.TotalSeconds, IsWindow(destination) && currentProcess == destinationProcess,
                    GetForegroundWindow() == destination, released))
                {
                    case PasteReadiness.Decision.Wait: await Task.Delay(20, cancellation.Token); break;
                    case PasteReadiness.Decision.Stop: PasteFallback(); return;
                    case PasteReadiness.Decision.Paste:
                        var inputs = new INPUT[] {
                            new() { type = 1, value = new() { keyboard = new() { key = 0x11 } } },
                            new() { type = 1, value = new() { keyboard = new() { key = 0x56 } } },
                            new() { type = 1, value = new() { keyboard = new() { key = 0x56, flags = 2 } } },
                            new() { type = 1, value = new() { keyboard = new() { key = 0x11, flags = 2 } } }
                        };
                        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) != inputs.Length) PasteFallback();
                        return;
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (ReferenceEquals(pasteCancellation, cancellation)) pasteCancellation = null;
            cancellation.Dispose();
        }
    }

    private void PasteFallback() => tray.ShowBalloonTip(4000, "Entry copied", "Automatic paste was unavailable. Focus the destination field and press Ctrl+V.", ToolTipIcon.Info);
    private void ClearHistory()
    {
        if (storageUnavailable) return;
        if (MessageBox.Show("Delete all saved clipboard entries?", "Clipboard History", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        entries.Clear(); SaveHistory(); RefreshList();
    }
    private void LoadHistory()
    {
        if (demo) { entries.Clear(); LoadDemo(); return; }
        CancelPaste();
        history?.Dispose(); history = null;
        thumbnails.Dispose(); entries.Clear(); savedEntries.Clear();
        lastSequence = GetClipboardSequenceNumber();
        try
        {
            history = new WindowsHistoryStorage(historyFolder);
            var loaded = history.Load();
            storageUnavailable = false;
            entries.AddRange(loaded.Entries);
            savedEntries = entries.Select(entry => entry.Clone()).ToList();
            storageStatus.Text = loaded.Skipped > 0 ? $"Recovered history; skipped {loaded.Skipped} damaged image(s)." : "";
            storageStatus.Visible = loaded.Skipped > 0;
            TrimHistory(); SaveHistory();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or CryptographicException)
        {
            history?.Dispose(); history = null;
            storageUnavailable = true;
            storageStatus.Text = "Recording stopped. Encrypted history could not be opened: " + e.Message;
            storageStatus.Visible = true;
        }
        RefreshList();
    }
    private bool TrimHistory()
    {
        var before = entries.Count;
        if (storageUnavailable) return false;
        var retained = HistoryRetention.Apply(entries, DateTime.UtcNow.AddHours(-hotkey.RetentionHours),
            hotkey.MaxEntries, hotkey.MaxMegabytes * 1_000_000L, LooksSensitive);
        entries.Clear(); entries.AddRange(retained);
        return before != entries.Count;
    }
    private bool SaveHistory()
    {
        if (demo) return true;
        if (storageUnavailable || history is null) return false;
        try
        {
            history.Save(entries);
            savedEntries = entries.Select(entry => entry.Clone()).ToList();
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            entries.Clear(); entries.AddRange(savedEntries.Select(entry => entry.Clone()));
            storageUnavailable = true;
            storageStatus.Text = "Recording stopped. Could not encrypt and save history: " + e.Message;
            storageStatus.Visible = true;
            return false;
        }
    }

    private static bool StartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        return key?.GetValue("ClipboardHistory") is not null;
    }
    private void ToggleStartup()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key.GetValue("ClipboardHistory") is null) key.SetValue("ClipboardHistory", $"\"{Application.ExecutablePath}\"");
            else key.DeleteValue("ClipboardHistory");
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        { MessageBox.Show("Could not change launch at login: " + error.Message); }
    }
    private void ChangeShortcut()
    {
        using var dialog = new Form {
            Text = "Keyboard Shortcut", Width = 380, Height = 190,
            FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
            StartPosition = FormStartPosition.CenterParent, BackColor = BackColor, ForeColor = ForeColor
        };
        var prompt = new Label { Text = "Press a combination with Ctrl or Alt", Left = 20, Top = 18, Width = 330 };
        var input = new TextBox { Left = 20, Top = 48, Width = 325, ReadOnly = true, Text = hotkey.Display };
        var save = new Button { Text = "Save", Left = 182, Top = 94, Width = 78, Enabled = false, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Left = 267, Top = 94, Width = 78, DialogResult = DialogResult.Cancel };
        HotkeySettings? candidate = null;
        input.KeyDown += (_, e) => {
            e.SuppressKeyPress = true;
            if (e.KeyCode is Keys.ControlKey or Keys.ShiftKey or Keys.Menu) return;
            if (!e.Control && !e.Alt) return;
            candidate = new HotkeySettings {
                Key = e.KeyCode,
                Modifiers = (e.Control ? 0x0002u : 0) | (e.Alt ? 0x0001u : 0) | (e.Shift ? 0x0004u : 0)
            };
            input.Text = candidate.Display;
            save.Enabled = true;
        };
        dialog.Controls.AddRange([prompt, input, save, cancel]);
        dialog.AcceptButton = save; dialog.CancelButton = cancel;
        dialog.Shown += (_, _) => input.Focus();
        if (dialog.ShowDialog(this) != DialogResult.OK || candidate is null) return;
        UnregisterHotKey(Handle, HotkeyId);
        if (RegisterHotKey(Handle, HotkeyId, candidate.Modifiers | 0x4000, (uint)candidate.Key))
        {
            hotkey.Key = candidate.Key;
            hotkey.Modifiers = candidate.Modifiers;
            SaveSettings();
        }
        else
        {
            RegisterHotKey(Handle, HotkeyId, hotkey.Modifiers | 0x4000, (uint)hotkey.Key);
            MessageBox.Show(dialog, candidate.Display + " is already in use. The previous shortcut is still active.");
        }
    }
    private void ChangeHistoryLimits()
    {
        using var dialog = new Form {
            Text = "History Limits", Width = 400, Height = 230,
            FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
            StartPosition = FormStartPosition.CenterParent, BackColor = BackColor, ForeColor = ForeColor
        };
        var countLabel = new Label { Text = "Maximum entries (100–5000)", Left = 20, Top = 22, Width = 240 };
        var count = new NumericUpDown { Left = 270, Top = 18, Width = 90, Minimum = 100, Maximum = 5000, Value = hotkey.MaxEntries };
        var sizeLabel = new Label { Text = "Storage limit in MB (40–2048)", Left = 20, Top = 68, Width = 240 };
        var size = new NumericUpDown { Left = 270, Top = 64, Width = 90, Minimum = 40, Maximum = 2048, Value = hotkey.MaxMegabytes };
        var save = new Button { Text = "Save", Left = 190, Top = 125, Width = 78, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Left = 276, Top = 125, Width = 84, DialogResult = DialogResult.Cancel };
        dialog.Controls.AddRange([countLabel, count, sizeLabel, size, save, cancel]);
        dialog.AcceptButton = save; dialog.CancelButton = cancel;
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        hotkey.MaxEntries = (int)count.Value;
        hotkey.MaxMegabytes = (int)size.Value;
        TrimHistory(); SaveHistory(); SaveSettings(); RefreshList();
    }
    private void SaveSettings()
    {
        if (demo) return;
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(hotkey));
    }
    private static bool LooksSensitive(string text)
    {
        try { return SensitiveContent.IsMatch(text); }
        catch (RegexMatchTimeoutException) { return true; }
    }
    private bool IsExcludedSource()
    {
        var excluded = PasswordManagers.Concat(hotkey.ExcludedProcesses.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var window in new[] { GetClipboardOwner(), GetForegroundWindow() })
        {
            GetWindowThreadProcessId(window, out var pid);
            if (pid == 0) continue;
            try { using var process = Process.GetProcessById((int)pid); if (excluded.Contains(process.ProcessName)) return true; }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        return false;
    }
    private void ChangePrivacy()
    {
        using var dialog = new Form { Text = "Privacy Settings", Width = 440, Height = 260,
            FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
            StartPosition = FormStartPosition.CenterParent };
        var label = new Label { Text = "Retention in hours (1–168)", Left = 20, Top = 20, Width = 250 };
        var hours = new NumericUpDown { Left = 300, Top = 18, Width = 90, Minimum = 1, Maximum = 168, Value = hotkey.RetentionHours };
        var note = new Label { Text = "Extra excluded process names (comma-separated)", Left = 20, Top = 60, Width = 380 };
        var excluded = new TextBox { Left = 20, Top = 85, Width = 375, Text = hotkey.ExcludedProcesses };
        var warning = new Label { Text = "Password managers and marked private items are excluded. Unmarked passwords may still be recorded; pause before copying secrets.", Left = 20, Top = 118, Width = 375, Height = 42 };
        var save = new Button { Text = "Save", Left = 230, Top = 170, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", Left = 315, Top = 170, DialogResult = DialogResult.Cancel };
        dialog.Controls.AddRange([label, hours, note, excluded, warning, save, cancel]);
        dialog.AcceptButton = save; dialog.CancelButton = cancel;
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        hotkey.RetentionHours = (int)hours.Value;
        hotkey.ExcludedProcesses = excluded.Text;
        TrimHistory(); SaveHistory(); SaveSettings(); RefreshList();
    }
    private void Quit() { CancelPaste(); quitting = true; timer.Stop(); tray.Visible = false; Close(); }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CancelPaste(); timer.Dispose(); tray.Dispose(); thumbnails.Dispose(); history?.Dispose(); picture.Image?.Dispose();
        }
        base.Dispose(disposing);
    }
}
