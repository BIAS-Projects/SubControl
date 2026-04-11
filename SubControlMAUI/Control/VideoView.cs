//using HomeKit;
using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;
using SubControlMAUI.Services;

namespace SubControlMAUI.Controls;

public class VideoView : SKCanvasView
{
    public static readonly BindableProperty StreamProperty =
        BindableProperty.Create(nameof(Stream), typeof(CameraStream), typeof(VideoView), null, propertyChanged: OnStreamChanged);

    public CameraStream? Stream
    {
        get => (CameraStream?)GetValue(StreamProperty);
        set => SetValue(StreamProperty, value);
    }

    protected VideoFrame? _lastFrame;
    protected readonly object _drawLock = new();

    private static void OnStreamChanged(BindableObject bindable, object oldValue, object newValue)
    {
        if (bindable is not VideoView control) return;

        if (oldValue is CameraStream oldStream) oldStream.OnFrame -= control.HandleNewFrame;

        // CRITICAL: Clear the old frame so the user knows the stream is switching
        lock (control._drawLock)
        {
            control._lastFrame?.Bitmap?.Dispose();
            control._lastFrame = null;
        }
        control.InvalidateSurface(); // Forces the screen to go black/grey immediately

        if (newValue is CameraStream newStream) newStream.OnFrame += control.HandleNewFrame;
    }

    private void HandleNewFrame(VideoFrame frame)
    {
        lock (_drawLock)
        {
            // Dispose the old frame copy we were holding
            _lastFrame?.Bitmap?.Dispose();
            _lastFrame = frame;
        }

        // Trigger a redraw on the UI Thread
        MainThread.BeginInvokeOnMainThread(InvalidateSurface);
    }

    protected override void OnPaintSurface(SKPaintSurfaceEventArgs e)
    {
        base.OnPaintSurface(e);
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Black);

        lock (_drawLock)
        {
            if (_lastFrame?.Bitmap == null) return;

            var size = _lastFrame.Bitmap.Info.Size;
            var dest = e.Info.Rect;
            float scale = Math.Min((float)dest.Width / size.Width,
                                   (float)dest.Height / size.Height);
            float w = size.Width * scale;
            float h = size.Height * scale;
            float x = (dest.Width - w) / 2;
            float y = (dest.Height - h) / 2;

            DrawFrame(canvas, _lastFrame.Bitmap, new SKRect(x, y, x + w, y + h));
        }
    }

    // Subclasses override this instead of OnPaintSurface
    protected virtual void DrawFrame(SKCanvas canvas, SKBitmap bitmap, SKRect dest)
    {
        canvas.DrawBitmap(bitmap, dest);
    }
}