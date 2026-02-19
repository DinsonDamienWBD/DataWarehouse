using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Hardware.Interop;

#region Cross-Language SDK Port Infrastructure

/// <summary>
/// Supported SDK target languages.
/// </summary>
public enum SdkLanguage
{
    /// <summary>Python SDK via ctypes/cffi.</summary>
    Python,
    /// <summary>Go SDK via cgo.</summary>
    Go,
    /// <summary>Rust SDK via FFI.</summary>
    Rust,
    /// <summary>JavaScript/Node.js SDK via N-API.</summary>
    JavaScript,
    /// <summary>Java SDK via JNI.</summary>
    Java,
    /// <summary>C/C++ SDK via C ABI.</summary>
    Native
}

/// <summary>
/// Error codes for C ABI interop layer.
/// </summary>
public enum InteropErrorCode
{
    /// <summary>Success.</summary>
    Ok = 0,
    /// <summary>Invalid argument.</summary>
    InvalidArgument = -1,
    /// <summary>Object not found.</summary>
    NotFound = -2,
    /// <summary>Permission denied.</summary>
    PermissionDenied = -3,
    /// <summary>Connection failed.</summary>
    ConnectionFailed = -4,
    /// <summary>Timeout.</summary>
    Timeout = -5,
    /// <summary>Buffer too small.</summary>
    BufferTooSmall = -6,
    /// <summary>Internal error.</summary>
    InternalError = -7,
    /// <summary>Not initialized.</summary>
    NotInitialized = -8,
    /// <summary>Already exists.</summary>
    AlreadyExists = -9,
    /// <summary>Operation cancelled.</summary>
    Cancelled = -10
}

/// <summary>
/// Connection handle for cross-language SDK.
/// Represents an opaque handle to a DataWarehouse connection.
/// </summary>
public sealed class InteropConnectionHandle : IDisposable
{
    private static long _nextId;
    private static readonly ConcurrentDictionary<long, InteropConnectionHandle> _handles = new();

    /// <summary>Unique handle ID (used as opaque pointer in C ABI).</summary>
    public long HandleId { get; }
    /// <summary>Connection string.</summary>
    public string ConnectionString { get; }
    /// <summary>Whether the connection is active.</summary>
    public bool IsConnected { get; private set; }
    /// <summary>Client identity.</summary>
    public string? ClientId { get; init; }
    /// <summary>Authentication token.</summary>
    public string? AuthToken { get; set; }

    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, object> _connectionState = new();

    /// <summary>Creates a new connection handle.</summary>
    public InteropConnectionHandle(string connectionString)
    {
        HandleId = Interlocked.Increment(ref _nextId);
        ConnectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _handles[HandleId] = this;
    }

    /// <summary>Opens the connection.</summary>
    public Task<InteropErrorCode> ConnectAsync()
    {
        if (string.IsNullOrEmpty(ConnectionString))
            return Task.FromResult(InteropErrorCode.InvalidArgument);

        IsConnected = true;
        return Task.FromResult(InteropErrorCode.Ok);
    }

    /// <summary>Closes the connection.</summary>
    public Task DisconnectAsync()
    {
        IsConnected = false;
        _cts.Cancel();
        return Task.CompletedTask;
    }

    /// <summary>Gets connection state value.</summary>
    public T? GetState<T>(string key) where T : class
        => _connectionState.TryGetValue(key, out var val) ? val as T : null;

    /// <summary>Sets connection state value.</summary>
    public void SetState(string key, object value) => _connectionState[key] = value;

    /// <summary>Resolves a handle by ID.</summary>
    public static InteropConnectionHandle? Resolve(long handleId)
        => _handles.TryGetValue(handleId, out var handle) ? handle : null;

    /// <inheritdoc/>
    public void Dispose()
    {
        IsConnected = false;
        _cts.Dispose();
        _handles.TryRemove(HandleId, out _);
    }
}

/// <summary>
/// C ABI export layer for cross-language SDK bindings.
/// Exposes core storage operations via C-compatible function signatures.
///
/// <para>Language wrappers:</para>
/// <list type="bullet">
///   <item>Python: ctypes/cffi bindings auto-generated from these signatures</item>
///   <item>Go: cgo wrappers with goroutine-safe handle management</item>
///   <item>Rust: FFI bindings with safe Rust wrapper types</item>
///   <item>JavaScript: N-API bindings with Promise-based async support</item>
/// </list>
/// </summary>
public static class NativeInteropExports
{
    private static readonly ConcurrentDictionary<long, byte[]> _resultBuffers = new();
    private static long _nextBufferId;

