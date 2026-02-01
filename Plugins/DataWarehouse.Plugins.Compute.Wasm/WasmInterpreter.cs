using System.Collections.Concurrent;
using System.Text;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.Plugins.Compute.Wasm;

#region WASM Module Structure

/// <summary>
/// Represents a parsed WASM module ready for execution.
/// </summary>
public class WasmModule
{
    /// <summary>
    /// Function ID this module belongs to.
    /// </summary>
    public string FunctionId { get; init; } = string.Empty;

    /// <summary>
    /// Raw WASM bytes.
    /// </summary>
    public byte[] RawBytes { get; init; } = Array.Empty<byte>();

    /// <summary>
    /// Type section - function signatures.
    /// </summary>
    public IReadOnlyList<WasmFunctionType> Types { get; init; } = Array.Empty<WasmFunctionType>();

    /// <summary>
    /// Import section.
    /// </summary>
    public IReadOnlyList<WasmImport> Imports { get; init; } = Array.Empty<WasmImport>();

    /// <summary>
    /// Function section - type indices.
    /// </summary>
    public IReadOnlyList<int> Functions { get; init; } = Array.Empty<int>();

    /// <summary>
    /// Table section.
    /// </summary>
    public IReadOnlyList<WasmTable> Tables { get; init; } = Array.Empty<WasmTable>();

    /// <summary>
    /// Memory section.
    /// </summary>
    public IReadOnlyList<WasmMemory> Memories { get; init; } = Array.Empty<WasmMemory>();

    /// <summary>
    /// Global section.
    /// </summary>
    public IReadOnlyList<WasmGlobal> Globals { get; init; } = Array.Empty<WasmGlobal>();

    /// <summary>
    /// Export section.
    /// </summary>
    public IReadOnlyList<WasmExport> Exports { get; init; } = Array.Empty<WasmExport>();

    /// <summary>
    /// Start function index (optional).
    /// </summary>
    public int? StartFunction { get; init; }

    /// <summary>
    /// Element section.
    /// </summary>
    public IReadOnlyList<WasmElement> Elements { get; init; } = Array.Empty<WasmElement>();

    /// <summary>
    /// Code section - function bodies.
    /// </summary>
    public IReadOnlyList<WasmFunctionBody> Code { get; init; } = Array.Empty<WasmFunctionBody>();

    /// <summary>
    /// Data section.
    /// </summary>
    public IReadOnlyList<WasmDataSegment> Data { get; init; } = Array.Empty<WasmDataSegment>();
}

/// <summary>
/// WASM function type (signature).
/// </summary>
public class WasmFunctionType
{
    /// <summary>
    /// Parameter types.
    /// </summary>
    public IReadOnlyList<WasmValueType> Parameters { get; init; } = Array.Empty<WasmValueType>();

    /// <summary>
    /// Result types.
    /// </summary>
    public IReadOnlyList<WasmValueType> Results { get; init; } = Array.Empty<WasmValueType>();
}

/// <summary>
/// WASM value types.
/// </summary>
public enum WasmValueType : byte
{
    I32 = 0x7F,
    I64 = 0x7E,
    F32 = 0x7D,
    F64 = 0x7C,
    V128 = 0x7B,
    FuncRef = 0x70,
    ExternRef = 0x6F
}

/// <summary>
/// WASM import entry.
/// </summary>
public class WasmImport
{
    /// <summary>
    /// Module name.
    /// </summary>
    public string Module { get; init; } = string.Empty;

    /// <summary>
    /// Import name.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Import kind.
    /// </summary>
    public WasmImportKind Kind { get; init; }

    /// <summary>
    /// Type index for function imports.
    /// </summary>
    public int TypeIndex { get; init; }
}

/// <summary>
/// WASM import kinds.
/// </summary>
public enum WasmImportKind : byte
{
    Function = 0x00,
    Table = 0x01,
    Memory = 0x02,
    Global = 0x03
}

/// <summary>
/// WASM export entry.
/// </summary>
public class WasmExport
{
    /// <summary>
    /// Export name.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Export kind.
    /// </summary>
    public WasmExportKind Kind { get; init; }

    /// <summary>
    /// Index into respective section.
    /// </summary>
    public int Index { get; init; }
}

/// <summary>
/// WASM export kinds.
/// </summary>
public enum WasmExportKind : byte
{
    Function = 0x00,
    Table = 0x01,
    Memory = 0x02,
    Global = 0x03
}

/// <summary>
/// WASM table definition.
/// </summary>
public class WasmTable
{
    /// <summary>
    /// Element type.
    /// </summary>
    public WasmValueType ElementType { get; init; }

    /// <summary>
    /// Minimum size.
    /// </summary>
    public uint Min { get; init; }

    /// <summary>
    /// Maximum size (optional).
    /// </summary>
    public uint? Max { get; init; }
}

/// <summary>
/// WASM memory definition.
/// </summary>
public class WasmMemory
{
    /// <summary>
    /// Minimum pages.
    /// </summary>
    public uint MinPages { get; init; }

    /// <summary>
    /// Maximum pages (optional).
    /// </summary>
    public uint? MaxPages { get; init; }
}

/// <summary>
/// WASM global variable.
/// </summary>
public class WasmGlobal
{
    /// <summary>
    /// Value type.
    /// </summary>
    public WasmValueType Type { get; init; }

    /// <summary>
    /// Whether the global is mutable.
    /// </summary>
    public bool Mutable { get; init; }

    /// <summary>
    /// Initial value expression.
    /// </summary>
    public byte[] InitExpr { get; init; } = Array.Empty<byte>();
}

/// <summary>
/// WASM element segment.
/// </summary>
public class WasmElement
{
    /// <summary>
    /// Table index.
    /// </summary>
    public int TableIndex { get; init; }

    /// <summary>
    /// Offset expression.
    /// </summary>
    public byte[] OffsetExpr { get; init; } = Array.Empty<byte>();

    /// <summary>
    /// Function indices.
    /// </summary>
    public IReadOnlyList<int> FunctionIndices { get; init; } = Array.Empty<int>();
}

/// <summary>
/// WASM function body (code).
/// </summary>
public class WasmFunctionBody
{
    /// <summary>
    /// Local variable declarations.
    /// </summary>
    public IReadOnlyList<WasmLocal> Locals { get; init; } = Array.Empty<WasmLocal>();

