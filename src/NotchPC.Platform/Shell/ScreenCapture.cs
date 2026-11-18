using System.Runtime.InteropServices;
using NotchPC.Core.Layout;

namespace NotchPC.Platform.Shell;

/// <summary>
/// Copies a part of the screen (physical pixels) with GDI BitBlt from the desktop. With desktop composition this
/// is what is on screen, including hardware-accelerated windows; DRM-protected video comes out black.
/// </summary>
public static partial class ScreenCapture
{
    private const int SrcCopy = 0x00CC0020;
    private const int CaptureBlt = 0x40000000;

    public static unsafe ShellImage? Capture(ScreenRect rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return null;
        }

        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var info = new BitmapInfoHeader
        {
            Size = (uint)sizeof(BitmapInfoHeader),
            Width = rect.Width,
            Height = -rect.Height, // top-down rows
            Planes = 1,
            BitCount = 32,
        };
        var dib = CreateDIBSection(screen, &info, 0, out var bits, IntPtr.Zero, 0);
        try
        {
            if (dib == IntPtr.Zero || bits == IntPtr.Zero)
            {
                return null;
            }

            var old = SelectObject(memory, dib);
            var ok = BitBlt(memory, 0, 0, rect.Width, rect.Height, screen, rect.Left, rect.Top, SrcCopy | CaptureBlt);
            SelectObject(memory, old);
            if (!ok)
            {
                return null;
            }

            var pixels = new byte[rect.Width * rect.Height * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            for (var i = 3; i < pixels.Length; i += 4)
            {
                pixels[i] = 255; // the desktop has no alpha
            }

            return new ShellImage(rect.Width, rect.Height, pixels);
        }
        finally
        {
            if (dib != IntPtr.Zero)
            {
                DeleteObject(dib);
            }

            DeleteDC(memory);
            _ = ReleaseDC(IntPtr.Zero, screen);
        }
    }

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetDC(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr CreateCompatibleDC(IntPtr dc);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(IntPtr dc);

    [LibraryImport("gdi32.dll")]
    private static unsafe partial IntPtr CreateDIBSection(IntPtr dc, BitmapInfoHeader* info, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BitBlt(IntPtr dest, int x, int y, int width, int height, IntPtr src, int srcX, int srcY, int rop);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(IntPtr obj);

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
        // BITMAPINFO.bmiColors is unused for 32-bit BI_RGB.
        public uint Colors;
    }
}
