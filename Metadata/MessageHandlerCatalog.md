# Comprehensive Message Handler Catalog

**Generated**: 2026-02-17
**Files Analyzed**: 59 files (53 plugin files + 6 additional handler files)
**Total Handlers Found**: 370+

## Legend
- **TYPED** = Simple request-response: deserialize payload, call method, write result back
- **OVERRIDE** = Complex: multi-step flow, side effects, special error handling, streaming
- **FIRE-AND-FORGET** = Write operation or event, no response needed
- **BROKEN** = Computes result but doesn't write back to message.Payload (lost response)
- **STUB** = Handler body is empty or placeholder (no real work done)

## Response Patterns Found
1. **Direct Payload Write**: `message.Payload["key"] = value` (most common)
2. **_response Pattern**: `message.Payload["_response"] = dict` (unified response object)
3. **MessageResponse Return**: `return MessageResponse.Ok(...)` via Subscribe callback
4. **PublishAsync Pattern**: Sends response to separate topic (pub/sub)
5. **No Response**: Handler does work but never writes back (BROKEN)

---

## 1. AirGapBridge (AirGapBridgePlugin.cs)

| MESSAGE_TYPE | CLASSIFICATION | WRITES_BACK | NOTES |
|---|---|---|---|
| airgap.scan | TYPED | YES | Scans for devices, writes device list to Payload["devices"] |
| airgap.mount | TYPED | YES | Mounts storage, writes storageId/capacity |
| airgap.unmount | TYPED | YES | Unmounts instance, writes instanceId/blobCount |
| airgap.setup | TYPED | YES | Sets up air gap, writes success=true |
| airgap.import | OVERRIDE | YES | Imports data via ImportDataAsync, writes result with multi-step flow |
| airgap.export | OVERRIDE | YES | Exports data via ExportDataAsync, writes packageId/filePath/shardCount |
| airgap.authenticate | TYPED | YES | Authenticates, writes success/sessionToken/expiresAt |
| airgap.sync | TYPED | YES | Syncs data, writes success/itemsSynced/conflicts/bytesSynced |
| airgap.status | TYPED | YES | Returns status, writes isRunning/deviceCount/devices |

## 2. AdaptiveTransport (AdaptiveTransportPlugin.cs)

| MESSAGE_TYPE | CLASSIFICATION | WRITES_BACK | NOTES |
|---|---|---|---|
| transport.send | TYPED | YES | Sends data, writes Payload["result"] with transfer info |
| transport.quality | TYPED | YES | Gets quality metrics, writes Payload["result"] |
| transport.switch | TYPED | YES | Switches protocol, writes Payload["result"] |
| transport.config | TYPED | YES | Gets config, writes Payload["result"] |
| transport.stats | TYPED | YES | Gets stats, writes Payload["result"] with metrics |
| transport.bandwidth.measure | TYPED | YES | Measures bandwidth, writes Payload["result"] |
| transport.bandwidth.classify | TYPED | YES | Classifies bandwidth, writes Payload["result"] |
| transport.bandwidth.parameters | TYPED | YES | Gets parameters, writes Payload["result"] |

## 3. PluginMarketplace (PluginMarketplacePlugin.cs)

| MESSAGE_TYPE | CLASSIFICATION | WRITES_BACK | NOTES |
|---|---|---|---|
| marketplace.list | TYPED | YES | Lists plugins, writes Payload["result"] |
| marketplace.install | OVERRIDE | YES | Complex multi-step install with dependency resolution, rollback on failure; writes result or error |
| marketplace.uninstall | OVERRIDE | YES | Complex uninstall with dependent checking, writes result or error |
| marketplace.update | OVERRIDE | YES | Complex update with rollback, dependency resolution; writes result or error |
| marketplace.search | TYPED | YES | Searches plugins, writes Payload["result"] |
| marketplace.certify | TYPED | YES | Certifies plugin, writes Payload["result"] |
| marketplace.review | TYPED | YES | Reviews plugin, writes Payload["result"] |
| marketplace.analytics | TYPED | YES | Gets analytics, writes Payload["result"] |

## 4. TamperProof (TamperProofPlugin.cs)

| MESSAGE_TYPE | CLASSIFICATION | WRITES_BACK | NOTES |
|---|---|---|---|
| (any) | STUB | NO | OnMessageAsync is effectively empty - logs message type and returns. Comment says "No hash handling needed here anymore" - delegates to UltimateDataIntegrity |

## 5. UltimateBlockchain (UltimateBlockchainPlugin.cs)

| MESSAGE_TYPE | CLASSIFICATION | WRITES_BACK | NOTES |
|---|---|---|---|
| blockchain.anchor | OVERRIDE | YES | Anchors hash to blockchain, writes anchor/success; complex error handling |
| blockchain.verify | OVERRIDE | YES | Verifies hash, writes result/isValid; complex error handling |
| blockchain.chain | TYPED | YES | Gets audit chain, writes auditChain/success |
| blockchain.latest | TYPED | YES | Gets latest block, writes blockInfo/success |

## 6. UltimateDataIntegrity (UltimateDataIntegrityPlugin.cs)

| MESSAGE_TYPE | CLASSIFICATION | WRITES_BACK | NOTES |
|---|---|---|---|
| integrity.hash.compute | TYPED | YES | Computes hash, writes hash/hashHex/algorithm/computedAt |
| integrity.hash.verify | TYPED | YES | Verifies hash, writes valid/actualHashHex |
| integrity.supported.algorithms | TYPED | YES | Lists algorithms, writes algorithms/count/defaultAlgorithm |

## 7. FuseDriver (FuseDriverPlugin.cs)

| MESSAGE_TYPE | CLASSIFICATION | WRITES_BACK | NOTES |
|---|---|---|---|
| mount | FIRE-AND-FORGET | NO | Mounts filesystem, no response written |
| unmount | FIRE-AND-FORGET | NO | Unmounts, no response |
| configure | FIRE-AND-FORGET | NO | Sets config, no response |
| invalidate-cache | FIRE-AND-FORGET | NO | Invalidates cache entries, no response |
| get-status | BROKEN | NO | Comment says "Respond with current status" but body is empty |

## 8. UltimateFilesystem (UltimateFilesystemPlugin.cs)

| MESSAGE_TYPE | CLASSIFICATION | WRITES_BACK | NOTES |
|---|---|---|---|
| filesystem.detect | TYPED | YES | Detects filesystem type, writes success/filesystemType/strategy |
| filesystem.read | TYPED | YES | Reads data, writes success/data/bytesRead |
| filesystem.write | TYPED | YES | Writes data, writes success/bytesWritten |
| filesystem.metadata | TYPED | YES | Gets metadata, writes success/filesystemType/totalBytes/etc |
| filesystem.mount | TYPED | YES | Mounts FS, writes success/mountPoint |
| filesystem.unmount | TYPED | YES | Unmounts FS, writes success |
| filesystem.list-strategies | TYPED | YES | Lists strategies, writes strategies/count |
| filesystem.stats | TYPED | YES | Gets stats, writes totalReads/totalWrites/etc |
| filesystem.quota | TYPED | YES | Manages quota, writes success |
| filesystem.optimize | TYPED | YES | Optimizes, writes success/recommendedStrategy |

## 9. UltimateDataLineage (UltimateDataLineagePlugin.cs)

