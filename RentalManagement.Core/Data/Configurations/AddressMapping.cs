// =====================================================================================================================
// Module: RentalManagement.Core / Data / Configurations / AddressMapping.cs
// Purpose: The one place that defines how an owned Address is stored: column names (with an optional prefix such as
//          "Current"), maximum lengths and required-ness. Used by the Property, Residence and RentalApplication
//          configurations so every address column looks the same.
// =====================================================================================================================

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RentalManagement.Core.Domain;

namespace RentalManagement.Core.Data.Configurations;

/// <summary>Shared column mapping for the owned <see cref="Address"/> type.</summary>
internal static class AddressMapping
{
    public const int StreetLength = 200;
    public const int CityLength = 100;
    public const int StateLength = 50;
    public const int PostalCodeLength = 20;

    /// <summary>Maps the address to columns named <c>{prefix}Street</c>, <c>{prefix}City</c>, … on the owner's table.</summary>
    /// <typeparam name="TOwner">The entity that owns the address.</typeparam>
    /// <param name="address">The owned-navigation builder for the address.</param>
    /// <param name="prefix">Column name prefix; empty for none.</param>
    public static void Map<TOwner>(OwnedNavigationBuilder<TOwner, Address> address, string prefix)
        where TOwner : class
    {
        address.Property(a => a.Street).HasColumnName($"{prefix}Street").HasMaxLength(StreetLength).IsRequired();
        address.Property(a => a.City).HasColumnName($"{prefix}City").HasMaxLength(CityLength).IsRequired();
        address.Property(a => a.State).HasColumnName($"{prefix}State").HasMaxLength(StateLength).IsRequired();
        address.Property(a => a.PostalCode).HasColumnName($"{prefix}PostalCode").HasMaxLength(PostalCodeLength).IsRequired();
    }
}
