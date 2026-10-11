using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace VrcNetworkMonitor;

public record CaptureRequest(int Version, int OscPort, bool Paused)
{
    public void Validate() { if (Version != 1 || OscPort is < 1 or > 65535) throw new InvalidDataException("Invalid capture request"); }
}
public static class Wire
{
    public static async Task Write<T>(Stream stream, T value, CancellationToken token)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > 131072) throw new InvalidDataException("IPC message too large");
        await stream.WriteAsync(BitConverter.GetBytes(bytes.Length), token);
        await stream.WriteAsync(bytes, token); await stream.FlushAsync(token);
    }
    public static async Task<T> Read<T>(Stream stream, CancellationToken token)
    {
        byte[] size = new byte[4]; await stream.ReadExactlyAsync(size, token);
        int length = BitConverter.ToInt32(size);
        if (length is < 1 or > 131072) throw new InvalidDataException("Invalid IPC frame length");
        byte[] bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, token);
        return JsonSerializer.Deserialize<T>(bytes) ?? throw new InvalidDataException("Empty IPC message");
    }
}
public static class Daemon
{
    public static bool Elevated => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint id);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint id);
    public static async Task<int> Run(string[] args)
    {
        try
        {
            string Arg(string name) => args.SkipWhile(a => a != name).Skip(1).First();
            if (!Elevated) return 2;
            using var parent = Process.GetProcessById(int.Parse(Arg("--parent")));
            if (parent.StartTime.ToUniversalTime().Ticks != long.Parse(Arg("--parent-start"))) return 3;
            using var lifetime = new CancellationTokenSource();
            _ = parent.WaitForExitAsync().ContinueWith(_ => { try { lifetime.Cancel(); } catch (ObjectDisposedException) { } }, TaskScheduler.Default);
            using var pipe = new NamedPipeClientStream(".", Arg("--pipe"), PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Anonymous);
            await pipe.ConnectAsync(10000, lifetime.Token);
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint peer) || peer != parent.Id) return 4;
            using var sampler = new TelemetrySampler();
            while (!lifetime.IsCancellationRequested)
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(10));
                var request = await Wire.Read<CaptureRequest>(pipe, deadline.Token); request.Validate();
                sampler.Network.OscPort = request.OscPort;
                var sample = request.Paused ? null : sampler.Capture();
                await Wire.Write(pipe, new Reply(1, Environment.ProcessId, true, sample), deadline.Token);
            }
            return 0;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or JsonException or UnauthorizedAccessException) { return 1; }
    }
    public record Reply(int Version, int Pid, bool Elevated, Sample? Sample);
}
public sealed class DaemonClient : IDisposable
{
    private NamedPipeServerStream? pipe;
    private Process? child;
    private readonly CancellationTokenSource lifetime = new();
    private bool disposed;
    public bool Connected => pipe?.IsConnected == true;
    public int? Pid => child?.Id;
    public async Task Start()
    {
        if (Connected) return;
        pipe?.Dispose(); child?.Dispose();
        string name = "VrcNetworkMonitor-" + Guid.NewGuid().ToString("N");
        pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
        using var self = Process.GetCurrentProcess();
        foreach (string arg in new[] { "--daemon", "--pipe", name, "--parent", Environment.ProcessId.ToString(), "--parent-start", self.StartTime.ToUniversalTime().Ticks.ToString() }) start.ArgumentList.Add(arg);
        try
        {
            child = Process.Start(start) ?? throw new IOException("Helper did not start");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); deadline.CancelAfter(15000);
            await pipe.WaitForConnectionAsync(deadline.Token);
            if (!Daemon.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint peer) || peer != child.Id) throw new IOException("Helper identity mismatch");
        }
        catch { pipe.Dispose(); pipe = null; throw; }
    }
    public async Task<Sample?> Capture(int port, bool paused)
    {
        if (pipe is null || !Connected) throw new IOException("Start the elevated helper to collect telemetry");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); deadline.CancelAfter(8000);
        try
        {
            await Wire.Write(pipe, new CaptureRequest(1, port, paused), deadline.Token);
            var reply = await Wire.Read<Daemon.Reply>(pipe, deadline.Token);
            if (reply.Version != 1 || reply.Pid != child?.Id || !reply.Elevated) throw new IOException("Invalid helper reply");
            return reply.Sample;
        }
        catch { pipe.Dispose(); pipe = null; throw; }
    }
    public void Dispose() { if (disposed) return; disposed = true; lifetime.Cancel(); pipe?.Dispose(); child?.Dispose(); lifetime.Dispose(); }
}
