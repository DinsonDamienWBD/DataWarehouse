package com.datawarehouse.client;

import software.amazon.awssdk.auth.credentials.AwsBasicCredentials;
import software.amazon.awssdk.auth.credentials.StaticCredentialsProvider;
import software.amazon.awssdk.core.sync.RequestBody;
import software.amazon.awssdk.regions.Region;
import software.amazon.awssdk.services.s3.S3Client;
import software.amazon.awssdk.services.s3.S3Configuration;
import software.amazon.awssdk.services.s3.model.*;
import software.amazon.awssdk.services.s3.presigner.S3Presigner;
import software.amazon.awssdk.services.s3.presigner.model.GetObjectPresignRequest;
import software.amazon.awssdk.services.s3.presigner.model.PutObjectPresignRequest;

import java.io.InputStream;
import java.net.URI;
import java.time.Duration;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.Objects;
import java.util.stream.Collectors;

/**
 * S3-compatible client for DataWarehouse.
 *
 * <p>Wraps the AWS SDK for Java v2 {@link S3Client} configured for a custom
 * DataWarehouse S3-compatible endpoint with path-style addressing and static
 * credentials. Provides bucket and object operations, copy, and presigned URL
 * generation.</p>
 *
 * <p>Implements {@link AutoCloseable} to ensure proper cleanup of underlying
 * HTTP connections and signer resources.</p>
 *
 * <h2>Usage Example</h2>
 * <pre>{@code
 * try (var client = new S3CompatClient("http://localhost:9878", "access", "secret", "us-east-1")) {
 *     client.createBucket("my-bucket");
 *     client.putObject("my-bucket", "key.txt",
 *         new ByteArrayInputStream("hello".getBytes()), 5, "text/plain", Map.of());
 *     InputStream data = client.getObject("my-bucket", "key.txt");
 * }
 * }</pre>
 */
public class S3CompatClient implements AutoCloseable {

    private final S3Client s3;
    private final S3Presigner presigner;

    /**
     * Creates a new S3-compatible client targeting a DataWarehouse endpoint.
     *
     * @param endpoint  the HTTP(S) endpoint URL (e.g., "http://localhost:9878")
     * @param accessKey the access key ID for authentication
     * @param secretKey the secret access key for authentication
     * @param region    the AWS region identifier (e.g., "us-east-1")
     * @throws NullPointerException if any argument is null
     */
    public S3CompatClient(String endpoint, String accessKey, String secretKey, String region) {
        Objects.requireNonNull(endpoint, "endpoint must not be null");
        Objects.requireNonNull(accessKey, "accessKey must not be null");
        Objects.requireNonNull(secretKey, "secretKey must not be null");
        Objects.requireNonNull(region, "region must not be null");

        URI endpointUri = URI.create(endpoint);
        StaticCredentialsProvider credentialsProvider = StaticCredentialsProvider.create(
                AwsBasicCredentials.create(accessKey, secretKey));

        S3Configuration s3Config = S3Configuration.builder()
                .pathStyleAccessEnabled(true)
                .build();

        this.s3 = S3Client.builder()
                .endpointOverride(endpointUri)
                .region(Region.of(region))
                .credentialsProvider(credentialsProvider)
                .serviceConfiguration(s3Config)
                .build();

        this.presigner = S3Presigner.builder()
                .endpointOverride(endpointUri)
                .region(Region.of(region))
                .credentialsProvider(credentialsProvider)
                .serviceConfiguration(s3Config)
                .build();
    }

    // ── Bucket Operations ────────────────────────────────────────────────

    /**
     * Lists all buckets accessible with the configured credentials.
     *
     * @return an unmodifiable list of {@link Bucket} objects
     */
    public List<Bucket> listBuckets() {
        ListBucketsResponse response = s3.listBuckets();
        return response.buckets();
    }

    /**
     * Creates a new bucket with the specified name.
     *
     * @param name the bucket name (must be globally unique within the server)
     * @throws S3Exception if the bucket already exists or the name is invalid
     */
    public void createBucket(String name) {
        Objects.requireNonNull(name, "bucket name must not be null");
        s3.createBucket(CreateBucketRequest.builder()
                .bucket(name)
                .build());
    }

