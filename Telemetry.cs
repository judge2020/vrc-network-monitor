using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace VrcNetworkMonitor;

public sealed class FrameWindow
{
    private readonly Queue<(double Time, string Chain, double Ms, double? CpuBusy, double? Gpu)> frames = new();
    private string[] header = [];
    private double latest;
    public long AcceptedCount { get; private set; }
    public void Accept(string line)
    {
        var fields = Csv(line);
        if (fields.Contains("msBetweenPresents") || fields.Contains("CPUStartTime")) { header = fields; return; }
        string Get(string name) { int i = Array.IndexOf(header, name); return i >= 0 && i < fields.Length ? fields[i] : ""; }
        double? Number(params string[] names) {
            foreach (var name in names) if (double.TryParse(Get(name), CultureInfo.InvariantCulture, out double v) && double.IsFinite(v) && v >= 0) return v;
            return null;
        }
        bool legacy = header.Contains("TimeInSeconds");
        double? time = legacy ? Number("TimeInSeconds") : Number("CPUStartTime") / 1000;
        double? busy = Number("CPUBusy", "MsCPUBusy"), wait = Number("CPUWait", "MsCPUWait");
        double? ms = legacy ? Number("msBetweenPresents") : Number("FrameTime", "MsBetweenPresents") ?? busy + wait;
        if (!time.HasValue || !ms.HasValue || ms <= 0) return;
        var chain = Get("SwapChainAddress"); if (chain.Length == 0) return;
        AcceptedCount++; latest = Math.Max(latest, time.Value); frames.Enqueue((time.Value, chain, ms.Value, busy, Number("GPUTime", "MsGPUTime")));
        while (frames.Count > 0 && (frames.Peek().Time < latest - 2 || frames.Count > 20000)) frames.Dequeue();
    }
    public double? Fps()
    {
        var chain = frames.GroupBy(f => f.Chain).OrderByDescending(g => g.Count()).FirstOrDefault();
        return chain is null || chain.Count() < 2 ? null : 1000 / chain.Average(f => f.Ms);
    }
    public double? Timing(string kind)
    {
        var chain = frames.GroupBy(f => f.Chain).OrderByDescending(g => g.Count()).FirstOrDefault();
        if (chain is null || chain.Count() < 2) return null;
        var values = chain.Select(f => kind == "cpu" ? (double?)f.Ms : kind == "busy" ? f.CpuBusy : f.Gpu).ToArray();
        // Missing GPU data must not turn into a reassuring zero or a partial average.
        return values.Any(v => !v.HasValue) ? null : values.Average(v => v!.Value);
    }
    public static string[] Csv(string line)
    {
        var result = new List<string>(); var value = new StringBuilder(); bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"') { if (quoted && i + 1 < line.Length && line[i + 1] == '"') { value.Append('"'); i++; } else quoted = !quoted; }
            else if (c == ',' && !quoted) { result.Add(value.ToString()); value.Clear(); }
            else value.Append(c);
        }
        result.Add(value.ToString()); return result.ToArray();
    }
}

public sealed class PresentCapture : IDisposable
{
    private readonly object gate = new();
    private FrameWindow window = new();
    private Process? process;
    private int? pid;
    private string? session;
    private DateTime lastFrame, nextStart;
    private string diagnostic = "Waiting for VRChat";
    private readonly string executable = Path.Combine(AppContext.BaseDirectory, "tools", "PresentMon.exe");
    public List<Metric> Capture(int? target)
    {
        if (target != pid) { Stop(); pid = target; nextStart = DateTime.MinValue; }
        if (target.HasValue && process is null && DateTime.UtcNow >= nextStart)
        {
            nextStart = DateTime.UtcNow.AddSeconds(15);
            lock (gate) { window = new(); lastFrame = DateTime.MinValue; diagnostic = "Waiting for PresentMon frame events"; }
            try
            {
                session = "VrcMonitor-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N");
                var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var arg in new[] { "--process_id", target.Value.ToString(), "--output_stdout", "--no_console_stats", "--v2_metrics", "--session_name", session,
                    "--no_track_input", "--no_track_display" }) info.ArgumentList.Add(arg);
                var child = new Process { StartInfo = info };
                child.OutputDataReceived += (_, e) => { if (e.Data is null) return; lock (gate) { long before = window.AcceptedCount; window.Accept(e.Data); if (window.AcceptedCount != before) lastFrame = DateTime.UtcNow; } };
                child.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) lock (gate) diagnostic = e.Data.Trim(); };
                process = child;
                child.Start(); child.BeginOutputReadLine(); child.BeginErrorReadLine();
            }
            catch (Exception ex) { lock (gate) diagnostic = "FPS capture unavailable: " + ex.Message; Stop(); }
        }
        if (process is not null)
        {
            try { if (process.HasExited) { lock (gate) diagnostic = "PresentMon stopped (exit " + process.ExitCode + "): " + diagnostic; Stop(); } }
            catch (InvalidOperationException) { Stop(); }
        }
        lock (gate)
        {
            var value = process is not null && DateTime.UtcNow - lastFrame < TimeSpan.FromSeconds(3) ? window.Fps() : null;
            const string caveat = "PresentMon ETW, dominant swap chain, rolling 2-second mean. May measure desktop mirror, not headset/compositor. No fixed hardware-independent maximum.";
            return [new("process.fps", "VRChat Present FPS", value, null, "FPS", "Info", value.HasValue ? caveat : diagnostic),
                new("process.cpuFrame", "CPU frame interval (includes wait)", value.HasValue ? window.Timing("cpu") : null, null, "ms", "Info", caveat + " CPU busy + CPU wait; not pure CPU execution time."),
                new("process.cpuBusy", "CPU busy per frame", value.HasValue ? window.Timing("busy") : null, null, "ms", "Info", caveat + " Time before Present excluding CPU wait; not total work across CPU cores."),
                new("process.gpuFrame", "GPU frame duration", value.HasValue ? window.Timing("gpu") : null, null, "ms", "Info", caveat + " GPUTime includes GPU busy and wait for this frame; requires driver ETW events. Overlapping pipelines mean CPU/GPU durations should not be added.")];
        }
    }
    private void Stop()
    {
        var child = process; process = null;
        if (child is null) return;
        try
        {
            if (!child.HasExited && session is not null)
            {
                // Stop only the ETW session owned by this instance; never stop another capture.
                var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var arg in new[] { "--session_name", session, "--terminate_existing_session" }) info.ArgumentList.Add(arg);
                using var stopper = Process.Start(info);
                if (stopper is not null && !stopper.WaitForExit(1500)) stopper.Kill();
                if (!child.WaitForExit(1500)) child.Kill();
            }
        }
        catch (Exception) { try { if (!child.HasExited) child.Kill(); } catch (Exception) { } }
        finally { child.Dispose(); session = null; }
    }
    public void Dispose() => Stop();
}

