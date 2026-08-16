using System.Runtime.InteropServices;
using OpenCvSharp;
using VisionWorkbench.Cameras.Abstractions;

namespace VisionWorkbench.Cameras.Files;

/// <summary>Mat → VideoFrame 转换（BGR24 连续缓冲）。中文路径安全：读文件用字节 + ImDecode。</summary>
internal static class FrameConvert
{
    public static VideoFrame? ToFrame(Mat mat, long sequence, DateTimeOffset? timestamp = null)
    {
        if (mat.Empty())
        {
            return null;
        }
        var source = EnsureBgr24(mat, out var created);
        try
        {
            var bytes = new byte[source.Width * source.Height * 3];
            Marshal.Copy(source.Data, bytes, 0, bytes.Length);
            return new VideoFrame(sequence, timestamp ?? DateTimeOffset.Now, source.Width, source.Height, bytes);
        }
        finally
        {
            if (created)
            {
                source.Dispose();
            }
        }
    }

    public static Mat? DecodeFile(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }
        var data = File.ReadAllBytes(path);
        return Cv2.ImDecode(data, ImreadModes.Color);
    }

    private static Mat EnsureBgr24(Mat mat, out bool converted)
    {
        if (mat.Type() == MatType.CV_8UC3 && mat.IsContinuous())
        {
            converted = false;
            return mat;
        }
        converted = true;
        Mat bgr;
        if (mat.Channels() == 1)
        {
            bgr = new Mat();
            Cv2.CvtColor(mat, bgr, ColorConversionCodes.GRAY2BGR);
        }
        else if (mat.Channels() == 4)
        {
            bgr = new Mat();
            Cv2.CvtColor(mat, bgr, ColorConversionCodes.BGRA2BGR);
        }
        else
        {
            bgr = mat.Clone();
        }
        if (bgr.Type() != MatType.CV_8UC3 || !bgr.IsContinuous())
        {
            var fixedMat = bgr.Clone();
            bgr.Dispose();
            return fixedMat;
        }
        return bgr;
    }
}
