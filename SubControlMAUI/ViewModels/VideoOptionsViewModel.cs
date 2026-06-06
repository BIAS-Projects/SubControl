using CommunityToolkit.Maui.Core;
using CommunityToolkit.Maui.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using SubControlMAUI.Models;
using SubControlMAUI.Services;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SubControlMAUI.ViewModels
{
    public partial class VideoOptionsViewModel : BaseViewModel
    {
        private readonly IMessenger _messenger;
        private readonly ILogger<VideoOptionsViewModel> _logger;
        private readonly ApplicationStateService _applicationStateService;
        private SQLiteService _sqliteService;


        private readonly IFolderPicker _folderPicker;

        public VideoOptionsViewModel(
            IMessenger messenger,
            ILogger<VideoOptionsViewModel> logger,
            ApplicationStateService applicationStateService,
            SQLiteService sqliteService,
            IFolderPicker folderPicker)
        {
            Title = "Video Options";
            _messenger = messenger;
            _logger = logger;
            _applicationStateService = applicationStateService;
            _sqliteService = sqliteService;
            _folderPicker = folderPicker;

            filePath = _applicationStateService.SnapShotPath;
        }

        [ObservableProperty]
    //    [NotifyPropertyChangedFor(nameof(CanSave))]
        private string filePath;


        [ObservableProperty]
        private static string statusText;

        public bool CanSave = true;

 //       public bool CanSave => !String.IsNullOrEmpty(FilePath);

        //// Called automatically by CommunityToolkit whenever MinRotatorValue changes
        //partial void OnMinRotatorValueChanged(int value) => UpdateStatus();

        //// Called automatically by CommunityToolkit whenever MaxRotatorValue changes
        //partial void OnMaxRotatorValueChanged(int value) => UpdateStatus();

        //private void UpdateStatus()
        //{
        //    StatusText = IsMinMaxInvalid
        //        ? "Invalid: Minimum value must be less than Maximum value"
        //        : "Ready";
        //}

        static async Task<bool> ArePermissionsGranted()
        {

                var readPermissionStatus = await Permissions.RequestAsync<Permissions.StorageRead>();
                var writePermissionStatus = await Permissions.RequestAsync<Permissions.StorageWrite>();

                if (readPermissionStatus is PermissionStatus.Granted
                    && writePermissionStatus is PermissionStatus.Granted)
                {
                    return true;
                }

                return false;

        }

        //[RelayCommand]
        //async Task PickFolderStatic(CancellationToken cancellationToken)
        //{
        //    IsBusy = true;
        //    try
        //    {
        //        if (!await ArePermissionsGranted())
        //        {
        //            return;
        //        }

        //        var folderResult = await FolderPicker.PickAsync(FilePath, cancellationToken);
        //        if (folderResult.IsSuccessful)
        //        {
        //            var filesCount = Directory.EnumerateFiles(folderResult.Folder.Path).Count();
        //            StatusText = $"Folder picked: Name - {folderResult.Folder.Name}, Path - {folderResult.Folder.Path}, Files count - {filesCount}";
        //        }
        //        else
        //        {
        //            StatusText = $"Folder is not picked, {folderResult.Exception.Message}";
        //        }

        //    }
        //    catch(Exception ex)
        //    {
        //        StatusText = ex.Message;
        //    }
        //    finally
        //    {
        //        IsBusy = false; 
        //    }
        //}



        [RelayCommand]
        async Task PickFolderInstance(CancellationToken cancellationToken)
        {
            IsBusy = true;
            if (!await ArePermissionsGranted())
            {
                StatusText = "File access permissions not granted";
                return;
            }

            var folderPickerInstance = new FolderPickerImplementation();
            try
            {
                var folderPickerResult = await folderPickerInstance.PickAsync(cancellationToken);
                folderPickerResult.EnsureSuccess();

                FilePath = folderPickerResult.Folder.Path;
            }
            catch (Exception e)
            {
                StatusText = $"Folder is not picked, {e.Message}";
            }
            finally
            { IsBusy = false; }
        }

        //[RelayCommand]
        //async Task PickFolder(CancellationToken cancellationToken)
        //{
        //    if (!await ArePermissionsGranted())
        //    {
        //        return;
        //    }

        //    var initialFolder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        //    var folderPickerResult = await _folderPicker.PickAsync(initialFolder, cancellationToken);
        //    if (folderPickerResult.IsSuccessful)
        //    {
        //        FilePath = folderPickerResult.Folder.Path;
        //    }
        //    else
        //    {
        //        StatusText = $"Folder is not picked, {folderPickerResult.Exception.Message}";
        //    }
        //}


        [RelayCommand]
        private async Task Save()
        {
            IsBusy = true;
            try
            {
                if(String.IsNullOrEmpty(FilePath))
                {
                    StatusText = "The file path is null or empty";
                    return;
                }
                _applicationStateService.SnapShotPath = FilePath;
                _sqliteService.config.SnapShotPath = FilePath;


                if (await _sqliteService.SaveConfigAsync(true) == 1)
                {
                    StatusText = "Settings saved";
                }
                else
                {
                    StatusText = "Error saving settings";
                }


            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private async Task GoBack()
        {
            await Shell.Current.GoToAsync("..");
        }
    }
}