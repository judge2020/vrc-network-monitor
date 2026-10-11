namespace VrcNetworkMonitor;

public static class SelfTests
{
    private sealed class FakeMemory : IMemory
    {
        public readonly Dictionary<ulong, byte> Bytes = new();
        public int Reads;
        public void Set(ulong at, byte[] bytes) { for (int i = 0; i < bytes.Length; i++) Bytes[at + (ulong)i] = bytes[i]; }
        public void Long(ulong at, ulong value) => Set(at, BitConverter.GetBytes(value));
        public void Int(ulong at, int value) => Set(at, BitConverter.GetBytes(value));
        public byte[] Read(ulong at, int count)
        {
            Reads++; var bytes = new byte[count];
            for (int i = 0; i < count; i++)
                if (!Bytes.TryGetValue(at + (ulong)i, out bytes[i])) throw new IOException("Unreadable test address");
            return bytes;
        }
    }
    public static string[] Run()
    {
        var passed = new List<string>();
        void Assert(bool condition, string name) { if (!condition) throw new Exception("FAILED: " + name); passed.Add(name); }
        var denied = new FakeMemory(); var bad = new FieldReader(denied, 0x180000000, false).Capture();
        Assert(denied.Reads == 0 && bad.All(m => !m.Value.HasValue), "Mismatched profile performs zero memory reads");
        Assert(!Profile.Matches(new string('0', 64)), "Unknown binary hash rejected");
        Metric Failure(int n) => new("osc.failures", "OSC failures", n, 32, "", "Pressure", "");
        Assert(Failure(32).State == "Near threshold" && Failure(33).State == "Cutoff exceeded", "OSC cutoff is strictly greater than 32");
        Assert(new Metric("capacity", "Capacity", 100, 100, "", "Info", "").State == "Info", "Capacity is never a hard-limit alert");
        Assert(new Metric("x", "Unavailable", null, 100, "", "Pressure", "").State == "Unavailable", "Unavailable never displayed as zero or healthy");
        var mem = new FakeMemory(); ulong module = 0x180000000, type = 0x200000000, statics = 0x200001000, instance = 0x200002000;
        ulong server = 0x200003000, array = 0x200004000, buffer = 0x200005000;
        mem.Long(module + Profile.OscType, type); mem.Int(type + 0xE4, 1); mem.Long(type + 0xB8, statics);
        mem.Long(statics, instance); mem.Int(statics + 0x18, 3); mem.Long(instance, type); mem.Set(instance + 0x48, [1]);
        mem.Long(instance + 0x38, server); mem.Long(server + 0x38, array); mem.Int(server + 0x40, 8);
        mem.Int(array + 0x18, 16); mem.Long(server + 0x20, buffer); mem.Int(buffer + 0x18, 4096);
        var rows = new FieldReader(mem, module, true).Capture();
        Assert(rows.Single(m => m.Id == "osc.pending").Value == 8 && rows.Single(m => m.Id == "osc.buffer").Value == 4096, "Verified OSC pointer chain resolves queue and packet buffer");
        mem.Int(server + 0x40, 17); rows = new FieldReader(mem, module, true).Capture();
        Assert(!rows.Any(m => m.Id == "osc.pending") && rows.Any(m => m.Id == "osc" && !m.Value.HasValue), "Inconsistent queue resize snapshot discarded");
        mem.Long(instance, type + 0x1000); rows = new FieldReader(mem, module, true).Capture();
        Assert(!rows.Any(m => m.Id == "osc.pending"), "Wrong instance type rejected before traversing queue");
        var frames = new FrameWindow();
        frames.Accept("Application,SwapChainAddress,TimeInSeconds,msBetweenPresents");
        frames.Accept("\"VRChat, game.exe\",A,1.00,10"); frames.Accept("VRChat.exe,A,1.01,10"); frames.Accept("VRChat.exe,B,1.01,50");
        Assert(frames.Fps() == 100, "FPS uses dominant swap chain without summing multiple chains");
        frames.Accept("VRChat.exe,A,4,20"); frames.Accept("VRChat.exe,A,4.02,20"); frames.Accept("VRChat.exe,A,4.03,NaN");
        Assert(frames.Fps() == 50, "FPS expires old samples and rejects non-finite intervals");
        var sample = new Sample(DateTime.Now, 1, "test", "", [new("process.fps", "FPS", 90, null, "", "Info", ""), new("process.ram", "RAM", null, null, "", "Info", "")]);
        Assert(ChatStatus.Format(sample, ["process.fps", "process.ram"]) == "FPS(P) 90" && ChatStatus.Format(sample, ["process.ram"]) == "", "Chat selection omits unavailable values");
        byte[] packet = ChatStatus.Packet("FPS(P) 90");
        Assert(packet.Length % 4 == 0 && System.Text.Encoding.UTF8.GetString(packet).StartsWith("/chatbox/input\0\0\0,sTF\0\0\0\0FPS(P) 90"), "OSC address, immediate/no-sound tags and 4-byte padding");
        using var listener = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        listener.Client.ReceiveTimeout = 2000;
        ChatStatus.Send("FPS(P) 90", ((System.Net.IPEndPoint)listener.Client.LocalEndPoint!).Port);
        var endpoint = new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0);
        Assert(listener.Receive(ref endpoint).SequenceEqual(packet), "OSC UDP loopback integration receives exact packet");
        var exported = StatsExport.Parse("""{"stats":[{"name":"Traffic","units":"bytes/s","mean":12,"tw-mean":14,"min":0,"max":30,"total":100}]}""");
        Assert(exported.Single()[5] == "30" && exported.Single()[3] == "14", "Native stats export schema preserves measured maxima and time-weighted mean");
        long tick = DateTime.UtcNow.Ticks;
        Assert(!StatusRateLimit.IsDue(tick, 10000, tick + TimeSpan.FromMilliseconds(4999).Ticks, 14999, 1)
            && StatusRateLimit.IsDue(tick, 10000, tick + TimeSpan.FromSeconds(5).Ticks, 15000, 1), "OSC sender enforces a five-second floor at the exact boundary");
        Assert(!StatusRateLimit.IsDue(tick, 10000, tick + TimeSpan.FromHours(1).Ticks, 11000, 5), "Forward clock jump cannot bypass monotonic OSC cooldown");
        var v2 = new FrameWindow();
        v2.Accept("SwapChainAddress,CPUStartTime,FrameTime,CPUBusy,CPUWait,GPUTime");
        v2.Accept("A,1000,10,3,7,6"); v2.Accept("A,1010,10,5,5,8");
        Assert(v2.Fps() == 100 && v2.Timing("cpu") == 10 && v2.Timing("busy") == 4 && v2.Timing("gpu") == 7, "V2 CPU frame, busy and GPU durations remain distinct");
        v2.Accept("A,4000,20,5,15,NA"); v2.Accept("A,4020,20,5,15,NA");
        Assert(v2.Fps() == 50 && v2.Timing("gpu") is null, "V2 millisecond timestamps expire history; unavailable GPU stays null");
        using var wire = new MemoryStream();
        Wire.Write(wire, new CaptureRequest(1, 9000, true), default).GetAwaiter().GetResult(); wire.Position = 0;
        Assert(Wire.Read<CaptureRequest>(wire, default).GetAwaiter().GetResult() == new CaptureRequest(1, 9000, true), "IPC framed JSON round trip");
        bool rejected = false;
        using var oversized = new MemoryStream(BitConverter.GetBytes(131073));
        try { Wire.Read<CaptureRequest>(oversized, default).GetAwaiter().GetResult(); } catch (InvalidDataException) { rejected = true; }
        Assert(rejected, "Oversized IPC rejected before allocation");
        rejected = false; try { new CaptureRequest(2, 9000, false).Validate(); } catch (InvalidDataException) { rejected = true; }
        Assert(rejected, "Unknown IPC version rejected");
        rejected = false; try { new CaptureRequest(1, 0, false).Validate(); } catch (InvalidDataException) { rejected = true; }
        Assert(rejected, "Invalid OSC port rejected");
        var now = DateTime.Now;
        Sample At(double seconds, double? value, int pid = 1) => new(now.AddSeconds(seconds), pid, "", "", [new("x", "x", value, null, "", "Info", "")]);
        var current = At(0, 10);
        Assert(ChatStatus.Average(current, [At(-6, 100), At(-4, 20), At(-2, 90, 2), current], 5)!.Metrics[0].Value == 15, "Status average includes only window and current process");
        Assert(ChatStatus.Average(At(0, null), [At(-1, 10), At(0, null)], 5)!.Metrics[0].Value is null, "Average cannot revive currently unavailable measurements");
        return passed.ToArray();
    }
}
