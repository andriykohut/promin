using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Educast.Master.Pages;

/// <summary>The Rename and Remove dialogs, the same on Set up devices and on the room wall.</summary>
internal static class RoomDialogs
{
    /// <summary>Asks for a new name. Null when cancelled.</summary>
    public static async Task<string?> RenameAsync(XamlRoot xamlRoot, string currentName)
    {
        var box = new TextBox { MaxLength = 64, Text = currentName };
        box.SelectAll();
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = $"Rename {currentName}",
            Content = box,
            PrimaryButtonText = "Rename",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Text : null;
    }

    /// <summary>Asks whether to remove the PC from the room.</summary>
    public static async Task<bool> ConfirmRemoveAsync(XamlRoot xamlRoot, string name)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = $"Remove {name} from this room?",
            Content = "It stops receiving your broadcasts. To add it back you will need the room code.",
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