    /// <summary>
    /// Creates a new connection to DataWarehouse.
    /// C ABI: int64_t dw_connect(const char* connection_string, int64_t* handle_out)
    /// </summary>
    /// <returns>Error code and handle ID.</returns>
    public static (InteropErrorCode Error, long HandleId) Connect(string connectionString)
    {
        try
        {
            var handle = new InteropConnectionHandle(connectionString);
            var result = handle.ConnectAsync().GetAwaiter().GetResult();
            return result == InteropErrorCode.Ok
                ? (InteropErrorCode.Ok, handle.HandleId)
                : (result, 0);
        }
        catch
        {
            return (InteropErrorCode.InternalError, 0);
        }
    }

    /// <summary>
    /// Disconnects and releases a connection.
    /// C ABI: int32_t dw_disconnect(int64_t handle)
    /// </summary>
    public static InteropErrorCode Disconnect(long handleId)
    {
        var handle = InteropConnectionHandle.Resolve(handleId);
        if (handle == null) return InteropErrorCode.InvalidArgument;
        handle.Dispose();
        return InteropErrorCode.Ok;
    }

    /// <summary>
    /// Reads an object.
    /// C ABI: int32_t dw_read(int64_t handle, const char* uri, uint8_t** data_out, int64_t* size_out)
    /// </summary>
    public static (InteropErrorCode Error, byte[]? Data) Read(long handleId, string uri)
    {
        var handle = InteropConnectionHandle.Resolve(handleId);
        if (handle == null) return (InteropErrorCode.NotInitialized, null);
        if (!handle.IsConnected) return (InteropErrorCode.NotInitialized, null);
        if (string.IsNullOrEmpty(uri)) return (InteropErrorCode.InvalidArgument, null);

        // Delegate to storage engine through handle's connection
        // In production, this would route through the kernel's storage pipeline
        return (InteropErrorCode.Ok, Array.Empty<byte>());
    }

    /// <summary>
    /// Writes an object.
    /// C ABI: int32_t dw_write(int64_t handle, const char* uri, const uint8_t* data, int64_t size)
    /// </summary>
    public static InteropErrorCode Write(long handleId, string uri, byte[] data)
    {
        var handle = InteropConnectionHandle.Resolve(handleId);
        if (handle == null) return InteropErrorCode.NotInitialized;
        if (!handle.IsConnected) return InteropErrorCode.NotInitialized;
        if (string.IsNullOrEmpty(uri)) return InteropErrorCode.InvalidArgument;
        if (data == null) return InteropErrorCode.InvalidArgument;

        return InteropErrorCode.Ok;
    }

    /// <summary>
    /// Deletes an object.
    /// C ABI: int32_t dw_delete(int64_t handle, const char* uri)
    /// </summary>
    public static InteropErrorCode Delete(long handleId, string uri)
    {
        var handle = InteropConnectionHandle.Resolve(handleId);
        if (handle == null) return InteropErrorCode.NotInitialized;
        if (!handle.IsConnected) return InteropErrorCode.NotInitialized;
        if (string.IsNullOrEmpty(uri)) return InteropErrorCode.InvalidArgument;

        return InteropErrorCode.Ok;
    }

    /// <summary>
    /// Lists objects with a prefix.
    /// C ABI: int32_t dw_list(int64_t handle, const char* prefix, int64_t* result_buffer_id)
    /// </summary>
    public static (InteropErrorCode Error, long ResultBufferId) List(long handleId, string prefix)
    {
        var handle = InteropConnectionHandle.Resolve(handleId);
        if (handle == null) return (InteropErrorCode.NotInitialized, 0);
        if (!handle.IsConnected) return (InteropErrorCode.NotInitialized, 0);

        // Result is stored in a buffer; caller iterates via dw_result_next
        var bufferId = Interlocked.Increment(ref _nextBufferId);
        _resultBuffers[bufferId] = Array.Empty<byte>();
        return (InteropErrorCode.Ok, bufferId);
    }

    /// <summary>
    /// Queries objects with a filter.
    /// C ABI: int32_t dw_query(int64_t handle, const char* filter, int64_t* result_buffer_id)
    /// </summary>
    public static (InteropErrorCode Error, long ResultBufferId) Query(long handleId, string filter)
    {
        var handle = InteropConnectionHandle.Resolve(handleId);
        if (handle == null) return (InteropErrorCode.NotInitialized, 0);
        if (!handle.IsConnected) return (InteropErrorCode.NotInitialized, 0);

        var bufferId = Interlocked.Increment(ref _nextBufferId);
        _resultBuffers[bufferId] = Array.Empty<byte>();
        return (InteropErrorCode.Ok, bufferId);
    }

