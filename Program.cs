using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WindowsCodexUsageTaskbar;

internal static class Program {
    [STAThread]
    private static void Main() {
        using var singleInstance = new Mutex(
            initiallyOwned: true,
            name: "WindowsCodexUsageTaskbar.SingleInstance",
            createdNew: out var createdNew);

        if (!createdNew) {
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new UsageApplicationContext());
    }
}

// Edit these colors to customize the tray icon and usage panel appearance.
internal static class AppColors {
    public static readonly Color Green = Color.FromArgb(0, 166, 81);
    public static readonly Color Orange = Color.FromArgb(204, 106, 0);
    public static readonly Color Red = Color.FromArgb(196, 30, 30);
    public static readonly Color TrayText = Color.Black;
    public static readonly Color PanelBackground = Color.FromArgb(28, 30, 34);
    public static readonly Color PanelText = Color.White;
    public static readonly Color PanelResetText = Color.LightGray;
    public static readonly Color PanelCloseText = Color.Gray;
}

internal static class AppSettings {
    // Usage is refreshed this often. Keep this at 1 or higher.
    public const int RefreshIntervalSeconds = 60;

    // Remaining-usage thresholds for the tray icon and panel percentages.
    public const int OrangeThresholdPercent = 25;
    public const int RedThresholdPercent = 10;

    // Panel layout and typography.
    public const int PanelWidth = 360;
    public const int PanelHeight = 178;
    public const float PanelHeaderFontSize = 13;
    public const float PanelUsageFontSize = 12;
    public const float PanelResetFontSize = 9;

    // Interaction and first-run options.
    public const bool OpenPanelOnLeftClick = true;
    public const bool ShowWeeklyLimit = true;
    public const bool ColorIconByWeeklyLimit = true;
    public const bool AskForStartupOnFirstRun = true;
    public const bool AskForDesktopShortcutOnFirstRun = true;
}

internal sealed class UsageApplicationContext : ApplicationContext {
    private const string StartupPromptFileName = "startup-prompt-shown";
    private const string DesktopShortcutPromptFileName = "desktop-shortcut-prompt-shown";

    private readonly NotifyIcon tray;
    private readonly CodexClient client;
    private readonly System.Windows.Forms.Timer timer;
    private readonly UsageWidget panel;
    private readonly SemaphoreSlim refreshGate = new(1, 1);

    private UsageResult? latest;
    private Icon currentIcon;
    private bool isExiting;

    public UsageApplicationContext() {
        client = new CodexClient();
        panel = new UsageWidget();
        currentIcon = TrayIconFactory.Create(UsageResult.Loading());

        tray = new NotifyIcon {
            Icon = currentIcon,
            Visible = true,
            Text = "Codex usage"
        };

        tray.ContextMenuStrip = CreateContextMenu();
        tray.MouseClick += OnTrayMouseClick;
        tray.DoubleClick += async (_, _) => await RefreshAsync();

        timer = new System.Windows.Forms.Timer {
            Interval = AppSettings.RefreshIntervalSeconds * 1000
        };
        timer.Tick += async (_, _) => await RefreshAsync();
        timer.Start();

        PromptForStartupAndDesktopShortcut();
        _ = RefreshAsync();
    }

    private ContextMenuStrip CreateContextMenu() {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Refresh now", null, async (_, _) => await RefreshAsync());
        menu.Items.Add("Open Codex", null, (_, _) => OpenCodexSafely());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        return menu;
    }

    private void OnTrayMouseClick(object? sender, MouseEventArgs e) {
        if (e.Button != MouseButtons.Left
            || latest is null
            || !AppSettings.OpenPanelOnLeftClick) {
            return;
        }

        if (panel.Visible) {
            panel.Hide();
        }
        else {
            panel.ShowAtCursor(latest);
        }
    }

