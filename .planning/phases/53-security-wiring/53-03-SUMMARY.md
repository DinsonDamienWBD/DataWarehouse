---
phase: 53-security-wiring
plan: 03
subsystem: security
tags: [tls, authentication, jwt, grpc, ftp, hsm, dashboard, cors]
dependency_graph:
  requires: []
  provides:
    - "TLS certificate validation on all gRPC and FTP strategies"
    - "Authenticated Dashboard SignalR hub"
    - "Ephemeral JWT dev secret"
    - "Launcher API security headers and CORS"
  affects:
    - "GrpcStorageStrategy"
    - "GrpcControlPlanePlugin"
    - "FtpTransitStrategy"
    - "AwsCloudHsmStrategy"
    - "DashboardHub"
    - "Dashboard Program.cs"
    - "LauncherHttpServer"
tech_stack:
  added: []
  patterns:
    - "Configurable TLS validation with secure default (validate=true)"
    - "Ephemeral JWT signing key generation via RandomNumberGenerator"
    - "X-Forwarded-Proto HTTPS enforcement for reverse proxy scenarios"
key_files:
  created: []
  modified:
    - Plugins/DataWarehouse.Plugins.UltimateStorage/Strategies/Specialized/GrpcStorageStrategy.cs
    - Plugins/DataWarehouse.Plugins.AedsCore/ControlPlane/GrpcControlPlanePlugin.cs
    - Plugins/DataWarehouse.Plugins.UltimateDataTransit/Strategies/Direct/FtpTransitStrategy.cs
    - Plugins/DataWarehouse.Plugins.UltimateKeyManagement/Strategies/Hsm/AwsCloudHsmStrategy.cs
    - DataWarehouse.Dashboard/Hubs/DashboardHub.cs
    - DataWarehouse.Dashboard/Program.cs
    - DataWarehouse.Launcher/Integration/LauncherHttpServer.cs
decisions:
  - "Used configurable _validateCertificate field pattern (consistent with already-fixed strategies)"
  - "GrpcControlPlanePlugin uses URL scheme detection for TLS vs insecure credential selection"
  - "JWT dev secret uses RandomNumberGenerator.Fill(64 bytes) converted to base64"
  - "Launcher HTTPS enforcement uses X-Forwarded-Proto header check (reverse proxy pattern)"
metrics:
  duration: "9m 2s"
  completed: "2026-02-19T07:55:31Z"
  tasks: 2
  files_modified: 7
---

# Phase 53 Plan 03: Fix TLS Bypasses and Dashboard Authentication Summary

Resolved 7 pentest findings (NET-01/02/03/06/09, AUTH-02/04/05) by fixing unconditional TLS certificate bypasses across 4 strategies and adding authentication/security to Dashboard and Launcher.

## Task 1: Fix TLS Certificate Validation Bypasses

**Commit:** `6a33cd93`

### NET-01 (CVSS 9.1): GrpcStorageStrategy
- Replaced unconditional `return true` in `RemoteCertificateValidationCallback` with configurable `_validateCertificate` field (default: `true`)
- When true: no callback set, .NET uses default secure validation
- When false (dev opt-in): explicit bypass with callback
- Custom CA cert support preserved with proper chain validation

### NET-02 (CVSS 8.7): GrpcControlPlanePlugin
- Replaced unconditional `ChannelCredentials.Insecure` with URL scheme detection
- HTTPS endpoints now use `ChannelCredentials.SecureSsl`
- HTTP endpoints log a warning about insecure connection

### NET-03 (CVSS 7.4): FtpTransitStrategy
- Changed `ValidateAnyCertificate = true` to `ValidateAnyCertificate = false` (secure default)
- FluentFTP will now validate certificates using system trust store

### NET-06 (CVSS 6.5): AwsCloudHsmStrategy
- Removed fallback that accepted `RemoteCertificateChainErrors` when no custom CA is configured
- Chain errors are now only accepted when a customer CA cert is explicitly provided AND validates
- Without custom CA, only `SslPolicyErrors.None` passes

### NET-09 (CVSS 3.1): gRPC Message Size
- Reduced default `MaxReceiveMessageSize` and `MaxSendMessageSize` from 100MB to 10MB
- Still configurable via `GetConfiguration` for legitimate large transfer use cases

## Task 2: Fix Dashboard Auth Gaps

**Commit:** `1ada1488`

### AUTH-04 (CVSS 7.5): DashboardHub Missing Authentication
- Added `[Authorize]` attribute to `DashboardHub` class
- All SignalR connections now require valid JWT token
- Token-in-query-string support for SignalR was already configured in Program.cs

### AUTH-02 (CVSS 8.1): Hardcoded JWT Development Secret
- Replaced hardcoded `"DataWarehouse_Development_Secret_Key_At_Least_32_Chars!"` with ephemeral random key
- Uses `RandomNumberGenerator.Fill(64 bytes)` converted to base64 on each startup
- Logs warning: "Using ephemeral JWT signing key -- tokens will not survive restart"
- Production path unchanged (throws if no key configured)

### AUTH-05 (CVSS 5.3): Launcher API Security
- Added security headers: `X-Content-Type-Options`, `X-Frame-Options`, `Referrer-Policy`, `Cache-Control`
- Added CORS middleware restricting to localhost origins only
- Added HTTPS enforcement via `X-Forwarded-Proto` check for non-local requests
- Returns 403 with guidance message when non-HTTPS detected behind reverse proxy

## Deviations from Plan

None - plan executed exactly as written.

## Verification

- All 4 plugin projects build with 0 errors, 0 warnings
- Dashboard project builds with 0 errors, 0 warnings
- Launcher project builds with 0 errors, 0 warnings
- Full solution has 5 pre-existing errors in DataWarehouse.Kernel (unrelated interface implementation issues)
- `grep "ValidateAnyCertificate = true"` returns 0 matches in UltimateDataTransit
- `grep "[Authorize]"` confirms attribute on DashboardHub
- `grep "Development_Secret"` returns 0 matches in Dashboard/Program.cs
