#!/usr/bin/env python3
"""
DataWarehouse Deep Semantic Audit Analyzer
Phase 65.1-04: Comprehensive production-readiness audit

Scans ALL .cs files in the repository and detects:
  Category 1: Silent stubs (Task.CompletedTask, yield break, empty returns)
  Category 2: No-op patterns (delegate passthrough, NotImplementedException, empty catch)
  Category 3: Unbounded collections (ConcurrentDictionary, unbound Dictionary/List fields)
  Category 4: Sync-over-async (.GetAwaiter().GetResult(), .Result, .Wait())
  Category 5: Obsolete code ([Obsolete], #pragma warning disable CS0618)
  Category 6: Hardcoded limits (magic numbers in capacities, hardcoded timeouts)
  Category 7: Suppressed build diagnostics (temporarily remove NoWarn, build, parse)

Output: Metadata/deep-audit-report.json
"""

import os
import re
import json
import subprocess
import shutil
import sys
from pathlib import Path
from typing import List, Dict, Any, Optional, Tuple

# ── repo root ───────────────────────────────────────────────────────────────
REPO_ROOT = Path(__file__).resolve().parent.parent
METADATA_DIR = REPO_ROOT / "Metadata"
REPORT_PATH = METADATA_DIR / "deep-audit-report.json"
BUILD_PROPS = REPO_ROOT / "Directory.Build.props"
SLN_FILE = REPO_ROOT / "DataWarehouse.slnx"

# ── directories to skip ─────────────────────────────────────────────────────
SKIP_DIRS = {"bin", "obj", ".planning", ".git", "artifacts", "Temp", "__pycache__"}

# ── files/directories to treat as safe (no stub alerts) ─────────────────────
SAFE_PATH_FRAGMENTS = {"Test", "Benchmark", "Bench", ".Tests", ".Benchmarks"}

# ── PluginBase lifecycle methods that legitimately return Task.CompletedTask ─
PLUGINBASE_LIFECYCLE = {
    "ActivateAsync", "ExecuteAsync", "CheckHealthAsync", "OnMessageAsync",
    "DeactivateAsync", "DisposeAsync", "OnHandshakeAsync",
    "OnBeforeStatePersistAsync", "OnAfterStatePersistAsync",
    "CreateCustomStateStore",
}

# ── method name prefixes/fragments that must do work ────────────────────────
STUB_WORK_NAMES = re.compile(
    r"(?i)(Publish|Subscribe|Execute|Process|Handle|Send|Write|Save|Apply|"
    r"Produce|Emit|Dispatch|Transmit|Flush|Deliver|Enqueue|Commit|Push|"
    r"Broadcast|Forward|Stream|Replicate|Index|Store|Persist|Sync)",
    re.IGNORECASE,
)

YIELD_WORK_NAMES = re.compile(
    r"(?i)(Get|List|Enumerate|Subscribe|Stream|Query|Find|Search|Scan|Fetch|"
    r"Read|Load|Retrieve|Select|Collect|Iterate)",
    re.IGNORECASE,
)

findings: List[Dict[str, Any]] = []


def find_cs_files() -> List[Path]:
    """Recursively find all .cs files, skipping excluded directories."""
    cs_files = []
    for root, dirs, files in os.walk(REPO_ROOT):
        root_path = Path(root)
        # Prune skip dirs
        dirs[:] = [d for d in dirs if d not in SKIP_DIRS]
        for f in files:
            if f.endswith(".cs"):
                cs_files.append(root_path / f)
    return cs_files


def is_safe_path(path: Path) -> bool:
    """Return True if this file is a test/benchmark file (stubs are OK there)."""
    parts = set(path.parts)
    for fragment in SAFE_PATH_FRAGMENTS:
        for part in path.parts:
            if fragment in part:
                return True
    return False


def rel(path: Path) -> str:
    """Return path relative to repo root."""
    try:
        return str(path.relative_to(REPO_ROOT))
    except ValueError:
        return str(path)


def read_file(path: Path) -> Optional[List[str]]:
    """Read file lines with graceful error handling."""
    try:
        with open(path, "r", encoding="utf-8", errors="replace") as f:
            return f.readlines()
    except Exception:
        return None


