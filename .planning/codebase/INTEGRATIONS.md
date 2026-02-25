# External Integrations

**Analysis Date:** 2026-02-10

## APIs & External Services

**AI/LLM Providers:**
- OpenAI - Supported (SDK-agnostic interface, configured via IAIProviderRegistry)
  - SDK/Client: Provider-specific (OpenAI, Claude, Ollama, Azure, Copilot supported)
  - Auth: Environment-based (provider-specific API keys)
  - Location: `DataWarehouse.SDK/AI/IAIProvider.cs` - AI-agnostic abstraction

- AWS Bedrock Agent Runtime - Integrated for AI agent execution
  - SDK/Client: AWSSDK.BedrockAgentRuntime 4.0.8.8
  - Auth: AWS credentials (via Azure.Identity or AWS SDK auth)
  - Location: Used in plugin system, referenced in tests

- Anthropic Claude - Supported via provider abstraction
  - SDK/Client: Provider-agnostic (no direct SDK required)
  - Auth: API key via environment configuration

- Ollama - Supported via provider abstraction
  - SDK/Client: Provider-agnostic (HTTP API compatible)
  - Auth: Local/network endpoint configuration

- Copilot/Azure OpenAI - Supported via Azure.Identity
  - SDK/Client: Provider-agnostic interface
  - Auth: Azure.Identity 1.17.1

**Observability Services:**
- Datadog - Plugin infrastructure available
  - Plugin: `DataWarehouse.Plugins.Datadog`
  - Purpose: Monitoring and metrics collection

- New Relic - Plugin infrastructure available
  - Plugin: `DataWarehouse.Plugins.NewRelic`
  - Purpose: Application performance monitoring

- Prometheus - Metrics export via OpenTelemetry
  - Exporters: SharpAbp.Abp.OpenTelemetry.Exporter.Prometheus.AspNetCore 4.6.6, SharpAbp.Abp.OpenTelemetry.Exporter.Prometheus.HttpListener 4.6.6
  - Purpose: Metrics scraping and aggregation

- Grafana/Grafana Loki - Plugin infrastructure available
  - Plugins: `DataWarehouse.Plugins.Grafana*`, `DataWarehouse.Plugins.GrafanaLoki`
  - Purpose: Visualization and log aggregation

- Jaeger - Plugin infrastructure available
  - Plugin: `DataWarehouse.Plugins.Jaeger`
  - Purpose: Distributed tracing

- Splunk - Plugin infrastructure available
  - Plugin: `DataWarehouse.Plugins.Splunk`
  - Purpose: Log analytics and searching

- SigNoz - Plugin infrastructure available
  - Plugin: `DataWarehouse.Plugins.SigNoz`
  - Purpose: Open-source observability platform

- CloudWatch - Implicit via AWS SDKs
  - SDK: AWSSDK.Core 4.0.3.12
  - Purpose: AWS resource monitoring

**Visualization & BI Tools:**
- Apache Superset - Plugin infrastructure available
  - Plugin: `DataWarehouse.Plugins.ApacheSuperset`

- Metabase - Plugin infrastructure available
  - Plugin: `DataWarehouse.Plugins.Metabase`

- Tableau - Plugin infrastructure available
  - Plugin: `DataWarehouse.Plugins.Tableau`

- PowerBI - Plugin infrastructure available
  - Plugin: `DataWarehouse.Plugins.PowerBI`

- Kibana - Plugin infrastructure available
  - Plugin: `DataWarehouse.Plugins.Kibana`

- Chronograf - Plugin infrastructure available
  - Plugin: `DataWarehouse.Plugins.Chronograf`

- Redash - Plugin infrastructure available
  - Plugin: `DataWarehouse.Plugins.Redash`

- Geckoboard - Plugin infrastructure available
  - Plugin: `DataWarehouse.Plugins.Geckoboard`

- Perses - Plugin infrastructure available
  - Plugin: `DataWarehouse.Plugins.Perses`

- Grafana - Plugin infrastructure available
  - Plugin: `DataWarehouse.Plugins.Grafana*` (multiple variants)

## Data Storage

**Relational Databases:**
- SQL Server
  - Client: Microsoft.Data.SqlClient 6.1.4
  - Connection: Environment-based configuration
  - Protocol support: TDS Protocol via `DataWarehouse.Plugins.TdsProtocol`

- PostgreSQL
  - Client: Npgsql 10.0.1
  - Connection: Environment-based configuration
  - Protocol support: PostgreSQL Wire Protocol via `DataWarehouse.Plugins.PostgresWireProtocol`

