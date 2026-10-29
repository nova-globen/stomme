using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// The gate for the sync-over-async ban: sync-over-async is banned in framework code, and Stomme-owned store-touching
    /// interfaces are async-only. This is a gate, not a sentence - a rule written down in prose alone was followed by a
    /// commit that shipped a build which could not render a page.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHY THE REAL PARSER AND NOT A TEXT SCAN. Naive scanning has produced false positives in this
    /// repository three times. Measured on this codebase before the cascade, a
    /// <c>grep '\.Result\b|\.GetAwaiter\(\)\.GetResult\(\)|\.Wait\(\)'</c> over <c>src/</c> reported 17 hits
    /// of which 7 were WRONG: five <c>match.Result("${key}")</c> in <c>TokenReplace.cs</c> (that is
    /// <see cref="System.Text.RegularExpressions.Match.Result(string)"/>, a method call, not
    /// <c>Task.Result</c>), one <c>context.Result = new AntiforgeryValidationFailedResult()</c> in
    /// <c>AutoValidateAntiforgeryTokenFilter.cs</c> (an assignment to an MVC property), and one occurrence
    /// inside a <c>&lt;remarks&gt;</c> COMMENT in <c>SeamInterop.cs</c>. A gate that cries wolf gets
    /// suppressed, and a suppressed gate is how such a defect ships. The C# parser tells a call from a comment and a
    /// read from a write by construction.
    /// </para>
    /// <para>
    /// WHY SYNTAX AND NOT IL. IL would give exact type information, but every <c>await</c> compiles to
    /// <c>awaiter.GetResult()</c> inside the generated state machine's <c>MoveNext</c>, so an IL scan for
    /// <c>GetResult</c> flags every async method in the framework — the instrument would be measuring the
    /// opposite of what it claims. In SOURCE, <c>await x</c> is an <see cref="AwaitExpressionSyntax"/> and is
    /// never an invocation of <c>GetResult</c>, so the discrimination is free and total.
    /// </para>
    /// <para>
    /// WHY THE SEMANTIC MODEL AND NOT SYNTAX ALONE. The parser tells a call from a
    /// comment, but it cannot tell <c>Task&lt;T&gt;.Result</c> from a property that merely happens to be
    /// named <c>Result</c>. Vendoring the pipeline engine brought in a whole vocabulary of the latter — a
    /// step's <c>Result</c> OUTPUT, a pipeline run's <c>Result</c> value, a completion signal's
    /// <c>Result</c>, and an <c>IStep.GetResult(StepExecutionContext)</c> extension — and the syntactic
    /// rules reported SEVEN blocking waits in a closure that contains none (measured: zero <c>.Wait()</c>,
    /// zero <c>GetAwaiter().GetResult()</c>). The <c>.Result</c> and <c>GetResult()</c> rules are therefore
    /// RECEIVER-TYPE-AWARE: a candidate is a violation only when the receiver is a Task/ValueTask (for
    /// <c>.Result</c>) or an awaiter (for <c>GetResult()</c>). The ban did not move and the allowlist is
    /// still exactly one entry. A receiver whose type cannot be RESOLVED is reported as a violation, never
    /// waved through: a precision rule that fails open is a gate that passes vacuously.
    /// </para>
    /// </remarks>
    public class AsyncAllTheWayDownTests
    {
        // ------------------------------------------------------------------------------------------------
        // THE PERMITTED-BOUNDARY ALLOWLIST. Keep it SMALL and keep every entry justified — a boundary kept
        // for convenience is a violation. Each entry names a platform contract Stomme does NOT own.
        // Every entry here must also be recorded in the sync-over-async decision. Do not add one without doing both.
        // ------------------------------------------------------------------------------------------------

        /// <summary>
        /// Files permitted to contain a blocking wait, each because a contract Stomme does not own is
        /// synchronous at that exact point and has no async form.
        /// </summary>
        private static readonly IReadOnlyDictionary<string, string> AllowedBlockingWaits =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                // ONE entry. See the sync-over-async decision for the full argument and the rejected alternative.
                //
                // .NET MAUI's entry point is `protected override MauiApp CreateMauiApp()` on
                // MauiWinUIApplication / MauiApplication / MauiUIApplicationDelegate. It is SYNCHRONOUS and
                // has no async form — Stomme does not own it and cannot await inside it. CreateMauiApp
                // must download the client assemblies BEFORE it finishes, because RegisterModuleServices
                // reflects over them to populate the DI container it is in the middle of building.
                //
                // This is NOT the deadlock class: the wait is wrapped in Task.Run, so the awaits happen
                // on a threadpool thread that has no SynchronizationContext to capture, and nothing here
                // touches EF Core, Npgsql, or a Blazor renderer.
                //
                // The honest alternative — HttpClient.Send, which is GENUINELY synchronous I/O and would
                // need no entry here — was rejected on evidence: the native Android/iOS handlers throw
                // PlatformNotSupportedException for sync Send, and those targets are commented-out-but-
                // intended in Stomme.Maui.csproj (line 6). Taking it would trade a real boundary for a
                // latent runtime crash on the platforms this shell exists to serve.
                ["src/Stomme.Maui/MauiProgram.cs"] =
                    "MAUI's CreateMauiApp() is a synchronous platform contract with no async form, and the " +
                    "client assemblies must be loaded before DI registration completes inside it.",
            };

        /// <summary>
        /// Sync members permitted on a store-touching interface, keyed by "IInterface.Member".
        /// </summary>
        private static readonly IReadOnlyDictionary<string, string> AllowedSyncMembers =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                // (empty - see the sync-over-async decision.)
            };

        // ------------------------------------------------------------------------------------------------
        // RULE 1 — no blocking waits in framework source.
        // ------------------------------------------------------------------------------------------------

        [Fact]
        public void Framework_code_contains_no_sync_over_async_blocking_waits()
        {
            var violations = FrameworkViolations.Value
                .Where(v => !AllowedBlockingWaits.ContainsKey(RepoRelative(v.File)))
                .ToList();

            Assert.True(
                violations.Count == 0,
                "Sync-over-async is banned in framework code. Found " + violations.Count +
                " blocking wait(s):" + Environment.NewLine +
                string.Join(Environment.NewLine, violations.Select(v => "  " + v)));
        }

        /// <summary>
        /// SeamInterop was the sanctioned home of the framework's blocking waits. It is retired, not improved. This
        /// asserts the file is gone, so the debt cannot be re-parked in a helper that the allowlist above
        /// would then have to name.
        /// </summary>
        [Fact]
        public void The_sync_over_async_bridge_is_deleted()
        {
            var seam = Path.Combine(RepoRoot(), "src", "Stomme.Server", "Infrastructure", "SeamInterop.cs");

            Assert.False(
                File.Exists(seam),
                "SeamInterop.cs must not exist: the sync-over-async bridge is retired, not improved.");
        }

        // ------------------------------------------------------------------------------------------------
        // THE INSTRUMENT CHECK. A rule-enforcing test that has never been shown to fire is worthless, and a
        // scanner that cannot be shown to be silent on clean input cries wolf. Both directions, on input
        // whose answer is known by construction, so they cannot rot as the codebase moves.
        // ------------------------------------------------------------------------------------------------

        [Fact]
        public void The_scanner_reports_the_patterns_it_claims_to_report()
        {
            var dirty = ScanText(@"
                class Dirty
                {
                    void A(System.Threading.Tasks.Task<int> t) { var x = t.Result; }
                    void B(System.Threading.Tasks.Task t) { t.GetAwaiter().GetResult(); }
                    void C(System.Threading.Tasks.Task t) { t.Wait(); }
                    void D(System.Threading.Tasks.Task<int> t)
                        { var x = System.Threading.Tasks.Task.Run(() => t).GetAwaiter().GetResult(); }
                }");

            Assert.Equal(4, dirty.Count);
        }

        [Fact]
        public void The_scanner_is_silent_on_the_shapes_that_are_not_violations()
        {
            // Every line here is a real shape from this repository that a naive text scan flagged. If the
            // scanner regresses to matching text, this test fails and names which shape it broke on.
            var clean = ScanText(@"
                using System.Text.RegularExpressions;
                class Clean
                {
                    // A comment mentioning .GetAwaiter().GetResult() and .Result and .Wait() is not code.
                    string Doc = "" .Result and .GetAwaiter().GetResult() in a string literal "";

                    // TokenReplace.cs: Regex Match.Result(string) is a METHOD, not Task.Result.
                    string A(Match match) => match.Result(""${key}"");

                    // AutoValidateAntiforgeryTokenFilter.cs: assigning an MVC property named Result.
                    void B(Microsoft.AspNetCore.Mvc.Filters.AuthorizationFilterContext context)
                        { context.Result = null; }

                    // The whole point of the exercise: awaiting is not blocking.
                    async System.Threading.Tasks.Task<int> C(System.Threading.Tasks.Task<int> t)
                        => await t;

                    // The vendored pipeline engine's own vocabulary. Seven shapes of this family were
                    // reported as blocking waits by the syntactic rules; not one of them is a Task.
                    object? D(StepCompletedContext context) => context.Result;
                    object? E(IStep step) => step.GetResult(0);
                    string F() => nameof(System.Threading.Tasks.Task<object>.Result);
                }

                // The receivers the shapes above read from: a result-carrying context, and a step whose
                // GetResult is the engine's own extension method rather than an awaiter's.
                class StepCompletedContext { public object? Result { get; set; } }
                interface IStep { }
                static class StepExtensions
                {
                    public static object? GetResult(this IStep step, int context) => null;
                }");

            Assert.Empty(clean);
        }

        /// <summary>
        /// An allowlist entry that no longer names a blocking wait is a permission nobody needs — and a
        /// permission nobody needs is how an allowlist grows. It also proves the receiver-type-aware rules
        /// still SEE the one boundary the decision permits (MAUI's <c>Task.Run(...).GetAwaiter().GetResult()</c>):
        /// that file is silent in the gate because it is ALLOWED, not because the scan stopped finding it.
        /// </summary>
        [Fact]
        public void Every_allowlist_entry_still_names_a_real_blocking_wait()
        {
            var files = FrameworkViolations.Value
                .Select(v => RepoRelative(v.File))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in AllowedBlockingWaits.Keys)
            {
                Assert.True(
                    files.Contains(entry),
                    "Allowlist entry '" + entry + "' no longer contains a blocking wait. Delete the entry: " +
                    "a stale permission is how an allowlist grows.");
            }
        }
        // ------------------------------------------------------------------------------------------------
        // RULE 2 — Stomme-owned store-touching interfaces are async-only.
        // ------------------------------------------------------------------------------------------------

        [Fact]
        public void Store_touching_interfaces_expose_no_synchronous_methods()
        {
            LoadFrameworkAssemblies();

            var violations = new List<string>();

            foreach (var type in StoreTouchingInterfaces())
            {
                foreach (var method in type.GetMethods())
                {
                    if (IsAwaitable(method.ReturnType))
                    {
                        continue;
                    }

                    var key = type.Name + "." + method.Name;
                    if (AllowedSyncMembers.ContainsKey(key))
                    {
                        continue;
                    }

                    violations.Add("  " + type.FullName + "." + method.Name +
                        " returns " + FriendlyName(method.ReturnType) + " (expected Task/Task<T>/ValueTask" +
                        "/IAsyncEnumerable<T>)");
                }
            }

            Assert.True(
                violations.Count == 0,
                "Store-touching Stomme interfaces must be async all the way down. Found " +
                violations.Count + " sync member(s):" + Environment.NewLine +
                string.Join(Environment.NewLine, violations.OrderBy(v => v, StringComparer.Ordinal)));
        }

        /// <summary>
        /// Guards the guard: if the discovery predicate stops matching (a namespace rename, a moved
        /// assembly), <see cref="Store_touching_interfaces_expose_no_synchronous_methods"/> would pass
        /// vacuously over an empty set — green, and blind. That is precisely how a skipped gate ships.
        /// </summary>
        [Fact]
        public void The_store_touching_interface_discovery_is_not_vacuous()
        {
            LoadFrameworkAssemblies();

            var found = StoreTouchingInterfaces().ToList();

            Assert.True(
                found.Count >= 25,
                "Expected the scan to find the framework's repository/store interfaces; found " +
                found.Count + ". The discovery predicate has probably stopped matching.");

            // The clusters the rule names, by name, so a silent narrowing of the scan is a failure.
            foreach (var required in new[]
                     {
                         "IPermissionRepository", "ISiteRepository", "IPageRepository", "IModuleRepository",
                         "IUserRepository", "IRoleRepository", "IUserRoleRepository", "ISettingRepository",
                         "IVisitorRepository", "IPageModuleRepository",
                     })
            {
                Assert.Contains(required, found.Select(t => t.Name));
            }
        }

        // ------------------------------------------------------------------------------------------------
        // The scanner.
        // ------------------------------------------------------------------------------------------------

        private sealed record Violation(string File, int Line, string Pattern, string Snippet)
        {
            public override string ToString()
                => RepoRelative(File) + "(" + Line + "): " + Pattern + " -> " + Snippet;
        }

        /// <summary>
        /// Which rule produced a candidate — and therefore what the RECEIVER's type has to be for that
        /// candidate to be a real blocking wait.
        /// </summary>
        private enum Rule
        {
            /// <summary><c>.Wait()</c>/<c>.WaitAll()</c>/<c>.WaitAny()</c>: blocking by definition, and no
            /// other member of that name is reached this way. Purely syntactic, unchanged.</summary>
            BlockingWaitCall,

            /// <summary><c>x.GetResult()</c>: a blocking wait only when <c>x</c> IS an awaiter.</summary>
            AwaiterGetResult,

            /// <summary><c>x.Result</c>: a blocking wait only when <c>x</c> is a Task/ValueTask (or an
            /// awaiter).</summary>
            TaskResultRead,
        }

        /// <summary>A syntactic match. Whether it is a VIOLATION depends on the receiver's type.</summary>
        private sealed record Candidate(
            string File,
            SyntaxTree Tree,
            SyntaxNode Node,
            ExpressionSyntax Receiver,
            Rule Rule,
            string Pattern);

        /// <summary>
        /// The framework, scanned once: two facts consult the result and parsing every file is the expensive
        /// half.
        /// </summary>
        private static readonly Lazy<IReadOnlyList<Violation>> FrameworkViolations =
            new(ScanFrameworkSource, isThreadSafe: true);

        /// <summary>
        /// Parses every framework source file ONCE, collects the syntactic candidates, and judges each one
        /// against the semantic model of the compilation its own project would produce.
        /// </summary>
        private static IReadOnlyList<Violation> ScanFrameworkSource()
        {
            var treesByFile = new Dictionary<string, SyntaxTree>(StringComparer.OrdinalIgnoreCase);
            var candidates = new List<Candidate>();

            foreach (var file in FrameworkSourceFiles())
            {
                var tree = Parse(file);
                treesByFile[file] = tree;
                candidates.AddRange(FindCandidates(tree, file));
            }

            var models = new Dictionary<SyntaxTree, SemanticModel>();
            var compilations = new Dictionary<string, CSharpCompilation>(StringComparer.OrdinalIgnoreCase);

            foreach (var group in candidates
                         .Where(c => c.Rule != Rule.BlockingWaitCall)
                         .GroupBy(c => OwningProject(c.File) ?? string.Empty, StringComparer.OrdinalIgnoreCase))
            {
                if (group.Key.Length == 0)
                {
                    // No owning .csproj: there is nothing to bind against, and Judge() reports every
                    // candidate in such a file as UNRESOLVED rather than waving it through.
                    continue;
                }

                var compilation = CompilationFor(
                    group.Key, treesByFile, compilations, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

                foreach (var candidate in group)
                {
                    models[candidate.Tree] = compilation.GetSemanticModel(candidate.Tree);
                }
            }

            var violations = new List<Violation>();

            foreach (var candidate in candidates)
            {
                models.TryGetValue(candidate.Tree, out var model);

                var violation = Judge(candidate, model);
                if (violation is not null)
                {
                    violations.Add(violation);
                }
            }

            return violations;
        }

        /// <summary>
        /// The inline instrument checks: one tree, compiled against the same reference set as the real scan,
        /// so the both-directions checks exercise the SAME judgement the framework scan uses.
        /// </summary>
        private static List<Violation> ScanText(string source)
        {
            var tree = CSharpSyntaxTree.ParseText(source, ParseOptions, path: "<inline>");
            var compilation = CSharpCompilation.Create(
                "InlineScan", new[] { tree }, PlatformReferences(), CompilationOptions);
            var model = compilation.GetSemanticModel(tree);

            return FindCandidates(tree, "<inline>")
                .Select(candidate => Judge(candidate, model))
                .Where(violation => violation is not null)
                .Select(violation => violation!)
                .ToList();
        }

        /// <summary>
        /// SYNTAX — the candidates. Cheap, total, and deliberately over-inclusive: it is the semantic pass
        /// below that decides which of these is a wait.
        /// </summary>
        private static List<Candidate> FindCandidates(SyntaxTree tree, string file)
        {
            var candidates = new List<Candidate>();
            var root = tree.GetRoot();

            // `.GetResult()` — in SOURCE an `await` is an AwaitExpressionSyntax and never an invocation of
            // GetResult, so the compiler's own call lives in generated IL this scanner never sees. This
            // catches `t.GetAwaiter().GetResult()` and the detached
            // `var a = t.GetAwaiter(); a.GetResult();` form in one rule.
            //
            // `.Wait()`/`.WaitAll()`/`.WaitAny()` — blocking by definition on a Task.
            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (invocation.Expression is not MemberAccessExpressionSyntax member)
                {
                    continue;
                }

                var name = member.Name.Identifier.ValueText;

                if (name is "Wait" or "WaitAll" or "WaitAny")
                {
                    // Monitor.Wait is a lock primitive, not sync-over-async: it is the only `Wait` in the
                    // BCL whose purpose is co-operative and whose receiver is a type, not a Task.
                    if (name == "Wait" && member.Expression is IdentifierNameSyntax { Identifier.ValueText: "Monitor" })
                    {
                        continue;
                    }

                    candidates.Add(new Candidate(
                        file, tree, invocation, member.Expression, Rule.BlockingWaitCall, "." + name + "()"));
                }
                else if (name == "GetResult")
                {
                    candidates.Add(new Candidate(
                        file, tree, invocation, member.Expression, Rule.AwaiterGetResult, ".GetResult()"));
                }
            }

            // `.Result` — a READ of a member named Result that is not itself a call. Excluding invocation
            // receivers is what keeps Regex's `match.Result("${key}")` out; excluding assignment targets is
            // what keeps MVC's `context.Result = ...` out.
            foreach (var member in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
            {
                if (member.Name.Identifier.ValueText != "Result")
                {
                    continue;
                }

                // `match.Result(...)` — the member access is the callee of an invocation.
                if (member.Parent is InvocationExpressionSyntax invocation && invocation.Expression == member)
                {
                    continue;
                }

                // `context.Result = ...` — the member access is the target of an assignment.
                if (member.Parent is AssignmentExpressionSyntax assignment && assignment.Left == member)
                {
                    continue;
                }

                candidates.Add(new Candidate(
                    file, tree, member, member.Expression, Rule.TaskResultRead, ".Result"));
            }

            return candidates;
        }

        /// <summary>
        /// SEMANTICS — the verdict. A candidate is a violation only when the receiver's TYPE makes it one.
        /// A receiver whose type cannot be resolved is reported as a violation too: a precision rule that
        /// fails open is a gate that passes vacuously, which is exactly the failure mode of a skipped gate.
        /// </summary>
        private static Violation? Judge(Candidate candidate, SemanticModel? model)
        {
            if (candidate.Rule == Rule.BlockingWaitCall)
            {
                return Describe(candidate, candidate.Pattern);
            }

            if (model is null)
            {
                return Describe(candidate, candidate.Pattern + " [receiver UNRESOLVED: no compilation for this file]");
            }

            // `nameof(Task<int>.Result)` and any other type-qualified access: the receiver NAMES a type,
            // nothing is evaluated, and nothing can block.
            if (model.GetSymbolInfo(candidate.Receiver).Symbol is ITypeSymbol or INamespaceSymbol)
            {
                return null;
            }

            var type = model.GetTypeInfo(candidate.Receiver).Type;

            if (type is null || type.TypeKind == TypeKind.Error)
            {
                return Describe(candidate, candidate.Pattern + " [receiver UNRESOLVED: " + candidate.Receiver + "]");
            }

            var blocking = candidate.Rule == Rule.AwaiterGetResult
                ? IsAwaiter(type, model.Compilation)
                : IsTaskLike(type, model.Compilation) || IsAwaiter(type, model.Compilation);

            return blocking
                ? Describe(candidate, candidate.Pattern + " on " + type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat))
                : null;
        }

        /// <summary>
        /// Task/Task&lt;T&gt;/ValueTask&lt;T&gt;, or anything deriving from one. Reading `Result` on one of
        /// these is the deadlock shape; reading `Result` on anything else is reading a property that
        /// merely happens to be called Result.
        /// </summary>
        private static bool IsTaskLike(ITypeSymbol type, Compilation compilation)
        {
            var task = compilation.GetTypeByMetadataName("System.Threading.Tasks.Task");
            var taskOfT = compilation.GetTypeByMetadataName("System.Threading.Tasks.Task`1");
            var valueTask = compilation.GetTypeByMetadataName("System.Threading.Tasks.ValueTask");
            var valueTaskOfT = compilation.GetTypeByMetadataName("System.Threading.Tasks.ValueTask`1");

            Assert.True(
                task is not null && taskOfT is not null && valueTask is not null && valueTaskOfT is not null,
                "The scan's compilation cannot resolve System.Threading.Tasks.Task: the reference set is " +
                "broken, and a rule that cannot recognise a Task would pass VACUOUSLY.");

            for (ITypeSymbol? current = type; current is not null; current = current.BaseType)
            {
                if (current is not INamedTypeSymbol named)
                {
                    break;
                }

                var definition = named.OriginalDefinition;

                if (SymbolEqualityComparer.Default.Equals(definition, task)
                    || SymbolEqualityComparer.Default.Equals(definition, taskOfT)
                    || SymbolEqualityComparer.Default.Equals(definition, valueTask)
                    || SymbolEqualityComparer.Default.Equals(definition, valueTaskOfT))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// An awaiter — a type implementing <c>INotifyCompletion</c>: TaskAwaiter, ValueTaskAwaiter, the
        /// ConfiguredTaskAwaitable/ConfiguredValueTaskAwaitable awaiters, and any hand-written one.
        /// <c>GetResult()</c> on one of these is the blocking half of an `await`. <c>GetResult()</c> on
        /// anything else is an ordinary method: the vendored engine ships
        /// <c>IStep.GetResult(StepExecutionContext)</c>, which reads a step's OUTPUT.
        /// </summary>
        private static bool IsAwaiter(ITypeSymbol type, Compilation compilation)
        {
            var notifyCompletion = compilation.GetTypeByMetadataName("System.Runtime.CompilerServices.INotifyCompletion");

            Assert.True(
                notifyCompletion is not null,
                "The scan's compilation cannot resolve INotifyCompletion: the reference set is broken, and a " +
                "rule that cannot recognise an awaiter would pass VACUOUSLY.");

            return type.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, notifyCompletion));
        }

        private static Violation Describe(Candidate candidate, string pattern)
        {
            var line = candidate.Node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            var snippet = candidate.Node.ToString().Replace("\r", " ").Replace("\n", " ").Trim();
            if (snippet.Length > 100)
            {
                snippet = snippet.Substring(0, 100) + "...";
            }

            return new Violation(candidate.File, line, pattern, snippet);
        }

        // ------------------------------------------------------------------------------------------------
        // The compilations. One per project, from SOURCE, following the ProjectReference graph — the same
        // shape the real build has, so a receiver's type is the type the compiler would give it.
        // ------------------------------------------------------------------------------------------------

        private static readonly CSharpCompilationOptions CompilationOptions =
            new(OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true,
                nullableContextOptions: NullableContextOptions.Enable);

        /// <summary>
        /// Microsoft.NET.Sdk's implicit usings, restated. The generated GlobalUsings.g.cs lives under obj/,
        /// which this scan deliberately does not read — that would make the gate depend on which
        /// configuration was built last. Added only for projects whose csproj/props enable the feature; the
        /// vendored engine is one of them.
        /// </summary>
        private const string ImplicitUsingsSource = @"
global using global::System;
global using global::System.Collections.Generic;
global using global::System.IO;
global using global::System.Linq;
global using global::System.Net.Http;
global using global::System.Threading;
global using global::System.Threading.Tasks;
";

        /// <summary>
        /// The vendored engine is C# 14 source: its extension MEMBERS (<c>extension(IStep step) { ... }</c>)
        /// only parse — and only BIND — at the latest language version, and a member that does not bind has
        /// no receiver type, which this gate reports as UNRESOLVED. Measured: the two extension-member files
        /// in Stomme.Engine.Pipelines were exactly that, until this.
        /// </summary>
        private static readonly CSharpParseOptions ParseOptions =
            CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);

        private static SyntaxTree Parse(string file)
            => CSharpSyntaxTree.ParseText(File.ReadAllText(file), ParseOptions, path: file);

        /// <summary>
        /// The compilation a project WOULD produce: its own sources (the very syntax trees the scan read),
        /// its ProjectReference closure compiled from source, and every non-Stomme assembly this test
        /// process already trusts. Stomme's own DLLs are deliberately NOT referenced — every Stomme type
        /// here comes from source, and referencing the built DLL as well would make each of them ambiguous.
        /// </summary>
        private static CSharpCompilation CompilationFor(
            string projectFile,
            IReadOnlyDictionary<string, SyntaxTree> treesByFile,
            IDictionary<string, CSharpCompilation> cache,
            ISet<string> visiting)
        {
            if (cache.TryGetValue(projectFile, out var cached))
            {
                return cached;
            }

            var trees = new List<SyntaxTree>();

            foreach (var file in ProjectSourceFiles(projectFile))
            {
                trees.Add(treesByFile.TryGetValue(file, out var tree) ? tree : Parse(file));
            }

            if (ImplicitUsingsEnabled(projectFile))
            {
                trees.Add(CSharpSyntaxTree.ParseText(ImplicitUsingsSource, ParseOptions, path: "<implicit-usings>"));
            }

            var references = new List<MetadataReference>(PlatformReferences());

            if (visiting.Add(projectFile))
            {
                foreach (var dependency in ProjectReferencesOf(projectFile))
                {
                    // MSBuild forbids a cycle; terminate on one rather than trust that.
                    if (visiting.Contains(dependency))
                    {
                        continue;
                    }

                    references.Add(CompilationFor(dependency, treesByFile, cache, visiting).ToMetadataReference());
                }

                visiting.Remove(projectFile);
            }

            var compilation = CSharpCompilation.Create(
                Path.GetFileNameWithoutExtension(projectFile), trees, references, CompilationOptions);

            cache[projectFile] = compilation;
            return compilation;
        }

        /// <summary>
        /// Every non-Stomme assembly the test process itself trusts (the TPA list): the shared framework,
        /// ASP.NET Core, and every package this solution restores.
        /// </summary>
        private static IReadOnlyList<MetadataReference> PlatformReferences()
        {
            if (_platformReferences is not null)
            {
                return _platformReferences;
            }

            var trusted = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

            var references = new List<MetadataReference>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var path in trusted)
            {
                var simpleName = Path.GetFileNameWithoutExtension(path);

                if (simpleName.StartsWith("Stomme", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!seen.Add(simpleName) || !File.Exists(path))
                {
                    continue;
                }

                references.Add(MetadataReference.CreateFromFile(path));
            }

            Assert.True(
                references.Count > 0,
                "No metadata references were found for the scan's compilations; every receiver type would be " +
                "UNRESOLVED. The test host's trusted-platform-assemblies list is empty.");

            return _platformReferences = references;
        }

        private static IReadOnlyList<MetadataReference>? _platformReferences;

        private static IEnumerable<string> ProjectSourceFiles(string projectFile)
        {
            var directory = Path.GetDirectoryName(projectFile)!;

            return Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
                .Where(f => !IsUnderDirectory(f, "obj"))
                .Where(f => !IsUnderDirectory(f, "bin"))
                // A nested project's files belong to that project, not to this one.
                .Where(f => string.Equals(OwningProject(f), projectFile, StringComparison.OrdinalIgnoreCase));
        }

        private static IEnumerable<string> ProjectReferencesOf(string projectFile)
        {
            var directory = Path.GetDirectoryName(projectFile)!;

            return XDocument.Load(projectFile)
                .Descendants()
                .Where(element => element.Name.LocalName == "ProjectReference")
                .Select(element => (string?)element.Attribute("Include"))
                .Where(include => !string.IsNullOrWhiteSpace(include))
                .Select(include => Path.GetFullPath(
                    Path.Combine(directory, include!.Replace('\\', Path.DirectorySeparatorChar))))
                .Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static readonly Dictionary<string, string?> OwningProjects = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>The nearest ancestor .csproj — the project that compiles this file.</summary>
        private static string? OwningProject(string file)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(file))!;

            if (OwningProjects.TryGetValue(directory, out var cached))
            {
                return cached;
            }

            var root = RepoRoot();
            string? owner = null;

            for (var current = new DirectoryInfo(directory);
                 current is not null && current.FullName.StartsWith(root, StringComparison.OrdinalIgnoreCase);
                 current = current.Parent)
            {
                var project = current.EnumerateFiles("*.csproj").FirstOrDefault();

                if (project is not null)
                {
                    owner = project.FullName;
                    break;
                }
            }

            return OwningProjects[directory] = owner;
        }

        private static bool ImplicitUsingsEnabled(string projectFile)
        {
            if (File.ReadAllText(projectFile).Contains("<ImplicitUsings>enable", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var root = RepoRoot();

            for (var current = new DirectoryInfo(Path.GetDirectoryName(projectFile)!);
                 current is not null && current.FullName.StartsWith(root, StringComparison.OrdinalIgnoreCase);
                 current = current.Parent)
            {
                var props = Path.Combine(current.FullName, "Directory.Build.props");

                if (File.Exists(props)
                    && File.ReadAllText(props).Contains("<ImplicitUsings>enable", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
        // ------------------------------------------------------------------------------------------------
        // Scope.
        // ------------------------------------------------------------------------------------------------

        /// <summary>
        /// Framework code: the Stomme projects under <c>src/</c>. Excludes build output, and excludes
        /// <c>src/Stomme.Application</c>, which is a <c>dotnet new</c> TEMPLATE for downstream apps rather
        /// than framework code — it is not in any solution and is not built by CI.
        /// </summary>
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

        /// <summary>
        /// The Stomme-owned interfaces that reach a store: the repository surfaces in
        /// <c>Stomme.Repository</c>, the framework stores in <c>Stomme.Repository.Stores</c>, and the
        /// persistence seam's own contracts.
        /// </summary>
        private static IEnumerable<Type> StoreTouchingInterfaces()
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => a.GetName().Name?.StartsWith("Stomme", StringComparison.Ordinal) == true)
                .SelectMany(SafeTypes)
                .Where(t => t.IsInterface && t.IsPublic)
                .Where(t => t.Namespace is "Stomme.Repository"
                         or "Stomme.Repository.Stores"
                         or "Stomme.Persistence.Abstractions")
                .Where(t => t.Name.EndsWith("Repository", StringComparison.Ordinal)
                         || t.Name.EndsWith("Store", StringComparison.Ordinal))
                // ITenantResolver/IMigrationHistoryRepository etc. are included by the rule above only if
                // they match the suffix; that is deliberate — the suffix IS the framework's naming contract.
                .Distinct()
                .OrderBy(t => t.FullName, StringComparer.Ordinal);
        }

        private static IEnumerable<Type> SafeTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types.Where(t => t is not null)!;
            }
        }

        private static bool IsAwaitable(Type type)
        {
            if (type == typeof(Task) || type == typeof(ValueTask))
            {
                return true;
            }

            if (!type.IsGenericType)
            {
                return false;
            }

            var definition = type.GetGenericTypeDefinition();
            return definition == typeof(Task<>)
                || definition == typeof(ValueTask<>)
                || definition == typeof(IAsyncEnumerable<>);
        }

        private static string FriendlyName(Type type)
        {
            if (!type.IsGenericType)
            {
                return type.Name;
            }

            var name = type.Name;
            var tick = name.IndexOf('`');
            if (tick >= 0)
            {
                name = name.Substring(0, tick);
            }

            return name + "<" + string.Join(", ", type.GetGenericArguments().Select(FriendlyName)) + ">";
        }

        // Mirrors HostCompositionTests: a test process only loads what it has touched, so the reflection
        // scan would otherwise see a subset of the framework and pass vacuously.
        private static void LoadFrameworkAssemblies()
        {
            var probePath = Path.GetDirectoryName(typeof(AsyncAllTheWayDownTests).Assembly.Location)!;

            foreach (var dll in Directory.EnumerateFiles(probePath, "Stomme*.dll", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    Assembly.Load(AssemblyName.GetAssemblyName(dll));
                }
                catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or FileNotFoundException)
                {
                    // Not a managed Stomme assembly, or already loaded — name-based, same as the loader.
                }
            }
        }

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
