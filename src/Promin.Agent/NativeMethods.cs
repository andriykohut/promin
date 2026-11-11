using System.Runtime.InteropServices;

namespace Promin.Agent;

/// <summary>The Win32 calls the render window, the lock screen, the input block and the console output need.</summary>
internal static class NativeMethods
{
    public delegate nint WindowProcedure(nint hwnd, uint message, nint wParam, nint lParam);

    public delegate nint HookProcedure(int code, nint wParam, nint lParam);

    public delegate bool MonitorProcedure(nint monitor, nint dc, nint bounds, nint data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WindowClass
    {
        public uint Size;
        public uint Style;
        public nint WindowProcedure;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public string? MenuName;
        public string ClassName;
        public nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Message
    {
        public nint Window;
        public uint Id;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public int PointX;
        public int PointY;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MonitorInfo
    {
        public uint Size;
        public Rect Bounds;
        public Rect WorkArea;
        public uint Flags;
    }

    /// <summary>PAINTSTRUCT on 64-bit Windows. Nothing here reads it: <c>BeginPaint</c> fills it and <c>EndPaint</c> takes it back.</summary>
    [StructLayout(LayoutKind.Sequential, Size = 72)]
    public struct PaintStruct
    {
        public nint Dc;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    /// <summary>
    /// INPUT: the type, then a union of a mouse and a keyboard event. The union starts at offset 8, where 64-bit Windows
    /// aligns it, and the agent runs only as 64-bit.
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    public struct Input
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public MouseInput Mouse;
        [FieldOffset(8)] public KeyboardInput Keyboard;
    }

    /// <summary>KBDLLHOOKSTRUCT, what a low-level keyboard hook's <c>lParam</c> points to.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct KeyboardHookData
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    /// <summary>MSLLHOOKSTRUCT, what a low-level mouse hook's <c>lParam</c> points to.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MouseHookData
    {
        public int PointX;
        public int PointY;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    public const uint ES_SYSTEM_REQUIRED = 0x00000001;
    public const uint ES_DISPLAY_REQUIRED = 0x00000002;
    public const uint ES_CONTINUOUS = 0x80000000;

    public const uint INPUT_MOUSE = 0;
    public const uint INPUT_KEYBOARD = 1;
    public const uint MOUSEEVENTF_MOVE = 0x0001;
    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;
    public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    public const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    public const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    public const uint MOUSEEVENTF_XDOWN = 0x0080;
    public const uint MOUSEEVENTF_XUP = 0x0100;
    public const uint MOUSEEVENTF_WHEEL = 0x0800;
    public const uint MOUSEEVENTF_HWHEEL = 0x1000;
    public const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
    public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const uint XBUTTON1 = 1;
    public const uint XBUTTON2 = 2;

    public const uint WS_POPUP = 0x80000000;
    public const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    public const uint WS_EX_TOPMOST = 0x00000008;
    public const uint WS_EX_TOOLWINDOW = 0x00000080;
    public const uint WS_EX_TRANSPARENT = 0x00000020;
    public const uint WS_EX_LAYERED = 0x00080000;
    public const uint LWA_ALPHA = 0x2;
    public const uint WS_EX_NOACTIVATE = 0x08000000;
    public const int CW_USEDEFAULT = unchecked((int)0x80000000);

    public const uint WM_DESTROY = 0x0002;
    public const uint WM_SIZE = 0x0005;
    public const uint WM_PAINT = 0x000F;
    public const uint WM_CLOSE = 0x0010;
    public const uint WM_QUIT = 0x0012;
    public const uint WM_ERASEBKGND = 0x0014;
    public const uint WM_SETCURSOR = 0x0020;
    public const uint WM_DISPLAYCHANGE = 0x007E;
    public const uint WM_NCCREATE = 0x0081;
    public const uint WM_NCDESTROY = 0x0082;
    public const uint WM_KEYUP = 0x0101;
    public const uint WM_SYSKEYUP = 0x0105;
    public const uint WM_TIMER = 0x0113;
    public const uint WM_MOUSEMOVE = 0x0200;
    public const uint WM_LBUTTONUP = 0x0202;
    public const uint WM_RBUTTONUP = 0x0205;
    public const uint WM_MBUTTONUP = 0x0208;
    public const uint WM_XBUTTONUP = 0x020C;
    public const uint WM_CLIPBOARDUPDATE = 0x031D;
    public const uint WM_APP = 0x8000;

    public const int WH_KEYBOARD_LL = 13;
    public const int WH_MOUSE_LL = 14;
    public const uint PM_NOREMOVE = 0;
    public const uint MONITORINFOF_PRIMARY = 1;

    public const uint DT_CENTER = 0x0001;
    public const uint DT_VCENTER = 0x0004;
    public const uint DT_WORDBREAK = 0x0010;
    public const uint DT_SINGLELINE = 0x0020;
    public const uint DT_CALCRECT = 0x0400;
    public const uint DT_NOPREFIX = 0x0800;
    public const uint DT_EDITCONTROL = 0x2000;
    public const int TRANSPARENT = 1;
    public const int FW_NORMAL = 400;
    public const int FW_SEMIBOLD = 600;
    public const uint DEFAULT_CHARSET = 1;
    public const uint CLEARTYPE_QUALITY = 5;

    public const int SIZE_MINIMIZED = 1;
    public const int HTCLIENT = 1;
    public const int SW_SHOW = 5;
    public const int SM_CXSCREEN = 0;
    public const int SM_CYSCREEN = 1;
    public const int GWLP_USERDATA = -21;
    public const int BLACK_BRUSH = 4;
    public static readonly nint HWND_TOPMOST = -1;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;

    public const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
    public const uint MONITOR_DEFAULTTOPRIMARY = 1;
    public const int MDT_EFFECTIVE_DPI = 0;
    public static readonly nint HWND_MESSAGE = -3;
    public const int NULL_PEN = 8;

    public const uint CF_UNICODETEXT = 13;
    public const uint GMEM_MOVEABLE = 2;

    public const uint DESKTOP_READOBJECTS = 0x0001;
    public const int UOI_NAME = 2;

    public const int STD_OUTPUT_HANDLE = -11;
    public const int ATTACH_PARENT_PROCESS = -1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassExW(ref WindowClass windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern nint CreateWindowExW(
        uint extendedStyle, string className, string? windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern nint DefWindowProcW(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(nint hwnd, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("user32.dll")]
    public static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetClientRect(nint hwnd, out Rect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AdjustWindowRectEx(ref Rect rect, uint style, [MarshalAs(UnmanagedType.Bool)] bool menu, uint extendedStyle);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    public static extern int GetMessageW(out Message message, nint hwnd, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PeekMessageW(out Message message, nint hwnd, uint filterMin, uint filterMax, uint remove);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TranslateMessage(ref Message message);

    [DllImport("user32.dll")]
    public static extern nint DispatchMessageW(ref Message message);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessageW(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostThreadMessageW(uint threadId, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    public static extern nuint SetTimer(nint hwnd, nuint id, uint milliseconds, nint callback);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint SetWindowsHookExW(int id, HookProcedure procedure, nint module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    public static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorProcedure procedure, nint data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    public static extern nint BeginPaint(nint hwnd, out PaintStruct paint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EndPaint(nint hwnd, ref PaintStruct paint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UpdateWindow(nint hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int DrawTextW(nint dc, string text, int length, ref Rect rect, uint format);

    [DllImport("user32.dll")]
    public static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll")]
    public static extern nint SetCursor(nint cursor);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ValidateRect(nint hwnd, nint rect);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern nint GetWindowLongPtr(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint count, [In] Input[] inputs, int size);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetLayeredWindowAttributes(nint hwnd, uint colourKey, byte alpha, uint flags);

    [DllImport("user32.dll")]
    public static extern nint MonitorFromPoint(Point point, uint flags);

    /// <summary>S_OK (0) when it gave the DPI; <c>shcore.dll</c> has no other way to ask a monitor.</summary>
    [DllImport("shcore.dll")]
    public static extern int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    public static extern nint GetDC(nint hwnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(nint hwnd, nint dc);

    [DllImport("user32.dll")]
    public static extern int SetWindowRgn(nint hwnd, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AddClipboardFormatListener(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RemoveClipboardFormatListener(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool OpenClipboard(nint owner);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint SetClipboardData(uint format, nint data);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint GetClipboardData(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern nint OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseDesktop(nint desktop);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetUserObjectInformationW(nint handle, int index, nint buffer, uint length, out uint needed);

    [DllImport("kernel32.dll")]
    public static extern nint GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern nint GlobalLock(nint memory);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GlobalUnlock(nint memory);

    [DllImport("kernel32.dll")]
    public static extern nint GlobalFree(nint memory);

    [DllImport("gdi32.dll")]
    public static extern nint CreatePen(int style, int width, uint colour);

    [DllImport("gdi32.dll")]
    public static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RoundRect(nint dc, int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Ellipse(nint dc, int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    public static extern nint GetStockObject(int index);

    [DllImport("gdi32.dll")]
    public static extern nint CreateSolidBrush(uint colour);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    public static extern nint CreateFontW(
        int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeOut,
        uint charset, uint outputPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);

    [DllImport("gdi32.dll")]
    public static extern nint SelectObject(nint dc, nint gdiObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteObject(nint gdiObject);

    [DllImport("gdi32.dll")]
    public static extern uint SetTextColor(nint dc, uint colour);

    [DllImport("gdi32.dll")]
    public static extern int SetBkMode(nint dc, int mode);

    /// <summary>Sets the calling thread's request and returns its previous one; 0 when it failed.</summary>
    [DllImport("kernel32.dll")]
    public static extern uint SetThreadExecutionState(uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern nint GetModuleHandleW(string? moduleName);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll")]
    public static extern nint GetStdHandle(int standardHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AttachConsole(int processId);

    // ---- starting the agent in a signed-in session (the service runs as SYSTEM) ------------------

    /// <summary>WTSGetActiveConsoleSessionId returns this when no session is attached to the console.</summary>
    public const uint NoActiveConsoleSession = 0xFFFFFFFF;

    public const int WTSUserName = 5; // WTS_INFO_CLASS

    public const int SecurityImpersonation = 2; // SECURITY_IMPERSONATION_LEVEL
    public const int TokenPrimary = 1; // TOKEN_TYPE

    public const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    public const uint CREATE_NO_WINDOW = 0x08000000;

    /// <summary>WTSQueryUserToken fails with this when nobody is signed in to the session.</summary>
    public const int ERROR_NO_TOKEN = 1008;

    /// <summary>WTSQueryUserToken fails with this when the caller is not SYSTEM.</summary>
    public const int ERROR_PRIVILEGE_NOT_HELD = 1314;

    public static readonly Guid FOLDERID_Desktop = new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641");
    public static readonly Guid FOLDERID_LocalAppData = new("F1B32785-6FBA-4FCF-9D55-7B8E7F157091");

    /// <summary>SHGetKnownFolderPath returns the path without checking that the folder exists, so it touches nothing on disk.</summary>
    public const uint KF_FLAG_DONT_VERIFY = 0x00004000;

    // The access a primary token needs to launch a process with CreateProcessAsUser, and to be retargeted to another
    // session: TOKEN_ASSIGN_PRIMARY | TOKEN_DUPLICATE | TOKEN_QUERY | TOKEN_ADJUST_DEFAULT | TOKEN_ADJUST_SESSIONID.
    public const uint TOKEN_FOR_PRIMARY = 0x0001 | 0x0002 | 0x0008 | 0x0080 | 0x0100;

    // Opening the service's own (SYSTEM) token so it can be duplicated: TOKEN_DUPLICATE | TOKEN_QUERY.
    public const uint TOKEN_FOR_SOURCE = 0x0002 | 0x0008;

    // TOKEN_INFORMATION_CLASS.TokenSessionId: retargets a duplicated token to another session.
    public const int TokenSessionId = 12;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2;
        public nint Reserved3;
        public nint StdInput;
        public nint StdOutput;
        public nint StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [DllImport("kernel32.dll")]
    public static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WTSQueryUserToken(uint sessionId, out nint token);

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WTSQuerySessionInformationW(nint server, uint sessionId, int infoClass, out nint buffer, out uint bytesReturned);

    [DllImport("wtsapi32.dll")]
    public static extern void WTSFreeMemory(nint memory);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DuplicateTokenEx(nint existingToken, uint desiredAccess, nint attributes, int impersonationLevel, int tokenType, out nint newToken);

    [DllImport("kernel32.dll")]
    public static extern nint GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool OpenProcessToken(nint process, uint desiredAccess, out nint token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetTokenInformation(nint token, int tokenInformationClass, ref uint information, uint length);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CreateEnvironmentBlock(out nint environment, nint token, [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyEnvironmentBlock(nint environment);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CreateProcessAsUserW(
        nint token, string? applicationName, string? commandLine, nint processAttributes, nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, nint environment, string? currentDirectory,
        ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TerminateProcess(nint process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetExitCodeProcess(nint process, out uint exitCode);

    /// <summary>
    /// The path of a known folder of the user whose token this is (zero: this process's user). Throws when Windows
    /// cannot give it; for another user, their profile must be loaded, as it is while they are signed in.
    /// </summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, PreserveSig = false)]
    [return: MarshalAs(UnmanagedType.LPWStr)]
    public static extern string SHGetKnownFolderPath(in Guid folderId, uint flags, nint token);

    /// <summary>Whether Windows warns before opening files of this type (programs and scripts). <paramref name="association"/> is an extension with its dot, e.g. <c>.exe</c>.</summary>
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AssocIsDangerous(string association);
}
