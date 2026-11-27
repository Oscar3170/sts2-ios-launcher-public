using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;

namespace STS2Mobile.DesktopHost.Harness;

internal sealed class ScreenshotService
{
    private readonly SceneTree _tree;
    private readonly string _outputDirectory;
    // PNG compression is the expensive half of a capture. Keep it off the main thread so the next native wait overlaps it,
    // with a small cap: each queued frame is a 1080x2340 RGBA image (~10 MB) until it is encoded.
    private readonly SemaphoreSlim _encodeSlots = new(2);
    private readonly List<Task> _pending = new();
    private readonly List<string> _failures = new();

    public ScreenshotService(SceneTree tree, string outputDirectory)
    {
        _tree = tree;
        _outputDirectory = outputDirectory;
    }

    public async Task<string> CaptureAsync(string name)
    {
        // Let the current frame finish and let the viewport copy the rendered
        // texture before reading it. This is the same mechanism used by
        // CaptureTheSpire.
        await _tree.ToSignal(_tree, SceneTree.SignalName.ProcessFrame);
        await _tree.ToSignal(_tree, SceneTree.SignalName.ProcessFrame);
        Image image = ReadRenderedFrame(name);
        return QueueFrame(name, image);
    }

    public Image ReadRenderedFrame(string name)
    {
        Image? image = _tree.Root.GetViewport().GetTexture()?.GetImage();
        if (image is not null && !image.IsEmpty()) return image;
        image?.Dispose();
        throw new InvalidOperationException($"Viewport screenshot '{name}' was empty.");
    }

    /// <summary>
    /// Saves a frame on the calling thread. Used where the frame is captured and released at a known point
    /// (<c>MenuStartup</c> keeps the images of a tween and disposes them itself); <see cref="CaptureAsync"/> queues the write.
    /// </summary>
    public string SaveFrame(string name, Image image)
    {
        string path = FramePath(name);
        Error error = image.SavePng(path);
        if (error != Error.Ok)
        {
            throw new InvalidOperationException($"Could not save screenshot '{path}': {error}");
        }

        return path;
    }

    /// <summary>
    /// Hands a captured main-thread frame (the GPU readback is done already) to a background PNG encoder. The returned path
    /// is final; the file appears when the write finishes, which <see cref="FlushAsync"/> waits for. The image is owned by
    /// the write from here on and disposed after encoding or on failure.
    /// </summary>
    private string QueueFrame(string name, Image image)
    {
        string path = FramePath(name);
        Task write = Task.Run(async () =>
        {
            await _encodeSlots.WaitAsync().ConfigureAwait(false);
            try
            {
                Error error = image.SavePng(path);
                if (error != Error.Ok)
                {
                    throw new InvalidOperationException($"Could not save screenshot '{path}': {error}");
                }
            }
            finally
            {
                _encodeSlots.Release();
                image.Dispose();
            }
        });
        lock (_pending)
        {
            _pending.Add(write);
        }

        return path;
    }

    private string FramePath(string name)
    {
        Directory.CreateDirectory(_outputDirectory);
        string safeName = string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
        return Path.Combine(_outputDirectory, $"{safeName}.png");
    }

    /// <summary>
    /// Waits for every queued PNG write before RESULTS.json is written and the game quits, then throws if any write failed.
    /// Polls ProcessFrame instead of awaiting the tasks so the continuation stays on the main thread, like the rest of the
    /// harness's waits.
    /// </summary>
    public async Task FlushAsync()
    {
        while (true)
        {
            lock (_pending)
            {
                if (_pending.All(task => task.IsCompleted))
                {
                    foreach (Task task in _pending)
                    {
                        if (task.Exception?.GetBaseException() is { } exception)
                        {
                            _failures.Add(exception.Message);
                        }
                    }

                    _pending.Clear();
                    break;
                }
            }

            await _tree.ToSignal(_tree, SceneTree.SignalName.ProcessFrame);
        }

        if (_failures.Count == 0)
        {
            return;
        }

        string detail = string.Join("; ", _failures.Take(5));
        _failures.Clear();
        throw new InvalidOperationException($"Could not write {detail}");
    }
}
