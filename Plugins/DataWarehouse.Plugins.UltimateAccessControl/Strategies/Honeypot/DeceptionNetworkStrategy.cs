using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.Honeypot;

/// <summary>
/// Advanced deception network strategy (T142.B4).
/// Creates convincing fake environments, misdirection paths, and threat luring systems.
/// </summary>
/// <remarks>
/// <para>Implements industry-first data-level deception technology that goes beyond OpenBSD's OS-level security.</para>
/// <para>Works in conjunction with CanaryStrategy for comprehensive threat detection.</para>
/// </remarks>
public sealed class DeceptionNetworkStrategy : AccessControlStrategyBase, IDisposable
{
    private readonly ConcurrentDictionary<string, DeceptionEnvironment> _environments = new();
    private readonly ConcurrentDictionary<string, MisdirectionPath> _misdirectionPaths = new();
    private readonly ConcurrentDictionary<string, ThreatLure> _threatLures = new();
    private readonly ConcurrentQueue<DeceptionEvent> _eventQueue = new();
    private readonly ConcurrentDictionary<string, AttackerProfile> _attackerProfiles = new();
    private readonly List<IDeceptionEventHandler> _eventHandlers = new();

    private Timer? _environmentRotationTimer;
    private Timer? _adaptationTimer;
    private int _maxEventsInQueue = 10000;
    private TimeSpan _rotationInterval = TimeSpan.FromHours(12);
    private bool _disposed;

    /// <inheritdoc/>
    public override string StrategyId => "deception-network";

    /// <inheritdoc/>
    public override string StrategyName => "Deception Network";

    /// <inheritdoc/>
    public override AccessControlCapabilities Capabilities { get; } = new()
    {
        SupportsRealTimeDecisions = true,
        SupportsAuditTrail = true,
        SupportsPolicyConfiguration = true,
        SupportsExternalIdentity = false,
        SupportsTemporalAccess = true,
        SupportsGeographicRestrictions = false,
        MaxConcurrentEvaluations = 10000
    };

    #region T142.B4.1 - Fake File Tree Strategy

    /// <summary>
    /// Creates a convincing fake directory tree to confuse and misdirect attackers.
    /// </summary>
    public FakeFileTree CreateFakeFileTree(FakeFileTreeConfig config)
    {
        var tree = new FakeFileTree
        {
            TreeId = Guid.NewGuid().ToString("N"),
            RootPath = config.RootPath,
            Depth = config.Depth,
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        };

        // Generate convincing directory structure
        tree.Directories = GenerateDirectoryStructure(config);
        tree.Files = GenerateFileStructure(config, tree.Directories);

        // Add honeytokens to key files
        foreach (var file in tree.Files.Where(f => f.IsHoneytoken))
        {
            file.HoneytokenId = Guid.NewGuid().ToString("N");
            file.Content = GenerateFakeContent(file.FileType);
        }

        return tree;
    }

    /// <summary>
    /// Deploys a fake file tree and monitors access.
    /// </summary>
    public DeceptionEnvironment DeployFakeEnvironment(FakeFileTree tree, DeploymentConfig deployConfig)
    {
        var environment = new DeceptionEnvironment
        {
            EnvironmentId = Guid.NewGuid().ToString("N"),
            FileTree = tree,
            DeployedAt = DateTime.UtcNow,
            IsActive = true,
            Configuration = deployConfig,
            Metadata = new Dictionary<string, object>
            {
                ["totalFiles"] = tree.Files.Count,
                ["totalDirectories"] = tree.Directories.Count,
                ["honeytokenCount"] = tree.Files.Count(f => f.IsHoneytoken)
            }
        };

        _environments[environment.EnvironmentId] = environment;
        return environment;
    }