    /// <summary>
    /// Function bytecode.
    /// </summary>
    public byte[] Code { get; init; } = Array.Empty<byte>();
}

/// <summary>
/// Local variable declaration.
/// </summary>
public class WasmLocal
{
    /// <summary>
    /// Number of locals of this type.
    /// </summary>
    public uint Count { get; init; }

    /// <summary>
    /// Value type.
    /// </summary>
    public WasmValueType Type { get; init; }
}

/// <summary>
/// WASM data segment.
/// </summary>
public class WasmDataSegment
{
    /// <summary>
    /// Memory index.
    /// </summary>
    public int MemoryIndex { get; init; }

    /// <summary>
    /// Offset expression.
    /// </summary>
    public byte[] OffsetExpr { get; init; } = Array.Empty<byte>();

    /// <summary>
    /// Data bytes.
    /// </summary>
    public byte[] Data { get; init; } = Array.Empty<byte>();
}

#endregion

#region Module Parser

/// <summary>
/// Result of parsing a WASM module.
/// </summary>
public class WasmParseResult
{
    public IReadOnlyList<WasmFunctionType> Types { get; init; } = Array.Empty<WasmFunctionType>();
    public IReadOnlyList<WasmImport> Imports { get; init; } = Array.Empty<WasmImport>();
    public IReadOnlyList<int> Functions { get; init; } = Array.Empty<int>();
    public IReadOnlyList<WasmTable> Tables { get; init; } = Array.Empty<WasmTable>();
    public IReadOnlyList<WasmMemory> Memories { get; init; } = Array.Empty<WasmMemory>();
    public WasmMemory? MemorySection { get; init; }
    public IReadOnlyList<WasmGlobal> Globals { get; init; } = Array.Empty<WasmGlobal>();
    public IReadOnlyList<WasmExport> Exports { get; init; } = Array.Empty<WasmExport>();
    public int? StartFunction { get; init; }
    public IReadOnlyList<WasmElement> Elements { get; init; } = Array.Empty<WasmElement>();
    public IReadOnlyList<WasmFunctionBody> Code { get; init; } = Array.Empty<WasmFunctionBody>();
    public IReadOnlyList<WasmDataSegment> Data { get; init; } = Array.Empty<WasmDataSegment>();
    public IReadOnlyList<string> RequiredFeatures { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Parses WASM binary format into structured module representation.
/// </summary>
public class WasmModuleParser
{
    private readonly byte[] _bytes;
    private int _position;

    /// <summary>
    /// Creates a new parser for the given WASM bytes.
    /// </summary>
    public WasmModuleParser(byte[] bytes)
    {
        _bytes = bytes ?? throw new ArgumentNullException(nameof(bytes));
        _position = 0;
    }

    /// <summary>
    /// Parses the WASM module.
    /// </summary>
    public WasmParseResult Parse()
    {
        // Skip magic and version (already validated)
        _position = 8;

        var types = new List<WasmFunctionType>();
        var imports = new List<WasmImport>();
        var functions = new List<int>();
        var tables = new List<WasmTable>();
        var memories = new List<WasmMemory>();
        var globals = new List<WasmGlobal>();
        var exports = new List<WasmExport>();
        int? startFunction = null;
        var elements = new List<WasmElement>();
        var code = new List<WasmFunctionBody>();
        var data = new List<WasmDataSegment>();
        var requiredFeatures = new List<string>();

        while (_position < _bytes.Length)
        {
            var sectionId = ReadByte();
            var sectionSize = (int)ReadLeb128();
            var sectionEnd = _position + sectionSize;

            switch (sectionId)
            {
                case 0: // Custom section
                    ParseCustomSection(sectionSize, requiredFeatures);
                    break;
                case 1: // Type section
                    types.AddRange(ParseTypeSection());
                    break;
                case 2: // Import section
                    imports.AddRange(ParseImportSection());
                    break;
                case 3: // Function section
                    functions.AddRange(ParseFunctionSection());
                    break;
                case 4: // Table section
                    tables.AddRange(ParseTableSection());
                    break;
                case 5: // Memory section
                    memories.AddRange(ParseMemorySection());
                    break;
                case 6: // Global section
                    globals.AddRange(ParseGlobalSection());
                    break;
                case 7: // Export section
                    exports.AddRange(ParseExportSection());
                    break;
                case 8: // Start section
                    startFunction = (int)ReadLeb128();
                    break;
                case 9: // Element section
                    elements.AddRange(ParseElementSection());
                    break;
                case 10: // Code section
                    code.AddRange(ParseCodeSection());
                    break;
                case 11: // Data section
                    data.AddRange(ParseDataSection());
                    break;
                default:
                    // Skip unknown sections
                    _position = sectionEnd;
                    break;
            }

            // Ensure we're at the end of the section
            _position = sectionEnd;
        }

        return new WasmParseResult
        {
            Types = types,
            Imports = imports,
            Functions = functions,
            Tables = tables,
            Memories = memories,
            MemorySection = memories.Count > 0 ? memories[0] : null,
            Globals = globals,
            Exports = exports,
            StartFunction = startFunction,
            Elements = elements,
            Code = code,
            Data = data,
            RequiredFeatures = requiredFeatures
        };
    }

    private void ParseCustomSection(int size, List<string> features)
    {
        var sectionEnd = _position + size;
        var name = ReadString();

        // Check for feature detection sections
        if (name == "target_features")
        {
            var count = ReadByte();
            for (int i = 0; i < count && _position < sectionEnd; i++)
            {
                var prefix = ReadByte(); // + or -
                var feature = ReadString();
                if (prefix == '+')
                {
                    features.Add(feature);
                }
            }
        }

        _position = sectionEnd;
    }

    private IEnumerable<WasmFunctionType> ParseTypeSection()
    {
        var count = (int)ReadLeb128();
        for (int i = 0; i < count; i++)
        {
            var form = ReadByte();
            if (form != 0x60)
                throw new InvalidOperationException($"Invalid function type form: 0x{form:X2}");

            var paramCount = (int)ReadLeb128();
            var parameters = new WasmValueType[paramCount];
            for (int j = 0; j < paramCount; j++)
            {
                parameters[j] = (WasmValueType)ReadByte();
            }

            var resultCount = (int)ReadLeb128();
            var results = new WasmValueType[resultCount];
            for (int j = 0; j < resultCount; j++)
            {
                results[j] = (WasmValueType)ReadByte();
            }

            yield return new WasmFunctionType
            {
                Parameters = parameters,
                Results = results
            };
        }
    }

    private IEnumerable<WasmImport> ParseImportSection()
    {
        var count = (int)ReadLeb128();
        for (int i = 0; i < count; i++)
        {
            var module = ReadString();
            var name = ReadString();
            var kind = (WasmImportKind)ReadByte();
            var typeIndex = 0;

            switch (kind)
            {
                case WasmImportKind.Function:
                    typeIndex = (int)ReadLeb128();
                    break;
                case WasmImportKind.Table:
                    ReadByte(); // element type
                    ReadLimits(); // limits
                    break;
                case WasmImportKind.Memory:
                    ReadLimits();
                    break;
                case WasmImportKind.Global:
                    ReadByte(); // value type
                    ReadByte(); // mutability
                    break;
            }

            yield return new WasmImport
            {
                Module = module,
                Name = name,
                Kind = kind,
                TypeIndex = typeIndex
            };
        }
    }

    private IEnumerable<int> ParseFunctionSection()
    {
        var count = (int)ReadLeb128();
        for (int i = 0; i < count; i++)
        {
            yield return (int)ReadLeb128();
        }
    }

    private IEnumerable<WasmTable> ParseTableSection()
    {
        var count = (int)ReadLeb128();
        for (int i = 0; i < count; i++)
        {
            var elementType = (WasmValueType)ReadByte();
            var (min, max) = ReadLimits();

            yield return new WasmTable
            {
                ElementType = elementType,
                Min = min,
                Max = max
            };
        }
    }

    private IEnumerable<WasmMemory> ParseMemorySection()
    {
        var count = (int)ReadLeb128();
        for (int i = 0; i < count; i++)
        {
            var (min, max) = ReadLimits();

            yield return new WasmMemory
            {
                MinPages = min,
                MaxPages = max
            };
        }
    }

    private IEnumerable<WasmGlobal> ParseGlobalSection()
    {
        var count = (int)ReadLeb128();
        for (int i = 0; i < count; i++)
        {
            var type = (WasmValueType)ReadByte();
            var mutable = ReadByte() == 1;
            var initExpr = ReadInitExpr();

            yield return new WasmGlobal
            {
                Type = type,
                Mutable = mutable,
                InitExpr = initExpr
            };
        }
    }

    private IEnumerable<WasmExport> ParseExportSection()
    {
        var count = (int)ReadLeb128();
        for (int i = 0; i < count; i++)
        {
            var name = ReadString();
            var kind = (WasmExportKind)ReadByte();
            var index = (int)ReadLeb128();

            yield return new WasmExport
            {
                Name = name,
                Kind = kind,
                Index = index
            };
        }
    }

    private IEnumerable<WasmElement> ParseElementSection()
    {
        var count = (int)ReadLeb128();
        for (int i = 0; i < count; i++)
        {
            var tableIndex = (int)ReadLeb128();
            var offsetExpr = ReadInitExpr();
            var funcCount = (int)ReadLeb128();
            var funcIndices = new int[funcCount];
            for (int j = 0; j < funcCount; j++)
            {
                funcIndices[j] = (int)ReadLeb128();
            }

            yield return new WasmElement
            {
                TableIndex = tableIndex,
                OffsetExpr = offsetExpr,
                FunctionIndices = funcIndices
            };
        }
    }

    private IEnumerable<WasmFunctionBody> ParseCodeSection()
    {
        var count = (int)ReadLeb128();
        for (int i = 0; i < count; i++)
        {
            var bodySize = (int)ReadLeb128();
            var bodyStart = _position;

            var localCount = (int)ReadLeb128();
            var locals = new WasmLocal[localCount];
            for (int j = 0; j < localCount; j++)
            {
                var n = (uint)ReadLeb128();
                var type = (WasmValueType)ReadByte();
                locals[j] = new WasmLocal { Count = n, Type = type };
            }

            var codeSize = bodySize - (_position - bodyStart);
            var code = new byte[codeSize];
            Array.Copy(_bytes, _position, code, 0, codeSize);
            _position += codeSize;

            yield return new WasmFunctionBody
            {
                Locals = locals,
                Code = code
            };
        }
    }

    private IEnumerable<WasmDataSegment> ParseDataSection()
    {
        var count = (int)ReadLeb128();
        for (int i = 0; i < count; i++)
        {
            var memoryIndex = (int)ReadLeb128();
            var offsetExpr = ReadInitExpr();
            var dataSize = (int)ReadLeb128();
            var data = new byte[dataSize];
            Array.Copy(_bytes, _position, data, 0, dataSize);
            _position += dataSize;

            yield return new WasmDataSegment
            {
                MemoryIndex = memoryIndex,
                OffsetExpr = offsetExpr,
                Data = data
            };
        }
    }

    private byte ReadByte()
    {
        if (_position >= _bytes.Length)
            throw new InvalidOperationException("Unexpected end of WASM binary");
        return _bytes[_position++];
    }

    private ulong ReadLeb128()
    {
        ulong result = 0;
        int shift = 0;
        byte b;
        do
        {
            b = ReadByte();
            result |= ((ulong)(b & 0x7F)) << shift;
            shift += 7;
        } while ((b & 0x80) != 0);
        return result;
    }

    private string ReadString()
    {
        var length = (int)ReadLeb128();
        var str = Encoding.UTF8.GetString(_bytes, _position, length);
        _position += length;
        return str;
    }

    private (uint min, uint? max) ReadLimits()
    {
        var flags = ReadByte();
        var min = (uint)ReadLeb128();
        uint? max = null;
        if ((flags & 0x01) != 0)
        {
            max = (uint)ReadLeb128();
        }
        return (min, max);
    }

    private byte[] ReadInitExpr()
    {
        var start = _position;
        // Read until we hit 0x0B (end)
        while (_position < _bytes.Length && _bytes[_position] != 0x0B)
        {
            _position++;
        }
        _position++; // Skip the 0x0B
        var length = _position - start;
        var expr = new byte[length];
        Array.Copy(_bytes, start, expr, 0, length);
        return expr;
    }
}

#endregion

#region Virtual Machine

/// <summary>
/// Stack-based WASM virtual machine for executing WASM bytecode.
/// </summary>
public class WasmVirtualMachine
{
    private readonly WasmModule _module;
    private readonly WasmSandbox _sandbox;
    private readonly IReadOnlyDictionary<string, string> _environment;
    private readonly Dictionary<(string module, string name), Func<object[], Task<object?>>> _hostFunctions = new();
    private readonly List<string> _logs = new();

    private byte[] _memory;
    private int _memoryPages;
    private readonly Stack<object> _valueStack = new();
    private readonly Stack<CallFrame> _callStack = new();
    private readonly object[] _globals;
    private long _instructionsExecuted;
    private long _peakMemoryUsage;
    private int _heapPointer;

    private const int PageSize = 65536;
    private const int MaxStackSize = 10000;
    private const int MaxCallDepth = 256;

    /// <summary>
    /// Creates a new virtual machine for the given module.
    /// </summary>
    public WasmVirtualMachine(WasmModule module, WasmSandbox sandbox, IReadOnlyDictionary<string, string> environment)
    {
        _module = module ?? throw new ArgumentNullException(nameof(module));
        _sandbox = sandbox ?? throw new ArgumentNullException(nameof(sandbox));
        _environment = environment ?? new Dictionary<string, string>();

        // Initialize memory
        var memoryDef = module.Memories.FirstOrDefault();
        _memoryPages = (int)(memoryDef?.MinPages ?? 1);
        _memory = new byte[_memoryPages * PageSize];
        _heapPointer = 1024; // Start heap after reserved area

        // Initialize data segments
        foreach (var segment in module.Data)
        {
            var offset = EvaluateInitExpr(segment.OffsetExpr);
            Array.Copy(segment.Data, 0, _memory, offset, segment.Data.Length);
        }

        // Initialize globals
        _globals = new object[module.Globals.Count];
        for (int i = 0; i < module.Globals.Count; i++)
        {
            _globals[i] = EvaluateInitExpr(module.Globals[i].InitExpr, module.Globals[i].Type);
        }

        // Register default host functions
        RegisterDefaultHostFunctions();
    }

    /// <summary>
    /// Instructions executed during the current/last execution.
    /// </summary>
    public long InstructionsExecuted => _instructionsExecuted;

    /// <summary>
    /// Peak memory usage in bytes.
    /// </summary>
    public long PeakMemoryUsage => _peakMemoryUsage;

    /// <summary>
    /// Logs generated during execution.
    /// </summary>
    public IReadOnlyList<string> Logs => _logs;

    /// <summary>
    /// Registers a host function callable from WASM.
    /// </summary>
    public void RegisterHostFunction(string module, string name, Func<object[], Task<object?>> function)
    {
        _hostFunctions[(module, name)] = function;
    }

    /// <summary>
    /// Adds a log entry.
    /// </summary>
    public void AddLog(string message)
    {
        _logs.Add($"[{DateTime.UtcNow:HH:mm:ss.fff}] {message}");
    }

    /// <summary>
    /// Allocates memory and returns the pointer.
    /// </summary>
    public int AllocateMemory(int size)
    {
        // Align to 8 bytes
        size = (size + 7) & ~7;

        if (_heapPointer + size > _memory.Length)
        {
            // Try to grow memory
            var pagesNeeded = ((_heapPointer + size - _memory.Length) / PageSize) + 1;
            GrowMemory(pagesNeeded);
        }

        var ptr = _heapPointer;
        _heapPointer += size;
        _peakMemoryUsage = Math.Max(_peakMemoryUsage, _heapPointer);

        return ptr;
    }

    /// <summary>
    /// Writes data to memory at the given address.
    /// </summary>
    public void WriteMemory(int address, byte[] data)
    {
        _sandbox.CheckMemoryAccess(address, data.Length);
        Array.Copy(data, 0, _memory, address, data.Length);
    }

    /// <summary>
    /// Writes an int32 to memory.
    /// </summary>
    public void WriteInt32(int address, int value)
    {
        _sandbox.CheckMemoryAccess(address, 4);
        BitConverter.TryWriteBytes(_memory.AsSpan(address, 4), value);
    }

    /// <summary>
    /// Reads data from memory.
    /// </summary>
    public byte[] ReadMemory(int address, int length)
    {
        _sandbox.CheckMemoryAccess(address, length);
        var data = new byte[length];
        Array.Copy(_memory, address, data, 0, length);
        return data;
    }

    /// <summary>
    /// Reads an int32 from memory.
    /// </summary>
    public int ReadInt32(int address)
    {
        _sandbox.CheckMemoryAccess(address, 4);
        return BitConverter.ToInt32(_memory, address);
    }

    /// <summary>
    /// Reads a string from memory.
    /// </summary>
    public string ReadString(int address, int length)
    {
        var bytes = ReadMemory(address, length);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// Executes a function by name.
    /// </summary>
    public async Task<object?> ExecuteAsync(string functionName, object[] args, CancellationToken ct)
    {
        _instructionsExecuted = 0;
        _valueStack.Clear();
        _callStack.Clear();

        // Find the export
        var export = _module.Exports.FirstOrDefault(e =>
            e.Name == functionName && e.Kind == WasmExportKind.Function);

        if (export == null)
        {
            throw new InvalidOperationException($"Function '{functionName}' not found in exports");
        }

        // Push arguments
        foreach (var arg in args)
        {
            _valueStack.Push(arg);
        }

        // Execute
        return await ExecuteFunctionAsync(export.Index, ct);
    }

    private async Task<object?> ExecuteFunctionAsync(int funcIndex, CancellationToken ct)
    {
        // Adjust for imports
        var importCount = _module.Imports.Count(i => i.Kind == WasmImportKind.Function);

        if (funcIndex < importCount)
        {
            // This is an imported function
            var import = _module.Imports
                .Where(i => i.Kind == WasmImportKind.Function)
                .ElementAt(funcIndex);

            return await CallHostFunctionAsync(import.Module, import.Name);
        }

        var localFuncIndex = funcIndex - importCount;
        if (localFuncIndex >= _module.Code.Count)
        {
            throw new InvalidOperationException($"Function index {funcIndex} out of range");
        }

        var body = _module.Code[localFuncIndex];
        var typeIndex = _module.Functions[localFuncIndex];
        var funcType = _module.Types[typeIndex];

        // Create call frame
        var frame = new CallFrame
        {
            FunctionIndex = funcIndex,
            Locals = new object[funcType.Parameters.Count + body.Locals.Sum(l => (int)l.Count)],
            ReturnArity = funcType.Results.Count
        };

        // Pop parameters into locals
        for (int i = funcType.Parameters.Count - 1; i >= 0; i--)
        {
            frame.Locals[i] = _valueStack.Pop();
        }

        // Initialize remaining locals to zero
        int localIdx = funcType.Parameters.Count;
        foreach (var local in body.Locals)
        {
            for (uint j = 0; j < local.Count; j++)
            {
                frame.Locals[localIdx++] = GetDefaultValue(local.Type);
            }
        }

        _callStack.Push(frame);

        if (_callStack.Count > MaxCallDepth)
        {
            throw new WasmExecutionException("Call stack overflow", WasmExceptionType.StackOverflow);
        }

        // Execute bytecode
        await ExecuteBytecodeAsync(body.Code, ct);

        _callStack.Pop();

        // Return result
        if (funcType.Results.Count > 0 && _valueStack.Count > 0)
        {
            return _valueStack.Pop();
        }

        return null;
    }

    private async Task ExecuteBytecodeAsync(byte[] code, CancellationToken ct)
    {
        int pc = 0;
        var frame = _callStack.Peek();

        while (pc < code.Length - 1) // -1 because last byte should be 0x0B (end)
        {
            ct.ThrowIfCancellationRequested();

            _instructionsExecuted++;
            _sandbox.CheckInstructionLimit(_instructionsExecuted);

            var opcode = code[pc++];

            switch (opcode)
            {
                // Control flow
                case 0x00: // unreachable
                    throw new WasmExecutionException("Unreachable instruction executed", WasmExceptionType.Trap);

                case 0x01: // nop
                    break;

                case 0x02: // block
                    pc = SkipBlockType(code, pc);
                    break;

                case 0x03: // loop
                    pc = SkipBlockType(code, pc);
                    break;

                case 0x04: // if
                    pc = SkipBlockType(code, pc);
                    var cond = PopI32();
                    if (cond == 0)
                    {
                        pc = SkipToElseOrEnd(code, pc);
                    }
                    break;

                case 0x05: // else
                    pc = SkipToEnd(code, pc);
                    break;

                case 0x0B: // end
                    break;

                case 0x0C: // br
                    var depth = ReadLeb128(code, ref pc);
                    // Branch handling would require label stack
                    break;

                case 0x0D: // br_if
                    depth = ReadLeb128(code, ref pc);
                    if (PopI32() != 0)
                    {
                        // Branch
                    }
                    break;

                case 0x0F: // return
                    return;

                case 0x10: // call
                    var funcIdx = (int)ReadLeb128(code, ref pc);
                    await ExecuteFunctionAsync(funcIdx, ct);
                    break;

                case 0x11: // call_indirect
                    var typeIdx = (int)ReadLeb128(code, ref pc);
                    var tableIdx = (int)ReadLeb128(code, ref pc);
                    var elemIdx = PopI32();
                    // Indirect call handling
                    break;

                // Parametric
                case 0x1A: // drop
                    _valueStack.Pop();
                    break;

                case 0x1B: // select
                    var selectCond = PopI32();
                    var val2 = _valueStack.Pop();
                    var val1 = _valueStack.Pop();
                    _valueStack.Push(selectCond != 0 ? val1 : val2);
                    break;

                // Variable access
                case 0x20: // local.get
                    var localIdx = (int)ReadLeb128(code, ref pc);
                    _valueStack.Push(frame.Locals[localIdx]);
                    break;

                case 0x21: // local.set
                    localIdx = (int)ReadLeb128(code, ref pc);
                    frame.Locals[localIdx] = _valueStack.Pop();
                    break;

                case 0x22: // local.tee
                    localIdx = (int)ReadLeb128(code, ref pc);
                    frame.Locals[localIdx] = _valueStack.Peek();
                    break;

                case 0x23: // global.get
                    var globalIdx = (int)ReadLeb128(code, ref pc);
                    _valueStack.Push(_globals[globalIdx]);
                    break;

                case 0x24: // global.set
                    globalIdx = (int)ReadLeb128(code, ref pc);
                    _globals[globalIdx] = _valueStack.Pop();
                    break;

                // Memory
                case 0x28: // i32.load
                    var align = ReadLeb128(code, ref pc);
                    var offset = (int)ReadLeb128(code, ref pc);
                    var addr = PopI32() + offset;
                    _sandbox.CheckMemoryAccess(addr, 4);
                    _valueStack.Push(BitConverter.ToInt32(_memory, addr));
                    break;

                case 0x29: // i64.load
                    align = ReadLeb128(code, ref pc);
                    offset = (int)ReadLeb128(code, ref pc);
                    addr = PopI32() + offset;
                    _sandbox.CheckMemoryAccess(addr, 8);
                    _valueStack.Push(BitConverter.ToInt64(_memory, addr));
                    break;

                case 0x2A: // f32.load
                    align = ReadLeb128(code, ref pc);
                    offset = (int)ReadLeb128(code, ref pc);
                    addr = PopI32() + offset;
                    _sandbox.CheckMemoryAccess(addr, 4);
                    _valueStack.Push(BitConverter.ToSingle(_memory, addr));
                    break;

                case 0x2B: // f64.load
                    align = ReadLeb128(code, ref pc);
                    offset = (int)ReadLeb128(code, ref pc);
                    addr = PopI32() + offset;
                    _sandbox.CheckMemoryAccess(addr, 8);
                    _valueStack.Push(BitConverter.ToDouble(_memory, addr));
                    break;

                case 0x36: // i32.store
                    align = ReadLeb128(code, ref pc);
                    offset = (int)ReadLeb128(code, ref pc);
                    var value = PopI32();
                    addr = PopI32() + offset;
                    _sandbox.CheckMemoryAccess(addr, 4);
                    BitConverter.TryWriteBytes(_memory.AsSpan(addr, 4), value);
                    break;

                case 0x37: // i64.store
                    align = ReadLeb128(code, ref pc);
                    offset = (int)ReadLeb128(code, ref pc);
                    var value64 = PopI64();
                    addr = PopI32() + offset;
                    _sandbox.CheckMemoryAccess(addr, 8);
                    BitConverter.TryWriteBytes(_memory.AsSpan(addr, 8), value64);
                    break;

                case 0x3F: // memory.size
                    ReadLeb128(code, ref pc); // reserved
                    _valueStack.Push(_memoryPages);
                    break;

                case 0x40: // memory.grow
                    ReadLeb128(code, ref pc); // reserved
                    var pagesToGrow = PopI32();
                    var oldPages = _memoryPages;
                    if (GrowMemory(pagesToGrow))
                    {
                        _valueStack.Push(oldPages);
                    }
                    else
                    {
                        _valueStack.Push(-1);
                    }
                    break;

                // Constants
                case 0x41: // i32.const
                    _valueStack.Push((int)ReadSignedLeb128(code, ref pc));
                    break;

                case 0x42: // i64.const
                    _valueStack.Push(ReadSignedLeb128_64(code, ref pc));
                    break;

                case 0x43: // f32.const
                    _valueStack.Push(BitConverter.ToSingle(code, pc));
                    pc += 4;
                    break;

                case 0x44: // f64.const
                    _valueStack.Push(BitConverter.ToDouble(code, pc));
                    pc += 8;
                    break;

                // Comparison operators (i32)
                case 0x45: // i32.eqz
                    _valueStack.Push(PopI32() == 0 ? 1 : 0);
                    break;

                case 0x46: // i32.eq
                    _valueStack.Push(PopI32() == PopI32() ? 1 : 0);
                    break;

                case 0x47: // i32.ne
                    _valueStack.Push(PopI32() != PopI32() ? 1 : 0);
                    break;

                case 0x48: // i32.lt_s
                    var b = PopI32();
                    var a = PopI32();
                    _valueStack.Push(a < b ? 1 : 0);
                    break;

                case 0x49: // i32.lt_u
                    var ub = (uint)PopI32();
                    var ua = (uint)PopI32();
                    _valueStack.Push(ua < ub ? 1 : 0);
                    break;

                case 0x4A: // i32.gt_s
                    b = PopI32();
                    a = PopI32();
                    _valueStack.Push(a > b ? 1 : 0);
                    break;

                case 0x4B: // i32.gt_u
                    ub = (uint)PopI32();
                    ua = (uint)PopI32();
                    _valueStack.Push(ua > ub ? 1 : 0);
                    break;

                case 0x4C: // i32.le_s
                    b = PopI32();
                    a = PopI32();
                    _valueStack.Push(a <= b ? 1 : 0);
                    break;

                case 0x4D: // i32.le_u
                    ub = (uint)PopI32();
                    ua = (uint)PopI32();
                    _valueStack.Push(ua <= ub ? 1 : 0);
                    break;

                case 0x4E: // i32.ge_s
                    b = PopI32();
                    a = PopI32();
                    _valueStack.Push(a >= b ? 1 : 0);
                    break;

                case 0x4F: // i32.ge_u
                    ub = (uint)PopI32();
                    ua = (uint)PopI32();
                    _valueStack.Push(ua >= ub ? 1 : 0);
                    break;

                // Arithmetic operators (i32)
                case 0x6A: // i32.add
                    _valueStack.Push(PopI32() + PopI32());
                    break;

                case 0x6B: // i32.sub
                    b = PopI32();
                    a = PopI32();
                    _valueStack.Push(a - b);
                    break;

                case 0x6C: // i32.mul
                    _valueStack.Push(PopI32() * PopI32());
                    break;

                case 0x6D: // i32.div_s
                    b = PopI32();
                    a = PopI32();
                    if (b == 0) throw new WasmExecutionException("Division by zero", WasmExceptionType.Trap);
                    _valueStack.Push(a / b);
                    break;

                case 0x6E: // i32.div_u
                    ub = (uint)PopI32();
                    ua = (uint)PopI32();
                    if (ub == 0) throw new WasmExecutionException("Division by zero", WasmExceptionType.Trap);
                    _valueStack.Push((int)(ua / ub));
                    break;

                case 0x6F: // i32.rem_s
                    b = PopI32();
                    a = PopI32();
                    if (b == 0) throw new WasmExecutionException("Division by zero", WasmExceptionType.Trap);
                    _valueStack.Push(a % b);
                    break;

                case 0x70: // i32.rem_u
                    ub = (uint)PopI32();
                    ua = (uint)PopI32();
                    if (ub == 0) throw new WasmExecutionException("Division by zero", WasmExceptionType.Trap);
                    _valueStack.Push((int)(ua % ub));
                    break;

                case 0x71: // i32.and
                    _valueStack.Push(PopI32() & PopI32());
                    break;

                case 0x72: // i32.or
                    _valueStack.Push(PopI32() | PopI32());
                    break;

                case 0x73: // i32.xor
                    _valueStack.Push(PopI32() ^ PopI32());
                    break;

                case 0x74: // i32.shl
                    b = PopI32();
                    a = PopI32();
                    _valueStack.Push(a << (b & 31));
                    break;

                case 0x75: // i32.shr_s
                    b = PopI32();
                    a = PopI32();
                    _valueStack.Push(a >> (b & 31));
                    break;

                case 0x76: // i32.shr_u
                    b = PopI32();
                    a = PopI32();
                    _valueStack.Push((int)((uint)a >> (b & 31)));
                    break;

                case 0x77: // i32.rotl
                    b = PopI32();
                    a = PopI32();
                    var shift = b & 31;
                    _valueStack.Push((a << shift) | ((int)((uint)a >> (32 - shift))));
                    break;

                case 0x78: // i32.rotr
                    b = PopI32();
                    a = PopI32();
                    shift = b & 31;
                    _valueStack.Push(((int)((uint)a >> shift)) | (a << (32 - shift)));
                    break;

                // i64 operations (basic)
                case 0x7C: // i64.add
                    _valueStack.Push(PopI64() + PopI64());
                    break;

                case 0x7D: // i64.sub
                    var b64 = PopI64();
                    var a64 = PopI64();
                    _valueStack.Push(a64 - b64);
                    break;

                case 0x7E: // i64.mul
                    _valueStack.Push(PopI64() * PopI64());
                    break;

                // f32 operations (basic)
                case 0x92: // f32.add
                    _valueStack.Push(PopF32() + PopF32());
                    break;

                case 0x93: // f32.sub
                    var bf32 = PopF32();
                    var af32 = PopF32();
                    _valueStack.Push(af32 - bf32);
                    break;

                case 0x94: // f32.mul
                    _valueStack.Push(PopF32() * PopF32());
                    break;

                case 0x95: // f32.div
                    bf32 = PopF32();
                    af32 = PopF32();
                    _valueStack.Push(af32 / bf32);
                    break;

                // f64 operations (basic)
                case 0xA0: // f64.add
                    _valueStack.Push(PopF64() + PopF64());
                    break;

                case 0xA1: // f64.sub
                    var bf64 = PopF64();
                    var af64 = PopF64();
                    _valueStack.Push(af64 - bf64);
                    break;

                case 0xA2: // f64.mul
                    _valueStack.Push(PopF64() * PopF64());
                    break;

                case 0xA3: // f64.div
                    bf64 = PopF64();
                    af64 = PopF64();
                    _valueStack.Push(af64 / bf64);
                    break;

                // Conversions
                case 0xA7: // i32.wrap_i64
                    _valueStack.Push((int)PopI64());
                    break;

                case 0xAC: // i64.extend_i32_s
                    _valueStack.Push((long)PopI32());
                    break;

                case 0xAD: // i64.extend_i32_u
                    _valueStack.Push((long)(uint)PopI32());
                    break;

                default:
                    // Unknown opcode - skip or throw
                    AddLog($"Warning: Unknown opcode 0x{opcode:X2} at PC {pc - 1}");
                    break;
            }

            if (_valueStack.Count > MaxStackSize)
            {
                throw new WasmExecutionException("Value stack overflow", WasmExceptionType.StackOverflow);
            }
        }
    }

    private int PopI32()
    {
        var val = _valueStack.Pop();
        return val switch
        {
            int i => i,
            long l => (int)l,
            float f => (int)f,
            double d => (int)d,
            _ => Convert.ToInt32(val)
        };
    }

    private long PopI64()
    {
        var val = _valueStack.Pop();
        return val switch
        {
            long l => l,
            int i => i,
            float f => (long)f,
            double d => (long)d,
            _ => Convert.ToInt64(val)
        };
    }

    private float PopF32()
    {
        var val = _valueStack.Pop();
        return val switch
        {
            float f => f,
            double d => (float)d,
            int i => i,
            long l => l,
            _ => Convert.ToSingle(val)
        };
    }

    private double PopF64()
    {
        var val = _valueStack.Pop();
        return val switch
        {
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            _ => Convert.ToDouble(val)
        };
    }

    private bool GrowMemory(int pages)
    {
        var newPageCount = _memoryPages + pages;
        var maxPages = _module.Memories.FirstOrDefault()?.MaxPages ?? 256;

        if (newPageCount > maxPages || newPageCount * PageSize > _sandbox.MaxMemoryBytes)
        {
            return false;
        }

        var newMemory = new byte[newPageCount * PageSize];
        Array.Copy(_memory, newMemory, _memory.Length);
        _memory = newMemory;
        _memoryPages = newPageCount;
        _peakMemoryUsage = Math.Max(_peakMemoryUsage, _memory.Length);

        return true;
    }

    private async Task<object?> CallHostFunctionAsync(string module, string name)
    {
        if (_hostFunctions.TryGetValue((module, name), out var func))
        {
            // Pop arguments from stack (need to know arity from import type)
            var args = new object[0]; // Simplified - would need type info
            var result = await func(args);
            if (result != null)
            {
                _valueStack.Push(result);
            }
            return result;
        }

        throw new WasmExecutionException($"Unknown host function: {module}.{name}", WasmExceptionType.Trap);
    }

    private void RegisterDefaultHostFunctions()
    {
        // WASI-like functions
        RegisterHostFunction("wasi_snapshot_preview1", "fd_write", async args =>
        {
            // Simplified fd_write
            return 0;
        });

        RegisterHostFunction("wasi_snapshot_preview1", "proc_exit", async args =>
        {
            return null;
        });

        RegisterHostFunction("env", "abort", async args =>
        {
            throw new WasmExecutionException("WASM abort called", WasmExceptionType.Trap);
        });
    }

    private int EvaluateInitExpr(byte[] expr)
    {
        return EvaluateInitExpr(expr, WasmValueType.I32) switch
        {
            int i => i,
            _ => 0
        };
    }

    private object EvaluateInitExpr(byte[] expr, WasmValueType expectedType)
    {
        if (expr.Length == 0) return GetDefaultValue(expectedType);

        var opcode = expr[0];
        return opcode switch
        {
            0x41 => ReadSignedLeb128FromBytes(expr, 1), // i32.const
            0x42 => ReadSignedLeb128_64FromBytes(expr, 1), // i64.const
            0x43 => BitConverter.ToSingle(expr, 1), // f32.const
            0x44 => BitConverter.ToDouble(expr, 1), // f64.const
            0x23 => _globals[(int)ReadLeb128FromBytes(expr, 1)], // global.get
            _ => GetDefaultValue(expectedType)
        };
    }

    private static object GetDefaultValue(WasmValueType type) => type switch
    {
        WasmValueType.I32 => 0,
        WasmValueType.I64 => 0L,
        WasmValueType.F32 => 0f,
        WasmValueType.F64 => 0d,
        _ => 0
    };

    private static ulong ReadLeb128(byte[] bytes, ref int pos)
    {
        ulong result = 0;
        int shift = 0;
        byte b;
        do
        {
            b = bytes[pos++];
            result |= ((ulong)(b & 0x7F)) << shift;
            shift += 7;
        } while ((b & 0x80) != 0);
        return result;
    }

    private static int ReadSignedLeb128(byte[] bytes, ref int pos)
    {
        int result = 0;
        int shift = 0;
        byte b;
        do
        {
            b = bytes[pos++];
            result |= (b & 0x7F) << shift;
            shift += 7;
        } while ((b & 0x80) != 0);

        if (shift < 32 && (b & 0x40) != 0)
        {
            result |= ~0 << shift;
        }
        return result;
    }

    private static long ReadSignedLeb128_64(byte[] bytes, ref int pos)
    {
        long result = 0;
        int shift = 0;
        byte b;
        do
        {
            b = bytes[pos++];
            result |= ((long)(b & 0x7F)) << shift;
            shift += 7;
        } while ((b & 0x80) != 0);

        if (shift < 64 && (b & 0x40) != 0)
        {
            result |= ~0L << shift;
        }
        return result;
    }

    private static ulong ReadLeb128FromBytes(byte[] bytes, int start)
    {
        var pos = start;
        return ReadLeb128(bytes, ref pos);
    }

    private static int ReadSignedLeb128FromBytes(byte[] bytes, int start)
    {
        var pos = start;
        return ReadSignedLeb128(bytes, ref pos);
    }

    private static long ReadSignedLeb128_64FromBytes(byte[] bytes, int start)
    {
        var pos = start;
        return ReadSignedLeb128_64(bytes, ref pos);
    }

    private static int SkipBlockType(byte[] code, int pc)
    {
        var b = code[pc];
        if (b == 0x40) return pc + 1; // Empty block type
        if ((b & 0x80) == 0) return pc + 1; // Single value type
        return pc + 1; // Simplified - would need full LEB128 for type index
    }

    private static int SkipToElseOrEnd(byte[] code, int pc)
    {
        int depth = 1;
        while (pc < code.Length && depth > 0)
        {
            var op = code[pc++];
            switch (op)
            {
                case 0x02: // block
                case 0x03: // loop
                case 0x04: // if
                    depth++;
                    pc = SkipBlockType(code, pc);
                    break;
                case 0x05: // else
                    if (depth == 1) return pc;
                    break;
                case 0x0B: // end
                    depth--;
                    break;
            }
        }
        return pc;
    }

    private static int SkipToEnd(byte[] code, int pc)
    {
        int depth = 1;
        while (pc < code.Length && depth > 0)
        {
            var op = code[pc++];
            switch (op)
            {
                case 0x02: // block
                case 0x03: // loop
                case 0x04: // if
                    depth++;
                    pc = SkipBlockType(code, pc);
                    break;
                case 0x0B: // end
                    depth--;
                    break;
            }
        }
        return pc;
    }

    private class CallFrame
    {
        public int FunctionIndex { get; init; }
        public object[] Locals { get; init; } = Array.Empty<object>();
        public int ReturnArity { get; init; }
    }
}

#endregion

#region Sandbox

/// <summary>
/// Provides sandboxing and resource limit enforcement for WASM execution.
/// </summary>
public class WasmSandbox
{
    private readonly WasmResourceLimits _limits;
    private readonly IReadOnlyList<string> _allowedPaths;
    private readonly bool _allowNetworkAccess;
    private readonly IKernelContext? _kernelContext;

    /// <summary>
    /// Creates a new sandbox with the specified limits.
    /// </summary>
    public WasmSandbox(
        WasmResourceLimits limits,
        IReadOnlyList<string> allowedPaths,
        bool allowNetworkAccess,
        IKernelContext? kernelContext)
    {
        _limits = limits ?? WasmResourceLimits.Default;
        _allowedPaths = allowedPaths ?? Array.Empty<string>();
        _allowNetworkAccess = allowNetworkAccess;
        _kernelContext = kernelContext;
    }

    /// <summary>
    /// Maximum memory in bytes.
    /// </summary>
    public long MaxMemoryBytes => _limits.MaxMemoryBytes;

    /// <summary>
    /// Checks if the instruction limit has been exceeded.
    /// </summary>
    public void CheckInstructionLimit(long instructionsExecuted)
    {
        if (instructionsExecuted > _limits.MaxInstructions)
        {
            throw new WasmExecutionException(
                $"Instruction limit exceeded: {instructionsExecuted} > {_limits.MaxInstructions}",
                WasmExceptionType.InstructionLimit);
        }
    }

    /// <summary>
    /// Checks if a memory access is valid.
    /// </summary>
    public void CheckMemoryAccess(int address, int size)
    {
        if (address < 0 || address + size > _limits.MaxMemoryBytes)
        {
            throw new WasmExecutionException(
                $"Memory access out of bounds: address {address}, size {size}, limit {_limits.MaxMemoryBytes}",
                WasmExceptionType.MemoryLimit);
        }
    }

    /// <summary>
    /// Checks if a path is allowed for access.
    /// </summary>
    public bool IsPathAllowed(string path)
    {
        if (_allowedPaths.Count == 0) return false;

        foreach (var allowed in _allowedPaths)
        {
            if (allowed == "*") return true;
            if (allowed.EndsWith("/*") && path.StartsWith(allowed[..^2])) return true;
            if (path.Equals(allowed, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// Checks if network access is allowed.
    /// </summary>
    public bool IsNetworkAllowed => _allowNetworkAccess;
}

#endregion

#region Exceptions

/// <summary>
/// Types of WASM execution exceptions.
/// </summary>
public enum WasmExceptionType
{
    /// <summary>
    /// General trap (unreachable, div by zero, etc.).
    /// </summary>
    Trap,

    /// <summary>
    /// Memory limit exceeded.
    /// </summary>
    MemoryLimit,

    /// <summary>
    /// Instruction limit exceeded.
    /// </summary>
    InstructionLimit,

    /// <summary>
    /// Stack overflow.
    /// </summary>
    StackOverflow,

    /// <summary>
    /// Timeout.
    /// </summary>
    Timeout
}

/// <summary>
/// Exception thrown during WASM execution.
/// </summary>
public class WasmExecutionException : Exception
{
    /// <summary>
    /// The type of exception.
    /// </summary>
    public WasmExceptionType Type { get; }

    /// <summary>
    /// Creates a new WASM execution exception.
    /// </summary>
    public WasmExecutionException(string message, WasmExceptionType type)
        : base(message)
    {
        Type = type;
    }

    /// <summary>
    /// Creates a new WASM execution exception with inner exception.
    /// </summary>
    public WasmExecutionException(string message, WasmExceptionType type, Exception innerException)
        : base(message, innerException)
    {
        Type = type;
    }
}

#endregion