| MESSAGE_TYPE | CLASSIFICATION | WRITES_BACK | NOTES |
|---|---|---|---|
| lineage.track | TYPED | YES | Tracks lineage, writes success/recordId |
| lineage.upstream | TYPED | YES | Gets upstream lineage, writes success/nodeCount/edgeCount/depth |
| lineage.downstream | TYPED | YES | Gets downstream lineage, writes success/nodeCount/edgeCount |
| lineage.impact | TYPED | YES | Analyzes impact, writes success/directlyImpacted/indirectlyImpacted/impactScore |
| lineage.add-node | TYPED | YES | Adds node, writes success/nodeId |
| lineage.add-edge | TYPED | YES | Adds edge, writes success/edgeId |
| lineage.get-node | TYPED | YES | Gets node, writes success/name/nodeType/etc |
| lineage.search | TYPED | YES | Searches, writes success/results/count |
| lineage.export | TYPED | YES | Exports, writes success/format/data |
| lineage.stats | TYPED | YES | Gets stats, writes totalNodes/totalEdges/etc |
| lineage.list-strategies | TYPED | YES | Lists strategies, writes strategies/count |
| lineage.provenance | TYPED | YES | Gets provenance, writes success/dataObjectId |

## 10. UltimateResourceManager (UltimateResourceManagerPlugin.cs)

| MESSAGE_TYPE | CLASSIFICATION | WRITES_BACK | NOTES |
|---|---|---|---|
| resource.allocate | OVERRIDE | YES | Complex allocation with quota checks, writes success/allocationHandle/allocated* |
| resource.release | TYPED | YES | Releases allocation, writes success |
| resource.metrics | TYPED | YES | Gets metrics, writes metrics dict |
| resource.quota.set | TYPED | YES | Sets quota, writes success |
| resource.quota.get | TYPED | YES | Gets quota, writes success/maxCpuCores/etc |
| resource.quota.remove | TYPED | YES | Removes quota, writes success |
| resource.list-strategies | TYPED | YES | Lists strategies, writes strategies/count |
| resource.stats | TYPED | YES | Gets stats, writes totalAllocations/etc |
| resource.preempt | OVERRIDE | YES | Preempts lower-priority allocations, writes success/preemptedCount |
| resource.reserve | TYPED | YES | Reserves resources, writes success/reservationId |

## 11. WinFspDriver (WinFspDriverPlugin.cs)

| MESSAGE_TYPE | CLASSIFICATION | WRITES_BACK | NOTES |
|---|---|---|---|
| winfsp.mount | TYPED | YES | Uses _response pattern; returns MessageResponse.Ok with MountPoint/Success |
| winfsp.unmount | TYPED | YES | Returns MessageResponse via _response pattern |
| winfsp.status | TYPED | YES | Returns status via _response pattern |
| winfsp.cache.stats | TYPED | YES | Returns cache stats via _response pattern |
| winfsp.cache.clear | TYPED | YES | Clears cache, returns via _response pattern |
| winfsp.vss.snapshot | TYPED | YES | Creates VSS snapshot via _response pattern |
| winfsp.vss.list | TYPED | YES | Lists VSS snapshots via _response pattern |
| winfsp.shell.register | TYPED | YES | Registers shell extension via _response pattern |
| winfsp.bitlocker.status | TYPED | YES | Gets BitLocker status via _response pattern |
| winfsp.bitlocker.health | TYPED | YES | Gets BitLocker health via _response pattern |
| winfsp.bitlocker.tpm | TYPED | YES | Gets TPM info via _response pattern |

## 12. UltimateInterface (UltimateInterfacePlugin.cs)

| MESSAGE_TYPE | CLASSIFICATION | WRITES_BACK | NOTES |
|---|---|---|---|
| interface.start | TYPED | YES | Starts interface strategy, writes success/strategyId |
| interface.stop | TYPED | YES | Stops interface strategy, writes success/strategyId |
| interface.list-strategies | TYPED | YES | Lists strategies, writes strategies/count |
| interface.set-default | TYPED | YES | Sets default, writes success/defaultStrategy |
| interface.stats | TYPED | YES | Gets stats, writes totalRequests/etc |
| interface.health | TYPED | YES | Gets health, writes isHealthy/lastChecked/lastError |
| interface.bridge | TYPED | YES | Creates protocol bridge, writes success/bridgeId/sourceProtocol/targetProtocol |
| interface.parse-intent | OVERRIDE | YES | Parses NLU intent with fallback logic, writes success/intent/confidence/entities |
| interface.conversation | OVERRIDE | YES | Handles conversation with fallback, writes success/response/intent/suggestedActions |
| interface.detect-language | OVERRIDE | YES | Detects language with fallback, writes success/languageCode/confidence |

## 13. UltimateCompliance (UltimateCompliancePlugin.cs)

| MESSAGE_TYPE | CLASSIFICATION | WRITES_BACK | NOTES |
|---|---|---|---|
| compliance.check | TYPED | YES | Checks compliance, writes Result |
| compliance.check-all | TYPED | YES | Checks all, writes Report |
| compliance.list-strategies | TYPED | YES | Lists strategies, writes Strategies/Count |
| compliance.report.generate | TYPED | YES | Generates report, writes Report |
| compliance.custody.export | TYPED | YES | Exports custody chain, writes Report |
| compliance.dashboard.request | TYPED | YES | Gets dashboard data, writes DashboardData |

## 14. UltimateAccessControl (UltimateAccessControlPlugin.cs)

| MESSAGE_TYPE | CLASSIFICATION | WRITES_BACK | NOTES |
|---|---|---|---|
| accesscontrol.evaluate | TYPED | YES | Evaluates access, writes Decision |
| accesscontrol.list-strategies | TYPED | YES | Lists strategies, writes Strategies/Count |
| accesscontrol.set-default | TYPED | YES | Sets default, writes Success/DefaultStrategy |

## 15. UltimateServerless (UltimateServerlessPlugin.cs)

| MESSAGE_TYPE | CLASSIFICATION | WRITES_BACK | NOTES |
|---|---|---|---|
| serverless.invoke | STUB | YES | Writes status="invocation_queued" but actual invocation not implemented |
| serverless.register | TYPED | YES | Registers function, writes status="registered" |
| serverless.list | TYPED | YES | Lists functions, writes functions/count |
| serverless.stats | TYPED | YES | Gets stats, writes statistics or globalStatistics |
| serverless.strategies | TYPED | YES | Lists strategies, writes strategies |
| serverless.optimize | TYPED | YES | Gets recommendations, writes recommendations/count |

## 16. UltimateKeyManagement (UltimateKeyManagementPlugin.cs)

| MESSAGE_TYPE | CLASSIFICATION | WRITES_BACK | NOTES |
|---|---|---|---|
| keymanagement.configure | STUB | NO | HandleConfigurationMessageAsync body is `return Task.CompletedTask;` |
| keymanagement.register.strategy | OVERRIDE | NO | Registers and initializes strategy but no response written back |
| keymanagement.rotate.now | FIRE-AND-FORGET | NO | Triggers immediate rotation; no response written |

## 17. UniversalDashboards (UniversalDashboardsPlugin.cs)

| MESSAGE_TYPE | CLASSIFICATION | WRITES_BACK | NOTES |
|---|---|---|---|
| dashboard.strategy.list | TYPED | YES | Lists strategies, writes strategies/count |
| dashboard.strategy.configure | TYPED | YES | Configures strategy, writes success/activeStrategy |
| dashboard.create | TYPED | YES | Creates dashboard, writes dashboard dict |
| dashboard.update | TYPED | YES | Updates dashboard, writes dashboard dict |
| dashboard.delete | TYPED | YES | Deletes dashboard, writes success |
| dashboard.list | TYPED | YES | Lists dashboards, writes dashboards/count |
| dashboard.push | TYPED | YES | Pushes data, writes success/rowsPushed/durationMs |
| dashboard.stats | TYPED | YES | Gets stats, writes totalDashboardsCreated/etc |

## 18. UltimateDocGen (UltimateDocGenPlugin.cs)