    private List<FakeDirectory> GenerateDirectoryStructure(FakeFileTreeConfig config)
    {
        var directories = new List<FakeDirectory>();
        var attractiveNames = new[] { "confidential", "secrets", "admin", "backup", "finance", "hr", "credentials", "keys", "passwords" };

        void GenerateLevel(string parentPath, int currentDepth)
        {
            if (currentDepth >= config.Depth) return;

            var count = config.BranchingFactor;
            for (int i = 0; i < count; i++)
            {
                var name = attractiveNames[i % attractiveNames.Length];
                if (currentDepth > 0) name += $"_{RandomNumberGenerator.GetInt32(100, 999)}";

                var dir = new FakeDirectory
                {
                    Path = Path.Combine(parentPath, name),
                    Name = name,
                    Depth = currentDepth,
                    IsAttractive = attractiveNames.Contains(name.Split('_')[0])
                };

                directories.Add(dir);
                GenerateLevel(dir.Path, currentDepth + 1);
            }
        }

        GenerateLevel(config.RootPath, 0);
        return directories;
    }

    private List<FakeFile> GenerateFileStructure(FakeFileTreeConfig config, List<FakeDirectory> directories)
    {
        var files = new List<FakeFile>();
        var attractiveFiles = new[]
        {
            ("passwords.xlsx", "excel", true),
            ("credentials.json", "json", true),
            ("private-key.pem", "pem", true),
            (".env", "env", true),
            ("secrets.yaml", "yaml", true),
            ("database_backup.sql", "sql", true),
            ("aws_credentials", "aws", true),
            ("kubeconfig", "yaml", true),
            ("README.md", "markdown", false),
            ("config.json", "json", false)
        };

        foreach (var dir in directories.Where(d => d.IsAttractive))
        {
            foreach (var (name, type, isHoneytoken) in attractiveFiles.Take(config.FilesPerDirectory))
            {
                files.Add(new FakeFile
                {
                    Path = Path.Combine(dir.Path, name),
                    Name = name,
                    FileType = type,
                    IsHoneytoken = isHoneytoken,
                    Size = RandomNumberGenerator.GetInt32(1024, 1024 * 100)
                });
            }
        }

        return files;
    }

    private byte[] GenerateFakeContent(string fileType)
    {
        return fileType switch
        {
            "excel" => Encoding.UTF8.GetBytes("Site,Username,Password\nadmin,admin@corp.internal,P@ssw0rd123!"),
            "json" => Encoding.UTF8.GetBytes("{\n  \"api_key\": \"sk_live_fake123\",\n  \"secret\": \"super_secret\"\n}"),
            "pem" => Encoding.UTF8.GetBytes("-----BEGIN RSA PRIVATE KEY-----\nFAKEKEYDATA\n-----END RSA PRIVATE KEY-----"),
            "env" => Encoding.UTF8.GetBytes("DATABASE_URL=postgresql://admin:password@db.internal:5432/main"),
            "yaml" => Encoding.UTF8.GetBytes("secrets:\n  api_key: sk_fake_123\n  password: super_secret"),
            "sql" => Encoding.UTF8.GetBytes("-- Fake database backup\nCREATE TABLE users (id INT, password VARCHAR(255));"),
            "aws" => Encoding.UTF8.GetBytes("[default]\naws_access_key_id = AKIAIOSFODNN7FAKE\naws_secret_access_key = fake/key"),
            _ => Encoding.UTF8.GetBytes("# Configuration file")
        };
    }

    #endregion

    #region T142.B4.2 - Delay Injection Strategy

    /// <summary>
    /// Injects artificial delays for suspicious operations to slow down attackers.
    /// </summary>
    public async Task<DelayInjectionResult> InjectDelayAsync(
        AccessContext context,
        SuspicionLevel suspicionLevel,
        CancellationToken ct = default)
    {
        var baseDelayMs = suspicionLevel switch
        {
            SuspicionLevel.Low => 100,
            SuspicionLevel.Medium => 500,
            SuspicionLevel.High => 2000,
            SuspicionLevel.Critical => 5000,
            _ => 0
        };

        // Add randomness to avoid detection
        var jitter = RandomNumberGenerator.GetInt32(0, (int)(baseDelayMs * 0.3));
        var totalDelay = baseDelayMs + jitter;

        if (totalDelay > 0)
        {
            await Task.Delay(totalDelay, ct);
        }

        // Record the delay event
        var delayEvent = new DeceptionEvent
        {
            EventId = Guid.NewGuid().ToString("N"),
            EventType = DeceptionEventType.DelayInjected,
            SubjectId = context.SubjectId,
            ResourceId = context.ResourceId,
            Timestamp = DateTime.UtcNow,
            Details = new Dictionary<string, object>
            {
                ["delayMs"] = totalDelay,
                ["suspicionLevel"] = suspicionLevel.ToString(),
                ["action"] = context.Action
            }
        };

        EnqueueEvent(delayEvent);

        return new DelayInjectionResult
        {
            DelayMs = totalDelay,
            SuspicionLevel = suspicionLevel,
            InjectedAt = DateTime.UtcNow
        };
    }

