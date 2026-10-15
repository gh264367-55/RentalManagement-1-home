// =====================================================================================================================
// Module: RentalManagement.Core / Services / PagedResult.cs
// Purpose: One page of a list query: the rows of the requested page plus the total number of rows that matched the
//          filters (counted before paging), from which the page count is derived.
// =====================================================================================================================

namespace RentalManagement.Core.Services;

/// <summary>One page of a filtered, sorted list.</summary>
/// <typeparam name="T">The row type.</typeparam>
/// <param name="Items">The rows of this page; empty when the page is beyond the last one.</param>
/// <param name="Page">The 1-based page number that was requested.</param>
/// <param name="PageSize">The maximum number of rows per page.</param>
/// <param name="TotalCount">How many rows matched the filters, before paging.</param>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)
{
    /// <summary>The number of pages needed for <see cref="TotalCount"/> rows; 0 when nothing matched.</summary>
    public int TotalPages => PageSize <= 0 ? 0 : (int)((TotalCount + (long)PageSize - 1) / PageSize);
}
