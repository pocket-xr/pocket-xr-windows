using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using PocketXR.Core;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace PocketXR.Render;

/// <summary>
/// Draws the Space on the glasses: each screen a textured panel (flat or bent round the head), seen from the head's
/// pose, once per eye in 3D side by side (each half of a 3840x1080 picture), else once. Corners are rounded and the
/// focused screen carries a rim of light. Then Pocket XR's edge fade, so panels end in black (the glasses show black
/// as see-through) rather than at a hard line.
/// </summary>
public sealed class Renderer : IDisposable
{
    public GpuDevice Gpu { get; }
    private readonly ID3D11VertexShader panelVertex;
    private readonly ID3D11PixelShader panelFragment;
    private readonly ID3D11InputLayout panelLayout;
    private readonly ID3D11VertexShader fadeVertex;
    private readonly ID3D11PixelShader fadeFragment;
    private readonly ID3D11Buffer panelUniforms;
    private readonly ID3D11Buffer fadeUniforms;
    private readonly ID3D11BlendState blend;
    private readonly ID3D11SamplerState sampler;
    private readonly ID3D11RasterizerState raster;
    /// <summary>Each placement's mesh, made once and kept until the placement changes.</summary>
    private readonly Dictionary<PanelPlacement, Mesh> meshes = new();

    private readonly record struct Mesh(ID3D11Buffer Buffer, int Count);

    [StructLayout(LayoutKind.Sequential)]
    private struct PanelUniforms
    {
        public Mat4 Mvp;
        public Vec4f Tint; // shown where there is no picture yet
        public Vec4f Glow; // the rim's colour, alpha its strength
        public Vec2f Size; // the panel's size in its own pixels, for the corners and the rim
        public float HasTexture;
        public float Brightness;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FadeUniforms
    {
        public Vec2f Size; // the eye's viewport, pixels
        public Vec2f Pad;
        public Vec4f Edges; // fade widths, pixels: left, right, top, bottom
    }

    /// <summary>
    /// How each panel's picture is turned, by panel: DXGI's rotation (1 upright, 2, 3 and 4 quarter turns). A screen
    /// whose panel is mounted turned (the Steam Deck's) is captured as the panel is wired; the picture is turned back
    /// when it is drawn. Anything else, or a missing entry, is upright.
    /// </summary>
    public int[] Turns = Array.Empty<int>();