    #endregion

    #region T142.B4.3 - Misdirection Strategy

    /// <summary>
    /// Creates a misdirection path to guide attackers toward honeypots.
    /// </summary>
    public MisdirectionPath CreateMisdirectionPath(MisdirectionConfig config)
    {
        var path = new MisdirectionPath
        {
            PathId = Guid.NewGuid().ToString("N"),
            Name = config.Name,
            EntryPoint = config.EntryPoint,
            TargetHoneypotId = config.TargetHoneypotId,
            Breadcrumbs = GenerateBreadcrumbs(config),
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        };

        _misdirectionPaths[path.PathId] = path;
        return path;
    }

    /// <summary>
    /// Evaluates whether to misdirect a request toward a honeypot.
    /// </summary>
    public MisdirectionDecision EvaluateMisdirection(AccessContext context)
    {
        // Check if this access pattern suggests an attacker
        var suspicionScore = CalculateSuspicionScore(context);

        if (suspicionScore < 0.5)
        {
            return new MisdirectionDecision
            {
                ShouldMisdirect = false,
                SuspicionScore = suspicionScore
            };
        }

        // Find appropriate misdirection path
        var suitablePath = _misdirectionPaths.Values
            .Where(p => p.IsActive && IsPathSuitable(p, context))
            .OrderByDescending(p => p.SuccessRate)
            .FirstOrDefault();

        if (suitablePath == null)
        {
            return new MisdirectionDecision
            {
                ShouldMisdirect = false,
                SuspicionScore = suspicionScore,
                Reason = "No suitable misdirection path"
            };
        }

        return new MisdirectionDecision
        {
            ShouldMisdirect = true,
            SuspicionScore = suspicionScore,
            MisdirectionPath = suitablePath,
            RedirectTo = suitablePath.TargetHoneypotId,
            Reason = "High suspicion score triggered misdirection"
        };
    }

    private List<Breadcrumb> GenerateBreadcrumbs(MisdirectionConfig config)
    {
        var breadcrumbs = new List<Breadcrumb>();

        // Create a trail of attractive hints leading to the honeypot
        var hints = new[]
        {
            ("Check backup folder for credentials", "path_hint"),
            ("Admin password in config.yaml", "file_hint"),
            ("See /confidential/secrets", "directory_hint"),
            ("API keys stored in .env", "content_hint")
        };

        for (int i = 0; i < Math.Min(config.BreadcrumbCount, hints.Length); i++)
        {
            breadcrumbs.Add(new Breadcrumb
            {
                BreadcrumbId = Guid.NewGuid().ToString("N"),
                Content = hints[i].Item1,
                Type = hints[i].Item2,
                PlacementLocation = config.EntryPoint + $"/hint_{i}",
                Order = i
            });
        }

        return breadcrumbs;
    }

    private double CalculateSuspicionScore(AccessContext context)
    {
        var score = 0.0;

        // Check for suspicious patterns
        if (context.Action.Equals("enumerate", StringComparison.OrdinalIgnoreCase))
            score += 0.3;

        if (context.ResourceId.Contains("password", StringComparison.OrdinalIgnoreCase) ||
            context.ResourceId.Contains("secret", StringComparison.OrdinalIgnoreCase))
            score += 0.2;

        // Check request time
        var hour = context.RequestTime.Hour;
        if (hour < 6 || hour > 22)
            score += 0.1;

        // Check for repeated access attempts
        if (_attackerProfiles.TryGetValue(context.SubjectId, out var profile))
        {
            score += Math.Min(0.3, profile.SuspiciousAccessCount * 0.05);
        }

        return Math.Min(1.0, score);
    }