- SQLite
  - Client: Microsoft.Data.Sqlite 10.0.2
  - Connection: File-based or in-memory
  - Embedded database support

- Oracle Database
  - Protocol support: Oracle TNS Protocol via `DataWarehouse.Plugins.OracleTnsProtocol`
  - Connection: Native TNS protocol handling

- MySQL
  - Protocol support: MySQL Protocol via `DataWarehouse.Plugins.MySqlProtocol`
  - Connection: Native MySQL wire protocol

**NoSQL Databases:**
- Generic NoSQL support via `DataWarehouse.Plugins.NoSqlProtocol`
- LiteDB 5.0.21 - Embedded NoSQL (in AirGapBridge plugin)

**File Storage:**
- Local filesystem - Primary storage backend
  - Configuration: `./data` path configurable in appsettings.json
  - Drivers:
    - `DataWarehouse.Plugins.FilesystemCore` - Core filesystem operations
    - `DataWarehouse.Plugins.UltimateFilesystem` - Extended filesystem features
    - `DataWarehouse.Plugins.FuseDriver` - FUSE driver integration (Linux)
    - `DataWarehouse.Plugins.WinFspDriver` - Windows File System Proxy (Windows)

- Cloud Storage - Via connector plugins
  - Azure Storage - Via `DataWarehouse.Plugins.UltimateMultiCloud`
  - AWS S3 - Via `DataWarehouse.Plugins.UltimateMultiCloud`
  - Generic cloud via Ultimate plugins

**Caching:**
- In-process caching
  - Configuration: CacheEnabled, CacheSize (e.g., "1GB") in appsettings.json
  - Used by data access layer for performance

## Authentication & Identity

**Auth Provider:**
- JWT (JSON Web Tokens) - Native support
  - Implementation: System.IdentityModel.Tokens.Jwt 8.15.0
  - Used in: `DataWarehouse.Dashboard` (ASP.NET Core)
  - Validation: Microsoft.AspNetCore.Authentication.JwtBearer 10.0.2

- Azure Identity - Multi-cloud identity provider
  - Package: Azure.Identity 1.17.1
  - Supported: Azure AD, Managed Identity, Service Principal
  - Used in: Cloud-dependent plugins (CarbonAwareness, UltimateConnector)

- Custom Authentication - Plugin-extensible architecture
  - Interface: `DataWarehouse.SDK` base abstractions
  - Ultimate Access Control: `DataWarehouse.Plugins.UltimateAccessControl`
    - Includes biometric/visual authentication support (SixLabors.ImageSharp 3.1.*)

## Messaging & Events

**Protocol Support:**
- gRPC - Interface and binary serialization
  - Plugin: `DataWarehouse.Plugins.GrpcInterface`
  - Purpose: High-performance RPC communication

- REST/HTTP - Standard web API
  - Plugin: `DataWarehouse.Plugins.RestInterface`
  - Documentation: Swashbuckle.AspNetCore 10.1.2 (Swagger/OpenAPI)

- GraphQL - Query language API
  - Plugin: `DataWarehouse.Plugins.GraphQlApi`
  - Purpose: Flexible data querying

- SignalR - Real-time bidirectional communication
  - SDK: Microsoft.AspNetCore.SignalR.Client 10.0.2
  - Purpose: WebSocket/persistent connection support

- JDBC Bridge - Java database connectivity
  - Plugin: `DataWarehouse.Plugins.JdbcBridge`
  - Purpose: Java interoperability

- ODBC Driver - Database connectivity standard
  - Plugin: `DataWarehouse.Plugins.OdbcDriver`
  - Purpose: Legacy application support

## Monitoring & Observability

**Distributed Tracing:**
- OpenTelemetry
  - Framework: OpenTelemetry 1.15.0
  - Exporters: OpenTelemetry.Exporter.OpenTelemetryProtocol 1.15.0
  - Instrumentation: OpenTelemetry.Instrumentation.Runtime 1.15.0
  - Purpose: Vendor-agnostic distributed tracing

- Jaeger - Distributed tracing backend
  - Plugin: `DataWarehouse.Plugins.Jaeger`
  - Protocol: OTLP (OpenTelemetry Protocol)

**Metrics:**
- Prometheus - Metrics scraping
  - Exporters: SharpAbp.Abp.OpenTelemetry.Exporter.Prometheus.*
  - Port: Configurable via appsettings.json (default 9090)
  - Metrics interval: Configurable

- Victoria Metrics - Prometheus-compatible metrics
  - Plugin: `DataWarehouse.Plugins.VictoriaMetrics`

