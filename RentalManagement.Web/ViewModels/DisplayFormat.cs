// =====================================================================================================================
// Module: RentalManagement.Web / ViewModels / DisplayFormat.cs
// Purpose: Small formatting helpers shared by several views, so the same value reads the same way everywhere.
// =====================================================================================================================

namespace RentalManagement.Web.ViewModels;

/// <summary>Small display helpers shared by views.</summary>
public static class DisplayFormat
{
    /// <summary>A bedroom count as text: "Studio", "1 bedroom", "3 bedrooms".</summary>
    /// <param name="bedrooms">The number of bedrooms (0 for a studio).</param>
    /// <returns>The display text.</returns>
    public static string Bedrooms(int bedrooms) => bedrooms switch
    {
        0 => "Studio",
        1 => "1 bedroom",
        _ => $"{bedrooms} bedrooms",
    };
}
