using System;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Stomme.Extensions;
using Stomme.Enums;
using Stomme.Models;
using Stomme.Repository;
using Stomme.Security;
using Stomme.Shared;

namespace Stomme.Infrastructure
{
    /// <summary>
    /// The framework's log surface. Synchronous — and, since D34, synchronous LEGITIMATELY: it enriches on
    /// the calling thread and hands off to <see cref="LogQueue"/>, touching no store.
    /// </summary>
    /// <remarks>
    /// Do not "fix" this to be async. It is called from ~443 sites; the reason it may stay sync is that its
    /// body no longer reaches a database, NOT that it is exempt from D33. If you add a store call here you
    /// have reintroduced the sync-over-async that D29 shipped a dead master with — put it in
    /// <see cref="LogDrainService"/> instead.
    /// </remarks>
    public interface ILogManager
    {
        void Log(Shared.LogLevel level, object @class, LogFunction function, string message, params object[] args);
        void Log(Shared.LogLevel level, object @class, LogFunction function, Exception exception, string message, params object[] args);
        void Log(int siteId, Shared.LogLevel level, object @class, LogFunction function, string message, params object[] args);
        void Log(int siteId, Shared.LogLevel level, object @class, LogFunction function, Exception exception, string message, params object[] args);
        void Log(Log log);
    }

    public class LogManager : ILogManager
    {
        private readonly LogQueue _queue;
        private readonly SiteState _siteState;
        private readonly IConfigManager _config;
        private readonly IHttpContextAccessor _accessor;
        private readonly ILogger<LogManager> _filelogger;
        private readonly ILogger<LogManager> _logger;

        public LogManager(LogQueue queue, SiteState siteState, IConfigManager config, IHttpContextAccessor accessor, ILogger<LogManager> filelogger, ILogger<LogManager> logger)
        {
            _queue = queue;
            _siteState = siteState;
            _config = config;
            _accessor = accessor;
            _filelogger = filelogger;
            _logger = logger;
        }

        /// <summary>
        /// The caller's ambient alias, WITHOUT touching a store.
        /// </summary>
        /// <remarks>
        /// This mirrors the fast path of <c>TenantManager.GetAlias()</c> — including its
        /// <c>AliasId != -1</c> guard, which excludes the "mock" alias <c>SetTenant</c> installs — but
        /// deliberately omits its store-reading fallback, because <c>Log</c> may not touch a store.
        /// In the request pipeline this is equivalent: <c>TenantMiddleware</c> resolves the alias into
        /// <c>SiteState</c> before anything downstream can log. Background jobs set it via
        /// <c>SetAlias</c>/<c>SetTenant</c>. The one case that differs is a log raised BEFORE tenant
        /// resolution has run (e.g. from the exception middleware, which wraps it): the old code would have
        /// resolved the alias from the database to attribute the entry; now <c>SiteId</c> stays -1 and the
        /// entry goes to the file logger, which is exactly what the old code did whenever resolution
        /// returned null.
        /// </remarks>
        private Alias CurrentAlias()
        {
            var alias = _siteState?.Alias;
            return (alias != null && alias.AliasId != -1) ? alias : null;
        }

        public void Log(Shared.LogLevel level, object @class, LogFunction function, string message, params object[] args)
        {
            Log(-1, level, @class, function, null, message, args);
        }

        public void Log(Shared.LogLevel level, object @class, LogFunction function, Exception exception, string message, params object[] args)
        {
            Log(-1, level, @class, function, exception, message, args);
        }

        public void Log(int siteId, Shared.LogLevel level, object @class, LogFunction function, string message, params object[] args)
        {
            Log(siteId, level, @class, function, null, message, args);
        }

