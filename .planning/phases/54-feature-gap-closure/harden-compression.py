#!/usr/bin/env python3
"""
Production readiness hardening for compression strategies.
Adds 7-point production readiness checklist to unhardened strategies.
"""

import os
import re
import sys

BASE_DIR = r"C:\Temp\DataWarehouse\DataWarehouse\Plugins\DataWarehouse.Plugins.UltimateCompression\Strategies"

ALREADY_HARDENED = {
    "LzFamily/LzoStrategy.cs", "LzFamily/Lz77Strategy.cs", "LzFamily/Lz78Strategy.cs",
    "LzFamily/LzfseStrategy.cs", "LzFamily/LzhStrategy.cs", "LzFamily/LzxStrategy.cs",
    "EntropyCoding/HuffmanStrategy.cs", "EntropyCoding/RleStrategy.cs",
    "EntropyCoding/ArithmeticStrategy.cs", "EntropyCoding/AnsStrategy.cs",
    "EntropyCoding/RansStrategy.cs",
    "Transform/BwtStrategy.cs", "Transform/MtfStrategy.cs",
    "Archive/TarStrategy.cs", "Archive/ZipStrategy.cs",
    "Archive/XzStrategy.cs", "Archive/SevenZipStrategy.cs",
}

def has_production_infra(content):
    markers = ['IncrementCounter', 'GetCachedHealthAsync', 'CheckHealthAsync',
               'ShutdownAsyncCore', 'DisposeAsyncCore']
    return sum(1 for m in markers if m in content) >= 3

def get_algo_name(content):
    m = re.search(r'AlgorithmName\s*=\s*"([^"]+)"', content)
    if m:
        return m.group(1)
    m = re.search(r'class\s+(\w+)Strategy', content)
    return m.group(1) if m else "Unknown"

def get_counter_prefix(content):
    algo = get_algo_name(content)
    return algo.lower().replace(' ', '-').replace('/', '-').replace('_', '-')

def find_class_body_start(content):
    """Find the opening brace of the strategy class (not namespace)."""
    # Find the class declaration
    m = re.search(r'public\s+(?:sealed\s+)?class\s+\w+Strategy\s*:\s*\w+', content)
    if not m:
        return None
    # Find the opening brace after class declaration
    pos = m.end()
    brace_pos = content.find('{', pos)
    return brace_pos + 1 if brace_pos >= 0 else None

def find_compress_core_start(content):
    """Find where CompressCore method doc comment starts."""
    # Look for the doc comment or the method itself
    patterns = [
        r'\n(\s*)///[^\n]*\n\s*protected\s+override\s+byte\[\]\s+CompressCore',
        r'\n(\s*)protected\s+override\s+byte\[\]\s+CompressCore',
    ]
    for pat in patterns:
        m = re.search(pat, content)
        if m:
            return m.start() + 1  # +1 to skip the leading newline
    return None

def find_characteristics_end(content):
    """Find end of Characteristics property."""
    m = re.search(r'Characteristics\s*(?:\{[^}]+\}\s*=>\s*new\s*\(\)\s*\{[^}]+\}|=>.*?;|\{[^}]*get[^}]*\})', content, re.DOTALL)
    if m:
        # Find the semicolon after the property
        pos = m.end()
        semi = content.find(';', pos)
        if semi >= 0:
            return semi + 1
        return pos
    return None

