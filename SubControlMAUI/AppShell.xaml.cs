

namespace SubControlMAUI
{
    public partial class AppShell : Shell
    {
        public AppShell()
        {
            InitializeComponent();
        }

        protected override async void OnAppearing()
        {
            base.OnAppearing();
            this.Window.MinimumHeight = 650;
            this.Window.MinimumWidth = 600;


        }
    }
}
