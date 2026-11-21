using System;
using System.Diagnostics;
using System.Text;
using Godot;

namespace STS2Mobile.GameHost;

/// <summary>
/// What the portrait passes cost per frame, always on. Each pass adds its ticks to the frame, the frame is folded into a
/// one-second aggregate, and that aggregate is printed as a <c>[stats] perf …</c> line (the tag the game's
/// <c>SessionStats</c> and <c>tools/mono/analyze_session.py</c> read). Two timestamps per pass are a monotonic
/// clock read, not a reflection call, so the whole set is far under a tenth of a millisecond even under the interpreter.
/// The Linux harness reads <see cref="LastFrameMs"/> from its own per-frame probe instead of the printed line.
///
/// A pass that only runs on frames whose inputs changed (the hand and rail passes) is reported as the mean and worst of
/// the frames it ran in, with that count: <c>hand=0.31/1.90@7</c> is 0.31 ms over 7 frames, 1.90 ms worst.
///
/// Four buckets nest inside another pass and are reported on their own as well: <c>stage</c> and <c>selection</c> inside
/// <c>combat</c> (the room pass), <c>orbs</c> inside <c>preDraw</c>, and <c>hpText</c> inside <c>labels</c>.
///
/// A pass whose wall time goes over <see cref="SpikeMsec"/> is attributed in the same line: whether a garbage collection ran
/// during it and how many bytes were allocated, so a device run can tell a pass that did real work from one a GC pause landed
/// in (<c>spikes=hand=7.8/gc=1/alloc=41k</c>). The second's own allocation total and GC counts are printed too.
/// </summary>
internal static class PortraitFrameTiming
{
    internal const int PassCount = 14;
    internal const int Combat = 0, Ftue = 1, Stage = 2, Labels = 3, Status = 4, Vfx = 5,
        Hand = 6, Rail = 7, Selection = 8, PreDraw = 9, Hover = 10, Touch = 11, Orbs = 12, HpText = 13;

    /// <summary>The wall time, in milliseconds, above which a single pass is reported with its GC and allocation facts.</summary>
    internal const double SpikeMsec = 5.0;

    /// <summary>The field names of the printed line, indexed by the pass constants above.</summary>
    internal static readonly string[] Names =
    [
        "combat", "ftue", "stage", "labels", "status", "vfx", "hand", "rail", "selection", "preDraw", "hover", "touch",
        "orbs", "hpText",
    ];

    private static readonly double Millisecond = 1000.0 / Stopwatch.Frequency;
    // The frame being measured: a pass that runs twice in one frame (the hand pass in the process and pre-draw passes)
    // accumulates both, and the frame's worst single sample is kept.
    private static readonly long[] FrameTicks = new long[PassCount];
    private static readonly long[] FrameMax = new long[PassCount];
    private static readonly double[] LastFrame = new double[PassCount];
    // The second being measured: per pass, the ticks of the frames it ran in, the worst of those frames, and how many
    // frames it ran in, so a gated pass is not diluted by the frames it skipped.
    private static readonly double[] SecondSum = new double[PassCount];
    private static readonly double[] SecondMax = new double[PassCount];
    private static readonly int[] SecondRuns = new int[PassCount];
    // The second's worst over-budget pass, with the GCs and allocations that happened inside it.
    private static readonly double[] SpikeMs = new double[PassCount];
    private static readonly int[] SpikeGcs = new int[PassCount];
    private static readonly long[] SpikeAllocated = new long[PassCount];
    private static long _lastAllocated;
    private static double _secondAllocatedMb;
    private static int _lastGc0, _lastGc1, _lastGc2;
    private static int _secondGc0, _secondGc1, _secondGc2;
    private static bool _allocationAvailable = true;
    private static bool FrameGcSeen;
    private static ulong _lastFrameIndex;
    private static ulong _lastPrintMsec;
    private static int _secondFrames;
    private static int _cards;
    private static int _cardsPeak;
    private static int _creatures;
    private static bool _drawing;
    private static bool _drewThisSecond;

    /// <summary>
    /// Whether the timings are being collected and printed at all. Off by default (the Graphics settings' "Performance
    /// Log"); the Linux harness turns it on for its runs. While it is off a pass costs one branch: no clock read, no
    /// arithmetic, and no line ever reaches the game log.
    /// </summary>
    internal static bool Enabled { get; private set; }