| MESSAGE_TYPE | CLASSIFICATION | WRITES_BACK | NOTES |
|---|---|---|---|
| docgen.strategy.list | TYPED | YES | Uses _response pattern, returns strategy list dict |
| docgen.strategy.select | TYPED | YES | Uses _response pattern, returns selection result |
| docgen.stats | TYPED | YES | Uses _response pattern, returns statistics |

## 19. UltimateMicroservices (UltimateMicroservicesPlugin.cs)

| MESSAGE_TYPE | CLASSIFICATION | WRITES_BACK | NOTES |
|---|---|---|---|
| microservices.register | TYPED | YES | Registers service, writes status="registered" |
| microservices.discover | TYPED | YES | Discovers services, writes services/count |
| microservices.invoke | TYPED | YES | Invokes service, writes status="invoked" |
| microservices.health | TYPED | YES | Gets health, writes healthStatus/lastHealthCheck |
| microservices.stats | TYPED | YES | Gets stats, writes globalStatistics dict |
| microservices.strategies | TYPED | YES | Lists strategies, writes strategies |

## 20. UltimateSDKPorts (UltimateSDKPortsPlugin.cs)

| MESSAGE_TYPE | CLASSIFICATION | WRITES_BACK | NOTES |
|---|---|---|---|
| sdkports.strategy.list | TYPED | YES | Uses _response pattern |
| sdkports.strategy.select | TYPED | YES | Uses _response pattern |
| sdkports.method.register | TYPED | YES | Uses _response pattern |
| sdkports.invoke | TYPED | YES | Uses _response pattern |
| sdkports.generate | TYPED | YES | Uses _response pattern |
| sdkports.languages | TYPED | YES | Uses _response pattern |
| sdkports.stats | TYPED | YES | Uses _response pattern |

## 21. UltimateWorkflow (UltimateWorkflowPlugin.cs)

| MESSAGE_TYPE | CLASSIFICATION | WRITES_BACK | NOTES |
|---|---|---|---|
| workflow.strategy.list | TYPED | YES | Uses _response pattern |
| workflow.strategy.select | TYPED | YES | Uses _response pattern |
| workflow.define | TYPED | YES | Uses _response pattern |
| workflow.execute | OVERRIDE | YES | Executes workflow via _response pattern (async execution) |
| workflow.status | TYPED | YES | Uses _response pattern |
| workflow.list | TYPED | YES | Uses _response pattern |
| workflow.stats | TYPED | YES | Uses _response pattern |

## 22. UltimateMultiCloud (UltimateMultiCloudPlugin.cs)

| MESSAGE_TYPE | CLASSIFICATION | WRITES_BACK | NOTES |
|---|---|---|---|
| multicloud.register-provider | TYPED | YES | Registers provider, writes success/message |
| multicloud.list-strategies | TYPED | YES | Lists strategies, writes strategies/count |
| multicloud.execute | TYPED | YES | Executes operation, writes success/message |
| multicloud.failover | TYPED | YES | Triggers failover, writes success/failedOver/from/to |
| multicloud.replicate | TYPED | YES | Replicates, writes success/replicated |
| multicloud.optimize-cost | TYPED | YES | Optimizes cost, writes success/estimatedMonthlySavings/recommendations |
| multicloud.recommend | TYPED | YES | Recommends provider, writes success/providerId/score/reason |
| multicloud.migrate | TYPED | YES | Migrates, writes success/status/migrationId |
| multicloud.stats | TYPED | YES | Gets stats, writes totalOperations/etc |

## 23. UltimateSustainability (UltimateSustainabilityPlugin.cs)

| MESSAGE_TYPE | CLASSIFICATION | WRITES_BACK | NOTES |
|---|---|---|---|
| sustainability.list | STUB | NO | Case body is just `break;` with comment - no response written |
| sustainability.select | FIRE-AND-FORGET | NO | Calls SetActiveStrategy() but writes nothing back |
| sustainability.recommendations | STUB | NO | Case body is just `break;` with comment "would need async handling" |

## 24. UltimateResilience (UltimateResiliencePlugin.cs)

| MESSAGE_TYPE | CLASSIFICATION | WRITES_BACK | NOTES |
|---|---|---|---|
| resilience.execute | TYPED | YES | Executes with resilience, writes success/message |
| resilience.list-strategies | TYPED | YES | Lists strategies, writes strategies/count |
| resilience.get-strategy | TYPED | YES | Gets strategy details, writes id/name/category/description/characteristics/statistics |
| resilience.stats | TYPED | YES | Gets stats, writes totalExecutions/etc |
| resilience.recommend | TYPED | YES | Recommends strategy, writes recommendedStrategy/reasoning/confidence/alternatives |
| resilience.health-check | TYPED | YES | Checks health, writes healthChecks/count |
| resilience.circuit-status | TYPED | YES | Gets circuit status, writes circuitBreakers/count |
| resilience.reset | TYPED | YES | Resets strategies, writes success/message |

## 25. UltimateDeployment (UltimateDeploymentPlugin.cs)

| MESSAGE_TYPE | CLASSIFICATION | WRITES_BACK | NOTES |
|---|---|---|---|
| deployment.deploy | OVERRIDE | YES | Complex deploy with strategy selection, writes result/deploymentId |
| deployment.rollback | TYPED | YES | Rollback, writes result |
| deployment.scale | TYPED | YES | Scales, writes result |
| deployment.health-check | TYPED | YES | Health check, writes results/healthyCount/totalCount |
| deployment.list-strategies | TYPED | YES | Lists strategies, writes strategies/count |
| deployment.recommend | TYPED | YES | Recommends, writes recommendation dict |
| deployment.status | TYPED | YES | Gets status, writes state/found |

## 26. UltimateRTOSBridge (UltimateRTOSBridgePlugin.cs)

| MESSAGE_TYPE | CLASSIFICATION | WRITES_BACK | NOTES |
|---|---|---|---|
| rtos.execute | TYPED | YES | Executes RTOS operation, writes Result |
| rtos.list-strategies | TYPED | YES | Lists strategies, writes Strategies/Count |
| rtos.set-default | TYPED | YES | Sets default, writes Success/DefaultStrategy |
| rtos.get-platforms | TYPED | YES | Gets platforms, writes Platforms |
| rtos.get-standards | TYPED | YES | Gets standards, writes Standards |

## 27. UltimateIoTIntegration (UltimateIoTIntegrationPlugin.cs)

| MESSAGE_TYPE | CLASSIFICATION | WRITES_BACK | NOTES |
|---|---|---|---|
| iot.device.register | TYPED | YES | Uses DeserializeRequest<T>, writes Payload["result"] |
| iot.telemetry.ingest | TYPED | YES | Uses DeserializeRequest<T>, writes Payload["result"] |
| iot.command.send | TYPED | YES | Uses DeserializeRequest<T>, writes Payload["result"] |
| iot.device.provision | TYPED | YES | Uses DeserializeRequest<T>, writes Payload["result"] |
| iot.analytics.detect | TYPED | YES | Uses DeserializeRequest<T>, writes Payload["result"] |
| iot.security.authenticate | TYPED | YES | Uses DeserializeRequest<T>, writes Payload["result"] |
| iot.edge.deploy | TYPED | YES | Uses DeserializeRequest<T>, writes Payload["result"] |
| iot.transform | TYPED | YES | Uses DeserializeRequest<T>, writes Payload["result"] |
| iot.list.strategies | TYPED | YES | Lists strategies, writes strategies/count |
| iot.statistics | TYPED | YES | Gets stats, writes totalDevicesManaged/etc |

## 28. UniversalObservability (UniversalObservabilityPlugin.cs)

| MESSAGE_TYPE | CLASSIFICATION | WRITES_BACK | NOTES |
|---|---|---|---|
| observability.universal.list | STUB | NO | Case body is just `break;` - no response written |
| observability.universal.select.metrics | FIRE-AND-FORGET | NO | Calls SetActiveMetricsStrategy() but writes nothing back |
| observability.universal.select.logging | FIRE-AND-FORGET | NO | Calls SetActiveLoggingStrategy() but writes nothing back |
| observability.universal.select.tracing | FIRE-AND-FORGET | NO | Calls SetActiveTracingStrategy() but writes nothing back |