    /**
     * Deletes the bucket with the specified name. The bucket must be empty.
     *
     * @param name the bucket name to delete
     * @throws S3Exception if the bucket does not exist or is not empty
     */
    public void deleteBucket(String name) {
        Objects.requireNonNull(name, "bucket name must not be null");
        s3.deleteBucket(DeleteBucketRequest.builder()
                .bucket(name)
                .build());
    }

    /**
     * Checks whether a bucket with the given name exists.
     *
     * @param name the bucket name to check
     * @return {@code true} if the bucket exists and is accessible
     */
    public boolean bucketExists(String name) {
        Objects.requireNonNull(name, "bucket name must not be null");
        try {
            s3.headBucket(HeadBucketRequest.builder()
                    .bucket(name)
                    .build());
            return true;
        } catch (NoSuchBucketException e) {
            return false;
        }
    }

    // ── Object Operations ────────────────────────────────────────────────

    /**
     * Stores an object in the specified bucket.
     *
     * @param bucket        the target bucket name
     * @param key           the object key (path)
     * @param body          the object data stream
     * @param contentLength the exact size of the data in bytes
     * @param contentType   the MIME type (e.g., "application/octet-stream")
     * @param metadata      user-defined metadata key-value pairs (may be empty)
     * @return the ETag (content hash) of the stored object
     * @throws S3Exception if the operation fails
     */
    public String putObject(String bucket, String key, InputStream body, long contentLength,
                            String contentType, Map<String, String> metadata) {
        Objects.requireNonNull(bucket, "bucket must not be null");
        Objects.requireNonNull(key, "key must not be null");
        Objects.requireNonNull(body, "body must not be null");

        PutObjectRequest.Builder builder = PutObjectRequest.builder()
                .bucket(bucket)
                .key(key)
                .contentLength(contentLength);

        if (contentType != null && !contentType.isEmpty()) {
            builder.contentType(contentType);
        }
        if (metadata != null && !metadata.isEmpty()) {
            builder.metadata(metadata);
        }

        PutObjectResponse response = s3.putObject(builder.build(),
                RequestBody.fromInputStream(body, contentLength));
        return response.eTag();
    }

    /**
     * Retrieves an object from the specified bucket as an input stream.
     *
     * <p>The caller is responsible for closing the returned stream.</p>
     *
     * @param bucket the bucket name
     * @param key    the object key
     * @return an {@link InputStream} containing the object data
     * @throws NoSuchKeyException if the object does not exist
     */
    public InputStream getObject(String bucket, String key) {
        Objects.requireNonNull(bucket, "bucket must not be null");
        Objects.requireNonNull(key, "key must not be null");

        return s3.getObject(GetObjectRequest.builder()
                .bucket(bucket)
                .key(key)
                .build());
    }

    /**
     * Deletes an object from the specified bucket.
     *
     * @param bucket the bucket name
     * @param key    the object key to delete
     */
    public void deleteObject(String bucket, String key) {
        Objects.requireNonNull(bucket, "bucket must not be null");
        Objects.requireNonNull(key, "key must not be null");

        s3.deleteObject(DeleteObjectRequest.builder()
                .bucket(bucket)
                .key(key)
                .build());
    }

    /**
     * Retrieves metadata about an object without downloading its body.
     *
     * @param bucket the bucket name
     * @param key    the object key
     * @return an {@link ObjectInfo} containing the object metadata
     * @throws NoSuchKeyException if the object does not exist
     */
    public ObjectInfo headObject(String bucket, String key) {
        Objects.requireNonNull(bucket, "bucket must not be null");
        Objects.requireNonNull(key, "key must not be null");

        HeadObjectResponse response = s3.headObject(HeadObjectRequest.builder()
                .bucket(bucket)
                .key(key)
                .build());

        Map<String, String> meta = response.metadata() != null
                ? new HashMap<>(response.metadata())
                : Map.of();

        return new ObjectInfo(
                key,
                response.contentLength(),
                response.lastModified(),
                response.eTag(),
                response.contentType(),
                meta);
    }

