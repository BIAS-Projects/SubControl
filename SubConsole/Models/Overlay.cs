//using SkiaSharp;


//namespace SubConsole.Models;

//public class OverlayItem
//{
//    public SKBitmap Bitmap { get; set; }
//    public SKPoint Position { get; set; } = new SKPoint(100, 100);
//    public float Opacity { get; set; } = 1f;
//    public SKColor TintColor { get; set; } = SKColors.White;

//    public float Width => Bitmap?.Width ?? 0;
//    public float Height => Bitmap?.Height ?? 0;

//    public float Scale { get; set; } = 1f; // 1.0 = 100%, 0.5 = 50%

//    public bool Contains(SKPoint point)
//    {
//        return point.X >= Position.X &&
//               point.X <= Position.X + Width &&
//               point.Y >= Position.Y &&
//               point.Y <= Position.Y + Height;

//}
//}