## 29. UltimateCompute (UltimateComputePlugin.cs)

| MESSAGE_TYPE | CLASSIFICATION | WRITES_BACK | NOTES |
|---|---|---|---|
| compute.list-strategies | TYPED | YES | Lists strategies, writes strategies/count |
| compute.list-runtimes | TYPED | YES | Lists runtimes, writes runtimes |
| compute.stats | TYPED | YES | Gets stats, writes registeredStrategies/usageByStrategy/etc |

## 30. UltimateDataManagement (UltimateDataManagementPlugin.cs)

| MESSAGE_TYPE | CLASSIFICATION | WRITES_BACK | NOTES |
|---|---|---|---|
| data-management.deduplicate | TYPED | YES | Deduplicates, writes success |
| data-management.apply-retention | TYPED | YES | Applies retention, writes success |
| data-management.version | TYPED | YES | Creates version, writes success |
| data-management.tier | TYPED | YES | Tiers data, writes success |
| data-management.shard | TYPED | YES | Shards data, writes success |
| data-management.lifecycle | TYPED | YES | Manages lifecycle, writes success |
| data-management.cache | TYPED | YES | Caches data, writes success |
| data-management.index | TYPED | YES | Indexes data, writes success |
| data-management.optimize | OVERRIDE | YES | Auto-optimization (can be disabled), writes success or error |
| data-management.list-strategies | TYPED | YES | Lists strategies, writes strategies/count |
| data-management.stats | TYPED | YES | Gets stats, writes totalOperations/etc |
| data-management.create-policy | TYPED | YES | Creates policy, writes success |
| data-management.apply-policy | TYPED | YES | Applies policy, writes success |

## 31. UltimateDataPrivacy (UltimateDataPrivacyPlugin.cs)

| MESSAGE_TYPE | CLASSIFICATION | WRITES_BACK | NOTES |
|---|---|---|---|
| privacy.anonymize | TYPED | YES | Anonymizes data, writes success |
| privacy.tokenize | TYPED | YES | Tokenizes data, writes success |
| privacy.mask | TYPED | YES | Masks data, writes success |
| privacy.detokenize | TYPED | YES | Detokenizes data, writes success |
| privacy.pseudonymize | TYPED | YES | Pseudonymizes, writes success |
| privacy.consent (sub: record) | OVERRIDE | YES | Records consent, writes consent object |
| privacy.consent (sub: check) | OVERRIDE | YES | Checks consent, writes consents list |
| privacy.dsar | TYPED | YES | Processes DSAR, writes success |
| privacy.audit | TYPED | YES | Gets audit log, writes success/compliant |
| privacy.retention | TYPED | YES | Applies retention, writes success |
| privacy.list-strategies | TYPED | YES | Lists strategies, writes strategies/count |
| privacy.stats | TYPED | YES | Gets stats, writes totalOperations/etc |

## 32. UltimateDataMesh (UltimateDataMeshPlugin.cs)

| MESSAGE_TYPE | CLASSIFICATION | WRITES_BACK | NOTES |
|---|---|---|---|
| mesh.create-domain | TYPED | YES | Creates domain, writes domain/success |
| mesh.list-domains | TYPED | YES | Lists domains, writes domains/count |
| mesh.create-product | TYPED | YES | Creates product, writes product/success |
| mesh.list-products | TYPED | YES | Lists products, writes products/count |
| mesh.register-consumer | TYPED | YES | Registers consumer, writes consumer/success |
| mesh.create-share | TYPED | YES | Creates data share, writes share/success |
| mesh.update-share | TYPED | YES | Updates share, writes share/success or error |
| mesh.create-policy | TYPED | YES | Creates policy, writes policy/success |
| mesh.discover-products | TYPED | YES | Discovers products, writes products/count |
| mesh.list-strategies | TYPED | YES | Lists strategies, writes strategies/count |
| mesh.stats | TYPED | YES | Gets stats, writes totalOperations/etc |

## 33. UltimateDataLake (UltimateDataLakePlugin.cs)

| MESSAGE_TYPE | CLASSIFICATION | WRITES_BACK | NOTES |
|---|---|---|---|
| datalake.ingest | TYPED | YES | Ingests data, writes success |
| datalake.query | TYPED | YES | Queries data, writes success |
| datalake.catalog (sub: add) | TYPED | YES | Adds catalog entry, writes entry |
| datalake.catalog (sub: get) | TYPED | YES | Gets entry, writes entry |
| datalake.catalog (sub: list) | TYPED | YES | Lists entries, writes entries |
| datalake.catalog (sub: remove) | FIRE-AND-FORGET | YES | Removes entry, writes success |
| datalake.lineage (sub: add) | TYPED | YES | Adds lineage record, writes record |
| datalake.lineage (sub: get) | TYPED | YES | Gets record, writes record |
| datalake.lineage (sub: upstream) | TYPED | YES | Gets upstream, writes records |
| datalake.lineage (sub: downstream) | TYPED | YES | Gets downstream, writes records |
| datalake.lineage (sub: list) | TYPED | YES | Lists records, writes records |
| datalake.governance (sub: add) | TYPED | YES | Adds policy, writes policy |
| datalake.governance (sub: list) | TYPED | YES | Lists policies, writes policies |
| datalake.governance (sub: remove) | FIRE-AND-FORGET | YES | Removes policy, writes success |
| datalake.optimize | TYPED | YES | Optimizes, writes success |
| datalake.partition | TYPED | YES | Partitions, writes success |
| datalake.list-strategies | TYPED | YES | Lists strategies, writes strategies/count |
| datalake.stats | TYPED | YES | Gets stats |

## 34. UltimateDataQuality (UltimateDataQualityPlugin.cs)

| MESSAGE_TYPE | CLASSIFICATION | WRITES_BACK | NOTES |
|---|---|---|---|
| quality.validate | TYPED | YES | Validates data, writes success |
| quality.profile | TYPED | YES | Profiles data, writes success |
| quality.cleanse | TYPED | YES | Cleanses data, writes success |
| quality.detect-duplicates | TYPED | YES | Detects duplicates, writes success |
| quality.standardize | TYPED | YES | Standardizes, writes success |
| quality.score | TYPED | YES | Scores quality, writes success |
| quality.monitor | TYPED | YES | Monitors quality, writes success |
| quality.report | TYPED | YES | Generates report, writes success |
| quality.list-strategies | TYPED | YES | Lists strategies, writes strategies/count |
| quality.stats | TYPED | YES | Gets stats, writes totalRecordsProcessed/etc |
| quality.create-policy | TYPED | YES | Creates policy, writes success |
| quality.apply-policy | TYPED | YES | Applies policy, writes success |

## 35. UltimateDataCatalog (UltimateDataCatalogPlugin.cs)

