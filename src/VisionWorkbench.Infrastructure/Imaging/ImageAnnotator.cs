using System.Runtime.InteropServices;
using OpenCvSharp;
using VisionWorkbench.Cameras.Abstractions;
using VisionWorkbench.Contracts.Results;

namespace VisionWorkbench.Infrastructure.Imaging;

/// <summary>检测结果图生成：将各类算法输出绘制到原始帧上。</summary>
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

        foreach (var segmentation in output.Segmentations)
        {
            var segmentationColor = segmentation.Mode.Equals("semantic", StringComparison.OrdinalIgnoreCase)
                ? new Scalar(0, 165, 255)
                : new Scalar(255, 191, 0);
            foreach (var contour in segmentation.Contours)
            {
                var points = contour
                    .Select(point => ToPixelPoint(point, frame.Width, frame.Height))
                    .ToArray();
                if (points.Length < 3)
                {
                    continue;
                }

                using var overlay = mat.Clone();
                Cv2.FillPoly(overlay, new[] { points }, segmentationColor);
                Cv2.AddWeighted(overlay, 0.30, mat, 0.70, 0, mat);
                Cv2.Polylines(mat, new[] { points }, true, segmentationColor, 2, LineTypes.AntiAlias);
                var label = string.IsNullOrWhiteSpace(segmentation.ClassId)
                    ? segmentation.Mode
                    : segmentation.ClassId;
                Cv2.PutText(mat, label,
                    new Point(points[0].X, Math.Max(points[0].Y - 6, 14)),
                    HersheyFonts.HersheyDuplex, 0.45, new Scalar(255, 255, 255), 1,
                    LineTypes.AntiAlias);
            }
        }

        foreach (var track in output.Tracks)
        {
            var rect = ToPixelRect(track.Box, frame.Width, frame.Height);
            Cv2.Rectangle(mat, rect, new Scalar(255, 255, 0), 2);
            Cv2.PutText(mat, $"#{track.TrackId}",
                new Point(rect.X, Math.Max(rect.Y - 6, 14)),
                HersheyFonts.HersheyDuplex, 0.45, new Scalar(255, 255, 0), 1,
                LineTypes.AntiAlias);
            if (track.Trail.Count > 1)
            {
                var trail = track.Trail
                    .Select(point => ToPixelPoint(point, frame.Width, frame.Height))
                    .ToArray();
                Cv2.Polylines(mat, new[] { trail }, false, new Scalar(0, 165, 255), 2,
                    LineTypes.AntiAlias);
            }
        }

        foreach (var keypoint in output.Keypoints)
        {
            foreach (var point in keypoint.Points)
            {
                if (point.Confidence <= 0)
                {
                    continue;
                }
                Cv2.Circle(mat, ToPixelPoint(point, frame.Width, frame.Height), 3,
                    new Scalar(0, 255, 255), -1, LineTypes.AntiAlias);
            }

            if (!string.IsNullOrWhiteSpace(keypoint.BehaviorClass)
                && keypoint.Points.Count > 0)
            {
                var anchor = ToPixelPoint(keypoint.Points[0], frame.Width, frame.Height);
                Cv2.PutText(mat, keypoint.BehaviorClass,
                    new Point(anchor.X, Math.Max(anchor.Y - 8, 14)),
                    HersheyFonts.HersheyDuplex, 0.50, new Scalar(0, 165, 255), 1,
                    LineTypes.AntiAlias);
            }
        }

        if (output.Classifications.Count > 0)
        {
            var classificationText = string.Join(" | ", output.Classifications.Take(4)
                .Select(item => $"{item.ClassName} {item.Confidence:0.00}"));
            Cv2.PutText(mat, classificationText, new Point(12, 100),
                HersheyFonts.HersheyDuplex, 0.55, new Scalar(255, 255, 255), 1,
                LineTypes.AntiAlias);
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
            Cv2.PutText(mat, $"{text}  count={output.GetCount()}", new Point(12, 58),
                HersheyFonts.HersheyDuplex, 2.0, color, 4, LineTypes.AntiAlias);
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

    private static Point ToPixelPoint(NormalizedPoint point, int width, int height) => new(
        Math.Clamp((int)Math.Round(point.X * (width - 1)), 0, Math.Max(0, width - 1)),
        Math.Clamp((int)Math.Round(point.Y * (height - 1)), 0, Math.Max(0, height - 1)));

    private static Point ToPixelPoint(Keypoint point, int width, int height) => new(
        Math.Clamp((int)Math.Round(point.X * (width - 1)), 0, Math.Max(0, width - 1)),
        Math.Clamp((int)Math.Round(point.Y * (height - 1)), 0, Math.Max(0, height - 1)));
}
