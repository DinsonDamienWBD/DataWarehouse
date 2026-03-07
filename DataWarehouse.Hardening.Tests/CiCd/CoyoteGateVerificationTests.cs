using Microsoft.Coyote;
using Microsoft.Coyote.SystematicTesting;

namespace DataWarehouse.Hardening.Tests.CiCd;

/// <summary>
/// INTENTIONAL BUG -- this test verifies the Coyote gate catches data races.
/// If Coyote does NOT find this bug, the gate is misconfigured.
///
/// The test creates a classic read-modify-write race condition on a shared counter.
/// Two concurrent tasks both read the counter, increment it, and write it back
/// without any synchronization. Under normal (sequential) execution the counter
/// ends up at 2, but under Coyote's systematic scheduler the interleaving that
/// causes the lost-update anomaly is reliably discovered.
///
/// Report: "Stage 4 - Gate Verification"
/// </summary>
public class CoyoteGateVerificationTests
{
    /// <summary>
    /// Shared mutable state with NO synchronization -- intentional race condition.
    /// Two tasks read-modify-write this counter concurrently. Without a lock or
    /// Interlocked, one increment can be lost (both read 0, both write 1).
    /// </summary>
    [Fact]
    public void Coyote_Finds_Intentional_DataRace_On_Shared_Counter()
    {
        // Coyote systematic testing configuration: enough iterations to find the race.
        var config = Configuration.Create()
            .WithTestingIterations(1000)
            .WithMaxSchedulingSteps(200);

        var engine = TestingEngine.Create(config, ConcurrentCounterWithRace);

        engine.Run();

        // The engine SHOULD find a bug (the data race). If it doesn't, the gate
        // is misconfigured or Coyote is not exploring interleavings properly.
        // We assert the bug WAS found -- proving the gate works.
        var report = engine.TestReport;
        Assert.True(
            report.NumOfFoundBugs > 0,
            $"Coyote should have found the intentional data race but reported 0 bugs. " +
            $"Iterations: {config.TestingIterations}. " +
            "The Coyote gate may be misconfigured.");
    }

    /// <summary>
    /// The test entry point for Coyote's systematic engine.
    /// Creates a classic lost-update race: two tasks concurrently read-modify-write
    /// a shared integer without synchronization.
    /// </summary>
    private static async Task ConcurrentCounterWithRace()
    {
        int counter = 0;

        var task1 = Task.Run(() =>
        {
            // Read-modify-write without lock (intentional race)
            int local = counter;
            local++;
            counter = local;
        });

        var task2 = Task.Run(() =>
        {
            // Read-modify-write without lock (intentional race)
            int local = counter;
            local++;
            counter = local;
        });

        await Task.WhenAll(task1, task2);

        // Under sequential execution, counter == 2. Under concurrent scheduling,
        // both tasks may read 0 and write 1, leaving counter == 1.
        // Coyote's assertion failure triggers its bug-finding report.
        Microsoft.Coyote.Specifications.Specification.Assert(
            counter == 2,
            $"Data race detected: counter is {counter} instead of expected 2. " +
            "This confirms the Coyote gate catches concurrency bugs.");
    }
}
