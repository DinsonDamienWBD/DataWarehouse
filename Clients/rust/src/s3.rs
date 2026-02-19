//! S3-compatible client for DataWarehouse.
//!
//! Wraps the AWS SDK for S3 to communicate with DataWarehouse's S3-compatible
//! HTTP endpoint (provided by `S3HttpServer` in the UniversalFabric plugin).

use aws_sdk_s3::config::Builder as S3ConfigBuilder;
use aws_sdk_s3::config::{BehaviorVersion, Credentials, Region};
use aws_sdk_s3::primitives::ByteStream;
use aws_sdk_s3::Client as AwsS3Client;

use crate::error::DwError;

/// Configuration for connecting to a DataWarehouse S3-compatible endpoint.
#[derive(Debug, Clone)]
pub struct S3Config {
    /// The HTTP endpoint URL (e.g., `http://localhost:9000`).
    pub endpoint: String,
    /// The access key ID for authentication.
    pub access_key: String,
    /// The secret access key for authentication.
    pub secret_key: String,
    /// The AWS region (e.g., `us-east-1`). Used for signing requests.
    pub region: String,
}

/// Metadata about a stored object.
#[derive(Debug, Clone)]
pub struct ObjectInfo {
    /// The object key (path within the bucket).
    pub key: String,
    /// The object size in bytes.
    pub size: i64,
    /// When the object was last modified (ISO 8601 string).
    pub last_modified: Option<String>,
    /// The entity tag (hash) of the object content.
    pub etag: Option<String>,
    /// The MIME content type of the object.
    pub content_type: Option<String>,
}

/// Low-level S3-compatible client for DataWarehouse.
///
/// Provides bucket and object operations against the DataWarehouse S3 HTTP server.
/// Uses `aws-sdk-s3` under the hood with path-style addressing and a custom endpoint.
pub struct S3Client {
    inner: AwsS3Client,
}

impl S3Client {
    /// Create a new S3 client connected to the given DataWarehouse endpoint.
    ///
    /// The client is configured for path-style access (required for custom S3
    /// endpoints) and uses the provided credentials for request signing.
    pub async fn new(config: S3Config) -> Result<Self, DwError> {
        let credentials = Credentials::new(
            &config.access_key,
            &config.secret_key,
            None, // session token
            None, // expiry
            "dw-client",
        );

        let s3_config = S3ConfigBuilder::new()
            .behavior_version(BehaviorVersion::latest())
            .endpoint_url(&config.endpoint)
            .region(Region::new(config.region))
            .credentials_provider(credentials)
            .force_path_style(true)
            .build();

        let client = AwsS3Client::from_conf(s3_config);
        Ok(Self { inner: client })
    }

    // ── Bucket Operations ───────────────────────────────────────────────

    /// List all buckets accessible with the current credentials.
    pub async fn list_buckets(&self) -> Result<Vec<String>, DwError> {
        let resp = self
            .inner
            .list_buckets()
            .send()
            .await
            .map_err(|e| DwError::S3(e.to_string()))?;

        let names = resp
            .buckets()
            .iter()
            .filter_map(|b| b.name().map(|n| n.to_string()))
            .collect();

        Ok(names)
    }

    /// Create a new bucket with the given name.
    pub async fn create_bucket(&self, name: &str) -> Result<(), DwError> {
        self.inner
            .create_bucket()
            .bucket(name)
            .send()
            .await
            .map_err(|e| DwError::S3(e.to_string()))?;

        Ok(())
    }

    /// Delete an empty bucket.
    pub async fn delete_bucket(&self, name: &str) -> Result<(), DwError> {
        self.inner
            .delete_bucket()
            .bucket(name)
            .send()
            .await
            .map_err(|e| DwError::S3(e.to_string()))?;

        Ok(())
    }

    // ── Object Operations ───────────────────────────────────────────────

    /// Upload an object to a bucket.
    ///
    /// Returns the ETag of the stored object.
    pub async fn put_object(
        &self,
        bucket: &str,
        key: &str,
        body: Vec<u8>,
        content_type: Option<&str>,
    ) -> Result<String, DwError> {
        let mut req = self
            .inner
            .put_object()
            .bucket(bucket)
            .key(key)
            .body(ByteStream::from(body));

        if let Some(ct) = content_type {
            req = req.content_type(ct);
        }

        let resp = req.send().await.map_err(|e| DwError::S3(e.to_string()))?;

        Ok(resp.e_tag().unwrap_or("").to_string())
    }

