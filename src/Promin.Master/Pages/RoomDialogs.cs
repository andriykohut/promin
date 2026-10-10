using Promin.Master.Core;
using Promin.Master.Core.Rooms;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Promin.Master.Pages;

/// <summary>The Rename and Remove dialogs, the same on Set up devices and on the room wall.</summary>
internal static class RoomDialogs
{
    /// <summary>Asks for a new name. Null when cancelled.</summary>
    public static async Task<string?> RenameAsync(XamlRoot xamlRoot, string currentName)
    {
        var box = new TextBox { MaxLength = RoomMember.MaxNameLength, Text = currentName };
        box.SelectAll();
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = Phrase.Format(Strings.Dialog_RenameTitle, ("name", currentName)),
            Content = box,
            PrimaryButtonText = Strings.Rename,
            CloseButtonText = Strings.Cancel,
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
            Title = Phrase.Format(Strings.Dialog_RemoveTitle, ("name", name)),
            Content = Strings.Dialog_RemoveText,
            PrimaryButtonText = Strings.Remove,
            CloseButtonText = Strings.Cancel,
            DefaultButton = ContentDialogButton.Close,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
