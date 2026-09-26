using System.Runtime.InteropServices;
using OpenCvSharp;
using VisionWorkbench.Cameras.Abstractions;

namespace VisionWorkbench.Infrastructure.Imaging;

/// <summary>
/// 首版图像传输：临时图片文件（文档 §11.3）。
/// C# 抓图 → 保存临时 PNG → JSON 传文件路径 → Worker 读取推理。
/// </summary>
public sealed class TempImageStore(string rootDirectory)
{
    public string RootDirectory { get; } = rootDirectory;

    public TempImageStore Initialize()
    {
        Directory.CreateDirectory(RootDirectory);
        return this;
    }

    /// <summary>保存一帧为 PNG 临时文件，返回绝对路径。</summary>
    public string SaveFrame(VideoFrame frame, string prefix = "submit")
    {
        var path = Path.Combine(RootDirectory, $"{prefix}-{Guid.NewGuid():N}.png");
        WritePng(frame.Pixels, frame.Width, frame.Height, path);
        return path;
    }

    public static void WritePng(byte[] bgr24, int width, int height, string path)
    {
        ArgumentNullException.ThrowIfNull(bgr24);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (bgr24.LongLength != checked((long)width * height * 3))
        {
            throw new ArgumentException("像素缓冲长度必须等于 width*height*3（BGR24）", nameof(bgr24));
        }
        using var mat = new Mat(height, width, MatType.CV_8UC3);
        Marshal.Copy(bgr24, 0, mat.Data, bgr24.Length);
        // ImEncode + WriteAllBytes：非 ASCII 路径安全（APP-003），ImWrite 对中文路径不稳
        Cv2.ImEncode(".png", mat, out var bytes);
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>安全删除临时文件（FRM-005）。</summary>
    public void Delete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Windows 下文件被占用时跳过，下次清理
        }
    }

    /// <summary>清理超龄临时文件（启动时调用）。</summary>
    public int PurgeOlderThan(TimeSpan age)
    {
        if (!Directory.Exists(RootDirectory))
        {
            return 0;
        }
        var cutoff = DateTime.UtcNow - age;
        var purged = 0;
        foreach (var file in Directory.EnumerateFiles(RootDirectory))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    File.Delete(file);
                    purged++;
                }
            }
            catch (IOException)
            {
                // 跳过被占用文件
            }
        }
        return purged;
    }

    public int CountFiles() =>
        Directory.Exists(RootDirectory) ? Directory.EnumerateFiles(RootDirectory).Count() : 0;
}
