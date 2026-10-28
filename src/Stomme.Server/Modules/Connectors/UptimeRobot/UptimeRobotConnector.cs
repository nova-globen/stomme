#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;

namespace Stomme.Modules.Connectors.UptimeRobot
{
    /// <summary>
    /// UptimeRobot (BL-229, n8n's UptimeRobot node: its account, monitors, alert contacts, maintenance windows and public status
    /// pages): UptimeRobot's API v2 with an API key in each call's form, over the host's egress. A test names the key's account.
    /// </summary>
    /// <remarks>
    /// <c>account</c>; <c>monitors</c> (by a search, statuses, types and ids, with their logs, response times, alert contacts and
    /// maintenance windows), <c>monitor</c>, <c>create_monitor</c>, <c>update_monitor</c> (paused or resumed too),
    /// <c>reset_monitor</c> and <c>delete_monitor</c>; <c>alert_contacts</c>, <c>alert_contact</c>, <c>create_alert_contact</c>,
    /// <c>update_alert_contact</c> and <c>delete_alert_contact</c>; <c>maintenance_windows</c>, <c>maintenance_window</c>,
    /// <c>create_maintenance_window</c>, <c>update_maintenance_window</c> and <c>delete_maintenance_window</c>;
    /// <c>status_pages</c>, <c>status_page</c>, <c>create_status_page</c> and <c>delete_status_page</c>. Kinds by their names
    /// or UptimeRobot's numbers. A list is read page by page by offset up to <c>max</c>, its total the end. A record is its
    /// fields as UptimeRobot gives them, a list or an object as its JSON. A password - a monitored site's, a status page's - is
    /// never a step's parameter. Every id, kind, number, time and address held to its shape before anything is sent;
    /// UptimeRobot's stat anything but ok a failure with its words, a 200 among them. A throttled call waits as UptimeRobot says.
    /// </remarks>
    public sealed class UptimeRobotConnector : IConnector, IConnectorExtractor
    {
        public const string AccountOperation = "account";

        public const string MonitorsOperation = "monitors";

        public const string MonitorOperation = "monitor";

        public const string CreateMonitorOperation = "create_monitor";

        public const string UpdateMonitorOperation = "update_monitor";

        public const string ResetMonitorOperation = "reset_monitor";

        public const string DeleteMonitorOperation = "delete_monitor";

        public const string AlertContactsOperation = "alert_contacts";

        public const string AlertContactOperation = "alert_contact";

        public const string CreateAlertContactOperation = "create_alert_contact";

        public const string UpdateAlertContactOperation = "update_alert_contact";

        public const string DeleteAlertContactOperation = "delete_alert_contact";

        public const string MaintenanceWindowsOperation = "maintenance_windows";

        public const string MaintenanceWindowOperation = "maintenance_window";

        public const string CreateMaintenanceWindowOperation = "create_maintenance_window";

        public const string UpdateMaintenanceWindowOperation = "update_maintenance_window";

        public const string DeleteMaintenanceWindowOperation = "delete_maintenance_window";

        public const string StatusPagesOperation = "status_pages";

        public const string StatusPageOperation = "status_page";

        public const string CreateStatusPageOperation = "create_status_page";

        public const string DeleteStatusPageOperation = "delete_status_page";

        public const int DefaultMax = 1000;

        private const int PageSize = 50;

        private const string DefaultEndpoint = "https://api.uptimerobot.com/v2";

        private static readonly Regex Id = new("^[1-9][0-9]{0,18}$", RegexOptions.CultureInvariant);

