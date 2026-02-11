using System.Collections.ObjectModel;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.WasmLanguages;

/// <summary>
/// Provides language-specific SDK binding documentation for DW (DataWarehouse) host functions
/// accessible from WASM modules. Documents how each supported language can import and call
/// the standard DW WASI host function interface for storage, query, transform, and logging operations.
/// </summary>
/// <remarks>
/// <para>
/// The DW runtime exposes a set of host functions to WASM modules via the "dw" import module.
/// These functions follow the WASI host function import convention and provide the primary
/// interface for WASM-based compute functions to interact with the DataWarehouse system.
/// </para>
/// <para>
/// Each language has a different FFI/import mechanism for calling WASM host functions:
/// </para>
/// <list type="bullet">
/// <item><description>Rust: <c>extern "C"</c> blocks with safe wrapper functions</description></item>
/// <item><description>C/C++: <c>__attribute__((import_module("dw")))</c> declarations</description></item>
/// <item><description>.NET: <c>[DllImport("dw")]</c> P/Invoke-style bindings</description></item>
/// <item><description>Go (TinyGo): <c>//go:wasmimport</c> compiler directives</description></item>
/// <item><description>AssemblyScript: <c>@external("dw", ...)</c> decorator declarations</description></item>
/// <item><description>Python: WIT (WebAssembly Interface Types) bindings via componentize-py</description></item>
/// <item><description>JavaScript: Host-injected global objects via Javy runtime</description></item>
/// </list>
/// </remarks>
internal sealed class WasmLanguageSdkDocumentation
{
    /// <summary>
    /// Standard DW host functions available to all WASM modules. These are imported from the "dw"
    /// module namespace and provide storage, query, transform, and logging capabilities.
    /// </summary>
    private static readonly IReadOnlyList<(string Name, string Signature, string Description)> HostFunctions =
    [
        ("dw_storage_read",  "(key_ptr: i32, key_len: i32, buf_ptr: i32, buf_len: i32) -> i32",
            "Reads a value from DW storage by key. Returns bytes read, or -1 on error."),
        ("dw_storage_write", "(key_ptr: i32, key_len: i32, val_ptr: i32, val_len: i32) -> i32",
            "Writes a value to DW storage by key. Returns 0 on success, or -1 on error."),
        ("dw_query",         "(query_ptr: i32, query_len: i32, result_ptr: i32, result_len: i32) -> i32",
            "Executes a DW query and writes the result to the output buffer. Returns bytes written, or -1 on error."),
        ("dw_transform",     "(input_ptr: i32, input_len: i32, output_ptr: i32, output_len: i32) -> i32",
            "Transforms data using a DW pipeline. Returns bytes written to output, or -1 on error."),
        ("dw_log",           "(level: i32, msg_ptr: i32, msg_len: i32) -> void",
            "Logs a message at the specified level (0=Trace, 1=Debug, 2=Info, 3=Warn, 4=Error).")
    ];

    /// <summary>
    /// Returns DW SDK binding documentation for the specified language. Includes the standard
    /// DW host function signatures, language-specific import syntax, and a complete usage example
    /// showing how to call each host function from WASM.
    /// </summary>
    /// <param name="language">
    /// The programming language name (case-insensitive). Supported languages include:
    /// Rust, C, C++, .NET/CSharp, Go, AssemblyScript, Zig, Python, JavaScript, TypeScript,
    /// Kotlin, Swift, Java, Dart, Haskell, OCaml, Grain, MoonBit, and others.
    /// </param>
    /// <returns>
    /// A documentation string containing host function definitions, language-specific import
    /// syntax, and a complete usage example. Returns generic WASM import documentation for
    /// languages without specific binding guides.
    /// </returns>
    public static string GetBindingDocumentation(string language)
    {
        var normalized = language.Trim().ToLowerInvariant();

        return normalized switch
        {
            "rust" => GetRustBindings(),
            "c" => GetCBindings(),
            "c++" or "cpp" => GetCppBindings(),
            ".net" or "dotnet" or "csharp" or "c#" => GetDotNetBindings(),
            "go" or "tinygo" => GetGoBindings(),
            "assemblyscript" => GetAssemblyScriptBindings(),
            "zig" => GetZigBindings(),
            "python" => GetPythonBindings(),
            "javascript" or "js" => GetJavaScriptBindings(),
            "typescript" or "ts" => GetTypeScriptBindings(),
            "kotlin" => GetKotlinBindings(),
            "swift" => GetSwiftBindings(),
            "java" => GetJavaBindings(),
            "dart" => GetDartBindings(),
            "haskell" => GetHaskellBindings(),
            "ocaml" => GetOCamlBindings(),
            "grain" => GetGrainBindings(),
            "moonbit" => GetMoonBitBindings(),
            "ruby" => GetRubyBindings(),
            "php" => GetPhpBindings(),
            "lua" => GetLuaBindings(),
            "nim" => GetNimBindings(),
            "v" => GetVBindings(),
            _ => GetGenericBindings(language)
        };
    }

    /// <summary>
    /// Returns combined binding documentation for all supported languages in a structured format,
    /// suitable for generating a comprehensive SDK reference manual.
    /// </summary>
    /// <returns>A concatenated documentation string covering all supported languages.</returns>
    public static string GetAllBindingDocumentation()
    {
        var languages = new[]
        {
            "Rust", "C", "C++", ".NET", "Go", "AssemblyScript", "Zig",
            "Python", "JavaScript", "TypeScript", "Kotlin", "Swift", "Java", "Dart",
            "Haskell", "OCaml", "Grain", "MoonBit",
            "Ruby", "PHP", "Lua", "Nim", "V"
        };

        var sections = new System.Text.StringBuilder();
        sections.AppendLine("# DW SDK WASM Bindings Reference");
        sections.AppendLine();
        sections.AppendLine("## Standard DW Host Functions");
        sections.AppendLine();
        sections.AppendLine("All WASM modules import these functions from the `dw` module:");
        sections.AppendLine();

        foreach (var (name, sig, desc) in HostFunctions)
        {
            sections.AppendLine($"- `{name}{sig}` -- {desc}");
        }

        sections.AppendLine();
        sections.AppendLine("---");
        sections.AppendLine();

        foreach (var lang in languages)
        {
            sections.AppendLine($"## {lang}");
            sections.AppendLine();
            sections.AppendLine(GetBindingDocumentation(lang));
            sections.AppendLine();
            sections.AppendLine("---");
            sections.AppendLine();
        }

        return sections.ToString();
    }

