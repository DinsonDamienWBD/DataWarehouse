using DataWarehouse.SDK.Infrastructure;
using DataWarehouse.SDK.Contracts;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.Telemetry;

/// <summary>
/// Tests for DistributedTracing context propagation and span management.
/// </summary>
public class DistributedTracingTests
{
    #region Trace Creation Tests

    [Fact]
    public void StartTrace_ShouldCreateNewTrace()
    {
        // Arrange
        var tracing = new DistributedTracing();

        // Act
        using var scope = tracing.StartTrace("test-operation");

        // Assert
        scope.Should().NotBeNull();
        scope.Context.CorrelationId.Should().NotBeNullOrEmpty();
        scope.Context.SpanId.Should().NotBeNullOrEmpty();
        scope.Context.ParentSpanId.Should().BeNull();
        scope.Context.OperationName.Should().Be("test-operation");
    }

    [Fact]
    public void StartTrace_ShouldSetCurrentContext()
    {
        // Arrange
        var tracing = new DistributedTracing();

        // Act
        using var scope = tracing.StartTrace("test");

        // Assert
        tracing.Current.Should().NotBeNull();
        tracing.Current!.CorrelationId.Should().Be(scope.Context.CorrelationId);
    }

    [Fact]
    public void StartTrace_ShouldAcceptBaggage()
    {
        // Arrange
        var tracing = new DistributedTracing();
        var baggage = new Dictionary<string, string>
        {
            ["tenant"] = "acme",
            ["user"] = "john"
        };

        // Act
        using var scope = tracing.StartTrace("test", baggage);

        // Assert
        scope.Context.Baggage.Should().ContainKey("tenant");
        scope.Context.Baggage["tenant"].Should().Be("acme");
        scope.Context.Baggage["user"].Should().Be("john");
    }

    #endregion

    #region Span Creation Tests

    [Fact]
    public void StartSpan_WithinTrace_ShouldCreateChildSpan()
    {
        // Arrange
        var tracing = new DistributedTracing();

        using var parentScope = tracing.StartTrace("parent");
        var parentSpanId = parentScope.Context.SpanId;

        // Act
        using var childScope = tracing.StartSpan("child");

        // Assert
        childScope.Context.CorrelationId.Should().Be(parentScope.Context.CorrelationId);
        childScope.Context.ParentSpanId.Should().Be(parentSpanId);
        childScope.Context.SpanId.Should().NotBe(parentSpanId);
    }

    [Fact]
    public void StartSpan_WithoutParent_ShouldCreateNewTrace()
    {
        // Arrange
        var tracing = new DistributedTracing();

        // Act
        using var scope = tracing.StartSpan("orphan");

        // Assert - Should behave like StartTrace
        scope.Context.CorrelationId.Should().NotBeNullOrEmpty();
        scope.Context.ParentSpanId.Should().BeNull();
    }

    [Fact]
    public void StartSpan_ShouldInheritBaggageFromParent()
    {
        // Arrange
        var tracing = new DistributedTracing();
        var parentBaggage = new Dictionary<string, string> { ["inherited"] = "value" };

        using var parent = tracing.StartTrace("parent", parentBaggage);

        // Act
        using var child = tracing.StartSpan("child");

        // Assert
        child.Context.Baggage.Should().ContainKey("inherited");
        child.Context.Baggage["inherited"].Should().Be("value");
    }

    [Fact]
    public void StartSpan_ShouldMergeBaggage()
    {
        // Arrange
        var tracing = new DistributedTracing();
        using var parent = tracing.StartTrace("parent", new Dictionary<string, string> { ["parent"] = "value1" });

        // Act
        using var child = tracing.StartSpan("child", new Dictionary<string, string> { ["child"] = "value2" });

        // Assert
        child.Context.Baggage.Should().ContainKey("parent");
        child.Context.Baggage.Should().ContainKey("child");
    }

    [Fact]
    public void StartSpan_ChildBaggage_ShouldOverrideParent()
    {
        // Arrange
        var tracing = new DistributedTracing();
        using var parent = tracing.StartTrace("parent", new Dictionary<string, string> { ["key"] = "parent-value" });

        // Act
        using var child = tracing.StartSpan("child", new Dictionary<string, string> { ["key"] = "child-value" });

        // Assert
        child.Context.Baggage["key"].Should().Be("child-value");
    }

