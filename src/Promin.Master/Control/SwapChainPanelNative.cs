using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Microsoft.UI.Xaml.Controls;
using Vortice.DXGI;

namespace Promin.Master.Control;

/// <summary>The native side of a <see cref="SwapChainPanel"/>: the panel shows whatever swap chain it is given.</summary>
[GeneratedComInterface]
[Guid("63aad0b8-7c24-40ff-85a8-640d944cc325")]
internal partial interface ISwapChainPanelNative
{
    [PreserveSig]
    int SetSwapChain(nint swapChain);
}

internal static class SwapChainPanelNative
{
    private static readonly Guid Iid = new("63aad0b8-7c24-40ff-85a8-640d944cc325");

    /// <summary>Makes <paramref name="panel"/> show <paramref name="swapChain"/>, a composition swap chain. UI thread only.</summary>
    public static void SetSwapChain(SwapChainPanel panel, IDXGISwapChain1 swapChain)
    {
        var inspectable = WinRT.MarshalInspectable<SwapChainPanel>.FromManaged(panel);
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(inspectable, in Iid, out var native));
            try
            {
                var panelNative = (ISwapChainPanelNative)new StrategyBasedComWrappers()
                    .GetOrCreateObjectForComInstance(native, CreateObjectFlags.None);
                Marshal.ThrowExceptionForHR(panelNative.SetSwapChain(swapChain.NativePointer));
            }
            finally
            {
                Marshal.Release(native);
            }
        }
        finally
        {
            Marshal.Release(inspectable);
        }
    }
}
