using System;
using System.Collections.Generic;
using System.Linq;
using PocketXR.Core;
using PocketXR.Platform;
using Vortice.Direct3D11;

namespace PocketXR.Render;

/// <summary>
/// Everything that puts the Space on the glasses, as one thing the app can start and stop: the graphics device, the
/// renderer, one capture for each floating screen, and the window over the glasses' display with its drawing thread.
/// </summary>
public sealed class GlassesPicture : IDisposable
{
    private readonly GpuDevice gpu;
    private readonly Renderer renderer;
    private readonly List<DisplayCapture> captures = new();
    private readonly GlassesWindow window;
    private readonly Func<(Quat Head, PanelPlacement[] Panels, RenderOptions Options)> frame;
    private bool disposed;

    /// <summary>The glasses' display or the graphics card went away; the app looks at the displays again.</summary>
    public event Action? Lost;

    /// <summary>Frames drawn on the glasses a second.</summary>
    public double Fps => window.Fps;

    /// <summary>Screens captured and showing a picture.</summary>
    public int Capturing => captures.Count(c => c.Texture != null);

    /// <summary>One line on each capture, for the log and the Glasses report.</summary>
    public string Describe() => string.Join("; ", captures.Select((c, i) =>
        $"screen {i + 1}: {c.Size.Width}x{c.Size.Height}, {c.Fps:0} pictures a second{(c.CopiesThroughMemory ? ", copied through memory" : "")}{(c.Error != null ? $", {c.Error}" : "")}"));

    /// <summary>
    /// Covers [glasses] and draws [sources] (the floating screens, in order) as panels. [frame] is called on the
    /// drawing thread just before each frame for the head's pose, where each panel is, and how to draw.
    /// </summary>
    public GlassesPicture(DisplayInfo glasses, IReadOnlyList<DisplayInfo> sources,
        Func<(Quat Head, PanelPlacement[] Panels, RenderOptions Options)> frame)
    {
        this.frame = frame;
        gpu = GpuDevice.ForDisplay(glasses);
        renderer = new Renderer(gpu);
        try
        {
            foreach (DisplayInfo source in sources)
            {
                var capture = new DisplayCapture(gpu, source);
                capture.Start();
                captures.Add(capture);
            }
            window = new GlassesWindow(glasses, renderer, Frame);
            window.Lost += why =>
            {
                Log.P($"glasses picture: lost: {why}");
                Lost?.Invoke();
            };
        }
        catch
        {
            foreach (DisplayCapture c in captures) c.Dispose();
            renderer.Dispose();
            gpu.Dispose();
            throw;
        }
        Log.P($"glasses picture: drawing on {glasses.GdiName} with {gpu.AdapterName}, {captures.Count} screens captured");
    }

    private FrameInput Frame()
    {
        (Quat head, PanelPlacement[] panels, RenderOptions options) = frame();
        var paired = new (PanelPlacement Placement, ID3D11ShaderResourceView? Texture)[panels.Length];
        var turns = new int[panels.Length];
        for (int i = 0; i < panels.Length && i < captures.Count; i++) turns[i] = captures[i].Rotation;
        renderer.Turns = turns;
        for (int i = 0; i < panels.Length; i++)
            paired[i] = (panels[i], i < captures.Count ? captures[i].Texture : null);
        return new FrameInput(head, paired, options);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        window.Dispose();
        foreach (DisplayCapture c in captures) c.Dispose();
        renderer.Dispose();
        gpu.Dispose();
    }
}
