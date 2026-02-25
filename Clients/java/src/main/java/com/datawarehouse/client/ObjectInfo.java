package com.datawarehouse.client;

import java.time.Instant;
import java.util.Map;

/**
 * Immutable record representing metadata about an S3 object.
 *
 * <p>Used as the return type for HEAD and LIST operations, providing
 * key metadata without transferring the object body.</p>
 *
 * @param key          the object key (path within the bucket)
 * @param size         the object size in bytes
 * @param lastModified when the object was last modified (UTC)
 * @param etag         the entity tag (content hash) of the object
 * @param contentType  the MIME type of the object content
 * @param metadata     user-defined metadata key-value pairs
 */
public record ObjectInfo(
        String key,
        long size,
        Instant lastModified,
        String etag,
        String contentType,
        Map<String, String> metadata) {

    /**
     * Creates an ObjectInfo with no user-defined metadata.
     *
     * @param key          the object key
     * @param size         the object size in bytes
     * @param lastModified when the object was last modified
     * @param etag         the entity tag
     * @param contentType  the MIME type
     * @return a new ObjectInfo instance with an empty metadata map
     */
    public static ObjectInfo of(String key, long size, Instant lastModified,
                                String etag, String contentType) {
        return new ObjectInfo(key, size, lastModified, etag, contentType, Map.of());
    }
}