        public void Log(int siteId, Shared.LogLevel level, object @class, LogFunction function, Exception exception, string message, params object[] args)
        {
            Log log = new Log();

            log.SiteId = siteId;
            if (log.SiteId == -1)
            {
                var alias = CurrentAlias();
                if (alias != null)
                {
                    log.SiteId = alias.SiteId;
                }
            }

            log.PageId = null;
            log.ModuleId = null;
            // Was `_userPermissions.GetUser().UserId`. IUserPermissions.GetUserAsync READS THE STORE, but only
            // to populate User.Roles — and this method never reads Roles, only UserId. UserId is
            // ClaimsPrincipal.UserId(), a pure claims lookup that returns -1 when the claim is absent, which
            // is precisely the value GetUserAsync yielded for an anonymous or null principal. So this is the
            // same number, without the query. (D34)
            var principal = _accessor.HttpContext?.User;
            log.UserId = (principal != null && principal.Identity.IsAuthenticated) ? principal.UserId() : -1;
            log.Url = "";
            if (_accessor.HttpContext != null)
            {
                if (string.IsNullOrEmpty(log.RemoteIPAddress))
                {
                    log.RemoteIPAddress = _accessor.HttpContext.Connection.RemoteIpAddress?.ToString();
                    if (log.RemoteIPAddress == "::1")
                    {
                        log.RemoteIPAddress = "127.0.0.1";
                    }
                }
                HttpRequest request = _accessor.HttpContext.Request;
                if (request != null)
                {
                    // An action that KNOWS its own URL carries a secret can say so, and that wins. See
                    // RedactedUrlKey - the query-string denylist below cannot help when the secret is a PATH
                    // segment, because nothing about a path says which segment is a value (BL-148).
                    log.Url = _accessor.HttpContext.Items.TryGetValue(RedactedUrlKey, out var declared)
                        && declared is string redacted && !string.IsNullOrEmpty(redacted)
                        ? redacted
                        : $"{request.Scheme}://{request.Host}{request.Path}{RedactQueryString(request.Query)}";
                }
            }

            Type type = @class.GetType();
            if (type != null && type != typeof(string))
            {
                log.Category = type.AssemblyQualifiedName;
                log.Feature = Utilities.GetTypeNameLastSegment(log.Category, 0);
            }
            else
            {
                log.Category = @class.ToString();
                log.Feature = log.Category;
            }
            log.Function = Enum.GetName(typeof(LogFunction), function);
            // Owner, 2026-10-10 (D99a): an inherited refusal of an ANONYMOUS caller is a Warning - logged, never mailed.
            level = Stomme.Infrastructure.Security.SecurityRefusals.LevelFor(level, function, message, principal);
            log.Level = Enum.GetName(typeof(Shared.LogLevel), level);
            if (exception != null)
            {
                log.Exception = exception.ToString();
            }
            log.Message = message;
            log.MessageTemplate = "";
            try
            {
                log.Properties = JsonSerializer.Serialize(SummarizeEntities(args));
            }
            catch // serialization error occurred
            {
                log.Properties = "";
            }
            Log(log);
        }

        /// <summary>
        /// A framework entity argument, logged as WHAT it is rather than everything it carries (BL-157).
        /// </summary>
        /// <remarks>
        /// "Page Added {Page}" used to serialise the whole <see cref="Page"/> - its permission list with role names, user
        /// ids and four timestamps per permission - into <c>Log.Message</c> AND again into <c>Log.Properties</c>: measured on a
        /// stock install boot, 11 rows came to 33,991 characters (max 4,005, median 3,032), in a table the admin viewer
        /// shows to every administrator. A <see cref="ModelBase"/> argument is now its type, its id and its name - "Page 42
        /// 'Home'" - which still says what changed. Everything else (strings, numbers, the callers' own summaries such as
        /// <c>SettingController.Describe</c>) passes through as it was.
        /// </remarks>
        internal static object[] SummarizeEntities(object[] args)
        {
            if (args is null || args.Length == 0)
            {
                return args;
            }

            var summarized = new object[args.Length];
            for (var i = 0; i < args.Length; i++)
            {
                summarized[i] = args[i] is ModelBase entity ? Summarize(entity) : args[i];
            }

            return summarized;
        }

        private static readonly string[] NameProperties = { "Name", "Title", "Username", "Path", "Code" };