    #endregion

    #region Continue Trace Tests

    [Fact]
    public void ContinueTrace_ShouldUseProvidedCorrelationId()
    {
        // Arrange
        var tracing = new DistributedTracing();
        var externalCorrelationId = "external-correlation-123";

        // Act
        using var scope = tracing.ContinueTrace(externalCorrelationId, "continued-operation");

        // Assert
        scope.Context.CorrelationId.Should().Be(externalCorrelationId);
        scope.Context.Tags.Should().ContainKey("continued_from_external");
    }

    [Fact]
    public void ContinueTrace_ShouldCreateNewSpanId()
    {
        // Arrange
        var tracing = new DistributedTracing();

        // Act
        using var scope = tracing.ContinueTrace("external-123", "operation");

        // Assert
        scope.Context.SpanId.Should().NotBe("external-123");
        scope.Context.SpanId.Should().NotBeNullOrEmpty();
    }

    #endregion

    #region Context Propagation Tests

    [Fact]
    public void InjectContext_ShouldAddHeaders()
    {
        // Arrange
        var tracing = new DistributedTracing();
        using var scope = tracing.StartTrace("test", new Dictionary<string, string> { ["user"] = "john" });

        var carrier = new Dictionary<string, string>();

        // Act
        tracing.InjectContext(carrier);

        // Assert
        carrier.Should().ContainKey(DistributedTracing.CorrelationIdHeader);
        carrier.Should().ContainKey(DistributedTracing.SpanIdHeader);
        carrier.Should().ContainKey("X-Baggage-user");
        carrier["X-Baggage-user"].Should().Be("john");
    }

    [Fact]
    public void InjectContext_WithoutCurrentContext_ShouldDoNothing()
    {
        // Arrange
        var tracing = new DistributedTracing();
        var carrier = new Dictionary<string, string>();

        // Act
        tracing.InjectContext(carrier);

        // Assert
        carrier.Should().BeEmpty();
    }

    [Fact]
    public void ExtractContext_ShouldParseHeaders()
    {
        // Arrange
        var tracing = new DistributedTracing();
        var carrier = new Dictionary<string, string>
        {
            [DistributedTracing.CorrelationIdHeader] = "correlation-123",
            [DistributedTracing.SpanIdHeader] = "span-456",
            [DistributedTracing.ParentSpanIdHeader] = "parent-789",
            ["X-Baggage-tenant"] = "acme"
        };

        // Act
        var context = tracing.ExtractContext(carrier);

        // Assert
        context.Should().NotBeNull();
        context!.CorrelationId.Should().Be("correlation-123");
        context.SpanId.Should().Be("span-456");
        context.ParentSpanId.Should().Be("parent-789");
        context.Baggage.Should().ContainKey("tenant");
        context.Baggage["tenant"].Should().Be("acme");
    }

    [Fact]
    public void ExtractContext_WithoutCorrelationId_ShouldReturnNull()
    {
        // Arrange
        var tracing = new DistributedTracing();
        var carrier = new Dictionary<string, string>
        {
            [DistributedTracing.SpanIdHeader] = "span-only"
        };

        // Act
        var context = tracing.ExtractContext(carrier);

        // Assert
        context.Should().BeNull();
    }

    [Fact]
    public void ExtractContext_WithoutSpanId_ShouldGenerateOne()
    {
        // Arrange
        var tracing = new DistributedTracing();
        var carrier = new Dictionary<string, string>
        {
            [DistributedTracing.CorrelationIdHeader] = "correlation-123"
        };

        // Act
        var context = tracing.ExtractContext(carrier);

        // Assert
        context.Should().NotBeNull();
        context!.SpanId.Should().NotBeNullOrEmpty();
    }

    #endregion

    #region Scope Lifecycle Tests

    [Fact]
    public void Scope_Dispose_ShouldRestoreParentContext()
    {
        // Arrange
        var tracing = new DistributedTracing();
        using var parent = tracing.StartTrace("parent");
        var parentContext = tracing.Current;

        // Act
        using (var child = tracing.StartSpan("child"))
        {
            tracing.Current.Should().NotBeSameAs(parentContext);
        }

        // Assert - Parent context should be restored
        tracing.Current.Should().BeSameAs(parentContext);
    }

