using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace DataWarehouse.Plugins.NoSqlProtocol;

/// <summary>
/// MongoDB authentication mechanisms.
/// </summary>
public enum MongoAuthMechanism
{
    /// <summary>SCRAM-SHA-1 authentication.</summary>
    ScramSha1,
    /// <summary>SCRAM-SHA-256 authentication.</summary>
    ScramSha256
}

/// <summary>
/// Represents stored credentials for MongoDB authentication.
/// </summary>
public sealed class MongoCredential
{
    /// <summary>The username.</summary>
    public required string Username { get; init; }
    /// <summary>The authentication database.</summary>
    public string Database { get; init; } = "admin";
    /// <summary>Stored key for SCRAM-SHA-1.</summary>
    public byte[]? StoredKeySha1 { get; init; }
    /// <summary>Server key for SCRAM-SHA-1.</summary>
    public byte[]? ServerKeySha1 { get; init; }
    /// <summary>Salt for SCRAM-SHA-1.</summary>
    public byte[]? SaltSha1 { get; init; }
    /// <summary>Iteration count for SCRAM-SHA-1.</summary>
    public int IterationsSha1 { get; init; } = 10000;
    /// <summary>Stored key for SCRAM-SHA-256.</summary>
    public byte[]? StoredKeySha256 { get; init; }
    /// <summary>Server key for SCRAM-SHA-256.</summary>
    public byte[]? ServerKeySha256 { get; init; }
    /// <summary>Salt for SCRAM-SHA-256.</summary>
    public byte[]? SaltSha256 { get; init; }
    /// <summary>Iteration count for SCRAM-SHA-256.</summary>
    public int IterationsSha256 { get; init; } = 15000;
    /// <summary>User roles.</summary>
    public List<string> Roles { get; init; } = new();
}

/// <summary>
/// State of an ongoing SCRAM authentication conversation.
/// </summary>
public sealed class ScramConversation
{
    /// <summary>Connection ID.</summary>
    public required string ConnectionId { get; init; }
    /// <summary>Authentication mechanism.</summary>
    public MongoAuthMechanism Mechanism { get; init; }
    /// <summary>Username being authenticated.</summary>
    public string? Username { get; set; }
    /// <summary>Authentication database.</summary>
    public string Database { get; set; } = "admin";
    /// <summary>Server nonce.</summary>
    public string? ServerNonce { get; set; }
    /// <summary>Client first message bare.</summary>
    public string? ClientFirstMessageBare { get; set; }
    /// <summary>Server first message.</summary>
    public string? ServerFirstMessage { get; set; }
    /// <summary>Current step (1, 2, 3).</summary>
    public int Step { get; set; }
    /// <summary>Whether authentication completed successfully.</summary>
    public bool Authenticated { get; set; }
    /// <summary>The credential being verified.</summary>
    public MongoCredential? Credential { get; set; }
}

/// <summary>
/// MongoDB SCRAM authentication handler.
/// Implements SCRAM-SHA-1 and SCRAM-SHA-256 authentication mechanisms.
/// Thread-safe implementation with proper credential storage.
/// </summary>
public sealed class MongoAuthenticator
{
    private readonly ConcurrentDictionary<string, MongoCredential> _credentials = new();
    private readonly ConcurrentDictionary<string, ScramConversation> _conversations = new();
    private readonly object _lock = new();

