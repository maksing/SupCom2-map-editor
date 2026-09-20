using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

/// <summary>Cheap per-section frame timing: <c>using (SC2FramePerf.Measure("name")) { ... }</c> adds the
/// time to that section, and once per second any section that took a noticeable share of it is
/// logged as "name=ms/s (calls)". Lets a slow frame be attributed to a part of the editor from a log
/// alone; costs two timestamp reads per measured section.</summary>
public static class SC2FramePerf
{
    /// <summary>Sections totalling less than this per second are not logged.</summary>
    private const double LogThresholdMsPerSecond = 15.0;
    private const double WindowSeconds = 1.0;

    private static readonly Dictionary<string, (long Ticks, int Calls)> Totals = new();
    private static long _windowStart = Stopwatch.GetTimestamp();

    public readonly struct Scope : IDisposable
    {
        private readonly string _name;
        private readonly long _start;
        public Scope(string name) { _name = name; _start = Stopwatch.GetTimestamp(); }
        public void Dispose() => Add(_name, Stopwatch.GetTimestamp() - _start, 1);
    }

    public static Scope Measure(string name) => new Scope(name);

    /// <summary>For a section timed by hand (a loop where a Scope per item would cost more than
    /// the item): the ticks from Stopwatch.GetTimestamp differences and how many items there were.</summary>
    public static void AddTicks(string name, long ticks, int calls)
    {
        Add(name, ticks, calls);
    }

    private static void Add(string name, long ticks, int calls)
    {
        Totals.TryGetValue(name, out var t);
        Totals[name] = (t.Ticks + ticks, t.Calls + calls);

        var now = Stopwatch.GetTimestamp();
        var elapsed = (now - _windowStart) / (double)Stopwatch.Frequency;
        if (elapsed < WindowSeconds) return;

        StringBuilder sb = null;
        foreach (var kv in Totals)
        {
            var ms = kv.Value.Ticks * 1000.0 / Stopwatch.Frequency / elapsed;
            if (ms < LogThresholdMsPerSecond) continue;
            sb ??= new StringBuilder("[SC2FramePerf]");
            sb.Append(' ').Append(kv.Key).Append('=').Append(ms.ToString("0")).Append("ms/s(").Append(kv.Value.Calls).Append(')');
        }
        if (sb != null) SC2Log.Log(sb.ToString());
        Totals.Clear();
        _windowStart = now;
    }
}
