// =====================================================================================================================
// Module: RentalManagement.Core / Services / QueryableOrderingExtensions.cs
// Purpose: OrderBy/ThenBy overloads that take the direction as a flag, so a sort can be composed on an IQueryable
//          (and translated to SQL ORDER BY) without writing every column twice.
// =====================================================================================================================

using System.Linq.Expressions;

namespace RentalManagement.Core.Services;

/// <summary>Direction-aware ordering on <see cref="IQueryable{T}"/>; the expressions are translated by EF Core.</summary>
internal static class QueryableOrderingExtensions
{
    /// <summary>OrderBy or OrderByDescending.</summary>
    public static IOrderedQueryable<T> OrderBy<T, TKey>(
        this IQueryable<T> source, Expression<Func<T, TKey>> key, bool descending) =>
        descending ? source.OrderByDescending(key) : source.OrderBy(key);

    /// <summary>ThenBy or ThenByDescending.</summary>
    public static IOrderedQueryable<T> ThenBy<T, TKey>(
        this IOrderedQueryable<T> source, Expression<Func<T, TKey>> key, bool descending) =>
        descending ? source.ThenByDescending(key) : source.ThenBy(key);
}
