namespace VrcNetworkMonitor;

public static class StatusRateLimit
{
    public const int MinimumSeconds = 5;
    public static bool IsDue(long lastUtc, long lastUptime, long utc, long uptime, int seconds)
    {
        long required = Math.Max(MinimumSeconds, seconds);
        if (utc - lastUtc < TimeSpan.FromSeconds(required).Ticks) return false;
        // TickCount64 is shared across processes and is not affected by wall-clock adjustments.
        return uptime < lastUptime || uptime - lastUptime >= required * 1000;
    }
    public static bool TrySend(string text, int port, int seconds)
    {
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VrcNetworkMonitor");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "osc-status-cooldown.bin");
        FileStream file;
        try { file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return false; } // Another monitor owns the send gate; skip this poll.
        using (file)
        {
            long now = DateTime.UtcNow.Ticks, uptime = Environment.TickCount64;
            if (file.Length != 0)
            {
                if (file.Length != 16) throw new InvalidDataException("Invalid OSC cooldown state; status sending stopped");
                Span<byte> stored = stackalloc byte[16]; file.ReadExactly(stored);
                if (!IsDue(BitConverter.ToInt64(stored[..8]), BitConverter.ToInt64(stored[8..]), now, uptime, seconds)) return false;
            }
            // Commit the reservation before sending. Failed attempts also consume the cooldown.
            file.Position = 0;
            Span<byte> stamp = stackalloc byte[16];
            BitConverter.TryWriteBytes(stamp[..8], now); BitConverter.TryWriteBytes(stamp[8..], uptime);
            file.Write(stamp); file.Flush(true);
            ChatStatus.Send(text, port);
            // Anchor the next interval to completion rather than the start of the send call.
            file.Position = 0;
            BitConverter.TryWriteBytes(stamp[..8], DateTime.UtcNow.Ticks);
            BitConverter.TryWriteBytes(stamp[8..], Environment.TickCount64);
            file.Write(stamp); file.Flush(true);
            return true;
        }
    }
}
