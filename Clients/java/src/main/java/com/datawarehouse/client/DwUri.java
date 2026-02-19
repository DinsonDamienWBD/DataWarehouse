package com.datawarehouse.client;

import java.util.Objects;

/**
 * Parses and represents a DataWarehouse URI in the form {@code dw://bucket/key} or {@code s3://bucket/key}.
 *
 * <p>The URI scheme identifies the addressing namespace:</p>
 * <ul>
 *   <li>{@code dw://} -- native DataWarehouse addressing</li>
 *   <li>{@code s3://} -- S3-compatible addressing (alias for interoperability)</li>
 * </ul>
 *
 * <h2>URI Format</h2>
 * <pre>
 *   dw://bucket-name/path/to/object
 *   s3://bucket-name/path/to/object
 *   dw://bucket-name              (bucket only, empty key)
 * </pre>
 *
 * <h2>Usage Example</h2>
 * <pre>{@code
 * DwUri uri = DwUri.parse("dw://my-bucket/folder/file.txt");
 * uri.bucket(); // "my-bucket"
 * uri.key();    // "folder/file.txt"
 * }</pre>
 *
 * @param bucket the bucket name (never null or empty)
 * @param key    the object key path (may be empty for bucket-only URIs)
 */
public record DwUri(String bucket, String key) {

    /**
     * Validates that the bucket name is never null or empty.
     */
    public DwUri {
        Objects.requireNonNull(bucket, "bucket must not be null");
        Objects.requireNonNull(key, "key must not be null");
        if (bucket.isEmpty()) {
            throw new IllegalArgumentException("bucket name must not be empty");
        }
    }

    /**
     * Parses a {@code dw://} or {@code s3://} URI string into its bucket and key components.
     *
     * @param uri the URI string to parse (must start with "dw://" or "s3://")
     * @return a new {@link DwUri} with the parsed bucket and key
     * @throws IllegalArgumentException if the URI scheme is invalid or the bucket is missing
     * @throws NullPointerException     if uri is null
     */
    public static DwUri parse(String uri) {
        Objects.requireNonNull(uri, "uri must not be null");

        String cleaned;
        if (uri.startsWith("dw://")) {
            cleaned = uri.substring(5);
        } else if (uri.startsWith("s3://")) {
            cleaned = uri.substring(5);
        } else {
            throw new IllegalArgumentException("URI must start with dw:// or s3://: " + uri);
        }

        // Remove trailing slash for bucket-only URIs
        if (cleaned.endsWith("/") && cleaned.indexOf('/') == cleaned.length() - 1) {
            cleaned = cleaned.substring(0, cleaned.length() - 1);
        }

        int slashIdx = cleaned.indexOf('/');
        if (slashIdx < 0) {
            // Bucket-only URI
            if (cleaned.isEmpty()) {
                throw new IllegalArgumentException("Missing bucket name: " + uri);
            }
            return new DwUri(cleaned, "");
        }

        String bucket = cleaned.substring(0, slashIdx);
        String key = cleaned.substring(slashIdx + 1);

        if (bucket.isEmpty()) {
            throw new IllegalArgumentException("Missing bucket name: " + uri);
        }

        return new DwUri(bucket, key);
    }

    /**
     * Returns true if this URI refers to a bucket only (no object key).
     *
     * @return {@code true} if the key is empty
     */
    public boolean isBucketOnly() {
        return key.isEmpty();
    }

    /**
     * Returns the canonical {@code dw://} string representation of this URI.
     *
     * @return the URI in {@code dw://bucket/key} format
     */
    @Override
    public String toString() {
        if (key.isEmpty()) {
            return "dw://" + bucket;
        }
        return "dw://" + bucket + "/" + key;
    }
}
