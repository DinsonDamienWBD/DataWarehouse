# DataWarehouse SDK Tasks

## Current Sprint - SDK Foundation Fixes

### Build Error Fixes
- [x] Fix CS0738: IPlugin.Id type mismatch (string vs Guid) - PluginBase.cs:12,97,144,204,269
- [x] Fix CS0246: PluginMessage missing using directive - IPlugin.cs:34
- [x] Fix CS0246: IExecutionContext missing using directive - IPluginCapability.cs:55
- [x] Fix CS0535: PluginBase missing OnMessageAsync implementation

### Architecture Improvements
- [x] Review and document all abstract base classes
- [x] Review and optimize interfaces for minimal boilerplate
- [x] Add AI-native infrastructure (IAIProvider abstraction)
- [x] Add runtime pipeline ordering support (IPipelineOrchestrator)
- [x] Add message bus infrastructure for kernel communication

## Abstract Base Classes (for plugin developers)

| Class | Purpose | Status |
|-------|---------|--------|
| PluginBase | Core plugin functionality | COMPLETED |
| DataTransformationPluginBase | Compression, Encryption | COMPLETED |
| StorageProviderPluginBase | S3, Local, IPFS | COMPLETED |
| MetadataIndexPluginBase | SQLite, Postgres | COMPLETED |
| FeaturePluginBase | SQL Listener, etc. | COMPLETED |
| SecurityProviderPluginBase | ACL, Auth | COMPLETED |
| OrchestrationProviderPluginBase | Workflow | COMPLETED |
| IntelligencePluginBase | AI/ML plugins | COMPLETED |
| InterfacePluginBase | REST, gRPC | COMPLETED |
| PipelinePluginBase | Pipeline stages | COMPLETED |

## Interfaces (minimal, generic, future-proof)

| Interface | Purpose | Status |
|-----------|---------|--------|
| IPlugin | Core plugin contract | COMPLETED |
| IKernelContext | Kernel services access | COMPLETED |
| IPluginCapability | Capability exposure | COMPLETED |
| IExecutionContext | Execution context | COMPLETED |
| IAIProvider | AI-agnostic provider | COMPLETED |
| IPipelineOrchestrator | Runtime ordering | COMPLETED |
| IMessageBus | Plugin communication | COMPLETED |

## Completed Tasks
- [x] Created CLAUDE.md documentation
- [x] Fixed all build errors
- [x] Added AI-native infrastructure
- [x] Added pipeline orchestration support
- [x] Added message bus for inter-plugin communication
