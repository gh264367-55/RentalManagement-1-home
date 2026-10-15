// =====================================================================================================================
// Module: RentalManagement.Web / Controllers / HomeController.cs
// Purpose: Public pages that need no sign-in: the landing page (with role-specific shortcuts), the privacy page, and
//          the generic error page used by the exception handler outside Development.
// =====================================================================================================================

using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RentalManagement.Web.Models;

namespace RentalManagement.Web.Controllers;

/// <summary>Public landing, privacy and error pages. Opts out of the sign-in-required fallback policy.</summary>
[AllowAnonymous]
public class HomeController : Controller
{
    /// <summary>The landing page.</summary>
    /// <returns>The home view.</returns>
    public IActionResult Index()
    {
        return View();
    }

    /// <summary>The privacy page.</summary>
    /// <returns>The privacy view.</returns>
    public IActionResult Privacy()
    {
        return View();
    }

    /// <summary>
    /// The page shown for an error status that has no body of its own (re-executed by the status-code-pages
    /// middleware). The original status code is kept on the response.
    /// </summary>
    /// <param name="code">The HTTP status code, e.g. 404.</param>
    /// <returns>The status view.</returns>
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Status(int code)
    {
        ViewData["StatusCode"] = code;
        return View();
    }

    /// <summary>The friendly error page; never cached, and shows the request id for support.</summary>
    /// <returns>The error view.</returns>
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
