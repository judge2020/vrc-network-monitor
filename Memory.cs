using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace VrcNetworkMonitor;

public interface IMemory { byte[] Read(ulong address, int count); }

public sealed class ProcessMemory : IMemory, IDisposable
{
    private readonly SafeProcessHandle handle;
    public ProcessMemory(int pid)
    {
        // PROCESS_VM_READ | PROCESS_QUERY_LIMITED_INFORMATION. No write or injection rights.
        handle = OpenProcess(0x1010, false, pid);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenProcess denied read access");
    }
    public byte[] Read(ulong address, int count)
    {
        if (count < 1 || count > 4096) throw new ArgumentOutOfRangeException(nameof(count));
        var bytes = new byte[count];
        if (!ReadProcessMemory(handle, (nint)address, bytes, (nuint)count, out var read))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "ReadProcessMemory failed");
        if (read != (nuint)count) throw new IOException("Partial memory read");
        return bytes;
    }
    public void Dispose() => handle.Dispose();
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(SafeProcessHandle process, nint address, byte[] data, nuint size, out nuint read);
}

public static class Profile
{
    public const string Hash = "F9A67F6BD822A40CEAF8540ED6FBA24F0EA7293D841B771A7087BBCFCDFD1AD7";
    public const ulong NetworkType = 0xD766A80, OscType = 0xD70CE80, DatagramType = 0xD772010;
    public const ulong DispatchType = 0xD7720B8, RatesType = 0xD765DD0;
    public static bool Matches(string hash) => string.Equals(hash, Hash, StringComparison.OrdinalIgnoreCase);
}

public sealed record Metric(string Id, string Name, double? Value, double? Limit, string Unit, string Kind, string Note)
{
    public double? Ratio => Value.HasValue && Limit > 0 ? Value / Limit : null;
    public string State => !Value.HasValue ? "Unavailable" : Kind != "Pressure" ? "Info"
        : Id == "osc.failures" && Value > 32 ? "Cutoff exceeded"
        : Ratio > 1 ? "Above threshold" : Ratio >= .8 ? "Near threshold" : "Below threshold";
}
public sealed record Sample(DateTime Timestamp, int? Pid, string Status, string Detail, List<Metric> Metrics);

