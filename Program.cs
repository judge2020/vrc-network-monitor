using System.Text.Json;

namespace VrcNetworkMonitor;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--daemon")) return Daemon.Run(args).GetAwaiter().GetResult();
        string output = args.SkipWhile(a => a != "--out").Skip(1).FirstOrDefault()
            ?? Path.Combine(AppContext.BaseDirectory, "probe.json");
        if (args.Contains("--helper-check"))
        {
            if (Daemon.Elevated) return 2;
            using var client = new DaemonClient();
            client.Start().GetAwaiter().GetResult();
            int helper = client.Pid!.Value;
            var samples = new List<Sample>();
            File.WriteAllText(output + ".running", JsonSerializer.Serialize(new { UiPid = Environment.ProcessId, HelperPid = helper, UiElevated = Daemon.Elevated }));
            for (int i = 0; i < 12; i++) {
                File.WriteAllText(output + ".progress", "Capture " + i);
                if (client.Capture(9000, false).GetAwaiter().GetResult() is Sample s) samples.Add(s);
                File.WriteAllText(output + ".partial", JsonSerializer.Serialize(samples)); Thread.Sleep(1000);
            }
            File.WriteAllText(output + ".progress", "Pause");
            bool pauseWorks = client.Capture(9000, true).GetAwaiter().GetResult() is null;
            // Paused UI still sends heartbeats for longer than the helper idle deadline.
            for (int i = 0; i < 11; i++) { client.Capture(9000, true).GetAwaiter().GetResult(); Thread.Sleep(1000); }
            using var helperProcess = System.Diagnostics.Process.GetProcessById(helper);
            File.WriteAllText(output + ".progress", "Disconnect");
            client.Dispose();
            File.WriteAllText(output + ".progress", "Wait for exit");
            bool exited = helperProcess.WaitForExit(10000);
            File.WriteAllText(output, JsonSerializer.Serialize(new { UiElevated = Daemon.Elevated, HelperPid = helper, PauseWorks = pauseWorks, HelperExited = exited, Samples = samples }, new JsonSerializerOptions { WriteIndented = true }));
            return exited && pauseWorks ? 0 : 1;
        }
        if (args.Contains("--network-check"))
        {
            using var listener = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
            listener.Client.ReceiveTimeout = 2000;
            using var sender = new System.Net.Sockets.UdpClient();
            using var trace = new NetworkEvents { OscPort = ((System.Net.IPEndPoint)listener.Client.LocalEndPoint!).Port };
            var initial = trace.Capture(Environment.ProcessId); Thread.Sleep(5500);
            int received = 0;
            for (int i=0; i<100; i++)
            {
                sender.Send(new byte[256], 256, new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, trace.OscPort));
                var endpoint = new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0);
                if (listener.Receive(ref endpoint).Length == 256) received++;
                Thread.Sleep(10);
            }
            Thread.Sleep(1800);
            File.WriteAllText(output, JsonSerializer.Serialize(new { LoopbackDatagramsReceived = received, Metrics = trace.Capture(Environment.ProcessId) }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        if (args.Contains("--access-probe"))
        {
            File.WriteAllText(output, JsonSerializer.Serialize(AccessDiagnostics.Run(), new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        if (args.Contains("--self-test"))
        {
            try { File.WriteAllText(output, JsonSerializer.Serialize(SelfTests.Run(), new JsonSerializerOptions { WriteIndented = true })); return 0; }
            catch (Exception ex) { File.WriteAllText(output, ex.ToString()); return 1; }
        }
        if (args.Contains("--probe"))
        {
            using var sampler = new TelemetrySampler();
            var samples = new List<Sample>();
            for (int i = 0; i < 12; i++) { samples.Add(sampler.Capture()); Thread.Sleep(1000); }
            File.WriteAllText(output, JsonSerializer.Serialize(samples, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        if (Daemon.Elevated) { MessageBox.Show("Launch the frontend normally. Only its telemetry helper should run as administrator.", "Normal permissions required"); return 2; }
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(args.Contains("--ui-smoke") ? output : null));
        return 0;
    }
}

public sealed class MainForm : Form
{
    private readonly DaemonClient sampler = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 250 };
    private readonly Label status = new() { AutoSize = true, Font = new Font("Segoe UI", 15, FontStyle.Bold) };
    private readonly Label detail = new() { AutoSize = false, Height = 58, Dock = DockStyle.Fill };
    private readonly DataGridView grid = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
    private readonly Sparkline graph = new() { Dock = DockStyle.Fill };
    private readonly Label explanation = new() { Dock = DockStyle.Fill, Padding = new Padding(10), AutoSize = false };
    private readonly Queue<Sample> history = new();
    private bool busy, paused, closing;
    private readonly CheckBox broadcast = new() { Text = "Send status above avatar", AutoSize = true };
    private readonly NumericUpDown oscPort = new() { Minimum = 1, Maximum = 65535, Value = 9000, Width = 80 };
    private readonly NumericUpDown sendInterval = new() { Minimum = 5, Maximum = 60, Value = 5, Width = 60 };
    private readonly CheckBox averageStatus = new() { Text = "Average status", AutoSize = true };
    private readonly ComboBox averageDuration = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 65 };
    private readonly Label chatPreview = new() { AutoSize = false, Dock = DockStyle.Fill };
    private readonly Dictionary<string, CheckBox> stats = new();
    private DateTime lastSend = DateTime.MinValue;
    private Sample? latest;
    private string selected = "process.fps";

    public MainForm(string? screenshot = null)
    {
        Text = "VRChat Network & OSC Monitor — read only";
        Size = new Size(1180, 930); MinimumSize = new Size(1060, 780);
        StartPosition = FormStartPosition.CenterScreen; Font = new Font("Segoe UI", 10);
        BackColor = Color.FromArgb(22, 26, 34); ForeColor = Color.FromArgb(229, 235, 243);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 8 };
        foreach (var height in new float[] { 38, 62, 45 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 120));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 180));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.Controls.Add(status, 0, 0); layout.Controls.Add(detail, 0, 1);
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill };
        var pause = new Button { Text = "Pause", Width = 88, Height = 32 };
        pause.Click += (_, _) => { paused = !paused; pause.Text = paused ? "Resume" : "Pause"; if (paused) status.Text = "Paused — displayed values are historical"; };
        var export = new Button { Text = "Export samples", Width = 130, Height = 32 };
        export.Click += (_, _) => Export();
        var reference = new Button { Text = "Build reference", Width = 130, Height = 32 };
        reference.Click += (_, _) => ShowReference();
        var interval = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 100 };
        interval.Items.AddRange(["100 ms", "250 ms", "500 ms", "1000 ms", "2000 ms"]); interval.SelectedIndex = 1;
        interval.SelectedIndexChanged += (_, _) => timer.Interval = int.Parse(interval.Text.Split(' ')[0]);
        var elevate = new Button { Text = "Start helper", Width = 120, Height = 32 };
        elevate.Click += async (_, _) => await StartHelper();
        var statsExport = new Button { Text = "VRC stats export", Width = 125, Height = 32 };
        statsExport.Click += (_, _) => StatsExport.Show(this);
        toolbar.Controls.AddRange([pause, export, reference, elevate, statsExport, new Label { Text = "Poll interval", AutoSize = true, Padding = new Padding(14, 7, 0, 0) }, interval]);
        layout.Controls.Add(toolbar, 0, 2);
        grid.BackgroundColor = BackColor; grid.BorderStyle = BorderStyle.None;
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(39, 46, 59);
        grid.ColumnHeadersDefaultCellStyle.ForeColor = ForeColor;
        grid.DefaultCellStyle.BackColor = Color.FromArgb(29, 35, 45);
        grid.DefaultCellStyle.ForeColor = ForeColor;
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(47, 72, 94);
        grid.DefaultCellStyle.SelectionForeColor = Color.White;
        grid.RowTemplate.Height = 30;
        foreach (var column in new[] { "Metric", "Live value", "Threshold", "Units", "Status" }) grid.Columns.Add(column, column);
        grid.Columns[0].FillWeight = 220; grid.Columns[4].FillWeight = 145;
        grid.SelectionChanged += (_, _) =>
        {
            if (grid.SelectedRows.Count == 0 || grid.SelectedRows[0].Tag is not Metric m) return;
            selected = m.Id; explanation.Text = m.Note; UpdateGraph();
        };
        layout.Controls.Add(grid, 0, 3); layout.Controls.Add(explanation, 0, 4); layout.Controls.Add(graph, 0, 5);
        layout.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "80% is this monitor’s warning level. Soft ceilings ≠ hard caps. OSC capacity ≠ throughput limit. Polling can miss brief spikes.", Padding = new Padding(0, 8, 0, 0) }, 0, 7);
        layout.Controls.Add(BuildChatPanel(), 0, 6);
        Controls.Add(layout);
        timer.Tick += async (_, _) => await Poll();
        Shown += async (_, _) =>
        {
            timer.Start(); if (screenshot is null) await StartHelper(); await Poll();
            if (screenshot is not null)
            {
                await Task.Delay(5000);
                using var bitmap = new Bitmap(Width, Height);
                DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height));
                bitmap.Save(screenshot, System.Drawing.Imaging.ImageFormat.Png);
                Close();
            }
        };
        FormClosing += (_, _) => { closing = true; timer.Stop(); sampler.Dispose(); };
    }
    private async Task StartHelper()
    {
        if (busy || closing) return;
        busy = true;
        try { status.Text = "Starting elevated telemetry helper…"; await sampler.Start(); }
        catch (Exception ex) { if (!closing) { status.Text = "Helper not started"; detail.Text = ex.Message + " · Use Start helper to retry."; } }
        finally { busy = false; }
    }
    private async Task Poll()
    {
        if (busy || closing) return;
        busy = true;
        try
        {
            var sample = await sampler.Capture((int)oscPort.Value, paused);
            if (sample is null) return;
            if (closing) return;
            latest = sample;
            history.Enqueue(sample); while (history.Count > 600) history.Dequeue();
            if (paused) return;
            status.Text = sample.Status + (sample.Pid.HasValue ? " · PID " + sample.Pid : "");
            status.ForeColor = sample.Status == "Memory profile matched" ? Color.FromArgb(99, 215, 169) : Color.FromArgb(255, 196, 109);
            detail.Text = sample.Detail + "\nUI: normal permissions · Elevated helper PID " + sampler.Pid + " · Last sample: " + sample.Timestamp.ToString("HH:mm:ss.fff");
            var preserve = selected; grid.Rows.Clear();
            foreach (var m in sample.Metrics)
            {
                int index = grid.Rows.Add(m.Name, m.Value?.ToString("N2") ?? "—", m.Limit?.ToString("N0") ?? "—", m.Unit, m.State);
                var row = grid.Rows[index]; row.Tag = m;
                row.DefaultCellStyle.ForeColor = m.State.Contains("exceeded") || m.State.StartsWith("Above") ? Color.FromArgb(255, 125, 135)
                    : m.State.StartsWith("Near") ? Color.FromArgb(255, 201, 113) : m.Value.HasValue ? ForeColor : Color.FromArgb(154, 166, 182);
                if (m.Id == preserve) { row.Selected = true; grid.CurrentCell = row.Cells[0]; }
            }
            if (sample.Metrics.FirstOrDefault(m => m.Id == preserve) is Metric retained)
            { selected = preserve; explanation.Text = retained.Note; }
            UpdateGraph(); UpdateChat();
        }
        catch (Exception ex) { if (!closing) { latest = null; grid.Rows.Clear(); status.Text = "Helper unavailable"; detail.Text = ex.Message; UpdateChat(false); } }
        finally { busy = false; }
    }
    private Control BuildChatPanel()
    {
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1 };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 66)); panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 33));
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 33));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var options = new FlowLayoutPanel { Dock = DockStyle.Fill };
        foreach (var choice in ChatStatus.Choices)
        {
            var check = new CheckBox { Text = choice.Label, AutoSize = true, Checked = choice.Id.StartsWith("process.") };
            check.CheckedChanged += (_, _) => UpdateChat(false); stats.Add(choice.Id, check); options.Controls.Add(check);
        }
        panel.Controls.Add(options, 0, 0);
        var controls = new FlowLayoutPanel { Dock = DockStyle.Fill };
        broadcast.CheckedChanged += (_, _) => UpdateChat(false);
        controls.Controls.AddRange([broadcast, new Label { Text = "127.0.0.1 port", AutoSize = true, Padding = new Padding(10, 4, 0, 0) }, oscPort,
            new Label { Text = "Every", AutoSize = true, Padding = new Padding(10, 4, 0, 0) }, sendInterval, new Label { Text = "seconds · no sound · unavailable stats omitted", AutoSize = true, Padding = new Padding(0, 4, 0, 0) }]);
        panel.Controls.Add(controls, 0, 1);
        var averages = new FlowLayoutPanel { Dock = DockStyle.Fill };
        averageDuration.Items.AddRange(["1s", "5s", "10s", "15s"]); averageDuration.SelectedIndex = 1; averageDuration.Enabled = false;
        averageStatus.CheckedChanged += (_, _) => { averageDuration.Enabled = averageStatus.Checked; UpdateChat(false); };
        averageDuration.SelectedIndexChanged += (_, _) => UpdateChat(false);
        averages.Controls.AddRange([averageStatus, averageDuration, new Label { Text = "rolling sample mean · warms up with available samples · send interval unchanged", AutoSize = true, Padding = new Padding(8, 4, 0, 0) }]);
        panel.Controls.Add(averages, 0, 2); panel.Controls.Add(chatPreview, 0, 3); return panel;
    }
    private void UpdateChat(bool allowSend = true)
    {
        int seconds = new[] { 1, 5, 10, 15 }[Math.Max(0, averageDuration.SelectedIndex)];
        var source = averageStatus.Checked ? ChatStatus.Average(latest, history, seconds) : latest;
        string message = ChatStatus.Format(source, stats.Where(s => s.Value.Checked).Select(s => s.Key));
        if (averageStatus.Checked && message.Length > 0) message = "Avg" + seconds + "s | " + message;
        if (message.Length > 144) { int cut = message.LastIndexOf(" | ", 143, StringComparison.Ordinal); message = cut > 0 ? message[..cut] : message[..144]; }
        string state = broadcast.Checked ? "Broadcast enabled" : "Broadcast off";
        if (allowSend && broadcast.Checked && !paused && latest is not null && (DateTime.Now - latest.Timestamp).TotalSeconds < 3
            && message.Length > 0 && (DateTime.UtcNow - lastSend).TotalSeconds >= (double)sendInterval.Value)
        {
            lastSend = DateTime.UtcNow;
            try { state = StatusRateLimit.TrySend(message, (int)oscPort.Value, (int)sendInterval.Value) ? "UDP sent (delivery not acknowledged)" : "Waiting for shared OSC cooldown"; }
            catch (Exception ex) { state = "Send failed: " + ex.Message; broadcast.Checked = false; }
        }
        chatPreview.Text = state + " · Preview: " + (message.Length > 0 ? message : "no selected stats available")
            + "\nFPS(P) = Present FPS; CPU 100% = one logical core. Enable OSC in VRChat. Pause also pauses sending.";
    }
    private void UpdateGraph()
    {
        graph.Set(history.Select(s => (s.Timestamp, s.Metrics.FirstOrDefault(m => m.Id == selected))).ToArray());
    }
    private void Export()
    {
        using var dialog = new SaveFileDialog { Filter = "JSON samples|*.json", FileName = "vrchat-monitor-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(history.ToArray(), new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Export failed"); }
    }
    private void ShowReference()
    {
        using var dialog = new Form { Text = "Compiled reference — NOT live values", Width = 950, Height = 530, StartPosition = FormStartPosition.CenterParent };
        var table = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
        foreach (var name in new[] { "Name", "Compiled fallback", "Meaning / constraint" }) table.Columns.Add(name, name);
        table.Columns[2].FillWeight = 230;
        string[][] rows = [
            ["OSC packet buffer", "4096 bytes", "One received datagram; not socket backlog"],
            ["OSC callback array", "16 slots, grows +16", "Expandable capacity; no fixed callback cap found"],
            ["OSC argument capacity", "32", "Arguments within one message, not number of avatar parameters"],
            ["OSC send-error cutoff", "> 32 errors", "Outgoing send path only"],
            ["clientBPSCeiling", "24576", "Remote config; clamp 2048–65536; rate units not independently verified"],
            ["clientReservedPlayerBPS", "7168", "Remote config; clamp 0–ceiling"],
            ["maxQueuedCommands", "100", "Remote config; clamp 20–4096; outgoing pressure threshold"],
            ["clientMaxDatagrams", "10", "Remote config; outgoing send calls per eligible pass"],
            ["clientSentCountAllowance", "15", "Remote config; Photon MaxResends"],
            ["clientDisconnectTimeout", "10000 ms", "Remote config; disconnect timeout"],
            ["Update intervals", "50 / 50 / 200 / 200 / 200 / 50 / 2000 ms", "Udon / minimum / normal / desktop / mobile / VR / maximum"],
            ["clientNetDispatchThread", "Live state unknown", "Remote config + an override branch; distinct from OSC receive thread"]
        ];
        foreach (var row in rows) table.Rows.Add(row.Cast<object>().ToArray());
        dialog.Controls.Add(table); dialog.ShowDialog(this);
    }
}

