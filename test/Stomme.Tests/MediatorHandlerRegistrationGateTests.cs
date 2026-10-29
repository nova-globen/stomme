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
    /// Every mediator handler the server declares is REGISTERED by a startup.
    /// </summary>
    /// <remarks>
    /// The modules register their handlers one by one (<c>services.AddScoped&lt;IRequestHandler&lt;TRequest, TResponse&gt;, THandler&gt;()</c>);
    /// nothing scans for them. A handler written and not registered compiles, every unit test that news it up is green, and
    /// the request answers 500 with no line in the log: the run-steps query did exactly that, and the editor showed a run as
    /// "starting…" for ever. This gate reads the server's source with Roslyn: a non-abstract class implementing
    /// <c>IRequestHandler&lt;,&gt;</c>, <c>ICommandHandler&lt;,&gt;</c> or <c>IQueryHandler&lt;,&gt;</c> must be named as a type argument of, or in a
    /// <c>typeof</c> passed to, an <c>Add…</c>/<c>TryAdd…</c> call somewhere in the server.
    /// </remarks>
    public sealed class MediatorHandlerRegistrationGateTests
    {
        private static readonly HashSet<string> HandlerInterfaces = new(StringComparer.Ordinal) { "IRequestHandler", "ICommandHandler", "IQueryHandler" };

        [Fact]
        public void Every_handler_the_server_declares_is_registered_by_a_startup()
        {
            var server = Path.Combine(RepoRoot(), "src", "Stomme.Server");
            var trees = Directory.EnumerateFiles(server, "*.cs", SearchOption.AllDirectories)
                .Where(file => !file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    && !file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                .Select(file => CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file))
                .ToList();

            var handlers = Handlers(trees);
            // Not vacuous: the server declares dozens of handlers (64 when this was written).
            Assert.True(handlers.Count >= 60, $"The scan found only {handlers.Count} handlers - is it still reading the server's source?");

            var registered = Registered(trees);
            var missing = handlers.Where(handler => !registered.Contains(handler.Key)).Select(handler => $"{handler.Key} ({Path.GetRelativePath(server, handler.Value)})").ToList();
            Assert.True(missing.Count == 0, "Handlers no startup registers - the request would answer 500 with nothing in the log:\n" + string.Join("\n", missing));
        }

        [Fact]
        public void The_gate_finds_an_unregistered_handler_and_passes_a_registered_one()
        {
            const string Source = """
                public sealed record Ping : IQuery<string>;
                public sealed class PingHandler : IQueryHandler<Ping, string> { }
                public sealed class PongHandler(Dependency dependency)
                    : ICommandHandler<Pong, bool> { }
                public abstract class BaseHandler : IRequestHandler<Ping, string> { }
                public sealed class NotAHandler : IDisposable { }
                public static class Startup
                {
                    public static void Configure(IServiceCollection services)
                    {
                        services.AddScoped<IRequestHandler<Ping, string>, PingHandler>();
                    }
                }
                """;
            var tree = CSharpSyntaxTree.ParseText(Source, path: "probe.cs");

            var handlers = Handlers(new[] { tree });
            Assert.Equal(new[] { "PingHandler", "PongHandler" }, handlers.Keys.OrderBy(name => name, StringComparer.Ordinal));
            var registered = Registered(new[] { tree });
            Assert.Contains("PingHandler", registered);
            Assert.DoesNotContain("PongHandler", registered);

            // A typeof registration counts too.
            var byType = CSharpSyntaxTree.ParseText("class S { void C(IServiceCollection s) { s.TryAddScoped(typeof(IRequestHandler<Pong, bool>), typeof(PongHandler)); } }");
            Assert.Contains("PongHandler", Registered(new[] { byType }));
        }

        /// <summary>Handler class name to the file that declares it.</summary>
        internal static Dictionary<string, string> Handlers(IEnumerable<SyntaxTree> trees)
        {
            var handlers = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var tree in trees)
            {
                foreach (var type in tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
                {
                    if (type.Modifiers.Any(SyntaxKind.AbstractKeyword) || type.BaseList is null)
                    {
                        continue;
                    }

                    var implementsHandler = type.BaseList.Types
                        .Select(baseType => baseType.Type is QualifiedNameSyntax qualified ? qualified.Right : baseType.Type)
                        .OfType<GenericNameSyntax>()
                        .Any(generic => HandlerInterfaces.Contains(generic.Identifier.Text));
                    if (implementsHandler)
                    {
                        handlers[type.Identifier.Text] = tree.FilePath;
                    }
                }
            }

            return handlers;
        }

        /// <summary>Every type named in a registration call: a type argument of, or a <c>typeof</c> passed to, an <c>Add…</c>/<c>TryAdd…</c> call.</summary>
        internal static HashSet<string> Registered(IEnumerable<SyntaxTree> trees)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var invocation in trees.SelectMany(tree => tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>()))
            {
                var method = invocation.Expression switch
                {
                    MemberAccessExpressionSyntax member => member.Name,
                    SimpleNameSyntax simple => simple,
                    _ => null,
                };
                var methodName = method?.Identifier.Text ?? "";
                if (!methodName.StartsWith("Add", StringComparison.Ordinal) && !methodName.StartsWith("TryAdd", StringComparison.Ordinal))
                {
                    continue;
                }

                if (method is GenericNameSyntax generic)
                {
                    foreach (var name in generic.TypeArgumentList.DescendantNodesAndSelf().OfType<SimpleNameSyntax>())
                    {
                        names.Add(name.Identifier.Text);
                    }
                }

                foreach (var typeOf in invocation.ArgumentList.DescendantNodes().OfType<TypeOfExpressionSyntax>())
                {
                    foreach (var name in typeOf.Type.DescendantNodesAndSelf().OfType<SimpleNameSyntax>())
                    {
                        names.Add(name.Identifier.Text);
                    }
                }
            }

            return names;
        }

        private static string RepoRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Stomme.slnx")))
            {
                directory = directory.Parent;
            }

            Assert.True(directory is not null, "Could not locate the repository root (Stomme.slnx) from " + AppContext.BaseDirectory);
            return directory!.FullName;
        }
    }
}
