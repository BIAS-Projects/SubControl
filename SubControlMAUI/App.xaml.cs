using SubControlMAUI.Pages;

namespace SubControlMAUI;

public partial class App : Application
{
    private readonly AppShell _appShell;
    public static event Action? ThemeChanged;

    public App(AppShell appShell)
    {
        InitializeComponent();
        _appShell = appShell;
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(_appShell);

        if (Application.Current is not null)
        {
            Application.Current.RequestedThemeChanged += (_, _) =>
            {
                ThemeChanged?.Invoke();
            };
        }

        return window;
    }
}