    /// <summary>
    /// Registers a new user credential.
    /// </summary>
    /// <param name="username">The username.</param>
    /// <param name="password">The plaintext password (will be hashed).</param>
    /// <param name="database">The authentication database.</param>
    /// <param name="roles">User roles.</param>
    public void RegisterCredential(string username, string password, string database = "admin", params string[] roles)
    {
        ArgumentException.ThrowIfNullOrEmpty(username);
        ArgumentException.ThrowIfNullOrEmpty(password);

        var saltSha1 = RandomNumberGenerator.GetBytes(16);
        var saltSha256 = RandomNumberGenerator.GetBytes(16);

        var (storedKeySha1, serverKeySha1) = ComputeScramKeys(username, password, saltSha1, 10000, false);
        var (storedKeySha256, serverKeySha256) = ComputeScramKeys(username, password, saltSha256, 15000, true);

        var credential = new MongoCredential
        {
            Username = username,
            Database = database,
            StoredKeySha1 = storedKeySha1,
            ServerKeySha1 = serverKeySha1,
            SaltSha1 = saltSha1,
            IterationsSha1 = 10000,
            StoredKeySha256 = storedKeySha256,
            ServerKeySha256 = serverKeySha256,
            SaltSha256 = saltSha256,
            IterationsSha256 = 15000,
            Roles = new List<string>(roles)
        };

        var key = $"{database}.{username}";
        _credentials[key] = credential;
    }

    /// <summary>
    /// Removes a user credential.
    /// </summary>
    /// <param name="username">The username.</param>
    /// <param name="database">The authentication database.</param>
    /// <returns>True if the credential was removed.</returns>
    public bool RemoveCredential(string username, string database = "admin")
    {
        var key = $"{database}.{username}";
        return _credentials.TryRemove(key, out _);
    }

    /// <summary>
    /// Gets a credential by username and database.
    /// </summary>
    /// <param name="username">The username.</param>
    /// <param name="database">The authentication database.</param>
    /// <returns>The credential or null if not found.</returns>
    public MongoCredential? GetCredential(string username, string database = "admin")
    {
        var key = $"{database}.{username}";
        return _credentials.TryGetValue(key, out var cred) ? cred : null;
    }

    /// <summary>
    /// Processes a SCRAM authentication step.
    /// </summary>
    /// <param name="connectionId">The connection ID.</param>
    /// <param name="mechanism">The authentication mechanism.</param>
    /// <param name="payload">The client payload.</param>
    /// <param name="database">The authentication database.</param>
    /// <returns>The server response document.</returns>
    public BsonDocument ProcessSaslStart(string connectionId, string mechanism, byte[] payload, string database = "admin")
    {
        var mech = mechanism switch
        {
            "SCRAM-SHA-1" => MongoAuthMechanism.ScramSha1,
            "SCRAM-SHA-256" => MongoAuthMechanism.ScramSha256,
            _ => throw new NotSupportedException($"Unsupported mechanism: {mechanism}")
        };

        var conversation = new ScramConversation
        {
            ConnectionId = connectionId,
            Mechanism = mech,
            Database = database,
            Step = 1
        };

        _conversations[connectionId] = conversation;

        return ProcessScramStep1(conversation, payload);
    }

    /// <summary>
    /// Continues a SCRAM authentication conversation.
    /// </summary>
    /// <param name="connectionId">The connection ID.</param>
    /// <param name="conversationId">The conversation ID (unused, kept for protocol compatibility).</param>
    /// <param name="payload">The client payload.</param>
    /// <returns>The server response document.</returns>
    public BsonDocument ProcessSaslContinue(string connectionId, int conversationId, byte[] payload)
    {
        if (!_conversations.TryGetValue(connectionId, out var conversation))
        {
            return new BsonDocument
            {
                ["ok"] = 0,
                ["errmsg"] = "No authentication in progress",
                ["code"] = 18,
                ["codeName"] = "AuthenticationFailed"
            };
        }

        return conversation.Step switch
        {
            1 => ProcessScramStep2(conversation, payload),
            2 => ProcessScramStep3(conversation, payload),
            _ => new BsonDocument
            {
                ["ok"] = 0,
                ["errmsg"] = "Invalid authentication state",
                ["code"] = 18,
                ["codeName"] = "AuthenticationFailed"
            }
        };
    }

    /// <summary>
    /// Checks if a connection is authenticated.
    /// </summary>
    /// <param name="connectionId">The connection ID.</param>
    /// <returns>True if authenticated.</returns>
    public bool IsAuthenticated(string connectionId)
    {
        return _conversations.TryGetValue(connectionId, out var conv) && conv.Authenticated;
    }

