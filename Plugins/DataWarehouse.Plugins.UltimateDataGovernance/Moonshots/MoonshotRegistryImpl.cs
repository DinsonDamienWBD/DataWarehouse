using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using DataWarehouse.SDK.Moonshots;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataGovernance.Moonshots;

/// <summary>
/// Thread-safe in-memory implementation of <see cref="IMoonshotRegistry"/>.
/// Stores moonshot registrations in a <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// and fires <see cref="StatusChanged"/> events when moonshot status transitions occur.
/// </summary>
public sealed class MoonshotRegistryImpl : IMoonshotRegistry
{
    private readonly BoundedDictionary<MoonshotId, MoonshotRegistration> _registrations = new BoundedDictionary<MoonshotId, MoonshotRegistration>(1000);

    /// <inheritdoc />
    public event EventHandler<MoonshotStatusChangedEventArgs>? StatusChanged;

    /// <inheritdoc />
    public void Register(MoonshotRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        _registrations.AddOrUpdate(
            registration.Id,
            registration,
            (_, _) => registration);
    }

    /// <inheritdoc />
    public MoonshotRegistration? Get(MoonshotId id)
    {
        return _registrations.TryGetValue(id, out var reg) ? reg : null;
    }

    /// <inheritdoc />
    public IReadOnlyList<MoonshotRegistration> GetAll()
    {
        return _registrations.Values.OrderBy(r => (int)r.Id).ToList().AsReadOnly();
    }

    /// <inheritdoc />
    public MoonshotStatus GetStatus(MoonshotId id)
    {
        return _registrations.TryGetValue(id, out var reg)
            ? reg.Status
            : MoonshotStatus.NotInstalled;
    }

    /// <inheritdoc />
    public void UpdateStatus(MoonshotId id, MoonshotStatus status)
    {
        if (!_registrations.TryGetValue(id, out var existing))
            return;

        var oldStatus = existing.Status;
        if (oldStatus == status)
            return;

        var updated = existing with { Status = status };
        _registrations.TryUpdate(id, updated, existing);

        StatusChanged?.Invoke(this, new MoonshotStatusChangedEventArgs
        {
            Id = id,
            OldStatus = oldStatus,
            NewStatus = status,
            Timestamp = DateTimeOffset.UtcNow
        });
    }

    /// <inheritdoc />
    public void UpdateHealthReport(MoonshotId id, MoonshotHealthReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        if (!_registrations.TryGetValue(id, out var existing))
            return;

        var updated = existing with
        {
            LastHealthCheck = DateTimeOffset.UtcNow,
            LastHealthReport = report
        };
        _registrations.TryUpdate(id, updated, existing);
    }

    /// <inheritdoc />
    public IReadOnlyList<MoonshotRegistration> GetByStatus(MoonshotStatus status)
    {
        return _registrations.Values
            .Where(r => r.Status == status)
            .OrderBy(r => (int)r.Id)
            .ToList()
            .AsReadOnly();
    }
}