def add_finding(
    path: Path,
    line_no: int,
    category: int,
    category_name: str,
    severity: str,
    pattern: str,
    snippet: str,
    details: str = "",
) -> None:
    findings.append({
        "file": rel(path),
        "line": line_no,
        "category": category,
        "category_name": category_name,
        "severity": severity,
        "pattern": pattern,
        "snippet": snippet.strip(),
        "details": details,
    })


def get_snippet(lines: List[str], line_idx: int, context: int = 2) -> str:
    """Return line_idx line + context surrounding lines (1-based display)."""
    start = max(0, line_idx - context)
    end = min(len(lines), line_idx + context + 1)
    parts = []
    for i in range(start, end):
        marker = ">>>" if i == line_idx else "   "
        parts.append(f"{marker} {i+1:5d}: {lines[i].rstrip()}")
    return "\n".join(parts)


# ─────────────────────────────────────────────────────────────────────────────
#  CATEGORY 1: Silent Stubs
# ─────────────────────────────────────────────────────────────────────────────

# Match: return Task.CompletedTask; or => Task.CompletedTask;
RE_COMPLETED_TASK = re.compile(r"\bTask\.CompletedTask\b")
# Match: yield break;
RE_YIELD_BREAK = re.compile(r"\byield\s+break\s*;")
# Match: return default; / return default(T); / return null; / return Array.Empty / return new List<>() etc.
RE_EMPTY_RETURN = re.compile(
    r"^\s*(?:return\s+(?:default(?:\([^)]*\))?|null|"
    r"Array\.Empty<[^>]*>\(\)|Enumerable\.Empty<[^>]*>\(\)|"
    r"new\s+(?:List|Dictionary|HashSet|Queue|Stack|Array)<[^>]*>\(\))\s*;)\s*$"
)

# Extract current method name (simple heuristic: look back for 'async ... MethodName(')
RE_METHOD_DEF = re.compile(
    r"(?:public|protected|private|internal|override|virtual|abstract|static|async|new|sealed)\s+"
    r"(?:[\w<>\[\],\s?]+?)\s+(\w+)\s*[\(<]",
    re.MULTILINE,
)


def get_current_method(lines: List[str], line_idx: int) -> str:
    """Scan backward from line_idx to find the nearest method name."""
    # Look back up to 30 lines
    start = max(0, line_idx - 30)
    for i in range(line_idx, start - 1, -1):
        m = RE_METHOD_DEF.search(lines[i])
        if m:
            return m.group(1)
    return ""


def scan_cat1_silent_stubs(path: Path, lines: List[str], safe: bool) -> None:
    """Category 1: Silent stubs."""
    if safe:
        return

    for idx, line in enumerate(lines):
        stripped = line.strip()

        # 1a: Task.CompletedTask in a work-suggesting method
        if RE_COMPLETED_TASK.search(line):
            method = get_current_method(lines, idx)
            if method and method not in PLUGINBASE_LIFECYCLE and STUB_WORK_NAMES.search(method):
                add_finding(
                    path, idx + 1, 1, "Silent Stubs", "critical",
                    "Task.CompletedTask in work-suggesting method",
                    get_snippet(lines, idx),
                    f"Method '{method}' returns Task.CompletedTask — likely a stub",
                )

        # 1b: yield break in IAsyncEnumerable/IEnumerable producing method
        if RE_YIELD_BREAK.search(line):
            method = get_current_method(lines, idx)
            if method and YIELD_WORK_NAMES.search(method):
                add_finding(
                    path, idx + 1, 1, "Silent Stubs", "critical",
                    "yield break in enumerable method",
                    get_snippet(lines, idx),
                    f"Method '{method}' uses yield break — produces nothing",
                )

        # 1c: Empty returns (return null/default/empty collection as sole action)
        if RE_EMPTY_RETURN.match(line):
            method = get_current_method(lines, idx)
            if method and STUB_WORK_NAMES.search(method):
                add_finding(
                    path, idx + 1, 1, "Silent Stubs", "critical",
                    "Empty/null return in work-suggesting method",
                    get_snippet(lines, idx),
                    f"Method '{method}' returns empty/null — likely a stub",
                )