public sealed class FieldReader(IMemory memory, ulong moduleBase, bool verified)
{
    private byte[] Read(ulong address, int length)
    {
        if (!verified) throw new InvalidOperationException("Binary hash mismatch: offsets disabled");
        return memory.Read(address, length);
    }
    private ulong Ptr(ulong address)
    {
        ulong value = BitConverter.ToUInt64(Read(address, 8));
        if (value < 0x10000 || value > 0x00007FFFFFFFFFFF || value % 8 != 0)
            throw new InvalidDataException("Pointer absent, unresolved, or changing");
        return value;
    }
    private int Int(ulong address, int max = 10000000)
    {
        int value = BitConverter.ToInt32(Read(address, 4));
        if (value < 0 || value > max) throw new InvalidDataException("Counter outside expected range; sample discarded");
        return value;
    }
    private float Float(ulong address)
    {
        float value = BitConverter.ToSingle(Read(address, 4));
        if (!float.IsFinite(value) || value < 0 || value > 1e10) throw new InvalidDataException("Invalid estimator sample");
        return value;
    }
    private (ulong Type, ulong Static) Statics(ulong rva)
    {
        ulong type = Ptr(moduleBase + rva);
        if (Int(type + 0xE4) == 0) throw new InvalidDataException("Class is not initialized yet");
        return (type, Ptr(type + 0xB8));
    }
    private ulong Instance(ulong statics, ulong expectedType)
    {
        ulong instance = Ptr(statics);
        if (Ptr(instance) != expectedType) throw new InvalidDataException("Instance type mismatch or instance changing");
        return instance;
    }
    public List<Metric> Capture()
    {
        var rows = new List<Metric>();
        void Add(string id, string name, double value, string unit, string note, double? limit = null, string kind = "Info")
            => rows.Add(new(id, name, value, limit, unit, kind, note));
        void Group(string id, string name, Action action)
        {
            try { action(); }
            catch (Exception ex) when (ex is Win32Exception or IOException or InvalidDataException or InvalidOperationException or ArgumentException)
            { rows.Add(new(id, name, null, null, "", "Unavailable", ex.Message)); }
        }
        Group("network", "Network peer", () =>
        {
            var (type, s) = Statics(Profile.NetworkType);
            int ceiling = Int(s + 8), reserved = Int(s + 12);
            if (ceiling < 2048 || ceiling > 65536 || reserved > ceiling) throw new InvalidDataException("Network limits failed validation");
            Add("network.ceiling", "Traffic soft ceiling", ceiling, "internal rate", "Live remote-config value. Compiled fallback: 24,576; clamp: 2,048–65,536.");
            Add("network.reserve", "Reserved player traffic", reserved, "internal rate", "Live remote-config value. Compiled fallback: 7,168.");
            ulong peer = Instance(s, type);
            int max = Int(peer + 0xE8, 4096);
            if (max < 20) throw new InvalidDataException("Queue threshold failed validation");
            Add("network.queueLimit", "Outgoing queue threshold", max, "commands", "maxQueuedCommands; fallback 100. This is not an OSC receive limit.");
            Group("network.queue", "Queue pressure estimator", () =>
            {
                ulong stats = Ptr(peer + 0xE0);
                Add("network.queue", "Queue pressure estimator", Float(stats + 0x40), "commands", "Internal float tested against maxQueuedCommands; not an atomic queue-length snapshot.", max, "Pressure");
            });
            Group("network.traffic", "Traffic limiter estimators", () =>
            {
                ulong stats = Ptr(peer + 0x168);
                Add("network.rateA", "Traffic limiter estimator A", Float(stats + 0x10), "internal rate", "Compared with the soft ceiling by native admission logic. Window and byte/bit units not established.", ceiling, "Pressure");
                Add("network.rateB", "Traffic limiter estimator B", Float(stats + 0x24), "internal rate", "Second estimator tested by the same admission logic. Not a measured adapter receive rate.", ceiling, "Pressure");
            });
            Add("network.retries", "Photon maximum resends", Int(peer + 0x4C), "resends", "clientSentCountAllowance writes PhotonPeer.MaxResends. Compiled fallback 15.");
            Add("network.timeout", "Photon disconnect timeout", Int(peer + 0x54), "ms", "Timeout, not throughput. Compiled fallback 10,000 ms.");
        });
        Group("osc", "Local OSC receiver", () =>
        {
            var (type, s) = Statics(Profile.OscType);
            int failures = Int(s + 0x18);
            Add("osc.failures", "OSC send failures", failures, "failures", "Outgoing OSC only. Native guard rejects sending when count > 32 (33rd failure). Reset behavior is not established.", 32, "Pressure");
            ulong instance = Instance(s, type);
            byte enabled = Read(instance + 0x48, 1)[0];
            if (enabled > 1) throw new InvalidDataException("OSC enabled flag failed validation");
            Add("osc.enabled", "OSC update enabled", enabled, "0 / 1", "The manager calls OscServer.Update only while this flag is set.");
            ulong server = Ptr(instance + 0x38);
            // Read count, array, then count again to avoid displaying inconsistent resize snapshots.
            int count = Int(server + 0x40);
            ulong array = Ptr(server + 0x38);
            int capacity = Int(array + 0x18);
            int after = Int(server + 0x40);
            if (count != after || after > capacity) throw new InvalidDataException("OSC queue changed during read; retry next sample");
            Add("osc.pending", "OSC callbacks pending", count, "callbacks", "Snapshot only; short per-frame spikes can be missed. The queue grows; there is no verified fixed throughput cap.");
            Add("osc.capacity", "OSC queue backing capacity", capacity, "slots", "Initial capacity 16, growth +16. Capacity is not a hard maximum.");
            ulong buffer = Ptr(server + 0x20);
            Add("osc.buffer", "OSC packet buffer", Int(buffer + 0x18), "bytes", "Per-datagram application buffer; verified creation path uses 4,096. Not the Windows socket backlog.");
        });
        Group("datagrams", "Outgoing send loop", () =>
        {
            var (_, s) = Statics(Profile.DatagramType);
            Add("datagrams", "Outgoing send calls per pass", Int(s + 0x38), "calls", "clientMaxDatagrams; compiled fallback 10. Limits LateUpdate send-loop iterations, not received OSC messages.");
        });
        Group("dispatch", "Network dispatch mode", () =>
        {
            var (_, s) = Statics(Profile.DispatchType);
            byte flag = Read(s + 8, 1)[0];
            if (flag > 1) throw new InvalidDataException("Dispatch flag failed validation");
            Add("dispatch", "Network dispatch thread", flag, "0 / 1", "Live clientNetDispatchThread-related state. Not the OSC receive thread.");
        });
        Group("rates", "Network update intervals", () =>
        {
            var (_, s) = Statics(Profile.RatesType);
            string[] labels = ["Udon manual", "Minimum", "Normal", "Desktop", "Mobile", "VR", "Maximum"];
            for (int i = 0; i < labels.Length; i++) Add("rate." + i, labels[i] + " network interval", Int(s + (ulong)(i * 4)), "ms", "Internal scheduler setting, not guaranteed observed face-tracking updates per second.");
        });
        return rows;
    }
}

