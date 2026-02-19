//! High-level DataWarehouse client with `dw://` URI addressing.
//!
//! The [`Client`] translates `dw://bucket/key` URIs into S3-compatible operations
//! against the DataWarehouse UniversalFabric S3 endpoint. It also accepts `s3://`
//! URIs for direct S3 compatibility.
//!
//! # URI Format
//!
//! ```text
//! dw://bucket-name/path/to/object
//! s3://bucket-name/path/to/object
//! ```

use crate::error::DwError;
use crate::s3::{ObjectInfo, S3Client, S3Config};

/// Configuration for the DataWarehouse client.
#[derive(Debug, Clone)]
pub struct ClientConfig {
    /// The S3-compatible HTTP endpoint URL (e.g., `http://localhost:9000`).
    pub endpoint: String,
    /// The access key ID for authentication.
    pub access_key: String,
    /// The secret access key for authentication.
    pub secret_key: String,
    /// The AWS region for request signing (e.g., `us-east-1`).
    pub region: String,
}

/// High-level DataWarehouse client using `dw://` URI addressing.
///
/// All operations accept URIs in `dw://bucket/key` or `s3://bucket/key` format.
/// Under the hood, requests are routed to the DataWarehouse S3-compatible HTTP
/// server provided by the UniversalFabric plugin.
pub struct Client {
    s3: S3Client,
}

impl Client {
    /// Create a new DataWarehouse client connected to the given endpoint.
    pub async fn new(config: ClientConfig) -> Result<Self, DwError> {
        let s3 = S3Client::new(S3Config {
            endpoint: config.endpoint,
            access_key: config.access_key,
            secret_key: config.secret_key,
            region: config.region,
        })
        .await?;
        Ok(Self { s3 })
    }

    /// Store data at a `dw://` address.
    ///
    /// # Arguments
    /// * `dw_uri` - URI in `dw://bucket/key` or `s3://bucket/key` format.
    /// * `data` - The bytes to store.
    ///
    /// # Errors
    /// Returns [`DwError::InvalidUri`] if the URI is malformed.
    pub async fn store(&self, dw_uri: &str, data: Vec<u8>) -> Result<(), DwError> {
        let (bucket, key) = parse_dw_uri(dw_uri)?;
        self.s3.put_object(&bucket, &key, data, None).await?;
        Ok(())
    }

    /// Retrieve data from a `dw://` address.
    ///
    /// Returns the full object body as bytes.
    pub async fn retrieve(&self, dw_uri: &str) -> Result<Vec<u8>, DwError> {
        let (bucket, key) = parse_dw_uri(dw_uri)?;
        self.s3.get_object(&bucket, &key).await
    }

    /// Delete the object at a `dw://` address.
    pub async fn delete(&self, dw_uri: &str) -> Result<(), DwError> {
        let (bucket, key) = parse_dw_uri(dw_uri)?;
        self.s3.delete_object(&bucket, &key).await
    }

    /// Check if an object exists at a `dw://` address.
    pub async fn exists(&self, dw_uri: &str) -> Result<bool, DwError> {
        let (bucket, key) = parse_dw_uri(dw_uri)?;
        self.s3.object_exists(&bucket, &key).await
    }

    /// List objects under a `dw://` prefix.
    ///
    /// The URI is split into bucket and prefix. For example, `dw://my-bucket/logs/`
    /// lists all objects in `my-bucket` with key prefix `logs/`.
    pub async fn list(&self, dw_uri: &str) -> Result<Vec<ObjectInfo>, DwError> {
        let (bucket, key) = parse_dw_uri(dw_uri)?;
        let prefix = if key.is_empty() { None } else { Some(key.as_str()) };
        self.s3.list_objects(&bucket, prefix).await
    }

    /// Copy data between two `dw://` addresses.
    pub async fn copy(&self, src_uri: &str, dst_uri: &str) -> Result<(), DwError> {
        let (src_bucket, src_key) = parse_dw_uri(src_uri)?;
        let (dst_bucket, dst_key) = parse_dw_uri(dst_uri)?;
        self.s3
            .copy_object(&src_bucket, &src_key, &dst_bucket, &dst_key)
            .await
    }

    /// Get the underlying S3 client for advanced operations.
    ///
    /// Use this when you need direct access to bucket operations, presigned URLs,
    /// or other S3-specific functionality not exposed via the `dw://` interface.
    pub fn s3(&self) -> &S3Client {
        &self.s3
    }
}

/// Parse a `dw://` or `s3://` URI into (bucket, key) components.
///
/// # Format
/// ```text
/// dw://bucket-name/path/to/key
/// s3://bucket-name/path/to/key
/// ```
///
/// # Errors
/// Returns [`DwError::InvalidUri`] if the URI does not start with `dw://` or `s3://`,
/// or if the bucket name is empty.
fn parse_dw_uri(uri: &str) -> Result<(String, String), DwError> {
    let stripped = uri
        .strip_prefix("dw://")
        .or_else(|| uri.strip_prefix("s3://"))
        .ok_or_else(|| {
            DwError::InvalidUri(format!(
                "URI must start with dw:// or s3://: {}",
                uri
            ))
        })?;

    let mut parts = stripped.splitn(2, '/');

    let bucket = parts
        .next()
        .filter(|s| !s.is_empty())
        .ok_or_else(|| DwError::InvalidUri("missing bucket name".into()))?
        .to_string();

    let key = parts.next().unwrap_or("").to_string();

    Ok((bucket, key))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_parse_dw_uri_basic() {
        let (bucket, key) = parse_dw_uri("dw://my-bucket/path/to/file.txt").unwrap();
        assert_eq!(bucket, "my-bucket");
        assert_eq!(key, "path/to/file.txt");
    }

    #[test]
    fn test_parse_s3_uri() {
        let (bucket, key) = parse_dw_uri("s3://data/archive/2024.tar.gz").unwrap();
        assert_eq!(bucket, "data");
        assert_eq!(key, "archive/2024.tar.gz");
    }

    #[test]
    fn test_parse_dw_uri_no_key() {
        let (bucket, key) = parse_dw_uri("dw://my-bucket").unwrap();
        assert_eq!(bucket, "my-bucket");
        assert_eq!(key, "");
    }

    #[test]
    fn test_parse_dw_uri_bucket_with_trailing_slash() {
        let (bucket, key) = parse_dw_uri("dw://my-bucket/").unwrap();
        assert_eq!(bucket, "my-bucket");
        assert_eq!(key, "");
    }

    #[test]
    fn test_parse_dw_uri_invalid_scheme() {
        let result = parse_dw_uri("http://my-bucket/key");
        assert!(result.is_err());
    }

    #[test]
    fn test_parse_dw_uri_empty_bucket() {
        let result = parse_dw_uri("dw:///key");
        assert!(result.is_err());
    }

    #[test]
    fn test_parse_dw_uri_nested_path() {
        let (bucket, key) = parse_dw_uri("dw://b/a/b/c/d/e").unwrap();
        assert_eq!(bucket, "b");
        assert_eq!(key, "a/b/c/d/e");
    }
}
