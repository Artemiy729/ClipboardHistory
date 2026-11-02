using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace ClipboardHistory.Windows;

internal sealed class Entry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime Created { get; set; } = DateTime.Now;
    public string? Text { get; set; }
    public byte[]? Image { get; set; }
    public string Fingerprint { get; set; } = "";
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
    public string Display => ((Modifiers & 0x0002) != 0 ? "Ctrl+" : "")
        + ((Modifiers & 0x0001) != 0 ? "Alt+" : "")
        + ((Modifiers & 0x0004) != 0 ? "Shift+" : "") + Key;
}

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, "Local\\ClipboardHistoryOpenSource", out var first);
        if (!first) return;
        ApplicationConfiguration.Initialize();
        Application.Run(new ClipboardForm());
    }
}

internal sealed class ClipboardForm : Form
{
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public INPUTUNION value; }
    [StructLayout(LayoutKind.Explicit)] private struct INPUTUNION { [FieldOffset(0)] public KEYBDINPUT keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort key; public ushort scan; public uint flags; public uint time; public IntPtr extra; }

    private const int HotkeyId = 1;
    private readonly string store = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipboardHistory", "history.json");
    private readonly string settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipboardHistory", "settings.json");
    private readonly List<Entry> entries = [];
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 500 };
    private readonly TextBox search = new() { PlaceholderText = "Search in History…", Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, Font = new Font("Segoe UI", 17) };
    private readonly ListBox list = new() { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, Font = new Font("Segoe UI", 12), IntegralHeight = false };
    private readonly TextBox preview = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 11) };
    private readonly PictureBox picture = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, Visible = false };
    private readonly Label info = new() { AutoSize = true, ForeColor = Color.Silver };
    private readonly NotifyIcon tray = new() { Icon = SystemIcons.Application, Text = "Clipboard History", Visible = true };
    private readonly Button paste = new() { Text = "Paste  ↵", Width = 125 };
    private uint lastSequence;
    private IntPtr previousWindow;
    private bool quitting;
    private HotkeySettings hotkey = new();

    public ClipboardForm()
    {
        Text = "Clipboard History";
        Width = 1000; Height = 650; MinimumSize = new Size(700, 450);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(20, 22, 27); ForeColor = Color.White;
        KeyPreview = true; ShowInTaskbar = false;
        try
        {
            if (File.Exists(settingsPath)) hotkey = JsonSerializer.Deserialize<HotkeySettings>(File.ReadAllText(settingsPath)) ?? new();
        }
        catch (Exception e) when (e is IOException or JsonException) { hotkey = new(); }
        hotkey.MaxEntries = Math.Clamp(hotkey.MaxEntries, 100, 5000);
        hotkey.MaxMegabytes = Math.Clamp(hotkey.MaxMegabytes, 40, 2048);

        var header = new Panel { Dock = DockStyle.Top, Height = 68, Padding = new Padding(20) };
        header.Controls.Add(search);
        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 350, BackColor = Color.FromArgb(30, 33, 39) };
        split.Panel1.Controls.Add(list);
        var details = new Panel { Dock = DockStyle.Fill, Padding = new Padding(18) };
        details.Controls.Add(preview); details.Controls.Add(picture);
        info.Dock = DockStyle.Bottom; info.Height = 28;
        details.Controls.Add(info); split.Panel2.Controls.Add(details);
        var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 58, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(12) };
        var actions = new Button { Text = "Actions", Width = 105 };
        footer.Controls.Add(actions); footer.Controls.Add(paste);
        Controls.Add(split); Controls.Add(footer); Controls.Add(header);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Copy to Clipboard", null, (_, _) => CopySelected());
        menu.Items.Add("Clear History", null, (_, _) => ClearHistory());
        menu.Items.Add("Keyboard Shortcut…", null, (_, _) => ChangeShortcut());
        menu.Items.Add("History Limits…", null, (_, _) => ChangeHistoryLimits());
        menu.Items.Add("Start with Windows", null, (_, _) => ToggleStartup());
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
        paste.Click += (_, _) => PasteSelected();
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Hide(); };
        FormClosing += (_, e) => { if (!quitting) { e.Cancel = true; Hide(); } };
        LoadHistory();
        lastSequence = GetClipboardSequenceNumber();
        timer.Tick += (_, _) => CaptureClipboard();
        timer.Start();
        Shown += (_, _) => Hide();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (!RegisterHotKey(Handle, HotkeyId, hotkey.Modifiers, (uint)hotkey.Key))
            tray.ShowBalloonTip(3000, "Clipboard History", hotkey.Display + " is already in use", ToolTipIcon.Warning);
    }
    protected override void OnHandleDestroyed(EventArgs e)
    {
        UnregisterHotKey(Handle, HotkeyId);
        base.OnHandleDestroyed(e);
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0312 && m.WParam.ToInt32() == HotkeyId) { ShowHistory(); return; }
        base.WndProc(ref m);
    }

    private void ShowHistory()
    {
        if (Visible) { Hide(); return; }
        previousWindow = GetForegroundWindow();
        search.Clear(); RefreshList(); Show(); Activate(); search.Focus();
    }
    private void SearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter) { PasteSelected(); e.SuppressKeyPress = true; }
        if (e.KeyCode is Keys.Down or Keys.Up && list.Items.Count > 0)
        {
            list.SelectedIndex = Math.Clamp(list.SelectedIndex + (e.KeyCode == Keys.Down ? 1 : -1), 0, list.Items.Count - 1);
            e.SuppressKeyPress = true;
        }
    }
    private void CaptureClipboard()
    {
        var sequence = GetClipboardSequenceNumber();
        if (sequence == lastSequence) return;
        lastSequence = sequence;
        try
        {
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
                if (value.Length > 0 && System.Text.Encoding.UTF8.GetByteCount(value) <= 1_000_000)
                    entry = new Entry { Text = value };
            }
            if (entry is null) return;
            var bytes = entry.Image ?? System.Text.Encoding.UTF8.GetBytes(entry.Text!);
            entry.Fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
            entries.RemoveAll(e => e.Fingerprint == entry.Fingerprint && (e.Image is null) == (entry.Image is null));
            entries.Insert(0, entry);
            TrimHistory();
            SaveHistory(); RefreshList();
        }
        catch (System.Runtime.InteropServices.ExternalException) { /* Clipboard held by another app. */ }
    }
    private void RefreshList()
    {
        var selected = list.SelectedItem as Entry;
        list.BeginUpdate(); list.Items.Clear();
        foreach (var entry in entries.Where(e => e.ToString().Contains(search.Text, StringComparison.CurrentCultureIgnoreCase)))
            list.Items.Add(entry);
        if (list.Items.Count > 0) list.SelectedItem = selected is not null && list.Items.Contains(selected) ? selected : list.Items[0];
        list.EndUpdate(); UpdatePreview();
    }
    private void UpdatePreview()
    {
        picture.Image?.Dispose(); picture.Image = null;
        var entry = list.SelectedItem as Entry;
        preview.Visible = entry?.Text is not null; picture.Visible = entry?.Image is not null;
        preview.Text = entry?.Text ?? "";
        if (entry?.Image is byte[] bytes)
        {
            using var stream = new MemoryStream(bytes);
            using var image = Image.FromStream(stream);
            picture.Image = new Bitmap(image);
        }
        info.Text = entry is null ? "" : $"{(entry.Image is null ? "Text" : "Image")}   ·   {entry.Created:g}";
        paste.Enabled = entry is not null;
    }
    private bool CopySelected()
    {
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
        catch (System.Runtime.InteropServices.ExternalException) { return false; }
    }
    private async void PasteSelected()
    {
        if (list.SelectedItem is not Entry) return;
        if (!CopySelected()) return;
        Hide();
        if (previousWindow == IntPtr.Zero) return;
        SetForegroundWindow(previousWindow);
        await Task.Delay(120);
        var inputs = new INPUT[] {
            new() { type = 1, value = new() { keyboard = new() { key = 0x11 } } },
            new() { type = 1, value = new() { keyboard = new() { key = 0x56 } } },
            new() { type = 1, value = new() { keyboard = new() { key = 0x56, flags = 2 } } },
            new() { type = 1, value = new() { keyboard = new() { key = 0x11, flags = 2 } } }
        };
        _ = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }
    private void ClearHistory()
    {
        if (MessageBox.Show("Delete all saved clipboard entries?", "Clipboard History", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        entries.Clear(); SaveHistory(); RefreshList();
    }
    private void LoadHistory()
    {
        try
        {
            if (File.Exists(store)) entries.AddRange(JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(store)) ?? []);
            if (TrimHistory()) SaveHistory();
        }
        catch (Exception e) when (e is IOException or JsonException) { MessageBox.Show("History could not be loaded: " + e.Message); }
        RefreshList();
    }
    private bool TrimHistory()
    {
        var before = entries.Count;
        long Size() => entries.Sum(e => (long)(e.Image?.Length ?? System.Text.Encoding.UTF8.GetByteCount(e.Text ?? "")));
        while (entries.Count > hotkey.MaxEntries || Size() > hotkey.MaxMegabytes * 1_000_000L)
            entries.RemoveAt(entries.Count - 1);
        return before != entries.Count;
    }
    private void SaveHistory()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(store)!);
            var temporary = store + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(entries));
            File.Move(temporary, store, true);
        }
        catch (IOException e) { MessageBox.Show("History could not be saved: " + e.Message); }
    }
    private void ToggleStartup()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
        if (key?.GetValue("ClipboardHistory") is null)
            key?.SetValue("ClipboardHistory", $"\"{Application.ExecutablePath}\"");
        else key.DeleteValue("ClipboardHistory");
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
        if (RegisterHotKey(Handle, HotkeyId, candidate.Modifiers, (uint)candidate.Key))
        {
            hotkey = candidate;
            SaveSettings();
        }
        else
        {
            RegisterHotKey(Handle, HotkeyId, hotkey.Modifiers, (uint)hotkey.Key);
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
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(hotkey));
    }
    private void Quit() { quitting = true; timer.Stop(); tray.Visible = false; Close(); }
}
