using System.Runtime.InteropServices;
using OpenCvSharp;
using VisionWorkbench.Cameras.Abstractions;
using VisionWorkbench.Contracts.Results;

namespace VisionWorkbench.Infrastructure.Imaging;

/// <summary>标注图生成：检测框 + 状态横幅（NG/待确认证据图，文档 §20.3）。</summary>
public static class ImageAnnotator
{
    public static void AnnotateToFile(
        VideoFrame frame,
        AlgorithmOutput output,
        DecisionResult? decision,
        string destPath)
    {
        using var mat = new Mat(frame.Height, frame.Width, MatType.CV_8UC3);
        Marshal.Copy(frame.Pixels, 0, mat.Data, frame.Pixels.Length);

        foreach (var detection in output.Detections)
        {
            var rect = ToPixelRect(detection.Box, frame.Width, frame.Height);
            Cv2.Rectangle(mat, rect, new Scalar(60, 220, 60), 2);
            var label = $"{detection.ClassName} {detection.Confidence:0.00}";
            Cv2.PutText(
                mat, label,
                new Point(rect.X, Math.Max(rect.Y - 6, 14)),
                HersheyFonts.HersheyDuplex, 0.45, new Scalar(255, 255, 255), 1, LineTypes.AntiAlias);
        }

        var (text, color) = decision?.Status switch
        {
            DecisionStatus.Ok => ("OK", new Scalar(80, 200, 80)),
            DecisionStatus.Ng => ("NG", new Scalar(60, 60, 240)),
            DecisionStatus.ReviewRequired => ("待确认", new Scalar(40, 160, 255)),
            _ => ("", new Scalar(255, 255, 255)),
        };
        if (text.Length > 0)
        {
            Cv2.PutText(mat, $"{text}  count={output.GetCount()}", new Point(12, 34),
                HersheyFonts.HersheyDuplex, 1.0, color, 2, LineTypes.AntiAlias);
        }

        // ImEncode + WriteAllBytes：非 ASCII 路径安全（APP-003）
        Cv2.ImEncode(".png", mat, out var bytes);
        File.WriteAllBytes(destPath, bytes);
    }

    private static Rect ToPixelRect(NormalizedRect box, int width, int height)
    {
        var x = Math.Clamp((int)Math.Floor(box.X * width), 0, width - 1);
        var y = Math.Clamp((int)Math.Floor(box.Y * height), 0, height - 1);
        var w = Math.Clamp((int)Math.Ceiling(box.Width * width), 1, width - x);
        var h = Math.Clamp((int)Math.Ceiling(box.Height * height), 1, height - y);
        return new Rect(x, y, w, h);
    }
}
