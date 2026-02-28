using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.Privacy
{
    /// <summary>
    /// T124.7: Privacy Impact Assessment (PIA/DPIA) Strategy
    /// Conducts Data Protection Impact Assessments per GDPR Article 35.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Assessment Phases:
    /// - Threshold Analysis: Determines if DPIA is required
    /// - Data Mapping: Documents data flows and processing
    /// - Risk Assessment: Identifies privacy risks and their impact
    /// - Mitigation Planning: Defines controls to address risks
    /// - Residual Risk Evaluation: Assesses remaining risk after controls
    /// - Consultation: Triggers DPA consultation if high risk remains
    /// </para>
    /// <para>
    /// DPIA Triggers (GDPR Article 35(3)):
    /// - Systematic and extensive profiling with legal effects
    /// - Large-scale processing of special categories
    /// - Systematic monitoring of public areas
    /// </para>
    /// </remarks>
    public sealed class PrivacyImpactAssessmentStrategy : ComplianceStrategyBase
    {
        private readonly BoundedDictionary<string, PrivacyImpactAssessment> _assessments = new BoundedDictionary<string, PrivacyImpactAssessment>(1000);
        private readonly BoundedDictionary<string, DpiaTemplate> _templates = new BoundedDictionary<string, DpiaTemplate>(1000);
        private readonly BoundedDictionary<string, RiskCategory> _riskCategories = new BoundedDictionary<string, RiskCategory>(1000);
        private readonly ConcurrentBag<DpiaAuditEntry> _auditLog = new();

        private double _highRiskThreshold = 15.0;
        private double _consultationThreshold = 20.0;
        private bool _autoTriggerDpia = true;

        /// <inheritdoc/>
        public override string StrategyId => "privacy-impact-assessment";

        /// <inheritdoc/>
        public override string StrategyName => "Privacy Impact Assessment";

        /// <inheritdoc/>
        public override string Framework => "DataPrivacy";

        /// <inheritdoc/>
        public override Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default)
        {
            if (configuration.TryGetValue("HighRiskThreshold", out var hrObj) && hrObj is double hr)
                _highRiskThreshold = hr;

            if (configuration.TryGetValue("ConsultationThreshold", out var ctObj) && ctObj is double ct)
                _consultationThreshold = ct;

            if (configuration.TryGetValue("AutoTriggerDpia", out var atObj) && atObj is bool at)
                _autoTriggerDpia = at;

            InitializeDefaultTemplates();
            InitializeDefaultRiskCategories();
            return base.InitializeAsync(configuration, cancellationToken);
        }

        /// <summary>
        /// Performs threshold analysis to determine if DPIA is required.
        /// </summary>
        public ThresholdAnalysisResult AnalyzeThreshold(ThresholdAnalysisInput input)
        {
            ArgumentNullException.ThrowIfNull(input);

            var triggers = new List<DpiaTrigger>();
            var score = 0.0;

            // Check GDPR Article 35(3) mandatory triggers
            if (input.InvolvesAutomatedDecisionMaking && input.HasLegalEffects)
            {
                triggers.Add(new DpiaTrigger
                {
                    TriggerId = "profiling-legal-effects",
                    Name = "Systematic Profiling with Legal Effects",
                    Regulation = "GDPR Article 35(3)(a)",
                    IsMandatory = true
                });
                score += 10;
            }

            if (input.InvolvesSpecialCategories && input.IsLargeScale)
            {
                triggers.Add(new DpiaTrigger
                {
                    TriggerId = "special-categories-large-scale",
                    Name = "Large-Scale Special Category Processing",
                    Regulation = "GDPR Article 35(3)(b)",
                    IsMandatory = true
                });
                score += 10;
            }

            if (input.InvolvesPublicMonitoring)
            {
                triggers.Add(new DpiaTrigger
                {
                    TriggerId = "public-monitoring",
                    Name = "Systematic Public Area Monitoring",
                    Regulation = "GDPR Article 35(3)(c)",
                    IsMandatory = true
                });
                score += 10;
            }

            // Additional risk factors (WP29 Guidelines)
            if (input.InvolvesVulnerableSubjects)
            {
                triggers.Add(new DpiaTrigger
                {
                    TriggerId = "vulnerable-subjects",
                    Name = "Processing of Vulnerable Subjects",
                    Regulation = "WP29 Guidelines",
                    IsMandatory = false
                });
                score += 5;
            }

            if (input.InvolvesNewTechnology)
            {
                triggers.Add(new DpiaTrigger
                {
                    TriggerId = "new-technology",
                    Name = "Use of New or Innovative Technology",
                    Regulation = "GDPR Article 35(1)",
                    IsMandatory = false
                });
                score += 3;
            }

            if (input.InvolvesDataMatching)
            {
                triggers.Add(new DpiaTrigger
                {
                    TriggerId = "data-matching",
                    Name = "Data Matching or Combining",
                    Regulation = "WP29 Guidelines",
                    IsMandatory = false
                });
                score += 3;
            }

            if (input.InvolvesInvisibleProcessing)
            {
                triggers.Add(new DpiaTrigger
                {
                    TriggerId = "invisible-processing",
                    Name = "Invisible or Unexpected Processing",
                    Regulation = "WP29 Guidelines",
                    IsMandatory = false
                });
                score += 4;
            }

            if (input.PreventsBenefits)
            {
                triggers.Add(new DpiaTrigger
                {
                    TriggerId = "prevents-benefits",
                    Name = "Processing Prevents Exercise of Rights",
                    Regulation = "WP29 Guidelines",
                    IsMandatory = false
                });
                score += 4;
            }

            if (input.InvolvesCrossBorderTransfer)
            {
                triggers.Add(new DpiaTrigger
                {
                    TriggerId = "cross-border",
                    Name = "Cross-Border Data Transfer",
                    Regulation = "GDPR Chapter V",
                    IsMandatory = false
                });
                score += 2;
            }

            var hasMandatoryTrigger = triggers.Any(t => t.IsMandatory);
            var dpiaRequired = hasMandatoryTrigger || score >= 6; // WP29: 2+ criteria = likely DPIA

            return new ThresholdAnalysisResult
            {
                DpiaRequired = dpiaRequired,
                RiskScore = score,
                Triggers = triggers,
                Recommendation = dpiaRequired
                    ? "DPIA is required before proceeding with processing"
                    : "DPIA is recommended but not mandatory",
                AnalyzedAt = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Creates a new Privacy Impact Assessment.
        /// </summary>
        public CreateDpiaResult CreateAssessment(CreateDpiaInput input)
        {
            ArgumentNullException.ThrowIfNull(input);

            var assessmentId = Guid.NewGuid().ToString();
            var assessment = new PrivacyImpactAssessment
            {
                AssessmentId = assessmentId,
                Name = input.Name,
                Description = input.Description,
                ProjectId = input.ProjectId,
                Status = DpiaStatus.Draft,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = input.CreatedBy,
                TemplateId = input.TemplateId,
                ProcessingDescription = input.ProcessingDescription,
                ProcessingPurpose = input.ProcessingPurpose,
                DataCategories = input.DataCategories ?? Array.Empty<string>(),
                DataSubjects = input.DataSubjects ?? Array.Empty<string>(),
                LawfulBasis = input.LawfulBasis,
                Risks = new List<IdentifiedRisk>(),
                Mitigations = new List<RiskMitigation>()
            };

            _assessments[assessmentId] = assessment;

            _auditLog.Add(new DpiaAuditEntry
            {
                EntryId = Guid.NewGuid().ToString(),
                AssessmentId = assessmentId,
                Action = DpiaAction.Created,
                Timestamp = DateTime.UtcNow,
                PerformedBy = input.CreatedBy
            });

            return new CreateDpiaResult
            {
                Success = true,
                AssessmentId = assessmentId,
                Status = DpiaStatus.Draft
            };
        }

        /// <summary>
        /// Adds an identified risk to the assessment.
        /// </summary>
        public AddRiskResult AddRisk(string assessmentId, IdentifiedRiskInput input)
        {
            if (!_assessments.TryGetValue(assessmentId, out var assessment))
            {
                return new AddRiskResult
                {
                    Success = false,
                    ErrorMessage = "Assessment not found"
                };
            }

            if (assessment.Status == DpiaStatus.Approved || assessment.Status == DpiaStatus.Closed)
            {
                return new AddRiskResult
                {
                    Success = false,
                    ErrorMessage = "Cannot modify approved or closed assessment"
                };
            }

            var riskId = Guid.NewGuid().ToString();
            var riskScore = CalculateRiskScore(input.Likelihood, input.Impact);

            var risk = new IdentifiedRisk
            {
                RiskId = riskId,
                CategoryId = input.CategoryId,
                Title = input.Title,
                Description = input.Description,
                Likelihood = input.Likelihood,
                Impact = input.Impact,
                RiskScore = riskScore,
                RiskLevel = DetermineRiskLevel(riskScore),
                AffectedRights = input.AffectedRights ?? Array.Empty<string>(),
                IdentifiedAt = DateTime.UtcNow,
                IdentifiedBy = input.IdentifiedBy
            };

            var risks = assessment.Risks.ToList();
            risks.Add(risk);

            _assessments[assessmentId] = assessment with
            {
                Risks = risks,
                LastModifiedAt = DateTime.UtcNow
            };

            _auditLog.Add(new DpiaAuditEntry
            {
                EntryId = Guid.NewGuid().ToString(),
                AssessmentId = assessmentId,
                Action = DpiaAction.RiskAdded,
                Timestamp = DateTime.UtcNow,
                PerformedBy = input.IdentifiedBy,
                Details = $"Risk '{input.Title}' added with score {riskScore:F1}"
            });

            return new AddRiskResult
            {
                Success = true,
                RiskId = riskId,
                RiskScore = riskScore,
                RiskLevel = risk.RiskLevel
            };
        }

        /// <summary>
        /// Adds a mitigation measure for a risk.
        /// </summary>
        public AddMitigationResult AddMitigation(string assessmentId, string riskId, MitigationInput input)
        {
            if (!_assessments.TryGetValue(assessmentId, out var assessment))
            {
                return new AddMitigationResult
                {
                    Success = false,
                    ErrorMessage = "Assessment not found"
                };
            }

            var risk = assessment.Risks.FirstOrDefault(r => r.RiskId == riskId);
            if (risk == null)
            {
                return new AddMitigationResult
                {
                    Success = false,
                    ErrorMessage = "Risk not found"
                };
            }

            var mitigationId = Guid.NewGuid().ToString();
            var residualScore = CalculateResidualRisk(risk.RiskScore, input.EffectivenessRating);

            var mitigation = new RiskMitigation
            {
                MitigationId = mitigationId,
                RiskId = riskId,
                Title = input.Title,
                Description = input.Description,
                MitigationType = input.MitigationType,
                EffectivenessRating = input.EffectivenessRating,
                ImplementationStatus = input.ImplementationStatus,
                ResponsibleParty = input.ResponsibleParty,
                TargetDate = input.TargetDate,
                ResidualRiskScore = residualScore,
                ResidualRiskLevel = DetermineRiskLevel(residualScore),
                CreatedAt = DateTime.UtcNow
            };

            var mitigations = assessment.Mitigations.ToList();
            mitigations.Add(mitigation);

            _assessments[assessmentId] = assessment with
            {
                Mitigations = mitigations,
                LastModifiedAt = DateTime.UtcNow
            };

            _auditLog.Add(new DpiaAuditEntry
            {
                EntryId = Guid.NewGuid().ToString(),
                AssessmentId = assessmentId,
                Action = DpiaAction.MitigationAdded,
                Timestamp = DateTime.UtcNow,
                PerformedBy = input.ResponsibleParty,
                Details = $"Mitigation '{input.Title}' added for risk {riskId}"
            });

            return new AddMitigationResult
            {
                Success = true,
                MitigationId = mitigationId,
                ResidualRiskScore = residualScore,
                ResidualRiskLevel = mitigation.ResidualRiskLevel
            };
        }

        /// <summary>
        /// Calculates the overall assessment result.
        /// </summary>
        public AssessmentCalculationResult CalculateAssessment(string assessmentId)
        {
            if (!_assessments.TryGetValue(assessmentId, out var assessment))
            {
                return new AssessmentCalculationResult
                {
                    Success = false,
                    ErrorMessage = "Assessment not found"
                };
            }

            if (assessment.Risks.Count == 0)
            {
                return new AssessmentCalculationResult
                {
                    Success = false,
                    ErrorMessage = "No risks have been identified"
                };
            }

            // Calculate inherent risk
            var totalInherentRisk = assessment.Risks.Sum(r => r.RiskScore);
            var maxInherentRisk = assessment.Risks.Max(r => r.RiskScore);
            var averageInherentRisk = assessment.Risks.Average(r => r.RiskScore);

            // Calculate residual risk (after mitigations)
            var residualRisks = assessment.Risks.Select(r =>
            {
                var mitigations = assessment.Mitigations.Where(m => m.RiskId == r.RiskId).ToList();
                if (mitigations.Count == 0)
                    return r.RiskScore;

                // Apply best mitigation
                var bestMitigation = mitigations.OrderBy(m => m.ResidualRiskScore).First();
                return bestMitigation.ResidualRiskScore;
            }).ToList();

            var totalResidualRisk = residualRisks.Sum();
            var maxResidualRisk = residualRisks.Max();
            var averageResidualRisk = residualRisks.Average();

            // Determine if DPA consultation is needed
            var requiresConsultation = maxResidualRisk >= _consultationThreshold;

            // Determine overall risk level
            var overallRiskLevel = DetermineRiskLevel(maxResidualRisk);

            // Generate recommendations
            var recommendations = GenerateRecommendations(assessment, maxResidualRisk, requiresConsultation);

            var result = new AssessmentCalculationResult
            {
                Success = true,
                InherentRiskSummary = new RiskSummary
                {
                    TotalScore = totalInherentRisk,
                    MaxScore = maxInherentRisk,
                    AverageScore = averageInherentRisk,
                    HighRiskCount = assessment.Risks.Count(r => r.RiskLevel >= RiskLevel.High)
                },
                ResidualRiskSummary = new RiskSummary
                {
                    TotalScore = totalResidualRisk,
                    MaxScore = maxResidualRisk,
                    AverageScore = averageResidualRisk,
                    HighRiskCount = residualRisks.Count(r => r >= _highRiskThreshold)
                },
                OverallRiskLevel = overallRiskLevel,
                RequiresDpaConsultation = requiresConsultation,
                Recommendations = recommendations
            };

            // Update assessment
            _assessments[assessmentId] = assessment with
            {
                InherentRiskScore = maxInherentRisk,
                ResidualRiskScore = maxResidualRisk,
                RequiresDpaConsultation = requiresConsultation,
                OverallRiskLevel = overallRiskLevel,
                LastCalculatedAt = DateTime.UtcNow
            };

            return result;
        }

        /// <summary>
        /// Submits assessment for review.
        /// </summary>
        public StatusChangeResult SubmitForReview(string assessmentId, string submittedBy)
        {
            if (!_assessments.TryGetValue(assessmentId, out var assessment))
            {
                return new StatusChangeResult
                {
                    Success = false,
                    ErrorMessage = "Assessment not found"
                };
            }

            if (assessment.Status != DpiaStatus.Draft)
            {
                return new StatusChangeResult
                {
                    Success = false,
                    ErrorMessage = $"Cannot submit assessment in {assessment.Status} status"
                };
            }

            // Validate assessment completeness
            if (assessment.Risks.Count == 0)
            {
                return new StatusChangeResult
                {
                    Success = false,
                    ErrorMessage = "Assessment must have at least one identified risk"
                };
            }

            _assessments[assessmentId] = assessment with
            {
                Status = DpiaStatus.UnderReview,
                SubmittedAt = DateTime.UtcNow,
                SubmittedBy = submittedBy
            };

            _auditLog.Add(new DpiaAuditEntry
            {
                EntryId = Guid.NewGuid().ToString(),
                AssessmentId = assessmentId,
                Action = DpiaAction.SubmittedForReview,
                Timestamp = DateTime.UtcNow,
                PerformedBy = submittedBy
            });

            return new StatusChangeResult
            {
                Success = true,
                AssessmentId = assessmentId,
                NewStatus = DpiaStatus.UnderReview
            };
        }

        /// <summary>
        /// Approves an assessment.
        /// </summary>
        public StatusChangeResult ApproveAssessment(string assessmentId, string approvedBy, string? comments = null)
        {
            if (!_assessments.TryGetValue(assessmentId, out var assessment))
            {
                return new StatusChangeResult
                {
                    Success = false,
                    ErrorMessage = "Assessment not found"
                };
            }

            if (assessment.Status != DpiaStatus.UnderReview)
            {
                return new StatusChangeResult
                {
                    Success = false,
                    ErrorMessage = $"Cannot approve assessment in {assessment.Status} status"
                };
            }

            _assessments[assessmentId] = assessment with
            {
                Status = DpiaStatus.Approved,
                ApprovedAt = DateTime.UtcNow,
                ApprovedBy = approvedBy,
                ApprovalComments = comments
            };

            _auditLog.Add(new DpiaAuditEntry
            {
                EntryId = Guid.NewGuid().ToString(),
                AssessmentId = assessmentId,
                Action = DpiaAction.Approved,
                Timestamp = DateTime.UtcNow,
                PerformedBy = approvedBy,
                Details = comments
            });

            return new StatusChangeResult
            {
                Success = true,
                AssessmentId = assessmentId,
                NewStatus = DpiaStatus.Approved
            };
        }

        /// <summary>
        /// Gets all assessments.
        /// </summary>
        public IReadOnlyList<PrivacyImpactAssessment> GetAssessments(DpiaStatus? status = null)
        {
            var query = _assessments.Values.AsEnumerable();
            if (status.HasValue)
                query = query.Where(a => a.Status == status.Value);

            return query.OrderByDescending(a => a.CreatedAt).ToList();
        }

        /// <summary>
        /// Gets audit log entries.
        /// </summary>
        public IReadOnlyList<DpiaAuditEntry> GetAuditLog(string? assessmentId = null, int count = 100)
        {
            var query = _auditLog.AsEnumerable();
            if (!string.IsNullOrEmpty(assessmentId))
                query = query.Where(e => e.AssessmentId == assessmentId);

            return query.OrderByDescending(e => e.Timestamp).Take(count).ToList();
        }

        /// <inheritdoc/>
        protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
        {
            IncrementCounter("privacy_impact_assessment.check");
            var violations = new List<ComplianceViolation>();
            var recommendations = new List<string>();

            // Check if DPIA is required
            if (context.Attributes.TryGetValue("ProjectId", out var projObj) && projObj is string projectId)
            {
                var assessment = _assessments.Values.FirstOrDefault(a => a.ProjectId == projectId);

                if (assessment == null)
                {
                    // Check if DPIA should be required
                    var thresholdInput = ExtractThresholdInput(context);
                    var thresholdResult = AnalyzeThreshold(thresholdInput);

                    if (thresholdResult.DpiaRequired)
                    {
                        violations.Add(new ComplianceViolation
                        {
                            Code = "DPIA-001",
                            Description = "DPIA required but not conducted",
                            Severity = ViolationSeverity.Critical,
                            Remediation = "Conduct DPIA before proceeding with processing",
                            RegulatoryReference = "GDPR Article 35"
                        });
                    }
                }
                else
                {
                    // Check assessment status
                    if (assessment.Status == DpiaStatus.Draft)
                    {
                        recommendations.Add($"DPIA for project '{projectId}' is still in draft status");
                    }

                    if (assessment.RequiresDpaConsultation && !assessment.DpaConsultationCompleted)
                    {
                        violations.Add(new ComplianceViolation
                        {
                            Code = "DPIA-002",
                            Description = "DPA consultation required but not completed",
                            Severity = ViolationSeverity.High,
                            Remediation = "Consult with supervisory authority before proceeding",
                            RegulatoryReference = "GDPR Article 36"
                        });
                    }
                }
            }

            // Check for stale assessments
            var staleAssessments = _assessments.Values
                .Where(a => a.Status == DpiaStatus.Approved &&
                           a.ApprovedAt.HasValue &&
                           DateTime.UtcNow - a.ApprovedAt.Value > TimeSpan.FromDays(365))
                .ToList();

            if (staleAssessments.Count > 0)
            {
                recommendations.Add($"{staleAssessments.Count} DPIA(s) are over 1 year old and should be reviewed");
            }

            var hasHighViolations = violations.Any(v => v.Severity >= ViolationSeverity.High);
            var isCompliant = !hasHighViolations;
            var status = violations.Count == 0 ? ComplianceStatus.Compliant :
                        hasHighViolations ? ComplianceStatus.NonCompliant :
                        ComplianceStatus.PartiallyCompliant;

            return Task.FromResult(new ComplianceResult
            {
                IsCompliant = isCompliant,
                Framework = Framework,
                Status = status,
                Violations = violations,
                Recommendations = recommendations,
                Metadata = new Dictionary<string, object>
                {
                    ["TotalAssessments"] = _assessments.Count,
                    ["ApprovedAssessments"] = _assessments.Values.Count(a => a.Status == DpiaStatus.Approved),
                    ["AwaitingConsultation"] = _assessments.Values.Count(a => a.RequiresDpaConsultation && !a.DpaConsultationCompleted)
                }
            });
        }

        private double CalculateRiskScore(int likelihood, int impact)
        {
            // 5x5 risk matrix
            return likelihood * impact;
        }

        private double CalculateResidualRisk(double inherentRisk, MitigationEffectiveness effectiveness)
        {
            var reductionFactor = effectiveness switch
            {
                MitigationEffectiveness.VeryHigh => 0.8,
                MitigationEffectiveness.High => 0.6,
                MitigationEffectiveness.Medium => 0.4,
                MitigationEffectiveness.Low => 0.2,
                MitigationEffectiveness.VeryLow => 0.1,
                _ => 0.3
            };
            return inherentRisk * (1 - reductionFactor);
        }

        private RiskLevel DetermineRiskLevel(double score)
        {
            return score switch
            {
                >= 20 => RiskLevel.Critical,
                >= 15 => RiskLevel.High,
                >= 10 => RiskLevel.Medium,
                >= 5 => RiskLevel.Low,
                _ => RiskLevel.VeryLow
            };
        }

        private List<string> GenerateRecommendations(
            PrivacyImpactAssessment assessment,
            double maxResidualRisk,
            bool requiresConsultation)
        {
            var recommendations = new List<string>();

            if (requiresConsultation)
            {
                recommendations.Add("MANDATORY: Consult with Data Protection Authority before proceeding (GDPR Article 36)");
            }

            // Check for unmitigated high risks
            var unmitigatedHighRisks = assessment.Risks
                .Where(r => r.RiskLevel >= RiskLevel.High)
                .Where(r => !assessment.Mitigations.Any(m => m.RiskId == r.RiskId))
                .ToList();

            if (unmitigatedHighRisks.Count > 0)
            {
                recommendations.Add($"Address {unmitigatedHighRisks.Count} high/critical risks without mitigation measures");
            }

            // Check for incomplete mitigations
            var incompleteMitigations = assessment.Mitigations
                .Where(m => m.ImplementationStatus != ImplementationStatus.Completed)
                .ToList();

            if (incompleteMitigations.Count > 0)
            {
                recommendations.Add($"Complete implementation of {incompleteMitigations.Count} pending mitigation measures");
            }

            // Check for special category data
            if (assessment.DataCategories.Any(c =>
                c.Contains("health", StringComparison.OrdinalIgnoreCase) ||
                c.Contains("biometric", StringComparison.OrdinalIgnoreCase) ||
                c.Contains("genetic", StringComparison.OrdinalIgnoreCase)))
            {
                recommendations.Add("Ensure explicit consent or Article 9 exemption for special category data");
            }

            return recommendations;
        }

        private ThresholdAnalysisInput ExtractThresholdInput(ComplianceContext context)
        {
            return new ThresholdAnalysisInput
            {
                InvolvesAutomatedDecisionMaking = context.Attributes.GetValueOrDefault("InvolvesAutomatedDecisionMaking") as bool? ?? false,
                HasLegalEffects = context.Attributes.GetValueOrDefault("HasLegalEffects") as bool? ?? false,
                InvolvesSpecialCategories = context.DataSubjectCategories.Any(c =>
                    c.Contains("health") || c.Contains("biometric") || c.Contains("genetic")),
                IsLargeScale = context.Attributes.GetValueOrDefault("IsLargeScale") as bool? ?? false,
                InvolvesPublicMonitoring = context.Attributes.GetValueOrDefault("InvolvesPublicMonitoring") as bool? ?? false,
                InvolvesVulnerableSubjects = context.Attributes.GetValueOrDefault("InvolvesVulnerableSubjects") as bool? ?? false,
                InvolvesNewTechnology = context.Attributes.GetValueOrDefault("InvolvesNewTechnology") as bool? ?? false
            };
        }

        private void InitializeDefaultTemplates()
        {
            _templates["standard"] = new DpiaTemplate
            {
                TemplateId = "standard",
                Name = "Standard DPIA Template",
                Description = "Standard template for GDPR DPIA",
                Sections = new[] { "Processing Description", "Necessity Assessment", "Risk Assessment", "Mitigation Measures" }
            };
        }

        private void InitializeDefaultRiskCategories()
        {
            _riskCategories["data-breach"] = new RiskCategory { CategoryId = "data-breach", Name = "Data Breach", Description = "Risk of unauthorized access or disclosure" };
            _riskCategories["purpose-creep"] = new RiskCategory { CategoryId = "purpose-creep", Name = "Purpose Creep", Description = "Risk of data being used beyond original purpose" };
            _riskCategories["accuracy"] = new RiskCategory { CategoryId = "accuracy", Name = "Data Accuracy", Description = "Risk of inaccurate data causing harm" };
            _riskCategories["retention"] = new RiskCategory { CategoryId = "retention", Name = "Excessive Retention", Description = "Risk of keeping data longer than necessary" };
            _riskCategories["rights-violation"] = new RiskCategory { CategoryId = "rights-violation", Name = "Rights Violation", Description = "Risk of infringing data subject rights" };
            _riskCategories["discrimination"] = new RiskCategory { CategoryId = "discrimination", Name = "Discrimination", Description = "Risk of discriminatory outcomes" };
            _riskCategories["surveillance"] = new RiskCategory { CategoryId = "surveillance", Name = "Surveillance", Description = "Risk of intrusive monitoring" };
        }
    
    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("privacy_impact_assessment.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("privacy_impact_assessment.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }
}

    #region Types

    /// <summary>
    /// DPIA status.
    /// </summary>
    public enum DpiaStatus
    {
        Draft,
        UnderReview,
        Approved,
        Rejected,
        Closed
    }

    /// <summary>
    /// Risk level.
    /// </summary>
    public enum RiskLevel
    {
        VeryLow = 1,
        Low = 2,
        Medium = 3,
        High = 4,
        Critical = 5
    }

    /// <summary>
    /// Mitigation effectiveness rating.
    /// </summary>
    public enum MitigationEffectiveness
    {
        VeryLow = 1,
        Low = 2,
        Medium = 3,
        High = 4,
        VeryHigh = 5
    }

    /// <summary>
    /// Mitigation type.
    /// </summary>
    public enum MitigationType
    {
        Technical,
        Organizational,
        Legal,
        Contractual
    }

    /// <summary>
    /// Implementation status.
    /// </summary>
    public enum ImplementationStatus
    {
        Planned,
        InProgress,
        Completed,
        Verified
    }

    /// <summary>
    /// DPIA action types for audit.
    /// </summary>
    public enum DpiaAction
    {
        Created,
        RiskAdded,
        RiskUpdated,
        MitigationAdded,
        SubmittedForReview,
        Approved,
        Rejected,
        Closed
    }

    /// <summary>
    /// Privacy Impact Assessment.
    /// </summary>
    public sealed record PrivacyImpactAssessment
    {
        public required string AssessmentId { get; init; }
        public required string Name { get; init; }
        public string? Description { get; init; }
        public string? ProjectId { get; init; }
        public required DpiaStatus Status { get; init; }
        public required DateTime CreatedAt { get; init; }
        public required string CreatedBy { get; init; }
        public DateTime? LastModifiedAt { get; init; }
        public string? TemplateId { get; init; }
        public string? ProcessingDescription { get; init; }
        public string? ProcessingPurpose { get; init; }
        public required string[] DataCategories { get; init; }
        public required string[] DataSubjects { get; init; }
        public string? LawfulBasis { get; init; }
        public required IReadOnlyList<IdentifiedRisk> Risks { get; init; }
        public required IReadOnlyList<RiskMitigation> Mitigations { get; init; }
        public double InherentRiskScore { get; init; }
        public double ResidualRiskScore { get; init; }
        public RiskLevel OverallRiskLevel { get; init; }
        public bool RequiresDpaConsultation { get; init; }
        public bool DpaConsultationCompleted { get; init; }
        public DateTime? LastCalculatedAt { get; init; }
        public DateTime? SubmittedAt { get; init; }
        public string? SubmittedBy { get; init; }
        public DateTime? ApprovedAt { get; init; }
        public string? ApprovedBy { get; init; }
        public string? ApprovalComments { get; init; }
    }

    /// <summary>
    /// Identified risk.
    /// </summary>
    public sealed record IdentifiedRisk
    {
        public required string RiskId { get; init; }
        public required string CategoryId { get; init; }
        public required string Title { get; init; }
        public string? Description { get; init; }
        public required int Likelihood { get; init; }
        public required int Impact { get; init; }
        public required double RiskScore { get; init; }
        public required RiskLevel RiskLevel { get; init; }
        public required string[] AffectedRights { get; init; }
        public required DateTime IdentifiedAt { get; init; }
        public string? IdentifiedBy { get; init; }
    }

    /// <summary>
    /// Risk mitigation.
    /// </summary>
    public sealed record RiskMitigation
    {
        public required string MitigationId { get; init; }
        public required string RiskId { get; init; }
        public required string Title { get; init; }
        public string? Description { get; init; }
        public required MitigationType MitigationType { get; init; }
        public required MitigationEffectiveness EffectivenessRating { get; init; }
        public required ImplementationStatus ImplementationStatus { get; init; }
        public string? ResponsibleParty { get; init; }
        public DateTime? TargetDate { get; init; }
        public required double ResidualRiskScore { get; init; }
        public required RiskLevel ResidualRiskLevel { get; init; }
        public required DateTime CreatedAt { get; init; }
    }

    /// <summary>
    /// Threshold analysis input.
    /// </summary>
    public sealed record ThresholdAnalysisInput
    {
        public bool InvolvesAutomatedDecisionMaking { get; init; }
        public bool HasLegalEffects { get; init; }
        public bool InvolvesSpecialCategories { get; init; }
        public bool IsLargeScale { get; init; }
        public bool InvolvesPublicMonitoring { get; init; }
        public bool InvolvesVulnerableSubjects { get; init; }
        public bool InvolvesNewTechnology { get; init; }
        public bool InvolvesDataMatching { get; init; }
        public bool InvolvesInvisibleProcessing { get; init; }
        public bool PreventsBenefits { get; init; }
        public bool InvolvesCrossBorderTransfer { get; init; }
    }

    /// <summary>
    /// Threshold analysis result.
    /// </summary>
    public sealed record ThresholdAnalysisResult
    {
        public required bool DpiaRequired { get; init; }
        public double RiskScore { get; init; }
        public required IReadOnlyList<DpiaTrigger> Triggers { get; init; }
        public required string Recommendation { get; init; }
        public DateTime AnalyzedAt { get; init; }
    }

    /// <summary>
    /// DPIA trigger.
    /// </summary>
    public sealed record DpiaTrigger
    {
        public required string TriggerId { get; init; }
        public required string Name { get; init; }
        public required string Regulation { get; init; }
        public required bool IsMandatory { get; init; }
    }

    /// <summary>
    /// Create DPIA input.
    /// </summary>
    public sealed record CreateDpiaInput
    {
        public required string Name { get; init; }
        public string? Description { get; init; }
        public string? ProjectId { get; init; }
        public required string CreatedBy { get; init; }
        public string? TemplateId { get; init; }
        public string? ProcessingDescription { get; init; }
        public string? ProcessingPurpose { get; init; }
        public string[]? DataCategories { get; init; }
        public string[]? DataSubjects { get; init; }
        public string? LawfulBasis { get; init; }
    }

    /// <summary>
    /// Create DPIA result.
    /// </summary>
    public sealed record CreateDpiaResult
    {
        public required bool Success { get; init; }
        public string? AssessmentId { get; init; }
        public DpiaStatus Status { get; init; }
        public string? ErrorMessage { get; init; }
    }

    /// <summary>
    /// Identified risk input.
    /// </summary>
    public sealed record IdentifiedRiskInput
    {
        public required string CategoryId { get; init; }
        public required string Title { get; init; }
        public string? Description { get; init; }
        public required int Likelihood { get; init; }
        public required int Impact { get; init; }
        public string[]? AffectedRights { get; init; }
        public string? IdentifiedBy { get; init; }
    }

    /// <summary>
    /// Add risk result.
    /// </summary>
    public sealed record AddRiskResult
    {
        public required bool Success { get; init; }
        public string? RiskId { get; init; }
        public double RiskScore { get; init; }
        public RiskLevel RiskLevel { get; init; }
        public string? ErrorMessage { get; init; }
    }

    /// <summary>
    /// Mitigation input.
    /// </summary>
    public sealed record MitigationInput
    {
        public required string Title { get; init; }
        public string? Description { get; init; }
        public required MitigationType MitigationType { get; init; }
        public required MitigationEffectiveness EffectivenessRating { get; init; }
        public required ImplementationStatus ImplementationStatus { get; init; }
        public string? ResponsibleParty { get; init; }
        public DateTime? TargetDate { get; init; }
    }

    /// <summary>
    /// Add mitigation result.
    /// </summary>
    public sealed record AddMitigationResult
    {
        public required bool Success { get; init; }
        public string? MitigationId { get; init; }
        public double ResidualRiskScore { get; init; }
        public RiskLevel ResidualRiskLevel { get; init; }
        public string? ErrorMessage { get; init; }
    }

    /// <summary>
    /// Assessment calculation result.
    /// </summary>
    public sealed record AssessmentCalculationResult
    {
        public required bool Success { get; init; }
        public RiskSummary? InherentRiskSummary { get; init; }
        public RiskSummary? ResidualRiskSummary { get; init; }
        public RiskLevel OverallRiskLevel { get; init; }
        public bool RequiresDpaConsultation { get; init; }
        public IReadOnlyList<string>? Recommendations { get; init; }
        public string? ErrorMessage { get; init; }
    }

    /// <summary>
    /// Risk summary.
    /// </summary>
    public sealed record RiskSummary
    {
        public double TotalScore { get; init; }
        public double MaxScore { get; init; }
        public double AverageScore { get; init; }
        public int HighRiskCount { get; init; }
    }

    /// <summary>
    /// Status change result.
    /// </summary>
    public sealed record StatusChangeResult
    {
        public required bool Success { get; init; }
        public string? AssessmentId { get; init; }
        public DpiaStatus NewStatus { get; init; }
        public string? ErrorMessage { get; init; }
    }

    /// <summary>
    /// DPIA template.
    /// </summary>
    public sealed record DpiaTemplate
    {
        public required string TemplateId { get; init; }
        public required string Name { get; init; }
        public string? Description { get; init; }
        public required string[] Sections { get; init; }
    }

    /// <summary>
    /// Risk category.
    /// </summary>
    public sealed record RiskCategory
    {
        public required string CategoryId { get; init; }
        public required string Name { get; init; }
        public string? Description { get; init; }
    }

    /// <summary>
    /// DPIA audit entry.
    /// </summary>
    public sealed record DpiaAuditEntry
    {
        public required string EntryId { get; init; }
        public required string AssessmentId { get; init; }
        public required DpiaAction Action { get; init; }
        public required DateTime Timestamp { get; init; }
        public string? PerformedBy { get; init; }
        public string? Details { get; init; }
    }

    #endregion
}