def process_file(filepath, relpath):
    with open(filepath, 'r', encoding='utf-8') as f:
        original = f.read()

    content = original
    algo_name = get_algo_name(content)
    counter_prefix = get_counter_prefix(content)
    changes = []

    # 1. Ensure usings exist (at top of file, before namespace)
    top_section_end = content.find('namespace ')
    if top_section_end < 0:
        print(f"  SKIP: no namespace in {relpath}")
        return False

    top = content[:top_section_end]
    usings_to_add = []
    if 'System.Threading;' not in top and 'System.Threading.Tasks' not in top:
        usings_to_add.append('using System.Threading;\nusing System.Threading.Tasks;')
    elif 'System.Threading;' not in top:
        usings_to_add.append('using System.Threading;')
    elif 'System.Threading.Tasks' not in top:
        usings_to_add.append('using System.Threading.Tasks;')
    if 'System.Collections.Generic' not in top:
        usings_to_add.append('using System.Collections.Generic;')
    if 'DataWarehouse.SDK.Contracts;' not in top:
        usings_to_add.append('using DataWarehouse.SDK.Contracts;')

    if usings_to_add:
        insert_text = '\n'.join(usings_to_add) + '\n'
        content = content[:top_section_end] + insert_text + content[top_section_end:]
        changes.append("usings")

    # 2. Add MaxInputSize constant after class opening brace
    if 'MaxInputSize' not in content:
        class_start = find_class_body_start(content)
        if class_start:
            content = content[:class_start] + '\n        private const int MaxInputSize = 100 * 1024 * 1024; // 100 MB\n' + content[class_start:]
            changes.append("MaxInputSize")

    # 3. Add IncrementCounter to CompressCore
    compress_pat = r'(protected\s+override\s+byte\[\]\s+CompressCore\s*\([^)]*\)\s*\{)'
    m = re.search(compress_pat, content)
    if m and 'IncrementCounter' not in content[m.end():m.end()+200]:
        insert_pos = m.end()
        content = content[:insert_pos] + f'\n            IncrementCounter("{counter_prefix}.compress");' + content[insert_pos:]
        changes.append("compress counter")

    # 4. Add IncrementCounter to DecompressCore
    decompress_pat = r'(protected\s+override\s+byte\[\]\s+DecompressCore\s*\([^)]*\)\s*\{)'
    m = re.search(decompress_pat, content)
    if m and 'IncrementCounter' not in content[m.end():m.end()+200]:
        insert_pos = m.end()
        content = content[:insert_pos] + f'\n            IncrementCounter("{counter_prefix}.decompress");' + content[insert_pos:]
        changes.append("decompress counter")

    # 5. Add input guards to CompressCore (after IncrementCounter if present)
    compress_pat2 = r'(protected\s+override\s+byte\[\]\s+CompressCore\s*\([^)]*\)\s*\{[^\n]*\n(?:\s*IncrementCounter[^\n]*\n)?)'
    m = re.search(compress_pat2, content)
    if m and 'MaxInputSize' not in content[m.start():m.start()+500]:
        insert_pos = m.end()
        guard = f'''
            if (input == null || input.Length == 0)
                return input ?? Array.Empty<byte>();

            if (input.Length > MaxInputSize)
                throw new ArgumentException($"Input exceeds maximum size of {{MaxInputSize / (1024 * 1024)}} MB for {algo_name}");
'''
        content = content[:insert_pos] + guard + content[insert_pos:]
        changes.append("compress guard")

    # 6. Add input guards to DecompressCore
    decompress_pat2 = r'(protected\s+override\s+byte\[\]\s+DecompressCore\s*\([^)]*\)\s*\{[^\n]*\n(?:\s*IncrementCounter[^\n]*\n)?)'
    m = re.search(decompress_pat2, content)
    if m and 'MaxInputSize' not in content[m.start():m.start()+500]:
        insert_pos = m.end()
        guard = f'''
            if (input == null || input.Length == 0)
                return input ?? Array.Empty<byte>();

            if (input.Length > MaxInputSize)
                throw new ArgumentException($"Input exceeds maximum size of {{MaxInputSize / (1024 * 1024)}} MB for {algo_name}");
'''
        content = content[:insert_pos] + guard + content[insert_pos:]
        changes.append("decompress guard")

    # 7. Add health check, shutdown, dispose INSIDE the class, before CompressCore
    if 'CheckHealthAsync' not in content:
        # Strategy: find CompressCore position and insert before it
        insert_pos = find_compress_core_start(content)

        if insert_pos is None:
            # Try after Characteristics
            insert_pos = find_characteristics_end(content)

        if insert_pos is None:
            # Last resort: after class opening + MaxInputSize
            class_start = find_class_body_start(content)
            if class_start:
                # Find end of the MaxInputSize line if present
                max_line = content.find('MaxInputSize', class_start)
                if max_line >= 0:
                    insert_pos = content.find('\n', max_line) + 1
                else:
                    insert_pos = class_start

        if insert_pos:
            block = f'''
        /// <summary>
        /// Performs a health check by executing a small compression round-trip test.
        /// Result is cached for 60 seconds.
        /// </summary>
        public async Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default)
        {{
            return await GetCachedHealthAsync(async ct =>
            {{
                try
                {{
                    var testData = new byte[] {{ 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 }};
                    var compressed = CompressCore(testData);
                    var decompressed = DecompressCore(compressed);

                    if (decompressed.Length != testData.Length)
                    {{
                        return new StrategyHealthCheckResult(
                            false,
                            $"Health check failed: decompressed length {{decompressed.Length}} != original {{testData.Length}}");
                    }}

                    return new StrategyHealthCheckResult(
                        true,
                        "{algo_name} strategy healthy",
                        new Dictionary<string, object>
                        {{
                            ["CompressOperations"] = GetCounter("{counter_prefix}.compress"),
                            ["DecompressOperations"] = GetCounter("{counter_prefix}.decompress")
                        }});
                }}
                catch (Exception ex)
                {{
                    return new StrategyHealthCheckResult(false, $"Health check failed: {{ex.Message}}");
                }}
            }}, TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
        }}

        /// <inheritdoc/>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {{
            return base.ShutdownAsyncCore(cancellationToken);
        }}

        /// <inheritdoc/>
        protected override ValueTask DisposeAsyncCore()
        {{
            return base.DisposeAsyncCore();
        }}

'''
            content = content[:insert_pos] + block + content[insert_pos:]
            changes.append("health+shutdown+dispose")

    if content != original:
        with open(filepath, 'w', encoding='utf-8') as f:
            f.write(content)
        print(f"  HARDENED: {relpath} [{', '.join(changes)}]")
        return True
    else:
        print(f"  SKIP (no changes needed): {relpath}")
        return False

def main():
    modified = 0
    skipped = 0

    for root, dirs, files in os.walk(BASE_DIR):
        for fn in sorted(files):
            if not fn.endswith('.cs'):
                continue
            fp = os.path.join(root, fn)
            rp = os.path.relpath(fp, BASE_DIR).replace('\\', '/')

            if rp in ALREADY_HARDENED:
                print(f"  SKIP (Phase 50.1): {rp}")
                skipped += 1
                continue

            with open(fp, 'r', encoding='utf-8') as f:
                c = f.read()
            if has_production_infra(c):
                print(f"  SKIP (already hardened): {rp}")
                skipped += 1
                continue

            if process_file(fp, rp):
                modified += 1
            else:
                skipped += 1

    print(f"\nSummary: {modified} hardened, {skipped} skipped")
    return 0

if __name__ == '__main__':
    sys.exit(main())
