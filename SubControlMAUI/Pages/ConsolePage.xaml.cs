using SubControlMAUI.ViewModels;

namespace SubControlMAUI.Pages;

public partial class ConsolePage : ContentPage
{
    readonly ConsoleViewModel viewModel;

    public ConsolePage(ConsoleViewModel viewModel)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        BindingContext = viewModel;

        // The Editor uses AutoSize="TextChanges" so it grows to fit all of its
        // text instead of scrolling internally; the outer ScrollView does the
        // scrolling instead. SizeChanged fires once the Editor has actually
        // finished resizing to the new content (TextChanged fires too early,
        // before layout catches up), so it's the reliable point to scroll to
        // the bottom.
        TerminalOutputEditor.SizeChanged += async (_, _) =>
        {
            await TerminalScrollView.ScrollToAsync(TerminalOutputEditor, ScrollToPosition.End, false);
        };
    }

    protected override async void OnDisappearing()
    {
        base.OnDisappearing();

        // Guard against leaving an SSH session dangling if the user swipes back
        // or the shell navigates away without going through GoBackCommand.
        if (viewModel.IsConnected && viewModel.DisconnectCommand.CanExecute(null))
        {
            await viewModel.DisconnectCommand.ExecuteAsync(null);
        }
    }
}