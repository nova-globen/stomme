#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Connectivity;
using Stomme.Modules.Connectors.Shared;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MongoDB.Driver.Core.Events;

namespace Stomme.Modules.Connectors.MongoDb
{
    /// <summary>
    /// MongoDB (n8n's MongoDB node: a collection's documents) over the official driver (Apache-2.0): the endpoint is a
    /// <c>mongodb://</c> address of ONE server - <c>mongodb://db.example.com:27017/kassa?authSource=admin&amp;tls=true</c> -
    /// and never a password: the Basic scheme's <c>username</c> and <c>password</c> come from the per-site secret store. A test
    /// pings the database and names the server's version.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>find</c> - by a query, sorted, projected, skipped and no further than <c>max</c>; <c>insert</c> - a document or an
    /// array of them, each back with its <c>_id</c>; <c>update</c> - a document's fields set on the one its key matches (an
    /// ObjectId's 24 hex digits read as one), created when asked; <c>find_and_update</c> and <c>find_and_replace</c> - the
    /// document as it is afterwards; <c>delete</c> - by a query that matches SOMETHING (an empty one deletes every document, so
    /// it is refused unless <c>all</c> says so); <c>aggregate</c> - a pipeline. Queries and documents are MongoDB's extended
    /// JSON (<c>{"$oid": ...}</c>, <c>{"$date": ...}</c>). A document's own fields come back as fields, an ObjectId as its hex,
    /// a nested document or array as JSON.
    /// </para>
    /// <para>
    /// <b>The driver dials only the server the host checked, and no other.</b> Its host goes to
    /// <see cref="IConnectorEgress.ResolveAndPinAsync"/> first; the permitted address replaces it, unless TLS needs the name to
    /// check the certificate (then the name stays, and the driver resolves it again - a known window for drivers that re-resolve,
    /// stated, not solved). The driver is held to a DIRECT connection, so it never dials the replica set's members a server
    /// names, and every connection it opens is checked against that one server and refused otherwise. So a <c>mongodb+srv</c>
    /// address (its members are DNS's), a list of servers, a replica set, a proxy and a password in the address are refused
    /// before anything is dialled. An Atlas cluster is reached by naming its primary's host, with <c>tls=true</c>.
    /// </para>
    /// </remarks>
    public sealed class MongoDbConnector : IConnector, IConnectorExtractor
    {
        public const string FindOperation = "find";

        public const string InsertOperation = "insert";

        public const string UpdateOperation = "update";

        public const string FindAndUpdateOperation = "find_and_update";

        public const string FindAndReplaceOperation = "find_and_replace";

        public const string DeleteOperation = "delete";

        public const string AggregateOperation = "aggregate";

        public const int DefaultMax = 1000;

        private static readonly string[] Known = { FindOperation, InsertOperation, UpdateOperation, FindAndUpdateOperation, FindAndReplaceOperation, DeleteOperation, AggregateOperation };

        private static readonly Regex Collection = new("^[^$\\x00]{1,255}$", RegexOptions.CultureInvariant);

        private static readonly Regex Database = new("^[^/\\\\. \"$*<>:|?\\x00]{1,63}$", RegexOptions.CultureInvariant);

        private static readonly Regex FieldName = new("^[^$\\x00][^\\x00]{0,254}$", RegexOptions.CultureInvariant);

        private static readonly Regex ObjectIdHex = new("^[0-9a-fA-F]{24}$", RegexOptions.CultureInvariant);

        private static readonly JsonWriterSettings Relaxed = new() { OutputMode = JsonOutputMode.RelaxedExtendedJson };

        public string ConnectorType => "mongodb";

        public string DisplayName => "MongoDB";

        public AuthenticationType AuthType => AuthenticationType.Basic;

        public ConnectorEndpointKind EndpointKind => ConnectorEndpointKind.ConnectionString;

        public string? EndpointExample => "mongodb://db.example.com:27017/kassa?authSource=admin&tls=true";

