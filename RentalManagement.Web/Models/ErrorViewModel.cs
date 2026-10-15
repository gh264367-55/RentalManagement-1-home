// =====================================================================================================================
// Module: RentalManagement.Web / Models / ErrorViewModel.cs
// Purpose: Model of the generic error page (Views/Shared/Error.cshtml) shown outside Development. It carries only the
//          request id so a user can quote it and the failure can be found in the logs.
// =====================================================================================================================

namespace RentalManagement.Web.Models;

/// <summary>Model of the generic error page.</summary>
public class ErrorViewModel
{
    /// <summary>The trace id of the failed request, for correlating with the logs.</summary>
    public string? RequestId { get; set; }

    /// <summary>Whether a request id is available to show.</summary>
    public bool ShowRequestId => !string.IsNullOrEmpty(RequestId);
}
