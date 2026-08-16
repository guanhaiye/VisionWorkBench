namespace VisionWorkbench.Cameras.Abstractions;

/// <summary>
/// 统一视频帧：BGR 24bit 连续字节缓冲（文档 §10.4）。
/// 抽象层不依赖任何具体相机库，便于虚拟相机与单元测试。
/// </summary>
public sealed class VideoFrame
{
    /// <summary>帧序号（会话内单调递增）。</summary>
    public long Sequence { get; }

    /// <summary>采集时间戳。</summary>
    public DateTimeOffset Timestamp { get; }

    public int Width { get; }
    public int Height { get; }

    /// <summary>BGR24 像素数据，长度 = Stride * Height。</summary>
    public byte[] Pixels { get; }

    public int Stride => Width * 3;

    public VideoFrame(long sequence, DateTimeOffset timestamp, int width, int height, byte[] bgr24)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        if (bgr24.Length != width * height * 3)
        {
            throw new ArgumentException("像素缓冲长度必须等于 width*height*3（BGR24）");
        }
        Sequence = sequence;
        Timestamp = timestamp;
        Width = width;
        Height = height;
        Pixels = bgr24;
    }
}