        private static string Summarize(ModelBase entity)
        {
            var type = entity.GetType();
            var id = type.GetProperty(type.Name + "Id")?.GetValue(entity);
            string name = null;
            foreach (var candidate in NameProperties)
            {
                if (type.GetProperty(candidate)?.GetValue(entity) is string value && !string.IsNullOrWhiteSpace(value))
                {
                    name = value;
                    break;
                }
            }

            return type.Name + (id is null ? "" : " " + id) + (name is null ? "" : " '" + name + "'");
        }

        public void Log(Log log)
        {
            var minlevel = Shared.LogLevel.Information;
            var section = _config.GetSection("Logging:LogLevel:Default");
            if (section.Exists())
            {
                minlevel = Enum.Parse<Shared.LogLevel>(section.Value);
            }

            if (Enum.Parse<Shared.LogLevel>(log.Level) >= minlevel)
            {
                log.LogDate = DateTime.UtcNow;
                log.Server = Environment.MachineName;
                log.Instance = Environment.GetEnvironmentVariable("WEBSITE_INSTANCE_ID") ?? "";
                log.MessageTemplate = log.Message;
                log = ProcessStructuredLog(log);
                try
                {
                    if (log.SiteId != -1)
                    {
                        // Hand off to LogDrainService. The store write and the notification used to happen
                        // right here, synchronously; since D33 made the repositories async they cannot, and
                        // blocking on them is the banned shape. Enrichment above already captured everything
                        // that needed the request, so the drain only needs the entry and its tenant. (D34)
                        var entry = new LogEntry(log, _siteState?.Alias, CurrentAlias());
                        if (!_queue.TryWrite(entry))
                        {
                            // Queue saturated. Do not block the caller and do not drop the entry: the file
                            // logger is the same fallback this method already uses when a site cannot be
                            // determined.
                            _filelogger.Log(GetLogLevel(log.Level), "[" + log.Category + "] " + log.Message);
                        }
                    }
                    else // use file logger as fallback when site cannot be determined
                    {
                        _filelogger.Log(GetLogLevel(log.Level), "[" + log.Category + "] " + log.Message);
                    }
                }
                catch
                {
                    // an error occurred writing the log
                }
            }

            // use ILogger to broadcast the event to any registered providers (e.g. Application Insights, Serilog, etc...)
            var broadcast = Shared.LogLevel.None;
            section = _config.GetSection("Logging:LogLevel:Broadcast");
            if (section.Exists())
            {
                broadcast = Enum.Parse<Shared.LogLevel>(section.Value);
            }
            if (Enum.Parse<Shared.LogLevel>(log.Level) >= broadcast)
            {
                // the SiteId will be passed as an EventId which will provide context and allow the FileLogger to ignore the log item
                var eventId = new EventId((log.SiteId.HasValue) ? log.SiteId.Value : -1, "SiteId");
                var exception = (!string.IsNullOrEmpty(log.Exception) ? new Exception(log.Exception) : null);
                _logger.Log(GetLogLevel(log.Level), eventId, exception, log.Message);
            }
        }

        private Log ProcessStructuredLog(Log log)
        {
            try
            {
                string message = log.Message;
                string properties = "";
                if (!string.IsNullOrEmpty(message) && message.Contains("{") && message.Contains("}") && !string.IsNullOrEmpty(log.Properties))
                {
                    // get the named holes in the message and replace values
                    object[] values = JsonSerializer.Deserialize<object[]>(log.Properties);
                    List<string> names = new List<string>();
                    int index = message.IndexOf("{");
                    while (index != -1)
                    {
                        if (message.IndexOf("}", index) != -1)
                        {
                            names.Add(message.Substring(index + 1, message.IndexOf("}", index) - index - 1));
                            if (values.Length > (names.Count - 1))
                            {
                                var value = (values[names.Count - 1] == null) ? "null" : values[names.Count - 1].ToString();
                                message = message.Replace("{" + names[names.Count - 1] + "}", value);
                            }
                        }
                        index = (index < message.Length - 1) ? message.IndexOf("{", index + 1) : -1;
                    }
                    // rebuild properties into dictionary
                    Dictionary<string, object> propertyDictionary = new Dictionary<string, object>();
                    for (int i = 0; i < values.Length; i++)
                    {
                        if (i < names.Count)
                        {
                            propertyDictionary.Add(names[i], values[i]);
                        }
                        else
                        {
                            propertyDictionary.Add("Property" + i.ToString(), values[i]);
                        }
                    }
                    properties = JsonSerializer.Serialize(propertyDictionary);
                }
                log.Message = message;
                log.Properties = properties;
            }
            catch
            {
                log.Properties = "";
            }
            return log;
        }

