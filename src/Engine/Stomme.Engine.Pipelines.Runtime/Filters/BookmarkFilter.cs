using System.Collections;
using System.Text;
using Stomme.Engine.Pipelines.Runtime.Entities;

namespace Stomme.Engine.Pipelines.Runtime.Filters;

/// <summary>
/// A filter for bookmarks.
/// </summary>
public class BookmarkFilter
{
    // Cache the properties of BookmarkFilter for performance.
    private static readonly System.Reflection.PropertyInfo[] CachedProperties = typeof(BookmarkFilter).GetProperties();

    /// <summary>
    /// Gets or sets the ID of the bookmark.
    /// </summary>
    public string? BookmarkId { get; set; }

    /// <summary>
    /// Gets or sets the IDs of the bookmark.
    /// </summary>
    public ICollection<string>? BookmarkIds { get; set; }

    /// <summary>
    /// Gets or sets the IDs of the pipeline instance.
    /// </summary>
    public string? PipelineInstanceId { get; set; }

    /// <summary>
    /// Gets or sets the IDs of the pipeline instances.
    /// </summary>
    public ICollection<string>? PipelineInstanceIds { get; set; }

    /// <summary>
    /// Gets or sets the hash of the bookmark to find.
    /// </summary>
    public string? Hash { get; set; }

    /// <summary>
    /// Gets or sets the hashes of the bookmarks to find.
    /// </summary>
    public ICollection<string>? Hashes { get; set; }

    /// <summary>
    /// Gets or sets the correlation ID of the bookmark to find.
    /// </summary>
    public string? CorrelationId { get; set; }

    /// <summary>
    /// Gets or sets the name of the bookmark to find.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets the names of the bookmarks to find.
    /// </summary>
    public ICollection<string>? Names { get; set; }

    /// <summary>
    /// Gets or sets the step instance ID of the bookmark to find.
    /// </summary>
    public string? StepInstanceId { get; set; }

    /// <summary>
    /// Get or sets if the triggers to find is a tenant agnostic search
    /// </summary>
    public bool TenantAgnostic { get; set; }

    /// <summary>
    /// Applies the filter to the specified query.
    /// </summary>
    public IQueryable<StoredBookmark> Apply(IQueryable<StoredBookmark> query)
    {
        var filter = this;
        if (filter.BookmarkId != null) query = query.Where(x => x.Id == filter.BookmarkId);
        if (filter.BookmarkIds != null) query = query.Where(x => filter.BookmarkIds.Contains(x.Id));
        if (filter.CorrelationId != null) query = query.Where(x => x.CorrelationId == filter.CorrelationId);
        if (filter.Hash != null) query = query.Where(x => x.Hash == filter.Hash);
        if (filter.Hashes != null) query = query.Where(x => filter.Hashes.Contains(x.Hash));
        if (filter.PipelineInstanceId != null) query = query.Where(x => x.PipelineInstanceId == filter.PipelineInstanceId);
        if (filter.PipelineInstanceIds != null) query = query.Where(x => filter.PipelineInstanceIds.Contains(x.PipelineInstanceId));
        if (filter.Name != null) query = query.Where(x => x.Name == filter.Name);
        if (filter.Names != null) query = query.Where(x => filter.Names.Contains(x.Name!));
        if (filter.StepInstanceId != null) query = query.Where(x => x.StepInstanceId == filter.StepInstanceId);

        return query;
    }

    public static BookmarkFilter ByStepTypeNames(IEnumerable<string> stepTypeNames) => new()
    {
        Names = stepTypeNames.ToList()
    };

    public string GetHashableString()
    {
        // Return a hashable string representation of the filter, excluding null values.
        var sb = new StringBuilder();
        foreach (var prop in CachedProperties)
        {
            var value = prop.GetValue(this);
            if (value == null)
                continue;

            string valueString;
            // Handle collections (excluding string)
            if (value is IEnumerable enumerable and not string)
            {
                var items = new List<string>();
                foreach (var item in enumerable)
                {
                    if (item != null)
                        items.Add(item.ToString()!);
                }
                items.Sort(StringComparer.Ordinal);
                valueString = string.Join(",", items);
            }
            else
            {
                var toStringResult = value.ToString();
                if (toStringResult == null)
                    continue;
                valueString = toStringResult;
            }
            sb.Append($"{prop.Name}:{valueString};");
        }

        return sb.ToString();
    }
}