# ─────────────────────────────────────────────────────────────────────────────
#  CATEGORY 2: No-Op Patterns
# ─────────────────────────────────────────────────────────────────────────────

RE_NOT_IMPLEMENTED = re.compile(r"\bthrow\s+new\s+(NotImplementedException|NotSupportedException)\b")
RE_EMPTY_CATCH_START = re.compile(r"^\s*catch\s*(?:\([^)]*\))?\s*\{?\s*$")
RE_BRACE_ONLY = re.compile(r"^\s*[\{\}]\s*$")
RE_COMMENT_LINE = re.compile(r"^\s*(?://|/\*|\*)")

# For delegate passthrough: detect method param named strategy/policy/handler that doesn't appear in body
RE_PARAM_NAMES = re.compile(
    r"\b(strategy|policy|handler|processor|filter|visitor|transformer|converter|resolver)\b",
    re.IGNORECASE,
)


def scan_cat2_noop_patterns(path: Path, lines: List[str], safe: bool) -> None:
    """Category 2: No-op patterns."""

    for idx, line in enumerate(lines):
        # 2a: NotImplementedException / NotSupportedException in non-abstract methods
        if RE_NOT_IMPLEMENTED.search(line):
            # Check if it's in an abstract method context — look back for 'abstract'
            is_abstract = False
            for back in range(max(0, idx - 5), idx):
                if "abstract" in lines[back] or "interface" in lines[back]:
                    is_abstract = True
                    break
            if not is_abstract:
                add_finding(
                    path, idx + 1, 2, "No-Op Patterns", "high",
                    "NotImplementedException/NotSupportedException",
                    get_snippet(lines, idx),
                    "Non-abstract method throws NotImplementedException — stub",
                )

        # 2b: Empty catch blocks
        if RE_EMPTY_CATCH_START.match(line):
            # Look ahead for empty body (only whitespace, comments, or closing brace)
            j = idx + 1
            body_empty = True
            while j < len(lines) and j < idx + 6:
                next_line = lines[j].strip()
                if not next_line:
                    j += 1
                    continue
                if next_line == "}":
                    break
                if RE_COMMENT_LINE.match(lines[j]):
                    # Comment-only is still effectively empty
                    j += 1
                    continue
                # Has actual code
                body_empty = False
                break
            if body_empty:
                add_finding(
                    path, idx + 1, 2, "No-Op Patterns", "high",
                    "Empty catch block",
                    get_snippet(lines, idx),
                    "Catch block swallows exceptions silently",
                )

    # 2c: Delegate passthrough — scan method bodies for unused strategy/policy parameters
    # This is a function-level check: find methods with such params and verify they appear in body
    scan_delegate_passthrough(path, lines)


def scan_delegate_passthrough(path: Path, lines: List[str]) -> None:
    """Detect methods that accept a strategy/policy/handler but never use it."""
    text = "".join(lines)
    # Find method signatures that have such parameters
    # Pattern: look for method declarations with the parameter keywords
    method_pattern = re.compile(
        r"(?:public|protected|private|internal|override|virtual|static|async)\s+"
        r"[\w<>\[\],\s?]+\s+(\w+)\s*\(([^)]*)\)\s*(?:where[^{]*)?(?:=>|{)",
        re.MULTILINE,
    )
    for m in method_pattern.finditer(text):
        method_name = m.group(1)
        params = m.group(2)
        if not RE_PARAM_NAMES.search(params):
            continue

        # Find each matching parameter name
        for param_match in RE_PARAM_NAMES.finditer(params):
            param_type = param_match.group(1).lower()
            # Extract the actual parameter variable name (last word before comma or end)
            # Look for: <Type> <name>
            param_block = params[param_match.start():]
            # Find the variable name after the keyword
            var_m = re.search(r"\b" + re.escape(param_match.group(1)) + r"\b\s+(\w+)", params[param_match.start():], re.IGNORECASE)
            if not var_m:
                # Try: param type as part of longer type name
                var_m = re.search(r"I?\w*" + re.escape(param_match.group(1)) + r"\w*\s+(\w+)", params[param_match.start():], re.IGNORECASE)
            if not var_m:
                continue

            var_name = var_m.group(1)
            if not var_name or var_name in ("this",):
                continue

            # Get method body (from match end to next matching brace)
            body_start = m.end()
            body = text[body_start:body_start + 2000]  # scan first 2000 chars of body

            # Check if var_name appears in the body
            if not re.search(r"\b" + re.escape(var_name) + r"\b", body):
                # Find line number
                line_no = text[:m.start()].count("\n") + 1
                add_finding(
                    path, line_no, 2, "No-Op Patterns", "high",
                    f"Unused strategy/policy parameter '{var_name}'",
                    lines[max(0, line_no - 1)].rstrip(),
                    f"Method '{method_name}' receives '{var_name}' ({param_match.group(1)}) but never uses it",
                )


