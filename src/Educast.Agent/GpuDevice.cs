using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Educast.Agent;

/// <summary>
/// The Direct3D 11 device the decoder and the render window share: on the adapter that drives the primary
/// display, with video support (the decoder needs it to decode on the GPU, the window needs it for the video
/// processor) and multithread protection on (Media Foundation uses the device from its own threads).
/// </summary>
internal sealed class GpuDevice : IDisposable
{
    private readonly IDXGIAdapter1 _adapter;

    private GpuDevice(IDXGIAdapter1 adapter, ID3D11Device device)
    {
        _adapter = adapter;
        Device = device;
    }

    public ID3D11Device Device { get; }

    public string AdapterName => _adapter.Description1.Description;

    /// <exception cref="NotSupportedException">
    /// No adapter drives the primary display, or the device it gives has no video processor. The message says which.
    /// </exception>
    public static GpuDevice Create()
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        var adapter = FindAdapterOfPrimaryOutput(factory)
            ?? throw new NotSupportedException("No graphics adapter drives the primary display.");

        try
        {
            D3D11.D3D11CreateDevice(
                adapter,
                DriverType.Unknown,
                DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport,
                [FeatureLevel.Level_11_0],
                out var device,
                out _,
                out var context).CheckError();

            // The device keeps its own immediate context; this extra reference is not needed.
            context.Dispose();

            try
            {
                using (var multithread = device.QueryInterface<ID3D11Multithread>())
                {
                    multithread.SetMultithreadProtected(true);
                }

                using var videoDevice = device.QueryInterfaceOrNull<ID3D11VideoDevice>();
                if (videoDevice is null)
                {
                    throw new NotSupportedException(
                        "The graphics driver offers no video processor, which scales the picture to the screen.");
                }

                return new GpuDevice(adapter, device);
            }
            catch
            {
                device.Dispose();
                throw;
            }
        }
        catch
        {
            adapter.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        Device.Dispose();
        _adapter.Dispose();
    }

    private static IDXGIAdapter1? FindAdapterOfPrimaryOutput(IDXGIFactory1 factory)
    {
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
            {
                var description = output.Description;
                output.Dispose();
                if (description.AttachedToDesktop && description.DesktopCoordinates.Left == 0 && description.DesktopCoordinates.Top == 0)
                {
                    return adapter;
                }
            }

            adapter.Dispose();
        }

        return null;
    }
}