public sealed class Sampler : IDisposable
{
    private Process? process;
    private ProcessMemory? memory;
    private ulong moduleBase;
    private bool verified;
    private string attachment = "Waiting for VRChat";
    private DateTime nextAttach;
    private TimeSpan? previousCpu;
    private DateTime previousTime;
    private void Detach()
    {
        memory?.Dispose(); memory = null; process?.Dispose(); process = null;
        moduleBase = 0; verified = false; previousCpu = null;
    }
    public Sample Capture()
    {
        var now = DateTime.Now;
        try
        {
            if (process is not null && process.HasExited) { Detach(); nextAttach = DateTime.MinValue; }
            if (process is null && now >= nextAttach)
            {
                nextAttach = now.AddSeconds(5);
                var found = Process.GetProcessesByName("VRChat");
                if (found.Length > 1) { foreach (var p in found) p.Dispose(); attachment = "Multiple VRChat processes; close extras to select unambiguously"; }
                else if (found.Length == 1) { process = found[0]; nextAttach = DateTime.MinValue; }
                else attachment = "Waiting for VRChat to start";
            }
            if (process is not null && !verified && now >= nextAttach)
            {
                // Keep the lightweight process counters alive while retrying denied memory access less often.
                nextAttach = now.AddSeconds(30);
                    string stage = "OpenProcess";
                    try
                    {
                        memory = new ProcessMemory(process.Id);
                        stage = "Module enumeration";
                        ProcessModule? module = process.Modules.Cast<ProcessModule>().FirstOrDefault(m => m.ModuleName.Equals("GameAssembly.dll", StringComparison.OrdinalIgnoreCase));
                        if (module is null) throw new IOException("GameAssembly.dll is not visible through normal process-module enumeration. VRChat may restrict access or still be starting.");
                        stage = "Binary verification";
                        using var file = new FileStream(module.FileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        var hash = Convert.ToHexString(SHA256.HashData(file));
                        if (!Profile.Matches(hash)) throw new InvalidDataException("Unsupported GameAssembly.dll SHA-256: " + hash + ". Memory offsets disabled until this build is investigated.");
                        moduleBase = (ulong)module.BaseAddress;
                        stage = "ReadProcessMemory";
                        var mz = memory.Read(moduleBase, 2);
                        if (mz[0] != 'M' || mz[1] != 'Z') throw new InvalidDataException("Loaded module header did not validate");
                        verified = true; attachment = "Verified binary; read-only memory sampling";
                    }
                    catch (Exception ex) { memory?.Dispose(); memory = null; attachment = stage + ": " + ex.Message + (ex is Win32Exception w ? " (Win32 " + w.NativeErrorCode + ")" : ""); }
            }
            var rows = new List<Metric>();
            if (process is not null)
            {
                try
                {
                    process.Refresh();
                    long working = process.WorkingSet64;
                    rows.Add(new("process.ram", "Process working set", working > 0 ? working / 1048576d : null, null, "MiB", "Info", "Windows process counter; a restricted zero is shown as unavailable."));
                    var cpu = process.TotalProcessorTime;
                    if (previousCpu.HasValue && now > previousTime && cpu >= previousCpu)
                        rows.Add(new("process.cpu", "CPU, one-core equivalent", 100 * (cpu - previousCpu.Value).TotalSeconds / (now - previousTime).TotalSeconds, null, "%", "Info", "100% = one fully occupied logical CPU; can exceed 100%. Not main-thread frametime."));
                    previousCpu = cpu; previousTime = now;
                }
                catch (Exception ex) { rows.Add(new("process", "Windows process counters", null, null, "", "Unavailable", ex.Message)); }
            }
            if (verified && memory is not null) rows.AddRange(new FieldReader(memory, moduleBase, true).Capture());
            else rows.Add(new("memory", "Internal VRChat values", null, null, "", "Unavailable", attachment));
            return new(now, process?.Id, verified ? "Memory profile matched" : "Memory unavailable", attachment, rows);
        }
        catch (Exception ex) { Detach(); attachment = ex.Message; return new(now, null, "Waiting / disconnected", ex.Message, []); }
    }
    public void Dispose() => Detach();
}
