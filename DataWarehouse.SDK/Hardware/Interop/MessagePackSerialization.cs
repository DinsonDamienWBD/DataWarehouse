using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DataWarehouse.SDK.Hardware.Interop;

#region MessagePack Serialization

/// <summary>
/// MessagePack format codes per the specification.
/// </summary>
public static class MessagePackFormatCode
{
    // Positive fixint: 0x00 - 0x7f
    // Negative fixint: 0xe0 - 0xff
    /// <summary>Nil format.</summary>
    public const byte Nil = 0xc0;
    /// <summary>False format.</summary>
    public const byte False = 0xc2;
    /// <summary>True format.</summary>
    public const byte True = 0xc3;
    /// <summary>Bin8 format.</summary>
    public const byte Bin8 = 0xc4;
    /// <summary>Bin16 format.</summary>
    public const byte Bin16 = 0xc5;
    /// <summary>Bin32 format.</summary>
    public const byte Bin32 = 0xc6;
    /// <summary>Ext8 format.</summary>
    public const byte Ext8 = 0xc7;
    /// <summary>Ext16 format.</summary>
    public const byte Ext16 = 0xc8;
    /// <summary>Ext32 format.</summary>
    public const byte Ext32 = 0xc9;
    /// <summary>Float32 format.</summary>
    public const byte Float32 = 0xca;
    /// <summary>Float64 format.</summary>
    public const byte Float64 = 0xcb;
    /// <summary>UInt8 format.</summary>
    public const byte UInt8 = 0xcc;
    /// <summary>UInt16 format.</summary>
    public const byte UInt16 = 0xcd;
    /// <summary>UInt32 format.</summary>
    public const byte UInt32 = 0xce;
    /// <summary>UInt64 format.</summary>
    public const byte UInt64 = 0xcf;
    /// <summary>Int8 format.</summary>
    public const byte Int8 = 0xd0;
    /// <summary>Int16 format.</summary>
    public const byte Int16 = 0xd1;
    /// <summary>Int32 format.</summary>
    public const byte Int32 = 0xd2;
    /// <summary>Int64 format.</summary>
    public const byte Int64 = 0xd3;
    /// <summary>Str8 format.</summary>
    public const byte Str8 = 0xd9;
    /// <summary>Str16 format.</summary>
    public const byte Str16 = 0xda;
    /// <summary>Str32 format.</summary>
    public const byte Str32 = 0xdb;
    /// <summary>Array16 format.</summary>
    public const byte Array16 = 0xdc;
    /// <summary>Array32 format.</summary>
    public const byte Array32 = 0xdd;
    /// <summary>Map16 format.</summary>
    public const byte Map16 = 0xde;
    /// <summary>Map32 format.</summary>
    public const byte Map32 = 0xdf;
}

/// <summary>
/// Extension type codes for custom DataWarehouse types.
/// Uses key-based (not index-based) fields for schema evolution compatibility.
/// </summary>
public static class DataWarehouseExtensionTypes
{
    /// <summary>StorageAddress extension type.</summary>
    public const sbyte StorageAddress = 1;
    /// <summary>ObjectIdentity extension type.</summary>
    public const sbyte ObjectIdentity = 2;
    /// <summary>VectorClock extension type.</summary>
    public const sbyte VectorClock = 3;
    /// <summary>Timestamp with timezone extension type.</summary>
    public const sbyte TimestampTz = 4;
    /// <summary>ObjectVersion extension type.</summary>
    public const sbyte ObjectVersion = 5;
    /// <summary>StorageMetrics extension type.</summary>
    public const sbyte StorageMetrics = 6;
}

/// <summary>
/// High-performance MessagePack writer for binary serialization.
/// Writes MessagePack-formatted data to a buffer with zero-copy support.
/// </summary>
public sealed class MessagePackWriter
{
    private readonly MemoryStream _stream;
    private readonly BinaryWriter _writer;

    /// <summary>Creates a new MessagePack writer.</summary>
    public MessagePackWriter() : this(new MemoryStream()) { }

    /// <summary>Creates a new MessagePack writer with an existing stream.</summary>
    public MessagePackWriter(MemoryStream stream)
    {
        _stream = stream;
        _writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
    }

    /// <summary>Writes nil.</summary>
    public void WriteNil() => _writer.Write(MessagePackFormatCode.Nil);

