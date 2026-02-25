using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DataWarehouse.SDK.Contracts.Ecosystem;

// =============================================================================
// ILanguageTemplate — interface for language-specific code generation
// =============================================================================

/// <summary>
/// Interface for language-specific SDK code generation templates.
/// Each implementation generates idiomatic client code for a target language.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Multi-language SDK specification (ECOS-07 through ECOS-11)")]
public interface ILanguageTemplate
{
    /// <summary>Target language for this template.</summary>
    SdkLanguage Language { get; }

    /// <summary>Generates the main client class source code.</summary>
    string GenerateClient(SdkApiSurface surface, SdkLanguageBinding binding);

    /// <summary>Generates type definitions source code.</summary>
    string GenerateTypes(SdkApiSurface surface, SdkLanguageBinding binding);

    /// <summary>Generates the package manifest / build file.</summary>
    string GenerateBuildFile(SdkLanguageBinding binding);

    /// <summary>Generates README with usage examples.</summary>
    string GenerateReadme(SdkApiSurface surface, SdkLanguageBinding binding);
}

// =============================================================================
// Python Template
// =============================================================================

/// <summary>
/// Generates Python SDK with grpcio, context managers, type hints, dataclasses,
/// and pandas DataFrame integration for query results.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Multi-language SDK specification (ECOS-07 through ECOS-11)")]
public sealed class PythonTemplate : ILanguageTemplate
{
    /// <inheritdoc />
    public SdkLanguage Language => SdkLanguage.Python;

    /// <inheritdoc />
    public string GenerateClient(SdkApiSurface surface, SdkLanguageBinding binding)
    {
        var sb = new StringBuilder(4096);
        sb.AppendLine("\"\"\"DataWarehouse Python SDK client.\"\"\"");
        sb.AppendLine();
        sb.AppendLine("from __future__ import annotations");
        sb.AppendLine();
        sb.AppendLine("import grpc");
        sb.AppendLine("from typing import Any, Dict, Iterator, List, Optional, Sequence");
        sb.AppendLine();
        sb.AppendLine("from .types import (");

        foreach (var type in surface.Types)
        {
            sb.Append("    ").Append(type.Name).AppendLine(",");
        }

        sb.AppendLine(")");
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("class DataWarehouseClient:");
        sb.AppendLine("    \"\"\"");
        sb.AppendLine("    DataWarehouse SDK client with gRPC transport.");
        sb.AppendLine();
        sb.AppendLine("    Supports context manager protocol for automatic cleanup:");
        sb.AppendLine("        with DataWarehouseClient('localhost', 9090) as client:");
        sb.AppendLine("            result = client.store('key', b'data')");
        sb.AppendLine("    \"\"\"");
        sb.AppendLine();
        sb.AppendLine("    def __init__(");
        sb.AppendLine("        self,");
        sb.AppendLine("        host: str = \"localhost\",");
        sb.AppendLine("        port: int = 9090,");
        sb.AppendLine("        *,");
        sb.AppendLine("        use_tls: bool = False,");
        sb.AppendLine("        auth_token: Optional[str] = None,");
        sb.AppendLine("        timeout_ms: int = 30000,");
        sb.AppendLine("        max_retries: int = 3,");
        sb.AppendLine("        metadata: Optional[Dict[str, str]] = None,");
        sb.AppendLine("    ) -> None:");
        sb.AppendLine("        self._host = host");
        sb.AppendLine("        self._port = port");
        sb.AppendLine("        self._timeout = timeout_ms / 1000.0");
        sb.AppendLine("        self._metadata = metadata or {}");
        sb.AppendLine("        target = f\"{host}:{port}\"");
        sb.AppendLine("        if use_tls:");
        sb.AppendLine("            credentials = grpc.ssl_channel_credentials()");
        sb.AppendLine("            self._channel = grpc.secure_channel(target, credentials)");
        sb.AppendLine("        else:");
        sb.AppendLine("            self._channel = grpc.insecure_channel(target)");
        sb.AppendLine("        self._connected = True");
        sb.AppendLine();
        sb.AppendLine("    def __enter__(self) -> \"DataWarehouseClient\":");
        sb.AppendLine("        return self");
        sb.AppendLine();
        sb.AppendLine("    def __exit__(self, exc_type, exc_val, exc_tb) -> None:");
        sb.AppendLine("        self.close()");
        sb.AppendLine();
        sb.AppendLine("    def close(self) -> None:");
        sb.AppendLine("        \"\"\"Close the gRPC channel.\"\"\"");
        sb.AppendLine("        if self._connected:");
        sb.AppendLine("            self._channel.close()");
        sb.AppendLine("            self._connected = False");
        sb.AppendLine();

        foreach (var method in surface.Methods)
        {
            GeneratePythonMethod(sb, method);
        }

        // Pandas integration for query results
        sb.AppendLine();
        sb.AppendLine("    @staticmethod");
        sb.AppendLine("    def to_dataframe(results: List[QueryResult]) -> \"pandas.DataFrame\":");
        sb.AppendLine("        \"\"\"Convert query results to a pandas DataFrame.\"\"\"");
        sb.AppendLine("        import pandas");
        sb.AppendLine("        rows = []");
        sb.AppendLine("        columns: List[str] = []");
        sb.AppendLine("        for batch in results:");
        sb.AppendLine("            if not columns and batch.columns:");
        sb.AppendLine("                columns = [c.name for c in batch.columns]");
        sb.AppendLine("            rows.extend(batch.rows)");
        sb.AppendLine("        return pandas.DataFrame(rows, columns=columns or None)");

        return sb.ToString();
    }

    private static void GeneratePythonMethod(StringBuilder sb, SdkMethod method)
    {
        var pyName = method.Name;
        sb.Append("    def ").Append(pyName).Append("(self");

        foreach (var field in method.InputType.Fields)
        {
            var pyType = MapPythonType(field.Type, field.Nullable);
            sb.Append(", ").Append(field.Name).Append(": ").Append(pyType);
            if (field.Nullable)
                sb.Append(" = None");
        }

        sb.Append(") -> ");
        if (method.IsStreaming)
            sb.Append("Iterator[").Append(method.OutputType.Name).Append(']');
        else
            sb.Append(method.OutputType.Name);
        sb.AppendLine(":");

        sb.Append("        \"\"\"").Append(method.Description).AppendLine(".\"\"\"");
        sb.AppendLine("        ...");
        sb.AppendLine();
    }

