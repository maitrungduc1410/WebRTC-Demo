using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using WinRT;

namespace WebRtcDemo.App.Video;

/// <summary>
/// Shows BGRA frames in a <see cref="SwapChainPanel"/>: each frame is uploaded straight into the
/// back buffer of a composition swap chain the size of the frame, and the swap chain's matrix
/// scales it to the panel. UI thread only.
/// </summary>
internal sealed unsafe class SwapChainRenderer : IDisposable
{
    private static readonly Guid SwapChainPanelNativeIid = new("63aad0b8-7c24-40ff-85a8-640d944cc325");
    private const int BufferCount = 2;
    private const Format PixelFormat = Format.B8G8R8A8_UNorm;

    private readonly SwapChainPanel _panel;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGISwapChain1? _swapChain;
    private int _width;
    private int _height;
    private float _scale = 1;

    public SwapChainRenderer(SwapChainPanel panel) => _panel = panel;

    /// <summary>DIPs per frame pixel.</summary>
    public float Scale
    {
        get => _scale;
        set
        {
            if (_scale == value) return;
            _scale = value;
            ApplyTransform();
        }
    }

    public void Draw(ReadOnlySpan<byte> bgra, int width, int height)
    {
        if (width <= 0 || height <= 0 || bgra.Length < width * height * 4) return;
        try
        {
            EnsureSwapChain(width, height);
            using var backBuffer = _swapChain!.GetBuffer<ID3D11Texture2D>(0);
            fixed (byte* pixels = bgra)
            {
                _context!.UpdateSubresource(backBuffer, 0, null, (nint)pixels, (uint)(width * 4), 0);
            }
            var result = _swapChain.Present(0, PresentFlags.None);
            if (result == Vortice.DXGI.ResultCode.DeviceRemoved || result == Vortice.DXGI.ResultCode.DeviceReset)
            {
                // A driver update or GPU reset: start over with the next frame.
                ReleaseDevice();
            }
        }
        catch (SharpGen.Runtime.SharpGenException e)
        {
            App.Log("Video renderer: " + e.Message);
            ReleaseDevice();
        }
    }

    private void EnsureSwapChain(int width, int height)
    {
        if (_device == null) CreateDevice();
        if (_swapChain == null)
        {
            using var dxgiDevice = _device!.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDevice.GetAdapter();
            using var factory = adapter.GetParent<IDXGIFactory2>();
            var description = new SwapChainDescription1
            {
                Width = (uint)width,
                Height = (uint)height,
                Format = PixelFormat,
                BufferCount = BufferCount,
                BufferUsage = Usage.RenderTargetOutput,
                SampleDescription = new SampleDescription(1, 0),
                Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipSequential,
                AlphaMode = AlphaMode.Ignore,
            };
            _swapChain = factory.CreateSwapChainForComposition(_device, description);
            SetPanelSwapChain(_panel, _swapChain.NativePointer);
            (_width, _height) = (width, height);
            ApplyTransform();
        }
        else if (width != _width || height != _height)
        {
            _swapChain.ResizeBuffers(BufferCount, (uint)width, (uint)height, PixelFormat, SwapChainFlags.None).CheckError();
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
    }

    private void ApplyTransform()
    {
        if (_swapChain == null) return;
        using var swapChain2 = _swapChain.QueryInterfaceOrNull<IDXGISwapChain2>();
        if (swapChain2 != null) swapChain2.MatrixTransform = Matrix3x2.CreateScale(_scale);
    }

    /// <summary>ISwapChainPanelNative::SetSwapChain, the one call WinUI does not project.</summary>
    private static void SetPanelSwapChain(SwapChainPanel panel, nint swapChain)
    {
        var unknown = ((IWinRTObject)panel).NativeObject.ThisPtr;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in SwapChainPanelNativeIid, out var native));
        try
        {
            var vtable = *(nint**)native;
            var setSwapChain = (delegate* unmanaged[Stdcall]<nint, nint, int>)vtable[3];
            Marshal.ThrowExceptionForHR(setSwapChain(native, swapChain));
        }
        finally
        {
            Marshal.Release(native);
        }
    }

    private void ReleaseDevice()
    {
        if (_swapChain != null)
        {
            try
            {
                SetPanelSwapChain(_panel, 0);
            }
            catch (COMException)
            {
                // The panel is already gone.
            }
        }
        _swapChain?.Dispose();
        _swapChain = null;
        _context?.ClearState();
        _context?.Dispose();
        _context = null;
        _device?.Dispose();
        _device = null;
    }

    public void Dispose() => ReleaseDevice();
}