    /// <summary>Writes a boolean.</summary>
    public void Write(bool value) => _writer.Write(value ? MessagePackFormatCode.True : MessagePackFormatCode.False);

    /// <summary>Writes an integer with optimal encoding.</summary>
    public void Write(long value)
    {
        if (value >= 0)
        {
            if (value <= 0x7f) _writer.Write((byte)value);
            else if (value <= byte.MaxValue) { _writer.Write(MessagePackFormatCode.UInt8); _writer.Write((byte)value); }
            else if (value <= ushort.MaxValue) { _writer.Write(MessagePackFormatCode.UInt16); WriteBigEndian((ushort)value); }
            else if (value <= uint.MaxValue) { _writer.Write(MessagePackFormatCode.UInt32); WriteBigEndian((uint)value); }
            else { _writer.Write(MessagePackFormatCode.UInt64); WriteBigEndian((ulong)value); }
        }
        else
        {
            if (value >= -32) _writer.Write((byte)(0xe0 | (value + 32 + 0x20)));
            else if (value >= sbyte.MinValue) { _writer.Write(MessagePackFormatCode.Int8); _writer.Write((sbyte)value); }
            else if (value >= short.MinValue) { _writer.Write(MessagePackFormatCode.Int16); WriteBigEndian((short)value); }
            else if (value >= int.MinValue) { _writer.Write(MessagePackFormatCode.Int32); WriteBigEndian((int)value); }
            else { _writer.Write(MessagePackFormatCode.Int64); WriteBigEndian(value); }
        }
    }

    /// <summary>Writes a float.</summary>
    public void Write(float value)
    {
        _writer.Write(MessagePackFormatCode.Float32);
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteSingleBigEndian(buf, value);
        _writer.Write(buf);
    }

    /// <summary>Writes a double.</summary>
    public void Write(double value)
    {
        _writer.Write(MessagePackFormatCode.Float64);
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteDoubleBigEndian(buf, value);
        _writer.Write(buf);
    }

    /// <summary>Writes a string (key-based field support).</summary>
    public void Write(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var len = bytes.Length;

        if (len <= 31) _writer.Write((byte)(0xa0 | len));
        else if (len <= byte.MaxValue) { _writer.Write(MessagePackFormatCode.Str8); _writer.Write((byte)len); }
        else if (len <= ushort.MaxValue) { _writer.Write(MessagePackFormatCode.Str16); WriteBigEndian((ushort)len); }
        else { _writer.Write(MessagePackFormatCode.Str32); WriteBigEndian((uint)len); }

        _writer.Write(bytes);
    }

    /// <summary>Writes binary data.</summary>
    public void WriteBinary(byte[] data)
    {
        var len = data.Length;
        if (len <= byte.MaxValue) { _writer.Write(MessagePackFormatCode.Bin8); _writer.Write((byte)len); }
        else if (len <= ushort.MaxValue) { _writer.Write(MessagePackFormatCode.Bin16); WriteBigEndian((ushort)len); }
        else { _writer.Write(MessagePackFormatCode.Bin32); WriteBigEndian((uint)len); }
        _writer.Write(data);
    }

    /// <summary>Writes an array header.</summary>
    public void WriteArrayHeader(int count)
    {
        if (count <= 15) _writer.Write((byte)(0x90 | count));
        else if (count <= ushort.MaxValue) { _writer.Write(MessagePackFormatCode.Array16); WriteBigEndian((ushort)count); }
        else { _writer.Write(MessagePackFormatCode.Array32); WriteBigEndian((uint)count); }
    }

    /// <summary>Writes a map header.</summary>
    public void WriteMapHeader(int count)
    {
        if (count <= 15) _writer.Write((byte)(0x80 | count));
        else if (count <= ushort.MaxValue) { _writer.Write(MessagePackFormatCode.Map16); WriteBigEndian((ushort)count); }
        else { _writer.Write(MessagePackFormatCode.Map32); WriteBigEndian((uint)count); }
    }

    /// <summary>Writes an extension type (for custom DataWarehouse types).</summary>
    public void WriteExtension(sbyte typeCode, byte[] data)
    {
        var len = data.Length;
        if (len <= byte.MaxValue) { _writer.Write(MessagePackFormatCode.Ext8); _writer.Write((byte)len); }
        else if (len <= ushort.MaxValue) { _writer.Write(MessagePackFormatCode.Ext16); WriteBigEndian((ushort)len); }
        else { _writer.Write(MessagePackFormatCode.Ext32); WriteBigEndian((uint)len); }
        _writer.Write((byte)typeCode);
        _writer.Write(data);
    }