# ─────────────────────────────────────────────────────────────────────────────
#  CATEGORY 3: Unbounded Collections
# ─────────────────────────────────────────────────────────────────────────────

RE_CONCURRENT_DICT = re.compile(r"\bConcurrentDictionary\s*<")
RE_FIELD_NEW_DICT = re.compile(r"(?:private|protected|internal|public)\s+.*\bnew\s+Dictionary\s*<")
RE_FIELD_NEW_LIST = re.compile(r"(?:private|protected|internal|public)\s+.*\bnew\s+List\s*<")

ALLOWED_CONCURRENT_DICT_PATHS = {
    "DataWarehouse.SDK/Contracts/Persistence",  # BoundedDictionary itself uses one internally
    "DataWarehouse.SDK/Contracts/PluginBase",   # after migration it's gone
}


def scan_cat3_unbounded(path: Path, lines: List[str], safe: bool) -> None:
    """Category 3: Unbounded collections."""
    rel_path = rel(path)

    # Only flag Plugins/ and SDK/ — kernel/other paths are lower priority
    in_scope = "Plugins/" in rel_path or "DataWarehouse.SDK/" in rel_path

    for idx, line in enumerate(lines):
        # 3a: ConcurrentDictionary in plugins/SDK
        if in_scope and RE_CONCURRENT_DICT.search(line):
            # Skip known-safe paths (BoundedDictionary internals)
            skip = any(safe_frag in rel_path for safe_frag in ALLOWED_CONCURRENT_DICT_PATHS)
            if not skip:
                add_finding(
                    path, idx + 1, 3, "Unbounded Collections", "high",
                    "ConcurrentDictionary usage",
                    get_snippet(lines, idx),
                    "Should use BoundedDictionary after migration (Phase 65.1-02)",
                )

        # 3b: Class field assigned new Dictionary/List without bounds
        if RE_FIELD_NEW_DICT.search(line):
            # Exclude local variables (no field modifiers) and constructor params
            if "class " not in line and "interface " not in line:
                add_finding(
                    path, idx + 1, 3, "Unbounded Collections", "medium",
                    "Field assigned new Dictionary without bounds",
                    get_snippet(lines, idx),
                    "Class field uses unbounded Dictionary — consider BoundedDictionary",
                )

        if RE_FIELD_NEW_LIST.search(line):
            if "class " not in line and "interface " not in line:
                add_finding(
                    path, idx + 1, 3, "Unbounded Collections", "medium",
                    "Field assigned new List without bounds",
                    get_snippet(lines, idx),
                    "Class field uses unbounded List — consider BoundedList",
                )


# ─────────────────────────────────────────────────────────────────────────────
#  CATEGORY 4: Sync-over-Async
# ─────────────────────────────────────────────────────────────────────────────

RE_GET_AWAITER = re.compile(r"\.GetAwaiter\(\)\.GetResult\(\)")
RE_TASK_RESULT = re.compile(r"(?<!\w)(?<!Task\.From)(?<!TaskCompletionSource)\.Result\b")
RE_TASK_WAIT = re.compile(r"\.\s*Wait\s*\(\s*\)")

# Exclude: property/field declarations named Result, TaskCompletionSource.Result, Task.FromResult
RE_RESULT_EXCLUSION = re.compile(
    r"(?:Task\.FromResult|TaskCompletionSource|public\s+\w+\s+Result\s*[{=]|"
    r"private\s+\w+\s+Result\s*[{=]|protected\s+\w+\s+Result\s*[{=])"
)


