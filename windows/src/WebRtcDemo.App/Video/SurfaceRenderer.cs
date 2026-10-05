using System.Runtime.InteropServices;
using Microsoft.Graphics.DirectX;
using Microsoft.UI.Composition;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.Mathematics;
using Windows.Graphics;
using WinRT;

namespace WebRtcDemo.App.Video;

/// <summary>
/// Shows BGRA frames on a composition sprite: each frame is uploaded into a drawing surface the
/// size of the frame, which the sprite's brush stretches over the sprite. A SwapChainPanel would
/// be external content, drawn by Windows below WinUI's rendering, where no rounded clip reaches
/// it; the compositor draws this surface itself, so clips apply. UI thread only.
/// </summary>
internal sealed unsafe class SurfaceRenderer : IDisposable
{
    // Microsoft.UI.Composition.Interop.h (Windows App SDK), not the Windows.UI.Composition ones.
    private static readonly Guid CompositorInteropIid = new("FAB19398-6D19-4D8A-B752-8F096C396069");
    private static readonly Guid DrawingSurfaceInteropIid = new("2D6355C2-AD57-4EAE-92E4-4C3EFF65D578");
    private static readonly Guid Texture2DIid = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    private readonly Compositor _compositor;
    private readonly SpriteVisual _sprite;
    private readonly CompositionSurfaceBrush _brush;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private CompositionGraphicsDevice? _graphicsDevice;
    private CompositionDrawingSurface? _surface;
    private nint _surfaceInterop;
    private int _width;
    private int _height;

    public SurfaceRenderer(SpriteVisual sprite)
    {
        _sprite = sprite;
        _compositor = sprite.Compositor;
        _brush = _compositor.CreateSurfaceBrush();
        _brush.Stretch = CompositionStretch.Fill;
        sprite.Brush = _brush;
    }

    public void Draw(ReadOnlySpan<byte> bgra, int width, int height)
    {
        if (width <= 0 || height <= 0 || bgra.Length < width * height * 4) return;
        try
        {
            EnsureSurface(width, height);
            var vtable = *(nint**)_surfaceInterop;
            var beginDraw = (delegate* unmanaged[Stdcall]<nint, RECT*, Guid*, nint*, POINT*, int>)vtable[3];
            var endDraw = (delegate* unmanaged[Stdcall]<nint, int>)vtable[4];
            var iid = Texture2DIid;
            nint texturePointer;
            POINT offset;
            Marshal.ThrowExceptionForHR(beginDraw(_surfaceInterop, null, &iid, &texturePointer, &offset));
            try
            {
                // The surface may live in an atlas: write only the frame's rectangle, at its offset.
                using var texture = new ID3D11Texture2D(texturePointer);
                var box = new Box(offset.X, offset.Y, 0, offset.X + width, offset.Y + height, 1);
                fixed (byte* pixels = bgra)
                {
                    _context!.UpdateSubresource(texture, 0, box, (nint)pixels, (uint)(width * 4), 0);
                }
            }
            finally
            {
                Marshal.ThrowExceptionForHR(endDraw(_surfaceInterop));
            }
            // A driver update or GPU reset: start over with the next frame.
            if (_device!.DeviceRemovedReason.Failure) ReleaseDevice();
        }
        // Any failed HRESULT, as whichever exception it maps to: a frame must never end the app.
        catch (Exception e) when (e is SharpGen.Runtime.SharpGenException or ExternalException or ArgumentException or InvalidCastException or InvalidOperationException)
        {
            App.Log("Video renderer: " + e.Message);
            ReleaseDevice();
        }
    }

    private void EnsureSurface(int width, int height)
    {
        if (_device == null) CreateDevice();
        if (_surface == null)
        {
            var surface = _graphicsDevice!.CreateDrawingSurface2(new SizeInt32(width, height),
                DirectXPixelFormat.B8G8R8A8UIntNormalized, DirectXAlphaMode.Ignore);
            var unknown = ((IWinRTObject)surface).NativeObject.ThisPtr;
            var hr = Marshal.QueryInterface(unknown, in DrawingSurfaceInteropIid, out var interop);
            if (hr < 0)
            {
                surface.Dispose();
                Marshal.ThrowExceptionForHR(hr);
            }
            (_surface, _surfaceInterop) = (surface, interop);
            _brush.Surface = _surface;
            (_width, _height) = (width, height);
        }
        else if (width != _width || height != _height)
        {
            _surface.Resize(new SizeInt32(width, height));
            (_width, _height) = (width, height);
        }
    }

    private void CreateDevice()
    {
        FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0];
        var result = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels, out _device, out _context);
        if (result.Failure)
        {
            // No usable GPU (e.g. some VMs): the software rasterizer is plenty for copying frames.
            D3D11.D3D11CreateDevice(null, DriverType.Warp, DeviceCreationFlags.BgraSupport, levels, out _device, out _context).CheckError();
        }
        // The compositor uses the device from its own thread too.
        using (var multithread = _context!.QueryInterface<ID3D11Multithread>()) multithread.SetMultithreadProtected(true);
        _graphicsDevice = CreateGraphicsDevice(_compositor, _device!.NativePointer);
    }

    /// <summary>ICompositorInterop::CreateGraphicsDevice, which WinUI does not project.</summary>
    private static CompositionGraphicsDevice CreateGraphicsDevice(Compositor compositor, nint device)
    {
        var unknown = ((IWinRTObject)compositor).NativeObject.ThisPtr;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in CompositorInteropIid, out var interop));
        try
        {
            var vtable = *(nint**)interop;
            var createGraphicsDevice = (delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)vtable[3];
            nint graphicsDevice;
            Marshal.ThrowExceptionForHR(createGraphicsDevice(interop, device, &graphicsDevice));
            try
            {
                return MarshalInspectable<CompositionGraphicsDevice>.FromAbi(graphicsDevice);
            }
            finally
            {
                Marshal.Release(graphicsDevice);
            }
        }
        finally
        {
            Marshal.Release(interop);
        }
    }

    private void ReleaseDevice()
    {
        _brush.Surface = null;
        if (_surfaceInterop != 0) Marshal.Release(_surfaceInterop);
        _surfaceInterop = 0;
        _surface?.Dispose();
        _surface = null;
        (_width, _height) = (0, 0);
        _graphicsDevice?.Dispose();
        _graphicsDevice = null;
        _context?.ClearState();
        _context?.Dispose();
        _context = null;
        _device?.Dispose();
        _device = null;
    }

    public void Dispose()
    {
        ReleaseDevice();
        _sprite.Brush = null;
        _brush.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }
}
