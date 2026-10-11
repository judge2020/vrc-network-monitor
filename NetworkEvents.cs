using System.Diagnostics;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace VrcNetworkMonitor;

public sealed class NetworkEvents : IDisposable
{
    private readonly object gate = new();
    private readonly Queue<(double Time, bool Receive, int Bytes, bool Osc)> events = new();
    private TraceEventSession? session;
    private Task? worker;
    private int targetPid;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private double began;
    private bool started, oscSeen;
    private long droppedByMonitor;
    private string state = "Network ETW not started";
    private int oscPort = 9000;
    public int OscPort
    {
        get { lock(gate) return oscPort; }
        set { lock(gate) { if(oscPort != value) { oscPort=value; events.Clear(); oscSeen=false; began=clock.Elapsed.TotalSeconds; } } }
    }
    public List<Metric> Capture(int? pid)
    {
        lock (gate)
        {
            if (targetPid != (pid ?? 0)) { targetPid = pid ?? 0; events.Clear(); began = clock.Elapsed.TotalSeconds; oscSeen = false; }
        }
        if (!started && pid.HasValue)
        {
            started = true;
            try
            {
                session = new TraceEventSession("VrcMonitorNet-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N"));
                session.StopOnDispose = true;
                session.EnableKernelProvider(KernelTraceEventParser.Keywords.NetworkTCPIP);
                var parser = session.Source.Kernel;
                void Add(int eventPid, bool rx, int size, bool osc, DateTime timestamp)
                {
                    if (size < 0) return;
                    lock (gate)
                    {
                        if (eventPid != targetPid || targetPid == 0) return;
                        // Use ETW's event time, not callback arrival time, to avoid artificial flush bursts.
                        double at = clock.Elapsed.TotalSeconds - (DateTime.Now - timestamp).TotalSeconds;
                        events.Enqueue((at, rx, size, osc)); oscSeen |= osc;
                        while (events.Count > 100000) { events.Dequeue(); droppedByMonitor++; }
                    }
                }
                parser.UdpIpRecv += e => Add(e.ProcessID,true,e.size,e.dport == OscPort,e.TimeStamp);
                parser.UdpIpSend += e => Add(e.ProcessID,false,e.size,false,e.TimeStamp);
                parser.UdpIpRecvIPV6 += e => Add(e.ProcessID,true,e.size,e.dport == OscPort,e.TimeStamp);
                parser.UdpIpSendIPV6 += e => Add(e.ProcessID,false,e.size,false,e.TimeStamp);
                parser.TcpIpRecv += e => Add(e.ProcessID,true,e.size,false,e.TimeStamp);
                parser.TcpIpSend += e => Add(e.ProcessID,false,e.size,false,e.TimeStamp);
                parser.TcpIpRecvIPV6 += e => Add(e.ProcessID,true,e.size,false,e.TimeStamp);
                parser.TcpIpSendIPV6 += e => Add(e.ProcessID,false,e.size,false,e.TimeStamp);
                var own = session;
                worker = Task.Run(() => { try { own.Source.Process(); } catch (Exception ex) { lock(gate) state = "Network ETW stopped: " + ex.Message; } });
                state = "Network ETW active"; began = clock.Elapsed.TotalSeconds;
            }
            catch (Exception ex) { state = "Network ETW unavailable: " + ex.Message; session?.Dispose(); session=null; }
        }
        lock (gate)
        {
            bool ready = session is not null && worker?.IsCompleted == false && pid.HasValue && clock.Elapsed.TotalSeconds - began >= 5;
            double now = clock.Elapsed.TotalSeconds;
            while (events.Count > 0 && events.Peek().Time < now - 5) events.Dequeue();
            var recent = events.Where(e => e.Time >= now - 5 && e.Time <= now).ToArray();
            string note = state + ". Rolling 5-second observed TCP/UDP event rate for VRChat only; includes assets/API as well as game traffic. ETW buffering/loss and unsupported paths can undercount. No game throughput cap is implied. Monitor buffer drops: " + droppedByMonitor + ".";
            return [
                new("etw.rx", "VRChat observed network receive", ready ? recent.Where(e=>e.Receive).Sum(e=>(double)e.Bytes)/5120 : null,null,"KiB/s","Info",note),
                new("etw.tx", "VRChat observed network send", ready ? recent.Where(e=>!e.Receive).Sum(e=>(double)e.Bytes)/5120 : null,null,"KiB/s","Info",note),
                new("etw.osc", "Observed OSC-port receive events", ready && oscSeen ? recent.Count(e=>e.Osc)/5d : null,null,"events/s","Info",
                    "UDP receive events to configured OSC port " + OscPort + ". Port classification only; not decoded messages or callbacks. Loopback coverage passed a 100-datagram check on this machine; blank until a matching event is seen. " + state),
                new("etw.oscSize", "Largest observed OSC-port datagram", ready && oscSeen ? recent.Where(e=>e.Osc).Select(e=>(double)e.Bytes).DefaultIfEmpty(0).Max() : null,null,"bytes","Info",
                    "Largest matching UDP event size in the last 5 seconds. Compiled OSC read-buffer reference: 4096 bytes for the investigated build; not a live memory value or packets/sec limit."),
                new("etw.lost", "Network ETW lost events", session?.Source.EventsLost,null,"events","Info","Tracing loss, not network packet loss. Nonzero means the observed rates may undercount.")
            ];
        }
    }
    public void Dispose()
    {
        session?.Dispose(); session=null;
        try { worker?.Wait(2000); } catch (AggregateException) { }
    }
}
