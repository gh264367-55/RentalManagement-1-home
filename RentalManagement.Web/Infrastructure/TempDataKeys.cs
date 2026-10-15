// =====================================================================================================================
// Module: RentalManagement.Web / Infrastructure / TempDataKeys.cs
// Purpose: Names of the TempData entries used for one-time success/error messages that survive a redirect
//          (Post/Redirect/Get) and are rendered by Views/Shared/_Alerts.cshtml.
// =====================================================================================================================

namespace RentalManagement.Web.Infrastructure;

/// <summary>TempData keys for one-time messages shown by <c>_Alerts</c> after a redirect.</summary>
public static class TempDataKeys
{
    /// <summary>A green confirmation, e.g. "Your application was submitted for review."</summary>
    public const string Success = "Alert.Success";

    /// <summary>A red error, e.g. "That unit is leased and not available for applications."</summary>
    public const string Error = "Alert.Error";
}