    internal static void SetEnabled(bool enabled) => Enabled = enabled;

    /// <summary>Times one pass for the rest of the enclosing block; a struct, so nothing is allocated.</summary>
    internal static Scope Begin(int pass) => Enabled
        ? new Scope(pass, Stopwatch.GetTimestamp(), GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2),
            AllocatedBytes())
        : default;

    internal readonly struct Scope : IDisposable
    {
        private readonly int _pass;
        private readonly long _start;
        private readonly long _allocated;
        private readonly int _gc0, _gc1, _gc2;

        internal Scope(int pass, long start, int gc0, int gc1, int gc2, long allocated)
        {
            _pass = pass;
            _start = start;
            _gc0 = gc0;
            _gc1 = gc1;
            _gc2 = gc2;
            _allocated = allocated;
        }

        public void Dispose()
        {
            if (_start == 0) return;
            long ticks = Stopwatch.GetTimestamp() - _start;
            FrameTicks[_pass] += ticks;
            if (ticks > FrameMax[_pass]) FrameMax[_pass] = ticks;
            int collections = GC.CollectionCount(0) - _gc0 + GC.CollectionCount(1) - _gc1 + GC.CollectionCount(2) - _gc2;
            if (collections > 0) FrameGcSeen = true;
            double ms = ticks * Millisecond;
            if (ms < SpikeMsec) return;
            // A GC pause inside the pass is part of its wall time; the counters say whether the pause or the pass's own work
            // made it long. Only the worst over-budget sample of the second is kept per pass.
            if (ms <= SpikeMs[_pass]) return;
            SpikeMs[_pass] = ms;
            SpikeGcs[_pass] = collections;
            SpikeAllocated[_pass] = AllocatedBytes() - _allocated;
        }
    }

    /// <summary>This thread's allocated bytes, or zero where the runtime does not offer the count (the field stays unset).</summary>
    private static long AllocatedBytes()
    {
        if (!_allocationAvailable) return 0;
        try
        {
            return GC.GetAllocatedBytesForCurrentThread();
        }
        catch (Exception)
        {
            _allocationAvailable = false;
            return 0;
        }
    }

    /// <summary>The live hand size, as the hand pass last saw it.</summary>
    internal static void SetCards(int cards) => _cards = cards;

    /// <summary>The creatures on the stage, as the stage pass last placed them.</summary>
    internal static void SetCreatures(int creatures) => _creatures = creatures;

    /// <summary>Whether the turn's draw is still arriving.</summary>
    internal static void SetDrawing(bool drawing) => _drawing = drawing;

    /// <summary>The last completed frame, in milliseconds, indexed by the pass constants; all zero before the first.</summary>
    internal static ReadOnlySpan<double> LastFrameMs() => LastFrame;

    /// <summary>
    /// Whether a garbage collection ran inside any of the last frame's passes. A collection pause is charged to the pass it
    /// lands in (wall clock), so a measurement that wants the passes' own work should leave these frames out: the harness's
    /// budget gate reads this per frame and reports the GC frames separately.
    /// </summary>
    internal static bool LastFrameGcInPass { get; private set; }

    /// <summary>How many collections (all generations) ran during the last frame, wherever they landed.</summary>
    internal static int LastFrameCollections { get; private set; }

    /// <summary>
    /// Ends the frame: the passes' numbers become <see cref="LastFrameMs"/>, are folded into the second, and the second is
    /// printed once it is over. Called from the end of the pre-draw pass, after every other pass of the frame has run; a
    /// second call in the same engine frame is a no-op, so no later flush point can count a frame twice.
    /// </summary>
    internal static void EndFrame()
    {
        if (!Enabled) return;
        ulong frame = Engine.GetProcessFrames();
        if (frame == _lastFrameIndex) return;
        _lastFrameIndex = frame;

        // What the whole main thread allocated (our passes and the game's code alike) and how many collections ran: this is
        // the pressure behind a pass whose wall time a GC pause inflated.
        long allocated = AllocatedBytes();
        int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
        if (_lastAllocated != 0)
        {
            _secondAllocatedMb += (allocated - _lastAllocated) / 1048576.0;
            _secondGc0 += gc0 - _lastGc0;
            _secondGc1 += gc1 - _lastGc1;
            _secondGc2 += gc2 - _lastGc2;
            LastFrameCollections = gc0 - _lastGc0 + gc1 - _lastGc1 + gc2 - _lastGc2;
        }
        _lastAllocated = allocated;
        _lastGc0 = gc0;
        _lastGc1 = gc1;
        _lastGc2 = gc2;
        // The frame's passes have all closed: what they saw is the frame's answer to "was this a GC frame".
        LastFrameGcInPass = FrameGcSeen;
        FrameGcSeen = false;

        bool any = false;
        for (int pass = 0; pass < PassCount; pass++)
        {
            bool ran = FrameTicks[pass] != 0 || FrameMax[pass] != 0;
            LastFrame[pass] = ran ? FrameTicks[pass] * Millisecond : 0;
            if (ran)
            {
                any = true;
                SecondSum[pass] += LastFrame[pass];
                SecondRuns[pass]++;
                double worst = FrameMax[pass] * Millisecond;
                if (worst > SecondMax[pass]) SecondMax[pass] = worst;
            }
            FrameTicks[pass] = 0;
            FrameMax[pass] = 0;
        }
        if (!any) return;
        _secondFrames++;
        if (_cards > _cardsPeak) _cardsPeak = _cards;
        _drewThisSecond |= _drawing;

        ulong now = Time.GetTicksMsec();
        if (_lastPrintMsec == 0) _lastPrintMsec = now;
        if (now - _lastPrintMsec < 1000) return;
        _lastPrintMsec = now;
        GD.Print(SecondLine(now));
        Array.Clear(SecondSum);
        Array.Clear(SecondMax);
        Array.Clear(SecondRuns);
        Array.Clear(SpikeMs);
        Array.Clear(SpikeGcs);
        Array.Clear(SpikeAllocated);
        _secondFrames = 0;
        _cardsPeak = 0;
        _drewThisSecond = false;
        _secondAllocatedMb = 0;
        _secondGc0 = _secondGc1 = _secondGc2 = 0;
    }

    /// <summary>
    /// The second's line: each pass as the mean and worst of the frames it ran in, with that count, then what the frames
    /// were doing — the largest hand seen, the last creature count, whether any frame was mid-draw, what the main thread
    /// allocated and how many collections ran — and, for each pass that went over <see cref="SpikeMsec"/>, its worst
    /// sample with the GCs and bytes allocated inside it.
    /// </summary>
    private static string SecondLine(ulong now)
    {
        var text = new StringBuilder("[stats] perf ms=", 512);
        text.Append(now).Append(" frames=").Append(_secondFrames);
        for (int pass = 0; pass < PassCount; pass++)
        {
            int runs = SecondRuns[pass];
            if (runs == 0) continue;
            text.Append(' ').Append(Names[pass]).Append('=')
                .Append((SecondSum[pass] / runs).ToString("F2"))
                .Append('/').Append(SecondMax[pass].ToString("F2"))
                .Append('@').Append(runs);
        }
        text.Append(" cards=").Append(_cardsPeak)
            .Append(" creatures=").Append(_creatures)
            .Append(" drawing=").Append(_drewThisSecond ? '1' : '0')
            .Append(" alloc_mb=").Append(_secondAllocatedMb.ToString("F1"))
            .Append(" gc0=").Append(_secondGc0)
            .Append(" gc1=").Append(_secondGc1)
            .Append(" gc2=").Append(_secondGc2);
        var spikes = new StringBuilder(96);
        for (int pass = 0; pass < PassCount; pass++)
        {
            if (SpikeMs[pass] < SpikeMsec) continue;
            if (spikes.Length > 0) spikes.Append(',');
            spikes.Append(Names[pass]).Append('=').Append(SpikeMs[pass].ToString("F1"))
                .Append("/gc=").Append(SpikeGcs[pass])
                .Append("/alloc=").Append(SpikeAllocated[pass] / 1024).Append('k');
        }
        if (spikes.Length > 0) text.Append(" spikes=").Append(spikes);
        return text.ToString();
    }
}
