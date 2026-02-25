---
phase: 65-infrastructure
plan: 11
subsystem: Security/KeyManagement, Security/SupplyChain
tags: [cloud-kms, gcp-kms, aws-kms, secrets-manager, slsa, provenance, supply-chain, dsse, in-toto]
dependency_graph:
  requires: [IKeyStore, IKeyStoreStrategy, KeyStoreStrategyBase, IEnvelopeKeyStore, IMessageBus, PluginMessage, ISecurityContext]
  provides: [GcpKmsProvider, AwsKmsProvider, AwsSecretsManagerKeyStore, GcpSecretManagerKeyStore, SlsaProvenanceGenerator, SlsaVerifier, IProvenanceGenerator, IProvenanceVerifier]
  affects: [security-posture-100, tier5-7-compliance, supply-chain-verification, cloud-key-management]
tech_stack:
  added: [SigV4 signing, GCP ADC, DSSE envelope, in-toto attestation]
  patterns: [KeyStoreStrategyBase, REST-only cloud APIs, secure memory pinning, CryptographicOperations.ZeroMemory]
key_files:
  created:
    - DataWarehouse.SDK/Security/KeyManagement/CloudKmsProvider.cs
    - DataWarehouse.SDK/Security/KeyManagement/SecretsManagerKeyStore.cs
    - DataWarehouse.SDK/Security/SupplyChain/SlsaProvenanceGenerator.cs
    - DataWarehouse.SDK/Security/SupplyChain/SlsaVerifier.cs
  modified: []
decisions:
  - "Used pure REST APIs for both GCP and AWS (no SDK dependencies) to maintain zero-dependency security layer"
  - "Implemented both KMS providers as IEnvelopeKeyStore for wrap/unwrap operations via cloud encrypt/decrypt"
  - "Used GCHandle.Alloc with GCHandleType.Pinned for secure cache entries to prevent GC relocation of key material"
  - "SLSA signature uses RSA-PSS SHA-256 primary with HMAC-SHA256 fallback for non-RSA keys"
metrics:
  duration: "8m 29s"
  completed: "2026-02-20"
  tasks_completed: 2
  tasks_total: 2
  files_created: 4
  files_modified: 0
---

# Phase 65 Plan 11: Cloud KMS + SLSA Provenance Summary

GCP/AWS KMS via REST with ADC/SigV4, cloud secrets managers as IKeyStore backends, and SLSA Level 3 provenance with DSSE-signed in-toto attestations.

## Task 1: Cloud KMS Providers (GCP ADC + AWS)

**Commit:** `112be57a`

### GcpKmsProvider
- Extends `KeyStoreStrategyBase`, implements `IEnvelopeKeyStore`
- GCP KMS REST API at `cloudkms.googleapis.com`
- ADC credential chain: service account JSON, gcloud CLI defaults, GCE metadata server
- OAuth2 JWT token exchange with automatic refresh (5 min buffer before expiry)
- Encrypt/Decrypt via POST `/v1/{keyName}:encrypt` and `:decrypt`
- Key URL: `projects/{project}/locations/{location}/keyRings/{ring}/cryptoKeys/{key}`
- Exponential backoff retry on 429/503, fail-fast on 403 (permission denied)

### AwsKmsProvider
- Extends `KeyStoreStrategyBase`, implements `IEnvelopeKeyStore`
- AWS KMS REST API at `kms.{region}.amazonaws.com`
- Full SigV4 request signing (canonical request, string-to-sign, HMAC signing chain)
- Credential chain: explicit config, env vars, `~/.aws/credentials`, IMDSv2
- GenerateDataKey for DEK creation, Encrypt/Decrypt for wrap/unwrap
- EC2 Instance Metadata Service v2 with token-based session authentication

### AwsSecretsManagerKeyStore
- Store/retrieve keys from AWS Secrets Manager via SigV4 REST
- GetSecretValue for retrieval, CreateSecret/PutSecretValue for upsert
- Secure cache with GCHandle.Alloc(Pinned) and CryptographicOperations.ZeroMemory on eviction
- ListSecrets with prefix filtering, DeleteSecret with 7-day recovery window

### GcpSecretManagerKeyStore
- Store/retrieve from GCP Secret Manager via OAuth2 bearer token
- GET `/v1/projects/{project}/secrets/{secret}/versions/latest:access`
- POST to create secret resource then addVersion for new keys
- Same pinned-memory secure cache pattern as AWS counterpart

## Task 2: SLSA Level 3 Provenance Generation

**Commit:** `e391d1be`

### SlsaProvenanceGenerator
- `IProvenanceGenerator` interface with `GenerateAsync(ProvenanceRequest)`
- Computes SHA-256 digest for each artifact file
- Builds in-toto Statement v1.0 with predicateType `https://slsa.dev/provenance/v1`
- SLSA v1.0 predicate: buildDefinition (buildType, externalParameters, resolvedDependencies) + runDetails (builder, metadata)
- Signs with RSA-PSS SHA-256 via IKeyStore key (HMAC-SHA256 fallback)
- Wraps signed output in DSSE (Dead Simple Signing Envelope) format
- Level determination: L1 (exists), L2 (signed), L3 (signed + builder + source + commit)

### SlsaVerifier
- `IProvenanceVerifier` with `VerifyAsync(SlsaProvenance, VerificationPolicy)`
- Level 1: structural validation (type, predicateType, subjects with SHA-256, builder ID)
- Level 2: signature verification (RSA-PSS, HMAC-SHA256, key store integration)
- Level 3: builder in allowed list, source in allowed repos, no user-controlled params, build type check
- Additional: MaxProvenanceAge enforcement, artifact digest re-verification against files on disk
- `VerifyFromJsonAsync` parses both DSSE envelopes and raw statements

### Message Bus Integration
- `security.supply-chain.provenance.generate` — published on generation
- `security.supply-chain.provenance.verify` — published on verification

## Deviations from Plan

None - plan executed exactly as written.

## Verification

- SDK build: zero errors, zero warnings
- Kernel build: zero errors, zero warnings
- GcpKmsProvider and AwsKmsProvider both implement IKeyStore and IEnvelopeKeyStore
- AwsSecretsManagerKeyStore and GcpSecretManagerKeyStore implement IKeyStore via KeyStoreStrategyBase
- SlsaProvenanceGenerator produces JSON with SLSA v1.0 predicate
- SlsaVerifier validates all 3 levels with VerificationPolicy

## Self-Check: PASSED

- All 4 created files exist on disk
- CloudKmsProvider.cs: 1037 lines (min 150)
- SecretsManagerKeyStore.cs: 1084 lines (min 150)
- SlsaProvenanceGenerator.cs: 525 lines (min 100)
- SlsaVerifier.cs: 660 lines (min 100)
- Commit 112be57a: found (Task 1)
- Commit e391d1be: found (Task 2)
- SDK build: 0 errors, 0 warnings
- Kernel build: 0 errors, 0 warnings