    private void OpenCodexSafely() {
        try {
            client.OpenCodex();
        }
        catch (Exception ex) {
            MessageBox.Show(
                $"Could not open Codex: {ex.Message}",
                "WindowsCodexUsageTaskbar",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private static void PromptForStartupAndDesktopShortcut() {
        try {
            var settingsDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WindowsCodexUsageTaskbar");
            Directory.CreateDirectory(settingsDirectory);

            var startupPromptFile = Path.Combine(settingsDirectory, StartupPromptFileName);
            if (AppSettings.AskForStartupOnFirstRun
                && !File.Exists(startupPromptFile)) {
                var startupAnswer = MessageBox.Show(
                    "Start WindowsCodexUsageTaskbar automatically when you sign in to Windows?",
                    "WindowsCodexUsageTaskbar",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (startupAnswer == DialogResult.Yes) {
                    using var startup = Registry.CurrentUser.CreateSubKey(
                        @"Software\Microsoft\Windows\CurrentVersion\Run");
                    startup?.SetValue(
                        "WindowsCodexUsageTaskbar",
                        $"\"{Application.ExecutablePath}\"");
                }

                File.WriteAllText(startupPromptFile, "shown");
            }

            var desktopShortcutPromptFile = Path.Combine(
                settingsDirectory,
                DesktopShortcutPromptFileName);
            if (AppSettings.AskForDesktopShortcutOnFirstRun
                && !File.Exists(desktopShortcutPromptFile)) {
                var shortcutAnswer = MessageBox.Show(
                    "Create a WindowsCodexUsageTaskbar shortcut on your desktop?",
                    "WindowsCodexUsageTaskbar",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (shortcutAnswer == DialogResult.Yes) {
                    CreateDesktopShortcut();
                }

                File.WriteAllText(desktopShortcutPromptFile, "shown");
            }
        }
        catch (Exception ex) {
            MessageBox.Show(
                $"Could not save the first-run settings: {ex.Message}",
                "WindowsCodexUsageTaskbar",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private static void CreateDesktopShortcut() {
        object? shellObject = null;
        object? shortcutObject = null;

        try {
            var desktopPath = Environment.GetFolderPath(
                Environment.SpecialFolder.DesktopDirectory);
            var shortcutPath = Path.Combine(
                desktopPath,
                "WindowsCodexUsageTaskbar.lnk");
            var shellType = Type.GetTypeFromProgID("WScript.Shell")
                ?? throw new InvalidOperationException(
                    "Windows Script Host is unavailable.");
            shellObject = Activator.CreateInstance(shellType)
                ?? throw new InvalidOperationException(
                    "Could not create the desktop shortcut.");
            dynamic shell = shellObject;
            shortcutObject = shell.CreateShortcut(shortcutPath);
            dynamic shortcut = shortcutObject;

            shortcut.TargetPath = Application.ExecutablePath;
            shortcut.WorkingDirectory = AppContext.BaseDirectory;
            shortcut.Description = "Show Codex usage in the Windows notification area.";
            shortcut.IconLocation = $"{Application.ExecutablePath},0";
            shortcut.Save();
        }
        catch (Exception ex) {
            MessageBox.Show(
                $"Could not create the desktop shortcut: {ex.Message}",
                "WindowsCodexUsageTaskbar",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        finally {
            if (shortcutObject is not null && Marshal.IsComObject(shortcutObject)) {
                Marshal.FinalReleaseComObject(shortcutObject);
            }

            if (shellObject is not null && Marshal.IsComObject(shellObject)) {
                Marshal.FinalReleaseComObject(shellObject);
            }
        }
    }

    private async Task RefreshAsync() {
        if (isExiting) {
            return;
        }

        if (!await refreshGate.WaitAsync(0)) {
            return;
        }

        try {
            var result = await client.ReadRateLimitsAsync();

            if (isExiting) {
                return;
            }

            latest = result;

            if (panel.Visible) {
                panel.UpdateUsage(result);
            }

            var nextIcon = TrayIconFactory.Create(result);
            tray.Icon = nextIcon;
            currentIcon.Dispose();
            currentIcon = nextIcon;
            tray.Text = result.Tooltip.Length > 63
                ? result.Tooltip[..63]
                : result.Tooltip;
        }
        finally {
            refreshGate.Release();
        }
    }

    protected override void ExitThreadCore() {
        isExiting = true;
        timer.Stop();
        tray.Visible = false;
        tray.Dispose();
        panel.Close();
        currentIcon.Dispose();
        client.Dispose();
        base.ExitThreadCore();
    }
}

internal sealed class UsageWidget : Form {
    private readonly Label header;
    private readonly Label fiveHourLabel;
    private readonly Label fiveHourPercentage;
    private readonly Label weeklyLabel;
    private readonly Label weeklyPercentage;
    private readonly Label fiveHourReset;
    private readonly Label weeklyReset;
    private readonly Button close;

    public UsageWidget() {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = AppColors.PanelBackground;
        ForeColor = AppColors.PanelText;
        Text = "Codex usage";
        Width = AppSettings.PanelWidth;
        Height = AppSettings.PanelHeight;

        header = new Label {
            AutoSize = true,
            Location = new Point(18, 16),
            Text = "Codex usage",
            Font = new Font("Segoe UI", AppSettings.PanelHeaderFontSize, FontStyle.Bold),
            ForeColor = AppColors.PanelText
        };

        fiveHourLabel = CreateUsageLabel(new Point(18, 70));
        fiveHourPercentage = CreateUsageLabel(new Point(72, 70));
        weeklyLabel = CreateUsageLabel(new Point(18, 117));
        weeklyPercentage = CreateUsageLabel(new Point(72, 117));

        fiveHourReset = CreateResetLabel(new Point(183, 70));
        weeklyReset = CreateResetLabel(new Point(183, 117));
        SetWeeklyVisibility(AppSettings.ShowWeeklyLimit);

        close = new Button {
            Text = "×",
            FlatStyle = FlatStyle.Flat,
            ForeColor = AppColors.PanelCloseText,
            BackColor = BackColor,
            Location = new Point(AppSettings.PanelWidth - 34, 8),
            Width = 26,
            Height = 26
        };
        close.FlatAppearance.BorderSize = 0;
        close.Click += (_, _) => Hide();
        Deactivate += (_, _) => Hide();

        Controls.AddRange(
        [
            header,
            fiveHourLabel,
            fiveHourPercentage,
            weeklyLabel,
            weeklyPercentage,
            fiveHourReset,
            weeklyReset,
            close
        ]);

        Shown += (_, _) => PositionNearCursor();
    }

    private static Label CreateUsageLabel(Point location) {
        return new Label {
            AutoSize = true,
            Location = location,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Segoe UI", AppSettings.PanelUsageFontSize, FontStyle.Bold),
            ForeColor = AppColors.PanelText
        };
    }

    private static Label CreateResetLabel(Point location) {
        return new Label {
            AutoSize = false,
            Location = location,
            Size = new Size(AppSettings.PanelWidth - 201, 30),
            TextAlign = ContentAlignment.MiddleRight,
            Font = new Font("Segoe UI", AppSettings.PanelResetFontSize),
            ForeColor = AppColors.PanelResetText
        };
    }

    private void PositionNearCursor() {
        var cursor = Cursor.Position;
        var workArea = Screen.FromPoint(cursor).WorkingArea;
        var x = Math.Max(workArea.Left, Math.Min(cursor.X - Width / 2, workArea.Right - Width));
        var y = Math.Max(workArea.Top, cursor.Y - Height - 8);
        Location = new Point(x, y);
    }

    public void UpdateUsage(UsageResult result) {
        Text = result.Tooltip;

        SetWeeklyVisibility(AppSettings.ShowWeeklyLimit);

        var display = result.Display.Split('\n');
        var resets = result.ResetText.Split('\n');

        SetUsageLine(fiveHourLabel, fiveHourPercentage, "5h:", display.ElementAtOrDefault(0) ?? result.Display, result.Color);
        if (AppSettings.ShowWeeklyLimit) {
            SetUsageLine(weeklyLabel, weeklyPercentage, "7d:", display.ElementAtOrDefault(1) ?? "", result.Color);
            weeklyReset.Text = resets.ElementAtOrDefault(1) ?? "";
        }

        fiveHourReset.Text = resets.ElementAtOrDefault(0) ?? "";
    }

    private void SetWeeklyVisibility(bool visible) {
        weeklyLabel.Visible = visible;
        weeklyPercentage.Visible = visible;
        weeklyReset.Visible = visible;
    }

    private static void SetUsageLine(
        Label name,
        Label value,
        string fixedName,
        string line,
        Color valueColor) {
        var separator = line.IndexOf(':');
        name.Text = fixedName;
        value.Text = separator >= 0 ? line[(separator + 1)..].Trim() : "";
        value.ForeColor = valueColor;
    }

    public void ShowAtCursor(UsageResult result) {
        UpdateUsage(result);
        PositionNearCursor();
        Show();
        Activate();
    }
}

internal sealed record UsageWindow(
    string Name,
    double UsedPercent,
    long? ResetAt,
    int? WindowDurationMinutes);

internal sealed record UsageResult(
    string Display,
    string ResetText,
    string Tooltip,
    Color Color,
    int Remaining) {
    public static UsageResult Loading() => new(
        "Loading...",
        "",
        "Codex usage loading",
        Color.Gray,
        -1);
}

internal static class TrayIconFactory {
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    public static Icon Create(UsageResult result) {
        using var bitmap = new Bitmap(64, 64);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);

        using var background = new SolidBrush(result.Color);
        graphics.FillEllipse(background, 2, 2, 60, 60);

        using var font = new Font(
            "Segoe UI",
            result.Remaining >= 100 ? 23 : 32,
            FontStyle.Bold,
            GraphicsUnit.Pixel);
        using var format = new StringFormat {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center
        };
        using var textBrush = new SolidBrush(AppColors.TrayText);

        if (result.Remaining >= 0) {
            graphics.DrawString(
                Math.Clamp(result.Remaining, 0, 100).ToString(),
                font,
                textBrush,
                new RectangleF(2, 2, 60, 60),
                format);
        }

        var handle = bitmap.GetHicon();
        try {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally {
            DestroyIcon(handle);
        }
    }
}

internal sealed class CodexClient : IDisposable {
    private const string CodexClientName = "codex-usage-taskbar";
    private const string CodexClientVersion = "0.1.0";

    private Process? process;
    private StreamWriter? input;
    private StreamReader? output;
    private int requestId;

    public async Task<UsageResult> ReadRateLimitsAsync() {
        try {
            EnsureProcess();

            var initializeId = Interlocked.Increment(ref requestId);
            await WriteAsync(new {
                jsonrpc = "2.0",
                id = initializeId,
                method = "initialize",
                @params = new {
                    clientInfo = new {
                        name = CodexClientName,
                        version = CodexClientVersion
                    },
                    capabilities = new { }
                }
            });

            await WriteAsync(new {
                jsonrpc = "2.0",
                method = "initialized",
                @params = new { }
            });

            var rateLimitsId = Interlocked.Increment(ref requestId);
            await WriteAsync(new {
                jsonrpc = "2.0",
                id = rateLimitsId,
                method = "account/rateLimits/read",
                @params = new { }
            });

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var document = await ReadResponseAsync(rateLimitsId, timeout.Token);

            if (document is null) {
                return Offline("No rate-limit response");
            }

            using (document) {
                return Parse(document.RootElement);
            }
        }
        catch (Exception ex) {
            DisposeProcess();
            return Offline(ex.Message);
        }
    }

    private async Task<JsonDocument?> ReadResponseAsync(int expectedId, CancellationToken cancellationToken) {
        while (true) {
            var line = await output!.ReadLineAsync(cancellationToken);
            if (line is null) {
                return null;
            }

            try {
                var document = JsonDocument.Parse(line);
                var root = document.RootElement;

                if (root.TryGetProperty("id", out var responseId)
                    && responseId.TryGetInt32(out var id)
                    && id == expectedId) {
                    return document;
                }

                document.Dispose();
            }
            catch (JsonException) {
                // Ignore non-JSON diagnostic output from the app-server.
            }
        }
    }

    private void EnsureProcess() {
        if (process is { HasExited: false }) {
            return;
        }

        var executable = FindCodex();
        var startInfo = new ProcessStartInfo(executable, "app-server --stdio") {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start Codex");
        input = process.StandardInput;
        output = process.StandardOutput;
    }

    private static string FindCodex() {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenAI",
            "Codex",
            "bin");

        if (Directory.Exists(root)) {
            var executable = Directory
                .GetFiles(root, "codex.exe", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();

            if (executable is not null) {
                return executable;
            }
        }

        return "codex";
    }

    public void OpenCodex() {
        const string command =
            "$pkg = Get-AppxPackage -Name 'OpenAI.Codex' | Select-Object -First 1; " +
            "if ($pkg) { Start-Process (Join-Path (Join-Path $pkg.InstallLocation 'app') 'ChatGPT.exe') } " +
            "else { throw 'OpenAI.Codex package was not found.' }";

        Process.Start(new ProcessStartInfo(
            "powershell.exe",
            $"-NoProfile -WindowStyle Hidden -Command \"{command}\"") {
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }

    private async Task WriteAsync(object value) {
        await input!.WriteLineAsync(JsonSerializer.Serialize(value));
        await input.FlushAsync();
    }

    private static UsageResult Parse(JsonElement root) {
        var values = new List<UsageWindow>();
        Walk(root, values);

        if (values.Count == 0) {
            return Offline("No usage data returned");
        }

        var fiveHour = values.FirstOrDefault(IsFiveHour) ?? values[0];
        var weekly = values.FirstOrDefault(IsWeekly)
            ?? values.FirstOrDefault(value => value != fiveHour)
            ?? fiveHour;

        var fiveHourRemaining = Math.Clamp(100 - fiveHour.UsedPercent, 0, 100);
        var weeklyRemaining = Math.Clamp(100 - weekly.UsedPercent, 0, 100);
        // Keep the icon number focused on the 5-hour limit. Optionally use the
        // more urgent of the two limits to choose its color.
        var colorBasis = AppSettings.ColorIconByWeeklyLimit
            ? Math.Min(fiveHourRemaining, weeklyRemaining)
            : fiveHourRemaining;
        var color = GetUsageColor(colorBasis);

        var display = $"5h: {fiveHourRemaining:0}%\n7d: {weeklyRemaining:0}%";
        var resetText = $"{FormatTimeRemaining(fiveHour)}\n{FormatReset(weekly)}";
        var tooltip = AppSettings.ShowWeeklyLimit
            ? $"5h: {fiveHourRemaining:0}% left | Weekly: {weeklyRemaining:0}% left"
            : $"5h: {fiveHourRemaining:0}% left";

        return new UsageResult(
            display,
            resetText,
            tooltip,
            color,
            (int)Math.Round(fiveHourRemaining));
    }

    private static Color GetUsageColor(double remaining) {
        return remaining <= AppSettings.RedThresholdPercent
            ? AppColors.Red
            : remaining <= AppSettings.OrangeThresholdPercent
                ? AppColors.Orange
                : AppColors.Green;
    }

    private static bool IsFiveHour(UsageWindow value) {
        var name = value.Name.ToLowerInvariant();
        return value.WindowDurationMinutes is <= 1440
            || name.Contains("primary")
            || name.Contains("5h")
            || name.Contains("five")
            || name.Contains("hour");
    }

    private static bool IsWeekly(UsageWindow value) {
        var name = value.Name.ToLowerInvariant();
        return value.WindowDurationMinutes is >= 3 * 24 * 60
            || name.Contains("secondary")
            || name.Contains("weekly")
            || name.Contains("week")
            || name.Contains("7day");
    }

    private static string FormatReset(UsageWindow value) {
        return value.ResetAt is long timestamp
            ? $"{DateTimeOffset.FromUnixTimeSeconds(timestamp).ToLocalTime():g}"
            : "unavailable";
    }

    private static string FormatTimeRemaining(UsageWindow value) {
        if (value.ResetAt is not long timestamp) {
            return "unavailable";
        }

        var remaining = DateTimeOffset.FromUnixTimeSeconds(timestamp) - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero) {
            return "now";
        }

        return remaining.TotalDays >= 1
            ? $"{(int)remaining.TotalDays}d {remaining.Hours}h"
            : $"{(int)remaining.TotalHours}h {remaining.Minutes}m";
    }

    private static void Walk(JsonElement element, List<UsageWindow> values, string path = "") {
        if (element.ValueKind == JsonValueKind.Object) {
            double? used = null;
            long? reset = null;
            int? duration = null;

            foreach (var property in element.EnumerateObject()) {
                var name = property.Name.ToLowerInvariant();

                if (IsUsedPercentProperty(name)
                    && property.Value.TryGetDouble(out var usedValue)) {
                    used = usedValue;
                }

                if (IsResetProperty(name)) {
                    reset = ParseReset(property.Value);
                }

                if (IsDurationProperty(name)
                    && property.Value.TryGetInt32(out var durationValue)) {
                    duration = durationValue;
                }

                var childPath = path.Length == 0
                    ? property.Name
                    : $"{path}.{property.Name}";
                Walk(property.Value, values, childPath);
            }

            if (used is not null) {
                values.Add(new UsageWindow(path, used.Value, reset, duration));
            }
        }
        else if (element.ValueKind == JsonValueKind.Array) {
            foreach (var value in element.EnumerateArray()) {
                Walk(value, values, path);
            }
        }
    }

    private static bool IsUsedPercentProperty(string name) {
        return name.Contains("usedpercent")
            || name is "used_percent" or "usedpercentage";
    }

    private static bool IsResetProperty(string name) {
        return name.Contains("resetsat")
            || name.Contains("resetat")
            || name is "reset_at" or "resets_at" or "resetat" or "resetsat";
    }

    private static bool IsDurationProperty(string name) {
        return name.Contains("windowduration")
            || name is "window_duration" or "durationminutes" or "duration_minutes";
    }

    private static long? ParseReset(JsonElement value) {
        if (value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var timestamp)) {
            return timestamp;
        }

        if (value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), out var dateTime)) {
            return dateTime.ToUnixTimeSeconds();
        }

        return null;
    }

    private static UsageResult Offline(string reason) {
        var shortReason = reason.Length > 35 ? reason[..35] : reason;
        return new UsageResult(
            "Codex offline",
            shortReason,
            "Codex usage unavailable",
            Color.Gray,
            -1);
    }

    private void DisposeProcess() {
        try {
            if (process is { HasExited: false }) {
                process.Kill(true);
            }
        }
        catch {
            // The process may already have exited during cleanup.
        }

        process?.Dispose();
        process = null;
        input = null;
        output = null;
    }

    public void Dispose() {
        DisposeProcess();
    }
}
