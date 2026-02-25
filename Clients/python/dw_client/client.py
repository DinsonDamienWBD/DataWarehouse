"""High-level DataWarehouse client with dw:// addressing support.

This module provides :class:`DataWarehouseClient`, a user-friendly interface
that translates ``dw://bucket/key`` URIs into S3-compatible operations via
:class:`~dw_client.s3_compat.S3CompatClient`.

Example::

    from dw_client import DataWarehouseClient

    client = DataWarehouseClient(
        endpoint="http://localhost:9000",
        access_key="AKID",
        secret_key="SECRET",
    )

    # Store and retrieve using dw:// URIs
    client.store("dw://my-bucket/data.json", b'{"value": 42}')
    data = client.retrieve("dw://my-bucket/data.json")

    # Upload/download files with automatic multipart
    client.upload_file("dw://my-bucket/archive.tar.gz", "/tmp/archive.tar.gz")
    client.download_file("dw://my-bucket/archive.tar.gz", "/tmp/out.tar.gz")
"""

from __future__ import annotations

import logging
from io import BytesIO
from typing import Dict, List, Optional, Union

from .s3_compat import S3CompatClient

logger = logging.getLogger(__name__)


class DataWarehouseClient:
    """High-level DataWarehouse client with ``dw://`` addressing support.

    Wraps :class:`S3CompatClient` and adds URI parsing so callers can use
    ``dw://bucket/key`` paths instead of separate bucket/key arguments.

    Also accepts ``s3://bucket/key`` and plain ``bucket/key`` formats.

    Args:
        endpoint: The DataWarehouse S3-compatible endpoint URL
            (e.g. ``http://localhost:9000``).
        access_key: AWS-style access key ID.
        secret_key: AWS-style secret access key.
        region: Region for signature computation. Defaults to ``us-east-1``.
        use_ssl: Whether to use HTTPS. Defaults to ``False``.
    """

    def __init__(
        self,
        endpoint: str,
        access_key: str,
        secret_key: str,
        region: str = "us-east-1",
        use_ssl: bool = False,
    ) -> None:
        self._endpoint = endpoint.rstrip("/")
        self._s3 = S3CompatClient(
            endpoint_url=endpoint,
            access_key=access_key,
            secret_key=secret_key,
            region=region,
            use_ssl=use_ssl,
        )

    # ------------------------------------------------------------------
    # Properties
    # ------------------------------------------------------------------

    @property
    def endpoint(self) -> str:
        """The configured endpoint URL."""
        return self._endpoint

    @property
    def s3(self) -> S3CompatClient:
        """Direct access to the underlying S3-compatible client."""
        return self._s3

    # ------------------------------------------------------------------
    # dw:// addressing operations
    # ------------------------------------------------------------------

    def store(
        self,
        dw_uri: str,
        data: Union[bytes, "BytesIO", str],
        content_type: Optional[str] = None,
        metadata: Optional[Dict[str, str]] = None,
    ) -> Dict:
        """Store data at a ``dw://`` address.

        Args:
            dw_uri: Target URI in ``dw://bucket/key`` format.
            data: The data to store. Accepts ``bytes``, ``str`` (UTF-8 encoded),
                or a file-like ``BytesIO`` object.
            content_type: Optional MIME type for the stored object.
            metadata: Optional user-defined metadata key-value pairs.

        Returns:
            A dict containing ``ETag`` and ``VersionId``.

        Example::

            client.store("dw://logs/2024/01/events.json", b'[{"event": "click"}]')
            client.store("dw://docs/readme.txt", "Hello, World!")
        """
        bucket, key = self._parse_dw_uri(dw_uri)

        if isinstance(data, str):
            data = data.encode("utf-8")
        if isinstance(data, bytes):
            data = BytesIO(data)

        result = self._s3.put_object(
            bucket, key, data, content_type=content_type, metadata=metadata,
        )
        logger.info("Stored object at dw://%s/%s", bucket, key)
        return result

    def retrieve(self, dw_uri: str) -> bytes:
        """Retrieve data from a ``dw://`` address.

        Args:
            dw_uri: Source URI in ``dw://bucket/key`` format.

        Returns:
            The object data as bytes.

        Example::

            data = client.retrieve("dw://my-bucket/config.json")
            config = json.loads(data)
        """
        bucket, key = self._parse_dw_uri(dw_uri)
        stream = self._s3.get_object(bucket, key)
        return stream.read()

    def delete(self, dw_uri: str) -> None:
        """Delete an object at a ``dw://`` address.

        Args:
            dw_uri: URI of the object to delete.

        Example::

            client.delete("dw://my-bucket/old-file.txt")
        """
        bucket, key = self._parse_dw_uri(dw_uri)
        self._s3.delete_object(bucket, key)
        logger.info("Deleted object at dw://%s/%s", bucket, key)

    def exists(self, dw_uri: str) -> bool:
        """Check if an object exists at a ``dw://`` address.

        Args:
            dw_uri: URI to check.

        Returns:
            ``True`` if the object exists, ``False`` otherwise.

        Example::

            if client.exists("dw://my-bucket/data.csv"):
                data = client.retrieve("dw://my-bucket/data.csv")
        """
        bucket, key = self._parse_dw_uri(dw_uri)
        return self._s3.object_exists(bucket, key)

    def list(self, dw_uri: str, max_keys: int = 1000) -> List[Dict]:
        """List objects under a ``dw://`` prefix.

        Args:
            dw_uri: URI prefix in ``dw://bucket/prefix`` format.
            max_keys: Maximum number of objects to return. Defaults to 1000.

        Returns:
            A list of dicts with ``Key``, ``Size``, ``LastModified``, ``ETag``.

        Example::

            for obj in client.list("dw://my-bucket/data/2024/"):
                print(f"{obj['Key']}: {obj['Size']} bytes")
        """
        bucket, key = self._parse_dw_uri(dw_uri)
        return list(self._s3.list_objects(bucket, prefix=key, max_keys=max_keys))

    def copy(self, source_uri: str, dest_uri: str) -> Dict:
        """Copy an object between ``dw://`` addresses.

        Args:
            source_uri: Source URI in ``dw://bucket/key`` format.
            dest_uri: Destination URI in ``dw://bucket/key`` format.

        Returns:
            A dict with ``ETag`` and ``LastModified`` of the new object.

        Example::

            client.copy("dw://raw/data.csv", "dw://processed/data.csv")
        """
        src_bucket, src_key = self._parse_dw_uri(source_uri)
        dst_bucket, dst_key = self._parse_dw_uri(dest_uri)
        result = self._s3.copy_object(src_bucket, src_key, dst_bucket, dst_key)
        logger.info(
            "Copied dw://%s/%s -> dw://%s/%s",
            src_bucket, src_key, dst_bucket, dst_key,
        )
        return result

    def upload_file(self, dw_uri: str, file_path: str) -> None:
        """Upload a local file to a ``dw://`` address with automatic multipart.

        Files larger than 100 MiB are automatically uploaded using multipart
        upload for reliability and performance.

        Args:
            dw_uri: Target URI in ``dw://bucket/key`` format.
            file_path: Path to the local file to upload.

        Example::

            client.upload_file("dw://backups/db-dump.sql.gz", "/tmp/db-dump.sql.gz")
        """
        bucket, key = self._parse_dw_uri(dw_uri)
        self._s3.upload_file(bucket, key, file_path)

    def download_file(self, dw_uri: str, file_path: str) -> None:
        """Download from a ``dw://`` address to a local file.

        Args:
            dw_uri: Source URI in ``dw://bucket/key`` format.
            file_path: Local path to write the downloaded data.

        Example::

            client.download_file("dw://backups/db-dump.sql.gz", "/tmp/restore.sql.gz")
        """
        bucket, key = self._parse_dw_uri(dw_uri)
        self._s3.download_file(bucket, key, file_path)

    # ------------------------------------------------------------------
    # Bucket management
    # ------------------------------------------------------------------

    def create_bucket(self, name: str) -> None:
        """Create a new storage bucket.

        Args:
            name: The bucket name to create.

        Example::

            client.create_bucket("analytics-data")
        """
        self._s3.create_bucket(name)

    def delete_bucket(self, name: str) -> None:
        """Delete an empty storage bucket.

        Args:
            name: The bucket name to delete.

        Example::

            client.delete_bucket("old-data")
        """
        self._s3.delete_bucket(name)

    def list_buckets(self) -> List[Dict]:
        """List all accessible buckets.

        Returns:
            A list of dicts with ``Name`` and ``CreationDate`` keys.

        Example::

            for bucket in client.list_buckets():
                print(bucket["Name"])
        """
        return self._s3.list_buckets()

    def bucket_exists(self, name: str) -> bool:
        """Check whether a bucket exists.

        Args:
            name: The bucket name to check.

        Returns:
            ``True`` if the bucket exists, ``False`` otherwise.
        """
        return self._s3.bucket_exists(name)

    # ------------------------------------------------------------------
    # Presigned URLs
    # ------------------------------------------------------------------

    def presign(
        self,
        dw_uri: str,
        method: str = "GET",
        expires_in: int = 3600,
    ) -> str:
        """Generate a presigned URL for a ``dw://`` address.

        Args:
            dw_uri: Target URI in ``dw://bucket/key`` format.
            method: HTTP method -- ``"GET"`` for download, ``"PUT"`` for upload.
            expires_in: URL expiry time in seconds. Defaults to 3600 (1 hour).

        Returns:
            A presigned URL string.

        Example::

            download_url = client.presign("dw://data/report.pdf")
            upload_url = client.presign("dw://uploads/new-file.bin", method="PUT")
        """
        bucket, key = self._parse_dw_uri(dw_uri)
        if method.upper() == "PUT":
            return self._s3.presign_put(bucket, key, expires_in)
        return self._s3.presign_get(bucket, key, expires_in)

    # ------------------------------------------------------------------
    # Object metadata
    # ------------------------------------------------------------------

    def metadata(self, dw_uri: str) -> Dict:
        """Retrieve object metadata from a ``dw://`` address.

        Args:
            dw_uri: URI of the object.

        Returns:
            A dict with ``ContentLength``, ``ContentType``, ``ETag``,
            ``LastModified``, and ``Metadata``.

        Example::

            meta = client.metadata("dw://my-bucket/data.parquet")
            print(f"Size: {meta['ContentLength']} bytes")
        """
        bucket, key = self._parse_dw_uri(dw_uri)
        return self._s3.head_object(bucket, key)

    # ------------------------------------------------------------------
    # URI parsing
    # ------------------------------------------------------------------

    @staticmethod
    def _parse_dw_uri(uri: str) -> tuple:
        """Parse a ``dw://bucket/key`` URI into ``(bucket, key)``.

        Accepts the following formats:
        - ``dw://bucket/key``
        - ``s3://bucket/key``
        - ``bucket/key`` (plain path)

        Args:
            uri: The URI string to parse.

        Returns:
            A ``(bucket, key)`` tuple.

        Raises:
            ValueError: If the URI is invalid or missing a bucket name.
        """
        if uri.startswith("dw://"):
            uri = uri[5:]
        elif uri.startswith("s3://"):
            uri = uri[5:]

        parts = uri.split("/", 1)
        bucket = parts[0]
        key = parts[1] if len(parts) > 1 else ""

        if not bucket:
            raise ValueError(f"Invalid URI: missing bucket name in '{uri}'")

        return bucket, key