public sealed class TelemetrySampler : IDisposable
{
    private readonly Sampler memory = new();
    private readonly PresentCapture frames = new();
    public readonly NetworkEvents Network = new();
    public Sample Capture()
    {
        var sample = memory.Capture(); sample.Metrics.InsertRange(0, frames.Capture(sample.Pid)); sample.Metrics.AddRange(Network.Capture(sample.Pid)); return sample;
    }
    public void Dispose() { Network.Dispose(); frames.Dispose(); memory.Dispose(); }
}

public static class ChatStatus
{
    public static readonly (string Id, string Label)[] Choices = [ ("process.fps", "FPS"), ("process.cpuFrame", "CPU frame"), ("process.gpuFrame", "GPU frame"), ("process.cpuBusy", "CPU busy"), ("process.cpu", "CPU"), ("process.ram", "RAM"),
        ("network.rateA", "Net A"), ("network.rateB", "Net B"), ("network.queue", "Queue"), ("osc.pending", "OSC pending"), ("osc.failures", "OSC errors"), ("etw.rx", "RX"), ("etw.tx", "TX"), ("etw.osc", "OSC rate") ];
    public static Sample? Average(Sample? latest, IEnumerable<Sample> history, int seconds)
    {
        if (latest is null) return null;
        if (seconds is not (1 or 5 or 10 or 15)) throw new ArgumentOutOfRangeException(nameof(seconds));
        var window = history.Where(s => s.Pid == latest.Pid && s.Timestamp > latest.Timestamp.AddSeconds(-seconds) && s.Timestamp <= latest.Timestamp).ToArray();
        return latest with { Metrics = latest.Metrics.Select(m => {
            if (!m.Value.HasValue) return m; // Do not resurrect an unavailable metric from old samples.
            var values = window.Select(s => s.Metrics.FirstOrDefault(v => v.Id == m.Id)?.Value)
                .Where(v => v.HasValue && double.IsFinite(v.Value)).Select(v => v!.Value).ToArray();
            return m with { Value = values.Length == 0 ? null : values.Average() };
        }).ToList() };
    }
    public static string Format(Sample? sample, IEnumerable<string> selected)
    {
        if (sample is null) return "";
        var parts = new List<string>();
        foreach (string id in selected)
        {
            var m = sample.Metrics.FirstOrDefault(m => m.Id == id);
            if (m?.Value is not double v || !double.IsFinite(v)) continue;
            string number = v.ToString("0", CultureInfo.InvariantCulture);
            string part = id switch
            {
                "process.fps" => "FPS(P) " + number,
                "process.cpuFrame" => "CPUft " + v.ToString("0.0", CultureInfo.InvariantCulture) + "ms",
                "process.gpuFrame" => "GPUft " + v.ToString("0.0", CultureInfo.InvariantCulture) + "ms",
                "process.cpuBusy" => "CPUbusy " + v.ToString("0.0", CultureInfo.InvariantCulture) + "ms",
                "process.cpu" => "CPU " + number + "%",
                "process.ram" => "RAM " + (v / 1024).ToString("0.0", CultureInfo.InvariantCulture) + "G",
                "network.rateA" or "network.rateB" or "network.queue" when m.Ratio.HasValue => (id == "network.queue" ? "Q " : id.EndsWith('A') ? "NetA " : "NetB ") + (m.Ratio.Value * 100).ToString("0", CultureInfo.InvariantCulture) + "%",
                "etw.rx" => "RX " + number + "K/s",
                "etw.tx" => "TX " + number + "K/s",
                "etw.osc" => "OSCrx " + number + "/s",
                "osc.pending" => "OSC " + number,
                "osc.failures" => "OSCerr " + number,
                _ => ""
            };
            if (part.Length > 0 && string.Join(" | ", parts.Append(part)).Length <= 144) parts.Add(part);
        }
        return string.Join(" | ", parts);
    }
    public static byte[] Packet(string message)
    {
        if (message.Length > 144 || message.Contains('\0')) throw new ArgumentException("Chatbox requires at most 144 characters and no NUL");
        using var bytes = new MemoryStream();
        void String(string s) { var data = Encoding.UTF8.GetBytes(s); bytes.Write(data); bytes.WriteByte(0); while (bytes.Length % 4 != 0) bytes.WriteByte(0); }
        String("/chatbox/input"); String(",sTF"); String(message); return bytes.ToArray();
    }
    public static void Send(string message, int port)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        byte[] packet = Packet(message); udp.Send(packet, packet.Length, new IPEndPoint(IPAddress.Loopback, port));
    }
}