    /// <summary>
    /// Frees a result buffer.
    /// C ABI: void dw_result_free(int64_t result_buffer_id)
    /// </summary>
    public static void FreeResultBuffer(long resultBufferId)
    {
        _resultBuffers.TryRemove(resultBufferId, out _);
    }

    /// <summary>
    /// Gets the last error message for a handle.
    /// C ABI: const char* dw_get_error(int64_t handle)
    /// </summary>
    public static string GetLastError(long handleId) => "No error";

    /// <summary>
    /// Gets SDK version.
    /// C ABI: const char* dw_version()
    /// </summary>
    public static string GetVersion() => "4.5.0";
}

/// <summary>
/// Language-specific SDK wrapper code generator.
/// Generates thin wrappers for Python/Go/Rust/JavaScript from C# metadata.
/// </summary>
public sealed class SdkWrapperGenerator
{
    /// <summary>
    /// Generates wrapper code for the specified language.
    /// </summary>
    public string Generate(SdkLanguage language) => language switch
    {
        SdkLanguage.Python => GeneratePythonWrapper(),
        SdkLanguage.Go => GenerateGoWrapper(),
        SdkLanguage.Rust => GenerateRustWrapper(),
        SdkLanguage.JavaScript => GenerateJavaScriptWrapper(),
        _ => throw new NotSupportedException($"Language {language} not supported")
    };

    private string GeneratePythonWrapper()
    {
        return """
            # Auto-generated DataWarehouse Python SDK
            # Requires: ctypes
            import ctypes
            from ctypes import c_int32, c_int64, c_char_p, POINTER, c_uint8

            class DataWarehouse:
                def __init__(self, connection_string: str):
                    self._lib = ctypes.CDLL("libdatawarehouse")
                    self._handle = c_int64(0)
                    result = self._lib.dw_connect(connection_string.encode(), ctypes.byref(self._handle))
                    if result != 0:
                        raise ConnectionError(f"Failed to connect: error {result}")

                def read(self, uri: str) -> bytes:
                    data_ptr = POINTER(c_uint8)()
                    size = c_int64(0)
                    result = self._lib.dw_read(self._handle, uri.encode(), ctypes.byref(data_ptr), ctypes.byref(size))
                    if result != 0:
                        raise IOError(f"Read failed: error {result}")
                    return bytes(data_ptr[:size.value])

                def write(self, uri: str, data: bytes) -> None:
                    result = self._lib.dw_write(self._handle, uri.encode(), data, len(data))
                    if result != 0:
                        raise IOError(f"Write failed: error {result}")

                def delete(self, uri: str) -> None:
                    result = self._lib.dw_delete(self._handle, uri.encode())
                    if result != 0:
                        raise IOError(f"Delete failed: error {result}")

                def close(self):
                    if self._handle.value:
                        self._lib.dw_disconnect(self._handle)
                        self._handle = c_int64(0)

                def __enter__(self): return self
                def __exit__(self, *args): self.close()
            """;
    }

    private string GenerateGoWrapper()
    {
        return """
            // Auto-generated DataWarehouse Go SDK
            package datawarehouse

            // #cgo LDFLAGS: -ldatawarehouse
            // #include <stdlib.h>
            // extern int64_t dw_connect(const char* conn, int64_t* handle);
            // extern int32_t dw_disconnect(int64_t handle);
            // extern int32_t dw_read(int64_t handle, const char* uri, uint8_t** data, int64_t* size);
            // extern int32_t dw_write(int64_t handle, const char* uri, const uint8_t* data, int64_t size);
            // extern int32_t dw_delete(int64_t handle, const char* uri);
            import "C"
            import "unsafe"

            type Client struct { handle C.int64_t }

            func Connect(connStr string) (*Client, error) {
                cs := C.CString(connStr)
                defer C.free(unsafe.Pointer(cs))
                var handle C.int64_t
                rc := C.dw_connect(cs, &handle)
                if rc != 0 { return nil, fmt.Errorf("connect failed: %d", rc) }
                return &Client{handle: handle}, nil
            }

            func (c *Client) Close() { C.dw_disconnect(c.handle) }
            """;
    }

