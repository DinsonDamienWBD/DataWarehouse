using System;
using DataWarehouse.SDK.VirtualDiskEngine.BlockAllocation;
using Xunit;

namespace DataWarehouse.Tests.Storage.ZeroGravity;

/// <summary>
/// Tests for SIMD-accelerated bitmap scanning.
/// Verifies: FindFirstZeroBit, FindContiguousZeroBits, CountZeroBits
/// across various bitmap states (empty, full, partial, large).
/// </summary>
public sealed class SimdBitmapScannerTests
{
    #region FindFirstZeroBit

    [Fact]
    public void FindFirstZeroBit_AllFree_ReturnsZero()
    {
        // 64 bits, all zero (all free)
        var bitmap = new byte[8];
        long result = SimdBitmapScanner.FindFirstZeroBit(bitmap, 64, 0);
        Assert.Equal(0, result);
    }

    [Fact]
    public void FindFirstZeroBit_AllAllocated_ReturnsNegativeOne()
    {
        // 64 bits, all ones (all allocated)
        var bitmap = new byte[8];
        Array.Fill(bitmap, (byte)0xFF);
        long result = SimdBitmapScanner.FindFirstZeroBit(bitmap, 64, 0);
        Assert.Equal(-1, result);
    }

    [Fact]
    public void FindFirstZeroBit_MidwayFree_ReturnsCorrectIndex()
    {
        // First 100 bits allocated, bit 100 is free
        int totalBits = 128;
        var bitmap = new byte[totalBits / 8];

        // Set first 100 bits (12 full bytes + 4 bits)
        for (int i = 0; i < 12; i++)
            bitmap[i] = 0xFF;
        bitmap[12] = 0x0F; // bits 96-99 set, bits 100-103 free

        long result = SimdBitmapScanner.FindFirstZeroBit(bitmap, totalBits, 0);
        Assert.Equal(100, result);
    }

    [Fact]
    public void FindFirstZeroBit_StartOffset_SkipsPrevious()
    {
        // Free bit at 50, but start scanning from 60
        int totalBits = 128;
        var bitmap = new byte[totalBits / 8];
        Array.Fill(bitmap, (byte)0xFF);

        // Clear bit 50 and bit 70
        bitmap[50 / 8] = (byte)(bitmap[50 / 8] & ~(1 << (50 % 8)));
        bitmap[70 / 8] = (byte)(bitmap[70 / 8] & ~(1 << (70 % 8)));

        long result = SimdBitmapScanner.FindFirstZeroBit(bitmap, totalBits, 60);
        Assert.Equal(70, result);
    }

    [Fact]
    public void FindFirstZeroBit_LargeBitmap_Performance()
    {
        // 10M blocks. First 9,999,999 allocated, last one free.
        long totalBits = 10_000_000;
        int totalBytes = (int)((totalBits + 7) / 8);
        var bitmap = new byte[totalBytes];
        Array.Fill(bitmap, (byte)0xFF);

        // Clear the very last bit within totalBits range
        long lastBit = totalBits - 1;
        bitmap[lastBit / 8] = (byte)(bitmap[lastBit / 8] & ~(1 << (int)(lastBit % 8)));

        long result = SimdBitmapScanner.FindFirstZeroBit(bitmap, totalBits, 0);
        Assert.Equal(lastBit, result);
    }

    #endregion

    #region FindContiguousZeroBits

    [Fact]
    public void FindContiguousZeroBits_ExactMatch_ReturnsStart()
    {
        // Create a bitmap with a gap of exactly 8 contiguous zero bits
        int totalBits = 128;
        var bitmap = new byte[totalBits / 8];
        Array.Fill(bitmap, (byte)0xFF);

        // Clear byte at index 5 (bits 40-47: 8 contiguous free)
        bitmap[5] = 0x00;

        long result = SimdBitmapScanner.FindContiguousZeroBits(bitmap, totalBits, 8);
        Assert.Equal(40, result);
    }

    [Fact]
    public void FindContiguousZeroBits_NoMatch_ReturnsNegativeOne()
    {
        // Fragmented bitmap: alternating allocated/free (no 8-contiguous run)
        int totalBits = 128;
        var bitmap = new byte[totalBits / 8];
        // 0xAA = 10101010 - every other bit set, max contiguous free = 1
        Array.Fill(bitmap, (byte)0xAA);

        long result = SimdBitmapScanner.FindContiguousZeroBits(bitmap, totalBits, 8);
        Assert.Equal(-1, result);
    }

    #endregion

    #region CountZeroBits

    [Fact]
    public void CountZeroBits_MixedBitmap_ReturnsCorrectCount()
    {
        // 128 bits with exactly 50% allocation (64 set, 64 free)
        int totalBits = 128;
        var bitmap = new byte[totalBits / 8];
        // 0xF0 = 11110000: 4 set, 4 free per byte -> 64 set, 64 free in 16 bytes
        Array.Fill(bitmap, (byte)0xF0);

        long zeroBits = SimdBitmapScanner.CountZeroBits(bitmap, totalBits);
        Assert.Equal(64, zeroBits);
    }

    [Fact]
    public void CountZeroBits_AllFree_ReturnsTotal()
    {
        int totalBits = 256;
        var bitmap = new byte[totalBits / 8];
        // All zeros = all free

        long zeroBits = SimdBitmapScanner.CountZeroBits(bitmap, totalBits);
        Assert.Equal(256, zeroBits);
    }

    [Fact]
    public void CountZeroBits_AllAllocated_ReturnsZero()
    {
        int totalBits = 256;
        var bitmap = new byte[totalBits / 8];
        Array.Fill(bitmap, (byte)0xFF);

        long zeroBits = SimdBitmapScanner.CountZeroBits(bitmap, totalBits);
        Assert.Equal(0, zeroBits);
    }

    #endregion
}
