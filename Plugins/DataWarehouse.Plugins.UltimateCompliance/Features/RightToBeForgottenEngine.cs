using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateCompliance.Features
{
    /// <summary>
    /// Automates GDPR Right to Be Forgotten (RTBF) requests with data discovery,
    /// deletion verification, and confirmation.
    /// </summary>
    public sealed class RightToBeForgottenEngine
    {
        private readonly BoundedDictionary<string, ErasureRequest> _requests = new BoundedDictionary<string, ErasureRequest>(1000);
        private readonly List<IDataStore> _registeredDataStores = new();

        /// <summary>
        /// Registers a data store for erasure operations.
        /// </summary>
        public void RegisterDataStore(IDataStore dataStore)
        {
            _registeredDataStores.Add(dataStore);
        }

        /// <summary>
        /// Initiates a right to be forgotten request for a data subject.
        /// </summary>
        public async Task<string> InitiateErasureRequestAsync(string dataSubjectId, string requestReason, CancellationToken cancellationToken = default)
        {
            var requestId = Guid.NewGuid().ToString();
            var request = new ErasureRequest
            {
                RequestId = requestId,
                DataSubjectId = dataSubjectId,
                RequestReason = requestReason,
                RequestTime = DateTime.UtcNow,
                Status = ErasureStatus.Pending,
                DataStoreResults = new ConcurrentBag<DataStoreErasureResult>()
            };

            _requests[requestId] = request;

            // Start discovery phase
            await DiscoverDataAsync(request, cancellationToken);

            return requestId;
        }

        /// <summary>
        /// Executes the erasure across all registered data stores.
        /// </summary>
        public async Task<ErasureExecutionResult> ExecuteErasureAsync(string requestId, CancellationToken cancellationToken = default)
        {
            if (!_requests.TryGetValue(requestId, out var request))
            {
                return new ErasureExecutionResult
                {
                    Success = false,
                    Message = $"Erasure request not found: {requestId}"
                };
            }

            if (request.Status != ErasureStatus.DiscoveryComplete)
            {
                return new ErasureExecutionResult
                {
                    Success = false,
                    Message = $"Cannot execute erasure. Current status: {request.Status}"
                };
            }

            request.Status = ErasureStatus.InProgress;
            var executionStartTime = DateTime.UtcNow;

            var tasks = _registeredDataStores.Select(async store =>
            {
                try
                {
                    var result = await store.EraseDataAsync(request.DataSubjectId, cancellationToken);
                    request.DataStoreResults.Add(result);
                    return result;
                }
                catch (Exception ex)
                {
                    var errorResult = new DataStoreErasureResult
                    {
                        DataStoreName = store.Name,
                        Success = false,
                        RecordsErased = 0,
                        Message = $"Error: {ex.Message}"
                    };
                    request.DataStoreResults.Add(errorResult);
                    return errorResult;
                }
            });

            var results = await Task.WhenAll(tasks);

            var allSuccessful = results.All(r => r.Success);
            var totalRecordsErased = results.Sum(r => r.RecordsErased);

            request.Status = allSuccessful ? ErasureStatus.Completed : ErasureStatus.Failed;
            request.CompletionTime = DateTime.UtcNow;

            return new ErasureExecutionResult
            {
                Success = allSuccessful,
                Message = allSuccessful
                    ? $"Successfully erased {totalRecordsErased} records across {results.Length} data stores"
                    : $"Erasure completed with errors. {results.Count(r => r.Success)} of {results.Length} data stores succeeded",
                TotalRecordsErased = totalRecordsErased,
                DataStoreResults = results.ToList()
            };
        }

        /// <summary>
        /// Verifies that data has been completely erased.
        /// </summary>
        public async Task<ErasureVerificationResult> VerifyErasureAsync(string requestId, CancellationToken cancellationToken = default)
        {
            if (!_requests.TryGetValue(requestId, out var request))
            {
                return new ErasureVerificationResult
                {
                    IsVerified = false,
                    Message = $"Erasure request not found: {requestId}"
                };
            }

            if (request.Status != ErasureStatus.Completed)
            {
                return new ErasureVerificationResult
                {
                    IsVerified = false,
                    Message = $"Cannot verify. Erasure status: {request.Status}"
                };
            }

            var verificationTasks = _registeredDataStores.Select(async store =>
            {
                var remainingRecords = await store.FindDataAsync(request.DataSubjectId, cancellationToken);
                return new { Store = store.Name, RemainingRecords = remainingRecords };
            });

            var verificationResults = await Task.WhenAll(verificationTasks);
            var totalRemaining = verificationResults.Sum(v => v.RemainingRecords);

            var isVerified = totalRemaining == 0;

            request.Status = isVerified ? ErasureStatus.Verified : ErasureStatus.VerificationFailed;

            return new ErasureVerificationResult
            {
                IsVerified = isVerified,
                Message = isVerified
                    ? "All data successfully erased and verified"
                    : $"Verification failed. {totalRemaining} records still found across data stores",
                RemainingRecords = totalRemaining
            };
        }

        /// <summary>
        /// Gets the status of an erasure request.
        /// </summary>
        public ErasureRequest? GetRequestStatus(string requestId)
        {
            _requests.TryGetValue(requestId, out var request);
            return request;
        }

        private async Task DiscoverDataAsync(ErasureRequest request, CancellationToken cancellationToken)
        {
            request.Status = ErasureStatus.Discovering;

            var discoveryTasks = _registeredDataStores.Select(async store =>
            {
                return await store.FindDataAsync(request.DataSubjectId, cancellationToken);
            });

            var discoveries = await Task.WhenAll(discoveryTasks);
            request.TotalRecordsFound = discoveries.Sum();

            request.Status = ErasureStatus.DiscoveryComplete;
        }
    }

    /// <summary>
    /// Interface for data stores that support erasure operations.
    /// </summary>
    public interface IDataStore
    {
        string Name { get; }
        Task<int> FindDataAsync(string dataSubjectId, CancellationToken cancellationToken);
        Task<DataStoreErasureResult> EraseDataAsync(string dataSubjectId, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Represents an erasure request.
    /// </summary>
    public sealed class ErasureRequest
    {
        public required string RequestId { get; init; }
        public required string DataSubjectId { get; init; }
        public required string RequestReason { get; init; }
        public required DateTime RequestTime { get; init; }
        public ErasureStatus Status { get; set; }
        public int TotalRecordsFound { get; set; }
        public DateTime? CompletionTime { get; set; }
        public required ConcurrentBag<DataStoreErasureResult> DataStoreResults { get; init; }
    }

    /// <summary>
    /// Status of an erasure request.
    /// </summary>
    public enum ErasureStatus
    {
        Pending,
        Discovering,
        DiscoveryComplete,
        InProgress,
        Completed,
        Failed,
        Verified,
        VerificationFailed
    }

    /// <summary>
    /// Result of erasure operation on a single data store.
    /// </summary>
    public sealed class DataStoreErasureResult
    {
        public required string DataStoreName { get; init; }
        public required bool Success { get; init; }
        public required int RecordsErased { get; init; }
        public required string Message { get; init; }
    }

    /// <summary>
    /// Result of an erasure execution.
    /// </summary>
    public sealed class ErasureExecutionResult
    {
        public required bool Success { get; init; }
        public required string Message { get; init; }
        public int TotalRecordsErased { get; init; }
        public IReadOnlyList<DataStoreErasureResult> DataStoreResults { get; init; } = Array.Empty<DataStoreErasureResult>();
    }

    /// <summary>
    /// Result of erasure verification.
    /// </summary>
    public sealed class ErasureVerificationResult
    {
        public required bool IsVerified { get; init; }
        public required string Message { get; init; }
        public int RemainingRecords { get; init; }
    }
}