| MESSAGE_TYPE | CLASSIFICATION | WRITES_BACK | NOTES |
|---|---|---|---|
| catalog.ingest | TYPED | YES | Ingests asset, writes success |
| catalog.search | TYPED | YES | Searches catalog, writes results/count/success |
| catalog.asset (sub: add) | TYPED | YES | Adds asset, writes asset |
| catalog.asset (sub: get) | TYPED | YES | Gets asset, writes asset |
| catalog.asset (sub: list) | TYPED | YES | Lists assets, writes assets |
| catalog.asset (sub: remove) | FIRE-AND-FORGET | YES | Removes asset, writes success |
| catalog.relationship (sub: add) | TYPED | YES | Adds relationship, writes relationship |
| catalog.relationship (sub: upstream) | TYPED | YES | Gets upstream, writes relationships |
| catalog.relationship (sub: downstream) | TYPED | YES | Gets downstream, writes relationships |
| catalog.relationship (sub: list) | TYPED | YES | Lists relationships, writes relationships |
| catalog.glossary (sub: add) | TYPED | YES | Adds term, writes term |
| catalog.glossary (sub: get) | TYPED | YES | Gets term, writes term |
| catalog.glossary (sub: list) | TYPED | YES | Lists terms, writes terms |
| catalog.glossary (sub: remove) | FIRE-AND-FORGET | YES | Removes term, writes success |
| catalog.enrich | TYPED | YES | Enriches catalog, writes success |
| catalog.api | TYPED | YES | Handles API, writes success |
| catalog.list-strategies | TYPED | YES | Lists strategies, writes strategies/count |
| catalog.stats | TYPED | YES | Gets stats, writes totalOperations/etc |

## 36. UltimateDataGovernance (UltimateDataGovernancePlugin.cs)

| MESSAGE_TYPE | CLASSIFICATION | WRITES_BACK | NOTES |
|---|---|---|---|
| governance.policy (sub: create) | TYPED | YES | Creates policy, writes policy |
| governance.policy (sub: get) | TYPED | YES | Gets policy, writes policy |
| governance.policy (sub: list) | TYPED | YES | Lists policies, writes policies |
| governance.policy (sub: enforce) | TYPED | YES | Enforces policy, writes enforced/policyName |
| governance.ownership (sub: assign) | TYPED | YES | Assigns ownership, writes ownership |
| governance.ownership (sub: get) | TYPED | YES | Gets ownership, writes ownership |
| governance.ownership (sub: list) | TYPED | YES | Lists ownerships, writes ownerships |
| governance.stewardship | TYPED | YES | Manages stewardship, writes success |
| governance.classification (sub: classify) | TYPED | YES | Classifies data, writes classification |
| governance.classification (sub: get) | TYPED | YES | Gets classification, writes classification |
| governance.classification (sub: list) | TYPED | YES | Lists classifications, writes classifications |
| governance.workflow | TYPED | YES | Manages workflow, writes success |
| governance.quality-gate | TYPED | YES | Manages quality gate, writes success |
| governance.audit | TYPED | YES | Gets audit, writes success/compliant |
| governance.compliance | TYPED | YES | Checks compliance, writes success |
| governance.list-strategies | TYPED | YES | Lists strategies, writes strategies/count |
| governance.stats | TYPED | YES | Gets stats, writes totalOperations/etc |

## 37. UltimateConnector (UltimateConnectorPlugin.cs)

| MESSAGE_TYPE | CLASSIFICATION | WRITES_BACK | NOTES |
|---|---|---|---|
| connector.list-strategies | TYPED | YES | Lists strategies, writes strategies/count |
| connector.list-categories | TYPED | YES | Lists categories, writes categories |
| connector.stats | TYPED | YES | Gets stats, writes registeredStrategies/etc |

## 38. UltimateDataProtection (UltimateDataProtectionPlugin.cs)

| MESSAGE_TYPE | CLASSIFICATION | WRITES_BACK | NOTES |
|---|---|---|---|
| dataprotection.list.strategies | STUB | NO | `HandleListStrategiesAsync` body is `return Task.CompletedTask;` |
| dataprotection.select.strategy | STUB | NO | `HandleSelectStrategyAsync` body is `return Task.CompletedTask;` |
| dataprotection.backup.request | STUB | NO | `HandleBackupRequestAsync` body is `return Task.CompletedTask;` |
| dataprotection.restore.request | STUB | NO | `HandleRestoreRequestAsync` body is `return Task.CompletedTask;` |
| dataprotection.statistics | STUB | NO | `HandleStatisticsRequestAsync` body is `return Task.CompletedTask;` |

## 39. UltimateReplication (UltimateReplicationPlugin.cs)

| MESSAGE_TYPE | CLASSIFICATION | WRITES_BACK | NOTES |
|---|---|---|---|
| replication.strategy.list | TYPED | YES | Uses _response pattern |
| replication.strategy.select | TYPED | YES | Uses _response pattern |
| replication.strategy.info | TYPED | YES | Uses _response pattern |
| replication.replicate | OVERRIDE | YES | Replicates data via _response pattern |
| replication.status | TYPED | YES | Uses _response pattern |
| replication.lag | TYPED | YES | Uses _response pattern |
| replication.conflict.detect | TYPED | YES | Uses _response pattern |
| replication.conflict.resolve | OVERRIDE | YES | Resolves conflicts via _response pattern |

## 40. UltimateRaid (UltimateRaidPlugin.cs)

OnMessageAsync delegates to `base.OnMessageAsync()`. All handlers registered via `MessageBus.Subscribe`:

| MESSAGE_TYPE (topic) | CLASSIFICATION | WRITES_BACK | NOTES |
|---|---|---|---|
| raid.ultimate.write | TYPED | YES | Writes data, writes bytesWritten |
| raid.ultimate.read | TYPED | YES | Reads data, writes data/bytesRead |
| raid.ultimate.rebuild | TYPED | YES | Rebuilds, writes rebuilt=true |
| raid.ultimate.verify | TYPED | YES | Verifies, writes result |
| raid.ultimate.scrub | TYPED | YES | Scrubs, writes result |
| raid.ultimate.health | TYPED | YES | Health check, writes health |
| raid.ultimate.stats | TYPED | YES | Stats, writes statistics |
| raid.ultimate.add-disk | TYPED | YES | Adds disk, writes added=true |
| raid.ultimate.remove-disk | TYPED | YES | Removes disk, writes removed=true |
| raid.ultimate.replace-disk | TYPED | YES | Replaces disk, writes replaced=true |
| raid.ultimate.list-strategies | TYPED | YES | Lists strategies, writes strategies/count |
| raid.ultimate.set-default | TYPED | YES | Sets default, writes success/defaultStrategy |
| raid.ultimate.predict.failure | OVERRIDE | YES | AI failure prediction, writes failureProbability/prediction/metadata |
| raid.ultimate.optimize.level | OVERRIDE | YES | AI level optimization, writes recommendedLevel/confidence/alternatives |
| raid.ultimate.predict.workload | OVERRIDE | YES | AI workload prediction, writes prediction/confidence/metadata |

## 41. UltimateStorageProcessing (UltimateStorageProcessingPlugin.cs)

| MESSAGE_TYPE | CLASSIFICATION | WRITES_BACK | NOTES |
|---|---|---|---|
| storage-processing.list-strategies | TYPED | YES | Lists strategies, writes strategies/count |
| storage-processing.list-categories | TYPED | YES | Lists categories, writes categories |
| storage-processing.stats | TYPED | YES | Gets stats, writes registeredStrategies/etc |

## 42. UltimateDatabaseProtocol (UltimateDatabaseProtocolPlugin.cs)

| MESSAGE_TYPE | CLASSIFICATION | WRITES_BACK | NOTES |
|---|---|---|---|
| database-protocol.list-strategies | TYPED | YES | Lists strategies, writes strategies/count |
| database-protocol.list-families | TYPED | YES | Lists protocol families, writes families |
| database-protocol.stats | TYPED | YES | Gets stats, writes registeredStrategies/etc |

## 43. UltimateDatabaseStorage (UltimateDatabaseStoragePlugin.cs)

