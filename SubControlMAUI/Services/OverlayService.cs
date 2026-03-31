//using WebCam.Models;
//using SkiaSharp;
//using WebCam.Models;

//namespace WebCam.Services;

//public class OverlayService : IOverlayService
//{
//    public IList<OverlayItem> Overlays { get; } = new List<OverlayItem>();

//    public void AddOverlay(OverlayItem item)
//    {
//        Overlays.Add(item);
//    }

//    public OverlayItem HitTest(float x, float y)
//    {
//        var point = new SKPoint(x, y);

//        return Overlays.LastOrDefault(o => o.Contains(point));
//    }
//}