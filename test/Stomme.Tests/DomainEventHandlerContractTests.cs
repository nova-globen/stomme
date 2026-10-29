using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace Stomme.Tests
{
    /// <summary>
    /// THE GATE for the handler contract: a domain-event handler STAGES work into the caller's unit of
    /// work and NEVER manages it — no <c>SaveChanges</c>, no scope juggling, no committing or disposing the
    /// ambient session.
    /// </summary>
    /// <remarks>
    /// <para>
    /// WHY THIS EXISTS AS A TEST AND NOT A PARAGRAPH. Dispatch uses the current scope: handlers share the
    /// caller's DI scope and therefore the caller's session. That is what keeps the audit trail atomic with
    /// the change it audits and carries the scoped tenant context in without re-propagation — and it is
    /// exactly what makes a handler's <c>SaveChanges</c> a re-entrancy bug, because it flushes the OUTER unit
    /// of work's pending changes mid-operation. The contract is the price of the decision. Written rules do not
    /// hold and failing tests do: a rule written in prose ("async all the way
    /// down") was followed by a build that could not render a page.
    /// </para>
    /// <para>
    /// WHY THE REAL PARSER AND NOT A TEXT SCAN. Naive scanning has produced false positives in this
    /// repository repeatedly — <c>Regex Match.Result("${key}")</c> is a METHOD, not a Task read; a <c>btn-</c>
    /// scan matched the project's own <c>db-btn-*</c>. A text scan for "SaveChangesAsync" would flag the word
    /// in this very sentence, in an XML doc telling authors not to call it, and in <c>UnitOfWork</c>, whose
    /// JOB is to call it. The C# parser tells a call from a comment, and a handler's body from its
    /// neighbours in the same file, by construction.
    /// </para>
    /// <para>
    /// WHY SYNTAX AND NOT A SEMANTIC MODEL. A full compilation would give exact symbol identity, but the
    /// four banned shapes are decidable from syntax alone: no domain-event handler has a legitimate reason to
    /// invoke a member named <c>SaveChanges*</c>, <c>CreateScope</c>/<c>CreateAsyncScope</c>, or
    /// <c>Commit*</c>, whatever the receiver's type. Disposal is the one shape where the receiver matters —
    /// disposing a <c>MemoryStream</c> you built is fine, disposing the injected session is not — so that
    /// rule keys on whether the receiver is an INJECTED dependency (a primary-constructor parameter or a
    /// field), which is itself syntactic. See <see cref="The_scanner_is_silent_on_the_shapes_that_are_not_violations"/>.
    /// </para>
    /// <para>
    /// KNOWN LIMIT, STATED PLAINLY. This gate reads <c>src/</c>: it pins the framework's own handlers and the
    /// example every downstream author copies. It cannot see a handler compiled into a third-party module
    /// assembly, and it does not attempt to prove that a method a handler calls does not itself save — an
    /// indirect <c>SaveChanges</c> behind a helper is out of reach of any syntactic gate. It catches the
    /// direct, common, and historically-shipped mistake.
    /// </para>
    /// </remarks>
    public class DomainEventHandlerContractTests
    {
        // ------------------------------------------------------------------------------------------------
        // THE GATE.
        // ------------------------------------------------------------------------------------------------

        [Fact]
        public void Domain_event_handlers_stage_work_and_never_manage_the_unit_of_work()
        {
            var violations = FrameworkSourceFiles()
                .SelectMany(ScanFile)
                .ToList();

            Assert.True(
                violations.Count == 0,
                "Domain-event handlers must stage work into the caller's unit of work, never manage it " +
                "(docs/decisions.md D40; see the contract on IDomainEventHandler<T>). Found " +
                violations.Count + " violation(s):" + Environment.NewLine +
                string.Join(Environment.NewLine, violations.Select(v => "  " + v)));
        }

        /// <summary>
        /// Guards the guard. If the discovery predicate stops matching — a rename, a moved assembly, a
        /// handler declared with a differently-shaped base list — the gate above would pass vacuously over an
        /// empty set: green, and blind. That is exactly how a skipped test shipped here before.
        /// </summary>
        [Fact]
        public void The_handler_discovery_is_not_vacuous()
        {
            var handlers = FrameworkSourceFiles()
                .SelectMany(file => HandlerDeclarations(Parse(file)).Select(h => h.Identifier.ValueText))
                .ToList();

            // The floor and the names below were re-pinned to the current handler set, not relaxed to go
            // green: five of the six named handlers (UserRegistered, RoleAssigned, UserDeactivated, UserLockedOut
            // and SessionRevoked) lived in the Stomme.Identity.* model, which was dead code - nothing
            // composed it - and was removed. They are gone from the tree, so pinning them would
            // assert the existence of deleted files. What the guard still does is the job it was written for:
            // if the discovery predicate stops matching, the count drops below the surviving set and this fails.
            Assert.True(
                handlers.Count >= 2,
                "Expected the scan to find the framework's domain-event handlers; found " + handlers.Count +
                ". The discovery predicate has probably stopped matching.");

            // By name, so a silent narrowing of the scan fails rather than quietly shrinking the gate.
            foreach (var required in new[]
                     {
                         "FeatureFlagAuditHandler", "GuestbookEntrySignedDomainEventHandler",
                     })
            {
                Assert.Contains(required, handlers);
            }
        }

        // ------------------------------------------------------------------------------------------------
        // THE INSTRUMENT CHECK. A rule-enforcing test that has never been shown to fire is worthless, and a
        // scanner that cannot be shown to be silent on clean input cries wolf — and a suppressed gate is no
        // gate. Both directions, on input whose answer is known by construction.
        // ------------------------------------------------------------------------------------------------

        [Fact]
        public void The_scanner_reports_the_violations_it_claims_to_report()
        {
            var dirty = ScanText(@"
                using Microsoft.Extensions.DependencyInjection;
                class Dirty : IDomainEventHandler<SomethingHappened>
                {
                    private readonly DbContext _db;
                    private readonly IServiceScopeFactory _scopes;

                    public async Task HandleAsync(SomethingHappened e, CancellationToken ct = default)
                    {
                        _db.SaveChanges();                       // 1: flushes the OUTER unit of work
                        await _db.SaveChangesAsync(ct);          // 2: same, async
                        using var scope = _scopes.CreateScope(); // 3: leaves the aggregate's transaction
                        await using var a = _scopes.CreateAsyncScope();  // 4
                        await _db.Database.CommitTransactionAsync(ct);   // (not matched: not named Commit*)
                        _tx.Commit();                            // 5: commits the ambient transaction
                        await _tx.CommitAsync(ct);               // 6
                        _db.Dispose();                           // 7: disposes an INJECTED dependency
                    }
                }");

            Assert.Equal(7, dirty.Count);
        }

        [Fact]
        public void The_scanner_is_silent_on_the_shapes_that_are_not_violations()
        {
            // Every shape here is real: each is either a legitimate handler pattern or a neighbour that a
            // text scan for "SaveChangesAsync" would flag. If the scanner regresses to matching text, this
            // test fails and names the shape it broke on.
            var clean = ScanText(@"
                class NotAHandler
                {
                    // UnitOfWork's JOB is to save. It is not a handler, so the contract does not apply.
                    public Task<int> SaveChangesAsync(CancellationToken ct) => _db.SaveChangesAsync(ct);
                    void Scope(IServiceScopeFactory f) { using var s = f.CreateAsyncScope(); }
                }

                class CleanHandler : IDomainEventHandler<SomethingHappened>
                {
                    private readonly IRepository<Audit, Guid> _audits;

                    // A comment naming SaveChangesAsync() and CreateScope() is prose, not a call.
                    private const string Doc = "" do not call SaveChanges() or CreateAsyncScope() "";

                    public async Task HandleAsync(SomethingHappened e, CancellationToken ct = default)
                    {
                        // STAGING: the shape the contract asks for. The caller commits this.
                        await _audits.AddAsync(Audit.Record(e.Id), ct);

                        // Disposing something the handler itself built is not touching the ambient session.
                        using var buffer = new System.IO.MemoryStream();
                        var owned = new System.IO.MemoryStream();
                        owned.Dispose();
                    }
                }");

            Assert.Empty(clean);
        }

        // ------------------------------------------------------------------------------------------------
        // The scanner.
        // ------------------------------------------------------------------------------------------------

        private sealed record Violation(string File, int Line, string Handler, string Rule, string Snippet)
        {
            public override string ToString()
                => RepoRelative(File) + "(" + Line + "): " + Handler + " -> " + Rule + ": " + Snippet;
        }

        private static List<Violation> ScanText(string source)
            => Scan(CSharpSyntaxTree.ParseText(source, path: "<inline>"), "<inline>");

        private static List<Violation> ScanFile(string file)
            => Scan(Parse(file), file);

        private static SyntaxTree Parse(string file)
            => CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file);

        private static List<Violation> Scan(SyntaxTree tree, string file)
        {
            var violations = new List<Violation>();

            // Per HANDLER, not per file: IdentityAuditHandlers.cs holds five handlers, and a file may hold a
            // handler next to a type (like a unit of work) that is allowed to save.
            foreach (var handler in HandlerDeclarations(tree))
            {
                var handlerName = handler.Identifier.ValueText;

                // The injected dependencies: primary-constructor parameters and fields. These are the ambient
                // things the handler was handed and does not own — the receivers that make a Dispose a bug.
                var injected = new HashSet<string>(StringComparer.Ordinal);
                if (handler.ParameterList is not null)
                {
                    foreach (var parameter in handler.ParameterList.Parameters)
                    {
                        injected.Add(parameter.Identifier.ValueText);
                    }
                }

                foreach (var field in handler.Members.OfType<FieldDeclarationSyntax>())
                {
                    foreach (var variable in field.Declaration.Variables)
                    {
                        injected.Add(variable.Identifier.ValueText);
                    }
                }

                // Every invocation in the handler's body, INCLUDING nested lambdas and local functions —
                // wrapping a SaveChanges in a lambda does not make it someone else's unit of work.
                foreach (var invocation in handler.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (invocation.Expression is not MemberAccessExpressionSyntax member)
                    {
                        continue;
                    }

                    var name = member.Name.Identifier.ValueText;
                    var rule = ClassifyInvocation(name, member, injected);
                    if (rule is not null)
                    {
                        violations.Add(Describe(file, invocation, handlerName, rule));
                    }
                }
            }

            return violations;
        }

        /// <summary>
        /// The four banned shapes, or null when the invocation is fine. Names only: a member named
        /// <c>SaveChanges</c> is a unit-of-work commit whatever the receiver's declared type, and there is no
        /// legitimate reason for a handler to call one.
        /// </summary>
        private static string? ClassifyInvocation(
            string name, MemberAccessExpressionSyntax member, HashSet<string> injected) => name switch
            {
                "SaveChanges" or "SaveChangesAsync" =>
                    "handler saves the caller's unit of work (flushes the OUTER session's pending changes)",

                "CreateScope" or "CreateAsyncScope" =>
                    "handler creates a DI scope (leaves the aggregate's transaction and re-resolves the tenant)",

                "Commit" or "CommitAsync" =>
                    "handler commits the ambient transaction it does not own",

                "Dispose" or "DisposeAsync" when IsInjectedReceiver(member, injected) =>
                    "handler disposes an injected dependency it does not own",

                _ => null,
            };

        /// <summary>
        /// True when the receiver is an injected dependency — <c>_db.Dispose()</c> or, through the implicit
        /// this, <c>this._db.Dispose()</c>. A local the handler constructed itself does not match, which is
        /// what keeps a legitimately-owned <c>MemoryStream</c> out of the results.
        /// </summary>
        private static bool IsInjectedReceiver(MemberAccessExpressionSyntax member, HashSet<string> injected)
            => member.Expression switch
            {
                IdentifierNameSyntax identifier => injected.Contains(identifier.Identifier.ValueText),
                MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } self =>
                    injected.Contains(self.Name.Identifier.ValueText),
                _ => false,
            };

        /// <summary>
        /// The class declarations that implement <see cref="Stomme.SharedKernel.Domain.IDomainEventHandler{T}"/>.
        /// Matched on the base list: that is where the contract is accepted, and the match needs no
        /// compilation. Generic/abstract bases are not filtered out — a handler base class is still bound by
        /// the contract.
        /// </summary>
        private static IEnumerable<ClassDeclarationSyntax> HandlerDeclarations(SyntaxTree tree)
            => tree.GetRoot()
                .DescendantNodes()
                .OfType<ClassDeclarationSyntax>()
                .Where(c => c.BaseList?.Types.Any(t => IsHandlerInterface(t.Type)) == true);

        /// <summary>
        /// Both spellings of the interface in a base list: the usual <c>IDomainEventHandler&lt;T&gt;</c>
        /// (a <see cref="GenericNameSyntax"/>) and the qualified
        /// <c>Stomme.SharedKernel.Domain.IDomainEventHandler&lt;T&gt;</c> (a
        /// <see cref="QualifiedNameSyntax"/> whose right-hand side is the generic name). Missing the second
        /// would let an author opt out of the gate by typing the name in full.
        /// </summary>
        private static bool IsHandlerInterface(TypeSyntax type) => type switch
        {
            GenericNameSyntax generic => generic.Identifier.ValueText == "IDomainEventHandler",
            QualifiedNameSyntax qualified => IsHandlerInterface(qualified.Right),
            AliasQualifiedNameSyntax aliased => IsHandlerInterface(aliased.Name),
            _ => false,
        };

        private static Violation Describe(string file, SyntaxNode node, string handler, string rule)
        {
            var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            var snippet = node.ToString().Replace("\r", " ").Replace("\n", " ").Trim();
            if (snippet.Length > 100)
            {
                snippet = snippet.Substring(0, 100) + "...";
            }

            return new Violation(file, line, handler, rule, snippet);
        }

        // ------------------------------------------------------------------------------------------------
        // Scope.
        // ------------------------------------------------------------------------------------------------

        /// <summary>
        /// Framework code: the Stomme projects under <c>src/</c>. Excludes build output, and excludes
        /// <c>src/Stomme.Application</c>, a <c>dotnet new</c> TEMPLATE for downstream apps rather than
        /// framework code — it is in no solution and is not built here.
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
