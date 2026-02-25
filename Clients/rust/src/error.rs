//! Error types for the DataWarehouse Rust client.

use thiserror::Error;

/// Unified error type for all DataWarehouse client operations.
#[derive(Error, Debug)]
pub enum DwError {
    /// An error returned by the S3-compatible backend.
    #[error("S3 error: {0}")]
    S3(String),

    /// The provided URI is malformed or uses an unsupported scheme.
    #[error("Invalid URI: {0}")]
    InvalidUri(String),

    /// The requested storage backend was not found.
    #[error("Backend not found: {0}")]
    BackendNotFound(String),

    /// An I/O error occurred during a streaming operation.
    #[error("IO error: {0}")]
    Io(#[from] std::io::Error),

    /// An error from the underlying AWS SDK.
    #[error("AWS SDK error: {0}")]
    AwsSdk(String),
}
