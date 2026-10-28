using Stomme.Engine.Pipelines;
using Stomme.Engine.Pipelines.Helpers;
using Stomme.Engine.Pipelines.Models;

// ReSharper disable once CheckNamespace
namespace Stomme.Engine.Extensions;

/// <summary>
/// Adds extensions to <c>Bookmark</c> and sets thereof.
/// </summary>
public static class BookmarkExtensions
{
    /// <param name="bookmarks">The set of bookmarks to filter.</param>
    extension(IEnumerable<Bookmark> bookmarks)
    {
        /// <summary>
        /// Filters the specified set of bookmarks by the specified step type.
        /// </summary>
        /// <typeparam name="T">The step type to filter by.</typeparam>
        /// <returns>The filtered set of bookmarks.</returns>
        public IEnumerable<Bookmark> Filter<T>() where T : IStep
        {
            var bookmarkName = StepTypeNameHelper.GenerateTypeName<T>();
            return bookmarks.Filter(bookmarkName);
        }

        public IEnumerable<Bookmark> Filter(string name)
        {
            return bookmarks.Where(x => x.Name == name);
        }
    }

    /// <summary>
    /// Returns the Data property of the bookmark as a strongly-typed object.
    /// </summary>
    /// <param name="bookmark">The bookmark.</param>
    /// <typeparam name="T">The type of the Data property.</typeparam>
    /// <returns>The Data property of the bookmark as a strongly-typed object.</returns>
    public static T GetPayload<T>(this Bookmark bookmark) => (T)bookmark.Payload!;
}