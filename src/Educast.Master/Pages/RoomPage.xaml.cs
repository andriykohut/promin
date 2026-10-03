using Educast.Master.Core.Rooms;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Educast.Master.Pages;

/// <summary>
/// The room wall's page. For now its empty state, or one line with the count; Task 13 builds
/// the wall.
/// </summary>
public sealed partial class RoomPage : Page
{
    public RoomPage()
    {
        ViewModel = new RoomViewModel(App.Services.RoomStore);
        InitializeComponent();
    }

    public RoomViewModel ViewModel { get; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.Activate();
    }

    private void SetUpDevices_Click(object sender, RoutedEventArgs e) => Frame.Navigate(typeof(SetupPage));
}
