using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SubControlMAUI.Services
{
    public interface IAlertService
    {
        // ----- async calls (use with "await" - MUST BE ON DISPATCHER THREAD) -----
        Task ShowAlertAsync(string title, string message, string cancel = "OK");
        Task<bool> ShowConfirmationAsync(string title, string message, string accept = "Yes", string cancel = "No");

        // ----- "Fire and forget" calls -----
        void ShowAlert(string title, string message, string cancel = "OK");
        /// <param name="callback">Action to perform afterwards.</param>
        void ShowConfirmation(string title, string message, Action<bool> callback,
                              string accept = "Yes", string cancel = "No");
    }


    public class AlertService : IAlertService
    {
        private Page GetCurrentPage() =>
            Application.Current?.Windows.FirstOrDefault()?.Page
            ?? throw new InvalidOperationException("No active page found");

        public Task ShowAlertAsync(string title, string message, string cancel = "OK")
        {
            return MainThread.InvokeOnMainThreadAsync(() =>
                GetCurrentPage().DisplayAlertAsync(title, message, cancel));
        }

        public Task<bool> ShowConfirmationAsync(string title, string message,
            string accept = "Yes", string cancel = "No")
        {
            return MainThread.InvokeOnMainThreadAsync(() =>
                GetCurrentPage().DisplayAlertAsync(title, message, accept, cancel));
        }

        public void ShowAlert(string title, string message, string cancel = "OK")
        {
            MainThread.BeginInvokeOnMainThread(async () =>
                await ShowAlertAsync(title, message, cancel));
        }

        public void ShowConfirmation(string title, string message, Action<bool> callback,
            string accept = "Yes", string cancel = "No")
        {
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                bool answer = await ShowConfirmationAsync(title, message, accept, cancel);
                callback(answer);
            });
        }
    }
}