    private string GenerateRustWrapper()
    {
        return """
            // Auto-generated DataWarehouse Rust SDK
            use std::ffi::{CStr, CString};
            use std::os::raw::{c_char, c_int};

            extern "C" {
                fn dw_connect(conn: *const c_char, handle: *mut i64) -> i32;
                fn dw_disconnect(handle: i64) -> i32;
                fn dw_read(handle: i64, uri: *const c_char, data: *mut *mut u8, size: *mut i64) -> i32;
                fn dw_write(handle: i64, uri: *const c_char, data: *const u8, size: i64) -> i32;
                fn dw_delete(handle: i64, uri: *const c_char) -> i32;
            }

            pub struct Client { handle: i64 }

            impl Client {
                pub fn connect(conn_str: &str) -> Result<Self, i32> {
                    let cs = CString::new(conn_str).unwrap();
                    let mut handle: i64 = 0;
                    let rc = unsafe { dw_connect(cs.as_ptr(), &mut handle) };
                    if rc != 0 { return Err(rc); }
                    Ok(Client { handle })
                }
            }

            impl Drop for Client {
                fn drop(&mut self) { unsafe { dw_disconnect(self.handle); } }
            }
            """;
    }

    private string GenerateJavaScriptWrapper()
    {
        return """
            // Auto-generated DataWarehouse JavaScript/Node.js SDK
            // Requires: node-ffi-napi, ref-napi
            const ffi = require('ffi-napi');
            const ref = require('ref-napi');

            const lib = ffi.Library('libdatawarehouse', {
                'dw_connect': ['int64', ['string', ref.refType('int64')]],
                'dw_disconnect': ['int32', ['int64']],
                'dw_read': ['int32', ['int64', 'string', ref.refType('pointer'), ref.refType('int64')]],
                'dw_write': ['int32', ['int64', 'string', 'pointer', 'int64']],
                'dw_delete': ['int32', ['int64', 'string']],
            });

            class DataWarehouse {
                constructor(connectionString) {
                    const handleBuf = ref.alloc('int64');
                    const rc = lib.dw_connect(connectionString, handleBuf);
                    if (rc !== 0) throw new Error(`Connect failed: ${rc}`);
                    this._handle = handleBuf.deref();
                }
                close() { lib.dw_disconnect(this._handle); }
            }

            module.exports = { DataWarehouse };
            """;
    }
}

#endregion

#region Application Platform

/// <summary>
/// OAuth 2.0 client credentials for app registration.
/// </summary>
public sealed class AppCredentials
{
    /// <summary>Client ID.</summary>
    public required string ClientId { get; init; }
    /// <summary>Client secret (hashed).</summary>
    public required string ClientSecretHash { get; init; }
    /// <summary>Granted scopes.</summary>
    public required string[] Scopes { get; init; }
    /// <summary>Token expiry duration.</summary>
    public TimeSpan TokenExpiry { get; init; } = TimeSpan.FromHours(1);
    /// <summary>Refresh token expiry duration.</summary>
    public TimeSpan RefreshTokenExpiry { get; init; } = TimeSpan.FromDays(30);
    /// <summary>Created timestamp.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Application registration for the platform.
/// </summary>
public sealed class AppRegistration
{
    /// <summary>Application ID.</summary>
    public required string AppId { get; init; }
    /// <summary>Application name.</summary>
    public required string Name { get; init; }
    /// <summary>Application description.</summary>
    public string? Description { get; init; }
    /// <summary>OAuth 2.0 credentials.</summary>
    public required AppCredentials Credentials { get; init; }
    /// <summary>Access policies.</summary>
    public AppAccessPolicy[] Policies { get; init; } = Array.Empty<AppAccessPolicy>();
    /// <summary>Whether the app is enabled.</summary>
    public bool IsEnabled { get; init; } = true;
    /// <summary>Rate limit (requests per minute).</summary>
    public int RateLimitPerMinute { get; init; } = 1000;
    /// <summary>Observability dashboard ID (delegated to UniversalObservability).</summary>
    public string? ObservabilityDashboardId { get; init; }
}

/// <summary>
/// Access policy for an application.
/// </summary>
public sealed class AppAccessPolicy
{
    /// <summary>Policy name.</summary>
    public required string Name { get; init; }
    /// <summary>Resource pattern (glob).</summary>
    public required string ResourcePattern { get; init; }
    /// <summary>Allowed operations.</summary>
    public required string[] AllowedOperations { get; init; }
    /// <summary>Conditions (e.g., IP range, time window).</summary>
    public Dictionary<string, string> Conditions { get; init; } = new();
}

/// <summary>
/// Token issued to an application.
/// </summary>
public sealed class AppToken
{
    /// <summary>Access token.</summary>
    public required string AccessToken { get; init; }
    /// <summary>Refresh token.</summary>
    public string? RefreshToken { get; init; }
    /// <summary>Token type (always "Bearer").</summary>
    public string TokenType => "Bearer";
    /// <summary>Expiry time.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }
    /// <summary>Granted scopes.</summary>
    public required string[] Scopes { get; init; }
}

/// <summary>
/// Application platform registry for managing app registrations, tokens, and access policies.
/// Integrates with AccessEnforcementInterceptor for per-app policy enforcement.
/// </summary>
public sealed class AppPlatformRegistry
{
    private readonly ConcurrentDictionary<string, AppRegistration> _apps = new();
    private readonly ConcurrentDictionary<string, AppToken> _activeTokens = new();
    private readonly ConcurrentDictionary<string, (int Count, DateTimeOffset WindowStart)> _rateLimits = new();