def scan_cat4_sync_over_async(path: Path, lines: List[str]) -> None:
    """Category 4: Sync-over-async anti-patterns."""
    for idx, line in enumerate(lines):
        # 4a: .GetAwaiter().GetResult()
        if RE_GET_AWAITER.search(line):
            add_finding(
                path, idx + 1, 4, "Sync-over-Async", "high",
                ".GetAwaiter().GetResult()",
                get_snippet(lines, idx),
                "Sync-over-async blocks the thread — use await instead",
            )

        # 4b: .Result on Task (excluding safe patterns)
        if RE_TASK_RESULT.search(line) and not RE_RESULT_EXCLUSION.search(line):
            # Additional filter: skip lines with 'Task.FromResult' or 'new TaskCompletionSource'
            if "Task.FromResult" not in line and "TaskCompletionSource" not in line:
                add_finding(
                    path, idx + 1, 4, "Sync-over-Async", "high",
                    "Task.Result access",
                    get_snippet(lines, idx),
                    ".Result blocks synchronously on async Task — use await",
                )

        # 4c: .Wait() on Task
        if RE_TASK_WAIT.search(line):
            # Exclude: event.Wait(), semaphore.Wait(), ManualResetEvent.Wait()
            if "SemaphoreSlim" not in line and "ManualResetEvent" not in line and "AutoReset" not in line:
                add_finding(
                    path, idx + 1, 4, "Sync-over-Async", "high",
                    "Task.Wait()",
                    get_snippet(lines, idx),
                    ".Wait() blocks synchronously — use await",
                )


# ─────────────────────────────────────────────────────────────────────────────
#  CATEGORY 5: Obsolete Code
# ─────────────────────────────────────────────────────────────────────────────

RE_OBSOLETE_ATTR = re.compile(r"\[Obsolete(?:\([^)]*\))?\]")
RE_PRAGMA_DISABLE_0618 = re.compile(r"#pragma\s+warning\s+disable\s+CS0618")


def scan_cat5_obsolete(path: Path, lines: List[str]) -> None:
    """Category 5: Obsolete code markers."""
    for idx, line in enumerate(lines):
        if RE_OBSOLETE_ATTR.search(line):
            add_finding(
                path, idx + 1, 5, "Obsolete Code", "medium",
                "[Obsolete] attribute",
                get_snippet(lines, idx),
                "Member marked [Obsolete] — should be removed or migrated",
            )

        if RE_PRAGMA_DISABLE_0618.search(line):
            add_finding(
                path, idx + 1, 5, "Obsolete Code", "medium",
                "#pragma warning disable CS0618",
                get_snippet(lines, idx),
                "Locally suppressing obsolete usage — should fix the underlying issue",
            )


# ─────────────────────────────────────────────────────────────────────────────
#  CATEGORY 6: Hardcoded Limits
# ─────────────────────────────────────────────────────────────────────────────

RE_LARGE_CAPACITY = re.compile(r"new\s+(?:Dictionary|List|Queue|Stack|HashSet|ConcurrentDictionary)\s*<[^>]*>\s*\(\s*(\d+)\s*\)")
RE_HARDCODED_TIMEOUT = re.compile(
    r"TimeSpan\.From(?:Seconds|Minutes|Hours|Milliseconds)\s*\(\s*(\d+)\s*\)"
)
RE_ARRAY_ALLOC = re.compile(r"new\s+\w+\s*\[\s*(\d+)\s*\]")

# Configuration-related files are allowed to have hardcoded defaults
CONFIG_PATH_FRAGMENTS = {"Config", "Option", "Setting", "Default", "Constant", "Limit"}


def is_config_file(path: Path) -> bool:
    return any(frag in path.name for frag in CONFIG_PATH_FRAGMENTS)


