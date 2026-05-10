using System;
using System.Collections.Generic;
using System.Text;

namespace SubControlMAUI.Services
{
    public interface INavigationService
    {
        Task GoToAsync(string route);
        Task GoBackAsync();
    }

    public class NavigationService : INavigationService
    {
        public async Task GoToAsync(string route)
        {
            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                if (Shell.Current is not null)
                    await Shell.Current.GoToAsync(route);
            });
        }

        public async Task GoBackAsync()
        {
            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                if (Shell.Current is not null)
                    await Shell.Current.GoToAsync("..");
            });
        }

        public async Task GoToRootAsync()
        {
            await MainThread.InvokeOnMainThreadAsync(async () =>
            {
                if (Shell.Current is not null)
                    await Shell.Current.GoToAsync("//MainPage");
            });
        }
    }
}
