using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateCompliance.Services
{
    /// <summary>
    /// Chain-of-custody exporter producing PDF-structured and JSON documents for legal discovery.
    /// Each custody entry forms a blockchain-style chain: timestamp, actor, action, hash, previous hash.
    /// Documents are sealed with HMAC-SHA256 integrity verification.
    /// Publishes exports via message bus topic "compliance.custody.export".
    /// </summary>
    public sealed class ChainOfCustodyExporter
    {
        private readonly IMessageBus? _messageBus;
        private readonly string _pluginId;
        private readonly byte[] _hmacKey;

        public ChainOfCustodyExporter(IMessageBus? messageBus, string pluginId, byte[]? hmacKey = null)
        {
            _messageBus = messageBus;
            _pluginId = pluginId;
            // Use caller-supplied key; fall back to a per-instance ephemeral key to avoid hardcoded secret
            _hmacKey = hmacKey is { Length: >= 16 } ? hmacKey : System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        }

        /// <summary>
        /// Exports a chain-of-custody document in the requested format (PDF-structured or JSON).
        /// Each entry in the chain is cryptographically linked to the previous entry via SHA-256 hashes.
        /// The entire document is sealed with HMAC-SHA256 for tamper detection.
        /// </summary>
        public async Task<ChainOfCustodyDocument> ExportAsync(
            ChainOfCustodyRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request.Entries == null || request.Entries.Count == 0)
                throw new ArgumentException("Chain of custody must contain at least one entry.", nameof(request));

            // Build blockchain-style chain: each entry hashes over its content + previous hash
            var chainedEntries = BuildBlockchainChain(request.Entries);

            // Generate document structure
            var document = new ChainOfCustodyDocument
            {
                DocumentId = $"COC-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..8]}",
                Title = request.Title ?? $"Chain of Custody - {request.SubjectIdentifier}",
                SubjectIdentifier = request.SubjectIdentifier,
                CaseReference = request.CaseReference,
                CreatedAtUtc = DateTime.UtcNow,
                ExportFormat = request.Format,
                Entries = chainedEntries,
                Header = new DocumentHeader
                {
                    Organization = request.Organization ?? "DataWarehouse System",
                    Department = request.Department ?? "Compliance",
                    PreparedBy = request.PreparedBy ?? "Automated Export",
                    Classification = request.Classification ?? "Confidential - Legal Privilege",
                    LegalNotice = "This document is prepared for legal discovery purposes. " +
                                  "Unauthorized modification, distribution, or destruction is prohibited. " +
                                  "Chain integrity is cryptographically verified via SHA-256 hash linkage and HMAC-SHA256 seal."
                },
                Signatures = BuildSignatures(request, chainedEntries),
                ChainIntegritySummary = VerifyChainIntegrity(chainedEntries)
            };

            // Seal the document with HMAC-SHA256
            document.IntegritySeal = ComputeDocumentSeal(document);

            // Generate format-specific output
            switch (request.Format)
            {
                case ExportFormat.Pdf:
                    document.PdfStructuredContent = GeneratePdfStructuredContent(document);
                    break;
                case ExportFormat.Json:
                    document.JsonContent = GenerateJsonContent(document);
                    break;
                case ExportFormat.Both:
                    document.PdfStructuredContent = GeneratePdfStructuredContent(document);
                    document.JsonContent = GenerateJsonContent(document);
                    break;
            }

            // Publish export event via message bus
            if (_messageBus != null)
            {
                await _messageBus.PublishAsync("compliance.custody.export", new PluginMessage
                {
                    Type = "compliance.custody.export",
                    Source = _pluginId,
                    Payload = new Dictionary<string, object>
                    {
                        ["documentId"] = document.DocumentId,
                        ["subjectIdentifier"] = document.SubjectIdentifier,
                        ["caseReference"] = document.CaseReference ?? "",
                        ["entryCount"] = document.Entries.Count,
                        ["format"] = request.Format.ToString(),
                        ["integrityVerified"] = document.ChainIntegritySummary.IsValid,
                        ["exportedAt"] = document.CreatedAtUtc.ToString("O")
                    }
                }, cancellationToken);
            }

            return document;
        }

        /// <summary>
        /// Verifies the integrity of an existing chain-of-custody document.
        /// Recomputes all hashes and checks the HMAC seal.
        /// </summary>
        public bool VerifyDocument(ChainOfCustodyDocument document)
        {
            // Verify chain integrity
            var chainCheck = VerifyChainIntegrity(document.Entries);
            if (!chainCheck.IsValid)
                return false;

            // Verify HMAC seal
            var expectedSeal = ComputeDocumentSeal(document);
            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(document.IntegritySeal ?? ""),
                Encoding.UTF8.GetBytes(expectedSeal));
        }

        private List<CustodyChainEntry> BuildBlockchainChain(List<CustodyEntryInput> inputs)
        {
            var entries = new List<CustodyChainEntry>();
            string? previousHash = null;

            foreach (var input in inputs.OrderBy(i => i.Timestamp))
            {
                var entry = new CustodyChainEntry
                {
                    EntryIndex = entries.Count,
                    Timestamp = input.Timestamp,
                    Actor = input.Actor,
                    ActorRole = input.ActorRole,
                    Action = input.Action,
                    Description = input.Description,
                    Location = input.Location,
                    EvidenceReference = input.EvidenceReference,
                    PreviousHash = previousHash
                };

                entry.Hash = ComputeEntryHash(entry);
                previousHash = entry.Hash;
                entries.Add(entry);
            }

            return entries;
        }

        private string ComputeEntryHash(CustodyChainEntry entry)
        {
            var data = $"{entry.EntryIndex}|{entry.Timestamp:O}|{entry.Actor}|{entry.ActorRole}|{entry.Action}|{entry.Description}|{entry.Location}|{entry.EvidenceReference}|{entry.PreviousHash ?? "GENESIS"}";
            var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(data));
            return Convert.ToHexString(hashBytes).ToLowerInvariant();
        }

        private ChainIntegritySummary VerifyChainIntegrity(List<CustodyChainEntry> entries)
        {
            if (entries.Count == 0)
                return new ChainIntegritySummary { IsValid = true, EntriesVerified = 0, Message = "Empty chain" };

            var invalidEntries = new List<int>();

            for (int i = 0; i < entries.Count; i++)
            {
                // Verify hash
                var expectedHash = ComputeEntryHash(entries[i]);
                if (entries[i].Hash != expectedHash)
                {
                    invalidEntries.Add(i);
                    continue;
                }

                // Verify chain linkage
                if (i > 0 && entries[i].PreviousHash != entries[i - 1].Hash)
                {
                    invalidEntries.Add(i);
                }
            }

            return new ChainIntegritySummary
            {
                IsValid = invalidEntries.Count == 0,
                EntriesVerified = entries.Count,
                InvalidEntryIndices = invalidEntries,
                Message = invalidEntries.Count == 0
                    ? $"All {entries.Count} entries verified. Chain integrity confirmed."
                    : $"Chain integrity BROKEN at {invalidEntries.Count} entries: [{string.Join(", ", invalidEntries)}]"
            };
        }

        private List<DocumentSignature> BuildSignatures(ChainOfCustodyRequest request, List<CustodyChainEntry> entries)
        {
            var signatures = new List<DocumentSignature>();

            // System signature (automated)
            var systemSignatureData = $"SYSTEM|{DateTime.UtcNow:O}|{entries.Count}|{entries.LastOrDefault()?.Hash ?? "EMPTY"}";
            var systemSigHash = SHA256.HashData(Encoding.UTF8.GetBytes(systemSignatureData));

            signatures.Add(new DocumentSignature
            {
                SignerName = "DataWarehouse Compliance System",
                SignerRole = "Automated Custodian",
                SignedAtUtc = DateTime.UtcNow,
                SignatureHash = Convert.ToHexString(systemSigHash).ToLowerInvariant(),
                SignatureMethod = "SHA-256"
            });

            // Preparer signature
            if (!string.IsNullOrEmpty(request.PreparedBy))
            {
                var preparerData = $"{request.PreparedBy}|{DateTime.UtcNow:O}|PREPARED";
                var preparerHash = SHA256.HashData(Encoding.UTF8.GetBytes(preparerData));

                signatures.Add(new DocumentSignature
                {
                    SignerName = request.PreparedBy,
                    SignerRole = "Document Preparer",
                    SignedAtUtc = DateTime.UtcNow,
                    SignatureHash = Convert.ToHexString(preparerHash).ToLowerInvariant(),
                    SignatureMethod = "SHA-256"
                });
            }

            return signatures;
        }

        private string ComputeDocumentSeal(ChainOfCustodyDocument document)
        {
            var sealData = new StringBuilder();
            sealData.Append(document.DocumentId);
            sealData.Append('|');
            sealData.Append(document.SubjectIdentifier);
            sealData.Append('|');
            sealData.Append(document.CreatedAtUtc.ToString("O"));
            sealData.Append('|');
            sealData.Append(document.Entries.Count);
            sealData.Append('|');
            sealData.Append(document.Entries.LastOrDefault()?.Hash ?? "EMPTY");

            using var hmac = new HMACSHA256(_hmacKey);
            var sealBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(sealData.ToString()));
            return Convert.ToHexString(sealBytes).ToLowerInvariant();
        }

        private string GeneratePdfStructuredContent(ChainOfCustodyDocument document)
        {
            // Generate a structured document representation suitable for PDF rendering.
            // In production, this feeds into a PDF library (e.g., QuestPDF, iTextSharp).
            // The structured output contains all sections a legal-discovery PDF requires.
            var sb = new StringBuilder();

            // PDF Header Section
            sb.AppendLine("%%PDF-STRUCTURED-CONTENT%%");
            sb.AppendLine($"TITLE: {document.Title}");
            sb.AppendLine($"DOCUMENT-ID: {document.DocumentId}");
            sb.AppendLine($"CLASSIFICATION: {document.Header.Classification}");
            sb.AppendLine();

            // Header Block
            sb.AppendLine("=== DOCUMENT HEADER ===");
            sb.AppendLine($"Organization: {document.Header.Organization}");
            sb.AppendLine($"Department: {document.Header.Department}");
            sb.AppendLine($"Prepared By: {document.Header.PreparedBy}");
            sb.AppendLine($"Date: {document.CreatedAtUtc:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine($"Subject: {document.SubjectIdentifier}");
            if (!string.IsNullOrEmpty(document.CaseReference))
                sb.AppendLine($"Case Reference: {document.CaseReference}");
            sb.AppendLine();
            sb.AppendLine($"Legal Notice: {document.Header.LegalNotice}");
            sb.AppendLine();

            // Chain of Custody Table
            sb.AppendLine("=== CHAIN OF CUSTODY ===");
            sb.AppendLine("| # | Timestamp | Actor | Role | Action | Description | Location | Evidence Ref | Hash |");
            sb.AppendLine("|---|-----------|-------|------|--------|-------------|----------|--------------|------|");

            foreach (var entry in document.Entries)
            {
                sb.AppendLine($"| {entry.EntryIndex} | {entry.Timestamp:yyyy-MM-dd HH:mm:ss} | {entry.Actor} | {entry.ActorRole} | {entry.Action} | {entry.Description} | {entry.Location} | {entry.EvidenceReference} | {(entry.Hash ?? "N/A")[..Math.Min(16, (entry.Hash ?? "N/A").Length)]}... |");
            }
            sb.AppendLine();

            // Integrity Verification
            sb.AppendLine("=== INTEGRITY VERIFICATION ===");
            sb.AppendLine($"Chain Status: {(document.ChainIntegritySummary.IsValid ? "VERIFIED" : "INTEGRITY FAILURE")}");
            sb.AppendLine($"Entries Verified: {document.ChainIntegritySummary.EntriesVerified}");
            sb.AppendLine($"Verification Message: {document.ChainIntegritySummary.Message}");
            sb.AppendLine($"Document Seal (HMAC-SHA256): {document.IntegritySeal}");
            sb.AppendLine();

            // Signatures Block
            sb.AppendLine("=== SIGNATURES ===");
            foreach (var sig in document.Signatures)
            {
                sb.AppendLine($"Signer: {sig.SignerName} ({sig.SignerRole})");
                sb.AppendLine($"Date: {sig.SignedAtUtc:yyyy-MM-dd HH:mm:ss} UTC");
                sb.AppendLine($"Signature ({sig.SignatureMethod}): {sig.SignatureHash}");
                sb.AppendLine();
            }

            return sb.ToString();
        }

        private string GenerateJsonContent(ChainOfCustodyDocument document)
        {
            var jsonObject = new
            {
                documentId = document.DocumentId,
                title = document.Title,
                subjectIdentifier = document.SubjectIdentifier,
                caseReference = document.CaseReference,
                createdAtUtc = document.CreatedAtUtc,
                format = document.ExportFormat.ToString(),
                header = new
                {
                    organization = document.Header.Organization,
                    department = document.Header.Department,
                    preparedBy = document.Header.PreparedBy,
                    classification = document.Header.Classification,
                    legalNotice = document.Header.LegalNotice
                },
                chainOfCustody = document.Entries.Select(e => new
                {
                    entryIndex = e.EntryIndex,
                    timestamp = e.Timestamp,
                    actor = e.Actor,
                    actorRole = e.ActorRole,
                    action = e.Action,
                    description = e.Description,
                    location = e.Location,
                    evidenceReference = e.EvidenceReference,
                    hash = e.Hash,
                    previousHash = e.PreviousHash
                }).ToArray(),
                integrity = new
                {
                    isValid = document.ChainIntegritySummary.IsValid,
                    entriesVerified = document.ChainIntegritySummary.EntriesVerified,
                    invalidEntries = document.ChainIntegritySummary.InvalidEntryIndices,
                    message = document.ChainIntegritySummary.Message
                },
                signatures = document.Signatures.Select(s => new
                {
                    signerName = s.SignerName,
                    signerRole = s.SignerRole,
                    signedAtUtc = s.SignedAtUtc,
                    signatureHash = s.SignatureHash,
                    signatureMethod = s.SignatureMethod
                }).ToArray(),
                seal = new
                {
                    algorithm = "HMAC-SHA256",
                    value = document.IntegritySeal
                }
            };

            return JsonSerializer.Serialize(jsonObject, new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });
        }
    }

    #region Chain of Custody Types

    /// <summary>
    /// Request to export a chain-of-custody document.
    /// </summary>
    public sealed class ChainOfCustodyRequest
    {
        public required string SubjectIdentifier { get; init; }
        public string? CaseReference { get; init; }
        public string? Title { get; init; }
        public string? Organization { get; init; }
        public string? Department { get; init; }
        public string? PreparedBy { get; init; }
        public string? Classification { get; init; }
        public required ExportFormat Format { get; init; }
        public required List<CustodyEntryInput> Entries { get; init; }
    }

    /// <summary>
    /// Input for a single custody chain entry.
    /// </summary>
    public sealed class CustodyEntryInput
    {
        public required DateTime Timestamp { get; init; }
        public required string Actor { get; init; }
        public required string ActorRole { get; init; }
        public required string Action { get; init; }
        public required string Description { get; init; }
        public string? Location { get; init; }
        public string? EvidenceReference { get; init; }
    }

    /// <summary>
    /// A chain-of-custody document with cryptographic integrity.
    /// </summary>
    public sealed class ChainOfCustodyDocument
    {
        public required string DocumentId { get; init; }
        public required string Title { get; init; }
        public required string SubjectIdentifier { get; init; }
        public string? CaseReference { get; init; }
        public required DateTime CreatedAtUtc { get; init; }
        public required ExportFormat ExportFormat { get; init; }
        public required DocumentHeader Header { get; init; }
        public required List<CustodyChainEntry> Entries { get; init; }
        public required List<DocumentSignature> Signatures { get; init; }
        public required ChainIntegritySummary ChainIntegritySummary { get; init; }
        public string? IntegritySeal { get; set; }
        public string? PdfStructuredContent { get; set; }
        public string? JsonContent { get; set; }
    }

    /// <summary>
    /// Document header for legal discovery.
    /// </summary>
    public sealed class DocumentHeader
    {
        public required string Organization { get; init; }
        public required string Department { get; init; }
        public required string PreparedBy { get; init; }
        public required string Classification { get; init; }
        public required string LegalNotice { get; init; }
    }

    /// <summary>
    /// Single entry in the chain of custody with blockchain-style hash linkage.
    /// </summary>
    public sealed class CustodyChainEntry
    {
        public required int EntryIndex { get; init; }
        public required DateTime Timestamp { get; init; }
        public required string Actor { get; init; }
        public required string ActorRole { get; init; }
        public required string Action { get; init; }
        public required string Description { get; init; }
        public string? Location { get; init; }
        public string? EvidenceReference { get; init; }
        public string? Hash { get; set; }
        public string? PreviousHash { get; init; }
    }

    /// <summary>
    /// Digital signature entry.
    /// </summary>
    public sealed class DocumentSignature
    {
        public required string SignerName { get; init; }
        public required string SignerRole { get; init; }
        public required DateTime SignedAtUtc { get; init; }
        public required string SignatureHash { get; init; }
        public required string SignatureMethod { get; init; }
    }

    /// <summary>
    /// Summary of chain integrity verification.
    /// </summary>
    public sealed class ChainIntegritySummary
    {
        public required bool IsValid { get; init; }
        public required int EntriesVerified { get; init; }
        public List<int> InvalidEntryIndices { get; init; } = new();
        public required string Message { get; init; }
    }

    /// <summary>
    /// Export format options.
    /// </summary>
    public enum ExportFormat
    {
        Pdf,
        Json,
        Both
    }

    #endregion
}