    /// <summary>
    /// Returns the DW host function WIT (WebAssembly Interface Types) definitions as key-value pairs.
    /// Keys are function names; values are the WIT interface definitions in the standard WIT syntax.
    /// </summary>
    /// <returns>
    /// A read-only dictionary mapping host function names to their WIT type definitions.
    /// </returns>
    public static IReadOnlyDictionary<string, string> GetHostFunctionDefinitions()
    {
        return new ReadOnlyDictionary<string, string>(new Dictionary<string, string>
        {
            ["dw_storage_read"] = @"/// Read a value from DW storage.
/// Returns the number of bytes read, or -1 on error.
dw-storage-read: func(key: list<u8>, buf-len: u32) -> result<list<u8>, dw-error>",

            ["dw_storage_write"] = @"/// Write a value to DW storage.
/// Returns 0 on success.
dw-storage-write: func(key: list<u8>, value: list<u8>) -> result<_, dw-error>",

            ["dw_query"] = @"/// Execute a DW query.
/// Returns the query result as bytes.
dw-query: func(query: string, result-buf-len: u32) -> result<list<u8>, dw-error>",

            ["dw_transform"] = @"/// Transform data via a DW pipeline.
/// Returns the transformed output.
dw-transform: func(input: list<u8>, output-buf-len: u32) -> result<list<u8>, dw-error>",

            ["dw_log"] = @"/// Log a message at the specified level.
/// Levels: 0=Trace, 1=Debug, 2=Info, 3=Warn, 4=Error.
dw-log: func(level: dw-log-level, message: string)"
        });
    }

    #region Language-Specific Binding Documentation

    private static string GetRustBindings() =>
@"### Rust DW SDK Bindings

Rust uses `extern ""C""` blocks to declare WASM host function imports. The DW host functions
are imported from the `""dw""` module using the `#[link(wasm_import_module = ""dw"")]` attribute.

```rust
#[link(wasm_import_module = ""dw"")]
extern ""C"" {
    fn dw_storage_read(key_ptr: *const u8, key_len: u32, buf_ptr: *mut u8, buf_len: u32) -> i32;
    fn dw_storage_write(key_ptr: *const u8, key_len: u32, val_ptr: *const u8, val_len: u32) -> i32;
    fn dw_query(query_ptr: *const u8, query_len: u32, result_ptr: *mut u8, result_len: u32) -> i32;
    fn dw_transform(input_ptr: *const u8, input_len: u32, output_ptr: *mut u8, output_len: u32) -> i32;
    fn dw_log(level: i32, msg_ptr: *const u8, msg_len: u32);
}

/// Safe wrapper for DW storage read.
pub fn storage_read(key: &[u8], buf: &mut [u8]) -> Result<usize, i32> {
    let result = unsafe {
        dw_storage_read(key.as_ptr(), key.len() as u32, buf.as_mut_ptr(), buf.len() as u32)
    };
    if result < 0 { Err(result) } else { Ok(result as usize) }
}

/// Safe wrapper for DW storage write.
pub fn storage_write(key: &[u8], value: &[u8]) -> Result<(), i32> {
    let result = unsafe {
        dw_storage_write(key.as_ptr(), key.len() as u32, value.as_ptr(), value.len() as u32)
    };
    if result < 0 { Err(result) } else { Ok(()) }
}

/// Safe wrapper for DW query execution.
pub fn query(query: &str, buf: &mut [u8]) -> Result<usize, i32> {
    let bytes = query.as_bytes();
    let result = unsafe {
        dw_query(bytes.as_ptr(), bytes.len() as u32, buf.as_mut_ptr(), buf.len() as u32)
    };
    if result < 0 { Err(result) } else { Ok(result as usize) }
}

/// Safe wrapper for DW data transform.
pub fn transform(input: &[u8], output: &mut [u8]) -> Result<usize, i32> {
    let result = unsafe {
        dw_transform(input.as_ptr(), input.len() as u32, output.as_mut_ptr(), output.len() as u32)
    };
    if result < 0 { Err(result) } else { Ok(result as usize) }
}

/// Safe wrapper for DW logging.
pub fn log(level: i32, message: &str) {
    let bytes = message.as_bytes();
    unsafe { dw_log(level, bytes.as_ptr(), bytes.len() as u32); }
}
```

**Compile:** `cargo build --target wasm32-wasi --release`
**Optimize:** `wasm-opt -O3 target/wasm32-wasi/release/module.wasm -o optimized.wasm`";

    private static string GetCBindings() =>
@"### C DW SDK Bindings

C uses `__attribute__((import_module(""dw"")))` to declare WASM host function imports.
Each function is declared with the `__attribute__((import_name(""func_name"")))` attribute.

```c
#include <stdint.h>
#include <stddef.h>

/* DW Host Function Imports */
__attribute__((import_module(""dw""), import_name(""dw_storage_read"")))
extern int32_t dw_storage_read(const uint8_t* key_ptr, uint32_t key_len,
                                uint8_t* buf_ptr, uint32_t buf_len);

__attribute__((import_module(""dw""), import_name(""dw_storage_write"")))
extern int32_t dw_storage_write(const uint8_t* key_ptr, uint32_t key_len,
                                 const uint8_t* val_ptr, uint32_t val_len);

__attribute__((import_module(""dw""), import_name(""dw_query"")))
extern int32_t dw_query(const uint8_t* query_ptr, uint32_t query_len,
                         uint8_t* result_ptr, uint32_t result_len);

__attribute__((import_module(""dw""), import_name(""dw_transform"")))
extern int32_t dw_transform(const uint8_t* input_ptr, uint32_t input_len,
                             uint8_t* output_ptr, uint32_t output_len);

__attribute__((import_module(""dw""), import_name(""dw_log"")))
extern void dw_log(int32_t level, const uint8_t* msg_ptr, uint32_t msg_len);

/* Convenience wrapper */
static inline int32_t dw_log_info(const char* msg) {
    dw_log(2, (const uint8_t*)msg, strlen(msg));
    return 0;
}
```

**Compile (clang):** `clang --target=wasm32-wasi --sysroot=/path/to/wasi-sysroot -O2 -o module.wasm source.c`
**Compile (emscripten):** `emcc -O2 -s STANDALONE_WASM=1 -o module.wasm source.c`";

