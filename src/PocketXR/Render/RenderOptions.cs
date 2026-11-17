using PocketXR.Core;

namespace PocketXR.Render;

/// <summary>How one frame is to look, from the settings.</summary>
public sealed record RenderOptions
{
    /// <summary>Each eye's vertical field of view, degrees.</summary>
    public double FovYDeg { get; init; } = 38.0;
    /// <summary>The eyes' distance apart, metres.</summary>
    public double Ipd { get; init; } = 0.063;
    /// <summary>The screens' brightness, 0.3 to 1.</summary>
    public double Brightness { get; init; } = 1.0;
    /// <summary>The focused screen's outline, 0 off to 1, and its colour.</summary>
    public double Glow { get; init; } = 1.0;
    public LookColor GlowColor { get; init; } = Look.Accent;
    /// <summary>Which screen is focused (the one the pointer is on), null for none.</summary>
    public int? Focused { get; init; }
    public EdgeFade EdgeFade { get; init; } = EdgeFade.Soft;
    /// <summary>
    /// One picture across the whole display even when it is 3840x1080: Steady mode, where the glasses show that
    /// picture as one wide flat canvas (Ultrawide), not as a left and a right eye.
    /// </summary>
    public bool Mono { get; init; }
}