def scan_cat6_hardcoded_limits(path: Path, lines: List[str]) -> None:
    """Category 6: Hardcoded limits and magic numbers."""
    if is_config_file(path):
        return  # Config files are allowed to define defaults

    for idx, line in enumerate(lines):
        # 6a: Large collection capacity literals
        m = RE_LARGE_CAPACITY.search(line)
        if m:
            capacity = int(m.group(1))
            if capacity > 100:
                add_finding(
                    path, idx + 1, 6, "Hardcoded Limits", "low",
                    f"Magic number collection capacity: {capacity}",
                    get_snippet(lines, idx),
                    f"Collection capacity {capacity} hardcoded — use configuration constant",
                )

        # 6b: Hardcoded timeout values (not in config files)
        m = RE_HARDCODED_TIMEOUT.search(line)
        if m:
            add_finding(
                path, idx + 1, 6, "Hardcoded Limits", "low",
                f"Hardcoded timeout: {m.group(0)}",
                get_snippet(lines, idx),
                "Timeout value hardcoded — should come from configuration",
            )

        # 6c: Large array allocations
        m = RE_ARRAY_ALLOC.search(line)
        if m:
            size = int(m.group(1))
            if size > 100:
                add_finding(
                    path, idx + 1, 6, "Hardcoded Limits", "low",
                    f"Large hardcoded array allocation: [{size}]",
                    get_snippet(lines, idx),
                    f"Array of {size} elements hardcoded — use configuration",
                )


# ─────────────────────────────────────────────────────────────────────────────
#  CATEGORY 7: Suppressed Build Diagnostics
# ─────────────────────────────────────────────────────────────────────────────

NOWARN_LINE_PATTERN = re.compile(
    r"<NoWarn>\$\(NoWarn\);CS0618;CS0649;CS0219;CS0414;CS0169;CS0162;CS0067;CS0168;CA1416;CA1418;CA2024;NU1608</NoWarn>"
)

DIAGNOSTIC_CODES = {
    "CS0618": "Obsolete API usage",
    "CS0649": "Field never assigned (initialized at runtime?)",
    "CS0219": "Variable assigned but never used",
    "CS0414": "Field assigned but never read",
    "CS0169": "Field never used (dead field)",
    "CS0162": "Unreachable code detected",
    "CS0067": "Event never used",
    "CS0168": "Variable declared but never used",
    "CA1416": "Platform compatibility issue",
    "CA1418": "Platform unsupported guard missing",
    "CA2024": "Missing CancellationToken",
    "NU1608": "Package version constraint violation",
}

# Pattern for parsing dotnet build warning lines
# Example: /path/to/File.cs(123,45): warning CS0618: 'X' is obsolete...
RE_BUILD_WARNING = re.compile(
    r"^(?P<file>[^(]+)\((?P<line>\d+),(?P<col>\d+)\):\s+warning\s+(?P<code>[A-Z]+\d+):\s*(?P<msg>.+)$"
)
RE_BUILD_ERROR = re.compile(
    r"^(?P<file>[^(]+)\((?P<line>\d+),(?P<col>\d+)\):\s+error\s+(?P<code>[A-Z]+\d+):\s*(?P<msg>.+)$"
)


