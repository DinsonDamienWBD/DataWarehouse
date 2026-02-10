using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using DataWarehouse.SDK.Contracts.StorageProcessing;

namespace DataWarehouse.Tests.Infrastructure
{
    /// <summary>
    /// Unit tests for SDK storage processing strategy infrastructure types:
    /// IStorageProcessingStrategy, StorageProcessingCapabilities, ProcessingQuery,
    /// ProcessingResult, AggregationType, AggregationResult, QueryCostEstimate.
    /// </summary>
    public class SdkProcessingStrategyTests
    {
        #region IStorageProcessingStrategy Interface Tests

        [Fact]
        public void IStorageProcessingStrategy_DefinesStrategyIdProperty()
        {
            var prop = typeof(IStorageProcessingStrategy).GetProperty("StrategyId");
            Assert.NotNull(prop);
            Assert.Equal(typeof(string), prop!.PropertyType);
        }

        [Fact]
        public void IStorageProcessingStrategy_DefinesCapabilitiesProperty()
        {
            var prop = typeof(IStorageProcessingStrategy).GetProperty("Capabilities");
            Assert.NotNull(prop);
            Assert.Equal(typeof(StorageProcessingCapabilities), prop!.PropertyType);
        }

        [Fact]
        public void IStorageProcessingStrategy_DefinesProcessAsyncMethod()
        {
            var method = typeof(IStorageProcessingStrategy).GetMethod("ProcessAsync");
            Assert.NotNull(method);
            Assert.Equal(typeof(Task<ProcessingResult>), method!.ReturnType);
        }

        [Fact]
        public void IStorageProcessingStrategy_DefinesAggregateAsyncMethod()
        {
            var method = typeof(IStorageProcessingStrategy).GetMethod("AggregateAsync");
            Assert.NotNull(method);
            Assert.Equal(typeof(Task<AggregationResult>), method!.ReturnType);
        }

        [Fact]
        public void IStorageProcessingStrategy_DefinesIsQuerySupportedMethod()
        {
            var method = typeof(IStorageProcessingStrategy).GetMethod("IsQuerySupported");
            Assert.NotNull(method);
            Assert.Equal(typeof(bool), method!.ReturnType);
        }

        [Fact]
        public void IStorageProcessingStrategy_DefinesEstimateQueryCostAsyncMethod()
        {
            var method = typeof(IStorageProcessingStrategy).GetMethod("EstimateQueryCostAsync");
            Assert.NotNull(method);
            Assert.Equal(typeof(Task<QueryCostEstimate>), method!.ReturnType);
        }

        #endregion

        #region StorageProcessingCapabilities Tests

        [Fact]
        public void StorageProcessingCapabilities_Minimal_HasBasicFiltering()
        {
            var caps = StorageProcessingCapabilities.Minimal;

            Assert.True(caps.SupportsFiltering);
            Assert.True(caps.SupportsProjection);
            Assert.False(caps.SupportsAggregation);
            Assert.False(caps.SupportsJoins);
            Assert.NotEmpty(caps.SupportedOperations);
            Assert.Contains("eq", caps.SupportedOperations);
        }

        [Fact]
        public void StorageProcessingCapabilities_Full_HasAllFeatures()
        {
            var caps = StorageProcessingCapabilities.Full;

            Assert.True(caps.SupportsFiltering);
            Assert.True(caps.SupportsPredication);
            Assert.True(caps.SupportsAggregation);
            Assert.True(caps.SupportsJoins);
            Assert.True(caps.SupportsProjection);
            Assert.True(caps.SupportsSorting);
            Assert.True(caps.SupportsGrouping);
            Assert.True(caps.SupportsLimiting);
            Assert.True(caps.SupportsPatternMatching);
            Assert.True(caps.SupportsFullTextSearch);
            Assert.True(caps.SupportsTransactions);
            Assert.Equal(10, caps.MaxQueryComplexity);
        }

        [Fact]
        public void StorageProcessingCapabilities_Full_SupportsAllOperators()
        {
            var caps = StorageProcessingCapabilities.Full;

            Assert.Contains("eq", caps.SupportedOperations);
            Assert.Contains("contains", caps.SupportedOperations);
            Assert.Contains("regex", caps.SupportedOperations);
            Assert.Contains("in", caps.SupportedOperations);
        }

        [Fact]
        public void StorageProcessingCapabilities_JsonSerializationRoundTrip_PreservesValues()
        {
            var original = new StorageProcessingCapabilities
            {
                SupportsFiltering = true,
                SupportsAggregation = true,
                MaxQueryComplexity = 7,
                SupportedOperations = new[] { "eq", "gt", "lt" }
            };

            var json = JsonSerializer.Serialize(original);
            var deserialized = JsonSerializer.Deserialize<StorageProcessingCapabilities>(json);

            Assert.NotNull(deserialized);
            Assert.Equal(original.SupportsFiltering, deserialized!.SupportsFiltering);
            Assert.Equal(original.SupportsAggregation, deserialized.SupportsAggregation);
            Assert.Equal(original.MaxQueryComplexity, deserialized.MaxQueryComplexity);
        }

        #endregion

        #region ProcessingQuery Tests

