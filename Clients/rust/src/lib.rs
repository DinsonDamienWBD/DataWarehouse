//! # dw-client
//!
//! DataWarehouse Universal Storage Client for Rust.
//!
//! Provides both a native `dw://` URI-based client and a low-level S3-compatible
//! client for interacting with DataWarehouse's S3-compatible HTTP API (powered by
//! the UniversalFabric plugin's `S3HttpServer`).
//!
//! # Quick Start
//!
//! ```no_run
//! use dw_client::{Client, ClientConfig};
//!
//! #[tokio::main]
//! async fn main() -> Result<(), dw_client::DwError> {
//!     let client = Client::new(ClientConfig {
//!         endpoint: "http://localhost:9000".into(),
//!         access_key: "admin".into(),
//!         secret_key: "password".into(),
//!         region: "us-east-1".into(),
//!     }).await?;
//!
//!     client.store("dw://my-bucket/hello.txt", b"Hello, DataWarehouse!".to_vec()).await?;
//!     let data = client.retrieve("dw://my-bucket/hello.txt").await?;
//!     assert_eq!(data, b"Hello, DataWarehouse!");
//!     Ok(())
//! }
//! ```

pub mod client;
pub mod s3;
mod error;

pub use client::{Client, ClientConfig};
pub use s3::{S3Client, S3Config, ObjectInfo};
pub use error::DwError;