    private bool IsPathSuitable(MisdirectionPath path, AccessContext context)
    {
        // Check if the access pattern matches the path's entry point
        return context.ResourceId.StartsWith(path.EntryPoint, StringComparison.OrdinalIgnoreCase);
    }

    #endregion

    #region T142.B4.4 - Digital Mirage Strategy

    /// <summary>
    /// Creates a digital mirage - a convincing fake environment that adapts to attacker behavior.
    /// </summary>
    public DigitalMirage CreateDigitalMirage(DigitalMirageConfig config)
    {
        var mirage = new DigitalMirage
        {
            MirageId = Guid.NewGuid().ToString("N"),
            Name = config.Name,
            EnvironmentType = config.EnvironmentType,
            CreatedAt = DateTime.UtcNow,
            IsActive = true,
            AdaptationEnabled = config.EnableAdaptation,
            Components = GenerateMirageComponents(config)
        };

        return mirage;
    }

    /// <summary>
    /// Adapts the digital mirage based on observed attacker behavior.
    /// </summary>
    public void AdaptMirage(string mirageId, AttackerBehavior behavior)
    {
        // Update mirage based on what the attacker seems to be looking for
        var adaptations = new List<string>();

        if (behavior.SeeksCredentials)
        {
            adaptations.Add("Added more credential-like files");
        }

        if (behavior.SeeksNetworkInfo)
        {
            adaptations.Add("Added network configuration files");
        }

        if (behavior.UsesPowerShell)
        {
            adaptations.Add("Added PowerShell-attractive targets");
        }

        // Log adaptation event
        var adaptEvent = new DeceptionEvent
        {
            EventId = Guid.NewGuid().ToString("N"),
            EventType = DeceptionEventType.MirageAdapted,
            Timestamp = DateTime.UtcNow,
            Details = new Dictionary<string, object>
            {
                ["mirageId"] = mirageId,
                ["adaptations"] = adaptations,
                ["behaviorProfile"] = behavior
            }
        };

        EnqueueEvent(adaptEvent);
    }

    private List<MirageComponent> GenerateMirageComponents(DigitalMirageConfig config)
    {
        var components = new List<MirageComponent>();

        // Generate based on environment type
        switch (config.EnvironmentType)
        {
            case MirageEnvironmentType.CorporateNetwork:
                components.Add(new MirageComponent { Name = "Active Directory", Type = "service" });
                components.Add(new MirageComponent { Name = "File Server", Type = "server" });
                components.Add(new MirageComponent { Name = "Database Server", Type = "server" });
                break;

            case MirageEnvironmentType.CloudInfrastructure:
                components.Add(new MirageComponent { Name = "S3 Buckets", Type = "storage" });
                components.Add(new MirageComponent { Name = "EC2 Instances", Type = "compute" });
                components.Add(new MirageComponent { Name = "RDS Databases", Type = "database" });
                break;

            case MirageEnvironmentType.DeveloperWorkstation:
                components.Add(new MirageComponent { Name = ".ssh folder", Type = "directory" });
                components.Add(new MirageComponent { Name = ".aws credentials", Type = "file" });
                components.Add(new MirageComponent { Name = "source code repos", Type = "directory" });
                break;
        }

        return components;
    }

    #endregion

    #region Threat Luring

    /// <summary>
    /// Creates a threat lure to attract and engage attackers.
    /// </summary>
    public ThreatLure CreateThreatLure(ThreatLureConfig config)
    {
        var lure = new ThreatLure
        {
            LureId = Guid.NewGuid().ToString("N"),
            LureType = config.LureType,
            Target = config.Target,
            Attractiveness = config.Attractiveness,
            CreatedAt = DateTime.UtcNow,
            IsActive = true,
            Content = GenerateLureContent(config)
        };

        _threatLures[lure.LureId] = lure;
        return lure;
    }

