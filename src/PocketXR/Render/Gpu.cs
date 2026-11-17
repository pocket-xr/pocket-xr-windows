using System;
using PocketXR.Core;
using PocketXR.Platform;
using SharpGen.Runtime;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace PocketXR.Render;

/// <summary>
/// One Direct3D 11 device and its immediate context. The renderer and the glasses' window share one; each capture
/// has one of its own.
///
/// The device may be used from any thread; its immediate context may not, so every run of context calls on a shared
/// device is made inside <see cref="Hold"/>. Each holder sets all the state it needs and leaves none behind
/// (ClearState). Nothing that waits (Desktop Duplication's wait for the next picture does) may run on the renderer's
/// device: it holds the context while it waits.
/// </summary>
public sealed class GpuDevice : IDisposable
{
    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    /// <summary>The graphics card's id, as in <see cref="DisplayInfo.AdapterLuid"/> for displays plugged into it.</summary>
    public long AdapterLuid { get; }
    public string AdapterName { get; }
    private readonly ID3D11Multithread multithread;

    private GpuDevice(IDXGIAdapter1 adapter)
    {
        AdapterDescription1 about = adapter.Description1;
        AdapterLuid = Outputs.Luid(adapter);
        AdapterName = about.Description;
        var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 };
        D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport, levels,
            out ID3D11Device device, out ID3D11DeviceContext context).CheckError();
        Device = device;
        Context = context;
        multithread = context.QueryInterface<ID3D11Multithread>();
        multithread.SetMultithreadProtected(true);
    }

    /// <summary>
    /// A device on the graphics card with that id, or on the first real one when [adapterLuid] is null or not found.
    /// Throws when the PC has no Direct3D 11 graphics card.
    /// </summary>
    public static GpuDevice Create(long? adapterLuid = null)
    {
        using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        IDXGIAdapter1? chosen = null;
        for (uint i = 0; factory.EnumAdapters1(i, out IDXGIAdapter1? adapter).Success && adapter != null; i++)
        {
            bool software = (adapter.Description1.Flags & AdapterFlags.Software) != 0;
            bool wanted = adapterLuid is long luid && Outputs.Luid(adapter) == luid;
            if (wanted || (chosen == null && !software))
            {
                chosen?.Dispose();
                chosen = adapter;
                if (wanted) break;
            }
            else
            {
                adapter.Dispose();
            }
        }
        if (chosen == null) throw new InvalidOperationException("This PC has no Direct3D 11 graphics card.");
        using (chosen) return new GpuDevice(chosen);
    }

    /// <summary>
    /// A device on the graphics card that draws [display] (found by the display's Windows name, so a display made by
    /// a virtual display driver gives the card that renders it), else on the first card.
    /// </summary>
    public static GpuDevice ForDisplay(DisplayInfo display)
    {
        if (Outputs.Find(display.GdiName, out IDXGIAdapter1? adapter, out IDXGIOutput? output))
        {
            using (adapter)
            using (output)
            {
                return new GpuDevice(adapter!);
            }
        }
        return Create();
    }

    /// <summary>False once the graphics card was reset or taken away; everything made on the device is then dead.</summary>
    public bool Alive => Device.DeviceRemovedReason.Success;

    /// <summary>Holds the immediate context for this thread until the returned value is disposed.</summary>
    public Held Hold()
    {
        multithread.Enter();
        return new Held(multithread);
    }

    public readonly struct Held : IDisposable
    {
        private readonly ID3D11Multithread owner;
        internal Held(ID3D11Multithread owner) { this.owner = owner; }
        public void Dispose() => owner.Leave();
    }

    public void Dispose()
    {
        multithread.Dispose();
        Context.Dispose();
        Device.Dispose();
    }

    /// <summary>Compiles one HLSL function; throws with the compiler's own words when it does not compile.</summary>
    internal static Blob Compile(string source, string entry, string profile, string name)
    {
        Result result = Compiler.Compile(source, entry, name, profile, out Blob blob, out Blob errors);
        if (result.Failure || blob == null)
        {
            string why = errors?.AsString() ?? result.ToString();
            errors?.Dispose();
            Log.P($"renderer: shaders failed: {why}");
            throw new InvalidOperationException($"The shader {entry} did not compile: {why}");
        }
        errors?.Dispose();
        return blob!;
    }
}

/// <summary>Windows' list of graphics cards and the displays on each (DXGI).</summary>
internal static class Outputs
{
    /// <summary>
    /// The graphics card and output whose Windows name is [gdiName] (such as \\.\DISPLAY2). A display made by an
    /// indirect display driver is listed under the card that renders it, so the name is what to match, not the
    /// display's own adapter id. The caller disposes both.
    /// </summary>
    internal static bool Find(string gdiName, out IDXGIAdapter1? adapter, out IDXGIOutput? output)
    {
        adapter = null;
        output = null;
        using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint a = 0; factory.EnumAdapters1(a, out IDXGIAdapter1? candidate).Success && candidate != null; a++)
        {
            for (uint o = 0; candidate.EnumOutputs(o, out IDXGIOutput? found).Success && found != null; o++)
            {
                if (string.Equals(found.Description.DeviceName, gdiName, StringComparison.OrdinalIgnoreCase))
                {
                    adapter = candidate;
                    output = found;
                    return true;
                }
                found.Dispose();
            }
            candidate.Dispose();
        }
        return false;
    }

    internal static long Luid(IDXGIAdapter1 adapter) => AsLong(adapter.Description1.Luid);

    private static unsafe long AsLong<T>(T value) where T : unmanaged =>
        sizeof(T) == sizeof(long) ? *(long*)&value : throw new InvalidOperationException("LUID is not 8 bytes");
}