        private static readonly Regex Ids = new("^[1-9][0-9]{0,18}(-[1-9][0-9]{0,18})*$", RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions Wire = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        private static readonly string[] Known =
        {
            AccountOperation, MonitorsOperation, MonitorOperation, CreateMonitorOperation, UpdateMonitorOperation, ResetMonitorOperation, DeleteMonitorOperation,
            AlertContactsOperation, AlertContactOperation, CreateAlertContactOperation, UpdateAlertContactOperation, DeleteAlertContactOperation, MaintenanceWindowsOperation,
            MaintenanceWindowOperation, CreateMaintenanceWindowOperation, UpdateMaintenanceWindowOperation, DeleteMaintenanceWindowOperation, StatusPagesOperation,
            StatusPageOperation, CreateStatusPageOperation, DeleteStatusPageOperation,
        };

        // UptimeRobot's kinds by name, each its number.
        private static readonly (string Name, int Number)[] MonitorTypes = { ("http", 1), ("keyword", 2), ("ping", 3), ("port", 4), ("heartbeat", 5) };

        private static readonly (string Name, int Number)[] MonitorStatuses = { ("paused", 0), ("not_checked", 1), ("up", 2), ("seems_down", 8), ("down", 9) };

        private static readonly (string Name, int Number)[] SubTypes = { ("http", 1), ("https", 2), ("ftp", 3), ("smtp", 4), ("pop3", 5), ("imap", 6), ("custom", 99) };

        private static readonly (string Name, int Number)[] HttpMethods = { ("head", 1), ("get", 2), ("post", 3), ("put", 4), ("patch", 5), ("delete", 6), ("options", 7) };

        private static readonly (string Name, int Number)[] ContactTypes = { ("sms", 1), ("email", 2), ("twitter", 3), ("boxcar", 4), ("webhook", 5), ("pushbullet", 6), ("pushover", 9) };

        private static readonly (string Name, int Number)[] WindowTypes = { ("once", 1), ("daily", 2), ("weekly", 3), ("monthly", 4) };

        private static readonly (string Name, int Number)[] Sorts = { ("name_asc", 1), ("name_desc", 2), ("up_down_paused", 3), ("down_up_paused", 4) };

        private static readonly (string Name, int Number)[] KeywordTypes = { ("exists", 1), ("not_exists", 2) };

        public string ConnectorType => "uptimerobot";

        public string DisplayName => "UptimeRobot";

        public AuthenticationType AuthType => AuthenticationType.ApiKey;

        public string EndpointExample => DefaultEndpoint;

        public IReadOnlyDictionary<string, string>? CredentialLabels { get; } = new Dictionary<string, string>(StringComparer.Ordinal) { ["api-key"] = "Main API key (Integrations & API > API)" };

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.Development };

        public Uri WebsiteUrl { get; } = new("https://uptimerobot.com/");

        public Uri ApiDocumentationUrl { get; } = new("https://uptimerobot.com/api/");

        public ConnectorConfigSchema ConfigSchema { get; } = new(Array.Empty<ConnectorConfigField>());

        private static ConnectorConfigField Field(string name, string label, string description, ConnectorConfigFieldType type = ConnectorConfigFieldType.Text, bool required = false) => new(name, label, type, Required: required, Description: description);

        private static ConnectorConfigField IdField(string what) => Field("id", "Id", $"The {what}'s id.", required: true);

        private static ConnectorConfigField IdsField(string what) => Field("ids", "Ids", $"The {what}' ids, joined by '-' - 236-1782.");

        private static readonly ConnectorConfigField MaxField = new("max", "At most", ConnectorConfigFieldType.Integer, Required: false, Description: "How many, at most.") { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) };

        private static ConnectorConfigSchema Schema(params ConnectorConfigField[] fields) => new(fields);