    /// Download an object from a bucket.
    ///
    /// Returns the full object body as bytes.
    pub async fn get_object(&self, bucket: &str, key: &str) -> Result<Vec<u8>, DwError> {
        let resp = self
            .inner
            .get_object()
            .bucket(bucket)
            .key(key)
            .send()
            .await
            .map_err(|e| DwError::S3(e.to_string()))?;

        let bytes = resp
            .body
            .collect()
            .await
            .map_err(|e| DwError::S3(e.to_string()))?
            .into_bytes();

        Ok(bytes.to_vec())
    }

    /// Delete an object from a bucket.
    pub async fn delete_object(&self, bucket: &str, key: &str) -> Result<(), DwError> {
        self.inner
            .delete_object()
            .bucket(bucket)
            .key(key)
            .send()
            .await
            .map_err(|e| DwError::S3(e.to_string()))?;

        Ok(())
    }

    /// Retrieve metadata for an object without downloading the body (HEAD).
    pub async fn head_object(&self, bucket: &str, key: &str) -> Result<ObjectInfo, DwError> {
        let resp = self
            .inner
            .head_object()
            .bucket(bucket)
            .key(key)
            .send()
            .await
            .map_err(|e| DwError::S3(e.to_string()))?;

        Ok(ObjectInfo {
            key: key.to_string(),
            size: resp.content_length().unwrap_or(0),
            last_modified: resp.last_modified().map(|dt| dt.to_string()),
            etag: resp.e_tag().map(|s| s.to_string()),
            content_type: resp.content_type().map(|s| s.to_string()),
        })
    }

    /// List objects in a bucket, optionally filtered by key prefix.
    pub async fn list_objects(
        &self,
        bucket: &str,
        prefix: Option<&str>,
    ) -> Result<Vec<ObjectInfo>, DwError> {
        let mut req = self.inner.list_objects_v2().bucket(bucket);

        if let Some(p) = prefix {
            req = req.prefix(p);
        }

        let resp = req.send().await.map_err(|e| DwError::S3(e.to_string()))?;

        let objects = resp
            .contents()
            .iter()
            .map(|obj| ObjectInfo {
                key: obj.key().unwrap_or("").to_string(),
                size: obj.size().unwrap_or(0),
                last_modified: obj.last_modified().map(|dt| dt.to_string()),
                etag: obj.e_tag().map(|s| s.to_string()),
                content_type: None,
            })
            .collect();

        Ok(objects)
    }

    /// Check whether an object exists in a bucket (via HEAD).
    pub async fn object_exists(&self, bucket: &str, key: &str) -> Result<bool, DwError> {
        match self.head_object(bucket, key).await {
            Ok(_) => Ok(true),
            Err(DwError::S3(msg)) if msg.contains("NotFound") || msg.contains("404") => Ok(false),
            Err(e) => Err(e),
        }
    }

    // ── Copy ────────────────────────────────────────────────────────────

    /// Copy an object from one location to another.
    pub async fn copy_object(
        &self,
        src_bucket: &str,
        src_key: &str,
        dst_bucket: &str,
        dst_key: &str,
    ) -> Result<(), DwError> {
        let copy_source = format!("{}/{}", src_bucket, src_key);

        self.inner
            .copy_object()
            .copy_source(&copy_source)
            .bucket(dst_bucket)
            .key(dst_key)
            .send()
            .await
            .map_err(|e| DwError::S3(e.to_string()))?;

        Ok(())
    }

    // ── Presigned URLs ──────────────────────────────────────────────────

    /// Generate a presigned GET URL for downloading an object.
    ///
    /// The URL is valid for the specified number of seconds.
    pub async fn presign_get(
        &self,
        bucket: &str,
        key: &str,
        expires_secs: u64,
    ) -> Result<String, DwError> {
        use aws_sdk_s3::presigning::PresigningConfig;
        use std::time::Duration;

        let presigning_config = PresigningConfig::builder()
            .expires_in(Duration::from_secs(expires_secs))
            .build()
            .map_err(|e| DwError::S3(e.to_string()))?;

        let presigned = self
            .inner
            .get_object()
            .bucket(bucket)
            .key(key)
            .presigned(presigning_config)
            .await
            .map_err(|e| DwError::S3(e.to_string()))?;

        Ok(presigned.uri().to_string())
    }
}
