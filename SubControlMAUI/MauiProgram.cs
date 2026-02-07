using CommunityToolkit.Maui;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using SubControlMAUI.Services;
using SubControlMAUI.ViewModels;

namespace SubControlMAUI
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();
            builder
                .UseMauiApp<App>()
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
            builder.Services.AddSingleton<ViewModels.MainViewModel>();

            builder.Services.AddSingleton<Pages.SettingsPage>();
            builder.Services.AddSingleton<ViewModels.SettingsViewModel>();

            builder.Services.AddSingleton<Pages.Comms>();
            builder.Services.AddSingleton<ViewModels.CommsViewModel>();

            builder.Services.AddSingleton<AppShellViewModel>();
            builder.Services.AddSingleton<AppShell>();


            builder.Services.AddSingleton<SQLiteService>();

            builder.Services.AddSingleton<IAlertService, AlertService>();

            builder.Services.AddSingleton<TcpSocketService>();
            builder.Services.AddSingleton<IMessenger>(WeakReferenceMessenger.Default);

            return builder.Build();
        }
    }
}