        private static ConnectorConfigField Kind(string name, string label, (string Name, int Number)[] kinds, bool required = false) =>
            Field(name, label, string.Join(", ", kinds.Select(kind => kind.Name)) + " - or UptimeRobot's number.", required: required);

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(AccountOperation, "Get the account", "n8n's UptimeRobot Account Get.") { Parameters = Schema() },
            new ConnectorOperation(MonitorsOperation, "Get monitors", "n8n's UptimeRobot Monitor Get Many.")
            {
                Parameters = Schema(
                    IdsField("monitors"),
                    Field("search", "Search", "A part of their name or address."),
                    Field("statuses", "Statuses", "Comma-separated: paused, not_checked, up, seems_down, down."),
                    Field("types", "Types", "Comma-separated: http, keyword, ping, port, heartbeat."),
                    Field("logs", "Logs", "true: each with its logs.", ConnectorConfigFieldType.Boolean),
                    Field("response_times", "Response times", "true: each with its response times.", ConnectorConfigFieldType.Boolean),
                    Field("alert_contacts", "Alert contacts", "true: each with its alert contacts.", ConnectorConfigFieldType.Boolean),
                    Field("mwindows", "Maintenance windows", "true: each with its maintenance windows.", ConnectorConfigFieldType.Boolean),
                    MaxField),
            },
            new ConnectorOperation(MonitorOperation, "Get a monitor", "n8n's UptimeRobot Monitor Get.") { Parameters = Schema(IdField("monitor")) },
            new ConnectorOperation(CreateMonitorOperation, "Create a monitor", "n8n's UptimeRobot Monitor Create.")
            {
                Parameters = Schema(
                    Field("friendly_name", "Name", "Its name.", required: true),
                    Field("url", "Address", "What it watches - an address, a host or an IP.", required: true),
                    Kind("type", "Type", MonitorTypes, required: true),
                    Kind("sub_type", "Port type", SubTypes),
                    Field("port", "Port", "A custom port's number.", ConnectorConfigFieldType.Integer),
                    Kind("keyword_type", "Keyword type", KeywordTypes),
                    Field("keyword_value", "Keyword", "A keyword monitor's word."),
                    Field("interval", "Interval", "Seconds between checks.", ConnectorConfigFieldType.Integer)),
            },
            new ConnectorOperation(UpdateMonitorOperation, "Update a monitor", "Paused or resumed too - n8n's UptimeRobot Monitor Update.")
            {
                Parameters = Schema(
                    IdField("monitor"),
                    Field("friendly_name", "Name", "Its name."),
                    Field("url", "Address", "What it watches."),
                    Kind("sub_type", "Port type", SubTypes),
                    Field("port", "Port", "A custom port's number.", ConnectorConfigFieldType.Integer),
                    Field("interval", "Interval", "Seconds between checks.", ConnectorConfigFieldType.Integer),
                    Kind("http_method", "HTTP method", HttpMethods),
                    Field("status", "Status", "paused or active.")),
            },
            new ConnectorOperation(ResetMonitorOperation, "Reset a monitor", "Its stats cleared - n8n's UptimeRobot Monitor Reset.") { Parameters = Schema(IdField("monitor")) },
            new ConnectorOperation(DeleteMonitorOperation, "Delete a monitor", "n8n's UptimeRobot Monitor Delete.") { Parameters = Schema(IdField("monitor")) },
            new ConnectorOperation(AlertContactsOperation, "Get alert contacts", "n8n's UptimeRobot Alert Contact Get Many.") { Parameters = Schema(IdsField("alert contacts"), MaxField) },
            new ConnectorOperation(AlertContactOperation, "Get an alert contact", "n8n's UptimeRobot Alert Contact Get.") { Parameters = Schema(IdField("alert contact")) },
            new ConnectorOperation(CreateAlertContactOperation, "Create an alert contact", "n8n's UptimeRobot Alert Contact Create.")
            {
                Parameters = Schema(Field("friendly_name", "Name", "Its name.", required: true), Kind("type", "Type", ContactTypes, required: true), Field("value", "Value", "Its e-mail, number or address.", required: true)),
            },
            new ConnectorOperation(UpdateAlertContactOperation, "Update an alert contact", "n8n's UptimeRobot Alert Contact Update.")
            {
                Parameters = Schema(IdField("alert contact"), Field("friendly_name", "Name", "Its name."), Field("value", "Value", "A webhook's address.")),
            },
            new ConnectorOperation(DeleteAlertContactOperation, "Delete an alert contact", "n8n's UptimeRobot Alert Contact Delete.") { Parameters = Schema(IdField("alert contact")) },
            new ConnectorOperation(MaintenanceWindowsOperation, "Get maintenance windows", "n8n's UptimeRobot Maintenance Window Get Many.") { Parameters = Schema(IdsField("maintenance windows"), MaxField) },
            new ConnectorOperation(MaintenanceWindowOperation, "Get a maintenance window", "n8n's UptimeRobot Maintenance Window Get.") { Parameters = Schema(IdField("maintenance window")) },
            new ConnectorOperation(CreateMaintenanceWindowOperation, "Create a maintenance window", "n8n's UptimeRobot Maintenance Window Create.")
            {
                Parameters = Schema(
                    Field("friendly_name", "Name", "Its name.", required: true),
                    Kind("type", "Type", WindowTypes, required: true),
                    Field("start_time", "Start", "A once window's moment - 2026-10-20T22:00:00Z; else its time of day - 22:00.", required: true),
                    Field("duration", "Duration", "Minutes.", ConnectorConfigFieldType.Integer, required: true),
                    Field("value", "Days", "A weekly window's days, 1 (Monday) to 7, joined by '-'; a monthly window's day, 1 to 31.")),
            },
            new ConnectorOperation(UpdateMaintenanceWindowOperation, "Update a maintenance window", "n8n's UptimeRobot Maintenance Window Update.")
            {
                Parameters = Schema(
                    IdField("maintenance window"),
                    Field("duration", "Duration", "Minutes.", ConnectorConfigFieldType.Integer, required: true),
                    Field("friendly_name", "Name", "Its name."),
                    Kind("type", "Type", WindowTypes),
                    Field("start_time", "Start", "A once window's moment, else its time of day."),
                    Field("value", "Days", "A weekly window's days or a monthly window's day.")),
            },
            new ConnectorOperation(DeleteMaintenanceWindowOperation, "Delete a maintenance window", "n8n's UptimeRobot Maintenance Window Delete.") { Parameters = Schema(IdField("maintenance window")) },
            new ConnectorOperation(StatusPagesOperation, "Get status pages", "n8n's UptimeRobot Public Status Page Get Many.") { Parameters = Schema(IdsField("status pages"), MaxField) },
            new ConnectorOperation(StatusPageOperation, "Get a status page", "n8n's UptimeRobot Public Status Page Get.") { Parameters = Schema(IdField("status page")) },
            new ConnectorOperation(CreateStatusPageOperation, "Create a status page", "n8n's UptimeRobot Public Status Page Create.")
            {
                Parameters = Schema(
                    Field("friendly_name", "Name", "Its name.", required: true),
                    Field("monitors", "Monitors", "Its monitors' ids joined by '-', or 0 for all.", required: true),
                    Field("custom_domain", "Custom domain", "Its own domain - status.example.com."),
                    Kind("sort", "Sort", Sorts)),
            },
            new ConnectorOperation(DeleteStatusPageOperation, "Delete a status page", "n8n's UptimeRobot Public Status Page Delete.") { Parameters = Schema(IdField("status page")) },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            try
            {
                var session = Session.Of(context.Endpoint, context.Credentials, context.Egress, context.Timeout);
                using var client = session.Client;
                var account = (await session.PostAsync("/getAccountDetails", Array.Empty<KeyValuePair<string, string>>(), cancellationToken))?["account"] as JsonObject;
                return ConnectionTestResult.Ok($"UptimeRobot: the key is {Text(account?["email"]) ?? "an account"}'s{(account?["monitor_limit"] is JsonValue limit && limit.TryGetValue<long>(out var most) ? $", {most.ToString(CultureInfo.InvariantCulture)} monitors at most" : "")}.");
            }
            catch (InvalidOperationException ex)
            {
                return ConnectionTestResult.Fail(ex.Message);
            }
            catch (HttpRequestException ex) when (!context.Egress.IsDenial(ex))
            {
                return ConnectionTestResult.Fail(ex.Message);
            }
        }

        public async IAsyncEnumerable<ConnectorRecord> ExtractAsync(ConnectorExtractContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? MonitorsOperation).Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"UptimeRobot has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;
            var session = Session.Of(context.Endpoint, context.Credentials, context.Egress, context.Timeout);

            // Everything held to its shape before anything is sent.
            var max = Values.Get(p, "max") is { } most ? int.TryParse(most, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : throw new InvalidOperationException($"'{most}' is not a number above 0 (max).") : DefaultMax;
            var form = new List<KeyValuePair<string, string>>();
            void Add(string name, string? value)
            {
                if (value is not null)
                {
                    form.Add(new(name, value));
                }
            }

            string? KindOf(string name, (string Name, int Number)[] kinds, bool required = false)
            {
                if (Values.Get(p, name) is not { } given)
                {
                    return required ? throw new InvalidOperationException($"Give its {name.Replace('_', ' ')} - {string.Join(", ", kinds.Select(kind => kind.Name))}.") : null;
                }

                var match = kinds.FirstOrDefault(kind => string.Equals(kind.Name, given, StringComparison.OrdinalIgnoreCase) || kind.Number.ToString(CultureInfo.InvariantCulture) == given);
                return match.Name is null ? throw new InvalidOperationException($"'{given}' is not {string.Join(", ", kinds[..^1].Select(kind => kind.Name))} or {kinds[^1].Name} ({name}).") : match.Number.ToString(CultureInfo.InvariantCulture);
            }

            string? NumberOf(string name, int least, int most, bool required = false) => Values.Get(p, name) is { } given
                ? int.TryParse(given, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= least && number <= most ? number.ToString(CultureInfo.InvariantCulture) : throw new InvalidOperationException($"'{given}' is not a whole number from {least} to {most} ({name}).")
                : required ? throw new InvalidOperationException($"Give its {name.Replace('_', ' ')}.") : null;
            string? FlagOf(string name) => Values.Get(p, name) is { } given ? given.ToLowerInvariant() is "true" or "false" ? given.ToLowerInvariant() == "true" ? "1" : null : throw new InvalidOperationException($"'{given}' is not true or false ({name}).") : null;
            string IdOf(string what) => Values.Get(p, "id") is { } given ? Id.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not an id (id).") : throw new InvalidOperationException($"Give the {what}'s id.");
            string? IdsOf(string name) => Values.Get(p, name) is { } given ? Ids.IsMatch(given) ? given : throw new InvalidOperationException($"'{given}' is not ids joined by '-' - 236-1782 ({name}).") : null;
            string NameOf() => Values.Get(p, "friendly_name") ?? throw new InvalidOperationException("Give its name.");
            string KindsOf(string name, (string Name, int Number)[] kinds, string given) =>
                string.Join("-", given.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(each =>
                    kinds.FirstOrDefault(kind => string.Equals(kind.Name, each, StringComparison.OrdinalIgnoreCase) || kind.Number.ToString(CultureInfo.InvariantCulture) == each) is { Name: not null } match
                        ? match.Number.ToString(CultureInfo.InvariantCulture)
                        : throw new InvalidOperationException($"'{each}' is not {string.Join(", ", kinds[..^1].Select(kind => kind.Name))} or {kinds[^1].Name} ({name}).")));

            // A maintenance window's start: a once window's moment as UptimeRobot's seconds, another's time of day.
            string? StartOf(string? type, bool required)
            {
                if (Values.Get(p, "start_time") is not { } start)
                {
                    return required ? throw new InvalidOperationException("Give its start.") : null;
                }

                if (type == "1")
                {
                    return DateTimeOffset.TryParse(start, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when) && start.Contains('T')
                        ? when.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)
                        : throw new InvalidOperationException($"'{start}' is not a moment - 2026-10-20T22:00:00Z (start_time).");
                }

                return TimeOnly.TryParseExact(start, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ? start : throw new InvalidOperationException($"'{start}' is not a time of day - 22:00 (start_time).");
            }

            // A weekly window's days, 1 to 7 joined by '-'; a monthly window's day, 1 to 31.
            string? DaysOf(string? type)
            {
                if (Values.Get(p, "value") is not { } days)
                {
                    return type is "3" or "4" ? throw new InvalidOperationException($"Give a {(type == "3" ? "weekly" : "monthly")} window's {(type == "3" ? "days" : "day")} (value).") : null;
                }

                return type switch
                {
                    "3" => Regex.IsMatch(days, "^[1-7](-[1-7])*$") ? days : throw new InvalidOperationException($"'{days}' is not week days 1 (Monday) to 7 joined by '-' (value)."),
                    "4" => int.TryParse(days, NumberStyles.None, CultureInfo.InvariantCulture, out var day) && day is >= 1 and <= 31 ? days : throw new InvalidOperationException($"'{days}' is not a day of the month from 1 to 31 (value)."),
                    _ => throw new InvalidOperationException("Only a weekly or monthly window has days (value)."),
                };
            }

            string path;
            string? list = null;
            switch (operation)
            {
                case AccountOperation:
                    path = "/getAccountDetails";
                    break;
                case MonitorsOperation:
                    Add("monitors", IdsOf("ids"));
                    Add("search", Values.Get(p, "search"));
                    Add("statuses", Values.Get(p, "statuses") is { } statuses ? KindsOf("statuses", MonitorStatuses, statuses) : null);
                    Add("types", Values.Get(p, "types") is { } types ? KindsOf("types", MonitorTypes, types) : null);
                    foreach (var flag in new[] { "logs", "response_times", "alert_contacts", "mwindows" })
                    {
                        Add(flag, FlagOf(flag));
                    }

                    (path, list) = ("/getMonitors", "monitors");
                    break;
                case MonitorOperation:
                    Add("monitors", IdOf("monitor"));
                    (path, list) = ("/getMonitors", "monitors");
                    break;
                case CreateMonitorOperation or UpdateMonitorOperation:
                    {
                        var create = operation == CreateMonitorOperation;
                        if (!create)
                        {
                            Add("id", IdOf("monitor"));
                        }

                        Add("friendly_name", create ? NameOf() : Values.Get(p, "friendly_name"));
                        if (Values.Get(p, "url") is { } url)
                        {
                            Add("url", url.Any(char.IsWhiteSpace) ? throw new InvalidOperationException($"'{url}' is not an address, a host or an IP (url).") : url);
                        }
                        else if (create)
                        {
                            throw new InvalidOperationException("Give what it watches - an address, a host or an IP (url).");
                        }

                        var type = create ? KindOf("type", MonitorTypes, required: true) : null;
                        Add("type", type);
                        var subType = KindOf("sub_type", SubTypes);
                        if (create && subType is not null && type != "4")
                        {
                            throw new InvalidOperationException("Only a port monitor has a port type (sub_type).");
                        }

                        Add("sub_type", subType);
                        Add("port", NumberOf("port", 1, 65535));
                        if (create)
                        {
                            var keyword = KindOf("keyword_type", KeywordTypes);
                            var word = Values.Get(p, "keyword_value");
                            if (type == "2" && (keyword is null || word is null))
                            {
                                throw new InvalidOperationException("A keyword monitor needs its keyword type and keyword (keyword_type, keyword_value).");
                            }

                            if (type != "2" && (keyword is not null || word is not null))
                            {
                                throw new InvalidOperationException("Only a keyword monitor has a keyword (keyword_type, keyword_value).");
                            }

                            Add("keyword_type", keyword);
                            Add("keyword_value", word);
                        }

                        Add("interval", NumberOf("interval", 30, 86400));
                        if (!create)
                        {
                            Add("http_method", KindOf("http_method", HttpMethods));
                            Add("status", Values.Get(p, "status") is { } status ? status.ToLowerInvariant() switch { "paused" or "pause" or "0" => "0", "active" or "resume" or "1" => "1", _ => throw new InvalidOperationException($"'{status}' is not paused or active (status).") } : null);
                            if (form.Count == 1)
                            {
                                throw new InvalidOperationException("Give what to change - its name, address, port type, port, interval, HTTP method or status.");
                            }
                        }

                        path = create ? "/newMonitor" : "/editMonitor";
                        break;
                    }

                case ResetMonitorOperation or DeleteMonitorOperation:
                    Add("id", IdOf("monitor"));
                    path = operation == ResetMonitorOperation ? "/resetMonitor" : "/deleteMonitor";
                    break;
                case AlertContactsOperation:
                    Add("alert_contacts", IdsOf("ids"));
                    (path, list) = ("/getAlertContacts", "alert_contacts");
                    break;
                case AlertContactOperation:
                    Add("alert_contacts", IdOf("alert contact"));
                    (path, list) = ("/getAlertContacts", "alert_contacts");
                    break;
                case CreateAlertContactOperation:
                    Add("friendly_name", NameOf());
                    Add("type", KindOf("type", ContactTypes, required: true));
                    Add("value", Values.Get(p, "value") ?? throw new InvalidOperationException("Give its value - an e-mail, a number or an address."));
                    path = "/newAlertContact";
                    break;
                case UpdateAlertContactOperation:
                    Add("id", IdOf("alert contact"));
                    Add("friendly_name", Values.Get(p, "friendly_name"));
                    Add("value", Values.Get(p, "value"));
                    path = form.Count > 1 ? "/editAlertContact" : throw new InvalidOperationException("Give what to change - its name or value.");
                    break;
                case DeleteAlertContactOperation:
                    Add("id", IdOf("alert contact"));
                    path = "/deleteAlertContact";
                    break;
                case MaintenanceWindowsOperation:
                    Add("mwindows", IdsOf("ids"));
                    (path, list) = ("/getMWindows", "mwindows");
                    break;
                case MaintenanceWindowOperation:
                    Add("mwindows", IdOf("maintenance window"));
                    (path, list) = ("/getMWindows", "mwindows");
                    break;
                case CreateMaintenanceWindowOperation:
                    {
                        Add("friendly_name", NameOf());
                        var type = KindOf("type", WindowTypes, required: true);
                        Add("type", type);
                        Add("value", DaysOf(type));
                        Add("start_time", StartOf(type, required: true));
                        Add("duration", NumberOf("duration", 1, 525600, required: true));
                        path = "/newMWindow";
                        break;
                    }

                case UpdateMaintenanceWindowOperation:
                    {
                        Add("id", IdOf("maintenance window"));
                        Add("duration", NumberOf("duration", 1, 525600, required: true));
                        Add("friendly_name", Values.Get(p, "friendly_name"));
                        var type = KindOf("type", WindowTypes);
                        Add("type", type);
                        Add("value", type is null ? Values.Get(p, "value") is null ? null : throw new InvalidOperationException("Give the window's type with its days (type, value).") : DaysOf(type));
                        Add("start_time", type is null ? Values.Get(p, "start_time") is null ? null : throw new InvalidOperationException("Give the window's type with its start (type, start_time).") : StartOf(type, required: false));
                        path = "/editMWindow";
                        break;
                    }

                case DeleteMaintenanceWindowOperation:
                    Add("id", IdOf("maintenance window"));
                    path = "/deleteMWindow";
                    break;
                case StatusPagesOperation:
                    Add("psps", IdsOf("ids"));
                    (path, list) = ("/getPSPs", "psps");
                    break;
                case StatusPageOperation:
                    Add("psps", IdOf("status page"));
                    (path, list) = ("/getPSPs", "psps");
                    break;
                case CreateStatusPageOperation:
                    Add("friendly_name", NameOf());
                    Add("monitors", Values.Get(p, "monitors") is { } monitors ? monitors == "0" || Ids.IsMatch(monitors) ? monitors : throw new InvalidOperationException($"'{monitors}' is not monitors' ids joined by '-', or 0 for all (monitors).") : throw new InvalidOperationException("Give its monitors - their ids joined by '-', or 0 for all."));
                    Add("custom_domain", Values.Get(p, "custom_domain") is { } domain ? Regex.IsMatch(domain, "^[A-Za-z0-9.-]{1,253}$") && domain.Contains('.') ? domain : throw new InvalidOperationException($"'{domain}' is not a domain - status.example.com (custom_domain).") : null);
                    Add("sort", KindOf("sort", Sorts));
                    path = "/newPSP";
                    break;
                default:
                    Add("id", IdOf("status page"));
                    path = "/deletePSP";
                    break;
            }

            using var client = session.Client;
            if (operation is MonitorsOperation or AlertContactsOperation or MaintenanceWindowsOperation or StatusPagesOperation)
            {
                // UptimeRobot pages by offset: the size fixed at the start, the next page where this one ended - a short page
                // skips nothing - its total or an empty page the end.
                var size = Math.Min(PageSize, max);
                var read = 0;
                var offset = 0;
                while (true)
                {
                    var answer = await session.PostAsync(path, form.Append(new("offset", offset.ToString(CultureInfo.InvariantCulture))).Append(new("limit", size.ToString(CultureInfo.InvariantCulture))), cancellationToken);
                    var items = (answer?[list!] as JsonArray ?? new JsonArray()).OfType<JsonObject>().ToList();
                    foreach (var item in items)
                    {
                        yield return Record(item);
                        if (++read >= max)
                        {
                            yield break;
                        }
                    }

                    // Its total in its pagination - or, for the alert contacts, beside its list.
                    var total = (answer?["pagination"]?["total"] ?? answer?["total"]) is JsonValue count && count.TryGetValue<long>(out var all) ? all : (long?)null;
                    offset += items.Count;
                    if (items.Count == 0 || total is null || offset >= total)
                    {
                        yield break;
                    }
                }
            }

            var result = await session.PostAsync(path, form, cancellationToken) as JsonObject ?? new JsonObject();
            var record = list is not null ? (result[list] as JsonArray)?.OfType<JsonObject>().FirstOrDefault() ?? throw new InvalidOperationException("UptimeRobot has none with that id.")
                : operation == AccountOperation ? result["account"] as JsonObject
                : result.Where(pair => pair.Key is not "stat").Select(pair => pair.Value).OfType<JsonObject>().FirstOrDefault();

            // An answer with no object of its own - a window deleted - its own words.
            yield return Record(record ?? new JsonObject(result.Where(pair => pair.Key is not "stat").Select(pair => KeyValuePair.Create(pair.Key, pair.Value?.DeepClone()))));
        }

        private static ConnectorRecord Record(JsonObject item) => new(item.ToDictionary(pair => pair.Key, pair => Value(pair.Value), StringComparer.Ordinal));

        private static object? Value(JsonNode? node) => node switch
        {
            null => null,
            JsonValue text when text.TryGetValue<string>(out var s) => s,
            JsonValue whole when whole.TryGetValue<long>(out var l) => l,
            JsonValue real when real.TryGetValue<double>(out var d) => d,
            var other => other.ToJsonString(Wire),
        };

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        // One connection's calls: UptimeRobot's API v2, every call a POST with the key and format=json in its form; a throttled
        // call waiting as UptimeRobot says; its stat anything but ok a failure with its words.
        private sealed class Session
        {
            private readonly string baseUrl;
            private readonly string key;

            private Session(string baseUrl, string key, HttpClient client)
            {
                this.baseUrl = baseUrl;
                this.key = key;
                Client = client;
            }

            public HttpClient Client { get; }

            public static Session Of(string endpoint, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout) =>
                new(Values.BaseUrl(endpoint, DefaultEndpoint), Values.Get(credentials, "api-key") ?? throw new InvalidOperationException("The UptimeRobot connection has no API key."), egress.CreateHttpClient(timeout));

            public async Task<JsonNode?> PostAsync(string path, IEnumerable<KeyValuePair<string, string>> form, CancellationToken cancellationToken)
            {
                var fields = form.Prepend(new("format", "json")).Prepend(new("api_key", key)).ToList();
                using var response = await RetryAfterPolicy.Default.SendAsync(Client, () => new HttpRequestMessage(HttpMethod.Post, baseUrl + path) { Content = new FormUrlEncodedContent(fields) }, cancellationToken);
                var answer = await response.Content.ReadAsStringAsync(cancellationToken);
                JsonNode? json = null;
                try
                {
                    json = string.IsNullOrWhiteSpace(answer) ? null : JsonNode.Parse(answer);
                }
                catch (JsonException) when (response.IsSuccessStatusCode)
                {
                    // A page that is not UptimeRobot's - a sign-in page, a proxy's - read as no monitors would hide that it never was.
                    throw new InvalidOperationException($"UptimeRobot answered HTTP {(int)response.StatusCode} with what is not JSON - check the address: a sign-in page or a proxy answers so.");
                }
                catch (JsonException)
                {
                }

                var error = (json as JsonObject)?["error"] as JsonObject;
                var said = string.Join(": ", new[] { Text(error?["type"]), Text(error?["message"]) }.Where(part => !string.IsNullOrEmpty(part)));
                if (!response.IsSuccessStatusCode || Text((json as JsonObject)?["stat"]) != "ok")
                {
                    // UptimeRobot says ok with its stat - anything else, a 200 among them, is a refusal.
                    throw new InvalidOperationException($"{(response.IsSuccessStatusCode ? "UptimeRobot refused it" : $"HTTP {(int)response.StatusCode}")}{(said.Length == 0 ? "." : ": " + (said.Length > 300 ? said[..300] : said))}");
                }

                return json;
            }
        }
    }
}
