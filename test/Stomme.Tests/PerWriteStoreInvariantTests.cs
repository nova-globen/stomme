using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// THE GATE for the per-write-store invariant (the BL-11 residual): a class holding a
    /// constructor-injected framework store must not write the same row twice through that one store
    /// instance in one method. Until now the rule was tribal — three worked examples
    /// (<c>UrlMappingRepository</c>, <c>JobRepository</c>, <c>JobLogRepository</c>) and nothing stopping a
    /// FUTURE cluster from re-shipping the defect that kept the job scheduler dead on every fresh install.
    /// </summary>
    /// <remarks>
    /// <para>
    /// THE MECHANISM BEING PINNED. A store owns ONE <c>DbContext</c>, and <c>EfRepository</c> leaves every
    /// write TRACKED. A second write of the same key through the same store attaches a SECOND instance with
    /// that key and EF throws <c>InvalidOperationException: ... already being tracked</c> — measured in the
    /// database as JobLog rows opened and never finalized, every run cycle. The sanctioned shapes are:
    /// reads through a query session, never the store; a FRESH store per write for any
    /// read-then-write / multi-write-per-scope cluster (<c>NewStore() =&gt;
    /// _services.GetRequiredService&lt;IXxxStore&gt;()</c>, the D47 UrlMapping pattern); read-then-DELETE of
    /// the very instance loaded is fine, because tracking is what a delete wants.
    /// </para>
    /// <para>
    /// WHAT SYNTAX CAN AND CANNOT SEE. Key identity is a runtime fact, so this gate over-approximates with
    /// two syntactic rules and keeps a small justified allowlist, exactly as
    /// <see cref="AsyncAllTheWayDownTests"/> does. Rule MULTI-WRITE: two or more writes on one store field in
    /// one method, at least one an <c>UpdateAsync</c> (a loop of deletes over distinct rows is legal and not
    /// flagged; both measured incidents involved an update). Rule READ-THEN-UPDATE: a read
    /// (<c>GetByIdAsync</c>/<c>GetAllAsync</c>) and an <c>UpdateAsync</c> on the same store field in one
    /// method — the identity-map trap when the update attaches a mapper-fresh instance. The scanner is
    /// validated in BOTH directions below (fires on known-dirty, silent on known-clean), because instruments
    /// have lied in this repository seven times.
    /// </para>
    /// <para>
    /// SCOPE — WIDENED 2026-09-20 by D102 (BL-79 binding item 4). It used to be fields whose declared type
    /// matches the closed framework-store naming contract (<c>I…Store</c>) alone, exempting the clean-arch
    /// modules because "their handlers write once per scope by construction" — and saying that widening the
    /// gate there would be a new decision rather than a pin of the existing one. D102 IS that decision, and
    /// the exemption's premise was falsified by the row that prompted it: BL-79's rollup pass reads a site's
    /// quotas and updates them in one method, and its jobs write repeatedly through one injected repository.
    /// So INSIDE MODULE SOURCE the scope also covers a module's own closed <c>IXxxRepository</c> contract, the
    /// open generic <c>IRepository&lt;TAggregate,TId&gt;</c>, and PRIMARY-CONSTRUCTOR parameters of either —
    /// which are fields in everything but syntax and are how the clean-arch modules inject. The framework's own
    /// <c>IXxxRepository</c> fields (users, sites, aliases, settings) are a different pattern and stay OUT:
    /// the module half of the scope applies only to files under a <c>Modules/</c> directory or
    /// <c>Stomme.FeatureManagement</c>.
    /// </para>
    /// </remarks>
    public class PerWriteStoreInvariantTests
    {
        // ------------------------------------------------------------------------------------------------
        // THE ALLOWLIST. Keyed "repo/relative/path.cs::Class.Method". Keep every entry justified: an entry
        // is a claim that the flagged method's writes can never attach two instances of one key to one
        // store — say WHY. A stale entry (one the scanner no longer flags) FAILS the stale-allowlist guard,
        // so the list cannot rot.
        // ------------------------------------------------------------------------------------------------
        private static readonly IReadOnlyDictionary<string, string> AllowedMultiWriteMethods =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["src/Stomme.Server/Repository/PermissionRepository.cs::PermissionRepository.UpdatePermissionsAsync"] =
                    "Each row is written at most once per call by construction: adds create NEW identities; an " +
                    "existing row is matched by its full (EntityName, EntityId, PermissionName, RoleId, UserId) " +
                    "tuple and its in-memory mutation makes a duplicate input row a no-op on the second pass; " +
                    "deletes target rows disjoint from the writes. KNOWN residual edge, stated not hidden: two " +
                    "CONTRADICTORY duplicate input rows (same tuple, different IsAuthorized) would update one key " +
                    "twice and throw — the harvested code survived that by re-marking the same tracked instance. " +
                    "Malformed input, not a reachable production shape; move to per-write stores if it ever is.",

                // ---- The MODULE half, added when D102 widened the scope (BL-79 binding item 4). ------------
                // MEASURED at the widening: 7 methods flagged. Refining the scanner to count the busiest single
                // EXECUTION PATH rather than every descendant removed one (a true if/else). The six below were
                // each READ before being justified, not assumed: five are the early-return upsert (the update
                // branch RETURNS, so the add after it cannot also run) and one reads a row and updates THAT VERY
                // INSTANCE, which is the shape this gate's own remarks call safe. None of them can attach two
                // instances of one key to one tracked context.

                ["src/Stomme.Server/Modules/Connectivity/Application/ConnectionRequests.cs::UpdateConnectionCommandHandler.Handle"] =
                    "A read-then-update of the very instance loaded: the connection is read by id, its connector's checks " +
                    "and the name clash (a second read, by normalised name) run, its secrets are stored, then Reconfigure " +
                    "and ONE UpdateAsync on that same instance, through the storage-blind module repository that " +
                    "AddModuleAggregatePersistence hands out NON-tracking (D79's correction) - no second instance of the " +
                    "key is ever attached (BL-94 increment 6, D110).",
                ["src/Stomme.Server/Modules/Connectivity/Application/ConnectionRequests.cs::TestConnectionCommandHandler.Handle"] =
                    "A read-then-update of the very instance loaded: the connection is read by id, the runner tests it " +
                    "(no write of its own), RecordTestResult mutates that instance and ONE UpdateAsync follows, through " +
                    "the NON-tracking module repository (D79's correction) - the shape this gate's own remarks call safe " +
                    "(BL-94 increment 6, D110).",
                ["src/Stomme.Server/Modules/Datasets/Application/DatasetRequests.cs::DatasetRegistrar.RegisterOrRefreshAsync"] =
                    "An early-return upsert: the origin's dataset is read by its deterministic id-shape (FindByOriginAsync) " +
                    "and the name clash by normalised name; when the origin's row exists it is refreshed and renamed on that " +
                    "SAME instance with ONE UpdateAsync and the method RETURNS; otherwise ONE AddAsync of a new aggregate. " +
                    "The two writes cannot both run (the scanner sums across the early return), and the repository is the " +
                    "NON-tracking one AddModuleAggregatePersistence hands out (D79's correction) - BL-94 increment 10, D110.",
                ["src/Stomme.Server/Modules/Datasets/Application/DatasetRequests.cs::RefreshDatasetCommandHandler.Handle"] =
                    "A read-then-update of the very instance loaded: the dataset is read by id, Refresh mutates that instance " +
                    "and ONE UpdateAsync follows, through the NON-tracking module repository (D79's correction) - the shape " +
                    "this gate's own remarks call safe (BL-94 increment 10, D110).",
                ["src/Stomme.Server/Modules/Formats/Application/FormatRequests.cs::SaveFormatDefinitionCommandHandler.Handle"] =
                    "An upsert: the name clash is read by normalised name, then EITHER a create or a read-then-update of " +
                    "the very instance loaded (one write per path), through the storage-blind module repository that " +
                    "AddModuleAggregatePersistence hands out NON-tracking (D79's correction: reads AsNoTracking, a saved " +
                    "write detached), so the second attach the identity-map class needs cannot happen.",

                ["src/Stomme.Server/Modules/Erasure/Controllers/ErasureController.cs::ErasureController.Retry"] =
                    "Read-then-update of the very instance loaded, through the storage-blind module repository that " +
                    "AddModuleAggregatePersistence hands out NON-tracking (D79's correction: reads AsNoTracking, a saved " +
                    "write detached), so the second attach the identity-map class needs cannot happen; one write per call.",

                ["src/Stomme.Server/Modules/Erasure/Services/ServerErasureService.cs::ServerErasureService.RetryAsync"] =
                    "The controller's Retry for a server-rendered page (BL-191), the same body: read-then-update of the very " +
                    "instance loaded, through the NON-tracking storage-blind module repository (D79's correction), so the " +
                    "second attach the identity-map class needs cannot happen; one write per call.",

                ["src/Stomme.FeatureManagement/Application/FeatureAdminService.cs::FeatureAdminService.ToggleAsync"] =
                    "Early-return upsert: the absent branch adds a NEW identity and returns; the present branch " +
                    "mutates the instance it just read and updates THAT instance. Exactly one write per call.",

                ["src/Stomme.Server/Modules/Metering/Application/QuotaCommands.cs::DefineQuotaCommandHandler.Handle"] =
                    "Early-return upsert over the quota's DETERMINISTIC id: the existing branch redefines the " +
                    "instance it read and returns; the add branch is reached only when that read found nothing. " +
                    "Exactly one write per call, and never two instances of the same id.",

                ["src/Stomme.Server/Modules/Metering/Infrastructure/UsageRollupJob.cs::UsageRollupPass.RollUpAiUsageAsync"] =
                    "Per rollup id at most one write, by construction. The ledger is grouped into slices whose key " +
                    "maps 1:1 onto the deterministic row id, and for each slice the row is read by id and then " +
                    "either added (absent) or updated THROUGH THE INSTANCE JUST READ. The deletes iterate the " +
                    "rows read BEFORE the adds, filtered to ids absent from the live set - disjoint from every id " +
                    "written above, and each delete targets the instance that was read.",

                // ---- Added 2026-09-20. Two methods, each read before it was justified. --------
                ["src/Stomme.Server/Modules/Health/Application/HealthRollupPass.cs::HealthRollupPass.FoldAsync"] =
                    "The same deterministic-id upsert as RecordAsync below, one window at a time: the coarse row's " +
                    "id is computed, read, and then either added as a NEW instance (absent) or Set and updated " +
                    "THROUGH THE INSTANCE JUST READ. The branches are exclusive, each id is visited at most once " +
                    "per call because the groups are keyed by (subject, period) which maps 1:1 onto the id, and no " +
                    "mapper-fresh instance is ever constructed for an id that was also read. The SOURCE rows it " +
                    "reads are a different window from the rows it writes, so the two sets are disjoint.",
                // ---- Added 2026-09-20. One method, read before it was justified. -----------
                ["src/Stomme.Server/Modules/Health/Application/HealthProbePass.cs::HealthProbePass.RecordAsync"] =
                    "Read-then-update of THE VERY INSTANCE loaded, over a DETERMINISTIC id - the shape this " +
                    "gate's own remarks call safe, because tracking is what the update wants. The method computes " +
                    "the Minute row's id, reads it, and then either adds a NEW instance (absent) or accumulates " +
                    "into and updates THE INSTANCE IT JUST READ; the two branches cannot both run. It never " +
                    "constructs a mapper-fresh instance carrying an id it also read, which is the trap. The trim " +
                    "that follows is a separate method, reads a DIFFERENT id (48 hours behind), and deletes the " +
                    "instance it read.",

                ["src/Stomme.Server/Modules/Notifications/Application/NotificationChannelCommands.cs::ConfigureChannelCommandHandler.Handle"] =
                    "Early-return upsert (the update branch returns the existing id before the add is reached); " +
                    "the updated instance is the one read by id at the top of the method.",

                ["src/Stomme.Server/Modules/Notifications/Application/NotificationDeliveryCommands.cs::RetryNotificationRequestCommandHandler.Handle"] =
                    "Read-then-update of THE VERY INSTANCE loaded - the case the remarks above call safe, because " +
                    "tracking is what the update wants. The trap is updating a mapper-FRESH instance with the same " +
                    "key, which this method never constructs.",

                ["src/Stomme.Server/Modules/Notifications/Application/NotificationTemplateCommands.cs::SaveNotificationTemplateCommandHandler.Handle"] =
                    "Early-return upsert: the existing branch updates the instance it read and returns; the add " +
                    "branch is reached only when neither the id nor the key matched an existing template.",

                ["src/Stomme.Server/Repository/ThemeRepository.cs::ThemeRepository.ProcessThemesAsync"] =
                    "Assembly-scan sync: adds are NEW identities; an existing row is matched by ThemeName at most " +
                    "once because themes.Remove(theme) retires it from the match list (a second assembly theme " +
                    "with the same name falls to the add branch, a new identity); orphan deletes are exactly the " +
                    "rows never matched. At most one write per row per call.",

                // ---- Added 2026-09-23. One method, read before it was justified. ------------
                ["src/Stomme.Server/Modules/ContextualHelp/Infrastructure/HelpTopicReconciler.cs::HelpTopicReconciler.ReconcileAsync"] =
                    "The deterministic-id upsert, once per contributed topic: the loop walks the registry, whose keys " +
                    "are UNIQUE by construction (a duplicate (anchor, locale) is rejected on every side), and each key " +
                    "maps 1:1 onto one row id. For each, the row is read by that id and then EITHER added as a NEW " +
                    "instance (absent - and `continue`) OR updated THROUGH THE INSTANCE JUST READ (TryApplySeedUpdate " +
                    "on it); the branches cannot both run, and no mapper-fresh instance is ever built for an id that " +
                    "was read. The storage-blind registration is non-tracking besides (BL-160).",

                ["src/Stomme.Server/Modules/ContextualHelp/Application/HelpMessages.cs::SaveHelpTopicCommandHandler.Handle"] =
                    "Early-return upsert: the row is read ONCE (by id, or by its deterministic (anchor, locale) key); " +
                    "the existing branch edits THAT instance, updates it and returns; the add branch is reached only " +
                    "when the read found nothing, and adds a NEW identity. Exactly one write per call.",

                // ---- Added 2026-10-09 by BL-229 (step comments). One method, read before it was justified. ------------
                ["src/Stomme.Server/Modules/Pipelines/Comments/PipelineStepComments.cs::PipelineStepCommentErasureParticipant.EraseAsync"] =
                    "Two loops over DISJOINT rows, each row written once through the instance just read: the deletes are " +
                    "the subject's own comments (UserId == subject), the updates others' comments the subject resolved - " +
                    "filtered to UserId != subject, so no row is both deleted and updated, and no mapper-fresh instance is " +
                    "built. The storage-blind registration is non-tracking besides (BL-160).",
            };

        // ------------------------------------------------------------------------------------------------
        // RULE — no unjustified multi-write / read-then-update through one ctor-injected store.
        // ------------------------------------------------------------------------------------------------

        [Fact]
        public void No_repository_writes_the_same_row_twice_through_one_store_instance()
        {
            var violations = FrameworkSourceFiles()
                .SelectMany(ScanFile)
                .Where(v => !AllowedMultiWriteMethods.ContainsKey(v.Key))
                .ToList();

            Assert.True(
                violations.Count == 0,
                "A ctor-injected store owns ONE context and EfRepository leaves writes TRACKED (BL-51/D47): " +
                "use a fresh store per write (the JobRepository NewStore() pattern), or justify the method in " +
                "the allowlist. Found " + violations.Count + " unjustified method(s):" + Environment.NewLine +
                string.Join(Environment.NewLine, violations.Select(v => "  " + v)));
        }

        /// <summary>
        /// An allowlist entry that no longer matches a flagged method is a stale claim — the method was
        /// renamed, rewritten or removed, and the entry would silently permit a future violation under the
        /// same name.
        /// </summary>
        [Fact]
        public void The_allowlist_contains_no_stale_entries()
        {
            var flagged = FrameworkSourceFiles().SelectMany(ScanFile).Select(v => v.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var stale = AllowedMultiWriteMethods.Keys.Where(key => !flagged.Contains(key)).ToList();

            Assert.True(
                stale.Count == 0,
                "Stale allowlist entr(ies) — the scanner no longer flags: " + string.Join(", ", stale));
        }

        // ------------------------------------------------------------------------------------------------
        // THE INSTRUMENT CHECK — both directions, on input whose answer is known by construction.
        // ------------------------------------------------------------------------------------------------

        [Fact]
        public void The_scanner_reports_the_patterns_it_claims_to_report()
        {
            var dirty = ScanText(@"
                class DirtyRepository
                {
                    private readonly IWidgetStore _store;

                    // MULTI-WRITE: add then update through one store — the BL-11 scheduler shape.
                    public async System.Threading.Tasks.Task A(Widget w)
                    {
                        await _store.AddAsync(w);
                        await _store.UpdateAsync(w);
                    }

                    // READ-THEN-UPDATE: load through the store, then attach a mapper-fresh instance.
                    public async System.Threading.Tasks.Task B(int id, Widget model)
                    {
                        var loaded = await _store.GetByIdAsync(id);
                        await _store.UpdateAsync(Map(model));
                    }

                    // MULTI-WRITE: two updates in one loop body count even without an add.
                    public async System.Threading.Tasks.Task C(Widget x, Widget y)
                    {
                        await _store.UpdateAsync(x);
                        await _store.UpdateAsync(y);
                    }

                    private Widget Map(Widget w) => w;
                }");

            Assert.Equal(3, dirty.Count);
        }

        [Fact]
        public void The_scanner_is_silent_on_the_sanctioned_shapes()
        {
            var clean = ScanText(@"
                class CleanRepository
                {
                    private readonly IWidgetStore _store;
                    private readonly System.IServiceProvider _services;

                    // The D47/BL-11 pattern: a FRESH store per write is not a field access.
                    private IWidgetStore NewStore() => Resolve();
                    private IWidgetStore Resolve() => null;

                    public async System.Threading.Tasks.Task A(Widget w)
                    {
                        await NewStore().AddAsync(w);
                        await NewStore().UpdateAsync(w);
                    }

                    // Read-then-DELETE of the very instance loaded (D19's sanctioned exception).
                    public async System.Threading.Tasks.Task B(int id)
                    {
                        var aggregate = await _store.GetByIdAsync(id);
                        await _store.DeleteAsync(aggregate);
                    }

                    // A loop of deletes over distinct rows read through a SESSION, not the store.
                    public async System.Threading.Tasks.Task C(System.Collections.Generic.List<Widget> rows)
                    {
                        foreach (var row in rows)
                        {
                            await _store.DeleteAsync(row);
                        }
                    }

                    // One write per method is the normal shape.
                    public async System.Threading.Tasks.Task D(Widget w)
                    {
                        await _store.UpdateAsync(w);
                    }
                }");

            Assert.Empty(clean);
        }

        /// <summary>
        /// Guards the guard: the live scan must actually FIND ctor-injected store fields, or the rule test
        /// passes vacuously over an empty set. A floor, not an exact count — an exact count is the
        /// hand-maintained list wearing a disguise.
        /// </summary>
        [Fact]
        public void The_store_field_discovery_is_not_vacuous()
        {
            var classesWithStoreFields = FrameworkSourceFiles()
                .Sum(file => CountStoreFieldClasses(ParseFile(file)));

            Assert.True(
                classesWithStoreFields >= 10,
                "Expected the scan to find the framework's ctor-injected store fields; found " +
                classesWithStoreFields + " class(es). The discovery predicate has probably stopped matching.");
        }

        // ------------------------------------------------------------------------------------------------
        // The scanner.
        // ------------------------------------------------------------------------------------------------

        private static readonly Regex StoreTypeName = new(@"^I[A-Z]\w*Store$", RegexOptions.Compiled);

        /// <summary>
        /// The MODULE half of the scope, widened by D102 (BL-79 binding item 4).
        /// </summary>
        /// <remarks>
        /// The scope note above used to exempt the clean-arch modules on the grounds that "their handlers write once
        /// per scope by construction". BL-79 falsified that: its rollup pass reads a site's quotas and updates them
        /// in one method, and its jobs write repeatedly through one injected repository - so the exemption named a
        /// property the code no longer has. The widening is deliberately NARROW: a module's own closed repository
        /// contract (<c>Stomme.Modules.*</c> / the module persistence namespaces declare <c>IXxxRepository</c>
        /// deriving from <c>IRepository&lt;,&gt;</c>) and the open generic itself. The FRAMEWORK's many
        /// <c>IXxxRepository</c> fields are a different pattern and stay out: they are matched only inside module
        /// source, which <see cref="ModuleSourceFiles"/> decides.
        /// </remarks>
        private static readonly Regex ModuleRepositoryTypeName = new(@"^I[A-Z]\w*Repository$", RegexOptions.Compiled);
        private static readonly HashSet<string> WriteMembers = new(StringComparer.Ordinal) { "AddAsync", "UpdateAsync", "DeleteAsync" };
        private static readonly HashSet<string> ReadMembers = new(StringComparer.Ordinal) { "GetByIdAsync", "GetAllAsync" };

        private sealed record Violation(string File, int Line, string ClassName, string Method, string Rule, string Detail)
        {
            public string Key => RepoRelative(File) + "::" + ClassName + "." + Method;

            public override string ToString()
                => RepoRelative(File) + "(" + Line + "): " + ClassName + "." + Method + " — " + Rule + " (" + Detail + ")";
        }

        private static List<Violation> ScanText(string source)
            => Scan(CSharpSyntaxTree.ParseText(source, path: "<inline>"), "<inline>");

        private static SyntaxTree ParseFile(string file)
            => CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file);

        private static List<Violation> ScanFile(string file)
            => Scan(ParseFile(file), file);

        private static int CountStoreFieldClasses(SyntaxTree tree)
            => tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>().Count(t => StoreFields(t).Count > 0);

        /// <summary>Names of fields on the type whose declared type matches the closed-store naming contract.</summary>
        private static HashSet<string> StoreFields(TypeDeclarationSyntax type, bool moduleScope = false)
        {
            var fields = new HashSet<string>(StringComparer.Ordinal);

            foreach (var field in type.Members.OfType<FieldDeclarationSyntax>())
            {
                if (InScope(field.Declaration.Type, moduleScope))
                {
                    foreach (var variable in field.Declaration.Variables)
                    {
                        fields.Add(variable.Identifier.ValueText);
                    }
                }
            }

            // Primary-constructor parameters are FIELDS in everything but syntax, and the clean-arch modules use
            // them throughout - a scan that read only field declarations would be blind to the shape it was
            // widened for.
            if (moduleScope && type is ClassDeclarationSyntax { ParameterList: { } parameters })
            {
                foreach (var parameter in parameters.Parameters)
                {
                    if (parameter.Type is not null && InScope(parameter.Type, moduleScope))
                    {
                        fields.Add(parameter.Identifier.ValueText);
                    }
                }
            }

            return fields;
        }

        /// <summary>Whether a declared type is in scope: a closed framework store always, a module repository only inside module source.</summary>
        private static bool InScope(TypeSyntax declared, bool moduleScope) => declared switch
        {
            IdentifierNameSyntax { Identifier.ValueText: var name } =>
                StoreTypeName.IsMatch(name) || (moduleScope && ModuleRepositoryTypeName.IsMatch(name)),
            GenericNameSyntax { Identifier.ValueText: "IRepository" } => moduleScope,
            _ => false
        };

        /// <summary>
        /// Module source: the in-tree modules and the example modules. The FRAMEWORK's own <c>IXxxRepository</c>
        /// fields (users, sites, aliases, settings) are a different pattern - they are not the clean-arch
        /// per-aggregate repository this widening is about - so they stay out of the module half of the scope.
        /// </summary>
        private static bool IsModuleSource(string file)
        {
            var relative = RepoRelative(file);
            return relative.Contains("/Modules/", StringComparison.OrdinalIgnoreCase)
                || relative.StartsWith("src/Stomme.FeatureManagement/", StringComparison.OrdinalIgnoreCase);
        }

        private static List<Violation> Scan(SyntaxTree tree, string file)
        {
            var violations = new List<Violation>();
            var moduleScope = IsModuleSource(file);

            foreach (var type in tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                var storeFields = StoreFields(type, moduleScope);
                if (storeFields.Count == 0)
                {
                    continue;
                }

                foreach (var method in type.Members.OfType<MethodDeclarationSyntax>())
                {
                    // Per store field: what this method does through it. Local functions and lambdas inside
                    // the body count toward the method — they run in the same scope over the same field.
                    var writes = new Dictionary<string, int>(StringComparer.Ordinal);
                    var updates = new Dictionary<string, int>(StringComparer.Ordinal);
                    var reads = new Dictionary<string, int>(StringComparer.Ordinal);

                    foreach (var field in storeFields)
                    {
                        var counts = CountOnOnePath(method.Body ?? (SyntaxNode)method.ExpressionBody, field);
                        writes[field] = counts.Writes;
                        updates[field] = counts.Updates;
                        reads[field] = counts.Reads;
                    }

                    foreach (var field in storeFields)
                    {
                        var w = writes.GetValueOrDefault(field);
                        var u = updates.GetValueOrDefault(field);
                        var r = reads.GetValueOrDefault(field);

                        if (w >= 2 && u >= 1)
                        {
                            violations.Add(NewViolation(file, type, method, "MULTI-WRITE",
                                w + " writes (" + u + " update(s)) on '" + field + "'"));
                        }
                        else if (r >= 1 && u >= 1)
                        {
                            violations.Add(NewViolation(file, type, method, "READ-THEN-UPDATE",
                                r + " read(s) + " + u + " update(s) on '" + field + "'"));
                        }
                    }
                }
            }

            return violations;
        }

        /// <summary>What one field is read and written on the BUSIEST SINGLE EXECUTION PATH through a node.</summary>
        /// <remarks>
        /// <b>Why a path and not a total.</b> The rule is about two writes of one key reaching one tracked context
        /// in one call - so writes that CANNOT both run are not two writes. The upsert every clean-arch handler is
        /// built from (<c>if (existing is not null) Update; else Add</c>) counts as ONE write here, which is what it
        /// is at run time; counting descendants instead reported it as two, and would have forced five
        /// near-identical allowlist entries whose justification was always "the branches are exclusive". An
        /// <c>if</c> contributes the MAXIMUM of its branches and a <c>switch</c> the maximum of its sections;
        /// everything else sums. Deliberately conservative: a LOOP still sums, because a loop body genuinely runs
        /// many times, and a write inside a condition still counts.
        /// </remarks>
        private static PathCounts CountOnOnePath(SyntaxNode node, string field)
        {
            if (node is null)
            {
                return default;
            }

            if (node is InvocationExpressionSyntax invocation
                && invocation.Expression is MemberAccessExpressionSyntax
                {
                    Expression: IdentifierNameSyntax { Identifier.ValueText: var receiver },
                    Name.Identifier.ValueText: var member
                }
                && receiver == field)
            {
                // An argument can hold a further call on the same field, so it is counted too.
                var inner = Sum(invocation.ArgumentList, field);
                if (WriteMembers.Contains(member))
                {
                    return inner.Add(1, member == "UpdateAsync" ? 1 : 0, 0);
                }

                return ReadMembers.Contains(member) ? inner.Add(0, 0, 1) : inner;
            }

            if (node is IfStatementSyntax branch)
            {
                var taken = CountOnOnePath(branch.Statement, field);
                var otherwise = CountOnOnePath(branch.Else?.Statement, field);
                return Sum(branch.Condition, field).Add(PathCounts.Max(taken, otherwise));
            }

            if (node is SwitchStatementSyntax choice)
            {
                var busiest = default(PathCounts);
                foreach (var section in choice.Sections)
                {
                    busiest = PathCounts.Max(busiest, Sum(section, field));
                }

                return Sum(choice.Expression, field).Add(busiest);
            }

            return Sum(node, field);
        }

        /// <summary>Every child counted and added - the ordinary case, where the statements all run.</summary>
        private static PathCounts Sum(SyntaxNode node, string field)
        {
            var total = default(PathCounts);
            if (node is null)
            {
                return total;
            }

            foreach (var child in node.ChildNodes())
            {
                total = total.Add(CountOnOnePath(child, field));
            }

            return total;
        }

        private readonly record struct PathCounts(int Writes, int Updates, int Reads)
        {
            public PathCounts Add(PathCounts other) => new(Writes + other.Writes, Updates + other.Updates, Reads + other.Reads);

            public PathCounts Add(int writes, int updates, int reads) => new(Writes + writes, Updates + updates, Reads + reads);

            public static PathCounts Max(PathCounts left, PathCounts right) =>
                new(Math.Max(left.Writes, right.Writes), Math.Max(left.Updates, right.Updates), Math.Max(left.Reads, right.Reads));
        }

        private static Violation NewViolation(string file, TypeDeclarationSyntax type, MethodDeclarationSyntax method, string rule, string detail)
        {
            var line = method.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            return new Violation(file, line, type.Identifier.ValueText, method.Identifier.ValueText, rule, detail);
        }

        // ------------------------------------------------------------------------------------------------
        // Scope (mirrors AsyncAllTheWayDownTests).
        // ------------------------------------------------------------------------------------------------

        private static IEnumerable<string> FrameworkSourceFiles()
        {
            var src = Path.Combine(RepoRoot(), "src");

            return Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
                .Where(f => !IsUnderDirectory(f, "obj"))
                .Where(f => !IsUnderDirectory(f, "bin"))
                .Where(f => !RepoRelative(f).StartsWith("src/Stomme.Application/", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
        }

        private static bool IsUnderDirectory(string file, string segment)
            => RepoRelative(file).Split('/').Contains(segment, StringComparer.OrdinalIgnoreCase);

        private static string RepoRelative(string file)
        {
            var root = RepoRoot();
            var full = Path.GetFullPath(file);

            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                ? full.Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/')
                : full.Replace('\\', '/');
        }

        private static string? _repoRoot;

        private static string RepoRoot()
        {
            if (_repoRoot is not null)
            {
                return _repoRoot;
            }

            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Stomme.slnx")))
            {
                directory = directory.Parent;
            }

            Assert.True(directory is not null, "Could not locate the repository root (Stomme.slnx) from " + AppContext.BaseDirectory);

            return _repoRoot = directory!.FullName;
        }
    }
}
