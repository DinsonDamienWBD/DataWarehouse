package com.datawarehouse.client;

import software.amazon.awssdk.services.s3.model.Bucket;

import java.io.ByteArrayInputStream;
import java.io.IOException;
import java.io.InputStream;
import java.time.Duration;
import java.util.List;
import java.util.Map;
import java.util.Objects;

/**
 * High-level DataWarehouse client with native {@code dw://} URI addressing.
 *
 * <p>Provides a simplified API for storing and retrieving data using DataWarehouse's
 * native {@code dw://bucket/key} URI scheme. All operations delegate to the underlying
 * {@link S3CompatClient} which communicates with the DataWarehouse S3-compatible endpoint.</p>
 *
 * <p>Implements {@link AutoCloseable} for proper resource management in try-with-resources blocks.</p>
 *
 * <h2>Usage Example</h2>
 * <pre>{@code
 * try (var dw = new DataWarehouseClient("http://localhost:9878", "access", "secret")) {
 *     dw.createBucket("analytics");
 *     dw.store("dw://analytics/report.csv", csvBytes);
 *     byte[] data = dw.retrieve("dw://analytics/report.csv");
 *     dw.delete("dw://analytics/report.csv");
 * }
 * }</pre>
 *
 * @see S3CompatClient
 * @see DwUri
 */
public class DataWarehouseClient implements AutoCloseable {

    private final S3CompatClient s3;

    /**
     * Creates a new DataWarehouse client with the default region (us-east-1).
     *
     * @param endpoint  the DataWarehouse S3-compatible endpoint URL
     * @param accessKey the access key ID
     * @param secretKey the secret access key
     */
    public DataWarehouseClient(String endpoint, String accessKey, String secretKey) {
        this(endpoint, accessKey, secretKey, "us-east-1");
    }

    /**
     * Creates a new DataWarehouse client with a specified region.
     *
     * @param endpoint  the DataWarehouse S3-compatible endpoint URL
     * @param accessKey the access key ID
     * @param secretKey the secret access key
     * @param region    the region identifier
     */
    public DataWarehouseClient(String endpoint, String accessKey, String secretKey, String region) {
        this.s3 = new S3CompatClient(endpoint, accessKey, secretKey, region);
    }

    // ── dw:// Store Operations ───────────────────────────────────────────

    /**
     * Stores a byte array at the specified {@code dw://} URI.
     *
     * @param dwUri the destination URI (e.g., "dw://bucket/key")
     * @param data  the data to store
     * @throws IllegalArgumentException if the URI is invalid or refers to a bucket only
     */
    public void store(String dwUri, byte[] data) {
        Objects.requireNonNull(data, "data must not be null");
        store(dwUri, new ByteArrayInputStream(data), data.length);
    }

    /**
     * Stores data from an input stream at the specified {@code dw://} URI.
     *
     * @param dwUri         the destination URI (e.g., "dw://bucket/key")
     * @param data          the data stream to store
     * @param contentLength the exact byte length of the data
     * @throws IllegalArgumentException if the URI is invalid or refers to a bucket only
     */
    public void store(String dwUri, InputStream data, long contentLength) {
        DwUri uri = DwUri.parse(dwUri);
        validateObjectUri(uri, dwUri);
        s3.putObject(uri.bucket(), uri.key(), data, contentLength,
                "application/octet-stream", Map.of());
    }

    // ── dw:// Retrieve Operations ────────────────────────────────────────

    /**
     * Retrieves the entire object at the specified {@code dw://} URI as a byte array.
     *
     * @param dwUri the source URI (e.g., "dw://bucket/key")
     * @return the object data as a byte array
     * @throws IllegalArgumentException if the URI is invalid
     * @throws RuntimeException         if an I/O error occurs reading the stream
     */
    public byte[] retrieve(String dwUri) {
        try (InputStream stream = retrieveStream(dwUri)) {
            return stream.readAllBytes();
        } catch (IOException e) {
            throw new RuntimeException("Failed to read object data from " + dwUri, e);
        }
    }

    /**
     * Retrieves the object at the specified {@code dw://} URI as a stream.
     *
     * <p>The caller is responsible for closing the returned stream.</p>
     *
     * @param dwUri the source URI (e.g., "dw://bucket/key")
     * @return an {@link InputStream} containing the object data
     * @throws IllegalArgumentException if the URI is invalid
     */
    public InputStream retrieveStream(String dwUri) {
        DwUri uri = DwUri.parse(dwUri);
        validateObjectUri(uri, dwUri);
        return s3.getObject(uri.bucket(), uri.key());
    }

