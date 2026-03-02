using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.VirtualDiskEngine.QoS;

/// <summary>
/// Eight-level Quality-of-Service class stored in the QOS module's 4-byte inode field
/// (Module Overflow Block, Byte 2) when QOS module (bit 34) is active.
/// Maps directly to hardware NVMe SQE ioprio values via io_uring on Linux and IoRing
/// priority hints on Windows 11+.
/// </summary>
/// <remarks>
/// When the QOS module is inactive the I/O scheduler treats every operation as
/// <see cref="Normal"/> (3) and applies default priority for all I/O.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: format-native QoS structures (VOPT-71)")]
public enum QoSClass : byte
{
    /// <summary>Lowest priority — best-effort, actively throttled. Maps to IOPRIO_CLASS_IDLE.</summary>
    Background = 0,

    /// <summary>Runs during idle time only — no active I/O competes with other classes. Maps to IOPRIO_CLASS_IDLE.</summary>
    Idle = 1,

    /// <summary>Below-default priority — tolerates latency spikes. Maps to IOPRIO_CLASS_BE | ioprio_data(7).</summary>
    BelowNormal = 2,

    /// <summary>Default interactive workload priority. Maps to IOPRIO_CLASS_BE | ioprio_data(4).</summary>
    Normal = 3,

    /// <summary>Latency-sensitive workloads (e.g., web serving). Maps to IOPRIO_CLASS_BE | ioprio_data(0).</summary>
    AboveNormal = 4,

    /// <summary>Time-critical operations (e.g., database commits). Maps to IOPRIO_CLASS_RT | ioprio_data(7).</summary>
    High = 5,

    /// <summary>Near-real-time workloads (e.g., OLTP hot path). Maps to IOPRIO_CLASS_RT | ioprio_data(4).</summary>
    Critical = 6,

    /// <summary>Hard real-time — never throttled. Maps to IOPRIO_CLASS_RT | ioprio_data(0).</summary>
    Realtime = 7,
}

/// <summary>
/// Two-bit NVMe hardware priority class stored in extent Flags bits 6-7.
/// Used by the I/O scheduler to set the NVMe SQE submission queue priority for
/// individual extents, providing coarse hardware-level prioritisation that is
/// independent of the per-inode <see cref="QoSClass"/> (which governs IOPS/bandwidth limits).
/// </summary>
/// <remarks>
/// This is NOT the same as <see cref="QoSClass"/>. <see cref="QoSClass"/> is a 3-bit value
/// (8 levels) stored in the QOS module inode field; <see cref="NvmeQoSPriority"/> is a 2-bit
/// value (4 levels) stored in extent flags bits 6-7 and maps directly to NVMe queue priority.
/// Updating a QoS Policy Vault record does not require changing these extent flags.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: per-extent NVMe hardware priority (VOPT-72)")]
public enum NvmeQoSPriority : byte
{
    /// <summary>Lowest NVMe hardware queue priority. Background/archive workloads.</summary>
    Background = 0,

    /// <summary>Default NVMe hardware queue priority for regular workloads.</summary>
    Normal = 1,

    /// <summary>High NVMe hardware queue priority. Latency-sensitive workloads.</summary>
    High = 2,

    /// <summary>Real-time NVMe hardware queue priority. Hard real-time workloads.</summary>
    RealTime = 3,
}
