using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace STS2Mobile.DesktopHost.Harness;

internal sealed class TestStep
{
    public string Name { get; set; } = "";
    public string Status { get; set; } = "failed";
    public string? Detail { get; set; }
    public string? Screenshot { get; set; }
    /// <summary>Wall clock when the step was recorded.</summary>
    public DateTimeOffset At { get; set; }
    /// <summary>Milliseconds since the report was created.</summary>
    public long AtMs { get; set; }
    /// <summary>Milliseconds since the previous step (or the report creation for the first step).</summary>
    public long ElapsedMs { get; set; }
}

/// <summary>
/// A named point on the scenario's timeline (menu ready, preload complete, run started, combat entered, teardown). Phases
/// are separate from <see cref="TestReport.Steps"/> so their timings can never change a scenario's step list.
/// </summary>
internal sealed class TestPhase
{
    public string Name { get; set; } = "";
    public string? Detail { get; set; }
    public DateTimeOffset At { get; set; }
    public long AtMs { get; set; }
    /// <summary>Milliseconds since the previous phase (or the report creation for the first phase).</summary>
    public long ElapsedMs { get; set; }
}

internal sealed class TestReport
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly List<TestStep> _steps = new();
    private readonly List<TestPhase> _phases = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _lastStepMs;
    private long _lastPhaseMs;

    public string Scenario { get; init; } = "";
    public string PointerMode { get; init; } = "";
    public string Seed { get; init; } = "";
    public string? OriginalProfileId { get; set; }
    public string? TestProfileId { get; set; }
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset? FinishedAt { get; set; }
    /// <summary>Scenario wall time from <see cref="StartedAt"/> to the results write, in milliseconds.</summary>
    public long? TotalMs { get; set; }
    public string? Error { get; set; }
    public IReadOnlyList<TestStep> Steps => _steps;
    public IReadOnlyList<TestPhase> Phases => _phases;
    public bool Passed => Error is null && _steps.Count > 0 && _steps.All(step => step.Status != "failed");

    public TestReport(string scenario, string pointerMode, string seed)
    {
        Scenario = scenario;
        PointerMode = pointerMode;
        Seed = seed;
    }

    public void Pass(string name, string? detail = null)
    {
        Add(name, "passed", detail);
    }

    public void Fail(string name, string detail)
    {
        Add(name, "failed", detail);
        Log.Error($"[TEST] FAIL {name}: {detail}");
    }

    public void Skip(string name, string detail)
    {
        Add(name, "skipped", detail);
    }

    public void Check(string name, bool condition, string detail)
    {
        if (condition)
        {
            Pass(name, detail);
        }
        else
        {
            Fail(name, detail);
        }
    }

    public void Screenshot(string name, string path)
    {
        AddStep(new TestStep
        {
            Name = $"screenshot:{name}",
            Status = "passed",
            Screenshot = path,
        });
        Log.Info($"[TEST] screenshot {name}: {path}");
    }

    /// <summary>
    /// Records a timeline marker with its time since the report started. Marks the phases later reports join on, so a
    /// run's cost can be attributed without parsing the log; it never adds a step.
    /// </summary>
    public void Phase(string name, string? detail = null)
    {
        long now = _clock.ElapsedMilliseconds;
        _phases.Add(new TestPhase
        {
            Name = name,
            Detail = detail,
            At = DateTimeOffset.Now,
            AtMs = now,
            ElapsedMs = now - _lastPhaseMs,
        });
        _lastPhaseMs = now;
        Log.Info($"[TEST] phase {name} at {now}ms{(string.IsNullOrEmpty(detail) ? "" : $": {detail}")}");
    }

    private void Add(string name, string status, string? detail)
    {
        AddStep(new TestStep
        {
            Name = name,
            Status = status,
            Detail = detail,
        });

        if (status == "passed")
        {
            Log.Info($"[TEST] PASS {name}{(string.IsNullOrEmpty(detail) ? "" : $": {detail}")}");
        }
        else if (status == "skipped")
        {
            Log.Warn($"[TEST] SKIP {name}: {detail}");
        }
    }

    private void AddStep(TestStep step)
    {
        long now = _clock.ElapsedMilliseconds;
        step.At = DateTimeOffset.Now;
        step.AtMs = now;
        step.ElapsedMs = now - _lastStepMs;
        _lastStepMs = now;
        _steps.Add(step);
    }

    public string Save(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        Phase("results_written");
        FinishedAt = DateTimeOffset.Now;
        TotalMs = _clock.ElapsedMilliseconds;

        string path = Path.Combine(outputDirectory, "RESULTS.json");
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        Log.Info($"[TEST] results written to {path} (passed={Passed})");
        return path;
    }
}