        public IReadOnlyList<ConnectorCategory> Categories { get; } = new[] { ConnectorCategory.DataAndFiles };

        public Uri? WebsiteUrl => new("https://www.mongodb.com");

        public Uri? ApiDocumentationUrl => new("https://www.mongodb.com/docs/manual/crud/");

        public ConnectorConfigSchema ConfigSchema { get; } = new(new[]
        {
            new ConnectorConfigField("database", "Database", ConnectorConfigFieldType.Text, Required: false, Description: "The database - when the address names none."),
        });

        private static ConnectorConfigField Field(string name, string label, string description, bool required = false, ConnectorConfigFieldType type = ConnectorConfigFieldType.Text) => new(name, label, type, Required: required, Description: description);

        private static readonly ConnectorConfigField CollectionField = Field("collection", "Collection", "The collection - orders.", required: true);

        private static readonly ConnectorConfigField KeyField = Field("key", "Match on", "The field the document is found by - _id (the default), or another, as n8n's Update Key.") with { Default = "_id" };

        private static readonly ConnectorConfigField DocumentField = Field("document", "Document", "A JSON object of the fields - the key's value among them: {\"_id\": \"6512...\", \"status\": \"paid\"}.", required: true, type: ConnectorConfigFieldType.Multiline);

        private static readonly ConnectorConfigField UpsertField = Field("upsert", "Create when missing", "true to create the document when none matches.");

        private static ConnectorConfigSchema Schema(params ConnectorConfigField[] fields) => new(fields);

        public IReadOnlyList<ConnectorOperation> Operations { get; } = new[]
        {
            new ConnectorOperation(FindOperation, "Find documents", "A collection's documents by a query, sorted, projected and no further than asked - n8n's Find.")
            {
                Parameters = Schema(CollectionField, Field("query", "Query", "Extended JSON - {\"status\": \"open\"}. Empty: every document.", type: ConnectorConfigFieldType.Multiline),
                    Field("sort", "Sort", "{\"created\": -1}"), Field("projection", "Fields", "{\"kund\": 1, \"summa\": 1}"), Field("skip", "Skip", "How many to pass over.", type: ConnectorConfigFieldType.Integer),
                    Field("max", "At most", "How many, at most.", type: ConnectorConfigFieldType.Integer) with { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) }),
            },
            new ConnectorOperation(InsertOperation, "Insert documents", "A document, or a JSON array of them, each back with its _id - n8n's Insert.")
            {
                Parameters = Schema(CollectionField, Field("documents", "Documents", "A JSON object, or an array of them.", required: true, type: ConnectorConfigFieldType.Multiline)),
            },
            new ConnectorOperation(UpdateOperation, "Update a document", "A document's fields set on the one its key matches - n8n's Update.") { Parameters = Schema(CollectionField, KeyField, DocumentField, UpsertField) },
            new ConnectorOperation(FindAndUpdateOperation, "Find and update a document", "As Update, the document back as it is afterwards - n8n's Find And Update.") { Parameters = Schema(CollectionField, KeyField, DocumentField, UpsertField) },
            new ConnectorOperation(FindAndReplaceOperation, "Find and replace a document", "The one its key matches replaced by the document, back as it is afterwards - n8n's Find And Replace.") { Parameters = Schema(CollectionField, KeyField, DocumentField, UpsertField) },
            new ConnectorOperation(DeleteOperation, "Delete documents", "The documents a query matches - n8n's Delete. An empty query deletes every one, so it needs all = true.")
            {
                Parameters = Schema(CollectionField, Field("query", "Query", "Extended JSON - {\"status\": \"void\"}.", required: true, type: ConnectorConfigFieldType.Multiline), Field("all", "Every document", "true to let an empty query delete every document.")),
            },
            new ConnectorOperation(AggregateOperation, "Aggregate", "A pipeline's results - n8n's Aggregate.")
            {
                Parameters = Schema(CollectionField, Field("pipeline", "Pipeline", "A JSON array of stages - [{\"$match\": {\"status\": \"paid\"}}, {\"$group\": {\"_id\": \"$kund\", \"summa\": {\"$sum\": \"$summa\"}}}].", required: true, type: ConnectorConfigFieldType.Multiline),
                    Field("max", "At most", "How many, at most.", type: ConnectorConfigFieldType.Integer) with { Default = DefaultMax.ToString(CultureInfo.InvariantCulture) }),
            },
        };

