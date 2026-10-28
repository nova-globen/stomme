using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Security;
using Stomme.Shared;

namespace Stomme.Services
{
    public class SearchService : ISearchService
    {
        private const string SearchProviderSettingName = "Search_SearchProvider";

        private readonly IServiceProvider _serviceProvider;
        private readonly ISettingRepository _settingRepository;
        private readonly IUserPermissions _userPermissions;
        private readonly IHttpContextAccessor _accessor;

        public SearchService(
            IServiceProvider serviceProvider,
            ISettingRepository settingRepository,
            IUserPermissions userPermissions,
            IHttpContextAccessor accessor)
        {
            _settingRepository = settingRepository;
            _serviceProvider = serviceProvider;
            _userPermissions = userPermissions;
            _accessor = accessor;
        }

        public async Task<SearchResults> GetSearchResultsAsync(SearchQuery searchQuery)
        {
            var searchProvider = await GetSearchProviderAsync(searchQuery.SiteId);
            var searchResults = await searchProvider.GetSearchResultsAsync(searchQuery);

            // security trim results and aggregate by Url
            // the permission check reaches the store, so it cannot run inside a Where predicate; it is
            // evaluated up front over the same set, preserving the filter exactly
            var permitted = new List<SearchResult>();
            foreach (var searchResult in searchResults)
            {
                if (await HasViewPermissionAsync(searchResult, searchQuery))
                {
                    permitted.Add(searchResult);
                }
            }

            var results = permitted.AsEnumerable()
                .OrderBy(item => item.Url).ThenByDescending(item => item.Score)
                .GroupBy(group => group.Url)
                .Select(result => new SearchResult
                {
                    SearchContentId = result.First().SearchContentId,
                    SiteId = result.First().SiteId,
                    EntityName = result.First().EntityName,
                    EntityId = result.First().EntityId,
                    Title = result.First().Title,
                    Description = result.First().Description,
                    Body = result.First().Body,
                    Url = result.First().Url,
                    Permissions = result.First().Permissions,
                    ContentModifiedBy = result.First().ContentModifiedBy,
                    ContentModifiedOn = result.First().ContentModifiedOn,
                    SearchContentProperties = result.First().SearchContentProperties,
                    Snippet = result.First().Snippet,
                    Score = result.Sum(group => group.Score) // recalculate score
                });

            // sort results
            if (searchQuery.SortOrder == SearchSortOrder.Descending)
            {
                switch (searchQuery.SortField)
                {
                    case SearchSortField.Relevance:
                        results = results.OrderByDescending(i => i.Score).ThenByDescending(i => i.ContentModifiedOn);
                        break;
                    case SearchSortField.Title:
                        results = results.OrderByDescending(i => i.Title).ThenByDescending(i => i.ContentModifiedOn);
                        break;
                    default:
                        results = results.OrderByDescending(i => i.ContentModifiedOn);
                        break;
                }
            }
            else
            {
                switch (searchQuery.SortField)
                {
                    case SearchSortField.Relevance:
                        results = results.OrderBy(i => i.Score).ThenByDescending(i => i.ContentModifiedOn);
                        break;
                    case SearchSortField.Title:
                        results = results.OrderBy(i => i.Title).ThenByDescending(i => i.ContentModifiedOn);
                        break;
                    default:
                        results = results.OrderBy(i => i.ContentModifiedOn);
                        break;
                }
            }

            return new SearchResults
            {
                Results = results.Skip(searchQuery.PageIndex * searchQuery.PageSize).Take(searchQuery.PageSize).ToList(),
                TotalResults = results.Count()
            };
        }

        private async Task<bool> HasViewPermissionAsync(SearchContent searchContent, SearchQuery searchQuery)
        {
            var visible = true;
            foreach (var permission in searchContent.Permissions.Split(','))
            {
                if (permission.Contains(":")) // permission
                {
                    var entityName = permission.Split(":")[0];
                    var entityId = int.Parse(permission.Split(":")[1]);
                    if (!await _userPermissions.IsAuthorizedAsync(_accessor.HttpContext.User, searchQuery.SiteId, entityName, entityId, PermissionNames.View))
                    {
                        visible = false;
                        break;
                    }
                }
                else // role name
                {
                    if (!_accessor.HttpContext.User.IsInRole(permission))
                    {
                        visible = false;
                        break;
                    }
                }
            }
            return visible;
        }

        public async Task<string> SaveSearchContentsAsync(List<SearchContent> searchContents, Dictionary<string, string> siteSettings)
        {
            var result = "";

            if (searchContents.Any())
            {
                var searchProvider = await GetSearchProviderAsync(searchContents.First().SiteId);

                foreach (var searchContent in searchContents)
                {
                    try
                    {
                        await searchProvider.SaveSearchContent(searchContent, siteSettings);
                    }
                    catch (Exception ex)
                    {
                        result += $"Error Saving Search Content With UniqueKey {searchContent.UniqueKey} - {ex.Message}<br />";
                    }
                }
            }

            return result;
        }

        public async Task DeleteSearchContentsAsync(int siteId)
        {
            var searchProvider = await GetSearchProviderAsync(siteId);
            await searchProvider.DeleteSearchContent(siteId);
        }

        private async Task<ISearchProvider> GetSearchProviderAsync(int siteId)
        {
            var providerName = await GetSearchProviderSettingAsync(siteId);
            var searchProviders = _serviceProvider.GetServices<ISearchProvider>();
            var provider = searchProviders.FirstOrDefault(i => i.Name == providerName);
            if (provider == null)
            {
                provider = searchProviders.FirstOrDefault(i => i.Name == Constants.DefaultSearchProviderName);
            }

            return provider;
        }

        private async Task<string> GetSearchProviderSettingAsync(int siteId)
        {
            var setting = await _settingRepository.GetSettingAsync(EntityNames.Site, siteId, SearchProviderSettingName);
            if (!string.IsNullOrEmpty(setting?.SettingValue))
            {
                return setting.SettingValue;
            }

            return Constants.DefaultSearchProviderName;
        }
    }
}
