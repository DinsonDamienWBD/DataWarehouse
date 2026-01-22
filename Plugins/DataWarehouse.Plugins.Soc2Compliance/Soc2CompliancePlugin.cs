using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.Soc2Compliance
{
    /// <summary>
    /// Production-ready SOC 2 Type II compliance plugin for DataWarehouse.
    /// Provides comprehensive controls implementation for all Trust Services Criteria.
    ///
    /// <para>
    /// <b>Features:</b>
    /// <list type="bullet">
    ///   <item>Full Trust Services Criteria coverage (Security, Availability, Processing Integrity, Confidentiality, Privacy)</item>
    ///   <item>Control categories CC1-CC9, A1, PI1, C1, P1-P8</item>
    ///   <item>Evidence collection automation</item>
    ///   <item>Control effectiveness testing</item>
    ///   <item>Continuous monitoring with real-time alerts</item>
    ///   <item>Audit readiness assessment</item>
    ///   <item>Compliance scoring and gap analysis</item>
    ///   <item>Audit report generation (Type I and Type II)</item>
    ///   <item>Control mapping to specific requirements</item>
    ///   <item>Finding and exception management</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// <b>Message Commands:</b>
    /// <list type="bullet">
    ///   <item>soc2.control.assess - Assess a specific control</item>
    ///   <item>soc2.evidence.collect - Collect evidence for a control</item>
    ///   <item>soc2.monitoring.check - Run continuous monitoring check</item>
    ///   <item>soc2.audit.generate - Generate audit report</item>
    ///   <item>soc2.readiness.assess - Assess audit readiness</item>
    ///   <item>soc2.finding.record - Record an audit finding</item>
    ///   <item>soc2.score.calculate - Calculate compliance score</item>
    ///   <item>soc2.gap.analyze - Perform gap analysis</item>
    /// </list>
    /// </para>
    /// </summary>
    public sealed class Soc2CompliancePlugin : ComplianceProviderPluginBase
    {
        private readonly ConcurrentDictionary<string, Soc2Control> _controls;
        private readonly ConcurrentDictionary<string, ControlEvidence> _evidence;
        private readonly ConcurrentDictionary<string, AuditFinding> _findings;
        private readonly ConcurrentDictionary<string, ControlAssessmentResult> _assessments;
        private readonly ConcurrentDictionary<string, MonitoringAlert> _alerts;
        private readonly ConcurrentDictionary<string, ControlException> _exceptions;
        private readonly List<TrustServicesCriteria> _trustServicesCriteria;
        private readonly SemaphoreSlim _persistLock = new(1, 1);
        private readonly Soc2Config _config;
        private readonly string _storagePath;
        private Timer? _monitoringTimer;
        private DateTime _lastMonitoringRun = DateTime.MinValue;

        /// <inheritdoc/>
        public override string Id => "com.datawarehouse.compliance.soc2";

        /// <inheritdoc/>
        public override string Name => "SOC 2 Compliance Plugin";

        /// <inheritdoc/>
        public override string Version => "1.0.0";

        /// <inheritdoc/>
        public override PluginCategory Category => PluginCategory.GovernanceProvider;

        /// <inheritdoc/>
        public override IReadOnlyList<string> SupportedFrameworks => new[] { "SOC2", "SOC2-Type-I", "SOC2-Type-II", "AICPA-TSC" };

        /// <summary>
        /// Initializes a new instance of the Soc2CompliancePlugin.
        /// </summary>
        /// <param name="config">SOC 2 configuration options.</param>
        public Soc2CompliancePlugin(Soc2Config? config = null)
        {
            _config = config ?? new Soc2Config();
            _controls = new ConcurrentDictionary<string, Soc2Control>();
            _evidence = new ConcurrentDictionary<string, ControlEvidence>();
            _findings = new ConcurrentDictionary<string, AuditFinding>();
            _assessments = new ConcurrentDictionary<string, ControlAssessmentResult>();
            _alerts = new ConcurrentDictionary<string, MonitoringAlert>();
            _exceptions = new ConcurrentDictionary<string, ControlException>();
            _trustServicesCriteria = InitializeTrustServicesCriteria();
            _storagePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DataWarehouse", "compliance", "soc2");

            InitializeControls();
        }

        /// <inheritdoc/>
        public override async Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
        {
            var response = await base.OnHandshakeAsync(request);
            await LoadDataAsync();

            // Start continuous monitoring if enabled
            if (_config.EnableContinuousMonitoring)
            {
                _monitoringTimer = new Timer(
                    async _ => await RunContinuousMonitoringAsync(),
                    null,
                    TimeSpan.FromMinutes(1),
                    TimeSpan.FromMinutes(_config.MonitoringIntervalMinutes));
            }

            return response;
        }

        /// <inheritdoc/>
        public override Task StartAsync(CancellationToken ct)
        {
            return Task.CompletedTask;
        }

        /// <inheritdoc/>
        public override async Task StopAsync()
        {
            if (_monitoringTimer != null)
            {
                await _monitoringTimer.DisposeAsync();
                _monitoringTimer = null;
            }
        }

        /// <inheritdoc/>
        protected override List<PluginCapabilityDescriptor> GetCapabilities()
        {
            return new List<PluginCapabilityDescriptor>
            {
                new() { Name = "soc2.control.assess", DisplayName = "Assess Control", Description = "Assess a specific SOC 2 control" },
                new() { Name = "soc2.evidence.collect", DisplayName = "Collect Evidence", Description = "Collect evidence for a control" },
                new() { Name = "soc2.monitoring.check", DisplayName = "Monitoring Check", Description = "Run continuous monitoring check" },
                new() { Name = "soc2.audit.generate", DisplayName = "Generate Audit", Description = "Generate SOC 2 audit report" },
                new() { Name = "soc2.readiness.assess", DisplayName = "Readiness Assessment", Description = "Assess audit readiness" },
                new() { Name = "soc2.finding.record", DisplayName = "Record Finding", Description = "Record an audit finding" },
                new() { Name = "soc2.score.calculate", DisplayName = "Calculate Score", Description = "Calculate compliance score" },
                new() { Name = "soc2.gap.analyze", DisplayName = "Gap Analysis", Description = "Perform gap analysis" }
            };
        }

        /// <inheritdoc/>
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["TotalControls"] = _controls.Count;
            metadata["AssessedControls"] = _assessments.Count;
            metadata["TotalEvidence"] = _evidence.Count;
            metadata["OpenFindings"] = _findings.Values.Count(f => f.Status == FindingStatus.Open);
            metadata["ActiveAlerts"] = _alerts.Values.Count(a => !a.Resolved);
            metadata["SupportsTrustServicesCriteria"] = true;
            metadata["SupportsContinuousMonitoring"] = true;
            metadata["SupportsEvidenceCollection"] = true;
            metadata["SupportsAuditReporting"] = true;
            metadata["ControlCategories"] = new[] { "CC1-CC9", "A1", "PI1", "C1", "P1-P8" };
            return metadata;
        }

        /// <inheritdoc/>
        public override async Task OnMessageAsync(PluginMessage message)
        {
            switch (message.Type)
            {
                case "soc2.control.assess":
                    await HandleAssessControlAsync(message);
                    break;
                case "soc2.evidence.collect":
                    await HandleCollectEvidenceAsync(message);
                    break;
                case "soc2.monitoring.check":
                    await HandleMonitoringCheckAsync(message);
                    break;
                case "soc2.audit.generate":
                    await HandleGenerateAuditAsync(message);
                    break;
                case "soc2.readiness.assess":
                    await HandleReadinessAssessAsync(message);
                    break;
                case "soc2.finding.record":
                    await HandleRecordFindingAsync(message);
                    break;
                case "soc2.score.calculate":
                    HandleCalculateScore(message);
                    break;
                case "soc2.gap.analyze":
                    HandleGapAnalysis(message);
                    break;
                default:
                    await base.OnMessageAsync(message);
                    break;
            }
        }

        #region IComplianceProvider Implementation

        /// <inheritdoc/>
        public override async Task<ComplianceValidationResult> ValidateAsync(
            string framework,
            object data,
            Dictionary<string, object>? context = null,
            CancellationToken ct = default)
        {
            var violations = new List<ComplianceViolation>();
            var warnings = new List<ComplianceWarning>();

            // Validate against applicable controls
            foreach (var control in _controls.Values.Where(c => c.IsEnabled))
            {
                var result = await ValidateControlAsync(control, data, context, ct);
                if (!result.IsCompliant)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = $"SOC2-{control.ControlId}",
                        Message = result.Message ?? $"Control {control.ControlId} validation failed",
                        Severity = MapControlSeverity(control.Priority),
                        Remediation = control.RemediationGuidance
                    });
                }
                else if (result.HasWarnings)
                {
                    warnings.AddRange(result.Warnings.Select(w => new ComplianceWarning
                    {
                        Code = $"SOC2-{control.ControlId}-WARN",
                        Message = w,
                        Recommendation = control.RemediationGuidance
                    }));
                }
            }

            return new ComplianceValidationResult
            {
                IsCompliant = violations.Count == 0,
                Framework = framework,
                Violations = violations,
                Warnings = warnings,
                ValidatedAt = DateTime.UtcNow
            };
        }

        /// <inheritdoc/>
        public override async Task<ComplianceStatus> GetStatusAsync(string framework, CancellationToken ct = default)
        {
            var score = CalculateComplianceScore();
            var assessedControls = _assessments.Values.ToList();
            var passingControls = assessedControls.Count(a => a.Result == AssessmentOutcome.Pass);
            var failingControls = assessedControls.Count(a => a.Result == AssessmentOutcome.Fail);

            return new ComplianceStatus
            {
                Framework = framework,
                IsCompliant = score.OverallScore >= _config.ComplianceThreshold,
                ComplianceScore = score.OverallScore,
                TotalControls = _controls.Count,
                PassingControls = passingControls,
                FailingControls = failingControls,
                LastAssessment = assessedControls.Any() ? assessedControls.Max(a => a.AssessedAt) : DateTime.MinValue,
                NextAssessmentDue = GetNextAssessmentDueDate()
            };
        }

        /// <inheritdoc/>
        public override async Task<ComplianceReport> GenerateReportAsync(
            string framework,
            DateTime? startDate = null,
            DateTime? endDate = null,
            CancellationToken ct = default)
        {
            var reportStart = startDate ?? DateTime.UtcNow.AddYears(-1);
            var reportEnd = endDate ?? DateTime.UtcNow;
            var auditReport = await GenerateAuditReportAsync(framework.Contains("Type-I") ? AuditType.TypeI : AuditType.TypeII, reportStart, reportEnd, ct);

            var controlAssessments = _controls.Values.Select(c =>
            {
                _assessments.TryGetValue(c.ControlId, out var assessment);
                return new ControlAssessment
                {
                    ControlId = c.ControlId,
                    ControlName = c.Name,
                    IsPassing = assessment?.Result == AssessmentOutcome.Pass,
                    Evidence = GetEvidenceForControl(c.ControlId).FirstOrDefault()?.Description,
                    Notes = assessment?.Notes
                };
            }).ToList();

            var status = await GetStatusAsync(framework, ct);

            return new ComplianceReport
            {
                Framework = framework,
                GeneratedAt = DateTime.UtcNow,
                ReportingPeriodStart = reportStart,
                ReportingPeriodEnd = reportEnd,
                Status = status,
                ControlAssessments = controlAssessments,
                ReportDocument = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(auditReport, new JsonSerializerOptions { WriteIndented = true })),
                ReportFormat = "application/json"
            };
        }

        /// <inheritdoc/>
        public override async Task<string> RegisterDataSubjectRequestAsync(
            DataSubjectRequest request,
            CancellationToken ct = default)
        {
            // SOC 2 Privacy criteria requires handling of data subject requests
            var requestId = Guid.NewGuid().ToString("N");

            // Record as evidence for P5-P8 controls
            await CollectEvidenceAsync(new EvidenceCollectionRequest
            {
                ControlId = "P5.1",
                Description = $"Data subject request ({request.RequestType}) processed",
                Source = "DataSubjectRequest",
                CollectedBy = "System",
                Metadata = new Dictionary<string, object>
                {
                    ["requestId"] = requestId,
                    ["requestType"] = request.RequestType,
                    ["subjectId"] = request.SubjectId
                }
            }, ct);

            await SaveDataAsync();
            return requestId;
        }

        /// <inheritdoc/>
        public override Task<RetentionPolicy> GetRetentionPolicyAsync(
            string dataType,
            string? framework = null,
            CancellationToken ct = default)
        {
            // Default SOC 2 retention policy
            var retention = _config.DefaultRetentionDays;
            var policy = new RetentionPolicy
            {
                DataType = dataType,
                RetentionPeriod = TimeSpan.FromDays(retention),
                LegalBasis = "SOC 2 Type II compliance requirement",
                RequiresSecureDeletion = true,
                ApplicableFrameworks = new List<string> { "SOC2", "SOC2-Type-II" }
            };

            return Task.FromResult(policy);
        }

        #endregion

        #region Core SOC 2 Methods

        /// <summary>
        /// Assesses a specific SOC 2 control.
        /// </summary>
        /// <param name="controlId">The control identifier.</param>
        /// <param name="assessedBy">The assessor identifier.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The assessment result.</returns>
        public async Task<ControlAssessmentResult> AssessControlAsync(
            string controlId,
            string assessedBy,
            CancellationToken ct = default)
        {
            if (!_controls.TryGetValue(controlId, out var control))
            {
                throw new ArgumentException($"Control {controlId} not found", nameof(controlId));
            }

            var evidence = GetEvidenceForControl(controlId);
            var result = new ControlAssessmentResult
            {
                AssessmentId = Guid.NewGuid().ToString("N"),
                ControlId = controlId,
                AssessedAt = DateTime.UtcNow,
                AssessedBy = assessedBy,
                EvidenceCount = evidence.Count,
                Result = DetermineAssessmentOutcome(control, evidence),
                EffectivenessScore = CalculateControlEffectiveness(control, evidence)
            };

            if (result.Result == AssessmentOutcome.Fail)
            {
                result.Notes = $"Control requires attention. Found {evidence.Count} evidence items, effectiveness score: {result.EffectivenessScore:P0}";
            }

            _assessments[controlId] = result;
            await SaveDataAsync();

            return result;
        }

        /// <summary>
        /// Collects evidence for a control.
        /// </summary>
        /// <param name="request">Evidence collection request.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The collected evidence.</returns>
        public async Task<ControlEvidence> CollectEvidenceAsync(
            EvidenceCollectionRequest request,
            CancellationToken ct = default)
        {
            if (!_controls.ContainsKey(request.ControlId))
            {
                throw new ArgumentException($"Control {request.ControlId} not found", nameof(request));
            }

            var evidenceId = Guid.NewGuid().ToString("N");
            var evidence = new ControlEvidence
            {
                EvidenceId = evidenceId,
                ControlId = request.ControlId,
                Description = request.Description,
                Source = request.Source,
                CollectedAt = DateTime.UtcNow,
                CollectedBy = request.CollectedBy,
                Type = request.Type,
                Metadata = request.Metadata ?? new Dictionary<string, object>(),
                Hash = ComputeEvidenceHash(request),
                RetentionUntil = DateTime.UtcNow.AddDays(_config.EvidenceRetentionDays)
            };

            _evidence[evidenceId] = evidence;
            await SaveDataAsync();

            return evidence;
        }

        /// <summary>
        /// Runs continuous monitoring checks.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Monitoring results.</returns>
        public async Task<ContinuousMonitoringResult> RunContinuousMonitoringAsync(CancellationToken ct = default)
        {
            _lastMonitoringRun = DateTime.UtcNow;
            var results = new List<MonitoringCheckResult>();
            var newAlerts = new List<MonitoringAlert>();

            foreach (var control in _controls.Values.Where(c => c.IsEnabled && c.MonitoringEnabled))
            {
                var checkResult = await RunControlMonitoringCheckAsync(control, ct);
                results.Add(checkResult);

                if (checkResult.Status == MonitoringStatus.Alert)
                {
                    var alert = new MonitoringAlert
                    {
                        AlertId = Guid.NewGuid().ToString("N"),
                        ControlId = control.ControlId,
                        Severity = checkResult.Severity,
                        Message = checkResult.Message ?? $"Monitoring alert for control {control.ControlId}",
                        DetectedAt = DateTime.UtcNow,
                        Resolved = false
                    };
                    _alerts[alert.AlertId] = alert;
                    newAlerts.Add(alert);
                }
            }

            await SaveDataAsync();

            return new ContinuousMonitoringResult
            {
                RunAt = _lastMonitoringRun,
                TotalChecks = results.Count,
                PassedChecks = results.Count(r => r.Status == MonitoringStatus.Healthy),
                FailedChecks = results.Count(r => r.Status == MonitoringStatus.Alert),
                WarningChecks = results.Count(r => r.Status == MonitoringStatus.Warning),
                NewAlerts = newAlerts,
                CheckResults = results
            };
        }

        /// <summary>
        /// Generates an audit report.
        /// </summary>
        /// <param name="auditType">Type of audit (Type I or Type II).</param>
        /// <param name="periodStart">Audit period start date.</param>
        /// <param name="periodEnd">Audit period end date.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The generated audit report.</returns>
        public async Task<Soc2AuditReport> GenerateAuditReportAsync(
            AuditType auditType,
            DateTime periodStart,
            DateTime periodEnd,
            CancellationToken ct = default)
        {
            var score = CalculateComplianceScore();
            var findings = _findings.Values.Where(f =>
                f.IdentifiedAt >= periodStart && f.IdentifiedAt <= periodEnd).ToList();

            var criteriaResults = new List<TrustServicesCriteriaResult>();
            foreach (var criteria in _trustServicesCriteria)
            {
                var controlsForCriteria = _controls.Values
                    .Where(c => c.TrustServicesCriteria == criteria.Id)
                    .ToList();

                var assessments = controlsForCriteria
                    .Select(c => _assessments.TryGetValue(c.ControlId, out var a) ? a : null)
                    .Where(a => a != null)
                    .ToList();

                criteriaResults.Add(new TrustServicesCriteriaResult
                {
                    CriteriaId = criteria.Id,
                    CriteriaName = criteria.Name,
                    Category = criteria.Category,
                    TotalControls = controlsForCriteria.Count,
                    PassingControls = assessments.Count(a => a!.Result == AssessmentOutcome.Pass),
                    FailingControls = assessments.Count(a => a!.Result == AssessmentOutcome.Fail),
                    Score = controlsForCriteria.Count > 0
                        ? (double)assessments.Count(a => a!.Result == AssessmentOutcome.Pass) / controlsForCriteria.Count
                        : 0
                });
            }

            return new Soc2AuditReport
            {
                ReportId = Guid.NewGuid().ToString("N"),
                AuditType = auditType,
                GeneratedAt = DateTime.UtcNow,
                PeriodStart = periodStart,
                PeriodEnd = periodEnd,
                OrganizationName = _config.OrganizationName,
                OverallScore = score.OverallScore,
                IsCompliant = score.OverallScore >= _config.ComplianceThreshold,
                TotalControls = _controls.Count,
                PassingControls = _assessments.Values.Count(a => a.Result == AssessmentOutcome.Pass),
                FailingControls = _assessments.Values.Count(a => a.Result == AssessmentOutcome.Fail),
                CriteriaResults = criteriaResults,
                Findings = findings,
                Exceptions = _exceptions.Values.Where(e =>
                    e.EffectiveFrom <= periodEnd && (!e.EffectiveTo.HasValue || e.EffectiveTo >= periodStart)).ToList(),
                EvidenceCount = _evidence.Values.Count(e =>
                    e.CollectedAt >= periodStart && e.CollectedAt <= periodEnd),
                ScoresByCategory = score.CategoryScores
            };
        }

        /// <summary>
        /// Assesses audit readiness.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Audit readiness assessment.</returns>
        public async Task<AuditReadinessAssessment> AssessAuditReadinessAsync(CancellationToken ct = default)
        {
            var gaps = new List<ReadinessGap>();
            var recommendations = new List<string>();

            // Check evidence coverage
            foreach (var control in _controls.Values.Where(c => c.IsEnabled))
            {
                var evidence = GetEvidenceForControl(control.ControlId);
                if (evidence.Count < control.MinimumEvidenceRequired)
                {
                    gaps.Add(new ReadinessGap
                    {
                        ControlId = control.ControlId,
                        GapType = GapType.InsufficientEvidence,
                        Description = $"Control {control.ControlId} has {evidence.Count} evidence items, requires at least {control.MinimumEvidenceRequired}",
                        Severity = control.Priority,
                        Recommendation = $"Collect additional evidence for control {control.ControlId}: {control.Name}"
                    });
                }

                // Check assessment currency
                if (!_assessments.TryGetValue(control.ControlId, out var assessment) ||
                    assessment.AssessedAt < DateTime.UtcNow.AddDays(-_config.AssessmentValidityDays))
                {
                    gaps.Add(new ReadinessGap
                    {
                        ControlId = control.ControlId,
                        GapType = GapType.StaleAssessment,
                        Description = $"Control {control.ControlId} assessment is stale or missing",
                        Severity = ControlPriority.Medium,
                        Recommendation = $"Perform current assessment for control {control.ControlId}"
                    });
                }
            }

            // Check for unresolved findings
            var unresolvedFindings = _findings.Values.Where(f => f.Status == FindingStatus.Open).ToList();
            if (unresolvedFindings.Count > 0)
            {
                recommendations.Add($"Resolve {unresolvedFindings.Count} open findings before audit");
            }

            // Calculate readiness score
            var totalControls = _controls.Values.Count(c => c.IsEnabled);
            var readyControls = totalControls - gaps.Select(g => g.ControlId).Distinct().Count();
            var readinessScore = totalControls > 0 ? (double)readyControls / totalControls : 0;

            return new AuditReadinessAssessment
            {
                AssessedAt = DateTime.UtcNow,
                ReadinessScore = readinessScore,
                IsReady = readinessScore >= _config.ReadinessThreshold && unresolvedFindings.Count == 0,
                TotalControls = totalControls,
                ReadyControls = readyControls,
                Gaps = gaps,
                OpenFindings = unresolvedFindings.Count,
                Recommendations = recommendations,
                EstimatedRemediationEffort = EstimateRemediationEffort(gaps)
            };
        }

        /// <summary>
        /// Records an audit finding.
        /// </summary>
        /// <param name="request">Finding details.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The recorded finding.</returns>
        public async Task<AuditFinding> RecordFindingAsync(
            FindingRequest request,
            CancellationToken ct = default)
        {
            var findingId = Guid.NewGuid().ToString("N");
            var finding = new AuditFinding
            {
                FindingId = findingId,
                ControlId = request.ControlId,
                Title = request.Title,
                Description = request.Description,
                Type = request.Type,
                Severity = request.Severity,
                IdentifiedAt = DateTime.UtcNow,
                IdentifiedBy = request.IdentifiedBy,
                Status = FindingStatus.Open,
                RemediationPlan = request.RemediationPlan,
                DueDate = request.DueDate ?? DateTime.UtcNow.AddDays(GetDefaultRemediationDays(request.Severity))
            };

            _findings[findingId] = finding;
            await SaveDataAsync();

            return finding;
        }

        /// <summary>
        /// Calculates the overall compliance score.
        /// </summary>
        /// <returns>Compliance score breakdown.</returns>
        public ComplianceScoreResult CalculateComplianceScore()
        {
            var categoryScores = new Dictionary<string, double>();
            var categoryWeights = new Dictionary<string, double>
            {
                ["Security"] = 0.40,
                ["Availability"] = 0.15,
                ["ProcessingIntegrity"] = 0.15,
                ["Confidentiality"] = 0.15,
                ["Privacy"] = 0.15
            };

            foreach (var category in categoryWeights.Keys)
            {
                var controlsInCategory = _controls.Values
                    .Where(c => c.IsEnabled && GetCategoryForCriteria(c.TrustServicesCriteria) == category)
                    .ToList();

                if (controlsInCategory.Count == 0)
                {
                    categoryScores[category] = 1.0;
                    continue;
                }

                var passingControls = controlsInCategory
                    .Count(c => _assessments.TryGetValue(c.ControlId, out var a) && a.Result == AssessmentOutcome.Pass);

                categoryScores[category] = (double)passingControls / controlsInCategory.Count;
            }

            var overallScore = categoryWeights.Sum(kv => kv.Value * categoryScores.GetValueOrDefault(kv.Key, 0));

            return new ComplianceScoreResult
            {
                OverallScore = overallScore,
                CategoryScores = categoryScores,
                CalculatedAt = DateTime.UtcNow,
                IsCompliant = overallScore >= _config.ComplianceThreshold,
                ComplianceThreshold = _config.ComplianceThreshold
            };
        }

        /// <summary>
        /// Performs a gap analysis against SOC 2 requirements.
        /// </summary>
        /// <returns>Gap analysis results.</returns>
        public GapAnalysisResult PerformGapAnalysis()
        {
            var gaps = new List<ComplianceGap>();

            foreach (var control in _controls.Values.Where(c => c.IsEnabled))
            {
                var issues = new List<string>();
                var severity = GapSeverity.Low;

                // Check evidence
                var evidence = GetEvidenceForControl(control.ControlId);
                if (evidence.Count < control.MinimumEvidenceRequired)
                {
                    issues.Add($"Insufficient evidence: {evidence.Count}/{control.MinimumEvidenceRequired}");
                    severity = GapSeverity.Medium;
                }

                // Check assessment
                if (!_assessments.TryGetValue(control.ControlId, out var assessment))
                {
                    issues.Add("No assessment performed");
                    severity = GapSeverity.High;
                }
                else if (assessment.Result == AssessmentOutcome.Fail)
                {
                    issues.Add($"Assessment failed: {assessment.Notes}");
                    severity = GapSeverity.Critical;
                }

                // Check for related findings
                var relatedFindings = _findings.Values.Where(f =>
                    f.ControlId == control.ControlId && f.Status == FindingStatus.Open).ToList();
                if (relatedFindings.Any())
                {
                    issues.Add($"Open findings: {relatedFindings.Count}");
                    if (relatedFindings.Any(f => f.Severity == FindingSeverity.Critical))
                        severity = GapSeverity.Critical;
                }

                if (issues.Any())
                {
                    gaps.Add(new ComplianceGap
                    {
                        ControlId = control.ControlId,
                        ControlName = control.Name,
                        Criteria = control.TrustServicesCriteria,
                        Category = GetCategoryForCriteria(control.TrustServicesCriteria),
                        Severity = severity,
                        Issues = issues,
                        RemediationSteps = GetRemediationSteps(control, issues)
                    });
                }
            }

            return new GapAnalysisResult
            {
                AnalyzedAt = DateTime.UtcNow,
                TotalControls = _controls.Values.Count(c => c.IsEnabled),
                ControlsWithGaps = gaps.Select(g => g.ControlId).Distinct().Count(),
                Gaps = gaps.OrderByDescending(g => g.Severity).ToList(),
                CriticalGaps = gaps.Count(g => g.Severity == GapSeverity.Critical),
                HighGaps = gaps.Count(g => g.Severity == GapSeverity.High),
                MediumGaps = gaps.Count(g => g.Severity == GapSeverity.Medium),
                LowGaps = gaps.Count(g => g.Severity == GapSeverity.Low)
            };
        }

        /// <summary>
        /// Gets all controls.
        /// </summary>
        /// <returns>Collection of SOC 2 controls.</returns>
        public IReadOnlyList<Soc2Control> GetControls() => _controls.Values.ToList();

        /// <summary>
        /// Gets controls by Trust Services Criteria.
        /// </summary>
        /// <param name="criteria">Criteria identifier (e.g., "CC1", "A1").</param>
        /// <returns>Controls for the specified criteria.</returns>
        public IReadOnlyList<Soc2Control> GetControlsByCriteria(string criteria) =>
            _controls.Values.Where(c => c.TrustServicesCriteria.StartsWith(criteria)).ToList();

        /// <summary>
        /// Gets evidence for a control.
        /// </summary>
        /// <param name="controlId">Control identifier.</param>
        /// <returns>Evidence items for the control.</returns>
        public IReadOnlyList<ControlEvidence> GetEvidenceForControl(string controlId) =>
            _evidence.Values.Where(e => e.ControlId == controlId).ToList();

        #endregion

        #region Message Handlers

        private async Task HandleAssessControlAsync(PluginMessage message)
        {
            var controlId = GetString(message.Payload, "controlId") ?? throw new ArgumentException("controlId required");
            var assessedBy = GetString(message.Payload, "assessedBy") ?? "system";

            var result = await AssessControlAsync(controlId, assessedBy);
            message.Payload["result"] = result;
        }

        private async Task HandleCollectEvidenceAsync(PluginMessage message)
        {
            var request = new EvidenceCollectionRequest
            {
                ControlId = GetString(message.Payload, "controlId") ?? throw new ArgumentException("controlId required"),
                Description = GetString(message.Payload, "description") ?? throw new ArgumentException("description required"),
                Source = GetString(message.Payload, "source") ?? "manual",
                CollectedBy = GetString(message.Payload, "collectedBy") ?? "system",
                Type = Enum.TryParse<EvidenceType>(GetString(message.Payload, "type"), true, out var type)
                    ? type : EvidenceType.Document
            };

            var result = await CollectEvidenceAsync(request);
            message.Payload["result"] = result;
        }

        private async Task HandleMonitoringCheckAsync(PluginMessage message)
        {
            var result = await RunContinuousMonitoringAsync();
            message.Payload["result"] = result;
        }

        private async Task HandleGenerateAuditAsync(PluginMessage message)
        {
            var auditType = Enum.TryParse<AuditType>(GetString(message.Payload, "auditType"), true, out var type)
                ? type : AuditType.TypeII;
            var periodStart = GetDateTime(message.Payload, "periodStart") ?? DateTime.UtcNow.AddYears(-1);
            var periodEnd = GetDateTime(message.Payload, "periodEnd") ?? DateTime.UtcNow;

            var result = await GenerateAuditReportAsync(auditType, periodStart, periodEnd);
            message.Payload["result"] = result;
        }

        private async Task HandleReadinessAssessAsync(PluginMessage message)
        {
            var result = await AssessAuditReadinessAsync();
            message.Payload["result"] = result;
        }

        private async Task HandleRecordFindingAsync(PluginMessage message)
        {
            var request = new FindingRequest
            {
                ControlId = GetString(message.Payload, "controlId") ?? throw new ArgumentException("controlId required"),
                Title = GetString(message.Payload, "title") ?? throw new ArgumentException("title required"),
                Description = GetString(message.Payload, "description") ?? "",
                Type = Enum.TryParse<FindingType>(GetString(message.Payload, "type"), true, out var type)
                    ? type : FindingType.ControlDeficiency,
                Severity = Enum.TryParse<FindingSeverity>(GetString(message.Payload, "severity"), true, out var sev)
                    ? sev : FindingSeverity.Medium,
                IdentifiedBy = GetString(message.Payload, "identifiedBy") ?? "system"
            };

            var result = await RecordFindingAsync(request);
            message.Payload["result"] = result;
        }

        private void HandleCalculateScore(PluginMessage message)
        {
            var result = CalculateComplianceScore();
            message.Payload["result"] = result;
        }

        private void HandleGapAnalysis(PluginMessage message)
        {
            var result = PerformGapAnalysis();
            message.Payload["result"] = result;
        }

        #endregion

        #region Helper Methods

        private void InitializeControls()
        {
            // Security Controls (CC1-CC9)
            AddSecurityControls();

            // Availability Controls (A1)
            AddAvailabilityControls();

            // Processing Integrity Controls (PI1)
            AddProcessingIntegrityControls();

            // Confidentiality Controls (C1)
            AddConfidentialityControls();

            // Privacy Controls (P1-P8)
            AddPrivacyControls();
        }

        private void AddSecurityControls()
        {
            // CC1: Control Environment
            AddControl("CC1.1", "CC1", "Organization Commitment to Integrity", "The entity demonstrates commitment to integrity and ethical values", ControlPriority.Critical);
            AddControl("CC1.2", "CC1", "Board Independence", "The board of directors demonstrates independence from management", ControlPriority.High);
            AddControl("CC1.3", "CC1", "Management Oversight", "Management establishes structures, reporting lines, and responsibilities", ControlPriority.High);
            AddControl("CC1.4", "CC1", "Competence Requirements", "The entity demonstrates commitment to attract and retain competent individuals", ControlPriority.Medium);
            AddControl("CC1.5", "CC1", "Accountability", "The entity holds individuals accountable for their internal control responsibilities", ControlPriority.High);

            // CC2: Communication and Information
            AddControl("CC2.1", "CC2", "Information Quality", "The entity obtains or generates and uses relevant quality information", ControlPriority.High);
            AddControl("CC2.2", "CC2", "Internal Communication", "The entity internally communicates information necessary to support functioning of internal control", ControlPriority.Medium);
            AddControl("CC2.3", "CC2", "External Communication", "The entity communicates with external parties regarding matters affecting functioning of internal control", ControlPriority.Medium);

            // CC3: Risk Assessment
            AddControl("CC3.1", "CC3", "Risk Objectives", "The entity specifies objectives with sufficient clarity to enable risk identification", ControlPriority.Critical);
            AddControl("CC3.2", "CC3", "Risk Identification", "The entity identifies risks to achievement of objectives and analyzes risks", ControlPriority.Critical);
            AddControl("CC3.3", "CC3", "Fraud Risk", "The entity considers the potential for fraud in assessing risks", ControlPriority.High);
            AddControl("CC3.4", "CC3", "Change Management", "The entity identifies and assesses changes that could significantly affect internal control", ControlPriority.High);

            // CC4: Monitoring Activities
            AddControl("CC4.1", "CC4", "Ongoing Monitoring", "The entity selects and develops ongoing evaluations to ascertain whether components of internal control are present", ControlPriority.High);
            AddControl("CC4.2", "CC4", "Deficiency Evaluation", "The entity evaluates and communicates internal control deficiencies in a timely manner", ControlPriority.High);

            // CC5: Control Activities
            AddControl("CC5.1", "CC5", "Control Activity Selection", "The entity selects and develops control activities that contribute to mitigation of risks", ControlPriority.Critical);
            AddControl("CC5.2", "CC5", "Technology Controls", "The entity selects and develops general control activities over technology", ControlPriority.Critical);
            AddControl("CC5.3", "CC5", "Policy Deployment", "The entity deploys control activities through policies and procedures", ControlPriority.High);

            // CC6: Logical and Physical Access Controls
            AddControl("CC6.1", "CC6", "Logical Access", "The entity implements logical access security measures to protect assets", ControlPriority.Critical);
            AddControl("CC6.2", "CC6", "Access Provisioning", "Prior to issuing credentials, entity registers and authorizes new users", ControlPriority.Critical);
            AddControl("CC6.3", "CC6", "Access Modification", "The entity removes access when no longer required", ControlPriority.Critical);
            AddControl("CC6.4", "CC6", "Physical Access", "The entity restricts physical access to facilities and assets", ControlPriority.High);
            AddControl("CC6.5", "CC6", "Asset Protection", "The entity discontinues logical and physical protections when assets are disposed", ControlPriority.Medium);
            AddControl("CC6.6", "CC6", "External Threats", "The entity implements controls to protect against threats from outside system boundaries", ControlPriority.Critical);
            AddControl("CC6.7", "CC6", "Data Transmission", "The entity restricts transmission, movement, and removal of information", ControlPriority.High);
            AddControl("CC6.8", "CC6", "Malware Prevention", "The entity implements controls to prevent or detect and act upon malicious software", ControlPriority.Critical);

            // CC7: System Operations
            AddControl("CC7.1", "CC7", "Vulnerability Management", "Entity identifies vulnerabilities and implements remediation", ControlPriority.Critical);
            AddControl("CC7.2", "CC7", "Incident Detection", "The entity monitors system components for indications of anomalies", ControlPriority.Critical);
            AddControl("CC7.3", "CC7", "Incident Response", "The entity evaluates security events to determine whether they are incidents", ControlPriority.Critical);
            AddControl("CC7.4", "CC7", "Incident Recovery", "The entity responds to identified security incidents", ControlPriority.Critical);
            AddControl("CC7.5", "CC7", "Recovery Testing", "The entity identifies, develops, and implements activities for recovery", ControlPriority.High);

            // CC8: Change Management
            AddControl("CC8.1", "CC8", "Change Authorization", "The entity authorizes, designs, develops, and implements changes", ControlPriority.Critical);

            // CC9: Risk Mitigation
            AddControl("CC9.1", "CC9", "Risk Mitigation Selection", "The entity identifies and assesses risk mitigation options", ControlPriority.High);
            AddControl("CC9.2", "CC9", "Vendor Management", "The entity assesses and manages risks from vendors and partners", ControlPriority.High);
        }

        private void AddAvailabilityControls()
        {
            // A1: Availability
            AddControl("A1.1", "A1", "Capacity Management", "The entity maintains, monitors, and evaluates current processing capacity", ControlPriority.High);
            AddControl("A1.2", "A1", "Environmental Protections", "The entity authorizes, designs, and implements environmental protections", ControlPriority.High);
            AddControl("A1.3", "A1", "Recovery Testing", "The entity tests recovery plan procedures supporting system recovery", ControlPriority.Critical);
        }

        private void AddProcessingIntegrityControls()
        {
            // PI1: Processing Integrity
            AddControl("PI1.1", "PI1", "Processing Accuracy", "The entity obtains or generates, uses, and communicates relevant, quality information", ControlPriority.High);
            AddControl("PI1.2", "PI1", "Error Handling", "The entity implements policies and procedures to identify data errors", ControlPriority.High);
            AddControl("PI1.3", "PI1", "Input Validation", "The entity implements policies and procedures over processing inputs", ControlPriority.High);
            AddControl("PI1.4", "PI1", "Processing Monitoring", "The entity implements policies and procedures to verify processing is complete and accurate", ControlPriority.High);
            AddControl("PI1.5", "PI1", "Output Completeness", "The entity implements policies and procedures to verify output is complete and accurate", ControlPriority.High);
        }

        private void AddConfidentialityControls()
        {
            // C1: Confidentiality
            AddControl("C1.1", "C1", "Confidential Information Identification", "Entity identifies and maintains confidential information inventory", ControlPriority.High);
            AddControl("C1.2", "C1", "Confidential Information Disposal", "Entity disposes of confidential information meeting retention requirements", ControlPriority.High);
        }

        private void AddPrivacyControls()
        {
            // P1: Notice
            AddControl("P1.1", "P1", "Privacy Notice", "Entity provides notice about its privacy practices", ControlPriority.High);

            // P2: Choice and Consent
            AddControl("P2.1", "P2", "Privacy Choices", "Entity communicates choices available regarding personal information", ControlPriority.High);

            // P3: Collection
            AddControl("P3.1", "P3", "Collection Limitation", "Entity collects personal information consistent with objectives", ControlPriority.High);
            AddControl("P3.2", "P3", "Informed Consent", "Entity describes collection practices for personal information", ControlPriority.High);

            // P4: Use, Retention, and Disposal
            AddControl("P4.1", "P4", "Use Limitation", "Entity limits use of personal information to purposes identified", ControlPriority.High);
            AddControl("P4.2", "P4", "Retention", "Entity retains personal information consistent with objectives", ControlPriority.High);
            AddControl("P4.3", "P4", "Disposal", "Entity securely disposes of personal information", ControlPriority.High);

            // P5: Access
            AddControl("P5.1", "P5", "Access Rights", "Entity grants identified individuals ability to access their personal information", ControlPriority.High);

            // P6: Disclosure
            AddControl("P6.1", "P6", "Disclosure to Third Parties", "Entity discloses personal information to third parties with consent", ControlPriority.High);
            AddControl("P6.2", "P6", "Authorized Disclosure", "Entity ensures third parties protect personal information", ControlPriority.High);

            // P7: Quality
            AddControl("P7.1", "P7", "Data Quality", "Entity collects and maintains accurate personal information", ControlPriority.Medium);

            // P8: Monitoring and Enforcement
            AddControl("P8.1", "P8", "Privacy Complaints", "Entity implements process for receiving privacy-related complaints", ControlPriority.High);
        }

        private void AddControl(string controlId, string criteria, string name, string description, ControlPriority priority)
        {
            _controls[controlId] = new Soc2Control
            {
                ControlId = controlId,
                TrustServicesCriteria = criteria,
                Name = name,
                Description = description,
                Priority = priority,
                IsEnabled = true,
                MonitoringEnabled = priority == ControlPriority.Critical || priority == ControlPriority.High,
                MinimumEvidenceRequired = priority == ControlPriority.Critical ? 3 : (priority == ControlPriority.High ? 2 : 1),
                RemediationGuidance = $"Implement and document controls for: {name}"
            };
        }

        private List<TrustServicesCriteria> InitializeTrustServicesCriteria()
        {
            return new List<TrustServicesCriteria>
            {
                new() { Id = "CC1", Name = "Control Environment", Category = "Security", Description = "Foundation for all other components of internal control" },
                new() { Id = "CC2", Name = "Communication and Information", Category = "Security", Description = "Information necessary to carry out internal control responsibilities" },
                new() { Id = "CC3", Name = "Risk Assessment", Category = "Security", Description = "Dynamic and iterative process for identifying and assessing risks" },
                new() { Id = "CC4", Name = "Monitoring Activities", Category = "Security", Description = "Evaluations to ascertain whether internal control components are present" },
                new() { Id = "CC5", Name = "Control Activities", Category = "Security", Description = "Actions established through policies and procedures" },
                new() { Id = "CC6", Name = "Logical and Physical Access Controls", Category = "Security", Description = "Security measures to protect against unauthorized access" },
                new() { Id = "CC7", Name = "System Operations", Category = "Security", Description = "Detection and response to system anomalies and incidents" },
                new() { Id = "CC8", Name = "Change Management", Category = "Security", Description = "Management of changes to infrastructure and software" },
                new() { Id = "CC9", Name = "Risk Mitigation", Category = "Security", Description = "Identification and management of risks from vendors" },
                new() { Id = "A1", Name = "Availability", Category = "Availability", Description = "System availability for operation and use as committed" },
                new() { Id = "PI1", Name = "Processing Integrity", Category = "ProcessingIntegrity", Description = "System processing is complete, valid, accurate, timely" },
                new() { Id = "C1", Name = "Confidentiality", Category = "Confidentiality", Description = "Information designated as confidential is protected" },
                new() { Id = "P1", Name = "Privacy - Notice", Category = "Privacy", Description = "Privacy notice to data subjects" },
                new() { Id = "P2", Name = "Privacy - Choice and Consent", Category = "Privacy", Description = "Choices available and consent for personal information" },
                new() { Id = "P3", Name = "Privacy - Collection", Category = "Privacy", Description = "Collection of personal information" },
                new() { Id = "P4", Name = "Privacy - Use, Retention, Disposal", Category = "Privacy", Description = "Use, retention, and disposal of personal information" },
                new() { Id = "P5", Name = "Privacy - Access", Category = "Privacy", Description = "Access to personal information" },
                new() { Id = "P6", Name = "Privacy - Disclosure", Category = "Privacy", Description = "Disclosure of personal information to third parties" },
                new() { Id = "P7", Name = "Privacy - Quality", Category = "Privacy", Description = "Quality of personal information" },
                new() { Id = "P8", Name = "Privacy - Monitoring and Enforcement", Category = "Privacy", Description = "Monitoring of privacy practices" }
            };
        }

        private async Task<ControlValidationResult> ValidateControlAsync(
            Soc2Control control,
            object data,
            Dictionary<string, object>? context,
            CancellationToken ct)
        {
            // Check if control has been assessed
            if (!_assessments.TryGetValue(control.ControlId, out var assessment))
            {
                return new ControlValidationResult
                {
                    IsCompliant = false,
                    Message = $"Control {control.ControlId} has not been assessed",
                    Warnings = new List<string> { "Assessment required" }
                };
            }

            // Check assessment outcome
            if (assessment.Result == AssessmentOutcome.Fail)
            {
                return new ControlValidationResult
                {
                    IsCompliant = false,
                    Message = $"Control {control.ControlId} failed assessment: {assessment.Notes}"
                };
            }

            // Check evidence sufficiency
            var evidence = GetEvidenceForControl(control.ControlId);
            if (evidence.Count < control.MinimumEvidenceRequired)
            {
                return new ControlValidationResult
                {
                    IsCompliant = true,
                    HasWarnings = true,
                    Warnings = new List<string>
                    {
                        $"Insufficient evidence for control {control.ControlId}: {evidence.Count}/{control.MinimumEvidenceRequired}"
                    }
                };
            }

            return new ControlValidationResult { IsCompliant = true };
        }

        private AssessmentOutcome DetermineAssessmentOutcome(Soc2Control control, IReadOnlyList<ControlEvidence> evidence)
        {
            if (evidence.Count < control.MinimumEvidenceRequired)
                return AssessmentOutcome.Fail;

            // Check for recent evidence
            var recentEvidence = evidence.Where(e =>
                e.CollectedAt >= DateTime.UtcNow.AddDays(-_config.EvidenceRecencyDays)).ToList();

            if (recentEvidence.Count == 0 && control.Priority == ControlPriority.Critical)
                return AssessmentOutcome.Fail;

            // Check for open findings
            var openFindings = _findings.Values.Where(f =>
                f.ControlId == control.ControlId && f.Status == FindingStatus.Open).ToList();

            if (openFindings.Any(f => f.Severity == FindingSeverity.Critical))
                return AssessmentOutcome.Fail;

            if (openFindings.Any())
                return AssessmentOutcome.PartialPass;

            return AssessmentOutcome.Pass;
        }

        private double CalculateControlEffectiveness(Soc2Control control, IReadOnlyList<ControlEvidence> evidence)
        {
            var score = 0.0;
            var maxScore = 100.0;

            // Evidence coverage (40%)
            var evidenceScore = Math.Min(evidence.Count / (double)Math.Max(control.MinimumEvidenceRequired, 1), 1.0) * 40;
            score += evidenceScore;

            // Evidence recency (30%)
            var recentEvidence = evidence.Where(e =>
                e.CollectedAt >= DateTime.UtcNow.AddDays(-_config.EvidenceRecencyDays)).ToList();
            var recencyScore = evidence.Count > 0
                ? (recentEvidence.Count / (double)evidence.Count) * 30
                : 0;
            score += recencyScore;

            // Finding status (30%)
            var openFindings = _findings.Values.Where(f =>
                f.ControlId == control.ControlId && f.Status == FindingStatus.Open).ToList();
            var findingScore = openFindings.Count == 0 ? 30 : Math.Max(0, 30 - (openFindings.Count * 10));
            score += findingScore;

            return score / maxScore;
        }

        private async Task<MonitoringCheckResult> RunControlMonitoringCheckAsync(Soc2Control control, CancellationToken ct)
        {
            // Automated monitoring checks based on control type
            var evidence = GetEvidenceForControl(control.ControlId);
            var assessment = _assessments.TryGetValue(control.ControlId, out var a) ? a : null;

            // Check evidence freshness
            var freshEvidence = evidence.Where(e =>
                e.CollectedAt >= DateTime.UtcNow.AddDays(-_config.MonitoringEvidenceThresholdDays)).ToList();

            if (freshEvidence.Count == 0 && control.Priority == ControlPriority.Critical)
            {
                return new MonitoringCheckResult
                {
                    ControlId = control.ControlId,
                    CheckedAt = DateTime.UtcNow,
                    Status = MonitoringStatus.Alert,
                    Severity = AlertSeverity.High,
                    Message = $"No fresh evidence for critical control {control.ControlId}"
                };
            }

            // Check assessment currency
            if (assessment == null || assessment.AssessedAt < DateTime.UtcNow.AddDays(-_config.AssessmentValidityDays))
            {
                return new MonitoringCheckResult
                {
                    ControlId = control.ControlId,
                    CheckedAt = DateTime.UtcNow,
                    Status = MonitoringStatus.Warning,
                    Severity = AlertSeverity.Medium,
                    Message = $"Assessment stale or missing for control {control.ControlId}"
                };
            }

            return new MonitoringCheckResult
            {
                ControlId = control.ControlId,
                CheckedAt = DateTime.UtcNow,
                Status = MonitoringStatus.Healthy,
                Severity = AlertSeverity.None,
                Message = "Control monitoring check passed"
            };
        }

        private string GetCategoryForCriteria(string criteria)
        {
            if (criteria.StartsWith("CC")) return "Security";
            if (criteria.StartsWith("A")) return "Availability";
            if (criteria.StartsWith("PI")) return "ProcessingIntegrity";
            if (criteria.StartsWith("C")) return "Confidentiality";
            if (criteria.StartsWith("P")) return "Privacy";
            return "Unknown";
        }

        private static string MapControlSeverity(ControlPriority priority) => priority switch
        {
            ControlPriority.Critical => "Critical",
            ControlPriority.High => "High",
            ControlPriority.Medium => "Medium",
            _ => "Low"
        };

        private DateTime? GetNextAssessmentDueDate()
        {
            if (!_assessments.Any()) return DateTime.UtcNow;
            var oldest = _assessments.Values.Min(a => a.AssessedAt);
            return oldest.AddDays(_config.AssessmentValidityDays);
        }

        private static string ComputeEvidenceHash(EvidenceCollectionRequest request)
        {
            using var sha256 = SHA256.Create();
            var content = $"{request.ControlId}|{request.Description}|{request.Source}|{DateTime.UtcNow:O}";
            var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(content));
            return Convert.ToHexString(hash);
        }

        private static int GetDefaultRemediationDays(FindingSeverity severity) => severity switch
        {
            FindingSeverity.Critical => 7,
            FindingSeverity.High => 30,
            FindingSeverity.Medium => 60,
            _ => 90
        };

        private TimeSpan EstimateRemediationEffort(List<ReadinessGap> gaps)
        {
            var hours = gaps.Sum(g => g.Severity switch
            {
                ControlPriority.Critical => 40,
                ControlPriority.High => 20,
                ControlPriority.Medium => 10,
                _ => 5
            });
            return TimeSpan.FromHours(hours);
        }

        private List<string> GetRemediationSteps(Soc2Control control, List<string> issues)
        {
            var steps = new List<string>();

            if (issues.Any(i => i.Contains("evidence")))
            {
                steps.Add($"Collect required evidence for {control.Name}");
                steps.Add("Document evidence collection process");
            }

            if (issues.Any(i => i.Contains("assessment")))
            {
                steps.Add($"Perform control assessment for {control.ControlId}");
                steps.Add("Document assessment methodology and findings");
            }

            if (issues.Any(i => i.Contains("findings")))
            {
                steps.Add("Review and remediate open findings");
                steps.Add("Document remediation actions taken");
            }

            steps.Add(control.RemediationGuidance ?? $"Implement control {control.ControlId}");

            return steps;
        }

        #endregion

        #region Persistence

        private async Task LoadDataAsync()
        {
            var path = Path.Combine(_storagePath, "soc2-data.json");
            if (!File.Exists(path)) return;

            try
            {
                var json = await File.ReadAllTextAsync(path);
                var data = JsonSerializer.Deserialize<Soc2PersistenceData>(json);

                if (data?.Evidence != null)
                {
                    foreach (var e in data.Evidence)
                        _evidence[e.EvidenceId] = e;
                }

                if (data?.Findings != null)
                {
                    foreach (var f in data.Findings)
                        _findings[f.FindingId] = f;
                }

                if (data?.Assessments != null)
                {
                    foreach (var a in data.Assessments)
                        _assessments[a.ControlId] = a;
                }

                if (data?.Exceptions != null)
                {
                    foreach (var e in data.Exceptions)
                        _exceptions[e.ExceptionId] = e;
                }
            }
            catch
            {
                // Log but don't throw on load failure
            }
        }

        private async Task SaveDataAsync()
        {
            if (!await _persistLock.WaitAsync(TimeSpan.FromSeconds(5)))
                return;

            try
            {
                Directory.CreateDirectory(_storagePath);

                var data = new Soc2PersistenceData
                {
                    Evidence = _evidence.Values.ToList(),
                    Findings = _findings.Values.ToList(),
                    Assessments = _assessments.Values.ToList(),
                    Exceptions = _exceptions.Values.ToList()
                };

                var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(Path.Combine(_storagePath, "soc2-data.json"), json);
            }
            finally
            {
                _persistLock.Release();
            }
        }

        #endregion

        #region Utility Methods

        private static string? GetString(Dictionary<string, object> payload, string key) =>
            payload.TryGetValue(key, out var val) && val is string s ? s : null;

        private static DateTime? GetDateTime(Dictionary<string, object> payload, string key)
        {
            if (payload.TryGetValue(key, out var val))
            {
                if (val is DateTime dt) return dt;
                if (val is string s && DateTime.TryParse(s, out var parsed)) return parsed;
            }
            return null;
        }

        #endregion
    }

    #region Configuration

    /// <summary>
    /// Configuration options for the SOC 2 Compliance Plugin.
    /// </summary>
    public class Soc2Config
    {
        /// <summary>
        /// Organization name for audit reports.
        /// </summary>
        public string OrganizationName { get; set; } = "Organization";

        /// <summary>
        /// Enable continuous monitoring.
        /// </summary>
        public bool EnableContinuousMonitoring { get; set; } = true;

        /// <summary>
        /// Monitoring interval in minutes.
        /// </summary>
        public int MonitoringIntervalMinutes { get; set; } = 60;

        /// <summary>
        /// Days evidence is considered recent for monitoring.
        /// </summary>
        public int MonitoringEvidenceThresholdDays { get; set; } = 30;

        /// <summary>
        /// Days evidence is considered recent for assessments.
        /// </summary>
        public int EvidenceRecencyDays { get; set; } = 90;

        /// <summary>
        /// Days to retain evidence.
        /// </summary>
        public int EvidenceRetentionDays { get; set; } = 2555; // ~7 years

        /// <summary>
        /// Days an assessment is valid.
        /// </summary>
        public int AssessmentValidityDays { get; set; } = 365;

        /// <summary>
        /// Default data retention days.
        /// </summary>
        public int DefaultRetentionDays { get; set; } = 2555;

        /// <summary>
        /// Compliance score threshold for passing (0.0-1.0).
        /// </summary>
        public double ComplianceThreshold { get; set; } = 0.85;

        /// <summary>
        /// Readiness score threshold for audit readiness (0.0-1.0).
        /// </summary>
        public double ReadinessThreshold { get; set; } = 0.90;
    }

    #endregion

    #region Models

    /// <summary>
    /// Represents a SOC 2 control.
    /// </summary>
    public class Soc2Control
    {
        /// <summary>
        /// Unique control identifier.
        /// </summary>
        public string ControlId { get; set; } = string.Empty;

        /// <summary>
        /// Trust Services Criteria this control supports.
        /// </summary>
        public string TrustServicesCriteria { get; set; } = string.Empty;

        /// <summary>
        /// Control name.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Control description.
        /// </summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Control priority.
        /// </summary>
        public ControlPriority Priority { get; set; }

        /// <summary>
        /// Whether the control is enabled.
        /// </summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>
        /// Whether continuous monitoring is enabled for this control.
        /// </summary>
        public bool MonitoringEnabled { get; set; }

        /// <summary>
        /// Minimum evidence items required.
        /// </summary>
        public int MinimumEvidenceRequired { get; set; } = 1;

        /// <summary>
        /// Guidance for remediation.
        /// </summary>
        public string? RemediationGuidance { get; set; }
    }

    /// <summary>
    /// Trust Services Criteria definition.
    /// </summary>
    public class TrustServicesCriteria
    {
        /// <summary>
        /// Criteria identifier (e.g., CC1, A1, P1).
        /// </summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>
        /// Criteria name.
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Category (Security, Availability, etc.).
        /// </summary>
        public string Category { get; set; } = string.Empty;

        /// <summary>
        /// Criteria description.
        /// </summary>
        public string Description { get; set; } = string.Empty;
    }

    /// <summary>
    /// Evidence collected for a control.
    /// </summary>
    public class ControlEvidence
    {
        /// <summary>
        /// Unique evidence identifier.
        /// </summary>
        public string EvidenceId { get; set; } = string.Empty;

        /// <summary>
        /// Associated control identifier.
        /// </summary>
        public string ControlId { get; set; } = string.Empty;

        /// <summary>
        /// Evidence description.
        /// </summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Evidence source.
        /// </summary>
        public string Source { get; set; } = string.Empty;

        /// <summary>
        /// Collection timestamp.
        /// </summary>
        public DateTime CollectedAt { get; set; }

        /// <summary>
        /// Collector identifier.
        /// </summary>
        public string CollectedBy { get; set; } = string.Empty;

        /// <summary>
        /// Evidence type.
        /// </summary>
        public EvidenceType Type { get; set; }

        /// <summary>
        /// Additional metadata.
        /// </summary>
        public Dictionary<string, object> Metadata { get; set; } = new();

        /// <summary>
        /// Evidence content hash for integrity verification.
        /// </summary>
        public string Hash { get; set; } = string.Empty;

        /// <summary>
        /// Retention end date.
        /// </summary>
        public DateTime RetentionUntil { get; set; }
    }

    /// <summary>
    /// Audit finding.
    /// </summary>
    public class AuditFinding
    {
        /// <summary>
        /// Unique finding identifier.
        /// </summary>
        public string FindingId { get; set; } = string.Empty;

        /// <summary>
        /// Associated control identifier.
        /// </summary>
        public string ControlId { get; set; } = string.Empty;

        /// <summary>
        /// Finding title.
        /// </summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>
        /// Finding description.
        /// </summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Finding type.
        /// </summary>
        public FindingType Type { get; set; }

        /// <summary>
        /// Finding severity.
        /// </summary>
        public FindingSeverity Severity { get; set; }

        /// <summary>
        /// Identification timestamp.
        /// </summary>
        public DateTime IdentifiedAt { get; set; }

        /// <summary>
        /// Identifier who discovered the finding.
        /// </summary>
        public string IdentifiedBy { get; set; } = string.Empty;

        /// <summary>
        /// Finding status.
        /// </summary>
        public FindingStatus Status { get; set; }

        /// <summary>
        /// Remediation plan.
        /// </summary>
        public string? RemediationPlan { get; set; }

        /// <summary>
        /// Due date for remediation.
        /// </summary>
        public DateTime? DueDate { get; set; }

        /// <summary>
        /// Resolution timestamp.
        /// </summary>
        public DateTime? ResolvedAt { get; set; }

        /// <summary>
        /// Resolution notes.
        /// </summary>
        public string? ResolutionNotes { get; set; }
    }

    /// <summary>
    /// Control assessment result.
    /// </summary>
    public class ControlAssessmentResult
    {
        /// <summary>
        /// Unique assessment identifier.
        /// </summary>
        public string AssessmentId { get; set; } = string.Empty;

        /// <summary>
        /// Assessed control identifier.
        /// </summary>
        public string ControlId { get; set; } = string.Empty;

        /// <summary>
        /// Assessment timestamp.
        /// </summary>
        public DateTime AssessedAt { get; set; }

        /// <summary>
        /// Assessor identifier.
        /// </summary>
        public string AssessedBy { get; set; } = string.Empty;

        /// <summary>
        /// Assessment outcome.
        /// </summary>
        public AssessmentOutcome Result { get; set; }

        /// <summary>
        /// Number of evidence items reviewed.
        /// </summary>
        public int EvidenceCount { get; set; }

        /// <summary>
        /// Control effectiveness score (0.0-1.0).
        /// </summary>
        public double EffectivenessScore { get; set; }

        /// <summary>
        /// Assessment notes.
        /// </summary>
        public string? Notes { get; set; }
    }

    /// <summary>
    /// Monitoring alert.
    /// </summary>
    public class MonitoringAlert
    {
        /// <summary>
        /// Alert identifier.
        /// </summary>
        public string AlertId { get; set; } = string.Empty;

        /// <summary>
        /// Related control identifier.
        /// </summary>
        public string ControlId { get; set; } = string.Empty;

        /// <summary>
        /// Alert severity.
        /// </summary>
        public AlertSeverity Severity { get; set; }

        /// <summary>
        /// Alert message.
        /// </summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>
        /// Detection timestamp.
        /// </summary>
        public DateTime DetectedAt { get; set; }

        /// <summary>
        /// Whether the alert is resolved.
        /// </summary>
        public bool Resolved { get; set; }

        /// <summary>
        /// Resolution timestamp.
        /// </summary>
        public DateTime? ResolvedAt { get; set; }
    }

    /// <summary>
    /// Control exception (documented deviation).
    /// </summary>
    public class ControlException
    {
        /// <summary>
        /// Exception identifier.
        /// </summary>
        public string ExceptionId { get; set; } = string.Empty;

        /// <summary>
        /// Related control identifier.
        /// </summary>
        public string ControlId { get; set; } = string.Empty;

        /// <summary>
        /// Exception reason.
        /// </summary>
        public string Reason { get; set; } = string.Empty;

        /// <summary>
        /// Compensating controls.
        /// </summary>
        public string? CompensatingControls { get; set; }

        /// <summary>
        /// Effective from date.
        /// </summary>
        public DateTime EffectiveFrom { get; set; }

        /// <summary>
        /// Effective to date.
        /// </summary>
        public DateTime? EffectiveTo { get; set; }

        /// <summary>
        /// Approver.
        /// </summary>
        public string ApprovedBy { get; set; } = string.Empty;
    }

    /// <summary>
    /// Compliance score result.
    /// </summary>
    public class ComplianceScoreResult
    {
        /// <summary>
        /// Overall compliance score (0.0-1.0).
        /// </summary>
        public double OverallScore { get; set; }

        /// <summary>
        /// Scores by category.
        /// </summary>
        public Dictionary<string, double> CategoryScores { get; set; } = new();

        /// <summary>
        /// Calculation timestamp.
        /// </summary>
        public DateTime CalculatedAt { get; set; }

        /// <summary>
        /// Whether compliant based on threshold.
        /// </summary>
        public bool IsCompliant { get; set; }

        /// <summary>
        /// Compliance threshold used.
        /// </summary>
        public double ComplianceThreshold { get; set; }
    }

    /// <summary>
    /// SOC 2 audit report.
    /// </summary>
    public class Soc2AuditReport
    {
        /// <summary>
        /// Report identifier.
        /// </summary>
        public string ReportId { get; set; } = string.Empty;

        /// <summary>
        /// Audit type (Type I or Type II).
        /// </summary>
        public AuditType AuditType { get; set; }

        /// <summary>
        /// Generation timestamp.
        /// </summary>
        public DateTime GeneratedAt { get; set; }

        /// <summary>
        /// Audit period start.
        /// </summary>
        public DateTime PeriodStart { get; set; }

        /// <summary>
        /// Audit period end.
        /// </summary>
        public DateTime PeriodEnd { get; set; }

        /// <summary>
        /// Organization name.
        /// </summary>
        public string OrganizationName { get; set; } = string.Empty;

        /// <summary>
        /// Overall compliance score.
        /// </summary>
        public double OverallScore { get; set; }

        /// <summary>
        /// Whether compliant.
        /// </summary>
        public bool IsCompliant { get; set; }

        /// <summary>
        /// Total controls evaluated.
        /// </summary>
        public int TotalControls { get; set; }

        /// <summary>
        /// Passing controls.
        /// </summary>
        public int PassingControls { get; set; }

        /// <summary>
        /// Failing controls.
        /// </summary>
        public int FailingControls { get; set; }

        /// <summary>
        /// Results by Trust Services Criteria.
        /// </summary>
        public List<TrustServicesCriteriaResult> CriteriaResults { get; set; } = new();

        /// <summary>
        /// Audit findings.
        /// </summary>
        public List<AuditFinding> Findings { get; set; } = new();

        /// <summary>
        /// Active exceptions.
        /// </summary>
        public List<ControlException> Exceptions { get; set; } = new();

        /// <summary>
        /// Evidence count in period.
        /// </summary>
        public int EvidenceCount { get; set; }

        /// <summary>
        /// Scores by category.
        /// </summary>
        public Dictionary<string, double> ScoresByCategory { get; set; } = new();
    }

    /// <summary>
    /// Trust Services Criteria result in audit report.
    /// </summary>
    public class TrustServicesCriteriaResult
    {
        /// <summary>
        /// Criteria identifier.
        /// </summary>
        public string CriteriaId { get; set; } = string.Empty;

        /// <summary>
        /// Criteria name.
        /// </summary>
        public string CriteriaName { get; set; } = string.Empty;

        /// <summary>
        /// Category.
        /// </summary>
        public string Category { get; set; } = string.Empty;

        /// <summary>
        /// Total controls.
        /// </summary>
        public int TotalControls { get; set; }

        /// <summary>
        /// Passing controls.
        /// </summary>
        public int PassingControls { get; set; }

        /// <summary>
        /// Failing controls.
        /// </summary>
        public int FailingControls { get; set; }

        /// <summary>
        /// Criteria score.
        /// </summary>
        public double Score { get; set; }
    }

    /// <summary>
    /// Continuous monitoring result.
    /// </summary>
    public class ContinuousMonitoringResult
    {
        /// <summary>
        /// Run timestamp.
        /// </summary>
        public DateTime RunAt { get; set; }

        /// <summary>
        /// Total checks performed.
        /// </summary>
        public int TotalChecks { get; set; }

        /// <summary>
        /// Passed checks.
        /// </summary>
        public int PassedChecks { get; set; }

        /// <summary>
        /// Failed checks.
        /// </summary>
        public int FailedChecks { get; set; }

        /// <summary>
        /// Warning checks.
        /// </summary>
        public int WarningChecks { get; set; }

        /// <summary>
        /// New alerts generated.
        /// </summary>
        public List<MonitoringAlert> NewAlerts { get; set; } = new();

        /// <summary>
        /// Individual check results.
        /// </summary>
        public List<MonitoringCheckResult> CheckResults { get; set; } = new();
    }

    /// <summary>
    /// Individual monitoring check result.
    /// </summary>
    public class MonitoringCheckResult
    {
        /// <summary>
        /// Control identifier checked.
        /// </summary>
        public string ControlId { get; set; } = string.Empty;

        /// <summary>
        /// Check timestamp.
        /// </summary>
        public DateTime CheckedAt { get; set; }

        /// <summary>
        /// Check status.
        /// </summary>
        public MonitoringStatus Status { get; set; }

        /// <summary>
        /// Alert severity if applicable.
        /// </summary>
        public AlertSeverity Severity { get; set; }

        /// <summary>
        /// Check message.
        /// </summary>
        public string? Message { get; set; }
    }

    /// <summary>
    /// Audit readiness assessment.
    /// </summary>
    public class AuditReadinessAssessment
    {
        /// <summary>
        /// Assessment timestamp.
        /// </summary>
        public DateTime AssessedAt { get; set; }

        /// <summary>
        /// Readiness score (0.0-1.0).
        /// </summary>
        public double ReadinessScore { get; set; }

        /// <summary>
        /// Whether ready for audit.
        /// </summary>
        public bool IsReady { get; set; }

        /// <summary>
        /// Total controls evaluated.
        /// </summary>
        public int TotalControls { get; set; }

        /// <summary>
        /// Controls ready for audit.
        /// </summary>
        public int ReadyControls { get; set; }

        /// <summary>
        /// Identified gaps.
        /// </summary>
        public List<ReadinessGap> Gaps { get; set; } = new();

        /// <summary>
        /// Number of open findings.
        /// </summary>
        public int OpenFindings { get; set; }

        /// <summary>
        /// Recommendations for readiness.
        /// </summary>
        public List<string> Recommendations { get; set; } = new();

        /// <summary>
        /// Estimated remediation effort.
        /// </summary>
        public TimeSpan EstimatedRemediationEffort { get; set; }
    }

    /// <summary>
    /// Readiness gap.
    /// </summary>
    public class ReadinessGap
    {
        /// <summary>
        /// Control identifier.
        /// </summary>
        public string ControlId { get; set; } = string.Empty;

        /// <summary>
        /// Gap type.
        /// </summary>
        public GapType GapType { get; set; }

        /// <summary>
        /// Gap description.
        /// </summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Gap severity.
        /// </summary>
        public ControlPriority Severity { get; set; }

        /// <summary>
        /// Remediation recommendation.
        /// </summary>
        public string? Recommendation { get; set; }
    }

    /// <summary>
    /// Gap analysis result.
    /// </summary>
    public class GapAnalysisResult
    {
        /// <summary>
        /// Analysis timestamp.
        /// </summary>
        public DateTime AnalyzedAt { get; set; }

        /// <summary>
        /// Total controls analyzed.
        /// </summary>
        public int TotalControls { get; set; }

        /// <summary>
        /// Controls with gaps.
        /// </summary>
        public int ControlsWithGaps { get; set; }

        /// <summary>
        /// Identified gaps.
        /// </summary>
        public List<ComplianceGap> Gaps { get; set; } = new();

        /// <summary>
        /// Critical gaps count.
        /// </summary>
        public int CriticalGaps { get; set; }

        /// <summary>
        /// High gaps count.
        /// </summary>
        public int HighGaps { get; set; }

        /// <summary>
        /// Medium gaps count.
        /// </summary>
        public int MediumGaps { get; set; }

        /// <summary>
        /// Low gaps count.
        /// </summary>
        public int LowGaps { get; set; }
    }

    /// <summary>
    /// Compliance gap.
    /// </summary>
    public class ComplianceGap
    {
        /// <summary>
        /// Control identifier.
        /// </summary>
        public string ControlId { get; set; } = string.Empty;

        /// <summary>
        /// Control name.
        /// </summary>
        public string ControlName { get; set; } = string.Empty;

        /// <summary>
        /// Trust Services Criteria.
        /// </summary>
        public string Criteria { get; set; } = string.Empty;

        /// <summary>
        /// Category.
        /// </summary>
        public string Category { get; set; } = string.Empty;

        /// <summary>
        /// Gap severity.
        /// </summary>
        public GapSeverity Severity { get; set; }

        /// <summary>
        /// Identified issues.
        /// </summary>
        public List<string> Issues { get; set; } = new();

        /// <summary>
        /// Remediation steps.
        /// </summary>
        public List<string> RemediationSteps { get; set; } = new();
    }

    /// <summary>
    /// Control validation result.
    /// </summary>
    public class ControlValidationResult
    {
        /// <summary>
        /// Whether compliant.
        /// </summary>
        public bool IsCompliant { get; set; }

        /// <summary>
        /// Validation message.
        /// </summary>
        public string? Message { get; set; }

        /// <summary>
        /// Whether there are warnings.
        /// </summary>
        public bool HasWarnings { get; set; }

        /// <summary>
        /// Warning messages.
        /// </summary>
        public List<string> Warnings { get; set; } = new();
    }

    #endregion

    #region Request Models

    /// <summary>
    /// Evidence collection request.
    /// </summary>
    public class EvidenceCollectionRequest
    {
        /// <summary>
        /// Control identifier.
        /// </summary>
        public string ControlId { get; set; } = string.Empty;

        /// <summary>
        /// Evidence description.
        /// </summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Evidence source.
        /// </summary>
        public string Source { get; set; } = string.Empty;

        /// <summary>
        /// Collector identifier.
        /// </summary>
        public string CollectedBy { get; set; } = string.Empty;

        /// <summary>
        /// Evidence type.
        /// </summary>
        public EvidenceType Type { get; set; }

        /// <summary>
        /// Additional metadata.
        /// </summary>
        public Dictionary<string, object>? Metadata { get; set; }
    }

    /// <summary>
    /// Finding request.
    /// </summary>
    public class FindingRequest
    {
        /// <summary>
        /// Control identifier.
        /// </summary>
        public string ControlId { get; set; } = string.Empty;

        /// <summary>
        /// Finding title.
        /// </summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>
        /// Finding description.
        /// </summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Finding type.
        /// </summary>
        public FindingType Type { get; set; }

        /// <summary>
        /// Finding severity.
        /// </summary>
        public FindingSeverity Severity { get; set; }

        /// <summary>
        /// Identifier who discovered.
        /// </summary>
        public string IdentifiedBy { get; set; } = string.Empty;

        /// <summary>
        /// Remediation plan.
        /// </summary>
        public string? RemediationPlan { get; set; }

        /// <summary>
        /// Due date.
        /// </summary>
        public DateTime? DueDate { get; set; }
    }

    #endregion

    #region Enums

    /// <summary>
    /// Control priority levels.
    /// </summary>
    public enum ControlPriority
    {
        /// <summary>Low priority.</summary>
        Low,
        /// <summary>Medium priority.</summary>
        Medium,
        /// <summary>High priority.</summary>
        High,
        /// <summary>Critical priority.</summary>
        Critical
    }

    /// <summary>
    /// Evidence types.
    /// </summary>
    public enum EvidenceType
    {
        /// <summary>Document evidence.</summary>
        Document,
        /// <summary>Screenshot evidence.</summary>
        Screenshot,
        /// <summary>Log file evidence.</summary>
        Log,
        /// <summary>Configuration evidence.</summary>
        Configuration,
        /// <summary>Automated test result.</summary>
        AutomatedTest,
        /// <summary>Interview notes.</summary>
        Interview,
        /// <summary>Observation notes.</summary>
        Observation,
        /// <summary>Inquiry response.</summary>
        Inquiry
    }

    /// <summary>
    /// Finding types.
    /// </summary>
    public enum FindingType
    {
        /// <summary>Control deficiency.</summary>
        ControlDeficiency,
        /// <summary>Control gap.</summary>
        ControlGap,
        /// <summary>Deviation from policy.</summary>
        PolicyDeviation,
        /// <summary>Material weakness.</summary>
        MaterialWeakness,
        /// <summary>Significant deficiency.</summary>
        SignificantDeficiency,
        /// <summary>Observation.</summary>
        Observation
    }

    /// <summary>
    /// Finding severity levels.
    /// </summary>
    public enum FindingSeverity
    {
        /// <summary>Low severity.</summary>
        Low,
        /// <summary>Medium severity.</summary>
        Medium,
        /// <summary>High severity.</summary>
        High,
        /// <summary>Critical severity.</summary>
        Critical
    }

    /// <summary>
    /// Finding status.
    /// </summary>
    public enum FindingStatus
    {
        /// <summary>Open finding.</summary>
        Open,
        /// <summary>In remediation.</summary>
        InRemediation,
        /// <summary>Pending validation.</summary>
        PendingValidation,
        /// <summary>Closed finding.</summary>
        Closed,
        /// <summary>Accepted risk.</summary>
        AcceptedRisk
    }

    /// <summary>
    /// Assessment outcome.
    /// </summary>
    public enum AssessmentOutcome
    {
        /// <summary>Passed assessment.</summary>
        Pass,
        /// <summary>Partial pass.</summary>
        PartialPass,
        /// <summary>Failed assessment.</summary>
        Fail,
        /// <summary>Not applicable.</summary>
        NotApplicable
    }

    /// <summary>
    /// Monitoring status.
    /// </summary>
    public enum MonitoringStatus
    {
        /// <summary>Healthy status.</summary>
        Healthy,
        /// <summary>Warning status.</summary>
        Warning,
        /// <summary>Alert status.</summary>
        Alert
    }

    /// <summary>
    /// Alert severity levels.
    /// </summary>
    public enum AlertSeverity
    {
        /// <summary>No alert.</summary>
        None,
        /// <summary>Low severity.</summary>
        Low,
        /// <summary>Medium severity.</summary>
        Medium,
        /// <summary>High severity.</summary>
        High,
        /// <summary>Critical severity.</summary>
        Critical
    }

    /// <summary>
    /// Audit types.
    /// </summary>
    public enum AuditType
    {
        /// <summary>Type I - point in time.</summary>
        TypeI,
        /// <summary>Type II - period of time.</summary>
        TypeII
    }

    /// <summary>
    /// Gap types.
    /// </summary>
    public enum GapType
    {
        /// <summary>Insufficient evidence.</summary>
        InsufficientEvidence,
        /// <summary>Stale assessment.</summary>
        StaleAssessment,
        /// <summary>Missing control.</summary>
        MissingControl,
        /// <summary>Failed control.</summary>
        FailedControl,
        /// <summary>Documentation gap.</summary>
        DocumentationGap
    }

    /// <summary>
    /// Gap severity levels.
    /// </summary>
    public enum GapSeverity
    {
        /// <summary>Low severity.</summary>
        Low,
        /// <summary>Medium severity.</summary>
        Medium,
        /// <summary>High severity.</summary>
        High,
        /// <summary>Critical severity.</summary>
        Critical
    }

    #endregion

    #region Persistence

    internal class Soc2PersistenceData
    {
        public List<ControlEvidence> Evidence { get; set; } = new();
        public List<AuditFinding> Findings { get; set; } = new();
        public List<ControlAssessmentResult> Assessments { get; set; } = new();
        public List<ControlException> Exceptions { get; set; } = new();
    }

    #endregion
}