        private Microsoft.Extensions.Logging.LogLevel GetLogLevel(string level)
        {
            switch (Enum.Parse<Shared.LogLevel>(level))
            {
                case Shared.LogLevel.Trace:
                    return Microsoft.Extensions.Logging.LogLevel.Trace;
                case Shared.LogLevel.Debug:
                    return Microsoft.Extensions.Logging.LogLevel.Debug;
                case Shared.LogLevel.Information:
                    return Microsoft.Extensions.Logging.LogLevel.Information;
                case Shared.LogLevel.Warning:
                    return Microsoft.Extensions.Logging.LogLevel.Warning;
                case Shared.LogLevel.Error:
                    return Microsoft.Extensions.Logging.LogLevel.Error;
                case Shared.LogLevel.Critical:
                    return Microsoft.Extensions.Logging.LogLevel.Critical;
                default:
                    return Microsoft.Extensions.Logging.LogLevel.None;
            }
        }

        /// <summary>
        /// <c>HttpContext.Items</c> key an action sets to declare the URL that should be logged for its request.
        /// </summary>
        /// <remarks>
        /// <b>For a secret in the PATH, which no generic rule can find.</b> A query string names its parts, so
        /// <see cref="RedactQueryString"/> can redact by key; a path does not — nothing about
        /// <c>/api/Setting/Site/1/Smtp:Password/hunter2/true</c> says which segment is the value, and a regex per
        /// route is a rule that rots the moment a route changes. So the ACTION, which knows, says so: it puts a
        /// safe URL here and <c>LogManager</c> prefers it for every entry raised during that request (BL-148).
        /// One writer today, <c>SettingController</c>'s legacy value-in-path route.
        /// </remarks>
        public const string RedactedUrlKey = "Stomme:RedactedLogUrl";

        /// <summary>Query-string keys whose VALUE is a credential and must never be written to the log.</summary>
        /// <remarks>
        /// Matched case-insensitively and by whole key. Kept deliberately short: this is a denylist, and a
        /// denylist is only honest about the names on it — see the remarks on <see cref="RedactQueryString"/>.
        /// </remarks>
        private static readonly HashSet<string> SecretQueryKeys =
            new(StringComparer.OrdinalIgnoreCase) { "token", "password", "secret", "code", "access_token", "refresh_token", "id_token", "client_secret" };

        /// <summary>
        /// The request's query string with credential values replaced, for <c>Log.Url</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Every log entry raised during a request carries that request's URL</b>, so a secret in the query
        /// string reaches the site Log table through a door that has nothing to do with what was being logged.
        /// MEASURED (BL-149): a failed password reset wrote
        /// <c>url=…/api/User/reset?token=…</c> into the table, where every administrator can read it — and it
        /// did so from a log line that does not mention the token at all. Fixing only the message would have
        /// left the token in the log and looked like a fix.
        /// </para>
        /// <para>
        /// <b>This is a denylist, with a denylist's honest limit:</b> it protects the names it knows. The
        /// durable answer is to keep credentials out of URLs altogether — the reset token should travel in the
        /// request BODY, which is the same change BL-148 needs for private setting values (there the value is a
        /// PATH segment, which this does not touch).
        /// </para>
        /// </remarks>
        internal static string RedactQueryString(IQueryCollection query)
        {
            if (query == null || query.Count == 0)
            {
                return string.Empty;
            }

            var parts = new List<string>();
            foreach (var pair in query)
            {
                var value = SecretQueryKeys.Contains(pair.Key) ? "[redacted]" : pair.Value.ToString();
                parts.Add($"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(value)}");
            }

            return "?" + string.Join("&", parts);
        }
    }
}