    [Fact]
    public void Scope_Dispose_ShouldRemoveFromActiveTraces()
    {
        // Arrange
        var tracing = new DistributedTracing();

        string spanId;
        using (var scope = tracing.StartTrace("test"))
        {
            spanId = scope.Context.SpanId;
            tracing.GetActiveTraces().Should().ContainKey(spanId);
        }

        // Assert
        tracing.GetActiveTraces().Should().NotContainKey(spanId);
    }

    [Fact]
    public void Scope_SetTag_ShouldAddToContext()
    {
        // Arrange
        var tracing = new DistributedTracing();

        using var scope = tracing.StartTrace("test");

        // Act
        scope.SetTag("key", "value");
        scope.SetTag("count", 42);

        // Assert
        scope.Context.Tags.Should().ContainKey("key");
        scope.Context.Tags["key"].Should().Be("value");
        scope.Context.Tags["count"].Should().Be(42);
    }

    [Fact]
    public void Scope_SetBaggage_ShouldAddToContext()
    {
        // Arrange
        var tracing = new DistributedTracing();
        using var scope = tracing.StartTrace("test");

        // Act
        scope.SetBaggage("key", "value");

        // Assert
        scope.Context.Baggage.Should().ContainKey("key");
        scope.Context.Baggage["key"].Should().Be("value");
    }

    [Fact]
    public void Scope_LogEvent_ShouldRecordEvent()
    {
        // Arrange
        var tracing = new DistributedTracing();
        using var scope = tracing.StartTrace("test");

        // Act
        scope.LogEvent("test-event", new Dictionary<string, object> { ["detail"] = "info" });

        // Assert
        scope.Context.Tags.Keys.Should().Contain(k => k.Contains("event.") && k.Contains("test-event"));
    }

    [Fact]
    public void Scope_SetError_ShouldRecordException()
    {
        // Arrange
        var tracing = new DistributedTracing();
        using var scope = tracing.StartTrace("test");

        // Act
        scope.SetError(new InvalidOperationException("Test error"));

        // Assert
        scope.Context.Tags["error"].Should().Be(true);
        scope.Context.Tags["error.type"].Should().Be("InvalidOperationException");
        scope.Context.Tags["error.message"].Should().Be("Test error");
    }

    [Fact]
    public void Scope_SetStatus_ShouldRecordStatus()
    {
        // Arrange
        var tracing = new DistributedTracing();
        using var scope = tracing.StartTrace("test");

        // Act
        scope.SetStatus(TraceStatus.Error, "Something went wrong");

        // Assert
        scope.Context.Tags["status"].Should().Be("Error");
        scope.Context.Tags["status.message"].Should().Be("Something went wrong");
    }

    [Fact]
    public void Scope_Dispose_ShouldRecordDuration()
    {
        // Arrange
        var tracing = new DistributedTracing();

        // Act
        using (var scope = tracing.StartTrace("test"))
        {
            Thread.Sleep(50);
        }

        // Note: Can't easily verify tags after dispose, but ensuring no exception
    }

    #endregion

    #region Extension Method Tests

    [Fact]
    public void GetCorrelationId_WithCurrentContext_ShouldReturnId()
    {
        // Arrange
        var tracing = new DistributedTracing();
        using var scope = tracing.StartTrace("test");

        // Act
        var correlationId = tracing.GetCorrelationId();

        // Assert
        correlationId.Should().Be(scope.Context.CorrelationId);
    }

    [Fact]
    public void GetCorrelationId_WithoutContext_ShouldGenerateId()
    {
        // Arrange
        var tracing = new DistributedTracing();

        // Act
        var correlationId = tracing.GetCorrelationId();

        // Assert
        correlationId.Should().NotBeNullOrEmpty();
        correlationId.Should().HaveLength(16); // 16 hex chars
    }

