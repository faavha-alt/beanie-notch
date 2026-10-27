using System.Runtime.InteropServices;

namespace Beanotch.Services;

static partial class Native
{
    [LibraryImport("user32.dll")]
    internal static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

    [LibraryImport("user32.dll")]
    private static partial int SetWindowRgn(nint hwnd, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [LibraryImport("kernel32.dll")]
    internal static partial uint SetThreadExecutionState(uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    // Windows 11 otherwise draws a 1px light border around the window region.
    internal static void RemoveBorder(nint hwnd)
    {
        const int DWMWA_BORDER_COLOR = 34;
        var none = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
        _ = DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref none, sizeof(int));
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileOperation
    {
        public nint Window;
        public uint Function;
        public string From;
        public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool Aborted;
        public nint NameMappings;
        public string? Title;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref FileOperation operation);

    // Moves a file or folder to the Recycle Bin, so a mistake can be undone.
    internal static void Recycle(string path)
    {
        var op = new FileOperation
        {
            Function = 3, // FO_DELETE
            From = path + "\0\0",
            Flags = 0x0040 | 0x0010 | 0x0004 | 0x0400, // FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI
        };
        SHFileOperationW(ref op);
    }

    // Clips the window to a rounded rectangle, so the island can be a true pill. Windows owns the region afterwards.
    internal static void Round(nint hwnd, int width, int height, int radius)
    {
        var region = CreateRoundRectRgn(0, 0, width + 1, height + 1, radius * 2, radius * 2);
        SetWindowRgn(hwnd, region, true);
    }
}
