using CommunityToolkit.Maui.Views;

namespace SubControlMAUI.Popups;

public partial class PasswordPopup : Popup
{
    public string? EnteredPassword { get; private set; }

    public PasswordPopup()
    {
        InitializeComponent();
        Opened += OnPopupOpened;
    }

    private async void OnPopupOpened(object? sender, EventArgs e)
    {
        PasswordEntry.Text = "";
       // await Task.Delay(100);
        PasswordEntry.Focus();
    }

    private async void OnOkClicked(object sender, EventArgs e)
    {
        EnteredPassword = PasswordEntry.Text;
        await CloseAsync();
    }

    private async void OnCancelClicked(object sender, EventArgs e)
    {
        EnteredPassword = null;
        await CloseAsync();
    }
}