    // ── dw:// Delete / Exists / List ─────────────────────────────────────

    /**
     * Deletes the object at the specified {@code dw://} URI.
     *
     * @param dwUri the URI of the object to delete
     * @throws IllegalArgumentException if the URI is invalid
     */
    public void delete(String dwUri) {
        DwUri uri = DwUri.parse(dwUri);
        validateObjectUri(uri, dwUri);
        s3.deleteObject(uri.bucket(), uri.key());
    }

    /**
     * Checks whether an object exists at the specified {@code dw://} URI.
     *
     * @param dwUri the URI to check
     * @return {@code true} if the object exists
     * @throws IllegalArgumentException if the URI is invalid
     */
    public boolean exists(String dwUri) {
        DwUri uri = DwUri.parse(dwUri);
        validateObjectUri(uri, dwUri);
        return s3.objectExists(uri.bucket(), uri.key());
    }

    /**
     * Lists objects under the specified {@code dw://} URI prefix.
     *
     * <p>If the URI contains a key, it is used as a prefix filter. If the URI
     * references only a bucket, all objects in the bucket are listed (up to 1000).</p>
     *
     * @param dwUri the URI prefix (e.g., "dw://bucket/folder/")
     * @return a list of {@link ObjectInfo} for matching objects
     */
    public List<ObjectInfo> list(String dwUri) {
        DwUri uri = DwUri.parse(dwUri);
        String prefix = uri.key().isEmpty() ? null : uri.key();
        return s3.listObjects(uri.bucket(), prefix, 1000);
    }

    // ── dw:// Copy ───────────────────────────────────────────────────────

    /**
     * Copies an object from one {@code dw://} URI to another.
     *
     * @param srcUri the source URI
     * @param dstUri the destination URI
     * @throws IllegalArgumentException if either URI is invalid
     */
    public void copy(String srcUri, String dstUri) {
        DwUri src = DwUri.parse(srcUri);
        DwUri dst = DwUri.parse(dstUri);
        validateObjectUri(src, srcUri);
        validateObjectUri(dst, dstUri);
        s3.copyObject(src.bucket(), src.key(), dst.bucket(), dst.key());
    }

    // ── Bucket Management ────────────────────────────────────────────────

    /**
     * Creates a new bucket with the specified name.
     *
     * @param name the bucket name
     */
    public void createBucket(String name) {
        s3.createBucket(name);
    }

    /**
     * Deletes the bucket with the specified name. The bucket must be empty.
     *
     * @param name the bucket name
     */
    public void deleteBucket(String name) {
        s3.deleteBucket(name);
    }

    /**
     * Lists all accessible buckets.
     *
     * @return an unmodifiable list of {@link Bucket} objects
     */
    public List<Bucket> listBuckets() {
        return s3.listBuckets();
    }

    // ── Presigned URLs ───────────────────────────────────────────────────

    /**
     * Generates a presigned URL for downloading the object at the specified {@code dw://} URI.
     *
     * @param dwUri  the object URI
     * @param expiry how long the URL remains valid
     * @return the presigned download URL
     */
    public String presignGet(String dwUri, Duration expiry) {
        DwUri uri = DwUri.parse(dwUri);
        validateObjectUri(uri, dwUri);
        return s3.presignGet(uri.bucket(), uri.key(), expiry);
    }

    /**
     * Generates a presigned URL for uploading to the specified {@code dw://} URI.
     *
     * @param dwUri  the target URI
     * @param expiry how long the URL remains valid
     * @return the presigned upload URL
     */
    public String presignPut(String dwUri, Duration expiry) {
        DwUri uri = DwUri.parse(dwUri);
        validateObjectUri(uri, dwUri);
        return s3.presignPut(uri.bucket(), uri.key(), expiry);
    }

    // ── Accessors ────────────────────────────────────────────────────────

    /**
     * Returns the underlying {@link S3CompatClient} for advanced S3 operations.
     *
     * @return the S3-compatible client instance
     */
    public S3CompatClient s3() {
        return s3;
    }

    /**
     * Closes the underlying S3 client and releases all HTTP connections.
     */
    @Override
    public void close() {
        s3.close();
    }

    // ── Internal Helpers ─────────────────────────────────────────────────

    private static void validateObjectUri(DwUri uri, String original) {
        if (uri.isBucketOnly()) {
            throw new IllegalArgumentException(
                    "URI must include an object key (not bucket-only): " + original);
        }
    }
}
