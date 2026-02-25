# DataWarehouse Python Client

Python SDK for DataWarehouse Universal Storage, providing both S3-compatible
and native `dw://` addressing interfaces.

## Installation

```bash
pip install -e .
```

Or from the repository root:

```bash
pip install -e Clients/python/
```

## Quick Start

### Using dw:// Addressing (Recommended)

```python
from dw_client import DataWarehouseClient

client = DataWarehouseClient(
    endpoint="http://localhost:9000",
    access_key="my-access-key",
    secret_key="my-secret-key",
)

# Create a bucket
client.create_bucket("my-data")

# Store data using dw:// URIs
client.store("dw://my-data/sensors/reading.json", b'{"temp": 22.5}')

# Retrieve data
data = client.retrieve("dw://my-data/sensors/reading.json")
print(data)  # b'{"temp": 22.5}'

# List objects
for obj in client.list("dw://my-data/sensors/"):
    print(obj["Key"], obj["Size"])

# Upload/download files with automatic multipart
client.upload_file("dw://my-data/large-file.bin", "/tmp/large-file.bin")
client.download_file("dw://my-data/large-file.bin", "/tmp/downloaded.bin")

# Generate presigned URLs
url = client.presign("dw://my-data/sensors/reading.json", expires_in=3600)
```

### Using S3-Compatible Client Directly

```python
from io import BytesIO
from dw_client import S3CompatClient

s3 = S3CompatClient(
    endpoint_url="http://localhost:9000",
    access_key="AKID",
    secret_key="SECRET",
)

# Standard S3 operations
s3.create_bucket("raw-data")
s3.put_object("raw-data", "file.txt", BytesIO(b"content"))
stream = s3.get_object("raw-data", "file.txt")
print(stream.read())

# Check existence
print(s3.object_exists("raw-data", "file.txt"))  # True
print(s3.bucket_exists("raw-data"))  # True

# Copy objects
s3.copy_object("raw-data", "file.txt", "raw-data", "backup/file.txt")
```

## URI Formats

The `DataWarehouseClient` accepts both `dw://` and `s3://` URI formats:

- `dw://bucket/key` -- DataWarehouse native addressing
- `s3://bucket/key` -- S3-compatible addressing
- `bucket/key` -- Plain path (bucket name extracted from first segment)

## Requirements

- Python >= 3.9
- boto3 >= 1.26.0
- requests >= 2.28.0

## Architecture

The Python client communicates with DataWarehouse's S3-compatible HTTP
endpoint (`S3HttpServer` in the UniversalFabric plugin) using standard
AWS Signature V4 authentication via boto3.

```
Python Client (boto3) --> S3 HTTP Protocol --> S3HttpServer --> Storage Fabric
```
