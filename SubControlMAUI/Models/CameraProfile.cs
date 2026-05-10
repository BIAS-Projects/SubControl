using static SubControlMAUI.ViewModels.VideoConfigViewModel;

namespace SubControlMAUI.Models;

public class CameraProfile
{
    public string StreamPathName { get; init; } = "";
    public FfmpegCameraOptions FfmpegOptions { get; init; } = new();
    public MediaMtxPathConfig MtxConfig { get; init; } = new();
}