    private static string GetCppBindings() =>
@"### C++ DW SDK Bindings

C++ uses the same attribute-based import mechanism as C, wrapped in `extern ""C""` to prevent
name mangling. A type-safe C++ wrapper class is provided for ergonomic usage.

```cpp
#include <cstdint>
#include <cstring>
#include <string>
#include <string_view>
#include <vector>
#include <span>

extern ""C"" {
    __attribute__((import_module(""dw""), import_name(""dw_storage_read"")))
    int32_t dw_storage_read(const uint8_t* key_ptr, uint32_t key_len,
                            uint8_t* buf_ptr, uint32_t buf_len);

    __attribute__((import_module(""dw""), import_name(""dw_storage_write"")))
    int32_t dw_storage_write(const uint8_t* key_ptr, uint32_t key_len,
                             const uint8_t* val_ptr, uint32_t val_len);

    __attribute__((import_module(""dw""), import_name(""dw_query"")))
    int32_t dw_query(const uint8_t* query_ptr, uint32_t query_len,
                     uint8_t* result_ptr, uint32_t result_len);

    __attribute__((import_module(""dw""), import_name(""dw_transform"")))
    int32_t dw_transform(const uint8_t* input_ptr, uint32_t input_len,
                         uint8_t* output_ptr, uint32_t output_len);

    __attribute__((import_module(""dw""), import_name(""dw_log"")))
    void dw_log(int32_t level, const uint8_t* msg_ptr, uint32_t msg_len);
}

namespace dw {
    inline int32_t storage_read(std::string_view key, std::span<uint8_t> buf) {
        return ::dw_storage_read(
            reinterpret_cast<const uint8_t*>(key.data()), static_cast<uint32_t>(key.size()),
            buf.data(), static_cast<uint32_t>(buf.size()));
    }

    inline int32_t storage_write(std::string_view key, std::span<const uint8_t> val) {
        return ::dw_storage_write(
            reinterpret_cast<const uint8_t*>(key.data()), static_cast<uint32_t>(key.size()),
            val.data(), static_cast<uint32_t>(val.size()));
    }

    inline void log(int32_t level, std::string_view msg) {
        ::dw_log(level, reinterpret_cast<const uint8_t*>(msg.data()),
                 static_cast<uint32_t>(msg.size()));
    }
}
```

**Compile:** `clang++ --target=wasm32-wasi --sysroot=/path/to/wasi-sysroot -std=c++20 -O2 -fno-exceptions -o module.wasm source.cpp`";

    private static string GetDotNetBindings() =>
@"### .NET DW SDK Bindings

.NET uses `[DllImport]` P/Invoke-style bindings to import WASM host functions. The DW host
functions are imported from the `""dw""` module. When compiled with `dotnet-wasi-sdk`, the
`[DllImport]` attributes generate the appropriate WASM import entries.

```csharp
using System;
using System.Runtime.InteropServices;
using System.Text;

/// <summary>
/// DW host function bindings for .NET WASM modules.
/// </summary>
public static class DwSdk
{
    [DllImport(""dw"", EntryPoint = ""dw_storage_read"")]
    private static extern unsafe int DwStorageReadRaw(
        byte* keyPtr, int keyLen, byte* bufPtr, int bufLen);

    [DllImport(""dw"", EntryPoint = ""dw_storage_write"")]
    private static extern unsafe int DwStorageWriteRaw(
        byte* keyPtr, int keyLen, byte* valPtr, int valLen);

    [DllImport(""dw"", EntryPoint = ""dw_query"")]
    private static extern unsafe int DwQueryRaw(
        byte* queryPtr, int queryLen, byte* resultPtr, int resultLen);

    [DllImport(""dw"", EntryPoint = ""dw_transform"")]
    private static extern unsafe int DwTransformRaw(
        byte* inputPtr, int inputLen, byte* outputPtr, int outputLen);

    [DllImport(""dw"", EntryPoint = ""dw_log"")]
    private static extern unsafe void DwLogRaw(int level, byte* msgPtr, int msgLen);

    public static int StorageRead(ReadOnlySpan<byte> key, Span<byte> buffer)
    {
        unsafe
        {
            fixed (byte* keyPtr = key)
            fixed (byte* bufPtr = buffer)
            {
                return DwStorageReadRaw(keyPtr, key.Length, bufPtr, buffer.Length);
            }
        }
    }

    public static int StorageWrite(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        unsafe
        {
            fixed (byte* keyPtr = key)
            fixed (byte* valPtr = value)
            {
                return DwStorageWriteRaw(keyPtr, key.Length, valPtr, value.Length);
            }
        }
    }

    public static void Log(int level, string message)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        unsafe
        {
            fixed (byte* ptr = bytes)
            {
                DwLogRaw(level, ptr, bytes.Length);
            }
        }
    }
}
```

**Compile:** `dotnet build -c Release -r wasi-wasm`
**Requires:** `Wasi.Sdk` NuGet package or `dotnet-wasi-sdk` workload";

    private static string GetGoBindings() =>
@"### Go (TinyGo) DW SDK Bindings

Go uses `//go:wasmimport` compiler directives (available in TinyGo and Go 1.21+) to declare
WASM host function imports. Each function is declared with the module and function name.

```go
package dw

import ""unsafe""

//go:wasmimport dw dw_storage_read
//go:noescape
func dwStorageRead(keyPtr unsafe.Pointer, keyLen uint32, bufPtr unsafe.Pointer, bufLen uint32) int32

//go:wasmimport dw dw_storage_write
//go:noescape
func dwStorageWrite(keyPtr unsafe.Pointer, keyLen uint32, valPtr unsafe.Pointer, valLen uint32) int32

//go:wasmimport dw dw_query
//go:noescape
func dwQuery(queryPtr unsafe.Pointer, queryLen uint32, resultPtr unsafe.Pointer, resultLen uint32) int32

//go:wasmimport dw dw_transform
//go:noescape
func dwTransform(inputPtr unsafe.Pointer, inputLen uint32, outputPtr unsafe.Pointer, outputLen uint32) int32

//go:wasmimport dw dw_log
//go:noescape
func dwLog(level int32, msgPtr unsafe.Pointer, msgLen uint32)

// StorageRead reads a value from DW storage by key.
func StorageRead(key string, buf []byte) (int, error) {
    keyBytes := []byte(key)
    result := dwStorageRead(
        unsafe.Pointer(&keyBytes[0]), uint32(len(keyBytes)),
        unsafe.Pointer(&buf[0]), uint32(len(buf)))
    if result < 0 {
        return 0, fmt.Errorf(""dw_storage_read failed with code %d"", result)
    }
    return int(result), nil
}

// StorageWrite writes a value to DW storage by key.
func StorageWrite(key string, value []byte) error {
    keyBytes := []byte(key)
    result := dwStorageWrite(
        unsafe.Pointer(&keyBytes[0]), uint32(len(keyBytes)),
        unsafe.Pointer(&value[0]), uint32(len(value)))
    if result < 0 {
        return fmt.Errorf(""dw_storage_write failed with code %d"", result)
    }
    return nil
}

// Log writes a message to the DW log at the specified level.
func Log(level int32, msg string) {
    msgBytes := []byte(msg)
    dwLog(level, unsafe.Pointer(&msgBytes[0]), uint32(len(msgBytes)))
}
```

**Compile (TinyGo):** `tinygo build -target=wasi -o module.wasm main.go`
**Compile (Go 1.21+):** `GOOS=wasip1 GOARCH=wasm go build -o module.wasm main.go`";

