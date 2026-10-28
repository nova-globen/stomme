#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Stomme.Collaboration.Persistence;
using Stomme.Modules.Collaboration.Application;
using Stomme.Modules.Collaboration.Domain;
using Stomme.SharedKernel.Domain;
using Stomme.Tenancy.Domain;

namespace Stomme.Tests.Collaboration
{
    /// <summary>An in-memory aggregate store: the module repositories run over it unchanged (their site filter is a query).</summary>
    internal sealed class InMemoryAggregates<T> : IRepository<T, Guid>
        where T : AggregateRoot<Guid>
    {
        private readonly ConcurrentDictionary<Guid, T> _rows = new();

        public int Writes;

        public IReadOnlyList<T> Rows => _rows.Values.ToList();

        public Task<T?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult(_rows.TryGetValue(id, out var row) ? row : null);

        public Task<IReadOnlyList<T>> GetAllAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<T>>(_rows.Values.ToList());

        public Task<IReadOnlyList<T>> FindAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<T>>(_rows.Values.Where(predicate.Compile()).ToList());

        public Task AddAsync(T aggregate, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Writes);
            _rows[aggregate.Id] = aggregate;
            return Task.CompletedTask;
        }

        public Task UpdateAsync(T aggregate, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Writes);
            _rows[aggregate.Id] = aggregate;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(T aggregate, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Writes);
            _rows.TryRemove(aggregate.Id, out _);
            return Task.CompletedTask;
        }
    }

    /// <summary>The request's principal, switchable between users - never a value a request carries.</summary>
    internal sealed class SwitchableCaller : ICollaborationCaller
    {
        public CollaborationCallerIdentity? Current { get; set; }

        public Task<CollaborationCallerIdentity> RequireAsync(CancellationToken cancellationToken = default) =>
            Current is { } me ? Task.FromResult(me) : throw new CollaborationRefusedException(CollaborationRefusal.NoCaller, "no caller");

        public static CollaborationCallerIdentity As(int userId, string name, int siteId = CollaborationRig.Site) => new(TenantId.Default, siteId, userId, name);
    }

    /// <summary>The owning modules' answer, scripted: every resource editable unless denied.</summary>
    internal sealed class ScriptedResourceAccess : ICollaborativeResourceAccess
    {
        public HashSet<string> Denied { get; } = new(StringComparer.Ordinal);

        public List<string> Asked { get; } = new();

        public Task<bool> CanEditAsync(ResourceRef resource, CancellationToken cancellationToken = default)
        {
            Asked.Add(resource.Value);
            return Task.FromResult(!Denied.Contains(resource.Value));
        }
    }

    /// <summary>Counts the saves - where a service would dispatch the domain events of what it wrote.</summary>
    internal sealed class CountingUnitOfWork : IUnitOfWork
    {
        public int Saves;

        public Task<int> SaveChangesAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref Saves);
            return Task.FromResult(0);
        }

        public void Dispose()
        {
        }
    }

    internal sealed class ManualClock(DateTime now) : TimeProvider
    {
        public DateTime Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => new(Now, TimeSpan.Zero);
    }

    /// <summary>The two services over one in-memory store, a scripted access, a switchable principal and a manual clock.</summary>
    internal sealed class CollaborationRig
    {
        public const int Site = 3;
        public const int Ada = 11;
        public const int Bo = 12;
        public const int Cy = 13;

        public static readonly DateTime Start = new(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc);

        public CollaborationRig()
        {
            LockRows = new InMemoryAggregates<ResourceLockAggregate>();
            SessionRows = new InMemoryAggregates<EditSessionAggregate>();
            Locks = new ResourceLockRepository(LockRows);
            Sessions = new EditSessionRepository(SessionRows);
            Caller.Current = SwitchableCaller.As(Ada, "Ada");
            LockService = new ResourceLockService(Locks, Caller, Access, Options, Gate, Clock, UnitOfWork);
            SessionService = new EditSessionService(Sessions, Locks, Caller, Access, Gate, Clock, UnitOfWork);
        }

        public InMemoryAggregates<ResourceLockAggregate> LockRows { get; }

        public InMemoryAggregates<EditSessionAggregate> SessionRows { get; }

        public IResourceLockRepository Locks { get; }

        public IEditSessionRepository Sessions { get; }

        public SwitchableCaller Caller { get; } = new();

        public ScriptedResourceAccess Access { get; } = new();

        public CollaborationOptions Options { get; } = new() { Enabled = true, LeaseSeconds = 120 };

        public CollaborationGate Gate { get; } = new();

        public ManualClock Clock { get; } = new(Start);

        public CountingUnitOfWork UnitOfWork { get; } = new();

        public ResourceLockService LockService { get; }

        public EditSessionService SessionService { get; }

        public CollaborationRig As(int userId, string name)
        {
            Caller.Current = SwitchableCaller.As(userId, name);
            return this;
        }
    }
}
