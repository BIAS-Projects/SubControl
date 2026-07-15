using SubControlMAUI.ViewModels;

namespace SubControlMAUI.Pages;

public partial class LogsPage : ContentPage
{
    readonly LogsViewModel _viewModel;

    public LogsPage(LogsViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        BindingContext = viewModel;

        // The Editor uses AutoSize="TextChanges" so it grows to fit all of its
        // text instead of scrolling internally; the outer ScrollView does the
        // scrolling instead. SizeChanged fires once the Editor has actually
        // finished resizing to new content (TextChanged fires too early, before
        // layout catches up), so it's the reliable point to scroll to the
        // bottom. Same pattern as ConsolePage's terminal output.
        TailOutputEditor.SizeChanged += async (_, _) =>
        {
            await TailScrollView.ScrollToAsync(TailOutputEditor, ScrollToPosition.End, false);
        };
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _viewModel.OnAppearing();
    }

    protected override async void OnDisappearing()
    {
        base.OnDisappearing();

        // Guard against leaving an SSH session dangling if the user swipes back
        // or the shell navigates away without going through GoBackCommand.
        // Same pattern as ConsolePage: only disconnect if we're actually
        // connected, then unconditionally dispose to unsubscribe from
        // SshService and clear the tail buffer regardless.
        if (_viewModel.IsConnected && _viewModel.DisconnectCommand.CanExecute(null))
        {
            await _viewModel.DisconnectCommand.ExecuteAsync(null);
        }

        _viewModel.Dispose();
    }
}