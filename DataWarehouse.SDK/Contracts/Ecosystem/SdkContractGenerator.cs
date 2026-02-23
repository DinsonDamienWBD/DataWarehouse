using System;
using System.Collections.Generic;
using System.Collections.Frozen;
using System.Linq;

namespace DataWarehouse.SDK.Contracts.Ecosystem;

// =============================================================================
// Generated SDK Output
// =============================================================================

/// <summary>
/// Output of SDK code generation for a single language, containing all files
/// needed to build and publish the SDK package.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Multi-language SDK specification (ECOS-07 through ECOS-11)")]
public sealed record GeneratedSdkOutput
{
    /// <summary>Target language.</summary>
    public required SdkLanguage Language { get; init; }

    /// <summary>Generated files: filename to source code content mapping.</summary>
    public required IReadOnlyDictionary<string, string> Files { get; init; }

    /// <summary>Package manifest content (setup.py, pom.xml, go.mod, Cargo.toml, package.json).</summary>
    public required string BuildFile { get; init; }

    /// <summary>Generated README with usage examples.</summary>
    public required string ReadmeContent { get; init; }

    /// <summary>Build file name for this language.</summary>
    public string BuildFileName => Language switch
    {
        SdkLanguage.Python => "setup.py",
        SdkLanguage.Java => "pom.xml",
        SdkLanguage.Go => "go.mod",
        SdkLanguage.Rust => "Cargo.toml",
        SdkLanguage.TypeScript => "package.json",
        _ => "build"
    };
}

// =============================================================================
// SDK Contract Generator — code generation engine
// =============================================================================

/// <summary>
/// Code generation engine that produces complete SDK client source code for
/// multiple languages from the canonical <see cref="SdkApiSurface"/>. Uses
/// <see cref="ILanguageTemplate"/> implementations for language-specific output.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Multi-language SDK specification (ECOS-07 through ECOS-11)")]
public sealed class SdkContractGenerator
{
    private static readonly FrozenDictionary<SdkLanguage, ILanguageTemplate> Templates =
        new Dictionary<SdkLanguage, ILanguageTemplate>
        {
            [SdkLanguage.Python] = new PythonTemplate(),
            [SdkLanguage.Java] = new JavaTemplate(),
            [SdkLanguage.Go] = new GoTemplate(),
            [SdkLanguage.Rust] = new RustTemplate(),
            [SdkLanguage.TypeScript] = new TypeScriptTemplate()
        }.ToFrozenDictionary();

    /// <summary>
    /// Generates a complete SDK package for the specified language from the
    /// API surface and language binding configuration.
    /// </summary>
    /// <param name="language">Target language.</param>
    /// <param name="surface">Canonical API surface to generate from.</param>
    /// <param name="binding">Language-specific binding with package metadata and idioms.</param>
    /// <returns>Generated SDK output with all source files, build file, and README.</returns>
    /// <exception cref="ArgumentException">If no template is registered for the language.</exception>
    public GeneratedSdkOutput GenerateSdk(
        SdkLanguage language,
        SdkApiSurface surface,
        SdkLanguageBinding binding)
    {
        if (!Templates.TryGetValue(language, out var template))
            throw new ArgumentException($"No template registered for language: {language}", nameof(language));

        var clientCode = template.GenerateClient(surface, binding);
        var typesCode = template.GenerateTypes(surface, binding);
        var buildFile = template.GenerateBuildFile(binding);
        var readme = template.GenerateReadme(surface, binding);

        var files = new Dictionary<string, string>();

        switch (language)
        {
            case SdkLanguage.Python:
                files["client.py"] = clientCode;
                files["types.py"] = typesCode;
                files["__init__.py"] = GeneratePythonInit(binding);
                files["py.typed"] = string.Empty; // PEP 561 marker
                break;

            case SdkLanguage.Java:
                files["DataWarehouseClient.java"] = clientCode;
                files["DataWarehouseTypes.java"] = typesCode;
                break;

            case SdkLanguage.Go:
                files["client.go"] = clientCode;
                files["types.go"] = typesCode;
                break;

            case SdkLanguage.Rust:
                files["client.rs"] = clientCode;
                files["types.rs"] = typesCode;
                files["error.rs"] = GenerateRustErrorModule();
                files["lib.rs"] = GenerateRustLibRs();
                break;

            case SdkLanguage.TypeScript:
                files["client.ts"] = clientCode;
                files["types.ts"] = typesCode;
                files["index.ts"] = GenerateTypeScriptIndex();
                break;
        }

        return new GeneratedSdkOutput
        {
            Language = language,
            Files = files,
            BuildFile = buildFile,
            ReadmeContent = readme
        };
    }