    /// <summary>Makes the pipelines on [gpu]. Throws when the shaders do not compile or the device is gone.</summary>
    public Renderer(GpuDevice gpu)
    {
        Gpu = gpu;
        ID3D11Device device = gpu.Device;
        using (Blob vs = GpuDevice.Compile(ShaderSource, "panelVertex", "vs_4_0", "panel"))
        using (Blob ps = GpuDevice.Compile(ShaderSource, "panelFragment", "ps_4_0", "panel"))
        {
            panelVertex = device.CreateVertexShader(vs);
            panelFragment = device.CreatePixelShader(ps);
            panelLayout = device.CreateInputLayout(new[]
            {
                new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
                new InputElementDescription("TEXCOORD", 0, Format.R32G32_Float, 12, 0),
            }, vs);
        }
        using (Blob vs = GpuDevice.Compile(ShaderSource, "fadeVertex", "vs_4_0", "fade"))
        using (Blob ps = GpuDevice.Compile(ShaderSource, "fadeFragment", "ps_4_0", "fade"))
        {
            fadeVertex = device.CreateVertexShader(vs);
            fadeFragment = device.CreatePixelShader(ps);
        }
        panelUniforms = device.CreateBuffer((uint)Marshal.SizeOf<PanelUniforms>(), BindFlags.ConstantBuffer);
        fadeUniforms = device.CreateBuffer((uint)Marshal.SizeOf<FadeUniforms>(), BindFlags.ConstantBuffer);

        // Corners are cut with alpha, over the black behind.
        var b = new BlendDescription();
        b.RenderTarget[0] = new RenderTargetBlendDescription
        {
            BlendEnable = true,
            SourceBlend = Blend.SourceAlpha,
            DestinationBlend = Blend.InverseSourceAlpha,
            BlendOperation = BlendOperation.Add,
            SourceBlendAlpha = Blend.One,
            DestinationBlendAlpha = Blend.InverseSourceAlpha,
            BlendOperationAlpha = BlendOperation.Add,
            RenderTargetWriteMask = ColorWriteEnable.All,
        };
        blend = device.CreateBlendState(b);
        sampler = device.CreateSamplerState(new SamplerDescription(
            Filter.MinMagLinearMipPoint, TextureAddressMode.Clamp, 0f, 1, ComparisonFunction.Never, 0f, float.MaxValue));
        raster = device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid));
    }

    /// <summary>The placement's mesh as a buffer of x, y, z, u, v per vertex (a triangle strip in head-centred space).</summary>
    private Mesh? MeshFor(PanelPlacement placement)
    {
        if (meshes.TryGetValue(placement, out Mesh m)) return m;
        // A change of layout leaves the old meshes unused; they are few, but not kept for ever.
        if (meshes.Count > 24) ForgetMeshes();
        PanelVertex[] vertices = placement.Mesh(columns: placement.Curve > 0 ? 64 : 1);
        if (vertices.Length == 0) return null;
        var floats = new float[vertices.Length * 5];
        for (int i = 0; i < vertices.Length; i++)
        {
            PanelVertex v = vertices[i];
            floats[i * 5] = v.Position.X;
            floats[i * 5 + 1] = v.Position.Y;
            floats[i * 5 + 2] = v.Position.Z;
            floats[i * 5 + 3] = v.Uv.X;
            floats[i * 5 + 4] = v.Uv.Y;
        }
        ID3D11Buffer buffer = Gpu.Device.CreateBuffer(floats, BindFlags.VertexBuffer, ResourceUsage.Immutable);
        m = new Mesh(buffer, vertices.Length);
        meshes[placement] = m;
        return m;
    }

    private void ForgetMeshes()
    {
        foreach (Mesh m in meshes.Values) m.Buffer.Dispose();
        meshes.Clear();
    }

    /// <summary>
    /// One frame into [target] ([width] by [height] pixels): [panels] seen from [head]. It clears the target to
    /// black, draws, and leaves the device context with no state set; showing the frame is the caller's part.
    /// </summary>
    public void Draw(ID3D11RenderTargetView target, int width, int height, Quat head,
        IReadOnlyList<(PanelPlacement Placement, ID3D11ShaderResourceView? Texture)> panels, RenderOptions options)
    {
        int w = width, h = height;
        if (w <= 0 || h <= 0) return;
        bool sbs = !options.Mono && GlassesOptics.IsSideBySide(width: w, height: h);
        using GpuDevice.Held held = Gpu.Hold();
        ID3D11DeviceContext ctx = Gpu.Context;
        ctx.ClearRenderTargetView(target, new Color4(0f, 0f, 0f, 1f));
        ctx.OMSetRenderTargets(target);
        ctx.OMSetBlendState(blend);
        ctx.RSSetState(raster);
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        ctx.PSSetSampler(0, sampler);
        var fade = options.EdgeFade.Widths();
        // Each eye: its offset in metres (right positive), its viewport x and width, and which side is the outer one.
        (double EyeX, int Vx, int Vw, bool LeftOuter, bool RightOuter)[] eyes = sbs
            ? new[] { (-options.Ipd / 2, 0, w / 2, true, false), (options.Ipd / 2, w / 2, w / 2, false, true) }
            : new[] { (0.0, 0, w, true, true) };
        foreach (var (eyeX, vx, vw, leftOuter, rightOuter) in eyes)
        {
            ctx.RSSetViewport(vx, 0, vw, h, 0f, 1f);
            Mat4 proj = SpaceMath.Perspective(fovYDeg: options.FovYDeg, aspect: (double)vw / (double)h);
            Mat4 view = SpaceMath.View(head: head, eyeX: eyeX);
            Mat4 vp = proj * view;
            ctx.IASetInputLayout(panelLayout);
            ctx.VSSetShader(panelVertex);
            ctx.PSSetShader(panelFragment);
            ctx.VSSetConstantBuffer(1, panelUniforms);
            ctx.PSSetConstantBuffer(1, panelUniforms);
            for (int index = 0; index < panels.Count; index++)
            {
                (PanelPlacement placement, ID3D11ShaderResourceView? texture) = panels[index];
                if (MeshFor(placement) is not Mesh mesh) continue;
                bool lit = options.Focused == index && options.Glow > 0 && panels.Count > 1;
                LookColor g = options.GlowColor;
                var u = new PanelUniforms
                {
                    Mvp = vp,
                    Tint = new Vec4f(0.12f, 0.14f, 0.19f, 1), // Pocket XR's raised glass, a placeholder until the first frame
                    Glow = new Vec4f((float)g.R, (float)g.G, (float)g.B, lit ? (float)options.Glow : 0),
                    // The panel's own pixels: 1080 high, so the corner and the rim are the same size on every screen.
                    Size = new Vec2f((float)(1080 * placement.Aspect), 1080),
                    // 0 no picture yet; else how the captured screen is turned (DXGI's 1 upright, 2 a quarter turn, 3 half, 4 three quarters).
                    HasTexture = texture == null ? 0 : index < Turns.Length && Turns[index] is >= 2 and <= 4 ? Turns[index] : 1,
                    Brightness = (float)options.Brightness,
                };
                ctx.UpdateSubresource(in u, panelUniforms);
                ctx.IASetVertexBuffer(0, mesh.Buffer, 20, 0);
                ctx.PSSetShaderResource(0, texture!);
                ctx.Draw((uint)mesh.Count, 0);
            }
            if (options.EdgeFade != EdgeFade.Off)
            {
                // The widths are for a 1920 wide eye.
                float k = (float)vw / 1920;
                float l = (float)(leftOuter ? fade.Outer : fade.Inner) * k;
                float r = (float)(rightOuter ? fade.Outer : fade.Inner) * k;
                float v = (float)fade.Vertical * k;
                var f = new FadeUniforms { Size = new Vec2f(vw, h), Edges = new Vec4f(l, r, v, v) };
                ctx.UpdateSubresource(in f, fadeUniforms);
                ctx.IASetInputLayout(null!);
                ctx.VSSetShader(fadeVertex);
                ctx.PSSetShader(fadeFragment);
                ctx.PSSetConstantBuffer(0, fadeUniforms);
                ctx.Draw(4, 0);
            }
        }
        // Nothing stays bound: a capture may write its texture next, and the window may resize its buffers.
        ctx.ClearState();
    }

    public void Dispose()
    {
        ForgetMeshes();
        raster.Dispose();
        sampler.Dispose();
        blend.Dispose();
        fadeUniforms.Dispose();
        panelUniforms.Dispose();
        fadeFragment.Dispose();
        fadeVertex.Dispose();
        panelLayout.Dispose();
        panelFragment.Dispose();
        panelVertex.Dispose();
    }

    /// <summary>
    /// The Mac's Metal shaders in HLSL, one for one. Direct3D's clip space (z 0 to 1, y up) and texture origin (top
    /// left) are Metal's, and a cbuffer's float4x4 is column-major as Mat4 is, so the matrices and uv are unchanged.
    /// </summary>
    public const string ShaderSource = @"
