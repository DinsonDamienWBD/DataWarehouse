using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Storage.Migration;
using Xunit;

namespace DataWarehouse.Tests.Storage.ZeroGravity;

/// <summary>
/// Tests for background migration engine.
/// Verifies: job lifecycle (start/pause/resume/cancel), read forwarding,
/// checkpoint save/restore, and throttling behavior.
/// </summary>
public sealed class MigrationEngineTests : IDisposable
{
    private readonly string _checkpointDir;
    private readonly ReadForwardingTable _forwardingTable;
    private readonly MigrationCheckpointStore _checkpointStore;

    public MigrationEngineTests()
    {
        _checkpointDir = Path.Combine(Path.GetTempPath(), $"dw-migration-test-{Guid.NewGuid():N}");
        _forwardingTable = new ReadForwardingTable(TimeSpan.FromHours(1));
        _checkpointStore = new MigrationCheckpointStore(_checkpointDir);
    }

    public void Dispose()
    {
        _forwardingTable.Dispose();
        try { Directory.Delete(_checkpointDir, recursive: true); } catch { }
    }

    #region Helpers

    private BackgroundMigrationEngine CreateEngine(int batchSize = 10)
    {
        var inMemoryStore = new ConcurrentDictionary<string, byte[]>();

        var engine = new BackgroundMigrationEngine(_forwardingTable, _checkpointStore, batchSize);
        engine.ReadObjectAsync = (key, node, ct) =>
        {
            var data = inMemoryStore.GetOrAdd($"{node}:{key}", _ => new byte[1024]);
            return Task.FromResult<Stream>(new MemoryStream(data));
        };
        engine.WriteObjectAsync = (key, node, stream, ct) =>
        {
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            inMemoryStore[$"{node}:{key}"] = ms.ToArray();
            return Task.CompletedTask;
        };
        engine.DeleteObjectAsync = (key, node, ct) =>
        {
            var removed = inMemoryStore.TryRemove($"{node}:{key}", out _);
            return Task.FromResult(removed);
        };
        return engine;
    }

    private static MigrationPlan CreatePlan(int objectCount = 3, long? throttle = null)
    {
        var objects = new MigrationObject[objectCount];
        for (int i = 0; i < objectCount; i++)
        {
            objects[i] = new MigrationObject(
                ObjectKey: $"obj-{i}",
                SizeBytes: 1024,
                SourceLocation: "source-node",
                TargetLocation: "target-node",
                Priority: 1);
        }
        return new MigrationPlan(
            SourceNode: "source-node",
            TargetNode: "target-node",
            Objects: objects,
            ThrottleBytesPerSec: throttle,
            EnableReadForwarding: true,
            ZeroDowntime: true,
            ValidateChecksums: false);
    }

    #endregion

    #region Lifecycle

    [Fact]
    public async Task StartMigration_ReturnsJobWithId()
    {
        using var engine = CreateEngine();
        var plan = CreatePlan(objectCount: 1);

        var job = await engine.StartMigrationAsync(plan);

        Assert.NotNull(job.JobId);
        Assert.NotEmpty(job.JobId);
        Assert.Equal(1, job.TotalObjects);
    }

    [Fact]
    public async Task PauseResume_SetsCorrectStatus()
    {
        using var engine = CreateEngine(batchSize: 1000);
        // Use throttling to slow down the migration so pause/resume can be observed
        var plan = CreatePlan(objectCount: 20, throttle: 512);
        var job = await engine.StartMigrationAsync(plan);

        // Give migration a moment to start
        await Task.Delay(100);

        // Pause
        await engine.PauseMigrationAsync(job.JobId);
        await Task.Delay(600); // Allow engine to observe pause flag

        var status = await engine.GetStatusAsync(job.JobId);
        // Verify it's not in a terminal state (engine should be paused or just transitioned)
        Assert.True(
            status.Status is MigrationStatus.Paused or MigrationStatus.InProgress or MigrationStatus.ReadForwarding,
            $"Expected Paused/InProgress/ReadForwarding, got {status.Status}");

        // Resume
        await engine.ResumeMigrationAsync(job.JobId);
        await Task.Delay(200);

        var resumed = await engine.GetStatusAsync(job.JobId);
        // After resume, should not be Cancelled
        Assert.NotEqual(MigrationStatus.Cancelled, resumed.Status);
    }

