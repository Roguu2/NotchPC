using System.Runtime.InteropServices;

namespace NotchPC.Platform.Shell;

/// <summary>Raw 32-bit image (BGRA, premultiplied alpha, top-down rows).</summary>
public sealed record ShellImage(int Width, int Height, byte[] Pixels)
{
    public int Stride => Width * 4;
}

/// <summary>
/// Thumbnails and icons exactly as File Explorer shows them (IShellItemImageFactory): photo previews for images,
/// the app icon for .exe/.lnk, the type icon for everything else. Safe to call from a background thread.
/// </summary>
public static partial class ShellImages
{
    private const int SiigbfResizeToFit = 0x00;
    private const int SiigbfIconOnly = 0x04;
    private static readonly Guid ImageFactoryId = new("BCC18B79-BA16-442F-80C4-8A59C30C463B");

    /// <summary>Returns null when the path does not exist or Windows has no image for it.</summary>
    public static ShellImage? Get(string path, int size, bool iconOnly = false)
    {
        IntPtr unknown = IntPtr.Zero;
        IntPtr hbitmap = IntPtr.Zero;
        try
        {
            var iid = ImageFactoryId;
            if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out unknown) != 0 || unknown == IntPtr.Zero)
            {
                return null;
            }

            var factory = (IShellItemImageFactory)Marshal.GetObjectForIUnknown(unknown);
            try
            {
                if (factory.GetImage(new NativeSize { Cx = size, Cy = size }, iconOnly ? SiigbfIconOnly : SiigbfResizeToFit, out hbitmap) != 0)
                {
                    return null;
                }
            }
            finally
            {
                Marshal.ReleaseComObject(factory);
            }

            return Read(hbitmap);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException)
        {
            return null;
        }
        finally
        {
            if (unknown != IntPtr.Zero)
            {
                Marshal.Release(unknown);
            }

            if (hbitmap != IntPtr.Zero)
            {
                DeleteObject(hbitmap);
            }
        }
    }

    private static unsafe ShellImage? Read(IntPtr hbitmap)
    {
        DibSection dib;
        if (GetObjectW(hbitmap, sizeof(DibSection), &dib) == 0 || dib.Bitmap.BitsPixel != 32 || dib.Bitmap.Bits == IntPtr.Zero)
        {
            return null;
        }

        var width = dib.Bitmap.Width;
        var height = dib.Bitmap.Height;
        var stride = dib.Bitmap.WidthBytes;
        var pixels = new byte[width * height * 4];
        var bottomUp = dib.Header.Height > 0;
        var anyAlpha = false;
        var src = (byte*)dib.Bitmap.Bits;
        for (var y = 0; y < height; y++)
        {
            var row = src + ((bottomUp ? height - 1 - y : y) * stride);
            var target = y * width * 4;
            for (var x = 0; x < width * 4; x++)
            {
                pixels[target + x] = row[x];
            }

            for (var x = 3; x < width * 4 && !anyAlpha; x += 4)
            {
                anyAlpha = row[x] != 0;
            }
        }

        if (!anyAlpha)
        {
            // Photo thumbnails come without an alpha channel: make them opaque.
            for (var i = 3; i < pixels.Length; i += 4)
            {
                pixels[i] = 255;
            }
        }

        return new ShellImage(width, height, pixels);
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid riid, out IntPtr item);

    [LibraryImport("gdi32.dll")]
    private static unsafe partial int GetObjectW(IntPtr handle, int size, void* result);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int Cx;
        public int Cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitsPixel;
        public IntPtr Bits;
    }

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
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct DibSection
    {
        public BitmapInfo Bitmap;
        public BitmapInfoHeader Header;
        public fixed uint BitFields[3];
        public IntPtr Section;
        public uint Offset;
    }

    [ComImport]
    [Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, int flags, out IntPtr bitmap);
    }
}
