using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SubConsole.Models;


namespace SubConsole.Services.Video
{
    public static class CameraServiceExtensions
    {
        public static IServiceCollection AddCameraManager(this IServiceCollection services)
        {
            // Bind MediaMtxSettings from appsettings.json
            services.AddOptions<MediaMtxSettings>()
                .BindConfiguration(MediaMtxSettings.SectionName)
                .ValidateDataAnnotations()
                .ValidateOnStart();

            // Register the named HttpClient.
            // BaseAddress is resolved after options are bound so the value from
            // appsettings is used, not the hardcoded default.
            services.AddHttpClient<MediaMtxClient>("mediamtx", (sp, client) =>
            {
                var settings = sp.GetRequiredService<IOptions<MediaMtxSettings>>().Value;
                client.BaseAddress = settings.BaseAddress;
                client.Timeout = TimeSpan.FromSeconds(10);
            });

            // Registry — singleton so the in-memory dictionaries live for the
            // application lifetime, same as DeviceRegistry.
            services.AddSingleton<ICameraRegistry, CameraRegistry>();

            // Manager — registered as both the hosted service (so BackgroundService
            // lifecycle is managed by the host) and the interface (so other services
            // can inject ICameraManagerService without going through the host).
            services.AddSingleton<CameraManagerService>();
            services.AddSingleton<ICameraManagerService>(
                sp => sp.GetRequiredService<CameraManagerService>());
            services.AddHostedService(
                sp => sp.GetRequiredService<CameraManagerService>());

            return services;
        }
    }
}
