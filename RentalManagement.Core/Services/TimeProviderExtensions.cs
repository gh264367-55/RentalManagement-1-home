// =====================================================================================================================
// Module: RentalManagement.Core / Services / TimeProviderExtensions.cs
// Purpose: Calendar helpers on the injected TimeProvider, so business rules get "today" from one testable place
//          instead of calling DateTime.Now.
// =====================================================================================================================

namespace RentalManagement.Core.Services;

/// <summary>Calendar helpers on <see cref="TimeProvider"/>.</summary>
public static class TimeProviderExtensions
{
    /// <summary>
    /// The business day in the server's local time zone. Lease terms and availability are calendar dates, so "today"
    /// is the local date rather than the UTC date.
    /// </summary>
    /// <param name="clock">The clock (the system clock in production, a fake one in tests).</param>
    /// <returns>Today's date in the server's local time zone.</returns>
    public static DateOnly GetToday(this TimeProvider clock) => DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
}