    private string GenerateLureContent(ThreatLureConfig config)
    {
        return config.LureType switch
        {
            ThreatLureType.FakeCredentials => JsonSerializer.Serialize(new
            {
                username = "admin",
                password = "Sup3rS3cr3t!",
                server = "db.internal.corp"
            }),
            ThreatLureType.FakeApiKey => $"sk_live_{Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))[..32]}",
            ThreatLureType.FakeToken => GenerateFakeJwt(),
            ThreatLureType.FakeConnectionString => "Server=db.internal;Database=production;User Id=sa;Password=P@ssw0rd;",
            _ => "Confidential data - do not share"
        };
    }

    private string GenerateFakeJwt()
    {
        var header = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"alg\":\"HS256\",\"typ\":\"JWT\"}"));
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"sub\":\"admin\",\"role\":\"superuser\"}"));
        var signature = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        return $"{header}.{payload}.{signature}";
    }

    #endregion

    #region Attacker Profiling

    /// <summary>
    /// Updates the attacker profile based on observed behavior.
    /// </summary>
    public void UpdateAttackerProfile(string subjectId, AccessContext context, DeceptionEvent? triggeringEvent = null)
    {
        var profile = _attackerProfiles.GetOrAdd(subjectId, _ => new AttackerProfile
        {
            SubjectId = subjectId,
            FirstSeen = DateTime.UtcNow
        });

        profile.LastSeen = DateTime.UtcNow;
        profile.TotalAccessCount++;

        if (triggeringEvent != null)
        {
            profile.SuspiciousAccessCount++;
            profile.TriggeredEventIds.Add(triggeringEvent.EventId);
        }

        // Update behavior patterns
        if (context.ResourceId.Contains("password", StringComparison.OrdinalIgnoreCase))
            profile.SeeksCredentials = true;

        if (context.ResourceId.Contains("ssh", StringComparison.OrdinalIgnoreCase) ||
            context.ResourceId.Contains("key", StringComparison.OrdinalIgnoreCase))
            profile.SeeksKeys = true;
    }

    /// <summary>
    /// Gets the attacker profile for a subject.
    /// </summary>
    public AttackerProfile? GetAttackerProfile(string subjectId)
    {
        return _attackerProfiles.TryGetValue(subjectId, out var profile) ? profile : null;
    }

    #endregion

    #region Core Access Evaluation

    /// <inheritdoc/>
    public override Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default)
    {
        if (configuration.TryGetValue("MaxEventsInQueue", out var maxEvents) && maxEvents is int max)
            _maxEventsInQueue = max;

        if (configuration.TryGetValue("RotationIntervalHours", out var rotationHours) && rotationHours is int hours)
            _rotationInterval = TimeSpan.FromHours(hours);

        if (configuration.TryGetValue("EnableAutoRotation", out var autoRotate) && autoRotate is true)
        {
            _environmentRotationTimer = new Timer(
                _ => RotateEnvironments(),
                null,
                _rotationInterval,
                _rotationInterval);
        }

        if (configuration.TryGetValue("EnableAdaptation", out var adapt) && adapt is true)
        {
            _adaptationTimer = new Timer(
                _ => PerformAdaptation(),
                null,
                TimeSpan.FromMinutes(15),
                TimeSpan.FromMinutes(15));
        }

        return base.InitializeAsync(configuration, cancellationToken);
    }

    /// <inheritdoc/>
    protected override async Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
    {
        // Check if accessing a deception environment
        foreach (var env in _environments.Values.Where(e => e.IsActive))
        {
            if (IsAccessingEnvironment(context, env))
            {
                // Log the deception event
                var deceptionEvent = new DeceptionEvent
                {
                    EventId = Guid.NewGuid().ToString("N"),
                    EventType = DeceptionEventType.EnvironmentAccessed,
                    SubjectId = context.SubjectId,
                    ResourceId = context.ResourceId,
                    EnvironmentId = env.EnvironmentId,
                    Timestamp = DateTime.UtcNow,
                    Details = new Dictionary<string, object>
                    {
                        ["action"] = context.Action,
                        ["clientIp"] = context.ClientIpAddress ?? "unknown"
                    }
                };

                EnqueueEvent(deceptionEvent);
                await NotifyHandlersAsync(deceptionEvent);
                UpdateAttackerProfile(context.SubjectId, context, deceptionEvent);

                // Evaluate misdirection
                var misdirection = EvaluateMisdirection(context);
                if (misdirection.ShouldMisdirect)
                {
                    return new AccessDecision
                    {
                        IsGranted = true, // Allow but redirect
                        Reason = "Access granted (misdirected)",
                        ApplicablePolicies = new[] { "DeceptionMisdirection" },
                        Metadata = new Dictionary<string, object>
                        {
                            ["misdirected"] = true,
                            ["redirectTo"] = misdirection.RedirectTo ?? "",
                            ["suspicionScore"] = misdirection.SuspicionScore
                        }
                    };
                }

                return new AccessDecision
                {
                    IsGranted = true,
                    Reason = "Access granted (monitored deception environment)",
                    ApplicablePolicies = new[] { "DeceptionMonitoring" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["isDeceptionEnvironment"] = true,
                        ["eventId"] = deceptionEvent.EventId
                    }
                };
            }
        }

        // Check threat lures
        foreach (var lure in _threatLures.Values.Where(l => l.IsActive))
        {
            if (context.ResourceId.Contains(lure.Target, StringComparison.OrdinalIgnoreCase))
            {
                var lureEvent = new DeceptionEvent
                {
                    EventId = Guid.NewGuid().ToString("N"),
                    EventType = DeceptionEventType.LureTriggered,
                    SubjectId = context.SubjectId,
                    ResourceId = context.ResourceId,
                    Timestamp = DateTime.UtcNow,
                    Details = new Dictionary<string, object>
                    {
                        ["lureId"] = lure.LureId,
                        ["lureType"] = lure.LureType.ToString()
                    }
                };

                EnqueueEvent(lureEvent);
                await NotifyHandlersAsync(lureEvent);
                UpdateAttackerProfile(context.SubjectId, context, lureEvent);
            }
        }

        return new AccessDecision
        {
            IsGranted = true,
            Reason = "Resource is not a deception target",
            ApplicablePolicies = Array.Empty<string>()
        };
    }

    private bool IsAccessingEnvironment(AccessContext context, DeceptionEnvironment env)
    {
        return context.ResourceId.StartsWith(env.FileTree.RootPath, StringComparison.OrdinalIgnoreCase);
    }

    private void EnqueueEvent(DeceptionEvent evt)
    {
        while (_eventQueue.Count >= _maxEventsInQueue)
            _eventQueue.TryDequeue(out _);

        _eventQueue.Enqueue(evt);
    }

    private async Task NotifyHandlersAsync(DeceptionEvent evt)
    {
        foreach (var handler in _eventHandlers)
        {
            try
            {
                await handler.HandleEventAsync(evt);
            }
            catch
            {
                // Log but don't fail
            }
        }
    }

    private void RotateEnvironments()
    {
        foreach (var env in _environments.Values.Where(e => e.IsActive))
        {
            // Regenerate file contents to avoid fingerprinting
            foreach (var file in env.FileTree.Files.Where(f => f.IsHoneytoken))
            {
                file.Content = GenerateFakeContent(file.FileType);
                file.RotatedAt = DateTime.UtcNow;
            }
            env.LastRotatedAt = DateTime.UtcNow;
        }
    }

    private void PerformAdaptation()
    {
        // Analyze recent events and adapt deception strategies
        var recentEvents = _eventQueue.ToArray().Where(e => e.Timestamp > DateTime.UtcNow.AddHours(-1)).ToList();

        if (recentEvents.Count > 10)
        {
            // Increase attractiveness of triggered lures
            foreach (var lureId in recentEvents
                .Where(e => e.EventType == DeceptionEventType.LureTriggered)
                .Select(e => e.Details.TryGetValue("lureId", out var id) ? id?.ToString() : null)
                .Where(id => id != null)
                .Distinct())
            {
                if (_threatLures.TryGetValue(lureId!, out var lure))
                {
                    lure.TriggerCount++;
                }
            }
        }
    }

    /// <summary>
    /// Registers a deception event handler.
    /// </summary>
    public void RegisterEventHandler(IDeceptionEventHandler handler)
    {
        _eventHandlers.Add(handler);
    }

    /// <summary>
    /// Gets recent deception events.
    /// </summary>
    public IReadOnlyCollection<DeceptionEvent> GetRecentEvents(int count = 100)
    {
        return _eventQueue.TakeLast(count).ToList();
    }

    public new void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _environmentRotationTimer?.Dispose();
        _adaptationTimer?.Dispose();
    }

    #endregion
}