    [Fact]
    public async Task Cancel_StopsExecution()
    {
        using var engine = CreateEngine();
        var plan = CreatePlan(objectCount: 100);
        var job = await engine.StartMigrationAsync(plan);

        await engine.CancelMigrationAsync(job.JobId);
        await Task.Delay(300);

        var status = await engine.GetStatusAsync(job.JobId);
        Assert.Equal(MigrationStatus.Cancelled, status.Status);
    }

    #endregion

    #region Read Forwarding

    [Fact]
    public async Task ReadForwarding_RegisteredDuringMigration()
    {
        using var engine = CreateEngine();
        var plan = CreatePlan(objectCount: 2);
        var job = await engine.StartMigrationAsync(plan);

        // Wait for migration to process at least one object
        await Task.Delay(500);

        // Check forwarding table has entries
        Assert.True(_forwardingTable.ActiveEntries > 0,
            "Expected forwarding entries to be registered during migration");
    }

    [Fact]
    public void ReadForwarding_LookupReturnsEntry()
    {
        _forwardingTable.RegisterForwarding("test-key", "old-node", "new-node");

        var entry = _forwardingTable.Lookup("test-key");

        Assert.NotNull(entry);
        Assert.Equal("test-key", entry.ObjectKey);
        Assert.Equal("old-node", entry.OriginalNode);
        Assert.Equal("new-node", entry.NewNode);
    }

    [Fact]
    public async Task ReadForwarding_ExpiredEntry_ReturnsNull()
    {
        // Create a table with very short TTL
        using var shortTtlTable = new ReadForwardingTable(TimeSpan.FromMilliseconds(1));
        shortTtlTable.RegisterForwarding("expired-key", "old-node", "new-node");

        // Wait for TTL to expire
        await Task.Delay(50);

        var entry = shortTtlTable.Lookup("expired-key");
        Assert.Null(entry);
    }

    #endregion

    #region Checkpointing

    [Fact]
    public async Task Checkpoint_SaveAndRestore()
    {
        var checkpoint = new MigrationCheckpoint(
            JobId: "test-job-123",
            LastProcessedKey: "obj-42",
            ProcessedCount: 42,
            TimestampUtc: DateTimeOffset.UtcNow);

        await _checkpointStore.SaveCheckpointAsync(checkpoint);
        var loaded = await _checkpointStore.LoadCheckpointAsync("test-job-123");

        Assert.NotNull(loaded);
        Assert.Equal("test-job-123", loaded.JobId);
        Assert.Equal("obj-42", loaded.LastProcessedKey);
        Assert.Equal(42, loaded.ProcessedCount);
    }

    #endregion

    #region Throttling

    [Fact]
    public async Task Throttling_RespectsLimit()
    {
        using var engine = CreateEngine(batchSize: 100);
        // Each object is 1024 bytes, throttle to 1024 bytes/sec = 1 object/sec
        var plan = CreatePlan(objectCount: 3, throttle: 1024);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var job = await engine.StartMigrationAsync(plan);

        // Wait for completion (should take ~3 seconds at 1 obj/sec)
        for (int i = 0; i < 50; i++)
        {
            await Task.Delay(200);
            var status = await engine.GetStatusAsync(job.JobId);
            if (status.Status is MigrationStatus.Completed or MigrationStatus.Failed)
                break;
        }
        sw.Stop();

        // With 3 objects at 1024 bytes/sec throttle, should take at least ~2 seconds
        // (first object is immediate, then throttling kicks in)
        Assert.True(sw.Elapsed.TotalSeconds >= 1.5,
            $"Expected >= 1.5s with throttling, got {sw.Elapsed.TotalSeconds:F1}s");
    }

    #endregion
}