    /// <summary>
    /// Gets the authenticated user for a connection.
    /// </summary>
    /// <param name="connectionId">The connection ID.</param>
    /// <returns>The username or null if not authenticated.</returns>
    public string? GetAuthenticatedUser(string connectionId)
    {
        return _conversations.TryGetValue(connectionId, out var conv) && conv.Authenticated
            ? conv.Username
            : null;
    }

    /// <summary>
    /// Cleans up authentication state for a connection.
    /// </summary>
    /// <param name="connectionId">The connection ID.</param>
    public void CleanupConnection(string connectionId)
    {
        _conversations.TryRemove(connectionId, out _);
    }

    private BsonDocument ProcessScramStep1(ScramConversation conversation, byte[] payload)
    {
        var clientFirst = Encoding.UTF8.GetString(payload);
        var parts = ParseScramMessage(clientFirst);

        if (!parts.TryGetValue("n", out var username))
        {
            return CreateAuthError("Missing username");
        }

        conversation.Username = username;
        var credential = GetCredential(username, conversation.Database);

        if (credential == null)
        {
            return CreateAuthError("User not found");
        }

        conversation.Credential = credential;

        // Extract client-first-message-bare (everything after "n,,")
        var gsHeaderEnd = clientFirst.IndexOf(',', clientFirst.IndexOf(',') + 1) + 1;
        conversation.ClientFirstMessageBare = clientFirst[gsHeaderEnd..];

        // Generate server nonce
        var clientNonce = parts.GetValueOrDefault("r", "");
        var serverNonceBytes = RandomNumberGenerator.GetBytes(24);
        var serverNonce = clientNonce + Convert.ToBase64String(serverNonceBytes);
        conversation.ServerNonce = serverNonce;

        // Build server-first-message
        var (salt, iterations) = conversation.Mechanism == MongoAuthMechanism.ScramSha256
            ? (credential.SaltSha256!, credential.IterationsSha256)
            : (credential.SaltSha1!, credential.IterationsSha1);

        var serverFirst = $"r={serverNonce},s={Convert.ToBase64String(salt)},i={iterations}";
        conversation.ServerFirstMessage = serverFirst;
        conversation.Step = 2;

        return new BsonDocument
        {
            ["ok"] = 1,
            ["conversationId"] = 1,
            ["done"] = false,
            ["payload"] = Encoding.UTF8.GetBytes(serverFirst)
        };
    }

    private BsonDocument ProcessScramStep2(ScramConversation conversation, byte[] payload)
    {
        var clientFinal = Encoding.UTF8.GetString(payload);
        var parts = ParseScramMessage(clientFinal);

        if (!parts.TryGetValue("r", out var nonce) || nonce != conversation.ServerNonce)
        {
            return CreateAuthError("Invalid nonce");
        }

        if (!parts.TryGetValue("p", out var clientProofBase64))
        {
            return CreateAuthError("Missing client proof");
        }

        var clientProof = Convert.FromBase64String(clientProofBase64);

        // Get stored credentials
        var credential = conversation.Credential!;
        var (storedKey, serverKey) = conversation.Mechanism == MongoAuthMechanism.ScramSha256
            ? (credential.StoredKeySha256!, credential.ServerKeySha256!)
            : (credential.StoredKeySha1!, credential.ServerKeySha1!);

        // Build auth message
        var channelBinding = "c=biws"; // Base64 of "n,,"
        var clientFinalWithoutProof = $"{channelBinding},r={nonce}";
        var authMessage = $"{conversation.ClientFirstMessageBare},{conversation.ServerFirstMessage},{clientFinalWithoutProof}";

        // Verify client proof
        using var hmac = conversation.Mechanism == MongoAuthMechanism.ScramSha256
            ? new HMACSHA256(storedKey)
            : (HMAC)new HMACSHA1(storedKey);

        var clientSignature = hmac.ComputeHash(Encoding.UTF8.GetBytes(authMessage));
        var clientKey = XorBytes(clientProof, clientSignature);

        using var hash = conversation.Mechanism == MongoAuthMechanism.ScramSha256
            ? SHA256.Create()
            : (HashAlgorithm)SHA1.Create();

        var computedStoredKey = hash.ComputeHash(clientKey);

        if (!CryptographicOperations.FixedTimeEquals(computedStoredKey, storedKey))
        {
            return CreateAuthError("Authentication failed");
        }

        // Generate server signature
        using var serverHmac = conversation.Mechanism == MongoAuthMechanism.ScramSha256
            ? new HMACSHA256(serverKey)
            : (HMAC)new HMACSHA1(serverKey);

        var serverSignature = serverHmac.ComputeHash(Encoding.UTF8.GetBytes(authMessage));
        var serverFinal = $"v={Convert.ToBase64String(serverSignature)}";

        conversation.Step = 3;
        conversation.Authenticated = true;

        return new BsonDocument
        {
            ["ok"] = 1,
            ["conversationId"] = 1,
            ["done"] = true,
            ["payload"] = Encoding.UTF8.GetBytes(serverFinal)
        };
    }