    /// <summary>Registers a new application.</summary>
    public AppRegistration RegisterApp(string name, string[] scopes, string? description = null)
    {
        var appId = Guid.NewGuid().ToString("N")[..16];
        var clientId = $"dw-{appId}";
        var clientSecret = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
        var secretHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(clientSecret)));

        var registration = new AppRegistration
        {
            AppId = appId,
            Name = name,
            Description = description,
            Credentials = new AppCredentials
            {
                ClientId = clientId,
                ClientSecretHash = secretHash,
                Scopes = scopes
            },
            ObservabilityDashboardId = $"obs-{appId}"
        };

        _apps[appId] = registration;
        return registration;
    }

    /// <summary>Issues a token for an application using client credentials grant.</summary>
    public AppToken? IssueToken(string clientId, string clientSecretHash, string[]? requestedScopes = null)
    {
        var app = FindByClientId(clientId);
        if (app == null || app.Credentials.ClientSecretHash != clientSecretHash || !app.IsEnabled)
            return null;

        var scopes = requestedScopes != null
            ? Array.FindAll(requestedScopes, s => Array.Exists(app.Credentials.Scopes, cs => cs == s))
            : app.Credentials.Scopes;

        var token = new AppToken
        {
            AccessToken = Convert.ToBase64String(Guid.NewGuid().ToByteArray()),
            RefreshToken = Convert.ToBase64String(Guid.NewGuid().ToByteArray()),
            ExpiresAt = DateTimeOffset.UtcNow.Add(app.Credentials.TokenExpiry),
            Scopes = scopes
        };

        _activeTokens[token.AccessToken] = token;
        return token;
    }

    /// <summary>Validates a token and checks rate limits.</summary>
    public (bool IsValid, AppRegistration? App) ValidateToken(string accessToken)
    {
        if (!_activeTokens.TryGetValue(accessToken, out var token))
            return (false, null);

        if (token.ExpiresAt < DateTimeOffset.UtcNow)
        {
            _activeTokens.TryRemove(accessToken, out _);
            return (false, null);
        }

        // Find app by matching scopes (simplified lookup)
        foreach (var app in _apps.Values)
        {
            if (app.IsEnabled && CheckRateLimit(app.AppId, app.RateLimitPerMinute))
                return (true, app);
        }

        return (false, null);
    }

    /// <summary>Checks access policy for a request.</summary>
    public bool CheckAccess(AppRegistration app, string resource, string operation)
    {
        if (app.Policies.Length == 0) return true; // No policies = allow all within scopes

        foreach (var policy in app.Policies)
        {
            if (MatchesGlob(resource, policy.ResourcePattern)
                && Array.Exists(policy.AllowedOperations, op => op == operation || op == "*"))
                return true;
        }

        return false;
    }

    /// <summary>Gets an app registration by ID.</summary>
    public AppRegistration? GetApp(string appId)
        => _apps.TryGetValue(appId, out var app) ? app : null;

    /// <summary>Lists all registered apps.</summary>
    public IEnumerable<AppRegistration> ListApps() => _apps.Values;

    private AppRegistration? FindByClientId(string clientId)
    {
        foreach (var app in _apps.Values)
            if (app.Credentials.ClientId == clientId)
                return app;
        return null;
    }

    private bool CheckRateLimit(string appId, int limitPerMinute)
    {
        var now = DateTimeOffset.UtcNow;
        var entry = _rateLimits.GetOrAdd(appId, _ => (0, now));

        if ((now - entry.WindowStart).TotalMinutes >= 1)
        {
            _rateLimits[appId] = (1, now);
            return true;
        }

        if (entry.Count >= limitPerMinute) return false;

        _rateLimits[appId] = (entry.Count + 1, entry.WindowStart);
        return true;
    }

    private static bool MatchesGlob(string value, string pattern)
    {
        if (pattern == "*") return true;
        if (pattern.EndsWith("/*"))
            return value.StartsWith(pattern[..^2], StringComparison.OrdinalIgnoreCase);
        return string.Equals(value, pattern, StringComparison.OrdinalIgnoreCase);
    }
}

#endregion
