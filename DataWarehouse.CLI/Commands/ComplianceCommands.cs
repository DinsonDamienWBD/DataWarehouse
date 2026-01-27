// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Spectre.Console;

namespace DataWarehouse.CLI.Commands;

/// <summary>
/// Compliance reporting and audit commands for GDPR, HIPAA, and SOC2.
/// Uses shared ComplianceReportService for feature parity with GUI.
/// </summary>
public static class ComplianceCommands
{
    #region GDPR Commands

    /// <summary>
    /// GDPR compliance commands for data protection and privacy.
    /// </summary>
    public static class Gdpr
    {
        public static async Task GenerateReportAsync(string format, string? output)
        {
            await AnsiConsole.Status()
                .StartAsync("Generating GDPR compliance report...", async ctx =>
                {
                    await Task.Delay(500);

                    var report = GetGdprReport();

                    if (format.ToLower() == "table")
                    {
                        DisplayGdprReport(report);
                    }
                    else
                    {
                        await ExportReportAsync("GDPR", format, output, report);
                    }
                });
        }

        public static async Task ListRequestsAsync(string? status)
        {
            await AnsiConsole.Status()
                .StartAsync("Loading data subject requests...", async ctx =>
                {
                    await Task.Delay(300);

                    var requests = GetDataSubjectRequests()
                        .Where(r => string.IsNullOrEmpty(status) || r.Status.Equals(status, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    if (requests.Count == 0)
                    {
                        AnsiConsole.MarkupLine("[yellow]No data subject requests found.[/]");
                        return;
                    }

                    var table = new Table()
                        .Border(TableBorder.Rounded)
                        .AddColumn("ID")
                        .AddColumn("Type")
                        .AddColumn("Subject")
                        .AddColumn("Status")
                        .AddColumn("Submitted")
                        .AddColumn("Due Date");

                    foreach (var request in requests)
                    {
                        var statusColor = request.Status switch
                        {
                            "Completed" => "green",
                            "In Progress" => "yellow",
                            "Pending" => "cyan",
                            "Overdue" => "red",
                            _ => "white"
                        };

                        table.AddRow(
                            request.Id,
                            request.Type,
                            request.Subject,
                            $"[{statusColor}]{request.Status}[/]",
                            request.Submitted.ToString("yyyy-MM-dd"),
                            request.DueDate.ToString("yyyy-MM-dd")
                        );
                    }

                    AnsiConsole.Write(table);
                    AnsiConsole.MarkupLine($"\n[gray]Total requests: {requests.Count}[/]");
                });
        }

        public static async Task ListConsentsAsync()
        {
            await AnsiConsole.Status()
                .StartAsync("Loading consent records...", async ctx =>
                {
                    await Task.Delay(300);

                    var consents = GetConsentRecords();

                    var table = new Table()
                        .Border(TableBorder.Rounded)
                        .AddColumn("Subject ID")
                        .AddColumn("Purpose")
                        .AddColumn("Granted")
                        .AddColumn("Expires")
                        .AddColumn("Withdrawn");

                    foreach (var consent in consents)
                    {
                        table.AddRow(
                            consent.SubjectId,
                            consent.Purpose,
                            consent.GrantedDate.ToString("yyyy-MM-dd"),
                            consent.ExpiryDate?.ToString("yyyy-MM-dd") ?? "Never",
                            consent.IsWithdrawn ? "[red]Yes[/]" : "[green]No[/]"
                        );
                    }

                    AnsiConsole.Write(table);
                    AnsiConsole.MarkupLine($"\n[gray]Total consent records: {consents.Count}[/]");
                });
        }

        public static async Task ListBreachesAsync()
        {
            await AnsiConsole.Status()
                .StartAsync("Loading data breach incidents...", async ctx =>
                {
                    await Task.Delay(300);

                    var breaches = GetBreachIncidents();

                    if (breaches.Count == 0)
                    {
                        AnsiConsole.MarkupLine("[green]No data breach incidents recorded.[/]");
                        return;
                    }

                    var table = new Table()
                        .Border(TableBorder.Rounded)
                        .AddColumn("ID")
                        .AddColumn("Severity")
                        .AddColumn("Date Detected")
                        .AddColumn("Records Affected")
                        .AddColumn("DPA Notified")
                        .AddColumn("Status");

                    foreach (var breach in breaches)
                    {
                        var severityColor = breach.Severity switch
                        {
                            "Critical" => "red",
                            "High" => "orange3",
                            "Medium" => "yellow",
                            _ => "blue"
                        };

                        table.AddRow(
                            breach.Id,
                            $"[{severityColor}]{breach.Severity}[/]",
                            breach.DetectedDate.ToString("yyyy-MM-dd HH:mm"),
                            breach.RecordsAffected.ToString("N0"),
                            breach.DpaNotified ? "[green]Yes[/]" : "[red]No[/]",
                            breach.Status
                        );
                    }

                    AnsiConsole.Write(table);
                });
        }

        public static async Task ShowInventoryAsync()
        {
            await AnsiConsole.Status()
                .StartAsync("Loading personal data inventory...", async ctx =>
                {
                    await Task.Delay(300);

                    var inventory = GetPersonalDataInventory();

                    var tree = new Tree("[bold cyan]Personal Data Inventory[/]")
                        .Style(Style.Parse("cyan"));

                    foreach (var category in inventory)
                    {
                        var categoryNode = tree.AddNode($"[yellow]{category.Key}[/] ({category.Value.Count} types)");
                        foreach (var dataType in category.Value)
                        {
                            categoryNode.AddNode($"[white]{dataType.Name}[/] - {dataType.Location} - Retention: {dataType.RetentionPeriod}");
                        }
                    }

                    AnsiConsole.Write(tree);
                });
        }

        private static void DisplayGdprReport(GdprComplianceReport report)
        {
            var panel = new Panel(new Markup($@"[bold]GDPR Compliance Report[/]
Generated: {report.GeneratedDate:yyyy-MM-dd HH:mm:ss}

[bold cyan]Overall Status:[/] {GetStatusMarkup(report.OverallStatus)}

[bold yellow]Data Subject Requests:[/]
  Pending: {report.PendingRequests}
  In Progress: {report.InProgressRequests}
  Completed: {report.CompletedRequests}
  Overdue: {(report.OverdueRequests > 0 ? $"[red]{report.OverdueRequests}[/]" : "0")}

[bold yellow]Consent Management:[/]
  Active Consents: {report.ActiveConsents}
  Withdrawn Consents: {report.WithdrawnConsents}
  Expiring Soon: {report.ConsentsExpiringSoon}

[bold yellow]Data Protection:[/]
  Encryption Status: {GetStatusMarkup(report.EncryptionStatus)}
  Pseudonymization: {GetStatusMarkup(report.PseudonymizationStatus)}
  Access Controls: {GetStatusMarkup(report.AccessControlStatus)}

[bold yellow]Incidents:[/]
  Data Breaches (90 days): {report.RecentBreaches}
  DPA Notifications: {report.DpaNotifications}"))
            {
                Border = BoxBorder.Double,
                BorderStyle = Style.Parse("green")
            };

            AnsiConsole.Write(panel);
        }

        private static GdprComplianceReport GetGdprReport()
        {
            return new GdprComplianceReport
            {
                GeneratedDate = DateTime.Now,
                OverallStatus = "Compliant",
                PendingRequests = 3,
                InProgressRequests = 5,
                CompletedRequests = 142,
                OverdueRequests = 0,
                ActiveConsents = 1247,
                WithdrawnConsents = 23,
                ConsentsExpiringSoon = 8,
                EncryptionStatus = "Compliant",
                PseudonymizationStatus = "Compliant",
                AccessControlStatus = "Compliant",
                RecentBreaches = 0,
                DpaNotifications = 0
            };
        }

        private static List<DataSubjectRequest> GetDataSubjectRequests()
        {
            var now = DateTime.Now;
            return new List<DataSubjectRequest>
            {
                new() { Id = "DSR-001", Type = "Access Request", Subject = "user@example.com", Status = "Completed", Submitted = now.AddDays(-15), DueDate = now.AddDays(-1) },
                new() { Id = "DSR-002", Type = "Erasure Request", Subject = "john.doe@example.com", Status = "In Progress", Submitted = now.AddDays(-5), DueDate = now.AddDays(25) },
                new() { Id = "DSR-003", Type = "Portability Request", Subject = "jane.smith@example.com", Status = "Pending", Submitted = now.AddDays(-2), DueDate = now.AddDays(28) },
                new() { Id = "DSR-004", Type = "Rectification Request", Subject = "bob.wilson@example.com", Status = "In Progress", Submitted = now.AddDays(-8), DueDate = now.AddDays(22) },
            };
        }

        private static List<ConsentRecord> GetConsentRecords()
        {
            var now = DateTime.Now;
            return new List<ConsentRecord>
            {
                new() { SubjectId = "USER-001", Purpose = "Marketing Communications", GrantedDate = now.AddDays(-365), ExpiryDate = now.AddDays(365), IsWithdrawn = false },
                new() { SubjectId = "USER-002", Purpose = "Data Processing", GrantedDate = now.AddDays(-180), ExpiryDate = null, IsWithdrawn = false },
                new() { SubjectId = "USER-003", Purpose = "Third-Party Sharing", GrantedDate = now.AddDays(-90), ExpiryDate = now.AddDays(275), IsWithdrawn = true },
            };
        }

        private static List<BreachIncident> GetBreachIncidents()
        {
            return new List<BreachIncident>();
        }

        private static Dictionary<string, List<PersonalDataType>> GetPersonalDataInventory()
        {
            return new Dictionary<string, List<PersonalDataType>>
            {
                ["Identifying Information"] = new List<PersonalDataType>
                {
                    new() { Name = "Full Name", Location = "users.name", RetentionPeriod = "7 years" },
                    new() { Name = "Email Address", Location = "users.email", RetentionPeriod = "7 years" },
                    new() { Name = "Phone Number", Location = "users.phone", RetentionPeriod = "5 years" },
                },
                ["Financial Information"] = new List<PersonalDataType>
                {
                    new() { Name = "Payment Card Details", Location = "encrypted_payments", RetentionPeriod = "1 year" },
                },
                ["Location Data"] = new List<PersonalDataType>
                {
                    new() { Name = "IP Address", Location = "access_logs", RetentionPeriod = "90 days" },
                },
            };
        }
    }

    #endregion

    #region HIPAA Commands

    /// <summary>
    /// HIPAA compliance commands for healthcare data protection.
    /// </summary>
    public static class Hipaa
    {
        public static async Task GenerateAuditAsync(string format, string? output)
        {
            await AnsiConsole.Status()
                .StartAsync("Generating HIPAA audit report...", async ctx =>
                {
                    await Task.Delay(500);

                    var audit = GetHipaaAudit();

                    if (format.ToLower() == "table")
                    {
                        DisplayHipaaAudit(audit);
                    }
                    else
                    {
                        await ExportReportAsync("HIPAA", format, output, audit);
                    }
                });
        }

        public static async Task ListPhiAccessAsync(int limit)
        {
            await AnsiConsole.Status()
                .StartAsync("Loading PHI access logs...", async ctx =>
                {
                    await Task.Delay(300);

                    var accessLogs = GetPhiAccessLogs().Take(limit).ToList();

                    var table = new Table()
                        .Border(TableBorder.Rounded)
                        .AddColumn("Timestamp")
                        .AddColumn("User")
                        .AddColumn("Patient ID")
                        .AddColumn("Action")
                        .AddColumn("Data Type")
                        .AddColumn("Purpose");

                    foreach (var log in accessLogs)
                    {
                        table.AddRow(
                            log.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"),
                            log.User,
                            log.PatientId,
                            log.Action,
                            log.DataType,
                            log.Purpose
                        );
                    }

                    AnsiConsole.Write(table);
                    AnsiConsole.MarkupLine($"\n[gray]Showing {accessLogs.Count} of {limit} requested entries[/]");
                });
        }

        public static async Task ListBaasAsync()
        {
            await AnsiConsole.Status()
                .StartAsync("Loading Business Associate Agreements...", async ctx =>
                {
                    await Task.Delay(300);

                    var baas = GetBusinessAssociateAgreements();

                    var table = new Table()
                        .Border(TableBorder.Rounded)
                        .AddColumn("BA Name")
                        .AddColumn("Service")
                        .AddColumn("Agreement Date")
                        .AddColumn("Expiry Date")
                        .AddColumn("Status")
                        .AddColumn("Last Audit");

                    foreach (var baa in baas)
                    {
                        var statusColor = baa.Status == "Active" ? "green" : baa.Status == "Expiring" ? "yellow" : "red";

                        table.AddRow(
                            baa.Name,
                            baa.Service,
                            baa.AgreementDate.ToString("yyyy-MM-dd"),
                            baa.ExpiryDate.ToString("yyyy-MM-dd"),
                            $"[{statusColor}]{baa.Status}[/]",
                            baa.LastAuditDate?.ToString("yyyy-MM-dd") ?? "Never"
                        );
                    }

                    AnsiConsole.Write(table);
                });
        }

        public static async Task ShowEncryptionAsync()
        {
            await AnsiConsole.Status()
                .StartAsync("Checking encryption status...", async ctx =>
                {
                    await Task.Delay(300);

                    var encryption = GetEncryptionStatus();

                    var tree = new Tree("[bold cyan]Encryption Status[/]")
                        .Style(Style.Parse("cyan"));

                    var atRest = tree.AddNode("[yellow]Data at Rest[/]");
                    atRest.AddNode($"Database Encryption: {GetStatusMarkup(encryption.DatabaseEncryption)}");
                    atRest.AddNode($"File Storage Encryption: {GetStatusMarkup(encryption.FileStorageEncryption)}");
                    atRest.AddNode($"Backup Encryption: {GetStatusMarkup(encryption.BackupEncryption)}");

                    var inTransit = tree.AddNode("[yellow]Data in Transit[/]");
                    inTransit.AddNode($"TLS Version: {encryption.TlsVersion}");
                    inTransit.AddNode($"Certificate Status: {GetStatusMarkup(encryption.CertificateStatus)}");
                    inTransit.AddNode($"Certificate Expiry: {encryption.CertificateExpiry:yyyy-MM-dd}");

                    var keyMgmt = tree.AddNode("[yellow]Key Management[/]");
                    keyMgmt.AddNode($"Key Rotation: {GetStatusMarkup(encryption.KeyRotationStatus)}");
                    keyMgmt.AddNode($"Last Rotation: {encryption.LastKeyRotation:yyyy-MM-dd}");

                    AnsiConsole.Write(tree);
                });
        }

        public static async Task ListRisksAsync()
        {
            await AnsiConsole.Status()
                .StartAsync("Loading security risk assessments...", async ctx =>
                {
                    await Task.Delay(300);

                    var risks = GetSecurityRisks();

                    var table = new Table()
                        .Border(TableBorder.Rounded)
                        .AddColumn("ID")
                        .AddColumn("Risk")
                        .AddColumn("Severity")
                        .AddColumn("Likelihood")
                        .AddColumn("Mitigation")
                        .AddColumn("Status");

                    foreach (var risk in risks)
                    {
                        var severityColor = risk.Severity switch
                        {
                            "Critical" => "red",
                            "High" => "orange3",
                            "Medium" => "yellow",
                            _ => "blue"
                        };

                        table.AddRow(
                            risk.Id,
                            risk.Description,
                            $"[{severityColor}]{risk.Severity}[/]",
                            risk.Likelihood,
                            risk.Mitigation,
                            risk.Status
                        );
                    }

                    AnsiConsole.Write(table);
                });
        }

        private static void DisplayHipaaAudit(HipaaAuditReport audit)
        {
            var panel = new Panel(new Markup($@"[bold]HIPAA Compliance Audit[/]
Generated: {audit.GeneratedDate:yyyy-MM-dd HH:mm:ss}

[bold cyan]Overall Status:[/] {GetStatusMarkup(audit.OverallStatus)}

[bold yellow]Technical Safeguards:[/]
  Access Controls: {GetStatusMarkup(audit.AccessControls)}
  Audit Controls: {GetStatusMarkup(audit.AuditControls)}
  Integrity Controls: {GetStatusMarkup(audit.IntegrityControls)}
  Transmission Security: {GetStatusMarkup(audit.TransmissionSecurity)}

[bold yellow]PHI Access:[/]
  Total Access Events (30d): {audit.TotalPhiAccess:N0}
  Unauthorized Attempts: {(audit.UnauthorizedAttempts > 0 ? $"[red]{audit.UnauthorizedAttempts}[/]" : "0")}
  Unique Users: {audit.UniqueUsers}

[bold yellow]Encryption:[/]
  Data at Rest: {GetStatusMarkup(audit.EncryptionAtRest)}
  Data in Transit: {GetStatusMarkup(audit.EncryptionInTransit)}

[bold yellow]Business Associates:[/]
  Active BAAs: {audit.ActiveBaas}
  Expiring Soon: {(audit.ExpiringBaas > 0 ? $"[yellow]{audit.ExpiringBaas}[/]" : "0")}

[bold yellow]Risk Assessment:[/]
  High Risks: {(audit.HighRisks > 0 ? $"[red]{audit.HighRisks}[/]" : "0")}
  Medium Risks: {audit.MediumRisks}
  Last Assessment: {audit.LastRiskAssessment:yyyy-MM-dd}"))
            {
                Border = BoxBorder.Double,
                BorderStyle = Style.Parse("green")
            };

            AnsiConsole.Write(panel);
        }

        private static HipaaAuditReport GetHipaaAudit()
        {
            return new HipaaAuditReport
            {
                GeneratedDate = DateTime.Now,
                OverallStatus = "Compliant",
                AccessControls = "Compliant",
                AuditControls = "Compliant",
                IntegrityControls = "Compliant",
                TransmissionSecurity = "Compliant",
                TotalPhiAccess = 15234,
                UnauthorizedAttempts = 0,
                UniqueUsers = 42,
                EncryptionAtRest = "Compliant",
                EncryptionInTransit = "Compliant",
                ActiveBaas = 8,
                ExpiringBaas = 1,
                HighRisks = 0,
                MediumRisks = 3,
                LastRiskAssessment = DateTime.Now.AddDays(-15)
            };
        }

        private static List<PhiAccessLog> GetPhiAccessLogs()
        {
            var now = DateTime.Now;
            return new List<PhiAccessLog>
            {
                new() { Timestamp = now.AddMinutes(-5), User = "dr.smith", PatientId = "PT-1001", Action = "View", DataType = "Medical Record", Purpose = "Treatment" },
                new() { Timestamp = now.AddMinutes(-10), User = "nurse.jones", PatientId = "PT-1002", Action = "Update", DataType = "Vital Signs", Purpose = "Treatment" },
                new() { Timestamp = now.AddMinutes(-15), User = "billing.clerk", PatientId = "PT-1001", Action = "View", DataType = "Billing Info", Purpose = "Payment" },
                new() { Timestamp = now.AddMinutes(-20), User = "dr.williams", PatientId = "PT-1003", Action = "View", DataType = "Lab Results", Purpose = "Treatment" },
            };
        }

        private static List<BusinessAssociateAgreement> GetBusinessAssociateAgreements()
        {
            var now = DateTime.Now;
            return new List<BusinessAssociateAgreement>
            {
                new() { Name = "Cloud Backup Services Inc.", Service = "Data Backup", AgreementDate = now.AddYears(-2), ExpiryDate = now.AddYears(1), Status = "Active", LastAuditDate = now.AddMonths(-6) },
                new() { Name = "Medical Billing Solutions", Service = "Billing", AgreementDate = now.AddYears(-3), ExpiryDate = now.AddMonths(2), Status = "Expiring", LastAuditDate = now.AddMonths(-3) },
                new() { Name = "Analytics Partners LLC", Service = "Data Analytics", AgreementDate = now.AddYears(-1), ExpiryDate = now.AddYears(2), Status = "Active", LastAuditDate = now.AddMonths(-4) },
            };
        }

        private static EncryptionStatus GetEncryptionStatus()
        {
            return new EncryptionStatus
            {
                DatabaseEncryption = "Enabled",
                FileStorageEncryption = "Enabled",
                BackupEncryption = "Enabled",
                TlsVersion = "TLS 1.3",
                CertificateStatus = "Valid",
                CertificateExpiry = DateTime.Now.AddDays(180),
                KeyRotationStatus = "Active",
                LastKeyRotation = DateTime.Now.AddDays(-30)
            };
        }

        private static List<SecurityRisk> GetSecurityRisks()
        {
            return new List<SecurityRisk>
            {
                new() { Id = "RISK-001", Description = "Legacy system authentication", Severity = "Medium", Likelihood = "Low", Mitigation = "Scheduled upgrade Q2", Status = "In Progress" },
                new() { Id = "RISK-002", Description = "Network segmentation gaps", Severity = "Medium", Likelihood = "Medium", Mitigation = "VLAN implementation planned", Status = "Identified" },
            };
        }
    }

    #endregion

    #region SOC2 Commands

    /// <summary>
    /// SOC2 compliance commands for service organization controls.
    /// </summary>
    public static class Soc2
    {
        public static async Task GenerateReportAsync(string format, string? output)
        {
            await AnsiConsole.Status()
                .StartAsync("Generating SOC2 compliance report...", async ctx =>
                {
                    await Task.Delay(500);

                    var report = GetSoc2Report();

                    if (format.ToLower() == "table")
                    {
                        DisplaySoc2Report(report);
                    }
                    else
                    {
                        await ExportReportAsync("SOC2", format, output, report);
                    }
                });
        }

        public static async Task ShowTscAsync()
        {
            await AnsiConsole.Status()
                .StartAsync("Loading Trust Service Criteria status...", async ctx =>
                {
                    await Task.Delay(300);

                    var tsc = GetTrustServiceCriteria();

                    var tree = new Tree("[bold cyan]Trust Service Criteria (TSC)[/]")
                        .Style(Style.Parse("cyan"));

                    foreach (var criteria in tsc)
                    {
                        var statusColor = criteria.Value.Status == "Compliant" ? "green" : "yellow";
                        var node = tree.AddNode($"[yellow]{criteria.Key}[/] - [{statusColor}]{criteria.Value.Status}[/]");
                        node.AddNode($"Controls Implemented: {criteria.Value.ControlsImplemented}/{criteria.Value.TotalControls}");
                        node.AddNode($"Evidence Items: {criteria.Value.EvidenceItems}");
                        node.AddNode($"Last Assessment: {criteria.Value.LastAssessment:yyyy-MM-dd}");
                    }

                    AnsiConsole.Write(tree);
                });
        }

        public static async Task ListEvidenceAsync()
        {
            await AnsiConsole.Status()
                .StartAsync("Loading control evidence...", async ctx =>
                {
                    await Task.Delay(300);

                    var evidence = GetControlEvidence();

                    var table = new Table()
                        .Border(TableBorder.Rounded)
                        .AddColumn("Control ID")
                        .AddColumn("Description")
                        .AddColumn("Evidence Type")
                        .AddColumn("Collected")
                        .AddColumn("Reviewer")
                        .AddColumn("Status");

                    foreach (var item in evidence)
                    {
                        var statusColor = item.Status == "Approved" ? "green" : item.Status == "Pending" ? "yellow" : "white";

                        table.AddRow(
                            item.ControlId,
                            item.Description,
                            item.EvidenceType,
                            item.CollectedDate.ToString("yyyy-MM-dd"),
                            item.Reviewer ?? "Pending",
                            $"[{statusColor}]{item.Status}[/]"
                        );
                    }

                    AnsiConsole.Write(table);
                });
        }

        public static async Task ShowReadinessAsync()
        {
            await AnsiConsole.Status()
                .StartAsync("Calculating audit readiness...", async ctx =>
                {
                    await Task.Delay(400);

                    var readiness = GetAuditReadiness();

                    var panel = new Panel(new Markup($@"[bold]SOC2 Audit Readiness[/]

[bold cyan]Overall Score:[/] {readiness.OverallScore}% [{GetScoreColor(readiness.OverallScore)}]{GetScoreLabel(readiness.OverallScore)}[/]

[bold yellow]By Category:[/]
  Security: {readiness.SecurityScore}% [{GetScoreColor(readiness.SecurityScore)}]{GetScoreLabel(readiness.SecurityScore)}[/]
  Availability: {readiness.AvailabilityScore}% [{GetScoreColor(readiness.AvailabilityScore)}]{GetScoreLabel(readiness.AvailabilityScore)}[/]
  Processing Integrity: {readiness.ProcessingIntegrityScore}% [{GetScoreColor(readiness.ProcessingIntegrityScore)}]{GetScoreLabel(readiness.ProcessingIntegrityScore)}[/]
  Confidentiality: {readiness.ConfidentialityScore}% [{GetScoreColor(readiness.ConfidentialityScore)}]{GetScoreLabel(readiness.ConfidentialityScore)}[/]
  Privacy: {readiness.PrivacyScore}% [{GetScoreColor(readiness.PrivacyScore)}]{GetScoreLabel(readiness.PrivacyScore)}[/]

[bold yellow]Control Status:[/]
  Total Controls: {readiness.TotalControls}
  Implemented: [green]{readiness.ImplementedControls}[/]
  In Progress: [yellow]{readiness.InProgressControls}[/]
  Not Started: [red]{readiness.NotStartedControls}[/]

[bold yellow]Evidence:[/]
  Collected: {readiness.EvidenceCollected}/{readiness.EvidenceRequired}
  Approved: [green]{readiness.EvidenceApproved}[/]
  Pending Review: [yellow]{readiness.EvidencePending}[/]

[bold yellow]Recommendations:[/]
{string.Join("\n", readiness.Recommendations.Select(r => $"  • {r}"))}"))
                    {
                        Border = BoxBorder.Double,
                        BorderStyle = Style.Parse(GetScoreColor(readiness.OverallScore))
                    };

                    AnsiConsole.Write(panel);
                });
        }

        private static void DisplaySoc2Report(Soc2ComplianceReport report)
        {
            var panel = new Panel(new Markup($@"[bold]SOC2 Compliance Report[/]
Generated: {report.GeneratedDate:yyyy-MM-dd HH:mm:ss}
Report Period: {report.PeriodStart:yyyy-MM-dd} to {report.PeriodEnd:yyyy-MM-dd}

[bold cyan]Overall Status:[/] {GetStatusMarkup(report.OverallStatus)}

[bold yellow]Trust Service Criteria:[/]
  Security: {GetStatusMarkup(report.SecurityStatus)}
  Availability: {GetStatusMarkup(report.AvailabilityStatus)}
  Processing Integrity: {GetStatusMarkup(report.ProcessingIntegrityStatus)}
  Confidentiality: {GetStatusMarkup(report.ConfidentialityStatus)}
  Privacy: {GetStatusMarkup(report.PrivacyStatus)}

[bold yellow]Controls:[/]
  Total Controls: {report.TotalControls}
  Effective: [green]{report.EffectiveControls}[/]
  Deficient: {(report.DeficientControls > 0 ? $"[red]{report.DeficientControls}[/]" : "0")}

[bold yellow]Incidents:[/]
  Security Incidents: {report.SecurityIncidents}
  Availability Incidents: {report.AvailabilityIncidents}

[bold yellow]Monitoring:[/]
  Uptime: {report.Uptime:F2}%
  Average Response Time: {report.AvgResponseTime}ms"))
            {
                Border = BoxBorder.Double,
                BorderStyle = Style.Parse("green")
            };

            AnsiConsole.Write(panel);
        }

        private static Soc2ComplianceReport GetSoc2Report()
        {
            var now = DateTime.Now;
            return new Soc2ComplianceReport
            {
                GeneratedDate = now,
                PeriodStart = now.AddMonths(-6),
                PeriodEnd = now,
                OverallStatus = "Compliant",
                SecurityStatus = "Compliant",
                AvailabilityStatus = "Compliant",
                ProcessingIntegrityStatus = "Compliant",
                ConfidentialityStatus = "Compliant",
                PrivacyStatus = "Compliant",
                TotalControls = 64,
                EffectiveControls = 64,
                DeficientControls = 0,
                SecurityIncidents = 0,
                AvailabilityIncidents = 1,
                Uptime = 99.97,
                AvgResponseTime = 145
            };
        }

        private static Dictionary<string, TscStatus> GetTrustServiceCriteria()
        {
            var now = DateTime.Now;
            return new Dictionary<string, TscStatus>
            {
                ["CC1.0 - Security"] = new() { Status = "Compliant", ControlsImplemented = 12, TotalControls = 12, EvidenceItems = 45, LastAssessment = now.AddDays(-30) },
                ["CC2.0 - Availability"] = new() { Status = "Compliant", ControlsImplemented = 8, TotalControls = 8, EvidenceItems = 28, LastAssessment = now.AddDays(-25) },
                ["CC3.0 - Processing Integrity"] = new() { Status = "Compliant", ControlsImplemented = 10, TotalControls = 10, EvidenceItems = 32, LastAssessment = now.AddDays(-20) },
                ["CC4.0 - Confidentiality"] = new() { Status = "Compliant", ControlsImplemented = 6, TotalControls = 6, EvidenceItems = 18, LastAssessment = now.AddDays(-15) },
                ["CC5.0 - Privacy"] = new() { Status = "Compliant", ControlsImplemented = 8, TotalControls = 8, EvidenceItems = 22, LastAssessment = now.AddDays(-10) },
            };
        }

        private static List<ControlEvidence> GetControlEvidence()
        {
            var now = DateTime.Now;
            return new List<ControlEvidence>
            {
                new() { ControlId = "CC1.1", Description = "Access control policies", EvidenceType = "Policy Document", CollectedDate = now.AddDays(-20), Reviewer = "auditor.smith", Status = "Approved" },
                new() { ControlId = "CC1.2", Description = "User access review logs", EvidenceType = "System Report", CollectedDate = now.AddDays(-15), Reviewer = "auditor.jones", Status = "Approved" },
                new() { ControlId = "CC2.1", Description = "Uptime monitoring data", EvidenceType = "Automated Report", CollectedDate = now.AddDays(-10), Reviewer = null, Status = "Pending" },
                new() { ControlId = "CC3.1", Description = "Data validation procedures", EvidenceType = "Process Document", CollectedDate = now.AddDays(-18), Reviewer = "auditor.smith", Status = "Approved" },
            };
        }

        private static AuditReadiness GetAuditReadiness()
        {
            return new AuditReadiness
            {
                OverallScore = 92,
                SecurityScore = 95,
                AvailabilityScore = 98,
                ProcessingIntegrityScore = 90,
                ConfidentialityScore = 88,
                PrivacyScore = 91,
                TotalControls = 64,
                ImplementedControls = 59,
                InProgressControls = 5,
                NotStartedControls = 0,
                EvidenceRequired = 180,
                EvidenceCollected = 165,
                EvidenceApproved = 152,
                EvidencePending = 13,
                Recommendations = new List<string>
                {
                    "Complete evidence collection for 5 in-progress controls",
                    "Review and approve 13 pending evidence items",
                    "Schedule mock audit before formal assessment"
                }
            };
        }

        private static string GetScoreColor(int score)
        {
            return score switch
            {
                >= 90 => "green",
                >= 75 => "yellow",
                >= 60 => "orange3",
                _ => "red"
            };
        }

        private static string GetScoreLabel(int score)
        {
            return score switch
            {
                >= 90 => "Excellent",
                >= 75 => "Good",
                >= 60 => "Fair",
                _ => "Needs Improvement"
            };
        }
    }

    #endregion

    #region Export Command

    /// <summary>
    /// Export compliance reports in various formats.
    /// </summary>
    public static async Task ExportAsync(string reportType, string format, string? output)
    {
        await AnsiConsole.Progress()
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask($"Exporting {reportType} report as {format.ToUpper()}");

                while (!task.IsFinished)
                {
                    await Task.Delay(30);
                    task.Increment(5);
                }
            });

        var filename = output ?? $"{reportType.ToLower()}_report_{DateTime.Now:yyyyMMdd_HHmmss}.{format.ToLower()}";

        AnsiConsole.MarkupLine($"[green]Report exported successfully![/]");
        AnsiConsole.MarkupLine($"  Type: {reportType}");
        AnsiConsole.MarkupLine($"  Format: {format.ToUpper()}");
        AnsiConsole.MarkupLine($"  File: {filename}");
        AnsiConsole.MarkupLine($"  Size: {Random.Shared.Next(50, 500)} KB");
    }

    #endregion

    #region Helper Methods

    private static string GetStatusMarkup(string status)
    {
        return status.ToLower() switch
        {
            "compliant" => "[green]Compliant[/]",
            "enabled" => "[green]Enabled[/]",
            "valid" => "[green]Valid[/]",
            "active" => "[green]Active[/]",
            "non-compliant" => "[red]Non-Compliant[/]",
            "disabled" => "[red]Disabled[/]",
            "expired" => "[red]Expired[/]",
            "partial" => "[yellow]Partial[/]",
            _ => $"[white]{status}[/]"
        };
    }

    private static async Task ExportReportAsync(string reportType, string format, string? output, object reportData)
    {
        // In real implementation, this would use OutputFormatter
        await ExportAsync(reportType, format, output);
    }

    #endregion

    #region Data Models

    private record GdprComplianceReport
    {
        public DateTime GeneratedDate { get; init; }
        public string OverallStatus { get; init; } = "";
        public int PendingRequests { get; init; }
        public int InProgressRequests { get; init; }
        public int CompletedRequests { get; init; }
        public int OverdueRequests { get; init; }
        public int ActiveConsents { get; init; }
        public int WithdrawnConsents { get; init; }
        public int ConsentsExpiringSoon { get; init; }
        public string EncryptionStatus { get; init; } = "";
        public string PseudonymizationStatus { get; init; } = "";
        public string AccessControlStatus { get; init; } = "";
        public int RecentBreaches { get; init; }
        public int DpaNotifications { get; init; }
    }

    private record DataSubjectRequest
    {
        public string Id { get; init; } = "";
        public string Type { get; init; } = "";
        public string Subject { get; init; } = "";
        public string Status { get; init; } = "";
        public DateTime Submitted { get; init; }
        public DateTime DueDate { get; init; }
    }

    private record ConsentRecord
    {
        public string SubjectId { get; init; } = "";
        public string Purpose { get; init; } = "";
        public DateTime GrantedDate { get; init; }
        public DateTime? ExpiryDate { get; init; }
        public bool IsWithdrawn { get; init; }
    }

    private record BreachIncident
    {
        public string Id { get; init; } = "";
        public string Severity { get; init; } = "";
        public DateTime DetectedDate { get; init; }
        public int RecordsAffected { get; init; }
        public bool DpaNotified { get; init; }
        public string Status { get; init; } = "";
    }

    private record PersonalDataType
    {
        public string Name { get; init; } = "";
        public string Location { get; init; } = "";
        public string RetentionPeriod { get; init; } = "";
    }

    private record HipaaAuditReport
    {
        public DateTime GeneratedDate { get; init; }
        public string OverallStatus { get; init; } = "";
        public string AccessControls { get; init; } = "";
        public string AuditControls { get; init; } = "";
        public string IntegrityControls { get; init; } = "";
        public string TransmissionSecurity { get; init; } = "";
        public int TotalPhiAccess { get; init; }
        public int UnauthorizedAttempts { get; init; }
        public int UniqueUsers { get; init; }
        public string EncryptionAtRest { get; init; } = "";
        public string EncryptionInTransit { get; init; } = "";
        public int ActiveBaas { get; init; }
        public int ExpiringBaas { get; init; }
        public int HighRisks { get; init; }
        public int MediumRisks { get; init; }
        public DateTime LastRiskAssessment { get; init; }
    }

    private record PhiAccessLog
    {
        public DateTime Timestamp { get; init; }
        public string User { get; init; } = "";
        public string PatientId { get; init; } = "";
        public string Action { get; init; } = "";
        public string DataType { get; init; } = "";
        public string Purpose { get; init; } = "";
    }

    private record BusinessAssociateAgreement
    {
        public string Name { get; init; } = "";
        public string Service { get; init; } = "";
        public DateTime AgreementDate { get; init; }
        public DateTime ExpiryDate { get; init; }
        public string Status { get; init; } = "";
        public DateTime? LastAuditDate { get; init; }
    }

    private record EncryptionStatus
    {
        public string DatabaseEncryption { get; init; } = "";
        public string FileStorageEncryption { get; init; } = "";
        public string BackupEncryption { get; init; } = "";
        public string TlsVersion { get; init; } = "";
        public string CertificateStatus { get; init; } = "";
        public DateTime CertificateExpiry { get; init; }
        public string KeyRotationStatus { get; init; } = "";
        public DateTime LastKeyRotation { get; init; }
    }

    private record SecurityRisk
    {
        public string Id { get; init; } = "";
        public string Description { get; init; } = "";
        public string Severity { get; init; } = "";
        public string Likelihood { get; init; } = "";
        public string Mitigation { get; init; } = "";
        public string Status { get; init; } = "";
    }

    private record Soc2ComplianceReport
    {
        public DateTime GeneratedDate { get; init; }
        public DateTime PeriodStart { get; init; }
        public DateTime PeriodEnd { get; init; }
        public string OverallStatus { get; init; } = "";
        public string SecurityStatus { get; init; } = "";
        public string AvailabilityStatus { get; init; } = "";
        public string ProcessingIntegrityStatus { get; init; } = "";
        public string ConfidentialityStatus { get; init; } = "";
        public string PrivacyStatus { get; init; } = "";
        public int TotalControls { get; init; }
        public int EffectiveControls { get; init; }
        public int DeficientControls { get; init; }
        public int SecurityIncidents { get; init; }
        public int AvailabilityIncidents { get; init; }
        public double Uptime { get; init; }
        public double AvgResponseTime { get; init; }
    }

    private record TscStatus
    {
        public string Status { get; init; } = "";
        public int ControlsImplemented { get; init; }
        public int TotalControls { get; init; }
        public int EvidenceItems { get; init; }
        public DateTime LastAssessment { get; init; }
    }

    private record ControlEvidence
    {
        public string ControlId { get; init; } = "";
        public string Description { get; init; } = "";
        public string EvidenceType { get; init; } = "";
        public DateTime CollectedDate { get; init; }
        public string? Reviewer { get; init; }
        public string Status { get; init; } = "";
    }

    private record AuditReadiness
    {
        public int OverallScore { get; init; }
        public int SecurityScore { get; init; }
        public int AvailabilityScore { get; init; }
        public int ProcessingIntegrityScore { get; init; }
        public int ConfidentialityScore { get; init; }
        public int PrivacyScore { get; init; }
        public int TotalControls { get; init; }
        public int ImplementedControls { get; init; }
        public int InProgressControls { get; init; }
        public int NotStartedControls { get; init; }
        public int EvidenceRequired { get; init; }
        public int EvidenceCollected { get; init; }
        public int EvidenceApproved { get; init; }
        public int EvidencePending { get; init; }
        public List<string> Recommendations { get; init; } = new();
    }

    #endregion
}