struct PanelVertexIn { float3 position : POSITION; float2 uv : TEXCOORD0; };
cbuffer PanelUniforms : register(b1) {
    float4x4 mvp; float4 tint; float4 glow; float2 size; float hasTexture; float brightness;
};
struct PanelOut { float4 position : SV_Position; float2 uv : TEXCOORD0; };
Texture2D tex : register(t0);
SamplerState s : register(s0);

// The mesh is already in head-centred space; the picture's top left is uv (0, 0).
PanelOut panelVertex(PanelVertexIn v) {
    PanelOut o;
    o.position = mul(mvp, float4(v.position, 1.0));
    o.uv = v.uv;
    return o;
}

// Distance in the panel's pixels from the inside of a rounded rectangle's edge: negative inside.
static float roundedBox(float2 p, float2 halfSize, float radius) {
    float2 q = abs(p) - halfSize + radius;
    return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - radius;
}

float4 panelFragment(PanelOut i) : SV_Target {
    float2 p = (i.uv - 0.5) * size;
    float d = roundedBox(p, size * 0.5, 22.0);
    // Smooth over about a pixel and a half of the panel, whatever its size on the display.
    float edge = max(fwidth(d), 0.75);
    float alpha = 1.0 - smoothstep(-edge, edge, d);
    // A screen captured as its panel is wired is turned back here (DXGI_MODE_ROTATION: 2 a quarter turn, 3 half, 4
    // three quarters): the picture's own uv for the point of the screen as it stands.
    float2 t = i.uv;
    if (hasTexture > 1.5 && hasTexture < 2.5) t = float2(i.uv.y, 1.0 - i.uv.x);
    else if (hasTexture > 2.5 && hasTexture < 3.5) t = 1.0 - i.uv;
    else if (hasTexture > 3.5) t = float2(1.0 - i.uv.y, i.uv.x);
    float3 colour = hasTexture < 0.5 ? tint.rgb : tex.Sample(s, t).rgb * brightness;
    // The focused screen: a thin rim of light at the edge, and a soft fall of it inward.
    float inside = max(-d, 0.0);
    float rim = (1.0 - smoothstep(2.0, 5.0, inside)) * 0.9 + exp(-inside / 26.0) * 0.28;
    colour = lerp(colour, glow.rgb, saturate(rim * glow.a));
    return float4(colour, alpha);
}

cbuffer FadeUniforms : register(b0) { float2 fadeSize; float2 fadePad; float4 fadeEdges; };
struct FadeOut { float4 position : SV_Position; float2 uv : TEXCOORD0; };

FadeOut fadeVertex(uint vid : SV_VertexID) {
    float2 corners[4] = { float2(-1, -1), float2(1, -1), float2(-1, 1), float2(1, 1) };
    FadeOut o;
    o.position = float4(corners[vid], 0.0, 1.0);
    o.uv = float2((corners[vid].x + 1) * 0.5, (1 - corners[vid].y) * 0.5);
    return o;
}

// Black, more opaque toward each edge of the eye's picture (Pocket XR's edge fade).
float4 fadeFragment(FadeOut i) : SV_Target {
    float2 p = i.uv * fadeSize;
    float a = 0.0;
    a = max(a, 1.0 - saturate(p.x / max(fadeEdges.x, 0.001)));
    a = max(a, 1.0 - saturate((fadeSize.x - p.x) / max(fadeEdges.y, 0.001)));
    a = max(a, 1.0 - saturate(p.y / max(fadeEdges.z, 0.001)));
    a = max(a, 1.0 - saturate((fadeSize.y - p.y) / max(fadeEdges.w, 0.001)));
    return float4(0.0, 0.0, 0.0, a);
}
";
}
