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
/// </summary>
internal static class PortraitFrameTiming
{
    internal const int PassCount = 14;
    internal const int Combat = 0, Ftue = 1, Stage = 2, Labels = 3, Status = 4, Vfx = 5,
        Hand = 6, Rail = 7, Selection = 8, PreDraw = 9, Hover = 10, Touch = 11, Orbs = 12, HpText = 13;

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
    internal static Scope Begin(int pass) => Enabled ? new Scope(pass, Stopwatch.GetTimestamp()) : default;

    internal readonly struct Scope : IDisposable
    {
        private readonly int _pass;
        private readonly long _start;

        internal Scope(int pass, long start)
        {
            _pass = pass;
            _start = start;
        }

        public void Dispose()
        {
            if (_start == 0) return;
            long ticks = Stopwatch.GetTimestamp() - _start;
            FrameTicks[_pass] += ticks;
            if (ticks > FrameMax[_pass]) FrameMax[_pass] = ticks;
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
        _secondFrames = 0;
        _cardsPeak = 0;
        _drewThisSecond = false;
    }

    /// <summary>
    /// The second's line: each pass as the mean and worst of the frames it ran in, with that count, then what the frames
    /// were doing — the largest hand seen, the last creature count, and whether any frame was mid-draw.
    /// </summary>
    private static string SecondLine(ulong now)
    {
        var text = new StringBuilder("[stats] perf ms=", 384);
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
        return text.Append(" cards=").Append(_cardsPeak)
            .Append(" creatures=").Append(_creatures)
            .Append(" drawing=").Append(_drewThisSecond ? '1' : '0').ToString();
    }
}
