#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Engine.Common;
using Stomme.Engine.Common.Multitenancy;
using Stomme.Engine.KeyValues.Contracts;
using Stomme.Engine.KeyValues.Entities;
using Stomme.Engine.KeyValues.Models;
using Stomme.Engine.Pipelines.Management;
using Stomme.Engine.Pipelines.Management.Filters;
using Stomme.Mediator;
using Stomme.Models.Pipelines;
using Stomme.Security.Abstractions;

namespace Stomme.Modules.Pipelines.Application
{
    /// <summary>
    /// BL-229 - a run's review (<see cref="PipelineRunReview"/>), kept in the engine's key-value store under the SITE and the run,
    /// as the run's own data is (<see cref="PipelineRunDataStore"/>, whose delete takes it too, so every path that deletes a run -
    /// by hand, the site's retention, a pipeline that keeps only failed runs - deletes its review).
    /// </summary>
    public static class PipelineRunReviewStore
    {
        public const string KeyPrefix = "pipelines:runreview:";

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        public static string Prefix(string? tenantId) => KeyPrefix + tenantId + ":";

        public static string Key(string? tenantId, string runId) => Prefix(tenantId) + runId;

        public static async Task<PipelineRunReview?> ReadAsync(IKeyValueStore keyValues, string? tenantId, string runId, CancellationToken ct) =>
            Read((await keyValues.FindAsync(new KeyValueFilter { Key = Key(tenantId, runId) }, ct))?.SerializedValue);

        /// <summary>Every reviewed run of the site: its id and its review.</summary>
        public static async Task<Dictionary<string, PipelineRunReview>> ReadAllAsync(IKeyValueStore keyValues, string? tenantId, CancellationToken ct)
        {
            var prefix = Prefix(tenantId);
            var all = new Dictionary<string, PipelineRunReview>(StringComparer.Ordinal);
            foreach (var pair in await keyValues.FindManyAsync(new KeyValueFilter { Key = prefix, StartsWith = true }, ct))
            {
                if (pair.Key.StartsWith(prefix, StringComparison.Ordinal) && Read(pair.SerializedValue) is { } review)
                {
                    all[pair.Key[prefix.Length..]] = review;
                }
            }

            return all;
        }

        /// <summary>Keeps the review; one that says nothing is removed.</summary>
        public static async Task SaveAsync(IKeyValueStore keyValues, string? tenantId, string runId, PipelineRunReview review, CancellationToken ct)
        {
            if (PipelineRunReviews.IsEmpty(review))
            {
                await keyValues.DeleteAsync(Key(tenantId, runId), ct);
                return;
            }

            await keyValues.SaveAsync(new SerializedKeyValuePair { Key = Key(tenantId, runId), SerializedValue = JsonSerializer.Serialize(review, Json) }, ct);
        }

        public static async Task DeleteAsync(IKeyValueStore keyValues, string? tenantId, IEnumerable<string> runIds, CancellationToken ct)
        {
            foreach (var runId in runIds)
            {
                await keyValues.DeleteAsync(Key(tenantId, runId), ct);
            }
        }

        // Written by this class only; anything unreadable is no review rather than a broken page.
        private static PipelineRunReview? Read(string? json)
        {
            try
            {
                return string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<PipelineRunReview>(json, Json);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Says how a run went - good or bad, tags, a note - for a pipeline's authors (<c>pipelines:manage</c>), who and when recorded;
    /// a review saying nothing removes it. Null when there is no such run on the site; refused, with the reason, past the rules.
    /// </summary>
    [Requires(global::Stomme.Pipelines.PipelinesPermissions.Manage)]
    public sealed record SetRunReviewCommand(string RunId, PipelineRunReview? Review) : ICommand<PipelineRunReview?>;

    public sealed class SetRunReviewCommandHandler(IPipelineInstanceStore instances, IKeyValueStore keyValues, ITenantAccessor tenants, ICurrentUser currentUser, ISystemClock clock)
        : ICommandHandler<SetRunReviewCommand, PipelineRunReview?>
    {
        public async Task<PipelineRunReview?> Handle(SetRunReviewCommand request, CancellationToken ct)
        {
            if (await instances.FindAsync(new PipelineInstanceFilter { Id = request.RunId }, ct) is null)
            {
                return null;
            }

            var given = request.Review ?? new PipelineRunReview();
            var review = new PipelineRunReview
            {
                Rating = string.IsNullOrWhiteSpace(given.Rating) ? null : given.Rating.Trim().ToLowerInvariant(),
                Tags = PipelineRunReviews.Tags(given.Tags),
                Note = string.IsNullOrWhiteSpace(given.Note) ? null : given.Note.Trim(),
            };
            if (!PipelineRunReviews.Check(review, out var error))
            {
                throw new ArgumentException(error);
            }

            if (!PipelineRunReviews.IsEmpty(review))
            {
                review.By = currentUser.UserName;
                review.AtUtc = clock.UtcNow.UtcDateTime;
            }

            await PipelineRunReviewStore.SaveAsync(keyValues, tenants.TenantId, request.RunId, review, ct);
            return review;
        }
    }
}
