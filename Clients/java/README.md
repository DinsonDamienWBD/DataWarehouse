# DataWarehouse Java Client

Java client SDK for DataWarehouse with S3-compatible and native `dw://` addressing.

## Requirements

- Java 17+
- Maven 3.8+

## Build

```bash
mvn clean compile
mvn package
```

## Usage

### Native dw:// Client

```java
try (var dw = new DataWarehouseClient("http://localhost:9878", "access-key", "secret-key")) {
    // Create a bucket
    dw.createBucket("my-data");

    // Store data
    dw.store("dw://my-data/reports/q4.csv", csvBytes);

    // Retrieve data
    byte[] data = dw.retrieve("dw://my-data/reports/q4.csv");

    // List objects
    List<ObjectInfo> objects = dw.list("dw://my-data/reports/");

    // Check existence
    boolean exists = dw.exists("dw://my-data/reports/q4.csv");

    // Copy
    dw.copy("dw://my-data/reports/q4.csv", "dw://archive/reports/q4.csv");

    // Delete
    dw.delete("dw://my-data/reports/q4.csv");

    // Presigned URLs
    String downloadUrl = dw.presignGet("dw://my-data/file.bin", Duration.ofHours(1));
}
```

### S3-Compatible Client

```java
try (var s3 = new S3CompatClient("http://localhost:9878", "access-key", "secret-key", "us-east-1")) {
    s3.createBucket("my-bucket");
    s3.putObject("my-bucket", "key.txt", inputStream, length, "text/plain", Map.of());
    InputStream data = s3.getObject("my-bucket", "key.txt");
    s3.deleteObject("my-bucket", "key.txt");
}
```

### URI Schemes

Both `dw://` and `s3://` schemes are supported:

```java
DwUri uri1 = DwUri.parse("dw://bucket/path/to/object");
DwUri uri2 = DwUri.parse("s3://bucket/path/to/object");
```

## Dependencies

- AWS SDK for Java v2 (S3 client, S3 presigner, S3 transfer manager)