    /// <summary>
    /// Writes a StorageAddress as an extension type.
    /// Format: {scheme (str), host (str), port (uint16), path (str)}
    /// </summary>
    public void WriteStorageAddress(string scheme, string host, int port, string path)
    {
        // Encode as a 4-element map with string keys for schema evolution
        var innerWriter = new MessagePackWriter(new MemoryStream());
        innerWriter.WriteMapHeader(4);
        innerWriter.Write("scheme"); innerWriter.Write(scheme);
        innerWriter.Write("host"); innerWriter.Write(host);
        innerWriter.Write("port"); innerWriter.Write((long)port);
        innerWriter.Write("path"); innerWriter.Write(path);

        var innerBytes = innerWriter.ToByteArray();
        WriteExtension(DataWarehouseExtensionTypes.StorageAddress, innerBytes);
    }

    /// <summary>
    /// Writes an ObjectIdentity as an extension type.
    /// Format: {namespace (str), collection (str), key (str), version (int64)}
    /// </summary>
    public void WriteObjectIdentity(string ns, string collection, string key, long version)
    {
        var innerWriter = new MessagePackWriter(new MemoryStream());
        innerWriter.WriteMapHeader(4);
        innerWriter.Write("namespace"); innerWriter.Write(ns);
        innerWriter.Write("collection"); innerWriter.Write(collection);
        innerWriter.Write("key"); innerWriter.Write(key);
        innerWriter.Write("version"); innerWriter.Write(version);

        WriteExtension(DataWarehouseExtensionTypes.ObjectIdentity, innerWriter.ToByteArray());
    }

    /// <summary>Gets the serialized bytes.</summary>
    public byte[] ToByteArray()
    {
        _writer.Flush();
        return _stream.ToArray();
    }

    private void WriteBigEndian(ushort value)
    {
        Span<byte> buf = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(buf, value);
        _writer.Write(buf);
    }

    private void WriteBigEndian(uint value)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buf, value);
        _writer.Write(buf);
    }

    private void WriteBigEndian(ulong value)
    {
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(buf, value);
        _writer.Write(buf);
    }

    private void WriteBigEndian(short value)
    {
        Span<byte> buf = stackalloc byte[2];
        BinaryPrimitives.WriteInt16BigEndian(buf, value);
        _writer.Write(buf);
    }

    private void WriteBigEndian(int value)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buf, value);
        _writer.Write(buf);
    }

    private void WriteBigEndian(long value)
    {
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(buf, value);
        _writer.Write(buf);
    }
}

/// <summary>
/// High-performance MessagePack reader for binary deserialization.
/// Reads MessagePack-formatted data with automatic type detection.
/// </summary>
public sealed class MessagePackReader
{
    private readonly byte[] _data;
    private int _offset;

    /// <summary>Creates a new reader from a byte array.</summary>
    public MessagePackReader(byte[] data)
    {
        _data = data ?? throw new ArgumentNullException(nameof(data));
        _offset = 0;
    }

    /// <summary>Current read position.</summary>
    public int Position => _offset;

    /// <summary>Whether there is more data to read.</summary>
    public bool HasMore => _offset < _data.Length;

    /// <summary>Peeks at the next format code without advancing.</summary>
    public byte PeekFormatCode() => _data[_offset];

    /// <summary>Reads a nil value.</summary>
    public void ReadNil()
    {
        if (_data[_offset++] != MessagePackFormatCode.Nil)
            throw new InvalidOperationException("Expected nil");
    }

    /// <summary>Tries to read nil, returns true if nil was read.</summary>
    public bool TryReadNil()
    {
        if (_data[_offset] == MessagePackFormatCode.Nil) { _offset++; return true; }
        return false;
    }

    /// <summary>Reads a boolean.</summary>
    public bool ReadBoolean()
    {
        var b = _data[_offset++];
        return b switch
        {
            MessagePackFormatCode.True => true,
            MessagePackFormatCode.False => false,
            _ => throw new InvalidOperationException($"Expected boolean, got 0x{b:X2}")
        };
    }