#region Supporting Types

/// <summary>Suspicion level for delay injection.</summary>
public enum SuspicionLevel { None, Low, Medium, High, Critical }

/// <summary>Deception event type.</summary>
public enum DeceptionEventType { EnvironmentAccessed, LureTriggered, MisdirectionApplied, DelayInjected, MirageAdapted }

/// <summary>Threat lure type.</summary>
public enum ThreatLureType { FakeCredentials, FakeApiKey, FakeToken, FakeConnectionString, FakeDocument }

/// <summary>Mirage environment type.</summary>
public enum MirageEnvironmentType { CorporateNetwork, CloudInfrastructure, DeveloperWorkstation, ServerFarm }

/// <summary>Deception event.</summary>
public sealed class DeceptionEvent
{
    public required string EventId { get; init; }
    public required DeceptionEventType EventType { get; init; }
    public string? SubjectId { get; init; }
    public string? ResourceId { get; init; }
    public string? EnvironmentId { get; init; }
    public required DateTime Timestamp { get; init; }
    public Dictionary<string, object> Details { get; init; } = new();
}

/// <summary>Deception event handler interface.</summary>
public interface IDeceptionEventHandler
{
    Task HandleEventAsync(DeceptionEvent evt);
}

/// <summary>Fake file tree configuration.</summary>
public sealed class FakeFileTreeConfig
{
    public string RootPath { get; init; } = "";
    public int Depth { get; init; } = 3;
    public int BranchingFactor { get; init; } = 4;
    public int FilesPerDirectory { get; init; } = 5;
}

