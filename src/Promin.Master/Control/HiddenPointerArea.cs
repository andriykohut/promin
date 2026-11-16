using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using WinRT;

namespace Promin.Master.Control;

/// <summary>A grid that hides this PC's pointer while the pointer is over it or captured by it.</summary>
public sealed partial class HiddenPointerArea : Grid
{
    private const int CursorSize = 32;

    // IInputCursorStaticsInterop, from Microsoft.UI.Input.InputCursor.Interop.h.
    private static readonly Guid InputCursorStaticsInterop = new("ac6f5065-90c4-46ce-beb7-05e138e54117");

    private static readonly Lazy<InputCursor> Blank = new(CreateBlankCursor);

    public HiddenPointerArea() => ProtectedCursor = Blank.Value;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateFromHCursorMethod(nint self, nint cursor, out nint result);

    // WinUI has no blank system cursor and ignores ShowCursor, so a transparent Win32 cursor is handed to it through
    // IInputCursorStaticsInterop, which copies it.
    private static InputCursor CreateBlankCursor()
    {
        // AND 1 and XOR 0 on every pixel: the screen shows through.
        var andPlane = Enumerable.Repeat((byte)0xFF, CursorSize * CursorSize / 8).ToArray();
        var xorPlane = new byte[CursorSize * CursorSize / 8];
        var handle = CreateCursor(GetModuleHandleW(null), 0, 0, CursorSize, CursorSize, andPlane, xorPlane);
        if (handle == 0)
        {
            throw new Win32Exception();
        }

        try
        {
            using var factory = ActivationFactory.Get("Microsoft.UI.Input.InputCursor", InputCursorStaticsInterop);

            // The method comes after IUnknown's three and IInspectable's three.
            var vtable = Marshal.ReadIntPtr(factory.ThisPtr);
            var create = Marshal.GetDelegateForFunctionPointer<CreateFromHCursorMethod>(Marshal.ReadIntPtr(vtable, 6 * nint.Size));
            Marshal.ThrowExceptionForHR(create(factory.ThisPtr, handle, out var cursor));
            try
            {
                return InputCursor.FromAbi(cursor);
            }
            finally
            {
                Marshal.Release(cursor);
            }
        }
        finally
        {
            DestroyCursor(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CreateCursor(nint instance, int hotSpotX, int hotSpotY, int width, int height, byte[] andPlane, byte[] xorPlane);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyCursor(nint cursor);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? moduleName);
}
