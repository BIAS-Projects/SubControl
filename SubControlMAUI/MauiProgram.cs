using CommunityToolkit.Maui;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using SkiaSharp.Views.Maui.Controls.Hosting;
using SubControlMAUI.Pages;
using SubControlMAUI.Services;
using SubControlMAUI.ViewModels;

namespace SubControlMAUI
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {

            // Call this FIRST before any FFmpeg service is registered or resolved
            RtspFrameDecoder.InitialiseFfmpeg();

            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
                .UseSkiaSharp()
                .UseMauiCommunityToolkit()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                    fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
                });

#if DEBUG
    		builder.Logging.AddDebug();
#endif
            builder.Services.AddSingleton<Pages.MainPage>();
            builder.Services.AddSingleton<MainViewModel>();

            builder.Services.AddSingleton<ConfigMenuPage>();
            builder.Services.AddSingleton<ConfigMenuViewModel>();

            builder.Services.AddSingleton<SettingsPage>();
            builder.Services.AddSingleton<SettingsViewModel>();

            builder.Services.AddSingleton<Comms>();
            builder.Services.AddSingleton<CommsViewModel>();

            builder.Services.AddSingleton<AppShell>();
          //  builder.Services.AddSingleton<AppShellViewModel>();

            builder.Services.AddSingleton<CutterCommandSettingsPage>();
            builder.Services.AddSingleton<CutterCommandViewModel>();

            builder.Services.AddSingleton<I2CSettingsPage>();
            builder.Services.AddSingleton<I2CSettingsViewModel>();

            builder.Services.AddSingleton<RS232SettingsPage>();
            builder.Services.AddSingleton<RS232SettingsViewModel>();

            builder.Services.AddSingleton<PeriscopeCommandSettingsPage>();
            builder.Services.AddSingleton<PeriscopeCommandViewModel>();

            builder.Services.AddSingleton<FeatureOptionsPage>();
            builder.Services.AddSingleton<FeatureOptionViewModel>();

            builder.Services.AddSingleton<PeriscopePage>();
            builder.Services.AddSingleton<PeriscopeViewModel>();

            builder.Services.AddSingleton<VideoConfigPage>();
            builder.Services.AddSingleton<VideoConfigViewModel>();

            builder.Services.AddSingleton<TechPage>();
            builder.Services.AddSingleton<TechViewModel>();

            builder.Services.AddSingleton<RotorPage>();
            builder.Services.AddSingleton<RotorViewModel>();


            builder.Services.AddSingleton<SQLiteService>();

            builder.Services.AddSingleton<IAlertService, AlertService>();

            builder.Services.AddSingleton<TcpSocketService>();
            builder.Services.AddSingleton<IMessenger>(WeakReferenceMessenger.Default);

            builder.Services.AddSingleton<IRtspFrameDecoder, RtspFrameDecoder>();

            builder.Services.AddSingleton<INavigationService, NavigationService>();

            builder.Services.AddSingleton<ApplicationStateService>();

            return builder.Build();
        }


    }
}
