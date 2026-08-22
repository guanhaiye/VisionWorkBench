using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VisionWorkbench.Cameras.Abstractions;

namespace VisionWorkbench.App;

/// <summary>VideoFrame(BGR24) → WriteableBitmap(Bgr32) 预览渲染。UI 线程调用。</summary>
public sealed class PreviewRenderer
{
    private WriteableBitmap? _bitmap;
    private byte[]? _buffer;
    private DateTimeOffset _lastRender = DateTimeOffset.MinValue;
    private long _renderedFrames;
    private DateTimeOffset _fpsWindowStart = DateTimeOffset.UtcNow;
    private int _fpsWindowFrames;
    private double _fps;

    public double Fps => _fps;
    public long RenderedFrames => _renderedFrames;

    /// <summary>写入目标 Image 控件；返回是否渲染（节流跳过返回 false）。</summary>
    public bool Render(System.Windows.Controls.Image image, VideoFrame frame, int maxFps = 30, bool force = false)
    {
        var now = DateTimeOffset.UtcNow;
        var minInterval = TimeSpan.FromMilliseconds(1000.0 / Math.Max(1, maxFps));
        if (!force && now - _lastRender < minInterval)
        {
            return false;
        }
        _lastRender = now;
        _renderedFrames++;

        _fpsWindowFrames++;
        if (now - _fpsWindowStart >= TimeSpan.FromSeconds(1))
        {
            _fps = _fpsWindowFrames / (now - _fpsWindowStart).TotalSeconds;
            _fpsWindowStart = now;
            _fpsWindowFrames = 0;
        }

        if (_bitmap is null || _bitmap.PixelWidth != frame.Width || _bitmap.PixelHeight != frame.Height)
        {
            _bitmap = new WriteableBitmap(
                frame.Width, frame.Height, 96, 96, PixelFormats.Bgr32, null);
            image.Source = _bitmap;
        }

        // BGR24 → BGR32（补 1 字节 alpha/x）
        var needed = frame.Width * frame.Height * 4;
        if (_buffer is null || _buffer.Length != needed)
        {
            _buffer = new byte[needed];
        }
        var src = frame.Pixels;
        var w = frame.Width;
        var h = frame.Height;
        for (var i = 0; i < w * h; i++)
        {
            _buffer[i * 4] = src[i * 3];
            _buffer[i * 4 + 1] = src[i * 3 + 1];
            _buffer[i * 4 + 2] = src[i * 3 + 2];
            _buffer[i * 4 + 3] = 255;
        }
        _bitmap.WritePixels(new Int32Rect(0, 0, w, h), _buffer, w * 4, 0);
        return true;
    }
}
