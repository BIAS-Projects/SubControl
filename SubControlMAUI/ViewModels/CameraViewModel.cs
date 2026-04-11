using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
//using HomeKit;
using System.Collections.ObjectModel;
using SubControlMAUI.Services;
using static SubControlMAUI.Services.CameraStream;

namespace SubControlMAUI.ViewModels;

public partial class CameraViewModel : ObservableObject, IAsyncDisposable
{
    [ObservableProperty]
    private CameraStream? _activeStream;


    public ObservableCollection<CameraStream> AvailableStreams { get; } = new();

    public CameraViewModel()
    {




        // Initialize the 3 specific ports
        AvailableStreams.Add(new CameraStream("Cam1", "127.0.0.1", 5001));
        AvailableStreams.Add(new CameraStream("Cam2", "127.0.0.1", 5002));
        AvailableStreams.Add(new CameraStream("Cam3", "127.0.0.1", 5003));

        // Default to the first one
        ActiveStream = AvailableStreams[0];

        Task.Run(async () => await StartAllStreams());
    }

    [RelayCommand]
    private async Task StartAllStreams()
    {
        foreach (var stream in AvailableStreams)
        {
            // This ensures the background FFmpeg workers for 5001, 5002, and 5003 
            // are ALL running and decoding, even if they aren't being displayed.
            await stream.StartAsync();
        }
    }

    [RelayCommand]
    private void SwitchStream(CameraStream selectedStream)
    {
        if (selectedStream != null)
            ActiveStream = selectedStream;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var stream in AvailableStreams)
        {
            await stream.DisposeAsync();
        }
    }
}