    /**
     * Lists objects in a bucket matching an optional prefix.
     *
     * @param bucket  the bucket name
     * @param prefix  optional key prefix filter (may be null or empty)
     * @param maxKeys maximum number of keys to return (1-1000)
     * @return a list of {@link ObjectInfo} for matching objects
     */
    public List<ObjectInfo> listObjects(String bucket, String prefix, int maxKeys) {
        Objects.requireNonNull(bucket, "bucket must not be null");

        ListObjectsV2Request.Builder builder = ListObjectsV2Request.builder()
                .bucket(bucket)
                .maxKeys(maxKeys);

        if (prefix != null && !prefix.isEmpty()) {
            builder.prefix(prefix);
        }

        ListObjectsV2Response response = s3.listObjectsV2(builder.build());

        return response.contents().stream()
                .map(obj -> ObjectInfo.of(
                        obj.key(),
                        obj.size(),
                        obj.lastModified(),
                        obj.eTag(),
                        "application/octet-stream"))
                .collect(Collectors.toList());
    }

    /**
     * Checks whether an object exists in the specified bucket.
     *
     * @param bucket the bucket name
     * @param key    the object key
     * @return {@code true} if the object exists
     */
    public boolean objectExists(String bucket, String key) {
        try {
            headObject(bucket, key);
            return true;
        } catch (NoSuchKeyException e) {
            return false;
        }
    }

    // ── Copy ─────────────────────────────────────────────────────────────

    /**
     * Copies an object from one location to another within or across buckets.
     *
     * @param srcBucket the source bucket name
     * @param srcKey    the source object key
     * @param dstBucket the destination bucket name
     * @param dstKey    the destination object key
     */
    public void copyObject(String srcBucket, String srcKey, String dstBucket, String dstKey) {
        Objects.requireNonNull(srcBucket, "srcBucket must not be null");
        Objects.requireNonNull(srcKey, "srcKey must not be null");
        Objects.requireNonNull(dstBucket, "dstBucket must not be null");
        Objects.requireNonNull(dstKey, "dstKey must not be null");

        s3.copyObject(CopyObjectRequest.builder()
                .sourceBucket(srcBucket)
                .sourceKey(srcKey)
                .destinationBucket(dstBucket)
                .destinationKey(dstKey)
                .build());
    }

    // ── Presigned URLs ───────────────────────────────────────────────────

    /**
     * Generates a presigned URL for downloading an object.
     *
     * @param bucket the bucket name
     * @param key    the object key
     * @param expiry how long the URL remains valid
     * @return the presigned URL string
     */
    public String presignGet(String bucket, String key, Duration expiry) {
        Objects.requireNonNull(bucket, "bucket must not be null");
        Objects.requireNonNull(key, "key must not be null");
        Objects.requireNonNull(expiry, "expiry must not be null");

        return presigner.presignGetObject(GetObjectPresignRequest.builder()
                .signatureDuration(expiry)
                .getObjectRequest(GetObjectRequest.builder()
                        .bucket(bucket)
                        .key(key)
                        .build())
                .build())
                .url()
                .toString();
    }

    /**
     * Generates a presigned URL for uploading an object.
     *
     * @param bucket the bucket name
     * @param key    the object key
     * @param expiry how long the URL remains valid
     * @return the presigned URL string
     */
    public String presignPut(String bucket, String key, Duration expiry) {
        Objects.requireNonNull(bucket, "bucket must not be null");
        Objects.requireNonNull(key, "key must not be null");
        Objects.requireNonNull(expiry, "expiry must not be null");

        return presigner.presignPutObject(PutObjectPresignRequest.builder()
                .signatureDuration(expiry)
                .putObjectRequest(PutObjectRequest.builder()
                        .bucket(bucket)
                        .key(key)
                        .build())
                .build())
                .url()
                .toString();
    }

    /**
     * Closes the underlying S3 client and presigner, releasing HTTP connections.
     */
    @Override
    public void close() {
        s3.close();
        presigner.close();
    }
}
