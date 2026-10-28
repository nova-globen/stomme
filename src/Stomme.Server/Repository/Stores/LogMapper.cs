using Stomme.Models;

namespace Stomme.Repository.Stores
{
    /// <summary>
    /// Translates between <see cref="LogAggregate"/> (the persisted aggregate) and <see cref="Log"/> (the
    /// client/server wire contract) at the <see cref="LogRepository"/> boundary.
    /// </summary>
    /// <remarks>
    /// <see cref="Log"/> has NO <c>[NotMapped]</c> members and no audit quartet: every public property is a
    /// column and every column is copied here. It is the only cluster in this batch where the contract and the
    /// aggregate are property-for-property identical.
    /// </remarks>
    public static class LogMapper
    {
        public static Log ToModel(LogAggregate aggregate)
        {
            if (aggregate == null)
            {
                return null;
            }

            return new Log
            {
                LogId = aggregate.Id,
                SiteId = aggregate.SiteId,
                LogDate = aggregate.LogDate,
                PageId = aggregate.PageId,
                ModuleId = aggregate.ModuleId,
                UserId = aggregate.UserId,
                Url = aggregate.Url,
                Server = aggregate.Server,
                Instance = aggregate.Instance,
                Category = aggregate.Category,
                Feature = aggregate.Feature,
                Function = aggregate.Function,
                Level = aggregate.Level,
                RemoteIPAddress = aggregate.RemoteIPAddress,
                Message = aggregate.Message,
                MessageTemplate = aggregate.MessageTemplate,
                Exception = aggregate.Exception,
                Properties = aggregate.Properties
            };
        }

        /// <summary>
        /// Rehydrates the aggregate from the model. <paramref name="assignId"/> is false for an insert (the
        /// database assigns the identity) and true for a delete. There is no update path.
        /// </summary>
        public static LogAggregate ToAggregate(Log log, bool assignId)
        {
            var aggregate = new LogAggregate
            {
                SiteId = log.SiteId,
                LogDate = log.LogDate,
                PageId = log.PageId,
                ModuleId = log.ModuleId,
                UserId = log.UserId,
                Url = log.Url,
                Server = log.Server,
                Instance = log.Instance,
                Category = log.Category,
                Feature = log.Feature,
                Function = log.Function,
                Level = log.Level,
                RemoteIPAddress = log.RemoteIPAddress,
                Message = log.Message,
                MessageTemplate = log.MessageTemplate,
                Exception = log.Exception,
                Properties = log.Properties
            };

            if (assignId)
            {
                aggregate.AssignId(log.LogId);
            }

            return aggregate;
        }

        /// <summary>
        /// Copies back the identity the database assigned. There are no audit stamps to copy — the Log table
        /// has no audit columns (see <see cref="LogAggregate"/>).
        /// </summary>
        public static void CopyPersistedValues(LogAggregate aggregate, Log log)
        {
            log.LogId = aggregate.Id;
        }
    }
}
