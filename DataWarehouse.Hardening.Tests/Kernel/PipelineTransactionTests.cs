using DataWarehouse.Kernel.Pipeline;
using PipelineContracts = DataWarehouse.SDK.Contracts.Pipeline;

namespace DataWarehouse.Hardening.Tests.Kernel;

/// <summary>
/// Hardening tests for PipelineTransaction — finding 133.
/// </summary>
public class PipelineTransactionTests
{
    // Finding 133: _failureException field is assigned but its value is never used
    // The field captures the exception on MarkFailed but is never exposed or logged.
    // FIX: The exception is already passed to _logger in MarkFailed. The field stores
    // it for potential future use in RollbackAsync diagnostics.
    [Fact]
    public async Task Finding133_FailureException_StoredButNotUsed()
    {
        var tx = new PipelineTransaction(transactionId: "test-tx-1", blobId: "blob-1");
        Assert.Equal(PipelineContracts.PipelineTransactionState.Active, tx.State);

        // Mark as failed with an exception
        tx.MarkFailed(new InvalidOperationException("test failure"));
        Assert.Equal(PipelineContracts.PipelineTransactionState.Failed, tx.State);

        // Should be able to rollback after failure
        var result = await tx.RollbackAsync();
        Assert.True(result.Success);
    }
}
