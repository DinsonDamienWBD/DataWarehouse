using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;

namespace DataWarehouse.Dashboard.Models;

/// <summary>
/// Query parameters for paginated requests.
/// </summary>
public class PaginationQuery
{
    private const int DefaultPageSize = 25;
    private const int MaxPageSize = 100;

    private int _page = 1;
    private int _pageSize = DefaultPageSize;

    /// <summary>
    /// Gets or sets the page number (1-based).
    /// </summary>
    [FromQuery(Name = "page")]
    [Range(1, int.MaxValue, ErrorMessage = "Page must be at least 1.")]
    public int Page
    {
        get => _page;
        set => _page = Math.Max(1, value);
    }

    /// <summary>
    /// Gets or sets the number of items per page.
    /// </summary>
    [FromQuery(Name = "pageSize")]
    [Range(1, MaxPageSize, ErrorMessage = "Page size must be between 1 and 100.")]
    public int PageSize
    {
        get => _pageSize;
        set => _pageSize = Math.Clamp(value, 1, MaxPageSize);
    }

    /// <summary>
    /// Gets or sets the sort field.
    /// </summary>
    [FromQuery(Name = "sortBy")]
    [StringLength(50)]
    public string? SortBy { get; set; }

    /// <summary>
    /// Gets or sets the sort direction (asc/desc).
    /// </summary>
    [FromQuery(Name = "sortDir")]
    [RegularExpression("^(asc|desc)$", ErrorMessage = "Sort direction must be 'asc' or 'desc'.")]
    public string SortDirection { get; set; } = "asc";

    /// <summary>
    /// Gets or sets the search/filter query.
    /// </summary>
    [FromQuery(Name = "q")]
    [StringLength(200)]
    public string? Query { get; set; }

    /// <summary>
    /// Gets the number of items to skip.
    /// </summary>
    public int Skip => (Page - 1) * PageSize;

    /// <summary>
    /// Gets whether the sort direction is descending.
    /// </summary>
    public bool IsDescending => SortDirection.Equals("desc", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Paginated response wrapper.
/// </summary>
/// <typeparam name="T">The type of items in the response.</typeparam>
public class PaginatedResponse<T>
{
    /// <summary>
    /// Gets or sets the items for the current page.
    /// </summary>
    public IReadOnlyList<T> Items { get; init; } = Array.Empty<T>();

    /// <summary>
    /// Gets or sets the current page number.
    /// </summary>
    public int Page { get; init; }

    /// <summary>
    /// Gets or sets the page size.
    /// </summary>
    public int PageSize { get; init; }

    /// <summary>
    /// Gets or sets the total number of items.
    /// </summary>
    public int TotalItems { get; init; }

    /// <summary>
    /// Gets the total number of pages.
    /// </summary>
    public int TotalPages => (int)Math.Ceiling((double)TotalItems / PageSize);

    /// <summary>
    /// Gets whether there is a previous page.
    /// </summary>
    public bool HasPreviousPage => Page > 1;

    /// <summary>
    /// Gets whether there is a next page.
    /// </summary>
    public bool HasNextPage => Page < TotalPages;

    /// <summary>
    /// Gets the first item number on the current page.
    /// </summary>
    public int FirstItemIndex => TotalItems > 0 ? (Page - 1) * PageSize + 1 : 0;

    /// <summary>
    /// Gets the last item number on the current page.
    /// </summary>
    public int LastItemIndex => Math.Min(Page * PageSize, TotalItems);
}

/// <summary>
/// Extension methods for pagination.
/// </summary>
public static class PaginationExtensions
{
    /// <summary>
    /// Applies pagination to a queryable.
    /// </summary>
    public static PaginatedResponse<T> ToPaginated<T>(
        this IEnumerable<T> source,
        PaginationQuery query)
    {
        var items = source.ToList();
        var totalItems = items.Count;

        var pagedItems = items
            .Skip(query.Skip)
            .Take(query.PageSize)
            .ToList();

        return new PaginatedResponse<T>
        {
            Items = pagedItems,
            Page = query.Page,
            PageSize = query.PageSize,
            TotalItems = totalItems
        };
    }

    /// <summary>
    /// Applies sorting to a queryable.
    /// </summary>
    public static IEnumerable<T> ApplySort<T, TKey>(
        this IEnumerable<T> source,
        PaginationQuery query,
        Func<T, TKey> keySelector)
    {
        return query.IsDescending
            ? source.OrderByDescending(keySelector)
            : source.OrderBy(keySelector);
    }

    /// <summary>
    /// Applies filtering to a queryable.
    /// </summary>
    public static IEnumerable<T> ApplyFilter<T>(
        this IEnumerable<T> source,
        PaginationQuery query,
        Func<T, string?, bool> filterPredicate)
    {
        if (string.IsNullOrWhiteSpace(query.Query))
            return source;

        return source.Where(x => filterPredicate(x, query.Query));
    }

    /// <summary>
    /// Creates a paginated response with custom total count (for pre-counted data).
    /// </summary>
    public static PaginatedResponse<T> ToPaginated<T>(
        this IEnumerable<T> items,
        int page,
        int pageSize,
        int totalItems)
    {
        return new PaginatedResponse<T>
        {
            Items = items.ToList(),
            Page = page,
            PageSize = pageSize,
            TotalItems = totalItems
        };
    }

    /// <summary>
    /// Adds pagination headers to the HTTP response.
    /// </summary>
    public static void AddPaginationHeaders<T>(
        this HttpResponse response,
        PaginatedResponse<T> paginatedResponse)
    {
        response.Headers["X-Pagination-Page"] = paginatedResponse.Page.ToString();
        response.Headers["X-Pagination-PageSize"] = paginatedResponse.PageSize.ToString();
        response.Headers["X-Pagination-TotalItems"] = paginatedResponse.TotalItems.ToString();
        response.Headers["X-Pagination-TotalPages"] = paginatedResponse.TotalPages.ToString();
        response.Headers["X-Pagination-HasPrevious"] = paginatedResponse.HasPreviousPage.ToString().ToLowerInvariant();
        response.Headers["X-Pagination-HasNext"] = paginatedResponse.HasNextPage.ToString().ToLowerInvariant();
    }
}

/// <summary>
/// Result type for paginated API endpoints.
/// </summary>
public static class PaginatedResult
{
    /// <summary>
    /// Creates a paginated response action result.
    /// </summary>
    public static ActionResult<PaginatedResponse<T>> Ok<T>(
        ControllerBase controller,
        PaginatedResponse<T> response)
    {
        controller.Response.AddPaginationHeaders(response);
        return controller.Ok(response);
    }
}