    private static string MapPythonType(string sdkType, bool nullable)
    {
        var baseType = sdkType switch
        {
            "string" => "str",
            "bytes" => "bytes",
            "int32" or "int64" => "int",
            "float64" => "float",
            "bool" => "bool",
            "datetime" => "str",
            _ when sdkType.StartsWith("map<", StringComparison.Ordinal) => "Dict[str, Any]",
            _ when sdkType.StartsWith("list<", StringComparison.Ordinal) => "List[Any]",
            _ when sdkType.StartsWith("stream<", StringComparison.Ordinal) => "Iterator[bytes]",
            _ => sdkType
        };
        return nullable ? $"Optional[{baseType}]" : baseType;
    }

    /// <inheritdoc />
    public string GenerateTypes(SdkApiSurface surface, SdkLanguageBinding binding)
    {
        var sb = new StringBuilder(2048);
        sb.AppendLine("\"\"\"DataWarehouse SDK type definitions.\"\"\"");
        sb.AppendLine();
        sb.AppendLine("from __future__ import annotations");
        sb.AppendLine();
        sb.AppendLine("from dataclasses import dataclass, field");
        sb.AppendLine("from enum import IntEnum");
        sb.AppendLine("from typing import Any, Dict, List, Optional, Sequence");
        sb.AppendLine();

        // Enums
        foreach (var e in surface.Enums)
        {
            sb.AppendLine();
            sb.Append("class ").Append(e.Name).AppendLine("(IntEnum):");
            foreach (var (name, value) in e.Values)
                sb.Append("    ").Append(name.ToUpperInvariant()).Append(" = ").Append(value).AppendLine();
            sb.AppendLine();
        }

        // Types as dataclasses
        foreach (var type in surface.Types)
        {
            sb.AppendLine();
            sb.AppendLine("@dataclass(frozen=True, slots=True)");
            sb.Append("class ").Append(type.Name).AppendLine(":");
            if (type.Fields.Count == 0)
            {
                sb.AppendLine("    pass");
                continue;
            }

            foreach (var f in type.Fields)
            {
                var pyType = MapPythonType(f.Type, f.Nullable);
                sb.Append("    ").Append(f.Name).Append(": ").Append(pyType);
                if (f.Nullable)
                    sb.Append(" = None");
                else if (f.Type.StartsWith("map<", StringComparison.Ordinal))
                    sb.Append(" = field(default_factory=dict)");
                else if (f.Type.StartsWith("list<", StringComparison.Ordinal))
                    sb.Append(" = field(default_factory=list)");
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    /// <inheritdoc />
    public string GenerateBuildFile(SdkLanguageBinding binding)
    {
        var sb = new StringBuilder(512);
        sb.AppendLine("from setuptools import setup, find_packages");
        sb.AppendLine();
        sb.AppendLine("setup(");
        sb.Append("    name=\"").Append(binding.PackageName).AppendLine("\",");
        sb.Append("    version=\"").Append(binding.PackageVersion).AppendLine("\",");
        sb.AppendLine("    description=\"DataWarehouse Python SDK\",");
        sb.AppendLine("    packages=find_packages(),");
        sb.AppendLine("    python_requires=\">=3.9\",");
        sb.AppendLine("    install_requires=[");
        sb.AppendLine("        \"grpcio>=1.60.0\",");
        sb.AppendLine("        \"grpcio-tools>=1.60.0\",");
        sb.AppendLine("        \"protobuf>=4.25.0\",");
        sb.AppendLine("    ],");
        sb.AppendLine("    extras_require={");
        sb.AppendLine("        \"pandas\": [\"pandas>=2.0.0\"],");
        sb.AppendLine("    },");
        sb.AppendLine("    package_data={\"" + binding.PackageName + "\": [\"py.typed\"]},");
        sb.AppendLine(")");
        return sb.ToString();
    }

    /// <inheritdoc />
    public string GenerateReadme(SdkApiSurface surface, SdkLanguageBinding binding)
    {
        var sb = new StringBuilder(1024);
        sb.Append("# ").Append(binding.PackageName).AppendLine(" - DataWarehouse Python SDK");
        sb.AppendLine();
        sb.AppendLine("## Installation");
        sb.AppendLine();
        sb.Append("```bash\npip install ").Append(binding.PackageName).AppendLine("\n```");
        sb.AppendLine();
        sb.AppendLine("## Quick Start");
        sb.AppendLine();
        sb.AppendLine("```python");
        sb.AppendLine("from datawarehouse import DataWarehouseClient");
        sb.AppendLine();
        sb.AppendLine("with DataWarehouseClient('localhost', 9090) as client:");
        sb.AppendLine("    # Store data");
        sb.AppendLine("    result = client.store('my-key', b'hello world')");
        sb.AppendLine();
        sb.AppendLine("    # Retrieve data");
        sb.AppendLine("    for chunk in client.retrieve('my-key'):");
        sb.AppendLine("        print(chunk.data)");
        sb.AppendLine();
        sb.AppendLine("    # Query with pandas integration");
        sb.AppendLine("    results = list(client.query('SELECT * FROM objects'))");
        sb.AppendLine("    df = DataWarehouseClient.to_dataframe(results)");
        sb.AppendLine("```");
        return sb.ToString();
    }
}

// =============================================================================
// Java Template
// =============================================================================

/// <summary>
/// Generates Java SDK with grpc-java, CompletableFuture, AutoCloseable,
/// Builder pattern, and JDBC driver class.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Multi-language SDK specification (ECOS-07 through ECOS-11)")]
public sealed class JavaTemplate : ILanguageTemplate
{
    /// <inheritdoc />
    public SdkLanguage Language => SdkLanguage.Java;

    /// <inheritdoc />
    public string GenerateClient(SdkApiSurface surface, SdkLanguageBinding binding)
    {
        var sb = new StringBuilder(4096);
        sb.Append("package ").Append(binding.PackageName).AppendLine(";");
        sb.AppendLine();
        sb.AppendLine("import io.grpc.ManagedChannel;");
        sb.AppendLine("import io.grpc.ManagedChannelBuilder;");
        sb.AppendLine("import java.util.Iterator;");
        sb.AppendLine("import java.util.Map;");
        sb.AppendLine("import java.util.concurrent.CompletableFuture;");
        sb.AppendLine("import java.util.concurrent.TimeUnit;");
        sb.AppendLine();
        sb.AppendLine("/**");
        sb.AppendLine(" * DataWarehouse Java SDK client.");
        sb.AppendLine(" *");
        sb.AppendLine(" * <p>Implements {@link AutoCloseable} for try-with-resources:</p>");
        sb.AppendLine(" * <pre>{@code");
        sb.AppendLine(" * try (var client = DataWarehouseClient.builder(\"localhost\", 9090).build()) {");
        sb.AppendLine(" *     StoreResponse response = client.store(\"key\", data).join();");
        sb.AppendLine(" * }");
        sb.AppendLine(" * }</pre>");
        sb.AppendLine(" */");
        sb.AppendLine("public final class DataWarehouseClient implements AutoCloseable {");
        sb.AppendLine();
        sb.AppendLine("    private final ManagedChannel channel;");
        sb.AppendLine("    private volatile boolean closed;");
        sb.AppendLine();
        sb.AppendLine("    private DataWarehouseClient(Builder builder) {");
        sb.AppendLine("        var channelBuilder = ManagedChannelBuilder.forAddress(builder.host, builder.port);");
        sb.AppendLine("        if (!builder.useTls) {");
        sb.AppendLine("            channelBuilder.usePlaintext();");
        sb.AppendLine("        }");
        sb.AppendLine("        this.channel = channelBuilder.build();");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    public static Builder builder(String host, int port) {");
        sb.AppendLine("        return new Builder(host, port);");
        sb.AppendLine("    }");
        sb.AppendLine();

        foreach (var method in surface.Methods)
        {
            GenerateJavaMethod(sb, method);
        }

        sb.AppendLine("    @Override");
        sb.AppendLine("    public void close() {");
        sb.AppendLine("        if (!closed) {");
        sb.AppendLine("            closed = true;");
        sb.AppendLine("            channel.shutdown();");
        sb.AppendLine("            try {");
        sb.AppendLine("                channel.awaitTermination(5, TimeUnit.SECONDS);");
        sb.AppendLine("            } catch (InterruptedException e) {");
        sb.AppendLine("                channel.shutdownNow();");
        sb.AppendLine("                Thread.currentThread().interrupt();");
        sb.AppendLine("            }");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine();

        // Builder class
        sb.AppendLine("    public static final class Builder {");
        sb.AppendLine("        private final String host;");
        sb.AppendLine("        private final int port;");
        sb.AppendLine("        private boolean useTls;");
        sb.AppendLine("        private String authToken;");
        sb.AppendLine("        private int timeoutMs = 30000;");
        sb.AppendLine("        private int maxRetries = 3;");
        sb.AppendLine();
        sb.AppendLine("        private Builder(String host, int port) {");
        sb.AppendLine("            this.host = host;");
        sb.AppendLine("            this.port = port;");
        sb.AppendLine("        }");
        sb.AppendLine();
        sb.AppendLine("        public Builder useTls(boolean useTls) { this.useTls = useTls; return this; }");
        sb.AppendLine("        public Builder authToken(String token) { this.authToken = token; return this; }");
        sb.AppendLine("        public Builder timeoutMs(int timeout) { this.timeoutMs = timeout; return this; }");
        sb.AppendLine("        public Builder maxRetries(int retries) { this.maxRetries = retries; return this; }");
        sb.AppendLine();
        sb.AppendLine("        public DataWarehouseClient build() {");
        sb.AppendLine("            return new DataWarehouseClient(this);");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
    }

    private static void GenerateJavaMethod(StringBuilder sb, SdkMethod method)
    {
        var javaName = method.Name;
        var returnType = method.IsStreaming
            ? $"Iterator<{method.OutputType.Name}>"
            : $"CompletableFuture<{method.OutputType.Name}>";

        sb.Append("    /** ").Append(method.Description).AppendLine(". */");
        sb.Append("    public ").Append(returnType).Append(' ').Append(javaName).Append('(');

        var first = true;
        foreach (var field in method.InputType.Fields)
        {
            if (!first) sb.Append(", ");
            first = false;
            sb.Append(MapJavaType(field.Type, field.Nullable)).Append(' ').Append(ToCamelCase(field.Name));
        }

        sb.AppendLine(") {");
        sb.AppendLine("        throw new UnsupportedOperationException(\"Requires gRPC stub initialization\");");
        sb.AppendLine("    }");
        sb.AppendLine();
    }

    private static string MapJavaType(string sdkType, bool nullable)
    {
        return sdkType switch
        {
            "string" => "String",
            "bytes" => "byte[]",
            "int32" => nullable ? "Integer" : "int",
            "int64" => nullable ? "Long" : "long",
            "float64" => nullable ? "Double" : "double",
            "bool" => nullable ? "Boolean" : "boolean",
            "datetime" => "String",
            _ when sdkType.StartsWith("map<", StringComparison.Ordinal) => "Map<String, String>",
            _ when sdkType.StartsWith("list<", StringComparison.Ordinal) => "java.util.List<Object>",
            _ when sdkType.StartsWith("stream<", StringComparison.Ordinal) => "Iterator<byte[]>",
            _ => sdkType
        };
    }

    private static string ToCamelCase(string snakeCase)
    {
        var parts = snakeCase.Split('_');
        var sb = new StringBuilder(parts[0]);
        for (int i = 1; i < parts.Length; i++)
        {
            if (parts[i].Length > 0)
            {
                sb.Append(char.ToUpperInvariant(parts[i][0]));
                sb.Append(parts[i].AsSpan(1));
            }
        }
        return sb.ToString();
    }

    /// <inheritdoc />
    public string GenerateTypes(SdkApiSurface surface, SdkLanguageBinding binding)
    {
        var sb = new StringBuilder(2048);
        sb.Append("package ").Append(binding.PackageName).AppendLine(";");
        sb.AppendLine();
        sb.AppendLine("import java.util.List;");
        sb.AppendLine("import java.util.Map;");
        sb.AppendLine();

        // Enums
        foreach (var e in surface.Enums)
        {
            sb.Append("public enum ").Append(e.Name).AppendLine(" {");
            for (int i = 0; i < e.Values.Count; i++)
            {
                var (name, value) = e.Values[i];
                sb.Append("    ").Append(name.ToUpperInvariant()).Append('(').Append(value).Append(')');
                sb.AppendLine(i < e.Values.Count - 1 ? "," : ";");
            }
            sb.AppendLine("    private final int value;");
            sb.Append("    ").Append(e.Name).AppendLine("(int value) { this.value = value; }");
            sb.AppendLine("    public int getValue() { return value; }");
            sb.AppendLine("}");
            sb.AppendLine();
        }

        // Types as POJOs with builders
        foreach (var type in surface.Types)
        {
            sb.Append("public final class ").Append(type.Name).AppendLine(" {");
            foreach (var f in type.Fields)
            {
                sb.Append("    private final ").Append(MapJavaType(f.Type, f.Nullable))
                  .Append(' ').Append(ToCamelCase(f.Name)).AppendLine(";");
            }

            sb.AppendLine();
            sb.Append("    private ").Append(type.Name).AppendLine("(Builder builder) {");
            foreach (var f in type.Fields)
            {
                var camel = ToCamelCase(f.Name);
                sb.Append("        this.").Append(camel).Append(" = builder.").Append(camel).AppendLine(";");
            }
            sb.AppendLine("    }");

            // Getters
            foreach (var f in type.Fields)
            {
                var camel = ToCamelCase(f.Name);
                var getter = "get" + char.ToUpperInvariant(camel[0]) + camel[1..];
                sb.Append("    public ").Append(MapJavaType(f.Type, f.Nullable)).Append(' ')
                  .Append(getter).Append("() { return ").Append(camel).AppendLine("; }");
            }

            sb.AppendLine("    public static Builder builder() { return new Builder(); }");
            sb.AppendLine("    public static final class Builder {");
            foreach (var f in type.Fields)
            {
                sb.Append("        private ").Append(MapJavaType(f.Type, f.Nullable))
                  .Append(' ').Append(ToCamelCase(f.Name)).AppendLine(";");
            }
            foreach (var f in type.Fields)
            {
                var camel = ToCamelCase(f.Name);
                sb.Append("        public Builder ").Append(camel).Append('(')
                  .Append(MapJavaType(f.Type, f.Nullable)).Append(" val) { this.").Append(camel)
                  .AppendLine(" = val; return this; }");
            }
            sb.Append("        public ").Append(type.Name).AppendLine(" build() { return new " + type.Name + "(this); }");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            sb.AppendLine();
        }

        // JDBC driver class
        sb.AppendLine("/**");
        sb.AppendLine(" * JDBC driver wrapping DataWarehouse PostgreSQL wire protocol.");
        sb.AppendLine(" * Register via {@code Class.forName(\"com.datawarehouse.sdk.DataWarehouseJdbcDriver\")}.");
        sb.AppendLine(" */");
        sb.AppendLine("public final class DataWarehouseJdbcDriver {");
        sb.AppendLine("    public static final String DRIVER_PREFIX = \"jdbc:datawarehouse://\";");
        sb.AppendLine("    public static boolean acceptsURL(String url) {");
        sb.AppendLine("        return url != null && url.startsWith(DRIVER_PREFIX);");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
    }

    /// <inheritdoc />
    public string GenerateBuildFile(SdkLanguageBinding binding)
    {
        var sb = new StringBuilder(1024);
        var parts = binding.PackageName.Split('.');
        var groupId = parts.Length >= 2 ? string.Join(".", parts.Take(parts.Length - 1)) : binding.PackageName;
        var artifactId = parts.Length >= 1 ? parts[^1] : binding.PackageName;

        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<project xmlns=\"http://maven.apache.org/POM/4.0.0\"");
        sb.AppendLine("         xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\"");
        sb.AppendLine("         xsi:schemaLocation=\"http://maven.apache.org/POM/4.0.0 http://maven.apache.org/xsd/maven-4.0.0.xsd\">");
        sb.AppendLine("    <modelVersion>4.0.0</modelVersion>");
        sb.Append("    <groupId>").Append(groupId).AppendLine("</groupId>");
        sb.Append("    <artifactId>").Append(artifactId).AppendLine("</artifactId>");
        sb.Append("    <version>").Append(binding.PackageVersion).AppendLine("</version>");
        sb.AppendLine("    <name>DataWarehouse Java SDK</name>");
        sb.AppendLine("    <properties>");
        sb.AppendLine("        <grpc.version>1.62.0</grpc.version>");
        sb.AppendLine("        <java.version>17</java.version>");
        sb.AppendLine("    </properties>");
        sb.AppendLine("    <dependencies>");
        sb.AppendLine("        <dependency>");
        sb.AppendLine("            <groupId>io.grpc</groupId>");
        sb.AppendLine("            <artifactId>grpc-netty-shaded</artifactId>");
        sb.AppendLine("            <version>${grpc.version}</version>");
        sb.AppendLine("        </dependency>");
        sb.AppendLine("        <dependency>");
        sb.AppendLine("            <groupId>io.grpc</groupId>");
        sb.AppendLine("            <artifactId>grpc-protobuf</artifactId>");
        sb.AppendLine("            <version>${grpc.version}</version>");
        sb.AppendLine("        </dependency>");
        sb.AppendLine("        <dependency>");
        sb.AppendLine("            <groupId>io.grpc</groupId>");
        sb.AppendLine("            <artifactId>grpc-stub</artifactId>");
        sb.AppendLine("            <version>${grpc.version}</version>");
        sb.AppendLine("        </dependency>");
        sb.AppendLine("    </dependencies>");
        sb.AppendLine("</project>");
        return sb.ToString();
    }

    /// <inheritdoc />
    public string GenerateReadme(SdkApiSurface surface, SdkLanguageBinding binding)
    {
        var sb = new StringBuilder(512);
        sb.AppendLine("# DataWarehouse Java SDK");
        sb.AppendLine();
        sb.AppendLine("## Maven Dependency");
        sb.AppendLine();
        sb.AppendLine("```xml");
        sb.AppendLine("<dependency>");
        sb.Append("    <groupId>com.datawarehouse</groupId>\n");
        sb.Append("    <artifactId>sdk</artifactId>\n");
        sb.Append("    <version>").Append(binding.PackageVersion).AppendLine("</version>");
        sb.AppendLine("</dependency>");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("## Quick Start");
        sb.AppendLine();
        sb.AppendLine("```java");
        sb.AppendLine("try (var client = DataWarehouseClient.builder(\"localhost\", 9090).build()) {");
        sb.AppendLine("    CompletableFuture<StoreResponse> future = client.store(\"key\", data);");
        sb.AppendLine("    StoreResponse response = future.join();");
        sb.AppendLine("}");
        sb.AppendLine("```");
        return sb.ToString();
    }
}

// =============================================================================
// Go Template
// =============================================================================

/// <summary>
/// Generates Go SDK with context.Context first parameter, (T, error) returns,
/// functional options pattern, and proper Go module structure.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Multi-language SDK specification (ECOS-07 through ECOS-11)")]
public sealed class GoTemplate : ILanguageTemplate
{
    /// <inheritdoc />
    public SdkLanguage Language => SdkLanguage.Go;

    /// <inheritdoc />
    public string GenerateClient(SdkApiSurface surface, SdkLanguageBinding binding)
    {
        var sb = new StringBuilder(4096);
        sb.AppendLine("// Package datawarehouse provides the DataWarehouse Go SDK client.");
        sb.AppendLine("package datawarehouse");
        sb.AppendLine();
        sb.AppendLine("import (");
        sb.AppendLine("\t\"context\"");
        sb.AppendLine("\t\"io\"");
        sb.AppendLine();
        sb.AppendLine("\t\"google.golang.org/grpc\"");
        sb.AppendLine(")");
        sb.AppendLine();
        sb.AppendLine("// Client provides access to DataWarehouse operations.");
        sb.AppendLine("type Client struct {");
        sb.AppendLine("\tconn *grpc.ClientConn");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("// Option configures the client.");
        sb.AppendLine("type Option func(*clientConfig)");
        sb.AppendLine();
        sb.AppendLine("type clientConfig struct {");
        sb.AppendLine("\tUseTLS    bool");
        sb.AppendLine("\tAuthToken string");
        sb.AppendLine("\tTimeoutMs int");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("// WithTLS enables TLS encryption.");
        sb.AppendLine("func WithTLS() Option { return func(c *clientConfig) { c.UseTLS = true } }");
        sb.AppendLine();
        sb.AppendLine("// WithAuthToken sets the authentication token.");
        sb.AppendLine("func WithAuthToken(token string) Option { return func(c *clientConfig) { c.AuthToken = token } }");
        sb.AppendLine();
        sb.AppendLine("// WithTimeout sets the connection timeout in milliseconds.");
        sb.AppendLine("func WithTimeout(ms int) Option { return func(c *clientConfig) { c.TimeoutMs = ms } }");
        sb.AppendLine();
        sb.AppendLine("// NewClient creates a new DataWarehouse client.");
        sb.AppendLine("func NewClient(ctx context.Context, host string, port int, opts ...Option) (*Client, error) {");
        sb.AppendLine("\tcfg := &clientConfig{TimeoutMs: 30000}");
        sb.AppendLine("\tfor _, opt := range opts {");
        sb.AppendLine("\t\topt(cfg)");
        sb.AppendLine("\t}");
        sb.AppendLine("\ttarget := fmt.Sprintf(\"%s:%d\", host, port)");
        sb.AppendLine("\tvar dialOpts []grpc.DialOption");
        sb.AppendLine("\tif !cfg.UseTLS {");
        sb.AppendLine("\t\tdialOpts = append(dialOpts, grpc.WithInsecure())");
        sb.AppendLine("\t}");
        sb.AppendLine("\tconn, err := grpc.DialContext(ctx, target, dialOpts...)");
        sb.AppendLine("\tif err != nil {");
        sb.AppendLine("\t\treturn nil, fmt.Errorf(\"datawarehouse: connect: %w\", err)");
        sb.AppendLine("\t}");
        sb.AppendLine("\treturn &Client{conn: conn}, nil");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("// Close closes the gRPC connection.");
        sb.AppendLine("func (c *Client) Close() error {");
        sb.AppendLine("\tif c.conn != nil {");
        sb.AppendLine("\t\treturn c.conn.Close()");
        sb.AppendLine("\t}");
        sb.AppendLine("\treturn nil");
        sb.AppendLine("}");
        sb.AppendLine();

        foreach (var method in surface.Methods)
        {
            GenerateGoMethod(sb, method);
        }

        return sb.ToString();
    }

    private static void GenerateGoMethod(StringBuilder sb, SdkMethod method)
    {
        var goName = ToPascalCase(method.Name);
        var returnType = method.IsStreaming
            ? $"(<-chan *{method.OutputType.Name}, error)"
            : $"(*{method.OutputType.Name}, error)";

        sb.Append("// ").Append(goName).Append(' ').Append(method.Description.ToLowerInvariant()).AppendLine(".");
        sb.Append("func (c *Client) ").Append(goName).Append("(ctx context.Context");

        foreach (var field in method.InputType.Fields)
        {
            sb.Append(", ").Append(ToCamelCaseGo(field.Name)).Append(' ').Append(MapGoType(field.Type, field.Nullable));
        }

        sb.Append(") ").Append(returnType).AppendLine(" {");
        sb.AppendLine("\tpanic(\"requires gRPC stub initialization\")");
        sb.AppendLine("}");
        sb.AppendLine();
    }

    private static string MapGoType(string sdkType, bool nullable)
    {
        var baseType = sdkType switch
        {
            "string" => nullable ? "*string" : "string",
            "bytes" => "[]byte",
            "int32" => "int32",
            "int64" => "int64",
            "float64" => "float64",
            "bool" => "bool",
            "datetime" => "string",
            _ when sdkType.StartsWith("map<", StringComparison.Ordinal) => "map[string]string",
            _ when sdkType.StartsWith("list<", StringComparison.Ordinal) => "[]interface{}",
            _ when sdkType.StartsWith("stream<", StringComparison.Ordinal) => "io.Reader",
            _ => sdkType
        };
        return baseType;
    }

    private static string ToCamelCaseGo(string snakeCase)
    {
        var parts = snakeCase.Split('_');
        var sb = new StringBuilder();
        foreach (var part in parts)
        {
            if (part.Length > 0)
            {
                sb.Append(char.ToLowerInvariant(part[0]));
                sb.Append(part.AsSpan(1));
            }
        }
        return sb.ToString();
    }

    private static string ToPascalCase(string snakeCase)
    {
        var parts = snakeCase.Split('_');
        var sb = new StringBuilder();
        foreach (var part in parts)
        {
            if (part.Length > 0)
            {
                sb.Append(char.ToUpperInvariant(part[0]));
                sb.Append(part.AsSpan(1));
            }
        }
        return sb.ToString();
    }

    /// <inheritdoc />
    public string GenerateTypes(SdkApiSurface surface, SdkLanguageBinding binding)
    {
        var sb = new StringBuilder(2048);
        sb.AppendLine("package datawarehouse");
        sb.AppendLine();

        // Enums as const blocks
        foreach (var e in surface.Enums)
        {
            sb.Append("// ").Append(e.Name).AppendLine(" enumeration.");
            sb.Append("type ").Append(e.Name).AppendLine(" int");
            sb.AppendLine();
            sb.AppendLine("const (");
            foreach (var (name, value) in e.Values)
            {
                sb.Append('\t').Append(e.Name).Append(name).Append(' ').Append(e.Name).Append(" = ").Append(value).AppendLine();
            }
            sb.AppendLine(")");
            sb.AppendLine();
        }

        // Types as structs
        foreach (var type in surface.Types)
        {
            sb.Append("// ").Append(type.Name).AppendLine(" represents an SDK type.");
            sb.Append("type ").Append(type.Name).AppendLine(" struct {");
            foreach (var f in type.Fields)
            {
                var goName = ToPascalCase(f.Name);
                var goType = MapGoType(f.Type, f.Nullable);
                sb.Append('\t').Append(goName).Append(' ').Append(goType)
                  .Append(" `json:\"").Append(f.Name).AppendLine("\"`");
            }
            sb.AppendLine("}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <inheritdoc />
    public string GenerateBuildFile(SdkLanguageBinding binding)
    {
        var sb = new StringBuilder(256);
        sb.Append("module ").AppendLine(binding.PackageName);
        sb.AppendLine();
        sb.AppendLine("go 1.21");
        sb.AppendLine();
        sb.AppendLine("require (");
        sb.AppendLine("\tgoogle.golang.org/grpc v1.62.0");
        sb.AppendLine("\tgoogle.golang.org/protobuf v1.33.0");
        sb.AppendLine(")");
        return sb.ToString();
    }

    /// <inheritdoc />
    public string GenerateReadme(SdkApiSurface surface, SdkLanguageBinding binding)
    {
        var sb = new StringBuilder(512);
        sb.AppendLine("# DataWarehouse Go SDK");
        sb.AppendLine();
        sb.AppendLine("## Installation");
        sb.AppendLine();
        sb.Append("```bash\ngo get ").Append(binding.PackageName).AppendLine("\n```");
        sb.AppendLine();
        sb.AppendLine("## Quick Start");
        sb.AppendLine();
        sb.AppendLine("```go");
        sb.AppendLine("client, err := datawarehouse.NewClient(ctx, \"localhost\", 9090)");
        sb.AppendLine("if err != nil {");
        sb.AppendLine("\tlog.Fatal(err)");
        sb.AppendLine("}");
        sb.AppendLine("defer client.Close()");
        sb.AppendLine();
        sb.AppendLine("resp, err := client.Store(ctx, \"key\", data, nil, nil, nil, -1, 0)");
        sb.AppendLine("```");
        return sb.ToString();
    }
}

// =============================================================================
// Rust Template
// =============================================================================

/// <summary>
/// Generates Rust SDK with tonic, Result types, Drop trait, tokio async runtime,
/// and serde derive for type serialization.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Multi-language SDK specification (ECOS-07 through ECOS-11)")]
public sealed class RustTemplate : ILanguageTemplate
{
    /// <inheritdoc />
    public SdkLanguage Language => SdkLanguage.Rust;

    /// <inheritdoc />
    public string GenerateClient(SdkApiSurface surface, SdkLanguageBinding binding)
    {
        var sb = new StringBuilder(4096);
        sb.AppendLine("//! DataWarehouse Rust SDK client.");
        sb.AppendLine();
        sb.AppendLine("use tonic::transport::Channel;");
        sb.AppendLine("use crate::error::DataWarehouseError;");
        sb.AppendLine("use crate::types::*;");
        sb.AppendLine();
        sb.AppendLine("/// DataWarehouse client wrapping a gRPC channel.");
        sb.AppendLine("pub struct Client {");
        sb.AppendLine("    channel: Channel,");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("/// Connection options for the client.");
        sb.AppendLine("pub struct ClientOptions {");
        sb.AppendLine("    pub host: String,");
        sb.AppendLine("    pub port: u16,");
        sb.AppendLine("    pub use_tls: bool,");
        sb.AppendLine("    pub auth_token: Option<String>,");
        sb.AppendLine("    pub timeout_ms: u64,");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("impl Default for ClientOptions {");
        sb.AppendLine("    fn default() -> Self {");
        sb.AppendLine("        Self {");
        sb.AppendLine("            host: \"localhost\".to_string(),");
        sb.AppendLine("            port: 9090,");
        sb.AppendLine("            use_tls: false,");
        sb.AppendLine("            auth_token: None,");
        sb.AppendLine("            timeout_ms: 30000,");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("impl Client {");
        sb.AppendLine("    /// Create a new client with the given options.");
        sb.AppendLine("    pub async fn connect(options: ClientOptions) -> Result<Self, DataWarehouseError> {");
        sb.AppendLine("        let endpoint = format!(\"http{}://{}:{}\",");
        sb.AppendLine("            if options.use_tls { \"s\" } else { \"\" },");
        sb.AppendLine("            options.host, options.port);");
        sb.AppendLine("        let channel = Channel::from_shared(endpoint)");
        sb.AppendLine("            .map_err(|e| DataWarehouseError::Connection(e.to_string()))?");
        sb.AppendLine("            .connect()");
        sb.AppendLine("            .await");
        sb.AppendLine("            .map_err(|e| DataWarehouseError::Connection(e.to_string()))?;");
        sb.AppendLine("        Ok(Self { channel })");
        sb.AppendLine("    }");
        sb.AppendLine();

        foreach (var method in surface.Methods)
        {
            GenerateRustMethod(sb, method);
        }

        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("impl Drop for Client {");
        sb.AppendLine("    fn drop(&mut self) {");
        sb.AppendLine("        // Channel cleanup handled by tonic");
        sb.AppendLine("    }");
        sb.AppendLine("}");

        return sb.ToString();
    }

    private static void GenerateRustMethod(StringBuilder sb, SdkMethod method)
    {
        var rustName = method.Name;
        var returnType = method.IsStreaming
            ? $"tonic::Streaming<{method.OutputType.Name}>"
            : method.OutputType.Name;

        sb.Append("    /// ").Append(method.Description).AppendLine(".");
        sb.Append("    pub async fn ").Append(rustName).Append("(&self");

        foreach (var field in method.InputType.Fields)
        {
            sb.Append(", ").Append(field.Name).Append(": ").Append(MapRustType(field.Type, field.Nullable));
        }

        sb.Append(") -> Result<").Append(returnType).AppendLine(", DataWarehouseError> {");
        sb.AppendLine("        todo!(\"requires gRPC stub initialization\")");
        sb.AppendLine("    }");
        sb.AppendLine();
    }

    private static string MapRustType(string sdkType, bool nullable)
    {
        var baseType = sdkType switch
        {
            "string" => "&str",
            "bytes" => "&[u8]",
            "int32" => "i32",
            "int64" => "i64",
            "float64" => "f64",
            "bool" => "bool",
            "datetime" => "&str",
            _ when sdkType.StartsWith("map<", StringComparison.Ordinal) => "std::collections::HashMap<String, String>",
            _ when sdkType.StartsWith("list<", StringComparison.Ordinal) => "Vec<String>",
            _ when sdkType.StartsWith("stream<", StringComparison.Ordinal) => "impl futures::Stream<Item = Vec<u8>>",
            _ => sdkType
        };
        return nullable ? $"Option<{baseType}>" : baseType;
    }

    /// <inheritdoc />
    public string GenerateTypes(SdkApiSurface surface, SdkLanguageBinding binding)
    {
        var sb = new StringBuilder(2048);
        sb.AppendLine("//! DataWarehouse SDK type definitions.");
        sb.AppendLine();
        sb.AppendLine("use serde::{Deserialize, Serialize};");
        sb.AppendLine("use std::collections::HashMap;");
        sb.AppendLine();

        // Enums
        foreach (var e in surface.Enums)
        {
            sb.AppendLine("#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]");
            sb.AppendLine("#[repr(i32)]");
            sb.Append("pub enum ").Append(e.Name).AppendLine(" {");
            foreach (var (name, value) in e.Values)
                sb.Append("    ").Append(name).Append(" = ").Append(value).AppendLine(",");
            sb.AppendLine("}");
            sb.AppendLine();
        }

        // Types as structs
        foreach (var type in surface.Types)
        {
            sb.Append("/// ").Append(type.Name).AppendLine(" SDK type.");
            sb.AppendLine("#[derive(Debug, Clone, Serialize, Deserialize)]");
            sb.Append("pub struct ").Append(type.Name).AppendLine(" {");
            foreach (var f in type.Fields)
            {
                var rustType = MapRustOwnedType(f.Type, f.Nullable);
                sb.Append("    pub ").Append(f.Name).Append(": ").Append(rustType).AppendLine(",");
            }
            sb.AppendLine("}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string MapRustOwnedType(string sdkType, bool nullable)
    {
        var baseType = sdkType switch
        {
            "string" => "String",
            "bytes" => "Vec<u8>",
            "int32" => "i32",
            "int64" => "i64",
            "float64" => "f64",
            "bool" => "bool",
            "datetime" => "String",
            _ when sdkType.StartsWith("map<", StringComparison.Ordinal) => "HashMap<String, String>",
            _ when sdkType.StartsWith("list<", StringComparison.Ordinal) => "Vec<String>",
            _ when sdkType.StartsWith("stream<", StringComparison.Ordinal) => "Vec<u8>",
            _ => sdkType
        };
        return nullable ? $"Option<{baseType}>" : baseType;
    }

    /// <inheritdoc />
    public string GenerateBuildFile(SdkLanguageBinding binding)
    {
        var sb = new StringBuilder(512);
        sb.AppendLine("[package]");
        sb.Append("name = \"").Append(binding.PackageName).AppendLine("\"");
        sb.Append("version = \"").Append(binding.PackageVersion).AppendLine("\"");
        sb.AppendLine("edition = \"2021\"");
        sb.AppendLine("description = \"DataWarehouse Rust SDK\"");
        sb.AppendLine();
        sb.AppendLine("[dependencies]");
        sb.AppendLine("tonic = \"0.11\"");
        sb.AppendLine("tokio = { version = \"1\", features = [\"full\"] }");
        sb.AppendLine("prost = \"0.12\"");
        sb.AppendLine("serde = { version = \"1\", features = [\"derive\"] }");
        sb.AppendLine("serde_json = \"1\"");
        sb.AppendLine("futures = \"0.3\"");
        return sb.ToString();
    }

    /// <inheritdoc />
    public string GenerateReadme(SdkApiSurface surface, SdkLanguageBinding binding)
    {
        var sb = new StringBuilder(512);
        sb.AppendLine("# DataWarehouse Rust SDK");
        sb.AppendLine();
        sb.AppendLine("## Installation");
        sb.AppendLine();
        sb.AppendLine("Add to `Cargo.toml`:");
        sb.AppendLine();
        sb.AppendLine("```toml");
        sb.Append(binding.PackageName).Append(" = \"").Append(binding.PackageVersion).AppendLine("\"");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("## Quick Start");
        sb.AppendLine();
        sb.AppendLine("```rust");
        sb.AppendLine("use datawarehouse::{Client, ClientOptions};");
        sb.AppendLine();
        sb.AppendLine("#[tokio::main]");
        sb.AppendLine("async fn main() -> Result<(), Box<dyn std::error::Error>> {");
        sb.AppendLine("    let client = Client::connect(ClientOptions::default()).await?;");
        sb.AppendLine("    let response = client.store(\"key\", b\"data\", Default::default(), vec![], None, -1, 0).await?;");
        sb.AppendLine("    Ok(())");
        sb.AppendLine("}");
        sb.AppendLine("```");
        return sb.ToString();
    }
}

// =============================================================================
// TypeScript Template
// =============================================================================

/// <summary>
/// Generates TypeScript SDK with grpc-js (Node.js) and grpc-web (browser) support,
/// Promise-based async, typed generics, and ESM+CJS dual output.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Multi-language SDK specification (ECOS-07 through ECOS-11)")]
public sealed class TypeScriptTemplate : ILanguageTemplate
{
    /// <inheritdoc />
    public SdkLanguage Language => SdkLanguage.TypeScript;

    /// <inheritdoc />
    public string GenerateClient(SdkApiSurface surface, SdkLanguageBinding binding)
    {
        var sb = new StringBuilder(4096);
        sb.AppendLine("/**");
        sb.AppendLine(" * DataWarehouse TypeScript SDK client.");
        sb.AppendLine(" *");
        sb.AppendLine(" * Supports both Node.js (@grpc/grpc-js) and browser (grpc-web) targets.");
        sb.AppendLine(" */");
        sb.AppendLine();
        sb.AppendLine("import { credentials, ChannelCredentials } from '@grpc/grpc-js';");
        sb.AppendLine("import { ConnectionOptions, ConnectionHandle } from './types';");
        sb.AppendLine();

        // Import all types
        sb.Append("import type { ");
        var typeNames = surface.Types.Select(t => t.Name).Where(n => n != "ConnectionOptions" && n != "ConnectionHandle");
        sb.Append(string.Join(", ", typeNames));
        sb.AppendLine(" } from './types';");
        sb.AppendLine();

        sb.AppendLine("export interface DataWarehouseClientOptions {");
        sb.AppendLine("  host: string;");
        sb.AppendLine("  port: number;");
        sb.AppendLine("  useTls?: boolean;");
        sb.AppendLine("  authToken?: string;");
        sb.AppendLine("  timeoutMs?: number;");
        sb.AppendLine("  maxRetries?: number;");
        sb.AppendLine("  /** 'node' for @grpc/grpc-js, 'web' for grpc-web */");
        sb.AppendLine("  target?: 'node' | 'web';");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("export class DataWarehouseClient {");
        sb.AppendLine("  private readonly options: Required<DataWarehouseClientOptions>;");
        sb.AppendLine("  private connected = false;");
        sb.AppendLine();
        sb.AppendLine("  constructor(options: DataWarehouseClientOptions) {");
        sb.AppendLine("    this.options = {");
        sb.AppendLine("      host: options.host,");
        sb.AppendLine("      port: options.port,");
        sb.AppendLine("      useTls: options.useTls ?? false,");
        sb.AppendLine("      authToken: options.authToken ?? '',");
        sb.AppendLine("      timeoutMs: options.timeoutMs ?? 30000,");
        sb.AppendLine("      maxRetries: options.maxRetries ?? 3,");
        sb.AppendLine("      target: options.target ?? 'node',");
        sb.AppendLine("    };");
        sb.AppendLine("    this.connected = true;");
        sb.AppendLine("  }");
        sb.AppendLine();

        foreach (var method in surface.Methods)
        {
            GenerateTypeScriptMethod(sb, method);
        }

        sb.AppendLine("  /** Close the gRPC channel. */");
        sb.AppendLine("  async close(): Promise<void> {");
        sb.AppendLine("    this.connected = false;");
        sb.AppendLine("  }");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("export default DataWarehouseClient;");

        return sb.ToString();
    }

    private static void GenerateTypeScriptMethod(StringBuilder sb, SdkMethod method)
    {
        var tsName = ToCamelCaseTs(method.Name);
        var returnType = method.IsStreaming
            ? $"AsyncIterable<{method.OutputType.Name}>"
            : method.OutputType.Name;

        sb.Append("  /** ").Append(method.Description).AppendLine(". */");
        sb.Append("  async ").Append(tsName).Append('(');

        var first = true;
        foreach (var field in method.InputType.Fields)
        {
            if (!first) sb.Append(", ");
            first = false;
            sb.Append(ToCamelCaseTs(field.Name));
            if (field.Nullable) sb.Append('?');
            sb.Append(": ").Append(MapTsType(field.Type));
        }

        sb.Append("): Promise<").Append(returnType).AppendLine("> {");
        sb.AppendLine("    throw new Error('Requires gRPC stub initialization');");
        sb.AppendLine("  }");
        sb.AppendLine();
    }

    private static string MapTsType(string sdkType)
    {
        return sdkType switch
        {
            "string" => "string",
            "bytes" => "Uint8Array",
            "int32" or "int64" or "float64" => "number",
            "bool" => "boolean",
            "datetime" => "string",
            _ when sdkType.StartsWith("map<", StringComparison.Ordinal) => "Record<string, string>",
            _ when sdkType.StartsWith("list<", StringComparison.Ordinal) => "Array<unknown>",
            _ when sdkType.StartsWith("stream<", StringComparison.Ordinal) => "AsyncIterable<Uint8Array>",
            _ => sdkType
        };
    }

    private static string ToCamelCaseTs(string snakeCase)
    {
        var parts = snakeCase.Split('_');
        var sb = new StringBuilder(parts[0]);
        for (int i = 1; i < parts.Length; i++)
        {
            if (parts[i].Length > 0)
            {
                sb.Append(char.ToUpperInvariant(parts[i][0]));
                sb.Append(parts[i].AsSpan(1));
            }
        }
        return sb.ToString();
    }

    /// <inheritdoc />
    public string GenerateTypes(SdkApiSurface surface, SdkLanguageBinding binding)
    {
        var sb = new StringBuilder(2048);
        sb.AppendLine("/**");
        sb.AppendLine(" * DataWarehouse SDK type definitions.");
        sb.AppendLine(" */");
        sb.AppendLine();

        // Enums
        foreach (var e in surface.Enums)
        {
            sb.Append("export enum ").Append(e.Name).AppendLine(" {");
            foreach (var (name, value) in e.Values)
                sb.Append("  ").Append(name).Append(" = ").Append(value).AppendLine(",");
            sb.AppendLine("}");
            sb.AppendLine();
        }

        // Types as interfaces
        foreach (var type in surface.Types)
        {
            sb.Append("export interface ").Append(type.Name).AppendLine(" {");
            foreach (var f in type.Fields)
            {
                var tsName = ToCamelCaseTs(f.Name);
                sb.Append("  ").Append(tsName);
                if (f.Nullable) sb.Append('?');
                sb.Append(": ").Append(MapTsType(f.Type)).AppendLine(";");
            }
            sb.AppendLine("}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <inheritdoc />
    public string GenerateBuildFile(SdkLanguageBinding binding)
    {
        var sb = new StringBuilder(512);
        sb.AppendLine("{");
        sb.Append("  \"name\": \"").Append(binding.PackageName).AppendLine("\",");
        sb.Append("  \"version\": \"").Append(binding.PackageVersion).AppendLine("\",");
        sb.AppendLine("  \"description\": \"DataWarehouse TypeScript SDK\",");
        sb.AppendLine("  \"main\": \"dist/cjs/index.js\",");
        sb.AppendLine("  \"module\": \"dist/esm/index.js\",");
        sb.AppendLine("  \"types\": \"dist/types/index.d.ts\",");
        sb.AppendLine("  \"exports\": {");
        sb.AppendLine("    \".\": {");
        sb.AppendLine("      \"import\": \"./dist/esm/index.js\",");
        sb.AppendLine("      \"require\": \"./dist/cjs/index.js\",");
        sb.AppendLine("      \"types\": \"./dist/types/index.d.ts\"");
        sb.AppendLine("    }");
        sb.AppendLine("  },");
        sb.AppendLine("  \"dependencies\": {");
        sb.AppendLine("    \"@grpc/grpc-js\": \"^1.10.0\",");
        sb.AppendLine("    \"grpc-web\": \"^1.5.0\",");
        sb.AppendLine("    \"google-protobuf\": \"^3.21.0\"");
        sb.AppendLine("  },");
        sb.AppendLine("  \"devDependencies\": {");
        sb.AppendLine("    \"typescript\": \"^5.3.0\"");
        sb.AppendLine("  }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <inheritdoc />
    public string GenerateReadme(SdkApiSurface surface, SdkLanguageBinding binding)
    {
        var sb = new StringBuilder(512);
        sb.AppendLine("# DataWarehouse TypeScript SDK");
        sb.AppendLine();
        sb.AppendLine("## Installation");
        sb.AppendLine();
        sb.Append("```bash\nnpm install ").Append(binding.PackageName).AppendLine("\n```");
        sb.AppendLine();
        sb.AppendLine("## Quick Start");
        sb.AppendLine();
        sb.AppendLine("```typescript");
        sb.AppendLine("import { DataWarehouseClient } from '@datawarehouse/sdk';");
        sb.AppendLine();
        sb.AppendLine("const client = new DataWarehouseClient({ host: 'localhost', port: 9090 });");
        sb.AppendLine();
        sb.AppendLine("// Store data");
        sb.AppendLine("const result = await client.store('my-key', new Uint8Array([1, 2, 3]));");
        sb.AppendLine();
        sb.AppendLine("// Query with async iteration");
        sb.AppendLine("for await (const batch of await client.query('SELECT * FROM objects')) {");
        sb.AppendLine("  console.log(batch);");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("await client.close();");
        sb.AppendLine("```");
        return sb.ToString();
    }
}