    /// <summary>Reads an integer.</summary>
    public long ReadInt64()
    {
        var b = _data[_offset];
        if (b <= 0x7f) { _offset++; return b; }
        if (b >= 0xe0) { _offset++; return (sbyte)b; }

        _offset++;
        return b switch
        {
            MessagePackFormatCode.UInt8 => _data[_offset++],
            MessagePackFormatCode.UInt16 => ReadBigEndianUInt16(),
            MessagePackFormatCode.UInt32 => ReadBigEndianUInt32(),
            MessagePackFormatCode.UInt64 => (long)ReadBigEndianUInt64(),
            MessagePackFormatCode.Int8 => (sbyte)_data[_offset++],
            MessagePackFormatCode.Int16 => ReadBigEndianInt16(),
            MessagePackFormatCode.Int32 => ReadBigEndianInt32(),
            MessagePackFormatCode.Int64 => ReadBigEndianInt64(),
            _ => throw new InvalidOperationException($"Expected integer, got 0x{b:X2}")
        };
    }

    /// <summary>Reads a string.</summary>
    public string ReadString()
    {
        var b = _data[_offset];
        int len;
        if ((b & 0xe0) == 0xa0) { len = b & 0x1f; _offset++; }
        else
        {
            _offset++;
            len = b switch
            {
                MessagePackFormatCode.Str8 => _data[_offset++],
                MessagePackFormatCode.Str16 => (int)ReadBigEndianUInt16(),
                MessagePackFormatCode.Str32 => (int)ReadBigEndianUInt32(),
                _ => throw new InvalidOperationException($"Expected string, got 0x{b:X2}")
            };
        }

        var s = Encoding.UTF8.GetString(_data, _offset, len);
        _offset += len;
        return s;
    }

    /// <summary>Reads binary data.</summary>
    public byte[] ReadBinary()
    {
        var b = _data[_offset++];
        var len = b switch
        {
            MessagePackFormatCode.Bin8 => (int)_data[_offset++],
            MessagePackFormatCode.Bin16 => (int)ReadBigEndianUInt16(),
            MessagePackFormatCode.Bin32 => (int)ReadBigEndianUInt32(),
            _ => throw new InvalidOperationException($"Expected binary, got 0x{b:X2}")
        };

        var result = new byte[len];
        Array.Copy(_data, _offset, result, 0, len);
        _offset += len;
        return result;
    }

    /// <summary>Reads an array header and returns element count.</summary>
    public int ReadArrayHeader()
    {
        var b = _data[_offset];
        if ((b & 0xf0) == 0x90) { _offset++; return b & 0x0f; }
        _offset++;
        return b switch
        {
            MessagePackFormatCode.Array16 => (int)ReadBigEndianUInt16(),
            MessagePackFormatCode.Array32 => (int)ReadBigEndianUInt32(),
            _ => throw new InvalidOperationException($"Expected array, got 0x{b:X2}")
        };
    }

    /// <summary>Reads a map header and returns entry count.</summary>
    public int ReadMapHeader()
    {
        var b = _data[_offset];
        if ((b & 0xf0) == 0x80) { _offset++; return b & 0x0f; }
        _offset++;
        return b switch
        {
            MessagePackFormatCode.Map16 => (int)ReadBigEndianUInt16(),
            MessagePackFormatCode.Map32 => (int)ReadBigEndianUInt32(),
            _ => throw new InvalidOperationException($"Expected map, got 0x{b:X2}")
        };
    }

    /// <summary>Reads an extension type header.</summary>
    public (sbyte TypeCode, byte[] Data) ReadExtension()
    {
        var b = _data[_offset++];
        var len = b switch
        {
            MessagePackFormatCode.Ext8 => (int)_data[_offset++],
            MessagePackFormatCode.Ext16 => (int)ReadBigEndianUInt16(),
            MessagePackFormatCode.Ext32 => (int)ReadBigEndianUInt32(),
            _ => throw new InvalidOperationException($"Expected ext, got 0x{b:X2}")
        };

        var typeCode = (sbyte)_data[_offset++];
        var data = new byte[len];
        Array.Copy(_data, _offset, data, 0, len);
        _offset += len;
        return (typeCode, data);
    }

