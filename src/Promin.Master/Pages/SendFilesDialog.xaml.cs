using System.Diagnostics;
using Promin.Master.Core;
using Promin.Master.Core.Sending;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;

namespace Promin.Master.Pages;

/// <summary>
/// The send window, bound to a <see cref="SendDialogModel"/>. Files and folders come from the stock pickers; Send
/// builds <see cref="Package"/> and closes the window, unless a picked item cannot be read, when the window says so
/// and stays open with the list to fix.
/// </summary>
public sealed partial class SendFilesDialog : ContentDialog
{
    public SendFilesDialog(SendTargets targets)
    {
        Model = new SendDialogModel(targets);
        InitializeComponent();
        To.SelectedIndex = Model.ToAll ? 1 : 0;

        // A changed list may no longer hold the item Send could not read.
        Model.Items.CollectionChanged += (_, _) => SourceError.IsOpen = false;
    }

    public SendDialogModel Model { get; }

    /// <summary>What Send built; null until Send succeeded.</summary>
    public SendPackage? Package { get; private set; }

    // The bindings' helpers.

    public Visibility Not(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public Visibility NotEmpty(string value) => string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility IsFile(bool isFolder) => isFolder ? Visibility.Collapsed : Visibility.Visible;

    public static string RowName(string name, string sizeText) => $"{name}, {sizeText}";

    public static string RemoveName(string name) => Phrase.Format(Strings.Send_RemoveName, ("name", name));

    // The pickers open over the window the dialog is in.
    private WindowId WindowId => XamlRoot.ContentIslandEnvironment.AppWindowId;

    private async void AddFiles_Click(object sender, RoutedEventArgs e) => await Guarded(async () =>
    {
        var picker = new FileOpenPicker(WindowId);
        picker.FileTypeFilter.Add("*");
        var picked = await picker.PickMultipleFilesAsync();
        Model.AddFiles(picked.Select(f => f.Path));
    });

    private async void AddFolder_Click(object sender, RoutedEventArgs e) => await Guarded(async () =>
    {
        if (await new FolderPicker(WindowId).PickSingleFolderAsync() is { } folder)
        {
            Model.AddFolder(folder.Path);
        }
    });

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is SendListItem item)
        {
            Model.Remove(item);
        }
    }

    private void To_SelectionChanged(object sender, SelectionChangedEventArgs e) => Model.ToAll = To.SelectedIndex == 1;

    private void Open_Click(object sender, RoutedEventArgs e) => Model.OpenOnArrival = ((CheckBox)sender).IsChecked == true;

    private void Send_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        SourceError.IsOpen = false;
        try
        {
            Package = SendPackage.From(Model.Picked);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // From does not say which pick failed, so each is sized again to name it; one that reads again by then is named by the exception.
            var bad = Model.Items.FirstOrDefault(i => !Readable(i.Picked));
            SourceError.Message = bad is null ? e.Message : Phrase.Format(Strings.SendStrip_SourceText, ("file", bad.Name));
            SourceError.IsOpen = true;
            args.Cancel = true;
        }
    }

    private static bool Readable(PickedItem picked)
    {
        try
        {
            SendPackage.SizeOf(picked);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Runs a handler's work so that nothing it throws reaches the dispatcher.</summary>
    private static async Task Guarded(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch (Exception e)
        {
            Trace.TraceError("Send files: a handler failed unexpectedly: {0}", e);
        }
    }
}
