* Core Philosophy
Quality Level: God-Tier (production-ready for Google/Microsoft/Amazon)
No Compromises: Zero placeholders, simulations, or simplifications
AI-Native: Every component designed for AI integration from the ground up

* The 12 Absolute Rules
** Rule 1: Production-Ready Implementation
- Full error handling with try-catch blocks
- Input validation for all public methods
- Null checks for all nullable parameters
- Thread-safe operations where concurrency is possible
- Resource disposal (IDisposable pattern)
- Logging of errors and warnings
- Retry logic for transient failures
- Timeout handling for async operations
- Graceful degradation when dependencies unavailable
- Forbidden: TODO comments, placeholders, simulated responses, hardcoded test data, console logging, empty catch blocks, magic numbers

** Rule 2: Comprehensive Documentation
- Class-level documentation (purpose, usage, thread safety, performance)
- Method-level documentation (summary, all parameters, return value, exceptions, examples)
- Property-level documentation (purpose, valid ranges, defaults, thread safety)
- XML documentation on ALL public APIs
- Rule 3: Maximum Code Reuse
- Never duplicate code
- Base classes for shared functionality
- Utility classes for common operations
- Extension methods for repeated patterns
- Generic methods/classes for type-agnostic operations
- Composition over inheritance where appropriate
- Dependency injection for pluggable components

** Rule 4: Message-Based Architecture
- No direct function calls between components
- All communication via messages (PluginMessage class)
- Async message handling (OnMessageAsync)
- Message types as strings
- Payload as dictionary for flexibility
- Standardized response format (MessageResponse)

** Rule 5: Standardized Plugin Architecture
Directory Structure:

Plugins/DataWarehouse.Plugins.{Category}.{Name}/
  Bootstrapper/Init.cs      ← Plugin entry point
  Engine/{Name}Engine.cs    ← Core logic (stateless)
  Service/{Name}Service.cs  ← Optional: stateful services
  Models/{Name}Models.cs    ← Optional: data models

** Rule 6: CategoryBase Abstract Classes for Plugins ⭐ MOST IMPORTANT
- All plugins MUST extend category-specific abstract base classes, NOT implement interfaces directly.
- Available CategoryBase Classes:
StorageProviderBase - Storage plugins (S3, Local, IPFS)
FeaturePluginBase - Feature plugins (Tiering, Caching)
InterfacePluginBase - Interface plugins (REST, SQL, gRPC)
MetadataProviderBase - Metadata plugins (SQLite, Postgres)
IntelligencePluginBase - AI/Governance plugins
OrchestrationPluginBase - Orchestration plugins (Raft)
SecurityProviderBase - Security/ACL plugins
PipelinePluginBase - Pipeline plugins (GZip, AES)
Property Override Pattern (Critical):

// ❌ WRONG: Assignment (causes CS0200 error)
public MyPlugin() : base(...) {
    SemanticDescription = "..."; // ERROR!
}

// ✅ CORRECT: Property override
protected override string SemanticDescription => "...";
protected override string[] SemanticTags => new[] { "tag1", "tag2" };

Benefits:
80% code reduction - plugins only implement backend-specific logic
Maximum code reuse - common operations implemented once
Consistency - all plugins in same category behave identically
AI-Native support - base classes handle metadata patterns

** Rule 7: AI-Native Integration
- Every component must include:
Semantic descriptions in natural language
Semantic tags for categorization
Performance profiles for optimization
Capability relationships for planning
Usage examples for learning
Event emission for observability
Standardized parameter schemas (JSON Schema)

** Rule 8: Error Handling & Resilience
- Validate all inputs
- Try-catch blocks for all external operations
- Specific exception types (not generic Exception)
- Meaningful error messages with context
- Logging of all errors with stack traces
- Retry logic for transient failures (3 attempts, exponential backoff)
- Circuit breaker pattern for repeated failures
- Fallback mechanisms where possible
- Resource cleanup in finally blocks or using statements

** Rule 9: Performance & Scalability
- Async/await for all I/O operations
- Proper disposal of resources
- Connection pooling for databases and HTTP clients
- Caching where appropriate (with expiration)
- Batch operations over individual operations
- Streaming for large data (don't load everything in memory)
- Pagination for large result sets
- Indexed database queries (no full table scans)
- Thread-safe concurrent operations

** Rule 10: Testing & Validation
- Public APIs must be unit testable
- Dependencies injected (not newed up internally)
- Interfaces for external dependencies
- Validation of all inputs at public boundaries
- Testable error conditions
- No static dependencies that can't be mocked

** Rule 11: Security & Safety
- Input validation (prevent SQL injection, XSS, path traversal)
- Authentication/authorization checks
- Secure credential storage (no hardcoded secrets)
- Encryption for sensitive data
- HTTPS for all network communication
- Rate limiting to prevent abuse
- Audit logging of security events
- Principle of least privilege

** Rule 12: Task Tracking & Documentation
- Add tasks to TODO.md BEFORE starting work
- Break large features into atomic, trackable tasks
- Include estimated lines of code for each task
- Mark tasks with status: NOT STARTED, IN PROGRESS, COMPLETED
- Update TODO.md immediately when task status changes
- Include file paths and commit references when completed