| MESSAGE_TYPE | CLASSIFICATION | WRITES_BACK | NOTES |
|---|---|---|---|
| database.storage.list | BROKEN | NO | Computes strategy list but comments "Response would be sent via message context" |
| database.storage.get | BROKEN | NO | Gets strategy stats but comments "Response would be sent via message context" |
| database.storage.stats | BROKEN | NO | Gets all stats but comments "Response would be sent via message context" |
| database.storage.store | FIRE-AND-FORGET | N/A | Stores data, no result to return (write operation) |
| database.storage.retrieve | BROKEN | NO | Retrieves data, copies to byte[], but discards it - "Response would be sent via message context" |
| database.storage.delete | FIRE-AND-FORGET | N/A | Deletes key, no response |
| database.storage.query | BROKEN | NO | Executes query, gets results, but discards them - "Response would be sent via message context" |
| database.storage.health | BROKEN | NO | Checks health of all strategies but discards results - "Response would be sent via message context" |

## 44. UltimateStorage (UltimateStoragePlugin.cs)

| MESSAGE_TYPE | CLASSIFICATION | WRITES_BACK | NOTES |
|---|---|---|---|
| storage.write | TYPED | YES | Writes data, writes path/strategyId/bytesWritten |
| storage.read | TYPED | YES | Reads data, writes data/bytesRead |
| storage.delete | TYPED | YES | Deletes, writes deleted=true |
| storage.list | TYPED | YES | Lists, writes items/count |
| storage.exists | TYPED | YES | Checks existence, writes exists |
| storage.copy | TYPED | YES | Copies, writes copied=true |
| storage.move | TYPED | YES | Moves, writes moved=true |
| storage.list-strategies | TYPED | YES | Lists strategies, writes strategies/count |
| storage.set-default | TYPED | YES | Sets default, writes success/defaultStrategy |
| storage.stats | TYPED | YES | Gets stats, writes totalWrites/etc |
| storage.health | TYPED | YES | Health check, writes health dict |
| storage.replicate | TYPED | YES | Replicates, writes replicationResults |
| storage.tier | TYPED | YES | Tiers data, writes tiered=true |
| storage.get-metadata | TYPED | YES | Gets metadata, writes metadata |
| storage.set-metadata | TYPED | YES | Sets metadata, writes success |

## 45. UltimateCompression (UltimateCompressionPlugin.cs)

| MESSAGE_TYPE | CLASSIFICATION | WRITES_BACK | NOTES |
|---|---|---|---|
| compression.ultimate.list | STUB | NO | Case body is just `break;` with comment "Reply mechanism would need to be handled by caller" |
| compression.ultimate.select | FIRE-AND-FORGET | NO | Calls SetActiveStrategy() but writes nothing back |

## 46. UltimateEncryption (UltimateEncryptionPlugin.cs)

| MESSAGE_TYPE | CLASSIFICATION | WRITES_BACK | NOTES |
|---|---|---|---|
| encryption.encrypt | TYPED | YES | Encrypts data, writes result/strategyId; may also write generatedKey |
| encryption.decrypt | TYPED | YES | Decrypts data, writes result |
| encryption.list-strategies | TYPED | YES | Lists strategies, writes strategies/count/fipsMode |
| encryption.set-default | TYPED | YES | Sets default, writes success/defaultStrategy |
| encryption.set-fips | TYPED | YES | Sets FIPS mode, writes fipsMode/defaultStrategy |
| encryption.stats | TYPED | YES | Gets stats, writes totalEncryptions/etc |
| encryption.validate-fips | TYPED | YES | Validates FIPS, writes isCompliant/algorithmName/violations |
| encryption.cascade | OVERRIDE | YES | Cascade encryption with multiple strategies, writes result/keys/strategyIds |
| encryption.reencrypt | OVERRIDE | YES | Re-encrypts with new key, writes result/newKey/newStrategyId |
| encryption.generate-key | TYPED | YES | Generates key, writes key/keySizeBits/strategyId |

## 47. UltimateDataFabric (UltimateDataFabricPlugin.cs)

| MESSAGE_TYPE | CLASSIFICATION | WRITES_BACK | NOTES |
|---|---|---|---|
| fabric.strategy.list | TYPED | YES | Uses _response pattern, returns strategy list |
| fabric.execute | OVERRIDE | YES | Uses _response pattern, executes operation via active strategy |

## 48. UltimateIntelligence (UltimateIntelligencePlugin.cs)

| MESSAGE_TYPE | CLASSIFICATION | WRITES_BACK | NOTES |
|---|---|---|---|
| intelligence.ultimate.list | BROKEN | NO | Gets strategies by category but only has comment "Reply mechanism would need to be handled by caller" |
| intelligence.ultimate.select.provider | FIRE-AND-FORGET | N/A | Calls SetActiveAIProvider(), no response needed |
| intelligence.ultimate.select.vector | FIRE-AND-FORGET | N/A | Calls SetActiveVectorStore(), no response needed |
| intelligence.ultimate.select.graph | FIRE-AND-FORGET | N/A | Calls SetActiveKnowledgeGraph(), no response needed |
| intelligence.ultimate.select.feature | FIRE-AND-FORGET | N/A | Calls SetActiveFeature(), no response needed |
| intelligence.ultimate.configure | FIRE-AND-FORGET | N/A | Configures strategy settings, no response needed |
| intelligence.ultimate.stats | BROKEN | NO | Gets strategy statistics but only has comment "Reply mechanism would need to be handled by caller" |
| intelligence.memory.store | BROKEN | NO | Stores memory but "Response would be sent via message bus reply mechanism" |
| intelligence.memory.store.tier | BROKEN | NO | Same as above |
| intelligence.memory.recall | BROKEN | NO | Recalls memories but "Response would be sent via message bus reply mechanism" |
| intelligence.memory.recall.tier | BROKEN | NO | Same as above |
| intelligence.memory.tier.configure | FIRE-AND-FORGET | N/A | Configures tier |
| intelligence.memory.tier.stats | BROKEN | NO | Gets tier stats but never writes back |
| intelligence.memory.tier.enable | FIRE-AND-FORGET | N/A | Enables tier |
| intelligence.memory.tier.disable | FIRE-AND-FORGET | N/A | Disables tier |
| intelligence.memory.consolidate | FIRE-AND-FORGET | N/A | Consolidates memories |
| intelligence.memory.refine | FIRE-AND-FORGET | N/A | Refines context |
| intelligence.memory.evolution.metrics | BROKEN | NO | Gets evolution metrics but never writes back |
| intelligence.memory.regenerate | BROKEN | NO | Regenerates data but never writes back |
| intelligence.memory.regenerate.validate | BROKEN | NO | Validates regeneration but never writes back |
| intelligence.memory.flush | FIRE-AND-FORGET | N/A | Flushes to persistent storage |
| intelligence.memory.restore | FIRE-AND-FORGET | N/A | Restores from persistent storage |
| intelligence.memory.forget | FIRE-AND-FORGET | N/A | Deletes memory |
| intelligence.memory.tier.promote | FIRE-AND-FORGET | N/A | Promotes memory |
| intelligence.memory.tier.demote | FIRE-AND-FORGET | N/A | Demotes memory |
| intelligence.memory.stats | BROKEN | NO | Gets stats but never writes back |

## 49. RaftConsensus (RaftConsensusPlugin.cs)

| MESSAGE_TYPE | CLASSIFICATION | WRITES_BACK | NOTES |
|---|---|---|---|
| raft.propose | OVERRIDE | YES | Uses _response pattern; proposes value to Raft |
| raft.lock.acquire | TYPED | YES | Uses _response pattern |
| raft.lock.release | TYPED | YES | Uses _response pattern |
| raft.lock.status | TYPED | YES | Uses _response pattern |
| raft.cluster.status | TYPED | YES | Uses _response pattern |
| raft.cluster.join | OVERRIDE | YES | Uses _response pattern |
| raft.cluster.leave | TYPED | YES | Uses _response pattern |
| raft.configure | TYPED | YES | Uses _response pattern |
| raft.rpc.request-vote | OVERRIDE | YES | Internal RPC, uses _response pattern |
| raft.rpc.append-entries | OVERRIDE | YES | Internal RPC, uses _response pattern |

