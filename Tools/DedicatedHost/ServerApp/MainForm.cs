using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DvmpServer;

/// <summary>
/// Setup (the save, Multiplayer's server settings, the server's mods) and the running server (its
/// log, status and a command box), in place of running run-dedicated-host.ps1 by hand.
/// </summary>
public class MainForm : Form
{
    private const string Title = "DV Multiplayer Server";

    private AppConfig config;
    private Watchdog watchdog;
    private bool closeWhenStopped;
    // Every setup value as it was last loaded or saved; unsaved changes are a difference from it.
    // (Compared rather than tracked by events, which controls also raise when their windows are made.)
    private string savedSnapshot = "";
    private bool Dirty => Snapshot() != savedSnapshot;
    private bool loading;
    private readonly List<string> history = new();
    private int historyIndex;

    private readonly TabControl tabs = new() { Dock = DockStyle.Fill };
    private readonly TabPage setupTab = new("Setup");
    private readonly TabPage serverTab = new("Server");

    // Setup
    private readonly TableLayoutPanel setupPanel = new() { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
    private readonly TextBox gameDir = new() { Dock = DockStyle.Fill };
    private readonly ComboBox save = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label saveNote = new() { AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(520, 0) };
    private readonly TextBox serverName = new() { Dock = DockStyle.Fill };
    private readonly TextBox password = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
    private readonly CheckBox showPassword = new() { Text = "Show", AutoSize = true };
    private readonly NumericUpDown maxPlayers = new() { Minimum = 1, Maximum = 255, Width = 80 };
    private readonly ComboBox visibility = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
    private readonly NumericUpDown port = new() { Minimum = 1024, Maximum = 49151, Width = 80 };
    private readonly TextBox details = new() { Dock = DockStyle.Fill };
    private readonly TextBox admins = new() { Dock = DockStyle.Fill, Multiline = true, Height = 70, ScrollBars = ScrollBars.Vertical };
    private readonly CheckBox debugLogging = new() { Text = "Debug logging (more detail in the log)", AutoSize = true };
    private readonly NumericUpDown autosave = new() { Minimum = 1, Maximum = 120, Width = 80 };
    private readonly CheckBox dailyRestartOn = new() { Text = "Restart every day at", AutoSize = true };
    private readonly DateTimePicker dailyRestart = new() { Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true, Width = 80 };
    private readonly NumericUpDown fps = new() { Minimum = 25, Maximum = 120, Width = 80 };
    private readonly CheckBox showGameWindow = new() { Text = "Show the game's window (otherwise it starts minimised)", AutoSize = true };
    private readonly ListView mods = new()
    {
        Dock = DockStyle.Fill, View = View.Details, CheckBoxes = true, FullRowSelect = true, HideSelection = false,
        ShowItemToolTips = true,
    };
    private readonly Label setupStatus = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly Button saveButton = new() { Text = "Save", AutoSize = true };
    private readonly Button saveAndStartButton = new() { Text = "Save and start the server", AutoSize = true };

    // Server
    private readonly Label state = new() { AutoSize = true, Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 12f, FontStyle.Bold) };
    private readonly Label beat = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly RichTextBox log = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, BackColor = Color.FromArgb(24, 24, 24), ForeColor = Color.Gainsboro,
        Font = new Font("Consolas", 9.5f), WordWrap = true, DetectUrls = false, BorderStyle = BorderStyle.None,
    };
    private readonly TextBox command = new() { Dock = DockStyle.Fill, Font = new Font("Consolas", 10f) };
    private readonly Button send = new() { Text = "Send", AutoSize = true };
    private readonly Button startButton = new() { Text = "Start", AutoSize = true };
    private readonly Button stopButton = new() { Text = "Stop", AutoSize = true };
    private readonly Button restartButton = new() { Text = "Restart", AutoSize = true };
    private readonly CheckBox showDebug = new() { Text = "Show debug lines", AutoSize = true, Anchor = AnchorStyles.Left };

    public MainForm()
    {
        Text = Title;
        Font = SystemFonts.MessageBoxFont;
        // Sizes here are at 96 dpi; the form scales them to the screen's.
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(1180, 760);
        MinimumSize = new Size(900, 600);
        StartPosition = FormStartPosition.CenterScreen;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }

        BuildSetup();
        BuildServer();
        tabs.TabPages.Add(setupTab);
        tabs.TabPages.Add(serverTab);
        Controls.Add(tabs);
        // Each control named after its field, for screen readers and UI Automation.
        foreach (var field in GetType().GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
            if (field.GetValue(this) is Control control && string.IsNullOrEmpty(control.Name))
                control.Name = field.Name;

        LoadSetup();
        SetRunning(false);
        if (!ServerMods.Exists(AppConfig.ServerDir) || string.IsNullOrEmpty(config.Save))
            setupStatus.Text = "Nothing is set up yet: pick a save and the server's settings, then Save.";
        else
            tabs.SelectedTab = serverTab;
        if (Watchdog.OtherServerRunning())
            AppendLine("A server started outside this app (the watchdog script, or an earlier run of this app) is running. Start asks it to stop first.", LineKind.Warning);
    }

    // ---- Layout ----

    private static TableLayoutPanel Grid()
    {
        var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 3, Padding = new Padding(4) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        return grid;
    }

    private static void Row(TableLayoutPanel grid, string label, Control control, Control extra = null)
    {
        int row = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        if (label != null)
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 8, 3) }, 0, row);
        grid.Controls.Add(control, label == null ? 0 : 1, row);
        if (label == null)
            grid.SetColumnSpan(control, extra == null ? 3 : 2);
        else if (extra == null)
            grid.SetColumnSpan(control, 2);
        if (extra != null)
            grid.Controls.Add(extra, 2, row);
    }

    private static Control Inline(params Control[] controls)
    {
        var flow = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0) };
        flow.Controls.AddRange(controls);
        return flow;
    }

    private static Label Note(string text) =>
        new() { Text = text, AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(3, 6, 3, 3) };

    private static GroupBox Group(string title, Control content)
    {
        var group = new GroupBox { Text = title, Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(6) };
        group.Controls.Add(content);
        return group;
    }

    private void BuildSetup()
    {
        setupPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        setupPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        setupPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        setupPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // Left: the save, the server's settings and how it runs. Groups dock to the top, so they're
        // added bottom first.
        var left = new Panel { Dock = DockStyle.Fill, AutoScroll = true };

        var running = Grid();
        Row(running, "Autosave", Inline(autosave, Note("minutes, while anyone is online")));
        Row(running, null, Inline(dailyRestartOn, dailyRestart));
        Row(running, "Frame cap", Inline(fps, Note("fps")));
        Row(running, null, showGameWindow);

        var server = Grid();
        Row(server, "Server name", serverName);
        Row(server, "Password", password, showPassword);
        Row(server, "Max players", Inline(maxPlayers, Note("not counting the server")));
        visibility.Items.AddRange(ServerSettings.Visibilities);
        Row(server, "Visibility", Inline(visibility, Note("Friends: only your Steam friends see it")));
        Row(server, "Port", Inline(port, Note("UDP; you generally don't need to change it")));
        Row(server, "Details", details);
        Row(server, "Admins", admins);
        Row(server, null, Note("SteamID64s, one per line. You're always an admin."));
        new ToolTip { AutoPopDelay = 20000 }.SetToolTip(admins,
            "Admins may use the admin chat commands: /kick, /save, /stop, /payfees and more.\n" +
            "The Steam account running the server always may.");
        Row(server, null, debugLogging);

        var game = Grid();
        var browse = new Button { Text = "Browse…", AutoSize = true };
        browse.Click += (_, _) => BrowseGame();
        Row(game, "Game folder", gameDir, browse);
        var refresh = new Button { Text = "Refresh", AutoSize = true };
        refresh.Click += (_, _) => { LoadSaves(SelectedSave()); LoadMods(SelectedMods()); };
        Row(game, "Save", save, refresh);
        Row(game, null, saveNote);

        left.Controls.Add(Group("Server (the Multiplayer mod's settings on the server)", server));
        left.Controls.Add(Group("Game and save", game));
        setupPanel.Controls.Add(left, 0, 0);

        // Right: the server's mods.
        mods.Columns.Add("Mod", 190);
        mods.Columns.Add("ID", 140);
        mods.Columns.Add("Version", 80);
        mods.Columns.Add("Multiplayer", 170);
        mods.Columns.Add("Note", 200);
        var modsGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(4) };
        modsGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        modsGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        modsGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        modsGrid.Controls.Add(mods, 0, 0);
        modsGrid.Controls.Add(new Label
        {
            AutoSize = true, MaximumSize = new Size(600, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(3, 6, 3, 3),
            Text = "Players must have the same mods as the server, except ones marked \"server only\" or \"not needed on " +
                   "the server\". Multiplayer is always on. Each start updates the server's mods from your game's copies; " +
                   "their settings are the server's own. A mod turned off stays in the server's folder, disabled.",
        }, 0, 1);
        var openMods = new Button { Text = "Open the server's folder", AutoSize = true };
        openMods.Click += (_, _) => OpenFolder(AppConfig.ServerDir);
        modsGrid.Controls.Add(openMods, 0, 2);
        var modsGroup = new GroupBox { Text = "Mods on the server", Dock = DockStyle.Fill, Padding = new Padding(6) };
        modsGroup.Controls.Add(modsGrid);
        // Right: the server's mods, and how it runs below them.
        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0) };
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        right.Controls.Add(modsGroup, 0, 0);
        right.Controls.Add(Group("Running", running), 0, 1);
        setupPanel.Controls.Add(right, 1, 0);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(4) };
        buttons.Controls.Add(saveAndStartButton);
        buttons.Controls.Add(saveButton);
        buttons.Controls.Add(setupStatus);
        setupPanel.Controls.Add(buttons, 0, 1);
        setupPanel.SetColumnSpan(buttons, 2);
        setupTab.Controls.Add(setupPanel);
        setupTab.Padding = new Padding(6);

        saveButton.Click += async (_, _) => await SaveSetup();
        saveAndStartButton.Click += async (_, _) =>
        {
            if (await SaveSetup())
            {
                tabs.SelectedTab = serverTab;
                StartServer();
            }
        };
        showPassword.CheckedChanged += (_, _) => password.UseSystemPasswordChar = !showPassword.Checked;
        dailyRestartOn.CheckedChanged += (_, _) => dailyRestart.Enabled = dailyRestartOn.Checked;
        save.SelectedIndexChanged += (_, _) => UpdateSaveNote();
        mods.ItemCheck += (_, e) =>
        {
            // Multiplayer is always on.
            if (!loading && mods.Items[e.Index].Tag is ModRow row && row.Id == ServerMods.MultiplayerId)
                e.NewValue = CheckState.Checked;
        };
        foreach (Control c in new Control[] { gameDir, serverName, password, details, admins })
            c.TextChanged += (_, _) => MarkDirty();
        foreach (NumericUpDown n in new[] { maxPlayers, port, autosave, fps })
            n.ValueChanged += (_, _) => MarkDirty();
        foreach (CheckBox c in new[] { debugLogging, dailyRestartOn, showGameWindow })
            c.CheckedChanged += (_, _) => MarkDirty();
        save.SelectedIndexChanged += (_, _) => MarkDirty();
        visibility.SelectedIndexChanged += (_, _) => MarkDirty();
        dailyRestart.ValueChanged += (_, _) => MarkDirty();
        mods.ItemChecked += (_, _) => MarkDirty();
    }

    private void BuildServer()
    {
        var top = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(4) };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var status = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Dock = DockStyle.Fill };
        status.Controls.Add(state);
        status.Controls.Add(beat);
        top.Controls.Add(status, 0, 0);
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Right | AnchorStyles.Top };
        var openLogs = new Button { Text = "Logs folder", AutoSize = true };
        openLogs.Click += (_, _) => OpenFolder(Path.Combine(AppConfig.DataDir, "logs"));
        var clear = new Button { Text = "Clear", AutoSize = true };
        clear.Click += (_, _) => log.Clear();
        buttons.Controls.AddRange(new Control[] { startButton, restartButton, stopButton, openLogs, clear });
        top.Controls.Add(buttons, 1, 0);

        var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 3, Padding = new Padding(4) };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.Controls.Add(command, 0, 0);
        bottom.Controls.Add(send, 1, 0);
        bottom.Controls.Add(showDebug, 2, 0);
        bottom.Controls.Add(Note("Commands run as the server's owner: /help lists them (/save, /stop, /kick Bob, /give Bob EOTLantern…). " +
                                 "A line without a slash is said in chat as the server."), 0, 1);
        bottom.SetColumnSpan(bottom.GetControlFromPosition(0, 1), 3);

        var logPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4), BackColor = log.BackColor };
        logPanel.Controls.Add(log);
        serverTab.Controls.Add(logPanel);
        serverTab.Controls.Add(top);
        serverTab.Controls.Add(bottom);
        serverTab.Padding = new Padding(6);

        startButton.Click += (_, _) => StartServer();
        stopButton.Click += (_, _) => { watchdog?.Stop(); state.Text = "Stopping (saving first)…"; stopButton.Enabled = restartButton.Enabled = false; };
        restartButton.Click += (_, _) => { watchdog?.Restart(); state.Text = "Restarting (saving first)…"; };
        send.Click += (_, _) => SendCommand();
        command.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { SendCommand(); e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Up && history.Count > 0) { historyIndex = Math.Max(0, historyIndex - 1); ShowHistory(); e.Handled = true; }
            else if (e.KeyCode == Keys.Down && history.Count > 0) { historyIndex = Math.Min(history.Count, historyIndex + 1); ShowHistory(); e.Handled = true; }
        };
        showDebug.CheckedChanged += (_, _) =>
        {
            if (config == null || loading) return;
            config.ShowDebugLines = showDebug.Checked;
            try { config.Write(); } catch (Exception) { }
        };
    }

    // ---- Setup ----

    private class SaveItem
    {
        public string Value;
        public string Text;
        public GameSession Session;
        public override string ToString() => Text;
    }

    private class ModRow
    {
        public string Id;
    }

    private void LoadSetup()
    {
        loading = true;
        try
        {
            config = AppConfig.Load();
            gameDir.Text = config.GameDir;
            LoadSaves(config.Save);

            // The server's own settings; before there are any, the game's, which the first save copies.
            string serverSettings = ServerMods.MultiplayerSettingsPath(AppConfig.ServerDir);
            string from = serverSettings != null && File.Exists(serverSettings) ? serverSettings : GameMultiplayerSettings();
            ServerSettings settings = ServerSettings.Load(from);
            serverName.Text = settings.ServerName;
            password.Text = settings.Password;
            maxPlayers.Value = Clamp(settings.MaxPlayers, maxPlayers);
            visibility.SelectedItem = settings.Visibility;
            port.Value = Clamp(settings.Port, port);
            details.Text = settings.Details;
            admins.Text = string.Join(Environment.NewLine, SplitAdmins(settings.Admins));
            debugLogging.Checked = settings.DebugLogging;

            autosave.Value = Clamp(config.AutosaveMinutes, autosave);
            fps.Value = Clamp(config.Fps, fps);
            dailyRestartOn.Checked = TimeSpan.TryParse(config.DailyRestart, out TimeSpan daily) && config.DailyRestart.Length > 0;
            dailyRestart.Value = DateTime.Today + (dailyRestartOn.Checked ? daily : new TimeSpan(5, 0, 0));
            dailyRestart.Enabled = dailyRestartOn.Checked;
            showGameWindow.Checked = config.ShowGameWindow;
            showDebug.Checked = config.ShowDebugLines;

            LoadMods(null);
        }
        finally { loading = false; }
        savedSnapshot = Snapshot();
    }

    private string Snapshot() => string.Join("\n", new object[]
    {
        gameDir.Text, SelectedSave(), serverName.Text, password.Text, maxPlayers.Value, visibility.SelectedItem, port.Value,
        details.Text, string.Join(",", SplitAdmins(admins.Text)), debugLogging.Checked, autosave.Value, dailyRestartOn.Checked,
        dailyRestart.Value.ToString("HH:mm"), fps.Value, showGameWindow.Checked,
        string.Join(",", (SelectedMods() ?? new HashSet<string>()).OrderBy(id => id, StringComparer.Ordinal)),
    });

    private static decimal Clamp(int value, NumericUpDown control) => Math.Max(control.Minimum, Math.Min(control.Maximum, value));

    private string GameMultiplayerSettings()
    {
        ModInfo mp = ServerMods.Scan(Path.Combine(gameDir.Text, "Mods")).TryGetValue(ServerMods.MultiplayerId, out ModInfo m) ? m : null;
        return mp == null ? null : Path.Combine(mp.Folder, "Settings.xml");
    }

    private string SelectedSave() => (save.SelectedItem as SaveItem)?.Value ?? config?.Save ?? "";

    private void LoadSaves(string selected)
    {
        bool wasLoading = loading;
        loading = true;
        save.Items.Clear();
        save.Items.Add(new SaveItem { Value = "continue", Text = "Continue: the save DV's launcher shows" });
        foreach (GameSession session in GameSaves.List().Where(s => s.Saves > 0))
        {
            string when = session.LastSave?.ToString("yyyy-MM-dd HH:mm") ?? "";
            string mode = session.Mode == "FreeRoam" ? "free roam" : "career";
            string user = session.CurrentUser ? "" : $", DV user {session.User}";
            save.Items.Add(new SaveItem { Value = session.Name, Session = session, Text = $"{session.Name} ({mode}, {when}{user})" });
        }
        SaveItem match = save.Items.Cast<SaveItem>().FirstOrDefault(i => i.Value == selected && (i.Session == null || i.Session.CurrentUser))
                         ?? save.Items.Cast<SaveItem>().FirstOrDefault(i => i.Value == selected);
        if (match == null && !string.IsNullOrEmpty(selected))
        {
            match = new SaveItem { Value = selected, Text = $"{selected} (not found in DV's saves)" };
            save.Items.Add(match);
        }
        save.SelectedItem = match;
        loading = wasLoading;
        UpdateSaveNote();
    }

    private void UpdateSaveNote()
    {
        GameSession session = (save.SelectedItem as SaveItem)?.Session;
        if (save.SelectedItem == null)
            saveNote.Text = "Pick the career or free roam session to host. The server loads its newest save.";
        else if (session == null)
            saveNote.Text = (save.SelectedItem as SaveItem).Value == "continue"
                ? "The server loads whatever DV's own launcher would continue."
                : "Not one of DV's sessions now; the server looks it up by name or save UID.";
        else if (!session.CurrentUser)
            saveNote.Text = $"This session belongs to DV user {session.User}, but the game loads DV's last user's sessions. Play once as {session.User} first.";
        else if (session.DuplicateName)
            saveNote.Text = "Another session has the same name, so the server may load either. Rename one in DV.";
        else
            saveNote.Text = $"{session.Saves} save(s) in {session.Folder}; the server loads its newest. The server and your own game can't play this session at the same time.";
    }

    private ISet<string> SelectedMods()
    {
        if (mods.Items.Count == 0)
            return null;
        // (While the list's window is being made, some of its items are briefly null.)
        return new HashSet<string>(mods.Items.Cast<ListViewItem>().Where(i => i != null && i.Checked && i.Tag is ModRow).Select(i => ((ModRow)i.Tag).Id));
    }

    /// <summary>
    /// The game's mods and the server's. Ticked: what's in the server's Mods and enabled in its own
    /// Params.xml, or with no server folder yet, everything (as the watchdog's first run copies everything).
    /// </summary>
    private void LoadMods(ISet<string> ticked)
    {
        bool wasLoading = loading;
        loading = true;
        Dictionary<string, ModInfo> game = ServerMods.Scan(Path.Combine(gameDir.Text, "Mods"));
        Dictionary<string, ModInfo> server = ServerMods.Scan(ServerMods.ModsDir(AppConfig.ServerDir));
        HashSet<string> disabled = ServerMods.Disabled(AppConfig.ServerDir);
        bool serverExists = ServerMods.Exists(AppConfig.ServerDir);
        mods.BeginUpdate();
        mods.Items.Clear();
        IEnumerable<string> ids = game.Keys.Union(server.Keys)
            .OrderBy(id => id == ServerMods.MultiplayerId ? 0 : 1)
            .ThenBy(id => (game.TryGetValue(id, out ModInfo g) ? g : server[id]).DisplayName, StringComparer.OrdinalIgnoreCase);
        foreach (string id in ids)
        {
            game.TryGetValue(id, out ModInfo inGame);
            server.TryGetValue(id, out ModInfo onServer);
            ModInfo mod = inGame ?? onServer;
            string note = "";
            if (inGame == null)
                note = "not in your game: kept as it is";
            else if (onServer != null && onServer.Version != inGame.Version)
                note = $"server has {onServer.Version}; updated at start";
            if (mod.Compatibility == "Incompatible")
                note = "doesn't work with Multiplayer";
            if (mod.Requirements.Length > 0)
                note = (note.Length > 0 ? note + "; " : "") + "needs " + string.Join(", ", mod.Requirements);
            bool on = id == ServerMods.MultiplayerId || (ticked != null ? ticked.Contains(id) : serverExists ? onServer != null && !disabled.Contains(id) : true);
            var item = new ListViewItem(new[] { mod.DisplayName, id, mod.Version, $"{mod.Compatibility}: {mod.CompatibilityMeaning}", note })
            {
                Tag = new ModRow { Id = id },
                ToolTipText = mod.Folder,
                Checked = on,
            };
            if (id == ServerMods.MultiplayerId)
                item.ForeColor = SystemColors.GrayText;
            mods.Items.Add(item);
        }
        mods.EndUpdate();
        loading = wasLoading;
    }

    private static IEnumerable<string> SplitAdmins(string text) =>
        (text ?? "").Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(a => a.Trim());

    // Settings.IsListedAdmin takes SteamID64s, or player IDs as GUIDs.
    private static bool ValidAdmin(string entry) =>
        (entry.Length == 17 && ulong.TryParse(entry, out ulong id) && id > 76561197960265728UL) || Guid.TryParse(entry, out _);

    private void MarkDirty()
    {
        if (loading || watchdog?.Running == true)
            return;
        if (Dirty)
            setupStatus.Text = "Not saved yet.";
        else if (setupStatus.Text == "Not saved yet.")
            setupStatus.Text = "";
    }

    private async Task<bool> SaveSetup()
    {
        if (watchdog?.Running == true)
        {
            MessageBox.Show(this, "Stop the server to change its setup.", Title);
            return false;
        }
        string game = gameDir.Text.Trim();
        if (!AppConfig.IsGameDir(game))
            return Fail("That isn't Derail Valley's folder (no DerailValley.exe in it).");
        if (save.SelectedItem is not SaveItem chosen)
            return Fail("Pick a save to host.");
        List<string> badAdmins = SplitAdmins(admins.Text).Where(a => !ValidAdmin(a)).ToList();
        if (badAdmins.Count > 0)
            return Fail("These admins aren't SteamID64s (17 digits, starting 7656119…): " + string.Join(", ", badAdmins));
        Dictionary<string, ModInfo> available = ServerMods.Scan(Path.Combine(game, "Mods"));
        if (!available.ContainsKey(ServerMods.MultiplayerId) && !ServerMods.Scan(ServerMods.ModsDir(AppConfig.ServerDir)).ContainsKey(ServerMods.MultiplayerId))
            return Fail("Multiplayer isn't in your game's Mods folder, so the server can't have it.");

        // Requirements: offer to turn on what the ticked mods need.
        var known = new Dictionary<string, ModInfo>();
        foreach (var dir in new[] { Path.Combine(game, "Mods"), ServerMods.ModsDir(AppConfig.ServerDir) })
            foreach (var pair in ServerMods.Scan(dir))
                if (!known.ContainsKey(pair.Key)) known[pair.Key] = pair.Value;
        ISet<string> enabled = SelectedMods() ?? new HashSet<string>();
        enabled.Add(ServerMods.MultiplayerId);
        List<string> missing = ServerMods.MissingRequirements(enabled.Where(known.ContainsKey).Select(id => known[id]));
        if (missing.Count > 0)
        {
            DialogResult answer = MessageBox.Show(this,
                string.Join(Environment.NewLine, missing) + Environment.NewLine + Environment.NewLine + "Turn those on too?",
                Title, MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (answer == DialogResult.Cancel)
                return false;
            if (answer == DialogResult.Yes)
            {
                foreach (ListViewItem item in mods.Items)
                    if (missing.Any(m => m.EndsWith(" needs " + ((ModRow)item.Tag).Id)))
                        item.Checked = true;
                enabled = SelectedMods();
            }
        }
        List<string> incompatible = enabled.Where(id => known.TryGetValue(id, out ModInfo m) && m.Compatibility == "Incompatible").ToList();
        if (incompatible.Count > 0 &&
            MessageBox.Show(this, "These mods say they don't work with Multiplayer: " + string.Join(", ", incompatible) + ". Keep them on?",
                Title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return false;

        config.GameDir = game;
        config.Save = chosen.Value;
        config.AutosaveMinutes = (int)autosave.Value;
        config.Fps = (int)fps.Value;
        config.DailyRestart = dailyRestartOn.Checked ? dailyRestart.Value.ToString("HH:mm") : "";
        config.ShowGameWindow = showGameWindow.Checked;
        config.ShowDebugLines = showDebug.Checked;
        var settings = new ServerSettings
        {
            ServerName = serverName.Text.Trim(),
            Password = password.Text.Trim(),
            MaxPlayers = (int)maxPlayers.Value,
            Visibility = visibility.SelectedItem as string ?? "Public",
            Port = (int)port.Value,
            Details = details.Text.Trim(),
            Admins = string.Join(",", SplitAdmins(admins.Text)),
            DebugLogging = debugLogging.Checked,
        };

        setupPanel.Enabled = false;
        UseWaitCursor = true;
        setupStatus.Text = "Saving (the first time copies your mods, which can take a minute)…";
        var said = new List<string>();
        try
        {
            await Task.Run(() =>
            {
                config.Write();
                ServerMods.Apply(game, AppConfig.ServerDir, enabled, said.Add);
                string path = ServerMods.MultiplayerSettingsPath(AppConfig.ServerDir)
                              ?? throw new InvalidOperationException("the server has no Multiplayer mod after copying it");
                settings.Save(path);
            });
        }
        catch (Exception e)
        {
            setupPanel.Enabled = true;
            UseWaitCursor = false;
            return Fail("Couldn't save: " + e.Message);
        }
        setupPanel.Enabled = true;
        UseWaitCursor = false;
        foreach (string line in said)
            AppendLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}", LineKind.App);
        // Read back what's on disk, so the screen shows what the server will use.
        LoadSetup();
        setupStatus.Text = $"Saved at {DateTime.Now:HH:mm:ss}." + (said.Count > 0 ? " " + string.Join("; ", said) + "." : "");
        return true;
    }

    private bool Fail(string message)
    {
        setupStatus.Text = message;
        MessageBox.Show(this, message, Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return false;
    }

    private void BrowseGame()
    {
        using var dialog = new FolderBrowserDialog { Description = "Derail Valley's folder (the one with DerailValley.exe)", SelectedPath = gameDir.Text };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            gameDir.Text = dialog.SelectedPath;
            LoadMods(SelectedMods());
        }
    }

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true });
    }

    // ---- Server ----

    private void StartServer()
    {
        if (watchdog?.Running == true)
            return;
        if (Dirty)
        {
            DialogResult answer = MessageBox.Show(this, "The setup has unsaved changes. Save them first?", Title, MessageBoxButtons.YesNoCancel);
            if (answer == DialogResult.Cancel)
                return;
            if (answer == DialogResult.Yes)
            {
                tabs.SelectedTab = setupTab;
                saveAndStartButton.PerformClick();
                return;
            }
            LoadSetup();
        }
        if (!ServerMods.Exists(AppConfig.ServerDir) || string.IsNullOrEmpty(config.Save) || !AppConfig.IsGameDir(config.GameDir))
        {
            tabs.SelectedTab = setupTab;
            Fail("Set the server up first: pick a save and Save.");
            return;
        }
        if (Watchdog.OtherServerRunning())
        {
            if (MessageBox.Show(this, "A server started outside this app is running (the watchdog script, or an earlier run of this app). " +
                                      "Ask it to save and stop? Start again once it has.", Title, MessageBoxButtons.YesNo) == DialogResult.Yes)
            {
                Watchdog.RequestStopOther();
                AppendLine("asked the other server to save and stop", LineKind.App);
            }
            return;
        }
        ServerSettings settings = ServerSettings.Load(ServerMods.MultiplayerSettingsPath(AppConfig.ServerDir));
        watchdog = new Watchdog(new Watchdog.Options
        {
            GameDir = config.GameDir,
            ServerDir = AppConfig.ServerDir,
            DataDir = AppConfig.DataDir,
            Save = config.Save,
            Port = settings.Port,
            Visibility = settings.Visibility,
            AutosaveMinutes = config.AutosaveMinutes,
            Fps = config.Fps,
            StaleSeconds = config.StaleSeconds,
            StartupSeconds = config.StartupSeconds,
            DailyRestart = config.DailyRestart.Length > 0 && TimeSpan.TryParse(config.DailyRestart, out TimeSpan t) ? t : null,
            ShowGameWindow = config.ShowGameWindow,
        });
        watchdog.Line += (text, kind) => Ui(() => AppendLine(text, kind));
        watchdog.Beat += b => Ui(() => ShowBeat(b));
        watchdog.Ready += () => Ui(() => { state.Text = "Running: players can join"; state.ForeColor = Color.ForestGreen; });
        watchdog.GameStarted += () => Ui(() =>
        {
            state.Text = "Starting: loading the world takes a minute or two…";
            state.ForeColor = Color.DarkOrange;
            beat.Text = "";
        });
        Watchdog started = watchdog;
        watchdog.Stopped += () => Ui(() =>
        {
            if (watchdog != started) return;
            SetRunning(false);
            if (closeWhenStopped) Close();
        });
        SetRunning(true);
        watchdog.Start();
    }

    private void Ui(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(action); } catch (Exception) { }
    }

    private void SetRunning(bool running)
    {
        startButton.Enabled = !running;
        stopButton.Enabled = restartButton.Enabled = running;
        send.Enabled = command.Enabled = running;
        foreach (Control c in setupPanel.Controls)
            c.Enabled = !running;
        state.Text = running ? "Starting: loading the world takes a minute or two…" : "Stopped";
        state.ForeColor = running ? Color.DarkOrange : SystemColors.ControlText;
        beat.Text = running ? "" : (ServerMods.Exists(AppConfig.ServerDir) ? $"Hosting {config?.Save}. Start runs it and keeps it running." : "");
        if (running)
            setupStatus.Text = "Stop the server to change its setup.";
    }

    private void ShowBeat(Heartbeat b)
    {
        var parts = new List<string>
        {
            b.Players.Length == 0 ? "Nobody online" : $"{b.Players.Length} online: {string.Join(", ", b.Players)}",
        };
        if (b.Fps is double f) parts.Add($"{f:0} fps");
        if (b.Paused) parts.Add("paused");
        if (b.LastSave is DateTime saved) parts.Add($"last save {saved:HH:mm}");
        if (b.Stopping) parts.Add("stopping");
        beat.Text = string.Join(" · ", parts);
    }

    private void SendCommand()
    {
        string line = command.Text.Trim();
        if (line.Length == 0 || watchdog?.Running != true)
            return;
        try { watchdog.SendCommand(line); }
        catch (Exception e) { AppendLine("couldn't send the command: " + e.Message, LineKind.Error); return; }
        if (history.Count == 0 || history[history.Count - 1] != line)
            history.Add(line);
        historyIndex = history.Count;
        command.Clear();
    }

    private void ShowHistory()
    {
        command.Text = historyIndex < history.Count ? history[historyIndex] : "";
        command.SelectionStart = command.Text.Length;
    }

    private void AppendLine(string text, LineKind kind)
    {
        if (kind == LineKind.Debug && !showDebug.Checked)
            return;
        // Keep the view to the last few thousand lines.
        if (log.Lines.Length > 5000)
        {
            log.ReadOnly = false;
            log.Select(0, log.GetFirstCharIndexFromLine(1000));
            log.SelectedText = "";
            log.ReadOnly = true;
        }
        log.SelectionStart = log.TextLength;
        log.SelectionLength = 0;
        log.SelectionColor = kind switch
        {
            LineKind.Error => Color.FromArgb(255, 110, 100),
            LineKind.Warning => Color.FromArgb(240, 200, 80),
            LineKind.Console => Color.FromArgb(90, 210, 230),
            LineKind.App => Color.White,
            LineKind.Debug => Color.Gray,
            _ => Color.Gainsboro,
        };
        log.AppendText(text + Environment.NewLine);
        log.SelectionColor = log.ForeColor;
        log.ScrollToCaret();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (watchdog?.Running == true)
        {
            if (!closeWhenStopped &&
                MessageBox.Show(this, "The server is running. Stop it (it saves first) and close?", Title, MessageBoxButtons.OKCancel) != DialogResult.OK)
            {
                e.Cancel = true;
                return;
            }
            closeWhenStopped = true;
            watchdog.Stop();
            state.Text = "Stopping (saving first), then closing…";
            e.Cancel = true;
            return;
        }
        base.OnFormClosing(e);
    }
}