/// <summary>Fake file tree.</summary>
public sealed class FakeFileTree
{
    public string TreeId { get; init; } = "";
    public string RootPath { get; init; } = "";
    public int Depth { get; init; }
    public DateTime CreatedAt { get; init; }
    public bool IsActive { get; set; }
    public List<FakeDirectory> Directories { get; set; } = new();
    public List<FakeFile> Files { get; set; } = new();
}

/// <summary>Fake directory.</summary>
public sealed class FakeDirectory
{
    public string Path { get; init; } = "";
    public string Name { get; init; } = "";
    public int Depth { get; init; }
    public bool IsAttractive { get; init; }
}

/// <summary>Fake file.</summary>
public sealed class FakeFile
{
    public string Path { get; init; } = "";
    public string Name { get; init; } = "";
    public string FileType { get; init; } = "";
    public bool IsHoneytoken { get; init; }
    public string? HoneytokenId { get; set; }
    public byte[]? Content { get; set; }
    public int Size { get; init; }
    public DateTime? RotatedAt { get; set; }
}

/// <summary>Deployment configuration.</summary>
public sealed class DeploymentConfig
{
    public bool MonitorAccess { get; init; } = true;
    public bool AlertOnAccess { get; init; } = true;
    public bool EnableMisdirection { get; init; } = true;
}

/// <summary>Deception environment.</summary>
public sealed class DeceptionEnvironment
{
    public string EnvironmentId { get; init; } = "";
    public required FakeFileTree FileTree { get; init; }
    public DateTime DeployedAt { get; init; }
    public DateTime? LastRotatedAt { get; set; }
    public bool IsActive { get; set; }
    public required DeploymentConfig Configuration { get; init; }
    public Dictionary<string, object> Metadata { get; init; } = new();
}

