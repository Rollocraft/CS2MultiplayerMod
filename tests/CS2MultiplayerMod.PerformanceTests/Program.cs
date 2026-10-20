using System.Diagnostics;
using System.Text;
using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Sync;

static class Program
{
    static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    static void Main(string[] args)
    {
        CheckWireBytes();
        CheckReplayWindow();
        OccupancyContentTests.Run(Assert);
        bool baseline = args.Contains("--baseline");
        MeasureWrites(baseline);
        MeasurePruning(baseline);
        Console.WriteLine("PASS: wire compatibility, replay expiry and performance checks.");
    }

    static void CheckWireBytes()
    {
        var writer = new NetworkWriter(4);
        var expected = new List<byte>();
        // Include signed zero, infinities, subnormals, and distinct NaN payloads.
        int[] bits = { 0, int.MinValue, 1, -1, 0x7f800000, unchecked((int)0xff800000),
            0x7fc00001, 0x7fa00001, 0x3f800000 };
        var random = new Random(721);
        foreach (int value in bits.Concat(Enumerable.Range(0, 10000).Select(_ => random.Next(int.MinValue, int.MaxValue))))
        {
            float valueAsFloat = BitConverter.Int32BitsToSingle(value);
            writer.WriteFloat(valueAsFloat);
            expected.AddRange(BitConverter.GetBytes(valueAsFloat));
        }
        string[] strings = { null, "", "hello", "München 東京 🚋", "\ud800", "a\udc00b",
            new string('é', 10000) };
        foreach (string value in strings)
        {
            writer.WriteString(value);
            byte[] utf8 = value == null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(value);
            expected.AddRange(BitConverter.GetBytes(value == null ? -1 : utf8.Length));
            expected.AddRange(utf8);
        }
        Assert(writer.ToArray().SequenceEqual(expected), "Writer changed the wire bytes");
    }

    static void CheckReplayWindow()
    {
        var window = new OperationReplayWindow<string>(StringComparer.OrdinalIgnoreCase);
        var expected = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var random = new Random(913);
        long now = 0;
        for (int i = 0; i < 50000; i++)
        {
            // Nonmonotonic clocks, variable durations, and replacement of existing keys.
            now += random.Next(-3, 7);
            string key = (random.Next(2) == 0 ? "key" : "KEY") + random.Next(100);
            switch (random.Next(20))
            {
                case 0:
                    window.Clear();
                    expected.Clear();
                    break;
                case 1:
                case 2:
                case 3:
                    bool found = expected.TryGetValue(key, out long expiry) && expiry > now;
                    if (!found) expected.Remove(key);
                    Assert(window.Contains(key, now) == found, "Contains disagreed with expiry model");
                    break;
                default:
                    if (random.Next(2) == 0)
                    {
                        long duration = random.Next(1, 100);
                        window.Remember(key, now, duration);
                        expected[key] = now + duration;
                    }
                    else
                    {
                        window.Prune(now);
                        foreach (string expired in expected.Where(p => p.Value <= now).Select(p => p.Key).ToArray())
                            expected.Remove(expired);
                    }
                    break;
            }
            Assert(window.Count == expected.Count, "Replay count disagreed with expiry model");
        }

        window.Clear();
        window.Remember("max", long.MaxValue - 5, 10);
        window.Prune(long.MaxValue - 1);
        Assert(window.Count == 1, "Overflow shortened replay retention");
        window.Prune(long.MaxValue);
        Assert(window.Count == 0, "Replay did not expire at long.MaxValue");
        window.Remember("renew", 0, 10);
        window.Remember("RENEW", 0, 20);
        window.Prune(10);
        Assert(window.Count == 1, "An old deadline removed a renewed key");
        window.Prune(20);
        Assert(window.Count == 0, "Renewed key did not expire");
    }

    static void MeasureWrites(bool baseline)
    {
        const int count = 100000;
        const string sample = "München 東京 🚋";
        // Preallocate to isolate temporary allocations from required output storage.
        var writer = new NetworkWriter(count * 64 + 4096);
        for (int i = 0; i < 100; i++) { writer.WriteFloat(i); writer.WriteString(sample); }
        long before = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        for (int i = 0; i < count; i++) { writer.WriteFloat(i); writer.WriteString(sample); }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        Console.WriteLine($"100,000 float/string writes: {allocated:N0} temporary bytes, {ms:F2} ms");
        if (!baseline) Assert(allocated == 0, "Primitive/string writes allocated temporary storage");
        GC.KeepAlive(writer);
    }

    static void MeasurePruning(bool baseline)
    {
        var window = new OperationReplayWindow<int>();
        for (int i = 0; i < 10000; i++) window.Remember(i, 0, 60000);
        for (int i = 0; i < 100; i++) window.Prune(1);
        long before = GC.GetAllocatedBytesForCurrentThread();
        long start = Stopwatch.GetTimestamp();
        for (int i = 0; i < 10000; i++) window.Prune(i);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        Console.WriteLine($"10,000 unexpired-cache prunes (10,000 keys): {allocated:N0} temporary bytes, {ms:F2} ms");
        Assert(window.Count == 10000, "Pruning removed live keys");
        if (!baseline) Assert(allocated == 0, "Idle pruning allocated temporary storage");
        window.Prune(60000);
        Assert(window.Count == 0, "Bulk expiry did not clear the cache");
    }
}