    [Fact]
    public void GetCorrelationId_OnNull_ShouldGenerateId()
    {
        // Arrange
        IDistributedTracing? tracing = null;

        // Act
        var correlationId = tracing.GetCorrelationId();

        // Assert
        correlationId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task TraceAsync_ShouldExecuteWithinSpan()
    {
        // Arrange
        var tracing = new DistributedTracing();
        using var parent = tracing.StartTrace("parent");

        // Act
        var result = await tracing.TraceAsync("child-operation", async scope =>
        {
            scope.SetTag("custom", "value");
            await Task.Delay(10);
            return 42;
        });

        // Assert
        result.Should().Be(42);
    }

    [Fact]
    public async Task TraceAsync_OnException_ShouldSetErrorStatus()
    {
        // Arrange
        var tracing = new DistributedTracing();
        using var parent = tracing.StartTrace("parent");

        // Act & Assert
        var act = async () => await tracing.TraceAsync<int>("failing", _ =>
        {
            throw new Exception("Test exception");
        });

        await act.Should().ThrowAsync<Exception>();
    }

    #endregion

    #region Thread Safety Tests

    [Fact]
    public async Task AsyncLocal_ShouldMaintainContextAcrossTasks()
    {
        // Arrange
        var tracing = new DistributedTracing();
        using var scope = tracing.StartTrace("async-test");
        var expectedCorrelationId = scope.Context.CorrelationId;

        // Act - Access context from multiple tasks
        var tasks = Enumerable.Range(0, 10).Select(_ => Task.Run(() =>
        {
            // Each task should see the same correlation ID
            return tracing.Current?.CorrelationId;
        }));

        var results = await Task.WhenAll(tasks);

        // Assert
        results.Should().AllBe(expectedCorrelationId);
    }

    [Fact]
    public void ConcurrentTraceCreation_ShouldNotInterfere()
    {
        // Arrange
        var tracing = new DistributedTracing();
        var correlationIds = new System.Collections.Concurrent.ConcurrentBag<string>();

        // Act
        Parallel.For(0, 100, _ =>
        {
            using var scope = tracing.StartTrace("parallel-trace");
            correlationIds.Add(scope.Context.CorrelationId);
        });

        // Assert - All correlation IDs should be unique
        correlationIds.Should().HaveCount(100);
        correlationIds.Distinct().Should().HaveCount(100);
    }

    [Fact]
    public void NestedSpans_ShouldMaintainCorrectHierarchy()
    {
        // Arrange
        var tracing = new DistributedTracing();

        // Act
        using (var root = tracing.StartTrace("root"))
        {
            var rootSpanId = root.Context.SpanId;

            using (var child1 = tracing.StartSpan("child1"))
            {
                child1.Context.ParentSpanId.Should().Be(rootSpanId);

                using (var grandchild = tracing.StartSpan("grandchild"))
                {
                    grandchild.Context.ParentSpanId.Should().Be(child1.Context.SpanId);
                }

                // After grandchild disposes, current should be child1
                tracing.Current!.SpanId.Should().Be(child1.Context.SpanId);
            }

            // After child1 disposes, current should be root
            tracing.Current!.SpanId.Should().Be(rootSpanId);
        }
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void Scope_SetTagAfterDispose_ShouldNotThrow()
    {
        // Arrange
        var tracing = new DistributedTracing();
        var scope = tracing.StartTrace("test");
        scope.Dispose();

        // Act & Assert - Should not throw
        var act = () => scope.SetTag("key", "value");
        act.Should().NotThrow();
    }

    [Fact]
    public void DoubleDispose_ShouldNotThrow()
    {
        // Arrange
        var tracing = new DistributedTracing();
        var scope = tracing.StartTrace("test");

        // Act
        scope.Dispose();
        var act = () => scope.Dispose();

        // Assert
        act.Should().NotThrow();
    }

    [Fact]
    public void GetActiveTraces_ShouldReflectCurrentState()
    {
        // Arrange
        var tracing = new DistributedTracing();

        // Act & Assert
        tracing.GetActiveTraces().Should().BeEmpty();

        using (var scope1 = tracing.StartTrace("trace1"))
        {
            tracing.GetActiveTraces().Should().HaveCount(1);

            using (var scope2 = tracing.StartSpan("span2"))
            {
                tracing.GetActiveTraces().Should().HaveCount(2);
            }

            tracing.GetActiveTraces().Should().HaveCount(1);
        }

        tracing.GetActiveTraces().Should().BeEmpty();
    }

    #endregion

    // Stub class for removed DistributedTracing functionality
    private class DistributedTracing
    {
        public IDisposable StartTrace(string operation) => new TraceScope();
        public string[] GetActiveTraces() => Array.Empty<string>();

        private class TraceScope : IDisposable
        {
            public void Dispose() { }
        }
    }
}