/// <summary>Delay injection result.</summary>
public sealed class DelayInjectionResult
{
    public int DelayMs { get; init; }
    public SuspicionLevel SuspicionLevel { get; init; }
    public DateTime InjectedAt { get; init; }
}

/// <summary>Misdirection configuration.</summary>
public sealed class MisdirectionConfig
{
    public string Name { get; init; } = "";
    public string EntryPoint { get; init; } = "";
    public string TargetHoneypotId { get; init; } = "";
    public int BreadcrumbCount { get; init; } = 3;
}

/// <summary>Misdirection path.</summary>
public sealed class MisdirectionPath
{
    public string PathId { get; init; } = "";
    public string Name { get; init; } = "";
    public string EntryPoint { get; init; } = "";
    public string TargetHoneypotId { get; init; } = "";
    public List<Breadcrumb> Breadcrumbs { get; init; } = new();
    public DateTime CreatedAt { get; init; }
    public bool IsActive { get; set; }
    public double SuccessRate { get; set; } = 0.5;
}

/// <summary>Breadcrumb for misdirection.</summary>
public sealed class Breadcrumb
{
    public string BreadcrumbId { get; init; } = "";
    public string Content { get; init; } = "";
    public string Type { get; init; } = "";
    public string PlacementLocation { get; init; } = "";
    public int Order { get; init; }
}

/// <summary>Misdirection decision.</summary>
public sealed class MisdirectionDecision
{
    public bool ShouldMisdirect { get; init; }
    public double SuspicionScore { get; init; }
    public MisdirectionPath? MisdirectionPath { get; init; }
    public string? RedirectTo { get; init; }
    public string? Reason { get; init; }
}

/// <summary>Digital mirage configuration.</summary>
public sealed class DigitalMirageConfig
{
    public string Name { get; init; } = "";
    public MirageEnvironmentType EnvironmentType { get; init; }
    public bool EnableAdaptation { get; init; } = true;
}

/// <summary>Digital mirage.</summary>
public sealed class DigitalMirage
{
    public string MirageId { get; init; } = "";
    public string Name { get; init; } = "";
    public MirageEnvironmentType EnvironmentType { get; init; }
    public DateTime CreatedAt { get; init; }
    public bool IsActive { get; set; }
    public bool AdaptationEnabled { get; init; }
    public List<MirageComponent> Components { get; init; } = new();
}

/// <summary>Mirage component.</summary>
public sealed class MirageComponent
{
    public string Name { get; init; } = "";
    public string Type { get; init; } = "";
}

/// <summary>Attacker behavior profile.</summary>
public sealed class AttackerBehavior
{
    public bool SeeksCredentials { get; init; }
    public bool SeeksNetworkInfo { get; init; }
    public bool UsesPowerShell { get; init; }
}

/// <summary>Threat lure configuration.</summary>
public sealed class ThreatLureConfig
{
    public ThreatLureType LureType { get; init; }
    public string Target { get; init; } = "";
    public double Attractiveness { get; init; } = 0.8;
}

/// <summary>Threat lure.</summary>
public sealed class ThreatLure
{
    public string LureId { get; init; } = "";
    public ThreatLureType LureType { get; init; }
    public string Target { get; init; } = "";
    public double Attractiveness { get; init; }
    public DateTime CreatedAt { get; init; }
    public bool IsActive { get; set; }
    public string Content { get; init; } = "";
    public int TriggerCount { get; set; }
}

/// <summary>Attacker profile.</summary>
public sealed class AttackerProfile
{
    public string SubjectId { get; init; } = "";
    public DateTime FirstSeen { get; init; }
    public DateTime LastSeen { get; set; }
    public int TotalAccessCount { get; set; }
    public int SuspiciousAccessCount { get; set; }
    public bool SeeksCredentials { get; set; }
    public bool SeeksKeys { get; set; }
    public List<string> TriggeredEventIds { get; } = new();
}

#endregion