    /// <summary>Skips the next value (any type).</summary>
    public void Skip()
    {
        var b = _data[_offset];
        if (b <= 0x7f || b >= 0xe0) { _offset++; return; }
        if ((b & 0xe0) == 0xa0) { var len = b & 0x1f; _offset += 1 + len; return; }
        if ((b & 0xf0) == 0x90) { var count = b & 0x0f; _offset++; for (var i = 0; i < count; i++) Skip(); return; }
        if ((b & 0xf0) == 0x80) { var count = b & 0x0f; _offset++; for (var i = 0; i < count * 2; i++) Skip(); return; }

        // Fall through to format-code handling
        _offset++;
        switch (b)
        {
            case MessagePackFormatCode.Nil:
            case MessagePackFormatCode.True:
            case MessagePackFormatCode.False: break;
            case MessagePackFormatCode.UInt8:
            case MessagePackFormatCode.Int8: _offset++; break;
            case MessagePackFormatCode.UInt16:
            case MessagePackFormatCode.Int16: _offset += 2; break;
            case MessagePackFormatCode.UInt32:
            case MessagePackFormatCode.Int32:
            case MessagePackFormatCode.Float32: _offset += 4; break;
            case MessagePackFormatCode.UInt64:
            case MessagePackFormatCode.Int64:
            case MessagePackFormatCode.Float64: _offset += 8; break;
            case MessagePackFormatCode.Str8:
            case MessagePackFormatCode.Bin8:
                _offset += _data[_offset] + 1; break;
            case MessagePackFormatCode.Str16:
            case MessagePackFormatCode.Bin16:
                var len16 = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(_offset));
                _offset += 2 + len16; break;
            case MessagePackFormatCode.Str32:
            case MessagePackFormatCode.Bin32:
                var len32 = (int)BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(_offset));
                _offset += 4 + len32; break;
            default:
                throw new InvalidOperationException($"Cannot skip unknown format 0x{b:X2}");
        }
    }

    private ushort ReadBigEndianUInt16()
    {
        var val = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(_offset));
        _offset += 2;
        return val;
    }

    private uint ReadBigEndianUInt32()
    {
        var val = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(_offset));
        _offset += 4;
        return val;
    }

    private ulong ReadBigEndianUInt64()
    {
        var val = BinaryPrimitives.ReadUInt64BigEndian(_data.AsSpan(_offset));
        _offset += 8;
        return val;
    }

    private short ReadBigEndianInt16()
    {
        var val = BinaryPrimitives.ReadInt16BigEndian(_data.AsSpan(_offset));
        _offset += 2;
        return val;
    }

    private int ReadBigEndianInt32()
    {
        var val = BinaryPrimitives.ReadInt32BigEndian(_data.AsSpan(_offset));
        _offset += 4;
        return val;
    }

    private long ReadBigEndianInt64()
    {
        var val = BinaryPrimitives.ReadInt64BigEndian(_data.AsSpan(_offset));
        _offset += 8;
        return val;
    }
}

/// <summary>
/// Extension type resolver registry for custom DataWarehouse types.
/// Maps extension type codes to serializer/deserializer pairs.
/// </summary>
public sealed class ExtensionTypeResolver
{
    private readonly ConcurrentDictionary<sbyte, (Func<object, byte[]> Serialize, Func<byte[], object> Deserialize)> _resolvers = new();

    /// <summary>Registers an extension type serializer/deserializer pair.</summary>
    public void Register<T>(sbyte typeCode, Func<T, byte[]> serialize, Func<byte[], T> deserialize) where T : notnull
    {
        _resolvers[typeCode] = (
            obj => serialize((T)obj),
            data => deserialize(data)!
        );
    }

    /// <summary>Serializes an object to extension type bytes.</summary>
    public byte[]? Serialize(sbyte typeCode, object value)
        => _resolvers.TryGetValue(typeCode, out var pair) ? pair.Serialize(value) : null;

    /// <summary>Deserializes extension type bytes to an object.</summary>
    public object? Deserialize(sbyte typeCode, byte[] data)
        => _resolvers.TryGetValue(typeCode, out var pair) ? pair.Deserialize(data) : null;

    /// <summary>Creates a resolver pre-configured with standard DataWarehouse types.</summary>
    public static ExtensionTypeResolver CreateDefault()
    {
        var resolver = new ExtensionTypeResolver();
        // Standard types are registered by the consuming plugin via RegisterStandardTypes()
        return resolver;
    }
}

#endregion
