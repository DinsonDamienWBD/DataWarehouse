# Technology Stack

**Analysis Date:** 2026-02-10

## Languages

**Primary:**
- C# - Latest language version (LangVersion: latest) - Used for all core and plugin implementations
- XAML - Windows Forms/MAUI UI markup - Used in `DataWarehouse.GUI`

**Secondary:**
- JSON - Configuration and data serialization

## Runtime

**Environment:**
- .NET 10.0 (net10.0) - Target framework for all projects
- .NET 10.0 (Windows-specific) (net10.0-windows10.0.17763.0) - For GUI/MAUI application
- Windows 10.0.17763.0 minimum - Windows desktop target

**Package Manager:**
- NuGet (.NET Package Manager)
- Lockfile: Implicit (managed by .NET SDK)

## Frameworks

**Core SDK:**
- Microsoft.NET.Sdk - Base SDK for class libraries and console apps
- Microsoft.NET.Sdk.Web - ASP.NET Core for web services (`DataWarehouse.Dashboard`)
- Microsoft.NET.Sdk.Razor - For Razor/MAUI UI (`DataWarehouse.GUI`)
- Microsoft.Maui.Controls 10.0.31 - Cross-platform mobile/desktop UI framework

**Web & API:**
- ASP.NET Core - Web framework for `DataWarehouse.Dashboard`
- Microsoft.AspNetCore.SignalR.Client 10.0.2 - Real-time communication
- Microsoft.AspNetCore.Authentication.JwtBearer 10.0.2 - JWT authentication
- Microsoft.AspNetCore.Components.WebView.Maui 10.0.31 - Blazor web components in MAUI
- Swashbuckle.AspNetCore 10.1.2 - OpenAPI/Swagger documentation

**Authentication:**
- System.IdentityModel.Tokens.Jwt 8.15.0 - JWT token handling
- Azure.Identity 1.17.1 - Azure authentication provider (used in plugins)

**Testing:**
- xunit.v3 3.2.2 - Test runner
- Microsoft.NET.Test.Sdk 18.0.1 - Test framework support
- Moq 4.20.72 - Mocking framework
- FluentAssertions 8.8.0 - Assertion library
- BenchmarkDotNet 0.15.8 - Performance benchmarking (in `DataWarehouse.Benchmarks`)

**Utilities & Libraries:**
- Newtonsoft.Json 13.0.4 - JSON serialization (JSON.NET)
- YamlDotNet 16.3.0 - YAML parsing
- Microsoft.Json.Schema 2.3.0 - JSON schema validation
- System.IO.Hashing 10.0.2 - Cryptographic hashing
- Spectre.Console 0.54.0 - Rich console output for CLI
- System.CommandLine 2.0.0-beta4.22272.1 - CLI argument parsing
- System.CommandLine.NamingConventionBinder 2.0.0-beta4.22272.1 - CLI binding conventions
- Microsoft.Extensions.Logging 10.0.2 - Structured logging abstraction
- Microsoft.Extensions.Logging.Abstractions 10.0.2 - Logging interface (used across projects)
- Microsoft.Extensions.Logging.Console 10.0.2 - Console log output
- Microsoft.Extensions.ObjectPool 10.0.2 - Object pooling utilities

**Database Clients:**
- Microsoft.Data.SqlClient 6.1.4 - SQL Server database access
- Microsoft.Data.Sqlite 10.0.2 - SQLite embedded database
- Npgsql 10.0.1 - PostgreSQL database access

**Cloud & Integration:**
- AWSSDK.Core 4.0.3.12 - AWS SDK core (base for all AWS services)
- AWSSDK.BedrockAgentRuntime 4.0.8.8 - AWS Bedrock AI agent runtime

**Observability & Monitoring:**
- OpenTelemetry 1.15.0 - Distributed tracing framework
- OpenTelemetry.Exporter.OpenTelemetryProtocol 1.15.0 - OTLP exporter
- OpenTelemetry.Instrumentation.Runtime 1.15.0 - .NET runtime instrumentation
- SharpAbp.Abp.OpenTelemetry.Exporter.Prometheus.AspNetCore 4.6.6 - Prometheus metrics export (ASP.NET)
- SharpAbp.Abp.OpenTelemetry.Exporter.Prometheus.HttpListener 4.6.6 - Prometheus metrics export (HTTP)

**Security & Cryptography:**
- BouncyCastle.Cryptography 2.6.2 - Advanced cryptography algorithms
- QRCoder 1.6.0 - QR code generation (in AirGapBridge plugin)

**Data Processing & Compression:**
- K4os.Compression.LZ4 1.3.* - LZ4 compression algorithm
- K4os.Compression.LZ4.Streams 1.3.* - LZ4 streaming
- ZstdSharp.Port 0.8.* - Zstandard compression
- SharpCompress 0.44.5 - Archive/compression library
- Snappier 1.3.0 - Snappy compression algorithm
- SixLabors.ImageSharp 3.1.* - Image processing library
- System.IO.Hashing 10.0.2 - Hashing utilities

**Other Utilities:**
- MaxMind.GeoIP2 5.4.1 - GeoIP database lookups (in UltimateCompliance plugin)
- LiteDB 5.0.21 - Embedded NoSQL database (in AirGapBridge plugin)

**Testing Infrastructure:**
- xunit.runner.visualstudio 3.1.5 - Visual Studio test runner
- coverlet.collector 6.0.4 - Code coverage collection

## Configuration

**Environment:**
- Environment-based configuration via appsettings.json
- Settings files: `DataWarehouse.Launcher/appsettings.json`
- Configuration sections:
  - `Kernel` - Kernel mode, kernel ID, plugin path
  - `Logging` - Log levels, log file path
  - `Storage` - Default provider (local), base path, cache settings
  - `Security` - Encryption settings, algorithm, key storage
  - `Pipeline` - Compression settings, concurrency
  - `Health` - Health check interval, metrics port

**Build:**
- Project files: *.csproj format (MSBuild)
- Solution files: DataWarehouse.slnx (modern Visual Studio solution format)
- Build configuration: Debug/Release x86-64/x64 platforms supported

## Platform Requirements

**Development:**
- .NET 10.0 SDK or later
- Windows 10 (version 17763) or later (for GUI development)
- Visual Studio 2022 or Visual Studio Code
- NuGet package manager (integrated with .NET CLI)

**Production:**
- .NET 10.0 Runtime or later
- Windows Server 2016+ (for server deployments)
- Linux/macOS support via .NET runtime (for SDK/Kernel/CLI only; GUI is Windows-specific)
- Plugin path accessible: `./plugins` (configurable in appsettings.json)
- Storage path accessible: `./data` (configurable in appsettings.json)
- Logs directory: `./logs` (configurable in appsettings.json)
- Keys directory: `./keys` (for encryption features, configurable)

## Deployment Artifacts

**Executable:**
- CLI tool `dw` (DataWarehouse.CLI) - PackAsTool=true, installable via `dotnet tool install`
- Dashboard web service (DataWarehouse.Dashboard) - ASP.NET Core web app
- GUI application (DataWarehouse.GUI) - Windows MAUI Blazor Hybrid app
- Launcher service (DataWarehouse.Launcher) - Service wrapper

**Libraries:**
- DataWarehouse.SDK - NuGet package candidate (core abstractions)
- DataWarehouse.Kernel - NuGet package candidate (orchestration engine)
- DataWarehouse.Shared - Internal shared library
- 140+ plugins - Modular plugin architecture

---

*Stack analysis: 2026-02-10*