def scan_cat7_suppressed_diagnostics() -> None:
    """
    Category 7: Temporarily remove NoWarn from Directory.Build.props,
    run dotnet build, capture all suppressed warnings, restore original.
    """
    print("\n[Category 7] Running suppressed diagnostic scan...")
    print("  Reading Directory.Build.props...")

    props_path = BUILD_PROPS
    original_content = props_path.read_text(encoding="utf-8")

    # Backup
    backup_content = original_content

    try:
        # Patch 1: Reduce NoWarn to just $(NoWarn) to expose suppressed warnings
        patched = original_content.replace(
            "<NoWarn>$(NoWarn);CS0618;CS0649;CS0219;CS0414;CS0169;CS0162;CS0067;CS0168;CA1416;CA1418;CA2024;NU1608</NoWarn>",
            "<NoWarn>$(NoWarn)</NoWarn>",
        )
        # Patch 2: Set TreatWarningsAsErrors to false so warnings don't abort the build
        patched = patched.replace(
            "<TreatWarningsAsErrors>true</TreatWarningsAsErrors>",
            "<TreatWarningsAsErrors>false</TreatWarningsAsErrors>",
        )

        if patched == original_content:
            print("  WARNING: NoWarn pattern not found in Directory.Build.props — skipping Category 7")
            add_finding(
                props_path, 41, 7, "Suppressed Build Diagnostics", "high",
                "NoWarn pattern not found",
                "Directory.Build.props:41",
                "Could not patch Directory.Build.props to expose suppressed warnings",
            )
            return

        # Write patched file
        props_path.write_text(patched, encoding="utf-8")
        print("  Patched Directory.Build.props (removed NoWarn, set TreatWarningsAsErrors=false)")

        # Run dotnet build
        print("  Running: dotnet build DataWarehouse.slnx --no-incremental ...")
        result = subprocess.run(
            ["dotnet", "build", str(SLN_FILE), "--no-incremental"],
            cwd=str(REPO_ROOT),
            capture_output=True,
            text=True,
            timeout=600,
        )

        output = result.stdout + "\n" + result.stderr
        print(f"  Build exit code: {result.returncode}")

        # Parse warnings and errors from build output
        warning_count = 0
        for line in output.splitlines():
            m = RE_BUILD_WARNING.match(line.strip())
            if not m:
                # Try with error too (in case -warnaserror mode leaked through)
                m = RE_BUILD_ERROR.match(line.strip())
                if not m:
                    continue
                is_error = True
            else:
                is_error = False

            code = m.group("code")
            if code not in DIAGNOSTIC_CODES and not code.startswith("CS") and not code.startswith("CA"):
                continue  # Only care about C#/analyzer diagnostics

            file_path_raw = m.group("file").strip()
            # Normalize path
            try:
                abs_path = Path(file_path_raw)
                if not abs_path.is_absolute():
                    abs_path = REPO_ROOT / file_path_raw
                file_rel = rel(abs_path)
            except Exception:
                file_rel = file_path_raw

            diag_line = int(m.group("line"))
            msg = m.group("msg").strip()
            description = DIAGNOSTIC_CODES.get(code, f"Diagnostic {code}")
            severity = "high" if is_error else ("high" if code in ("CS0618", "CS0067", "CS0169", "CS0649") else "medium")

            add_finding(
                props_path, diag_line, 7, "Suppressed Build Diagnostics", severity,
                f"{code}: {description}",
                f"{file_rel}({diag_line}): {msg[:200]}",
                f"File: {file_rel}, Code: {code}, Message: {msg[:300]}",
            )
            # Override file for correct attribution
            findings[-1]["file"] = file_rel
            findings[-1]["line"] = diag_line
            warning_count += 1

        print(f"  Found {warning_count} suppressed diagnostics")

    except subprocess.TimeoutExpired:
        print("  WARNING: Build timed out after 600s — partial results captured")
        add_finding(
            props_path, 41, 7, "Suppressed Build Diagnostics", "high",
            "Build timeout",
            "dotnet build timed out",
            "Category 7 scan incomplete — build exceeded 600s timeout",
        )
    except Exception as e:
        print(f"  ERROR during Category 7 scan: {e}")
        add_finding(
            props_path, 41, 7, "Suppressed Build Diagnostics", "high",
            f"Scan error: {type(e).__name__}",
            str(e),
            f"Category 7 scan failed: {e}",
        )
    finally:
        # ALWAYS restore original file
        props_path.write_text(backup_content, encoding="utf-8")
        print("  Restored original Directory.Build.props")


# ─────────────────────────────────────────────────────────────────────────────
#  MAIN
# ─────────────────────────────────────────────────────────────────────────────