    private static string GetAssemblyScriptBindings() =>
@"### AssemblyScript DW SDK Bindings

AssemblyScript uses `@external` decorators to declare WASM host function imports.
Functions are imported from the `""dw""` namespace with explicit function names.

```typescript
// dw.ts - DW SDK bindings for AssemblyScript

@external(""dw"", ""dw_storage_read"")
declare function dwStorageRead(keyPtr: usize, keyLen: u32, bufPtr: usize, bufLen: u32): i32;

@external(""dw"", ""dw_storage_write"")
declare function dwStorageWrite(keyPtr: usize, keyLen: u32, valPtr: usize, valLen: u32): i32;

@external(""dw"", ""dw_query"")
declare function dwQuery(queryPtr: usize, queryLen: u32, resultPtr: usize, resultLen: u32): i32;

@external(""dw"", ""dw_transform"")
declare function dwTransform(inputPtr: usize, inputLen: u32, outputPtr: usize, outputLen: u32): i32;

@external(""dw"", ""dw_log"")
declare function dwLog(level: i32, msgPtr: usize, msgLen: u32): void;

/** Read a value from DW storage by key. */
export function storageRead(key: string): ArrayBuffer | null {
    const keyBuf = String.UTF8.encode(key);
    const resultBuf = new ArrayBuffer(65536);
    const bytesRead = dwStorageRead(
        changetype<usize>(keyBuf), keyBuf.byteLength,
        changetype<usize>(resultBuf), resultBuf.byteLength);
    if (bytesRead < 0) return null;
    return resultBuf.slice(0, bytesRead);
}

/** Write a value to DW storage by key. */
export function storageWrite(key: string, value: ArrayBuffer): bool {
    const keyBuf = String.UTF8.encode(key);
    const result = dwStorageWrite(
        changetype<usize>(keyBuf), keyBuf.byteLength,
        changetype<usize>(value), value.byteLength);
    return result >= 0;
}

/** Log a message at the specified level. */
export function log(level: i32, message: string): void {
    const msgBuf = String.UTF8.encode(message);
    dwLog(level, changetype<usize>(msgBuf), msgBuf.byteLength);
}
```

**Compile:** `asc assembly/index.ts --target release --outFile module.wasm --optimize`";

    private static string GetZigBindings() =>
@"### Zig DW SDK Bindings

Zig uses its built-in WASM import mechanism with `extern` functions and the `callconv(.c)` convention.
The `@""dw""` namespace is specified using Zig's string literal import syntax.

```zig
const std = @import(""std"");

// DW Host Function Imports
extern ""dw"" fn dw_storage_read(key_ptr: [*]const u8, key_len: u32, buf_ptr: [*]u8, buf_len: u32) callconv(.c) i32;
extern ""dw"" fn dw_storage_write(key_ptr: [*]const u8, key_len: u32, val_ptr: [*]const u8, val_len: u32) callconv(.c) i32;
extern ""dw"" fn dw_query(query_ptr: [*]const u8, query_len: u32, result_ptr: [*]u8, result_len: u32) callconv(.c) i32;
extern ""dw"" fn dw_transform(input_ptr: [*]const u8, input_len: u32, output_ptr: [*]u8, output_len: u32) callconv(.c) i32;
extern ""dw"" fn dw_log(level: i32, msg_ptr: [*]const u8, msg_len: u32) callconv(.c) void;

/// Read from DW storage. Returns bytes read or error.
pub fn storageRead(key: []const u8, buf: []u8) !usize {
    const result = dw_storage_read(key.ptr, @intCast(key.len), buf.ptr, @intCast(buf.len));
    if (result < 0) return error.StorageReadFailed;
    return @intCast(result);
}

/// Write to DW storage.
pub fn storageWrite(key: []const u8, value: []const u8) !void {
    const result = dw_storage_write(key.ptr, @intCast(key.len), value.ptr, @intCast(value.len));
    if (result < 0) return error.StorageWriteFailed;
}

/// Log a message at the given level.
pub fn log(level: i32, msg: []const u8) void {
    dw_log(level, msg.ptr, @intCast(msg.len));
}

/// Execute a DW query.
pub fn query(q: []const u8, result_buf: []u8) !usize {
    const result = dw_query(q.ptr, @intCast(q.len), result_buf.ptr, @intCast(result_buf.len));
    if (result < 0) return error.QueryFailed;
    return @intCast(result);
}
```

**Compile:** `zig build-exe -target wasm32-wasi -O ReleaseFast src/main.zig`";

    private static string GetPythonBindings() =>
@"### Python DW SDK Bindings (componentize-py)

Python WASM modules use WIT (WebAssembly Interface Types) bindings generated by
`componentize-py`. The DW host functions are defined in a WIT file and accessed
through generated Python bindings.

**WIT Definition (dw.wit):**
```wit
package dw:sdk@0.1.0;

interface storage {
    read: func(key: list<u8>, buf-len: u32) -> result<list<u8>, string>;
    write: func(key: list<u8>, value: list<u8>) -> result<_, string>;
}

interface query {
    execute: func(query: string, result-buf-len: u32) -> result<list<u8>, string>;
}

interface transform {
    apply: func(input: list<u8>, output-buf-len: u32) -> result<list<u8>, string>;
}

interface logging {
    enum log-level { trace, debug, info, warn, error }
    log: func(level: log-level, message: string);
}

world dw-module {
    import storage;
    import query;
    import transform;
    import logging;
    export run: func() -> result<list<u8>, string>;
}
```

**Python Usage:**
```python
# app.py - DW SDK usage via componentize-py generated bindings
from dw_sdk import storage, query, transform, logging
from dw_sdk.types import LogLevel

def run():
    # Entry point for the DW WASM module.
    logging.log(LogLevel.INFO, ""Starting DW compute function"")

    # Read from storage
    data = storage.read(b""my-key"", 65536)

    # Execute a query
    result = query.execute(""SELECT * FROM dataset LIMIT 10"", 65536)

    # Transform data
    transformed = transform.apply(data, 65536)

    # Write result back
    storage.write(b""output-key"", transformed)

    logging.log(LogLevel.INFO, ""Compute function completed"")
    return transformed
```

**Compile:** `componentize-py -d dw.wit -w dw-module componentize app -o module.wasm`
**Requires:** `pip install componentize-py`";