**Logs:**
- File-based logging
  - Path: Configurable in appsettings.json (default `./logs`)
  - Abstractions: Microsoft.Extensions.Logging.Abstractions 10.0.2
  - Implementation: Microsoft.Extensions.Logging.Console 10.0.2

- Structured logging via ILogger
  - Format: Configurable log levels and routing
  - Plugins: Grafana Loki, Splunk, Logz.io for shipping

## CI/CD & Deployment

**Build System:**
- MSBuild (.NET Native build system)
- Solution file: `DataWarehouse.slnx` (Modern solution format)
- Target frameworks: net10.0, net10.0-windows10.0.17763.0

**Deployment Approaches:**
- CLI Tool Distribution - `dotnet tool install` via NuGet
  - Package: DataWarehouse.CLI with PackAsTool=true
  - Binary: `dw` command

- Container Deployment - Docker support
  - Plugin: `DataWarehouse.Plugins.Docker`
  - Purpose: Containerization and orchestration

- Kubernetes - Container orchestration
  - Plugin: `DataWarehouse.Plugins.K8sOperator`
  - Purpose: Enterprise-scale deployment

- Blue-Green Deployment - Strategy support
  - Plugin: `DataWarehouse.Plugins.BlueGreenDeployment`
  - Purpose: Zero-downtime updates

- Canary Deployment - Progressive rollout
  - Plugin: `DataWarehouse.Plugins.CanaryDeployment`
  - Purpose: Risk mitigation

- Zero-Downtime Upgrade - Advanced deployment
  - Plugin: `DataWarehouse.Plugins.ZeroDowntimeUpgrade`
  - Purpose: In-place service updates

**Cloud Platforms:**
- Azure - Multi-cloud support
  - SDKs: Azure.Identity 1.17.1
  - Plugin: Multiple Azure-specific plugins

- AWS - Multi-cloud support
  - SDKs: AWSSDK.Core, AWSSDK.BedrockAgentRuntime
  - Plugin: Multiple AWS-specific plugins

- Multi-cloud abstraction
  - Plugin: `DataWarehouse.Plugins.UltimateMultiCloud`
  - Purpose: Cloud-agnostic deployment

## Environment Configuration

**Required env vars:**
- Kernel mode and ID configuration (via appsettings.json or environment)
- AI provider credentials (OPENAI_API_KEY, ANTHROPIC_API_KEY, AZURE_KEY_VAULT_* etc.)
- Database connection strings (SQL Server, PostgreSQL, SQLite)
- AWS credentials (if using AWS plugins)
- Azure credentials (if using Azure plugins)
- Observability endpoints (Prometheus, Jaeger, etc.)

**Secrets location:**
- Local development: appsettings.json, environment variables, .env files
- Azure: Azure Key Vault (via Azure.Identity)
- AWS: AWS Secrets Manager/SSM Parameter Store (via AWSSDK)
- Encrypted storage: `./keys` directory (configurable)

## Data Formats & Serialization

**JSON Processing:**
- Newtonsoft.Json 13.0.4 (JSON.NET)
  - Used across SDK, Shared, and plugins for serialization

- Microsoft.Json.Schema 2.3.0
  - Purpose: Schema validation for JSON payloads

**YAML Support:**
- YamlDotNet 16.3.0
  - Used in DataWarehouse.Shared for configuration parsing
  - Purpose: Human-readable config files

**Binary Serialization:**
- Protocol Buffers support (via gRPC plugin)
- System.IO.Hashing 10.0.2 - Cryptographic hashing for data integrity

## Security & Compliance

**Encryption:**
- Algorithm: AES-256-GCM (configurable in appsettings.json)
- Cryptography: BouncyCastle.Cryptography 2.6.2
  - Used in TamperProof and AirGapBridge plugins
  - Advanced cryptographic algorithms beyond .NET standard library

**Tamper Detection:**
- Plugin: `DataWarehouse.Plugins.TamperProof`
- Purpose: Data integrity verification and attribution

**Compliance Features:**
- GDPR support - Via UltimateDataPrivacy plugin
- Data retention policies - Via data governance plugins
- Audit logging - Via `DataWarehouse.Plugins.AuditLogging`
- GeoIP-based access control - Via MaxMind.GeoIP2 5.4.1 (UltimateCompliance)

**Air-Gap/Offline:**
- Plugin: `DataWarehouse.Plugins.AirGapBridge`
- Features: QR code generation (QRCoder 1.6.0), LiteDB offline storage
- Purpose: Secure offline data transfer capability

---

*Integration audit: 2026-02-10*
