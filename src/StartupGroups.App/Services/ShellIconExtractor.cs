using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace StartupGroups.App.Services;

[SupportedOSPlatform("windows")]
internal static class ShellIconExtractor
{
    public static BitmapSource? GetImage(string parsingName, int size)
    {
        if (string.IsNullOrWhiteSpace(parsingName))
        {
            return null;
        }

        var iid = typeof(IShellItemImageFactory).GUID;
        int hr = SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out var factory);
        if (hr != 0 || factory is null)
        {
            return null;
        }

        IntPtr hbitmap = IntPtr.Zero;
        try
        {
            var sz = new SIZE { cx = size, cy = size };
            hr = factory.GetImage(sz, SIIGBF.ResizeToFit | SIIGBF.IconOnly, out hbitmap);
            if (hr != 0 || hbitmap == IntPtr.Zero)
            {
                return null;
            }

            var bmp = Imaging.CreateBitmapSourceFromHBitmap(
                hbitmap,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hbitmap != IntPtr.Zero)
            {
                DeleteObject(hbitmap);
            }

            try
            {
                Marshal.FinalReleaseComObject(factory);
            }
            catch
            {
            }
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
        IntPtr pbc,
        [In] ref Guid riid,
        [Out, MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    [Flags]
    private enum SIIGBF : uint
    {
        ResizeToFit = 0,
        BiggerSizeOk = 1,
        MemoryOnly = 2,
        IconOnly = 4,
        ThumbnailOnly = 8,
        InCacheOnly = 0x10,
        CropToSquare = 0x20,
        WideThumbnails = 0x40,
        IconBackground = 0x80,
        ScaleUp = 0x100
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
    }
}
