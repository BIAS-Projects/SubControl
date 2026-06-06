using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;
using Microsoft.UI.Xaml.Controls;

namespace SubControlMAUI.Platforms.Windows
{
    public class CustomPickerHandler : PickerHandler
    {
        protected override void ConnectHandler(ComboBox platformView)
        {
            base.ConnectHandler(platformView);
            ApplyTheme(platformView);

            // Re-apply when theme changes
            platformView.ActualThemeChanged += (s, e) => ApplyTheme(platformView);
        }

        private void ApplyTheme(ComboBox comboBox)
        {
            var isDark = Application.Current?.RequestedTheme == AppTheme.Dark;

            var bgColor = isDark
                ? Color.FromArgb("#ac99ea")
                : Colors.White;

            var textColor = isDark
                ? Color.FromArgb("##242424")
                : Color.FromArgb("#212121");


            // --- Selected item colours ---
            comboBox.Resources["ComboBoxItemBackgroundSelected"] =
                new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    (isDark ? Color.FromArgb("#ACACAC") : Color.FromArgb("#ACACAC"))
                    .ToWindowsColor());

            comboBox.Resources["ComboBoxItemForegroundSelected"] =
                new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    (isDark ? Color.FromArgb("#2B0B98") : Color.FromArgb("#512BD4"))
                    .ToWindowsColor());


            comboBox.Resources["ComboBoxDropDownBackground"] =
                new Microsoft.UI.Xaml.Media.SolidColorBrush(bgColor.ToWindowsColor());

            comboBox.Resources["ComboBoxItemForeground"] =
                new Microsoft.UI.Xaml.Media.SolidColorBrush(textColor.ToWindowsColor());

            // Selected + hovered


            //comboBox.Resources["ComboBoxItemForegroundSelectedPointerOver"] =
            //    new Microsoft.UI.Xaml.Media.SolidColorBrush(
            //        Color.FromArgb("#YOUR_SELECTED_TEXT").ToWindowsColor());

            comboBox.Resources["ComboBoxItemBackgroundPointerOver"] =
                new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    (isDark ? Color.FromArgb("#DFD8F7") : Color.FromArgb("#E1E1E1"))
                    .ToWindowsColor());


            //// Selected + hovered
            //comboBox.Resources["ComboBoxItemBackgroundSelectedPointerOver"] =
            //    new Microsoft.UI.Xaml.Media.SolidColorBrush(
            //        Color.FromArgb("#YOUR_SELECTED_HOVER_BG").ToWindowsColor());


            //// Selected + pressed
            //comboBox.Resources["ComboBoxItemBackgroundSelectedPressed"] =
            //    new Microsoft.UI.Xaml.Media.SolidColorBrush(
            //        Color.FromArgb("#YOUR_SELECTED_PRESSED_BG").ToWindowsColor());
        }
    }
}