public sealed class Sparkline : Control
{
    private (DateTime Time, Metric? Metric)[] points = [];
    public Sparkline() { DoubleBuffered = true; }
    public void Set((DateTime, Metric?)[] values) { points = values; Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); e.Graphics.Clear(Color.FromArgb(17, 21, 28));
        var valid = points.Where(p => p.Metric?.Value is not null).ToArray();
        using var brush = new SolidBrush(Color.LightSteelBlue);
        if (valid.Length < 2) { e.Graphics.DrawString("Select a metric. History appears after two valid samples; gaps remain blank.", Font, brush, 12, 12); return; }
        var last = valid[^1].Metric!; double max = Math.Max(1, valid.Max(p => Math.Max(p.Metric!.Value!.Value, p.Metric.Limit ?? 0))) * 1.1;
        double timeSpan = Math.Max(.001, (points[^1].Time - points[0].Time).TotalSeconds);
        e.Graphics.DrawString(last.Name + " · " + last.Unit + " · " + timeSpan.ToString("N0") + " seconds", Font, brush, 12, 6);
        float Y(double value) => Height - 12 - (float)(value / max * (Height - 42));
        using var line = new Pen(Color.FromArgb(92, 206, 200), 2);
        using var limit = new Pen(Color.FromArgb(255, 190, 100)) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
        if (last.Limit.HasValue) e.Graphics.DrawLine(limit, 12, Y(last.Limit.Value), Width - 12, Y(last.Limit.Value));
        PointF? previous = null;
        foreach (var p in points)
        {
            if (p.Metric?.Value is not double value) { previous = null; continue; }
            var point = new PointF(12 + (float)((p.Time - points[0].Time).TotalSeconds / timeSpan * (Width - 24)), Y(value));
            if (previous.HasValue) e.Graphics.DrawLine(line, previous.Value, point);
            previous = point;
        }
    }
}