## 50. KubernetesCsi (KubernetesCsiPlugin.cs)

| MESSAGE_TYPE | CLASSIFICATION | WRITES_BACK | NOTES |
|---|---|---|---|
| csi.identity.probe | TYPED | YES | Uses _response pattern |
| csi.identity.capabilities | TYPED | YES | Uses _response pattern |
| csi.identity.info | TYPED | YES | Uses _response pattern |
| csi.controller.create_volume | OVERRIDE | YES | Creates volume, uses _response pattern |
| csi.controller.delete_volume | OVERRIDE | YES | Deletes volume, uses _response pattern |
| csi.controller.expand_volume | OVERRIDE | YES | Expands volume, uses _response pattern |
| csi.controller.create_snapshot | OVERRIDE | YES | Creates snapshot, uses _response pattern |
| csi.controller.delete_snapshot | OVERRIDE | YES | Deletes snapshot, uses _response pattern |
| csi.controller.list_volumes | TYPED | YES | Lists volumes, uses _response pattern |
| csi.controller.list_snapshots | TYPED | YES | Lists snapshots, uses _response pattern |
| csi.controller.get_capacity | TYPED | YES | Gets capacity, uses _response pattern |
| csi.controller.validate_capabilities | TYPED | YES | Validates caps, uses _response pattern |
| csi.controller.publish_volume | OVERRIDE | YES | Publishes volume, uses _response pattern |
| csi.controller.unpublish_volume | OVERRIDE | YES | Unpublishes volume, uses _response pattern |
| csi.node.stage_volume | OVERRIDE | YES | Stages volume, uses _response pattern |
| csi.node.unstage_volume | OVERRIDE | YES | Unstages volume, uses _response pattern |
| csi.node.publish_volume | OVERRIDE | YES | Publishes volume, uses _response pattern |
| csi.node.unpublish_volume | OVERRIDE | YES | Unpublishes volume, uses _response pattern |
| csi.node.get_info | TYPED | YES | Gets node info, uses _response pattern |
| csi.node.get_capabilities | TYPED | YES | Gets caps, uses _response pattern |
| csi.node.get_volume_stats | TYPED | YES | Gets volume stats, uses _response pattern |
| csi.node.expand_volume | OVERRIDE | YES | Expands volume, uses _response pattern |
| csi.storageclass.create | TYPED | YES | Creates storage class, uses _response pattern |
| csi.storageclass.list | TYPED | YES | Lists storage classes, uses _response pattern |
| csi.storageclass.get | TYPED | YES | Gets storage class, uses _response pattern |

## 51. DataMarketplace (DataMarketplacePlugin.cs)

| MESSAGE_TYPE | CLASSIFICATION | WRITES_BACK | NOTES |
|---|---|---|---|
| marketplace.list | TYPED | YES | Lists marketplace, writes Payload["result"] |
| marketplace.search | TYPED | YES | Searches, writes Payload["result"] |
| marketplace.subscribe | TYPED | YES | Subscribes to data product, writes Payload["result"] |
| marketplace.access | TYPED | YES | Accesses data, writes Payload["result"] |
| marketplace.preview | TYPED | YES | Previews data, writes Payload["result"] |
| marketplace.meter | TYPED | YES | Meters usage, writes Payload["result"] |
| marketplace.invoice | TYPED | YES | Generates invoice, writes Payload["result"] |
| marketplace.review | TYPED | YES | Reviews product, writes Payload["result"] |
| marketplace.revoke | TYPED | YES | Revokes access, writes Payload["result"] |
| marketplace.chargeback | TYPED | YES | Processes chargeback, writes Payload["result"] |
| marketplace.contract (sub: create) | OVERRIDE | YES | Creates smart contract, writes Payload["result"] |
| marketplace.contract (sub: deploy) | OVERRIDE | YES | Deploys contract, writes Payload["result"] |
| marketplace.contract (sub: sign) | OVERRIDE | YES | Signs contract, writes Payload["result"] |
| marketplace.contract (sub: verify) | OVERRIDE | YES | Verifies contract, writes Payload["result"] |

---

## SDK Base Classes

## 52. HybridStoragePluginBase (SDK)

| MESSAGE_TYPE | CLASSIFICATION | WRITES_BACK | NOTES |
|---|---|---|---|
| storage.instance.register | FIRE-AND-FORGET | NO | Registers storage instance, no response written |
| storage.instance.unregister | FIRE-AND-FORGET | NO | Unregisters instance, no response written |
| storage.instance.update-roles | FIRE-AND-FORGET | NO | Updates roles, no response written |
| storage.instance.update-priority | FIRE-AND-FORGET | NO | Updates priority, no response written |
| storage.instance.list | BROKEN | NO | Computes instance list but "Response would be sent via message context" |
| storage.instance.health | FIRE-AND-FORGET | NO | Runs health check, no response written |
| storage.health | BROKEN | NO | Gets aggregate health but "Response would be sent via message context" |

## 53. HybridDatabasePluginBase (SDK)

| MESSAGE_TYPE | CLASSIFICATION | WRITES_BACK | NOTES |
|---|---|---|---|
| storage.{Scheme}.save | TYPED | YES (via return) | Returns MessageResponse but response var is NEVER assigned to Payload - **BROKEN** |
| storage.{Scheme}.load | TYPED | YES (via return) | Same pattern - returns MessageResponse but never assigned |
| storage.{Scheme}.delete | TYPED | YES (via return) | Same |
| storage.{Scheme}.exists | TYPED | YES (via return) | Same |
| storage.{Scheme}.query | TYPED | YES (via return) | Same |
| storage.{Scheme}.count | TYPED | YES (via return) | Same |
| storage.{Scheme}.connect | TYPED | YES (via return) | Same |
| storage.{Scheme}.disconnect | TYPED | YES (via return) | Same |
| storage.{Scheme}.createdb | TYPED | YES (via return) | Same |
| storage.{Scheme}.dropdb | TYPED | YES (via return) | Same |
| storage.{Scheme}.createcollection | TYPED | YES (via return) | Same |
| storage.{Scheme}.dropcollection | TYPED | YES (via return) | Same |
| storage.{Scheme}.listdatabases | TYPED | YES (via return) | Same |
| storage.{Scheme}.listcollections | TYPED | YES (via return) | Same |
| storage.{Scheme}.register | TYPED | YES (via return) | Same |
| storage.{Scheme}.unregister | TYPED | YES (via return) | Same |
| storage.{Scheme}.instances | TYPED | YES (via return) | Same |
| storage.{Scheme}.cache.stats | TYPED | YES (via return) | Same |
| storage.{Scheme}.cache.invalidate | TYPED | YES (via return) | Same |
| storage.{Scheme}.cache.cleanup | TYPED | YES (via return) | Same |
| storage.{Scheme}.index.rebuild | TYPED | YES (via return) | Same |
| storage.{Scheme}.index.stats | TYPED | YES (via return) | Same |
| storage.{Scheme}.index.search | TYPED | YES (via return) | Same |

**CRITICAL BUG**: `var response = message.Type switch { ... }; ` computes MessageResponse but the method ends without assigning response to `message.Payload`. All 23 handlers produce results that are silently discarded.

---

## Additional Handler Files (MessageBus.Subscribe)

## 54. TamperProof/Services/MessageBusIntegration.cs

File exists but contains **no Subscribe calls or message handlers**. Empty integration point.

## 55. UltimateIntelligence/KernelKnowledgeIntegration.cs

| MESSAGE_TYPE (topic) | CLASSIFICATION | WRITES_BACK | NOTES |
|---|---|---|---|
| knowledge.query | OVERRIDE | VIA PUBLISH | Queries knowledge aggregator, sends response via PublishAsync to source plugin |
| knowledge.command | OVERRIDE | VIA PUBLISH | Executes commands (Refresh/Invalidate/Subscribe/Unsubscribe), sends result via PublishAsync |
| knowledge.registration | OVERRIDE | VIA PUBLISH | Handles knowledge registration, sends via PublishAsync |

