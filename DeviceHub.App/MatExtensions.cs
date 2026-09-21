using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenCvSharp;

namespace DeviceHub.App;

/// <summary>OpenCV Mat → WPF BitmapSource 转换。</summary>
public static class MatExtensions
{
    public static BitmapSource ToBitmapSource(this Mat mat)
    {
        var format = mat.Channels() switch
        {
            3 => PixelFormats.Bgr24,
            1 => PixelFormats.Gray8,
            4 => PixelFormats.Bgr32,
            _ => throw new NotSupportedException($"不支持的通道数：{mat.Channels()}"),
        };

        var bitmap = new WriteableBitmap(mat.Width, mat.Height, 96, 96, format, null);
        var bytes = new byte[mat.Total() * mat.ElemSize()];
        System.Runtime.InteropServices.Marshal.Copy(mat.Data, bytes, 0, bytes.Length);
        bitmap.WritePixels(
            new System.Windows.Int32Rect(0, 0, mat.Width, mat.Height),
            bytes,
            mat.Width * mat.ElemSize(),
            0);
        bitmap.Freeze();
        return bitmap;
    }
}
