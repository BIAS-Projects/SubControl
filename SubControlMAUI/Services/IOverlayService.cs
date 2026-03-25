using WebCam.Models;
using WebCam.Models;

namespace SubConsole.Services;

public interface IOverlayService
{
    IList<OverlayItem> Overlays { get; }
    void AddOverlay(OverlayItem item);
    OverlayItem HitTest(float x, float y);
}