## 56. UltimateIntelligence/IntelligenceDiscoveryHandler.cs

| MESSAGE_TYPE (topic) | CLASSIFICATION | WRITES_BACK | NOTES |
|---|---|---|---|
| intelligence.discover | TYPED | VIA PUBLISH | Handles discovery requests, broadcasts capability response via PublishAsync |
| intelligence.query-capability | TYPED | VIA PUBLISH | Handles capability queries, publishes filtered response via PublishAsync |

## 57. UltimateIntelligence/KnowledgeSystem.cs

| MESSAGE_TYPE (topic) | CLASSIFICATION | WRITES_BACK | NOTES |
|---|---|---|---|
| plugin.state.update | FIRE-AND-FORGET | N/A | Updates internal plugin state tracking |
| connection.state.update | FIRE-AND-FORGET | N/A | Updates connection state (no explicit handler body) |
| operation.state.update | FIRE-AND-FORGET | N/A | Updates operation state (no explicit handler body) |
| metric.update | FIRE-AND-FORGET | N/A | Updates internal metric tracking |

## 58. AppPlatform (AppPlatformPlugin.cs)

All handlers use `Task<MessageResponse>` return type via `MessageBus.Subscribe()`:

| MESSAGE_TYPE (topic) | CLASSIFICATION | WRITES_BACK | NOTES |
|---|---|---|---|
| platform.register | TYPED | VIA RETURN | Returns MessageResponse.Ok(registration) |
| platform.deregister | TYPED | VIA RETURN | Returns MessageResponse.Ok/Error |
| platform.get | TYPED | VIA RETURN | Returns MessageResponse.Ok(app) |
| platform.list | TYPED | VIA RETURN | Returns MessageResponse.Ok(apps) |
| platform.update | TYPED | VIA RETURN | Returns MessageResponse.Ok(app) |
| platform.token.create | TYPED | VIA RETURN | Returns MessageResponse.Ok with RawKey/Token |
| platform.token.rotate | OVERRIDE | VIA RETURN | Complex rotation with validation |
| platform.token.revoke | TYPED | VIA RETURN | Returns MessageResponse.Ok/Error |
| platform.token.validate | TYPED | VIA RETURN | Returns MessageResponse.Ok(result) |
| platform.service.storage | OVERRIDE | VIA RETURN | Routes to storage service |
| platform.service.accesscontrol | OVERRIDE | VIA RETURN | Routes to access control |
| platform.service.intelligence | OVERRIDE | VIA RETURN | Routes to intelligence |
| platform.service.observability | OVERRIDE | VIA RETURN | Routes to observability |
| platform.service.replication | OVERRIDE | VIA RETURN | Routes to replication |
| platform.service.compliance | OVERRIDE | VIA RETURN | Routes to compliance |
| platform.policy.bind | OVERRIDE | VIA RETURN | Binds access policy |
| platform.policy.unbind | TYPED | VIA RETURN | Unbinds policy |
| platform.policy.get | TYPED | VIA RETURN | Gets policy |
| platform.policy.evaluate | OVERRIDE | VIA RETURN | Evaluates policy decision |
| platform.ai.configure | OVERRIDE | VIA RETURN | Configures AI workflow |
| platform.ai.remove | TYPED | VIA RETURN | Removes AI config |
| platform.ai.get | TYPED | VIA RETURN | Gets AI config |
| platform.ai.update | OVERRIDE | VIA RETURN | Updates AI config |
| platform.ai.request | OVERRIDE | VIA RETURN | Routes AI request with budget enforcement |
| platform.ai.usage | TYPED | VIA RETURN | Gets AI usage stats |
| platform.ai.usage.reset | TYPED | VIA RETURN | Resets usage counters |
| platform.observability.configure | OVERRIDE | VIA RETURN | Configures observability |
| platform.observability.remove | TYPED | VIA RETURN | Removes config |
| platform.observability.get | TYPED | VIA RETURN | Gets config |
| platform.observability.update | OVERRIDE | VIA RETURN | Updates config |
| platform.observability.emit.metric | TYPED | VIA RETURN | Emits metric |
| platform.observability.emit.trace | TYPED | VIA RETURN | Emits trace |
| platform.observability.emit.log | TYPED | VIA RETURN | Emits log |
| platform.observability.query.metrics | TYPED | VIA RETURN | Queries metrics |
| platform.observability.query.traces | TYPED | VIA RETURN | Queries traces |
| platform.observability.query.logs | TYPED | VIA RETURN | Queries logs |
| platform.services.list | TYPED | VIA RETURN | Lists platform services |
| platform.services.get | TYPED | VIA RETURN | Gets service info |
| platform.services.forapp | TYPED | VIA RETURN | Gets services for app |
| platform.services.health | TYPED | VIA RETURN | Checks service health |

## 59. SelfEmulatingObjects (SelfEmulatingObjectsPlugin.cs)

| MESSAGE_TYPE (topic) | CLASSIFICATION | WRITES_BACK | NOTES |
|---|---|---|---|
| selfemulating.bundle | OVERRIDE | VIA PUBLISH | Bundles data with viewer, publishes result to "selfemulating.bundled" topic |
| selfemulating.view | OVERRIDE | VIA PUBLISH | Executes viewer, publishes result to "selfemulating.viewed" topic |

---

## Summary Statistics

| Classification | Count | Percentage |
|---|---|---|
| TYPED | ~240 | ~65% |
| OVERRIDE | ~55 | ~15% |
| FIRE-AND-FORGET | ~35 | ~9% |
| BROKEN | ~30 | ~8% |
| STUB | ~10 | ~3% |

## BROKEN Handlers (Critical - Must Fix)

These handlers compute results but never write them back:

1. **HybridDatabasePluginBase** (SDK): ALL 23 handlers - `var response = switch { ... }` result never stored in Payload
2. **UltimateDatabaseStorage**: 5 handlers (list, get, stats, retrieve, query, health) - `// Response would be sent via message context`
3. **HybridStoragePluginBase** (SDK): 2 handlers (instance.list, storage.health) - `// Response would be sent via message context`
4. **UltimateIntelligence**: 8 memory handlers (store, recall, tier.stats, evolution.metrics, regenerate, validate, stats) + 2 main handlers (list, stats)
5. **FuseDriver**: 1 handler (get-status) - empty case body

## STUB Handlers (Must Implement)

1. **TamperProof**: OnMessageAsync is effectively a no-op
2. **UltimateDataProtection**: All 5 OnMessageAsync handlers are `return Task.CompletedTask;`
3. **UltimateSustainability**: 2 handlers (list, recommendations) are empty `break;`
4. **UniversalObservability**: 1 handler (list) is empty `break;`
5. **UltimateCompression**: 1 handler (list) is empty `break;`
6. **UltimateKeyManagement**: configure handler is `return Task.CompletedTask;`

## Response Pattern Distribution

| Pattern | Plugins Using It |
|---|---|
| Direct Payload Write (`message.Payload["key"] = value`) | ~35 plugins |
| _response Pattern (`message.Payload["_response"] = dict`) | RaftConsensus, WinFspDriver, KubernetesCsi, UltimateSDKPorts, UltimateWorkflow, UltimateReplication, UltimateDataFabric, UltimateDocGen |
| MessageResponse Return (via Subscribe callback) | AppPlatform (40 handlers) |
| PublishAsync to separate topic | SelfEmulatingObjects, IntelligenceDiscoveryHandler, KernelKnowledgeIntegration |
| No response at all | BROKEN/STUB handlers above |
