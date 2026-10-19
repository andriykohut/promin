using System.Globalization;
using System.Xml.Linq;
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace Promin.Agent;

/// <summary>The Windows notification that tells the person files arrived and where they are.</summary>
internal static class ReceivedNotice
{
    private const string AppId = "Promin.Agent";

    /// <summary>
    /// Shows the notification as the current user, with a button that opens <paramref name="prominFolder"/>.
    /// Registers the app identity first, because an unpackaged app has no name or icon for its notifications otherwise.
    /// </summary>
    public static void Show(int count, string prominFolder)
    {
        using (var identity = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{AppId}"))
        {
            identity.SetValue("DisplayName", "Promin");
            identity.SetValue("IconUri", Path.Combine(AppContext.BaseDirectory, "Beacon.png"));
        }

        var one = count == 1;
        var toast = new XElement("toast",
            new XElement("visual",
                new XElement("binding", new XAttribute("template", "ToastGeneric"),
                    new XElement("text", one ? Strings.Notice_OneArrived : string.Format(CultureInfo.CurrentCulture, Strings.Notice_ManyArrived, count)),
                    new XElement("text", one ? Strings.Notice_WhereOne : Strings.Notice_WhereMany))),
            new XElement("actions",
                new XElement("action",
                    new XAttribute("content", Strings.Notice_OpenFolder),
                    new XAttribute("activationType", "protocol"),
                    new XAttribute("arguments", new Uri(prominFolder).AbsoluteUri))));

        var document = new XmlDocument();
        document.LoadXml(toast.ToString(SaveOptions.DisableFormatting));
        ToastNotificationManager.CreateToastNotifier(AppId).Show(new ToastNotification(document));
    }
}