def main() -> None:
    print(f"DataWarehouse Deep Semantic Audit Analyzer")
    print(f"Repo root: {REPO_ROOT}")
    print(f"Report: {REPORT_PATH}")
    print()

    # Find all .cs files
    print("Scanning for .cs files...")
    cs_files = find_cs_files()
    print(f"Found {len(cs_files)} .cs files")
    print()

    # Categories 1-6: file-by-file scan
    for i, path in enumerate(cs_files):
        if i % 100 == 0:
            print(f"  [{i}/{len(cs_files)}] Scanning files...")

        lines = read_file(path)
        if lines is None:
            continue

        safe = is_safe_path(path)

        scan_cat1_silent_stubs(path, lines, safe)
        scan_cat2_noop_patterns(path, lines, safe)
        scan_cat3_unbounded(path, lines, safe)
        scan_cat4_sync_over_async(path, lines)
        scan_cat5_obsolete(path, lines)
        scan_cat6_hardcoded_limits(path, lines)

    print(f"\nFile scan complete. {len(findings)} findings before Category 7.")

    # Category 7: build-based suppressed diagnostics scan
    scan_cat7_suppressed_diagnostics()

    print(f"\nTotal findings: {len(findings)}")

    # ── Summary by category and severity ────────────────────────────────────
    from collections import defaultdict
    by_cat: Dict[int, List] = defaultdict(list)
    by_sev: Dict[str, int] = defaultdict(int)

    for f in findings:
        by_cat[f["category"]].append(f)
        by_sev[f["severity"]] += 1

    print("\n=== FINDINGS SUMMARY ===")
    cat_names = {
        1: "Silent Stubs",
        2: "No-Op Patterns",
        3: "Unbounded Collections",
        4: "Sync-over-Async",
        5: "Obsolete Code",
        6: "Hardcoded Limits",
        7: "Suppressed Build Diagnostics",
    }
    for cat_num in sorted(by_cat.keys()):
        cat_findings = by_cat[cat_num]
        sev_counts = defaultdict(int)
        for f in cat_findings:
            sev_counts[f["severity"]] += 1
        sev_str = ", ".join(f"{v} {k}" for k, v in sorted(sev_counts.items()))
        print(f"  Category {cat_num} ({cat_names.get(cat_num, '?')}): {len(cat_findings)} findings ({sev_str})")

    print("\nSeverity totals:")
    for sev in ["critical", "high", "medium", "low"]:
        print(f"  {sev}: {by_sev.get(sev, 0)}")

    # ── Spot-check known issues ──────────────────────────────────────────────
    print("\n=== SPOT-CHECK KNOWN ISSUES ===")
    checks = [
        ("UltimateStreamingData PublishAsync Task.CompletedTask", "UltimateStreamingData", "Task.CompletedTask"),
        ("UltimateResilience ExecuteWithResilienceAsync", "UltimateResilience", "ExecuteWithResilienceAsync"),
        ("ChaosVaccination GetAwaiter().GetResult()", "ChaosVaccination", ".GetAwaiter().GetResult()"),
        ("CS0618 VectorClock obsolete", "CS0618", ""),
        ("CS0067 unused events", "CS0067", ""),
        ("CS0169 dead fields", "CS0169", ""),
    ]

    for check_name, file_frag, code_frag in checks:
        matched = []
        for f in findings:
            file_match = file_frag in f.get("file", "") or file_frag in f.get("snippet", "") or file_frag in f.get("pattern", "") or file_frag in f.get("details", "")
            code_match = not code_frag or code_frag in f.get("snippet", "") or code_frag in f.get("pattern", "") or code_frag in f.get("details", "")
            if file_match and code_match:
                matched.append(f)
        status = "FOUND" if matched else "NOT FOUND"
        print(f"  [{status}] {check_name}: {len(matched)} finding(s)")
        if matched:
            f0 = matched[0]
            print(f"           -> {f0['file']}:{f0['line']} [{f0['pattern']}]")

    # ── Write JSON report ────────────────────────────────────────────────────
    report = {
        "meta": {
            "tool": "DataWarehouse Deep Semantic Audit Analyzer",
            "phase": "65.1-04",
            "repo_root": str(REPO_ROOT),
            "cs_files_scanned": len(cs_files),
            "total_findings": len(findings),
            "findings_by_category": {
                str(cat): len(by_cat[cat]) for cat in sorted(by_cat.keys())
            },
            "findings_by_severity": dict(by_sev),
        },
        "findings": findings,
    }

    REPORT_PATH.write_text(json.dumps(report, indent=2, default=str), encoding="utf-8")
    print(f"\nReport written to: {REPORT_PATH}")
    print(f"Total findings: {len(findings)}")


if __name__ == "__main__":
    main()
