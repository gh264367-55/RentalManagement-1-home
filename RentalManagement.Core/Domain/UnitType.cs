// =====================================================================================================================
// Module: RentalManagement.Core / Domain / UnitType.cs
// Purpose: The Unit Type lookup (Studio, Apartment, …) with an Active flag, and the rule that an inactive type may
//          stay on the unit that already uses it but may not be assigned to any other unit.
// =====================================================================================================================

namespace RentalManagement.Core.Domain;

/// <summary>
/// Lookup of unit types. An inactive type stays on units that already use it but cannot be assigned to any other
/// unit (see <see cref="CanBeAssignedTo"/>).
/// </summary>
public class UnitType
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool IsActive { get; set; } = true;

    public int SortOrder { get; set; }

    /// <summary>
    /// True when this type may be saved on a unit whose <b>persisted</b> type id is
    /// <paramref name="currentUnitTypeId"/> (null for a new unit). Always pass the stored value, never one from the browser.
    /// </summary>
    public bool CanBeAssignedTo(int? currentUnitTypeId) => IsActive || currentUnitTypeId == Id;
}