    private static string GetJavaScriptBindings() =>
@"### JavaScript DW SDK Bindings (Javy / SpiderMonkey)

JavaScript WASM modules compiled via Javy (Shopify's JS-to-WASM compiler) access DW host
functions through host-provided global objects injected at runtime. The `Dw` global is
available in the WASM execution context.

```javascript
// module.js - DW SDK usage via Javy runtime globals

// The DW runtime injects the `Dw` global object into the JavaScript context.
// Each function corresponds to a DW host function.

/**
 * Read a value from DW storage.
 * @param {string} key - The storage key.
 * @returns {Uint8Array|null} The value bytes, or null on error.
 */
function storageRead(key) {
    const keyBytes = new TextEncoder().encode(key);
    const result = Dw.storageRead(keyBytes);
    return result;
}

/**
 * Write a value to DW storage.
 * @param {string} key - The storage key.
 * @param {Uint8Array} value - The value bytes.
 * @returns {boolean} True on success.
 */
function storageWrite(key, value) {
    const keyBytes = new TextEncoder().encode(key);
    return Dw.storageWrite(keyBytes, value) >= 0;
}

/**
 * Execute a DW query.
 * @param {string} queryStr - The query string.
 * @returns {Uint8Array|null} The result bytes, or null on error.
 */
function query(queryStr) {
    const queryBytes = new TextEncoder().encode(queryStr);
    return Dw.query(queryBytes);
}

/**
 * Transform data via a DW pipeline.
 * @param {Uint8Array} input - The input data.
 * @returns {Uint8Array|null} The transformed output, or null on error.
 */
function transform(input) {
    return Dw.transform(input);
}

/**
 * Log a message. Levels: 0=Trace, 1=Debug, 2=Info, 3=Warn, 4=Error.
 * @param {number} level - The log level.
 * @param {string} message - The message to log.
 */
function log(level, message) {
    Dw.log(level, message);
}

// Entry point
const input = storageRead(""input-key"");
if (input) {
    const output = transform(input);
    if (output) {
        storageWrite(""output-key"", output);
        log(2, ""Transform complete"");
    }
}
```

**Compile:** `javy compile module.js -o module.wasm`
**Requires:** Javy CLI (`cargo install javy-cli`)";

    private static string GetTypeScriptBindings() =>
@"### TypeScript DW SDK Bindings

TypeScript WASM modules are compiled via AssemblyScript (for direct WASM compilation) or
via Javy (transpiled to JS first). The AssemblyScript path provides stronger typing.

```typescript
// dw-sdk.ts - Type-safe DW SDK bindings for TypeScript/AssemblyScript

@external(""dw"", ""dw_storage_read"")
declare function _dwStorageRead(keyPtr: usize, keyLen: u32, bufPtr: usize, bufLen: u32): i32;

@external(""dw"", ""dw_storage_write"")
declare function _dwStorageWrite(keyPtr: usize, keyLen: u32, valPtr: usize, valLen: u32): i32;

@external(""dw"", ""dw_query"")
declare function _dwQuery(queryPtr: usize, queryLen: u32, resultPtr: usize, resultLen: u32): i32;

@external(""dw"", ""dw_transform"")
declare function _dwTransform(inputPtr: usize, inputLen: u32, outputPtr: usize, outputLen: u32): i32;

@external(""dw"", ""dw_log"")
declare function _dwLog(level: i32, msgPtr: usize, msgLen: u32): void;

export enum LogLevel { Trace = 0, Debug = 1, Info = 2, Warn = 3, Error = 4 }

export class DwSdk {
    static storageRead(key: string): ArrayBuffer | null {
        const keyBuf = String.UTF8.encode(key);
        const resultBuf = new ArrayBuffer(65536);
        const n = _dwStorageRead(changetype<usize>(keyBuf), keyBuf.byteLength,
                                  changetype<usize>(resultBuf), resultBuf.byteLength);
        return n >= 0 ? resultBuf.slice(0, n) : null;
    }

    static storageWrite(key: string, value: ArrayBuffer): boolean {
        const keyBuf = String.UTF8.encode(key);
        return _dwStorageWrite(changetype<usize>(keyBuf), keyBuf.byteLength,
                               changetype<usize>(value), value.byteLength) >= 0;
    }

    static log(level: LogLevel, message: string): void {
        const msgBuf = String.UTF8.encode(message);
        _dwLog(level, changetype<usize>(msgBuf), msgBuf.byteLength);
    }
}
```

**Compile (AssemblyScript):** `asc assembly/index.ts --target release --outFile module.wasm`
**Compile (Javy):** `npx tsc && javy compile dist/module.js -o module.wasm`";

    private static string GetKotlinBindings() =>
@"### Kotlin DW SDK Bindings (Kotlin/Wasm)

Kotlin/Wasm uses `@WasmImport` annotations to declare host function imports. The DW host
functions are imported from the `""dw""` module.

```kotlin
// DwSdk.kt - DW host function bindings for Kotlin/Wasm

@WasmImport(""dw"", ""dw_storage_read"")
external fun dwStorageRead(keyPtr: Int, keyLen: Int, bufPtr: Int, bufLen: Int): Int

@WasmImport(""dw"", ""dw_storage_write"")
external fun dwStorageWrite(keyPtr: Int, keyLen: Int, valPtr: Int, valLen: Int): Int

@WasmImport(""dw"", ""dw_query"")
external fun dwQuery(queryPtr: Int, queryLen: Int, resultPtr: Int, resultLen: Int): Int

@WasmImport(""dw"", ""dw_log"")
external fun dwLog(level: Int, msgPtr: Int, msgLen: Int)

// High-level wrapper using Kotlin/Wasm memory management
object DwSdk {
    fun log(level: Int, message: String) {
        val bytes = message.encodeToByteArray()
        // Pin bytes and pass pointer via Kotlin/Wasm interop
        dwLog(level, bytes.toWasmPtr(), bytes.size)
    }
}
```

**Compile:** `kotlinc-wasm -o module.wasm main.kt`
**Requires:** Kotlin/Wasm toolchain (experimental)";

    private static string GetSwiftBindings() =>
@"### Swift DW SDK Bindings (SwiftWasm)

Swift uses `@_silgen_name` attributes and `@_extern(wasm)` to declare WASM host function imports
via the SwiftWasm toolchain.

```swift
// DwSdk.swift - DW host function bindings for SwiftWasm

@_extern(wasm, module: ""dw"", name: ""dw_storage_read"")
func _dwStorageRead(_ keyPtr: UnsafePointer<UInt8>, _ keyLen: UInt32,
                     _ bufPtr: UnsafeMutablePointer<UInt8>, _ bufLen: UInt32) -> Int32

@_extern(wasm, module: ""dw"", name: ""dw_storage_write"")
func _dwStorageWrite(_ keyPtr: UnsafePointer<UInt8>, _ keyLen: UInt32,
                      _ valPtr: UnsafePointer<UInt8>, _ valLen: UInt32) -> Int32

@_extern(wasm, module: ""dw"", name: ""dw_query"")
func _dwQuery(_ queryPtr: UnsafePointer<UInt8>, _ queryLen: UInt32,
               _ resultPtr: UnsafeMutablePointer<UInt8>, _ resultLen: UInt32) -> Int32

@_extern(wasm, module: ""dw"", name: ""dw_log"")
func _dwLog(_ level: Int32, _ msgPtr: UnsafePointer<UInt8>, _ msgLen: UInt32)

struct DwSdk {
    static func storageRead(key: String) -> Data? {
        var buffer = [UInt8](repeating: 0, count: 65536)
        let result = key.utf8CString.withUnsafeBufferPointer { keyBuf in
            buffer.withUnsafeMutableBufferPointer { outBuf in
                _dwStorageRead(UnsafePointer(keyBuf.baseAddress!.assumingMemoryBound(to: UInt8.self)),
                               UInt32(key.utf8.count),
                               outBuf.baseAddress!, UInt32(outBuf.count))
            }
        }
        guard result >= 0 else { return nil }
        return Data(buffer.prefix(Int(result)))
    }

    static func log(_ level: Int32, _ message: String) {
        message.utf8CString.withUnsafeBufferPointer { buf in
            _dwLog(level, UnsafePointer(buf.baseAddress!.assumingMemoryBound(to: UInt8.self)),
                   UInt32(message.utf8.count))
        }
    }
}
```

**Compile:** `swiftc -target wasm32-unknown-wasi -O -o module.wasm main.swift`
**Requires:** SwiftWasm toolchain";

    private static string GetJavaBindings() =>
@"### Java DW SDK Bindings (TeaVM / JWebAssembly)

Java compiles to WASM via TeaVM (bytecode-to-WASM AOT compiler) or JWebAssembly.
Host function imports use TeaVM's `@Import` annotation.

```java
import org.teavm.interop.Import;
import org.teavm.interop.Address;

public class DwSdk {
    @Import(module = ""dw"", name = ""dw_storage_read"")
    private static native int dwStorageRead(Address keyPtr, int keyLen, Address bufPtr, int bufLen);

    @Import(module = ""dw"", name = ""dw_storage_write"")
    private static native int dwStorageWrite(Address keyPtr, int keyLen, Address valPtr, int valLen);

    @Import(module = ""dw"", name = ""dw_query"")
    private static native int dwQuery(Address queryPtr, int queryLen, Address resultPtr, int resultLen);

    @Import(module = ""dw"", name = ""dw_transform"")
    private static native int dwTransform(Address inputPtr, int inputLen, Address outputPtr, int outputLen);

    @Import(module = ""dw"", name = ""dw_log"")
    private static native void dwLog(int level, Address msgPtr, int msgLen);

    public static byte[] storageRead(String key) {
        byte[] keyBytes = key.getBytes(java.nio.charset.StandardCharsets.UTF_8);
        byte[] buffer = new byte[65536];
        // TeaVM handles address pinning for managed arrays
        int result = dwStorageRead(Address.ofData(keyBytes), keyBytes.length,
                                   Address.ofData(buffer), buffer.length);
        if (result < 0) return null;
        return java.util.Arrays.copyOf(buffer, result);
    }

    public static void log(int level, String message) {
        byte[] msgBytes = message.getBytes(java.nio.charset.StandardCharsets.UTF_8);
        dwLog(level, Address.ofData(msgBytes), msgBytes.length);
    }
}
```

**Compile (TeaVM):** `mvn package -P wasm` (with TeaVM Maven plugin)
**Compile (JWebAssembly):** `java -jar jwebassembly.jar -o module.wasm Main.class`";

    private static string GetDartBindings() =>
@"### Dart DW SDK Bindings (dart2wasm)

Dart compiles to WASM via the `dart2wasm` compiler (Dart 3.0+). Host function imports
use the `@pragma('wasm:import')` annotation.

```dart
// dw_sdk.dart - DW host function bindings for Dart/Wasm

import 'dart:typed_data';
import 'dart:convert';

@pragma('wasm:import', 'dw', 'dw_storage_read')
external int _dwStorageRead(int keyPtr, int keyLen, int bufPtr, int bufLen);

@pragma('wasm:import', 'dw', 'dw_storage_write')
external int _dwStorageWrite(int keyPtr, int keyLen, int valPtr, int valLen);

@pragma('wasm:import', 'dw', 'dw_query')
external int _dwQuery(int queryPtr, int queryLen, int resultPtr, int resultLen);

@pragma('wasm:import', 'dw', 'dw_log')
external void _dwLog(int level, int msgPtr, int msgLen);

class DwSdk {
  static Uint8List? storageRead(String key) {
    final keyBytes = utf8.encode(key);
    // Dart/Wasm handles linear memory pointer conversion
    final result = _dwStorageRead(
      keyBytes.address, keyBytes.length,
      _buffer.address, _buffer.length);
    if (result < 0) return null;
    return Uint8List.fromList(_buffer.sublist(0, result));
  }

  static bool storageWrite(String key, Uint8List value) {
    final keyBytes = utf8.encode(key);
    return _dwStorageWrite(
      keyBytes.address, keyBytes.length,
      value.address, value.length) >= 0;
  }

  static void log(int level, String message) {
    final msgBytes = utf8.encode(message);
    _dwLog(level, msgBytes.address, msgBytes.length);
  }

  static final _buffer = Uint8List(65536);
}
```

**Compile:** `dart compile wasm main.dart -o module.wasm`
**Requires:** Dart SDK 3.0+";

    private static string GetHaskellBindings() =>
@"### Haskell DW SDK Bindings (WASM Backend / Asterius)

Haskell compiles to WASM via the GHC WASM backend (GHC 9.6+) or Asterius. Host function
imports use Haskell's Foreign Function Interface (FFI) with the `ccall` calling convention.

```haskell
{-# LANGUAGE ForeignFunctionInterface #-}

module DwSdk where

import Foreign (Ptr, Word8, Int32, castPtr, allocaBytes, peekArray, pokeArray)
import Foreign.C.String (CString, withCString)
import qualified Data.ByteString as BS

-- Raw FFI imports from the ""dw"" module
foreign import ccall ""dw_storage_read""
    c_dw_storage_read :: Ptr Word8 -> Int32 -> Ptr Word8 -> Int32 -> IO Int32

foreign import ccall ""dw_storage_write""
    c_dw_storage_write :: Ptr Word8 -> Int32 -> Ptr Word8 -> Int32 -> IO Int32

foreign import ccall ""dw_query""
    c_dw_query :: Ptr Word8 -> Int32 -> Ptr Word8 -> Int32 -> IO Int32

foreign import ccall ""dw_log""
    c_dw_log :: Int32 -> Ptr Word8 -> Int32 -> IO ()

-- High-level wrapper
storageRead :: BS.ByteString -> IO (Maybe BS.ByteString)
storageRead key = BS.useAsCStringLen key $ \(keyPtr, keyLen) ->
    allocaBytes 65536 $ \bufPtr -> do
        result <- c_dw_storage_read (castPtr keyPtr) (fromIntegral keyLen)
                                     bufPtr 65536
        if result < 0
            then return Nothing
            else Just . BS.pack <$> peekArray (fromIntegral result) bufPtr

dwLog :: Int32 -> String -> IO ()
dwLog level msg = withCString msg $ \cstr ->
    c_dw_log level (castPtr cstr) (fromIntegral $ length msg)
```

**Compile (GHC WASM):** `wasm32-wasi-ghc -O2 -o module.wasm Main.hs`
**Compile (Asterius):** `ahc-link --input-hs Main.hs --output-wasm module.wasm`";

    private static string GetOCamlBindings() =>
@"### OCaml DW SDK Bindings (wasm_of_ocaml)

OCaml compiles to WASM via `wasm_of_ocaml` (from the js_of_ocaml ecosystem). Host function
imports use OCaml's C FFI interop mechanism with `external` declarations.

```ocaml
(* dw_sdk.ml - DW host function bindings for OCaml/Wasm *)

(* Raw FFI imports -- these map to WASM imports from the ""dw"" module *)
external dw_storage_read : Bytes.t -> int -> Bytes.t -> int -> int
  = ""dw_storage_read""
external dw_storage_write : Bytes.t -> int -> Bytes.t -> int -> int
  = ""dw_storage_write""
external dw_query : Bytes.t -> int -> Bytes.t -> int -> int
  = ""dw_query""
external dw_log : int -> Bytes.t -> int -> unit
  = ""dw_log""

(* High-level wrappers *)
let storage_read key =
  let buf = Bytes.create 65536 in
  let key_bytes = Bytes.of_string key in
  let result = dw_storage_read key_bytes (Bytes.length key_bytes) buf (Bytes.length buf) in
  if result < 0 then None
  else Some (Bytes.sub_string buf 0 result)

let storage_write key value =
  let key_bytes = Bytes.of_string key in
  let val_bytes = Bytes.of_string value in
  dw_storage_write key_bytes (Bytes.length key_bytes) val_bytes (Bytes.length val_bytes) >= 0

let log level message =
  let msg_bytes = Bytes.of_string message in
  dw_log level msg_bytes (Bytes.length msg_bytes)
```

**Compile:** `ocamlfind ocamlopt -package wasm_of_ocaml -linkpkg main.ml -o main.byte && wasm_of_ocaml main.byte -o module.wasm`";

    private static string GetGrainBindings() =>
@"### Grain DW SDK Bindings

Grain is a WASM-native language designed specifically for WebAssembly. It compiles directly
to WASM with built-in foreign function import support.

```grain
// dw_sdk.gr - DW host function bindings for Grain

// Grain uses the `foreign wasm` syntax for host imports
@unsafe
provide foreign wasm dw_storage_read:
  (WasmI32, WasmI32, WasmI32, WasmI32) -> WasmI32 from ""dw""

@unsafe
provide foreign wasm dw_storage_write:
  (WasmI32, WasmI32, WasmI32, WasmI32) -> WasmI32 from ""dw""

@unsafe
provide foreign wasm dw_query:
  (WasmI32, WasmI32, WasmI32, WasmI32) -> WasmI32 from ""dw""

@unsafe
provide foreign wasm dw_log:
  (WasmI32, WasmI32, WasmI32) -> Void from ""dw""

// Higher-level Grain wrappers
provide let logInfo = (message: String) => {
  @unsafe
  let msgPtr = WasmI32.fromGrain(message)
  @unsafe
  let msgLen = WasmI32.load(msgPtr, 4n)
  @unsafe
  dw_log(2n, WasmI32.add(msgPtr, 8n), msgLen)
}
```

**Compile:** `grain compile --release -o module.wasm main.gr`";

    private static string GetMoonBitBindings() =>
@"### MoonBit DW SDK Bindings

MoonBit is a WASM-native language with first-class WebAssembly support. It compiles directly
to WASM with built-in FFI for host function imports.

```moonbit
// dw_sdk.mbt - DW host function bindings for MoonBit

// MoonBit uses extern declarations for WASM host imports
extern ""wasm"" fn dw_storage_read(key_ptr : Int, key_len : Int, buf_ptr : Int, buf_len : Int) -> Int =
  ""dw"" ""dw_storage_read""

extern ""wasm"" fn dw_storage_write(key_ptr : Int, key_len : Int, val_ptr : Int, val_len : Int) -> Int =
  ""dw"" ""dw_storage_write""

extern ""wasm"" fn dw_query(query_ptr : Int, query_len : Int, result_ptr : Int, result_len : Int) -> Int =
  ""dw"" ""dw_query""

extern ""wasm"" fn dw_log(level : Int, msg_ptr : Int, msg_len : Int) =
  ""dw"" ""dw_log""

// High-level wrappers
pub fn log_info(message : String) -> Unit {
  let bytes = message.to_bytes()
  dw_log(2, bytes.ptr(), bytes.length())
}

pub fn storage_read(key : String) -> Bytes? {
  let key_bytes = key.to_bytes()
  let buf = Bytes::new(65536)
  let result = dw_storage_read(key_bytes.ptr(), key_bytes.length(), buf.ptr(), buf.length())
  if result < 0 { None } else { Some(buf.sub(0, result)) }
}
```

**Compile:** `moon build --target wasm -o module.wasm`";

    private static string GetRubyBindings() =>
@"### Ruby DW SDK Bindings (ruby.wasm)

Ruby runs in WASM via the `ruby.wasm` project which bundles the CRuby interpreter into
a WASM module. Host functions are accessed through the `DW` module injected by the runtime.

```ruby
# dw_sdk.rb - DW SDK bindings for Ruby/Wasm
# The DW runtime injects the DW module as a host-provided extension.

module DW
  module SDK
    # Read a value from DW storage by key.
    def self.storage_read(key)
      DW::Native.storage_read(key.encode('UTF-8'))
    end

    # Write a value to DW storage.
    def self.storage_write(key, value)
      DW::Native.storage_write(key.encode('UTF-8'), value)
    end

    # Execute a DW query.
    def self.query(query_str)
      DW::Native.query(query_str.encode('UTF-8'))
    end

    # Log a message (levels: 0=trace, 1=debug, 2=info, 3=warn, 4=error)
    def self.log(level, message)
      DW::Native.log(level, message.encode('UTF-8'))
    end
  end
end
```

**Compile:** `rbwasm build -o module.wasm`
**Requires:** ruby.wasm toolchain";

    private static string GetPhpBindings() =>
@"### PHP DW SDK Bindings (php-wasm)

PHP runs in WASM via the `php-wasm` project which compiles the PHP interpreter to WASM
using Emscripten. Host functions are accessed through a PHP extension bridge.

```php
<?php
// dw_sdk.php - DW SDK bindings for PHP/Wasm
// Host functions are exposed via the dw_* extension functions.

class DwSdk {
    /** Read a value from DW storage. */
    public static function storageRead(string $key): ?string {
        $result = dw_storage_read($key);
        return $result !== false ? $result : null;
    }

    /** Write a value to DW storage. */
    public static function storageWrite(string $key, string $value): bool {
        return dw_storage_write($key, $value) >= 0;
    }

    /** Execute a DW query. */
    public static function query(string $query): ?string {
        $result = dw_query($query);
        return $result !== false ? $result : null;
    }

    /** Log a message at the specified level. */
    public static function log(int $level, string $message): void {
        dw_log($level, $message);
    }
}
```

**Compile:** `emcc -O2 -s STANDALONE_WASM=1 php-src/sapi/cli/php_cli.c -o php.wasm`
**Requires:** Emscripten, php-wasm build system";

    private static string GetLuaBindings() =>
@"### Lua DW SDK Bindings (wasmoon)

Lua runs in WASM via `wasmoon` (Lua interpreter compiled to WASM) or direct Emscripten
compilation of the Lua C source. Host functions are accessed through the `dw` Lua table.

```lua
-- dw_sdk.lua - DW SDK bindings for Lua/Wasm
-- The DW runtime injects the 'dw' table as a host-provided module.

local dw = require('dw')

local M = {}

--- Read a value from DW storage.
function M.storage_read(key)
    local result, err = dw.storage_read(key)
    if err then return nil, err end
    return result
end

--- Write a value to DW storage.
function M.storage_write(key, value)
    local code = dw.storage_write(key, value)
    return code >= 0
end

--- Execute a DW query.
function M.query(query_str)
    local result, err = dw.query(query_str)
    if err then return nil, err end
    return result
end

--- Log a message. Levels: 0=trace, 1=debug, 2=info, 3=warn, 4=error.
function M.log(level, message)
    dw.log(level, message)
end

return M
```

**Compile:** `emcc -O2 lua-5.4/src/*.c -s STANDALONE_WASM=1 -o lua.wasm`
**Runtime:** wasmoon (`npm install wasmoon`)";

    private static string GetNimBindings() =>
@"### Nim DW SDK Bindings (nlvm / Emscripten)

Nim compiles to WASM via its LLVM backend (nlvm) targeting wasm32-wasi, or via Emscripten
through C codegen. Host function imports use Nim's `importc` pragma.

```nim
# dw_sdk.nim - DW host function bindings for Nim/Wasm

proc dwStorageRead(keyPtr: pointer, keyLen: cint, bufPtr: pointer, bufLen: cint): cint
  {.importc: ""dw_storage_read"", header: ""<dw.h>"".}

proc dwStorageWrite(keyPtr: pointer, keyLen: cint, valPtr: pointer, valLen: cint): cint
  {.importc: ""dw_storage_write"", header: ""<dw.h>"".}

proc dwQuery(queryPtr: pointer, queryLen: cint, resultPtr: pointer, resultLen: cint): cint
  {.importc: ""dw_query"", header: ""<dw.h>"".}

proc dwLog(level: cint, msgPtr: pointer, msgLen: cint)
  {.importc: ""dw_log"", header: ""<dw.h>"".}

proc storageRead*(key: string): string =
  var buf = newString(65536)
  let result = dwStorageRead(key.cstring, key.len.cint, buf.cstring, buf.len.cint)
  if result < 0: return """"
  buf.setLen(result)
  return buf

proc log*(level: int, message: string) =
  dwLog(level.cint, message.cstring, message.len.cint)
```

**Compile:** `nim c --cpu:wasm32 --os:wasi -d:release -o:module.wasm main.nim`";

    private static string GetVBindings() =>
@"### V DW SDK Bindings (V WASM backend)

V compiles to WASM via its built-in WASM backend or through C codegen + Emscripten.
Host function imports use V's C interop mechanism.

```v
// dw_sdk.v - DW host function bindings for V/Wasm

#flag -imported_memory

fn C.dw_storage_read(key_ptr &u8, key_len int, buf_ptr &u8, buf_len int) int
fn C.dw_storage_write(key_ptr &u8, key_len int, val_ptr &u8, val_len int) int
fn C.dw_query(query_ptr &u8, query_len int, result_ptr &u8, result_len int) int
fn C.dw_log(level int, msg_ptr &u8, msg_len int)

pub fn storage_read(key string) !string {
    mut buf := []u8{len: 65536}
    result := C.dw_storage_read(key.str, key.len, buf.data, buf.len)
    if result < 0 { return error('storage read failed') }
    return buf[..result].bytestr()
}

pub fn storage_write(key string, value string) ! {
    result := C.dw_storage_write(key.str, key.len, value.str, value.len)
    if result < 0 { return error('storage write failed') }
}

pub fn log(level int, message string) {
    C.dw_log(level, message.str, message.len)
}
```

**Compile:** `v -backend wasm -prod -o module.wasm main.v`";

    private static string GetGenericBindings(string language) =>
$@"### {language} DW SDK Bindings (Generic WASM Import Pattern)

For languages without a dedicated binding guide, DW host functions are imported using the
standard WASM import mechanism. The module name is `""dw""` and each function has a specific
name and signature.

**WASM Import Table (WAT syntax):**

```wat
(import ""dw"" ""dw_storage_read""  (func $dw_storage_read  (param i32 i32 i32 i32) (result i32)))
(import ""dw"" ""dw_storage_write"" (func $dw_storage_write (param i32 i32 i32 i32) (result i32)))
(import ""dw"" ""dw_query""         (func $dw_query         (param i32 i32 i32 i32) (result i32)))
(import ""dw"" ""dw_transform""     (func $dw_transform     (param i32 i32 i32 i32) (result i32)))
(import ""dw"" ""dw_log""           (func $dw_log           (param i32 i32 i32)))
```

**Function Signatures:**

| Function           | Parameters                                           | Returns    |
|--------------------|------------------------------------------------------|------------|
| `dw_storage_read`  | key_ptr (i32), key_len (i32), buf_ptr (i32), buf_len (i32) | i32 (bytes read, or -1) |
| `dw_storage_write` | key_ptr (i32), key_len (i32), val_ptr (i32), val_len (i32) | i32 (0 = success, -1 = error) |
| `dw_query`         | query_ptr (i32), query_len (i32), result_ptr (i32), result_len (i32) | i32 (bytes written, or -1) |
| `dw_transform`     | input_ptr (i32), input_len (i32), output_ptr (i32), output_len (i32) | i32 (bytes written, or -1) |
| `dw_log`           | level (i32), msg_ptr (i32), msg_len (i32)            | void       |

**Integration Steps:**
1. Configure your {language} WASM toolchain to import from the `""dw""` module
2. Map each host function to your language's FFI/import mechanism
3. Handle linear memory pointer passing according to your language's conventions
4. Compile to WASM with `--target wasm32-wasi` or equivalent flag";

    #endregion
}