    /// <summary>
    /// Generates complete SDK packages for all 5 supported languages.
    /// </summary>
    /// <param name="surface">Canonical API surface to generate from.</param>
    /// <returns>Dictionary of language to generated SDK output.</returns>
    public IReadOnlyDictionary<SdkLanguage, GeneratedSdkOutput> GenerateAllSdks(SdkApiSurface surface)
    {
        var results = new Dictionary<SdkLanguage, GeneratedSdkOutput>();

        foreach (var language in Templates.Keys)
        {
            var binding = SdkClientSpecification.GetLanguageBinding(language);
            results[language] = GenerateSdk(language, surface, binding);
        }

        return results;
    }

    // -------------------------------------------------------------------------
    // Language-specific helper file generators
    // -------------------------------------------------------------------------

    private static string GeneratePythonInit(SdkLanguageBinding binding)
    {
        return $"""
            ""\"DataWarehouse Python SDK.""\"

            from .client import DataWarehouseClient
            from .types import *

            __version__ = "{binding.PackageVersion}"
            __all__ = ["DataWarehouseClient"]
            """;
    }

    private static string GenerateRustErrorModule()
    {
        return """
            //! DataWarehouse SDK error types.

            use std::fmt;

            /// Errors that can occur when using the DataWarehouse SDK.
            #[derive(Debug)]
            pub enum DataWarehouseError {
                /// Connection error.
                Connection(String),
                /// gRPC transport error.
                Transport(String),
                /// Server returned an error status.
                Status { code: i32, message: String },
                /// Serialization/deserialization error.
                Serialization(String),
                /// Operation timed out.
                Timeout,
                /// Client is not connected.
                NotConnected,
            }

            impl fmt::Display for DataWarehouseError {
                fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
                    match self {
                        Self::Connection(msg) => write!(f, "connection error: {msg}"),
                        Self::Transport(msg) => write!(f, "transport error: {msg}"),
                        Self::Status { code, message } => write!(f, "server error {code}: {message}"),
                        Self::Serialization(msg) => write!(f, "serialization error: {msg}"),
                        Self::Timeout => write!(f, "operation timed out"),
                        Self::NotConnected => write!(f, "client is not connected"),
                    }
                }
            }

            impl std::error::Error for DataWarehouseError {}
            """;
    }

    private static string GenerateRustLibRs()
    {
        return """
            //! DataWarehouse Rust SDK.
            //!
            //! # Example
            //!
            //! ```rust,no_run
            //! use datawarehouse::{Client, ClientOptions};
            //!
            //! #[tokio::main]
            //! async fn main() -> Result<(), Box<dyn std::error::Error>> {
            //!     let client = Client::connect(ClientOptions::default()).await?;
            //!     // Use client...
            //!     Ok(())
            //! }
            //! ```

            pub mod client;
            pub mod error;
            pub mod types;

            pub use client::{Client, ClientOptions};
            pub use error::DataWarehouseError;
            """;
    }

    private static string GenerateTypeScriptIndex()
    {
        return """
            /**
             * DataWarehouse TypeScript SDK.
             *
             * @packageDocumentation
             */

            export { DataWarehouseClient } from './client';
            export type { DataWarehouseClientOptions } from './client';
            export * from './types';
            """;
    }
}