        public async Task<ConnectionTestResult> TestConnectionAsync(ConnectorTestContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            (MongoUrl Url, string? Database) address;
            try
            {
                address = Parse(context.Endpoint, context.Settings);
            }
            catch (ArgumentException ex)
            {
                return ConnectionTestResult.Fail($"Invalid MongoDB address: {ex.Message}");
            }

            var settings = await SettingsAsync(address.Url, context.Credentials, context.Egress, context.Timeout, cancellationToken);
            try
            {
                using var client = new MongoClient(settings);
                await client.GetDatabase(address.Database ?? "admin").RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: cancellationToken);
                var build = await client.GetDatabase("admin").RunCommandAsync<BsonDocument>(new BsonDocument("buildInfo", 1), cancellationToken: cancellationToken);
                return ConnectionTestResult.Ok($"Connected to MongoDB {build.GetValue("version", "?")}.");
            }
            catch (Exception ex) when (ex is MongoException or TimeoutException or SocketException)
            {
                return ConnectionTestResult.Fail(ex.Message); // the server's or the network's failure; the message stays inside the runner
            }
        }

        public async IAsyncEnumerable<ConnectorRecord> ExtractAsync(ConnectorExtractContext context, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            var operation = Known.FirstOrDefault(known => string.Equals(known, (context.OperationName ?? FindOperation).Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"MongoDB has no operation '{context.OperationName}' - {string.Join(", ", Known[..^1])} or {Known[^1]}.");
            var p = context.Parameters;

            // Everything held to its shape before anything is dialled.
            var (url, database) = Parse(context.Endpoint, context.Settings);
            if (database is null)
            {
                throw new InvalidOperationException("Give the database - in the address (mongodb://db.example.com/kassa) or the connection's setting.");
            }

            var collection = Values.Get(p, "collection") is { } named
                ? Collection.IsMatch(named) && !named.StartsWith("system.", StringComparison.Ordinal) ? named : throw new InvalidOperationException($"'{named}' is not a collection's name (collection).")
                : throw new InvalidOperationException("Give the collection (collection).");
            var max = Number(p, "max", DefaultMax, 1);
            var skip = Number(p, "skip", 0, 0);
            var key = Values.GetOrDefault(p, "key", "_id");
            if (!FieldName.IsMatch(key))
            {
                throw new InvalidOperationException($"'{key}' is not a field's name (key).");
            }

            var upsert = Flag(p, "upsert");
            var query = operation is FindOperation or DeleteOperation ? Document(Values.Get(p, "query") ?? "{}", "query") : null;
            if (operation == DeleteOperation && query!.ElementCount == 0 && !Flag(p, "all"))
            {
                throw new InvalidOperationException("An empty query deletes every document of the collection - give a query, or all = true to mean it.");
            }

            var sort = operation == FindOperation && Values.Get(p, "sort") is { } sorted ? Document(sorted, "sort") : null;
            var projection = operation == FindOperation && Values.Get(p, "projection") is { } fields ? Document(fields, "projection") : null;
            var documents = operation == InsertOperation ? Documents(Values.Get(p, "documents") ?? throw new InvalidOperationException("Give the documents - a JSON object, or an array of them (documents)."), "documents") : null;
            var document = operation is UpdateOperation or FindAndUpdateOperation or FindAndReplaceOperation
                ? Document(Values.Get(p, "document") ?? throw new InvalidOperationException("Give the document - a JSON object of its fields, the key's among them (document)."), "document")
                : null;
            BsonDocument? filter = null;
            if (document is not null)
            {
                if (!document.TryGetValue(key, out var match))
                {
                    throw new InvalidOperationException($"The document has no '{key}' - the value it is found by is one of its fields (document).");
                }

                filter = new BsonDocument(key, key == "_id" && match is BsonString hex && ObjectIdHex.IsMatch(hex.Value) ? new ObjectId(hex.Value) : match);
                if (operation != FindAndReplaceOperation && document.ElementCount == 1)
                {
                    throw new InvalidOperationException($"The document has no field to set but '{key}' (document).");
                }
            }

            var pipeline = operation == AggregateOperation ? Stages(Values.Get(p, "pipeline") ?? throw new InvalidOperationException("Give the pipeline - a JSON array of stages (pipeline)."), "pipeline") : null;

            var settings = await SettingsAsync(url, context.Credentials, context.Egress, context.Timeout, cancellationToken);
            using var client = new MongoClient(settings);
            var items = client.GetDatabase(database).GetCollection<BsonDocument>(collection);
            switch (operation)
            {
                case FindOperation:
                    {
                        var find = items.Find(query).Skip(skip).Limit(max);
                        if (sort is not null)
                        {
                            find = find.Sort(sort);
                        }

                        using var cursor = projection is null ? await find.ToCursorAsync(cancellationToken) : await find.Project(projection).ToCursorAsync(cancellationToken);
                        while (await cursor.MoveNextAsync(cancellationToken))
                        {
                            foreach (var found in cursor.Current)
                            {
                                yield return Record(found);
                            }
                        }

                        break;
                    }

                case InsertOperation:
                    await items.InsertManyAsync(documents, cancellationToken: cancellationToken);
                    foreach (var inserted in documents!)
                    {
                        yield return Record(inserted);
                    }

                    break;
                case UpdateOperation:
                    {
                        var changes = new BsonDocument("$set", new BsonDocument(document!.Where(field => field.Name != key)));
                        var result = await items.UpdateOneAsync(filter, changes, new UpdateOptions { IsUpsert = upsert }, cancellationToken);
                        yield return Fields(("matched", result.MatchedCount), ("modified", result.ModifiedCount), ("upserted_id", result.UpsertedId is null ? null : Clr(result.UpsertedId)));
                        break;
                    }

                case FindAndUpdateOperation:
                    {
                        var changes = new BsonDocument("$set", new BsonDocument(document!.Where(field => field.Name != key)));
                        var after = await items.FindOneAndUpdateAsync<BsonDocument>(filter, changes, new FindOneAndUpdateOptions<BsonDocument> { IsUpsert = upsert, ReturnDocument = ReturnDocument.After }, cancellationToken);
                        if (after is not null)
                        {
                            yield return Record(after);
                        }

                        break;
                    }

                case FindAndReplaceOperation:
                    {
                        var replacement = new BsonDocument(document!.Where(field => field.Name != "_id"));
                        var after = await items.FindOneAndReplaceAsync<BsonDocument>(filter, replacement, new FindOneAndReplaceOptions<BsonDocument> { IsUpsert = upsert, ReturnDocument = ReturnDocument.After }, cancellationToken);
                        if (after is not null)
                        {
                            yield return Record(after);
                        }

                        break;
                    }

                case DeleteOperation:
                    {
                        var result = await items.DeleteManyAsync(query, cancellationToken);
                        yield return Fields(("deleted", result.DeletedCount));
                        break;
                    }

                default:
                    {
                        using var cursor = await items.AggregateAsync(PipelineDefinition<BsonDocument, BsonDocument>.Create(pipeline!), cancellationToken: cancellationToken);
                        var read = 0;
                        while (read < max && await cursor.MoveNextAsync(cancellationToken))
                        {
                            foreach (var found in cursor.Current)
                            {
                                if (read++ == max)
                                {
                                    yield break;
                                }

                                yield return Record(found);
                            }
                        }

                        break;
                    }
            }
        }

        /// <summary>The address held to ONE server and no secret; its database - the address's, or the connection's setting.</summary>
        internal static (MongoUrl Url, string? Database) Parse(string endpoint, IReadOnlyDictionary<string, string> settings)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                throw new ArgumentException("it names no server - mongodb://db.example.com:27017/kassa.");
            }

            MongoUrl url;
            try
            {
                url = MongoUrl.Create(endpoint.Trim());
            }
            catch (MongoConfigurationException ex)
            {
                throw new ArgumentException(ex.Message, ex);
            }

            if (url.Scheme == MongoDB.Driver.Core.Configuration.ConnectionStringScheme.MongoDBPlusSrv)
            {
                throw new ConnectorEgressDeniedException("A mongodb+srv address's servers are DNS's to name, not the address's - name the server (Atlas: its primary's host, with tls=true).");
            }

            if (!string.IsNullOrEmpty(url.Username) || !string.IsNullOrEmpty(url.Password))
            {
                throw new ArgumentException("a user or a password is never part of the address - give them as the connection's credentials.");
            }

            if (url.Servers.Count() != 1)
            {
                throw new ConnectorEgressDeniedException("A MongoDB connection names ONE server; a list of servers cannot be pinned to a checked address.");
            }

            if (!string.IsNullOrEmpty(url.ReplicaSetName))
            {
                throw new ConnectorEgressDeniedException("A MongoDB connection is direct to one server; a replica set's members are named by the server, and are not dialled.");
            }

            if (!string.IsNullOrEmpty(url.ProxyHost))
            {
                throw new ConnectorEgressDeniedException("A MongoDB connection is dialled by the host, not through a proxy the address names.");
            }

            var database = url.DatabaseName ?? Values.Get(settings, "database");
            if (database is not null && !Database.IsMatch(database))
            {
                throw new ArgumentException($"'{database}' is not a database's name.");
            }

            return (url, database);
        }

        /// <summary>The client's settings: the one server checked through the egress (pinned unless TLS needs its name), held direct, every connection checked against it; the scheme's credentials; the timeouts clamped.</summary>
        internal static async Task<MongoClientSettings> SettingsAsync(MongoUrl url, IReadOnlyDictionary<string, string> credentials, IConnectorEgress egress, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var server = url.Server;
            var pinned = await egress.ResolveAndPinAsync(server.Host, server.Port, cancellationToken)
                ?? throw new ConnectorEgressDeniedException($"The database host '{server.Host}' resolves to no address this site's egress policy permits (a private host must be on the site's allowlist).");
            var dialled = url.UseTls ? server : new MongoServerAddress(pinned.ToString(), server.Port);
            var settings = MongoClientSettings.FromUrl(url);
            settings.Server = dialled;
            settings.DirectConnection = true;
            settings.ApplicationName = "Stomme";
            var span = TimeSpan.FromMilliseconds(Math.Clamp(timeout.TotalMilliseconds, 1000, 120_000));
            settings.ConnectTimeout = span;
            settings.ServerSelectionTimeout = span;
            settings.SocketTimeout = span;
            if (credentials.TryGetValue("username", out var user) && !string.IsNullOrWhiteSpace(user))
            {
                settings.Credential = MongoCredential.CreateCredential(url.AuthenticationSource ?? url.DatabaseName ?? "admin", user.Trim(), credentials.TryGetValue("password", out var password) ? password : "");
            }

            settings.ClusterConfigurator = cluster => cluster.Subscribe<ConnectionOpeningEvent>(opening => Guard(opening.ServerId.EndPoint, dialled));
            return settings;
        }

        /// <summary>Every connection the driver opens: to the one server it was given, or refused.</summary>
        internal static void Guard(EndPoint endpoint, MongoServerAddress dialled)
        {
            var (host, port) = endpoint switch
            {
                DnsEndPoint named => (named.Host, named.Port),
                IPEndPoint address => (address.Address.ToString(), address.Port),
                _ => (endpoint.ToString() ?? "", -1),
            };
            if (port != dialled.Port || !string.Equals(host.Trim('[', ']'), dialled.Host.Trim('[', ']'), StringComparison.OrdinalIgnoreCase))
            {
                throw new ConnectorEgressDeniedException($"The MongoDB driver was to dial another server ({endpoint}) - only the server this site's egress policy checked is dialled.");
            }
        }

        private static int Number(IReadOnlyDictionary<string, string> p, string name, int fallback, int least) =>
            Values.Get(p, name) is not { } given ? fallback
            : int.TryParse(given, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= least ? n
            : throw new InvalidOperationException($"'{given}' is not a number{(least > 0 ? " above 0" : "")} ({name}).");

        private static bool Flag(IReadOnlyDictionary<string, string> p, string name) =>
            Values.Get(p, name) is { } given && (bool.TryParse(given, out var yes) ? yes : throw new InvalidOperationException($"'{given}' is not true or false ({name})."));

        // A JSON object in MongoDB's extended JSON.
        internal static BsonDocument Document(string json, string name)
        {
            try
            {
                return BsonDocument.Parse(json);
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or ArgumentException or BsonSerializationException)
            {
                throw new InvalidOperationException($"'{Cut(json)}' is not a JSON object ({name}).");
            }
        }

        // A JSON object or an array of them.
        private static List<BsonDocument> Documents(string json, string name)
        {
            if (!json.TrimStart().StartsWith('['))
            {
                return new List<BsonDocument> { Document(json, name) };
            }

            var array = Array(json, name);
            return array.Count > 0 && array.All(item => item is BsonDocument)
                ? array.Cast<BsonDocument>().ToList()
                : throw new InvalidOperationException($"The documents are a JSON object, or an array of objects ({name}).");
        }

        private static List<BsonDocument> Stages(string json, string name)
        {
            var array = Array(json, name);
            return array.Count > 0 && array.All(item => item is BsonDocument stage && stage.ElementCount == 1 && stage.GetElement(0).Name.StartsWith('$'))
                ? array.Cast<BsonDocument>().ToList()
                : throw new InvalidOperationException($"The pipeline is a JSON array of stages, each one $stage - [{{\"$match\": {{...}}}}] ({name}).");
        }

        private static BsonArray Array(string json, string name)
        {
            try
            {
                return BsonSerializer.Deserialize<BsonArray>(json);
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or ArgumentException or BsonSerializationException)
            {
                throw new InvalidOperationException($"'{Cut(json)}' is not a JSON array ({name}).");
            }
        }

        private static string Cut(string text) => text.Length > 60 ? text[..60] + "..." : text;

        /// <summary>A document as a record: its own fields, an ObjectId as its hex, a date as one, a nested document or array as JSON.</summary>
        internal static ConnectorRecord Record(BsonDocument document) =>
            new(document.ToDictionary(field => field.Name, field => Clr(field.Value), StringComparer.Ordinal));

        private static object? Clr(BsonValue value) => value.BsonType switch
        {
            BsonType.Null or BsonType.Undefined => null,
            BsonType.String => value.AsString,
            BsonType.Int32 => value.AsInt32,
            BsonType.Int64 => value.AsInt64,
            BsonType.Double => value.AsDouble,
            BsonType.Boolean => value.AsBoolean,
            BsonType.Decimal128 => (decimal)value.AsDecimal128,
            BsonType.ObjectId => value.AsObjectId.ToString(),
            BsonType.DateTime => value.ToUniversalTime(),
            _ => value.ToJson(Relaxed),
        };

        private static ConnectorRecord Fields(params (string Name, object? Value)[] fields) =>
            new(fields.ToDictionary(field => field.Name, field => field.Value, StringComparer.Ordinal));
    }
}