        [Fact]
        public void ProcessingQuery_Construction_SetsRequiredProperties()
        {
            var query = new ProcessingQuery
            {
                Source = "warehouse/sales",
                Limit = 100,
                Offset = 0
            };

            Assert.Equal("warehouse/sales", query.Source);
            Assert.Equal(100, query.Limit);
            Assert.Equal(0, query.Offset);
            Assert.Null(query.Filters);
            Assert.Null(query.Projection);
            Assert.Null(query.Sort);
        }

        [Fact]
        public void ProcessingQuery_WithFilters_SetsCorrectly()
        {
            var filters = new List<FilterExpression>
            {
                new FilterExpression
                {
                    Field = "amount",
                    Operator = "gt",
                    Value = 100.0,
                    LogicalOperator = LogicalOperator.And
                }
            };

            var query = new ProcessingQuery
            {
                Source = "transactions",
                Filters = filters
            };

            Assert.Single(query.Filters!);
            Assert.Equal("amount", query.Filters![0].Field);
            Assert.Equal("gt", query.Filters[0].Operator);
        }

        #endregion

        #region ProcessingResult Tests

        [Fact]
        public void ProcessingResult_Construction_SetsRequiredProperties()
        {
            var data = new Dictionary<string, object?> { ["count"] = 42, ["total"] = 1500.0 };
            var result = new ProcessingResult
            {
                Data = data,
                Metadata = new ProcessingMetadata
                {
                    RowsProcessed = 1000,
                    RowsReturned = 42,
                    BytesProcessed = 1024 * 1024,
                    ProcessingTimeMs = 15.5
                }
            };

            Assert.Equal(42, result.Data["count"]);
            Assert.Equal(1000, result.Metadata!.RowsProcessed);
            Assert.Equal(42, result.Metadata.RowsReturned);
        }

        #endregion

        #region AggregationType Enum Tests

        [Fact]
        public void AggregationType_Has10Values()
        {
            var values = Enum.GetValues<AggregationType>();
            Assert.Equal(10, values.Length);
        }

        [Fact]
        public void AggregationType_ContainsStandardAggregations()
        {
            var values = Enum.GetValues<AggregationType>();
            Assert.Contains(AggregationType.Count, values);
            Assert.Contains(AggregationType.Sum, values);
            Assert.Contains(AggregationType.Average, values);
            Assert.Contains(AggregationType.Min, values);
            Assert.Contains(AggregationType.Max, values);
            Assert.Contains(AggregationType.CountDistinct, values);
            Assert.Contains(AggregationType.StdDev, values);
            Assert.Contains(AggregationType.Variance, values);
            Assert.Contains(AggregationType.Median, values);
            Assert.Contains(AggregationType.Percentile, values);
        }

        #endregion

        #region AggregationResult Tests

        [Fact]
        public void AggregationResult_Construction_SetsRequiredProperties()
        {
            var result = new AggregationResult
            {
                AggregationType = AggregationType.Sum,
                Value = 15000.0
            };

            Assert.Equal(AggregationType.Sum, result.AggregationType);
            Assert.Equal(15000.0, result.Value);
        }

        #endregion

        #region QueryCostEstimate Tests

        [Fact]
        public void QueryCostEstimate_Construction_SetsProperties()
        {
            var estimate = new QueryCostEstimate
            {
                EstimatedTimeMs = 50.0,
                EstimatedIOOperations = 500,
                EstimatedDataScanned = 10 * 1024 * 1024,
                EstimatedCPUCost = 5.0,
                EstimatedMemoryUsage = 2 * 1024 * 1024,
                CostScore = 25.0,
                Recommendations = new[] { "Add index on 'amount' column" }
            };

            Assert.Equal(50.0, estimate.EstimatedTimeMs);
            Assert.Equal(500, estimate.EstimatedIOOperations);
            Assert.Equal(25.0, estimate.CostScore);
            Assert.Single(estimate.Recommendations!);
        }

        #endregion

        #region FilterExpression and Supporting Types Tests

        [Fact]
        public void FilterExpression_DefaultLogicalOperator_IsAnd()
        {
            var filter = new FilterExpression
            {
                Field = "status",
                Operator = "eq",
                Value = "active"
            };

            Assert.Equal(LogicalOperator.And, filter.LogicalOperator);
        }

        [Fact]
        public void LogicalOperator_Has3Values()
        {
            var values = Enum.GetValues<LogicalOperator>();
            Assert.Equal(3, values.Length);
            Assert.Contains(LogicalOperator.And, values);
            Assert.Contains(LogicalOperator.Or, values);
            Assert.Contains(LogicalOperator.Not, values);
        }

        [Fact]
        public void ProjectionField_Construction_SetsProperties()
        {
            var field = new ProjectionField
            {
                SourceField = "full_name",
                OutputName = "name",
                Transformation = "UPPER"
            };

            Assert.Equal("full_name", field.SourceField);
            Assert.Equal("name", field.OutputName);
            Assert.Equal("UPPER", field.Transformation);
        }

        [Fact]
        public void SortOrder_DefaultDirection_IsAscending()
        {
            var sort = new SortOrder { Field = "created_at" };
            Assert.Equal(SortDirection.Ascending, sort.Direction);
        }

        #endregion
    }
}