    private BsonDocument ProcessScramStep3(ScramConversation conversation, byte[] payload)
    {
        // Final step - just confirm authentication is complete
        return new BsonDocument
        {
            ["ok"] = 1,
            ["conversationId"] = 1,
            ["done"] = true,
            ["payload"] = Array.Empty<byte>()
        };
    }

    private static (byte[] storedKey, byte[] serverKey) ComputeScramKeys(
        string username, string password, byte[] salt, int iterations, bool useSha256)
    {
        // For MongoDB, the password is pre-processed differently for SHA-1 vs SHA-256
        var passwordBytes = useSha256
            ? Encoding.UTF8.GetBytes(SaslPrepPassword(password))
            : Encoding.UTF8.GetBytes(MongoPasswordDigest(username, password));

        var saltedPassword = useSha256
            ? Rfc2898DeriveBytes.Pbkdf2(passwordBytes, salt, iterations, HashAlgorithmName.SHA256, 32)
            : Rfc2898DeriveBytes.Pbkdf2(passwordBytes, salt, iterations, HashAlgorithmName.SHA1, 20);

        using var hmac = useSha256
            ? new HMACSHA256(saltedPassword)
            : (HMAC)new HMACSHA1(saltedPassword);

        var clientKey = hmac.ComputeHash(Encoding.UTF8.GetBytes("Client Key"));
        var serverKey = hmac.ComputeHash(Encoding.UTF8.GetBytes("Server Key"));

        using var hash = useSha256
            ? SHA256.Create()
            : (HashAlgorithm)SHA1.Create();

        var storedKey = hash.ComputeHash(clientKey);

        return (storedKey, serverKey);
    }

    private static string MongoPasswordDigest(string username, string password)
    {
        using var md5 = MD5.Create();
        var digestInput = $"{username}:mongo:{password}";
        var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(digestInput));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string SaslPrepPassword(string password)
    {
        // Simplified SASLprep - in production, use a full implementation
        return password.Normalize(NormalizationForm.FormKC);
    }

    private static Dictionary<string, string> ParseScramMessage(string message)
    {
        var result = new Dictionary<string, string>();
        var parts = message.Split(',');

        foreach (var part in parts)
        {
            var eqIndex = part.IndexOf('=');
            if (eqIndex > 0)
            {
                var key = part[..eqIndex];
                var value = part[(eqIndex + 1)..];
                result[key] = value;
            }
        }

        return result;
    }

    private static byte[] XorBytes(byte[] a, byte[] b)
    {
        var result = new byte[a.Length];
        for (int i = 0; i < a.Length; i++)
        {
            result[i] = (byte)(a[i] ^ b[i]);
        }
        return result;
    }

    private static BsonDocument CreateAuthError(string message)
    {
        return new BsonDocument
        {
            ["ok"] = 0,
            ["errmsg"] = message,
            ["code"] = 18,
            ["codeName"] = "AuthenticationFailed"
        };
    }
}
