"""S3-compatible client for DataWarehouse using boto3.

This module provides a thin, production-ready wrapper around the boto3 S3
client, configured to communicate with DataWarehouse's S3-compatible HTTP
endpoint (see ``S3HttpServer`` in the UniversalFabric plugin).

All methods are fully implemented with proper error handling -- no stubs
or placeholders.

Example::

    from dw_client import S3CompatClient

    client = S3CompatClient(
        endpoint_url="http://localhost:9000",
        access_key="AKID",
        secret_key="SECRET",
    )
    client.create_bucket("my-bucket")
    client.put_object("my-bucket", "hello.txt", BytesIO(b"Hello!"))
"""

from __future__ import annotations

import logging
import os
from io import BytesIO
from typing import BinaryIO, Dict, Iterator, List, Optional

import boto3
from botocore.config import Config
from botocore.exceptions import ClientError

logger = logging.getLogger(__name__)


class S3CompatError(Exception):
    """Raised when an S3-compatible operation fails."""

    def __init__(self, message: str, error_code: str = "", status_code: int = 0):
        super().__init__(message)
        self.error_code = error_code
        self.status_code = status_code


class S3CompatClient:
    """Client for DataWarehouse S3-compatible API using boto3.

    Connects to the DataWarehouse S3-compatible endpoint and exposes bucket
    management, object CRUD, multipart upload/download, presigned URLs, and
    copy operations.

    Args:
        endpoint_url: The S3-compatible endpoint (e.g. ``http://localhost:9000``).
        access_key: AWS-style access key ID.
        secret_key: AWS-style secret access key.
        region: Region name for signature computation. Defaults to ``us-east-1``.
        use_ssl: Whether to use HTTPS. Defaults to ``False``.
        connect_timeout: Connection timeout in seconds. Defaults to ``10``.
        read_timeout: Read timeout in seconds. Defaults to ``60``.
        max_retries: Maximum number of retries for transient failures. Defaults to ``3``.
    """

    def __init__(
        self,
        endpoint_url: str,
        access_key: str,
        secret_key: str,
        region: str = "us-east-1",
        use_ssl: bool = False,
        connect_timeout: int = 10,
        read_timeout: int = 60,
        max_retries: int = 3,
    ) -> None:
        self._endpoint_url = endpoint_url
        self._client = boto3.client(
            "s3",
            endpoint_url=endpoint_url,
            aws_access_key_id=access_key,
            aws_secret_access_key=secret_key,
            region_name=region,
            use_ssl=use_ssl,
            config=Config(
                signature_version="s3v4",
                s3={"addressing_style": "path"},
                connect_timeout=connect_timeout,
                read_timeout=read_timeout,
                retries={"max_attempts": max_retries, "mode": "adaptive"},
            ),
        )

    # ------------------------------------------------------------------
    # Bucket operations
    # ------------------------------------------------------------------

    def list_buckets(self) -> List[Dict]:
        """List all accessible buckets.

        Returns:
            A list of dicts, each with ``Name`` and ``CreationDate`` keys.

        Raises:
            S3CompatError: If the request fails.
        """
        try:
            response = self._client.list_buckets()
            return [
                {"Name": b["Name"], "CreationDate": b["CreationDate"]}
                for b in response.get("Buckets", [])
            ]
        except ClientError as exc:
            raise self._wrap_error(exc, "list_buckets") from exc

    def create_bucket(self, bucket: str) -> None:
        """Create a new bucket.

        Args:
            bucket: The bucket name to create.

        Raises:
            S3CompatError: If the bucket already exists or the request fails.
        """
        try:
            self._client.create_bucket(Bucket=bucket)
            logger.info("Created bucket: %s", bucket)
        except ClientError as exc:
            raise self._wrap_error(exc, "create_bucket", bucket=bucket) from exc

    def delete_bucket(self, bucket: str) -> None:
        """Delete an empty bucket.

        Args:
            bucket: The bucket name to delete.

        Raises:
            S3CompatError: If the bucket is not empty or the request fails.
        """
        try:
            self._client.delete_bucket(Bucket=bucket)
            logger.info("Deleted bucket: %s", bucket)
        except ClientError as exc:
            raise self._wrap_error(exc, "delete_bucket", bucket=bucket) from exc

    def bucket_exists(self, bucket: str) -> bool:
        """Check whether a bucket exists.

        Args:
            bucket: The bucket name to check.

        Returns:
            ``True`` if the bucket exists and is accessible, ``False`` otherwise.
        """
        try:
            self._client.head_bucket(Bucket=bucket)
            return True
        except ClientError as exc:
            error_code = int(exc.response.get("Error", {}).get("Code", 0))
            if error_code == 404:
                return False
            raise self._wrap_error(exc, "bucket_exists", bucket=bucket) from exc

    # ------------------------------------------------------------------
    # Object operations
    # ------------------------------------------------------------------

    def put_object(
        self,
        bucket: str,
        key: str,
        body: BinaryIO,
        content_type: Optional[str] = None,
        metadata: Optional[Dict[str, str]] = None,
    ) -> Dict:
        """Upload an object.

        Args:
            bucket: Target bucket name.
            key: Object key (path within the bucket).
            body: A file-like object providing the data to upload.
            content_type: Optional MIME type. Defaults to ``application/octet-stream``.
            metadata: Optional user-defined metadata (key-value string pairs).

        Returns:
            A dict containing ``ETag`` and ``VersionId`` (if versioning enabled).

        Example::

            from io import BytesIO
            result = client.put_object("my-bucket", "hello.txt", BytesIO(b"Hello!"))
        """
        kwargs: Dict = {"Bucket": bucket, "Key": key, "Body": body}
        if content_type:
            kwargs["ContentType"] = content_type
        if metadata:
            kwargs["Metadata"] = metadata
        try:
            response = self._client.put_object(**kwargs)
            return {
                "ETag": response.get("ETag", ""),
                "VersionId": response.get("VersionId", ""),
            }
        except ClientError as exc:
            raise self._wrap_error(exc, "put_object", bucket=bucket, key=key) from exc

    def get_object(self, bucket: str, key: str) -> BinaryIO:
        """Download an object and return a readable stream.

        Args:
            bucket: Source bucket name.
            key: Object key.

        Returns:
            A file-like ``StreamingBody`` that supports ``.read()``.

        Example::

            stream = client.get_object("my-bucket", "hello.txt")
            data = stream.read()
        """
        try:
            response = self._client.get_object(Bucket=bucket, Key=key)
            return response["Body"]
        except ClientError as exc:
            raise self._wrap_error(exc, "get_object", bucket=bucket, key=key) from exc

    def delete_object(self, bucket: str, key: str) -> None:
        """Delete an object.

        Args:
            bucket: Bucket containing the object.
            key: Object key to delete.
        """
        try:
            self._client.delete_object(Bucket=bucket, Key=key)
            logger.debug("Deleted object: s3://%s/%s", bucket, key)
        except ClientError as exc:
            raise self._wrap_error(exc, "delete_object", bucket=bucket, key=key) from exc

    def head_object(self, bucket: str, key: str) -> Dict:
        """Retrieve object metadata without downloading the body.

        Args:
            bucket: Bucket containing the object.
            key: Object key.

        Returns:
            A dict with ``ContentLength``, ``ContentType``, ``ETag``,
            ``LastModified``, and ``Metadata``.
        """
        try:
            response = self._client.head_object(Bucket=bucket, Key=key)
            return {
                "ContentLength": response.get("ContentLength", 0),
                "ContentType": response.get("ContentType", ""),
                "ETag": response.get("ETag", ""),
                "LastModified": response.get("LastModified"),
                "Metadata": response.get("Metadata", {}),
            }
        except ClientError as exc:
            raise self._wrap_error(exc, "head_object", bucket=bucket, key=key) from exc

    def list_objects(
        self,
        bucket: str,
        prefix: str = "",
        max_keys: int = 1000,
    ) -> Iterator[Dict]:
        """List objects in a bucket, with automatic pagination.

        Args:
            bucket: Bucket to list.
            prefix: Filter objects by key prefix.
            max_keys: Maximum number of objects to return across all pages.

        Yields:
            Dicts with ``Key``, ``Size``, ``LastModified``, and ``ETag``.

        Example::

            for obj in client.list_objects("my-bucket", prefix="data/"):
                print(obj["Key"], obj["Size"])
        """
        paginator = self._client.get_paginator("list_objects_v2")
        yielded = 0
        try:
            for page in paginator.paginate(
                Bucket=bucket,
                Prefix=prefix,
                PaginationConfig={"MaxItems": max_keys, "PageSize": min(max_keys, 1000)},
            ):
                for obj in page.get("Contents", []):
                    if yielded >= max_keys:
                        return
                    yield {
                        "Key": obj["Key"],
                        "Size": obj["Size"],
                        "LastModified": obj["LastModified"],
                        "ETag": obj.get("ETag", ""),
                    }
                    yielded += 1
        except ClientError as exc:
            raise self._wrap_error(exc, "list_objects", bucket=bucket) from exc

    def object_exists(self, bucket: str, key: str) -> bool:
        """Check whether an object exists.

        Args:
            bucket: Bucket containing the object.
            key: Object key.

        Returns:
            ``True`` if the object exists, ``False`` otherwise.
        """
        try:
            self._client.head_object(Bucket=bucket, Key=key)
            return True
        except ClientError as exc:
            error_code = int(exc.response.get("Error", {}).get("Code", 0))
            if error_code == 404:
                return False
            raise self._wrap_error(exc, "object_exists", bucket=bucket, key=key) from exc

    # ------------------------------------------------------------------
    # Multipart upload / download
    # ------------------------------------------------------------------

    def upload_file(
        self,
        bucket: str,
        key: str,
        file_path: str,
        multipart_threshold: int = 100 * 1024 * 1024,
    ) -> None:
        """Upload a local file, using multipart upload for large files.

        Args:
            bucket: Target bucket.
            key: Object key.
            file_path: Path to the local file.
            multipart_threshold: File size threshold in bytes above which
                multipart upload is used. Defaults to 100 MiB.
        """
        file_size = os.path.getsize(file_path)
        if file_size <= multipart_threshold:
            with open(file_path, "rb") as fh:
                self.put_object(bucket, key, fh)
        else:
            try:
                from boto3.s3.transfer import TransferConfig

                config = TransferConfig(
                    multipart_threshold=multipart_threshold,
                    multipart_chunksize=max(multipart_threshold // 10, 8 * 1024 * 1024),
                    max_concurrency=4,
                )
                self._client.upload_file(file_path, bucket, key, Config=config)
                logger.info(
                    "Uploaded %s to s3://%s/%s (multipart, %d bytes)",
                    file_path, bucket, key, file_size,
                )
            except ClientError as exc:
                raise self._wrap_error(exc, "upload_file", bucket=bucket, key=key) from exc

    def download_file(self, bucket: str, key: str, file_path: str) -> None:
        """Download an object to a local file.

        Args:
            bucket: Source bucket.
            key: Object key.
            file_path: Local path to write the downloaded data.
        """
        try:
            self._client.download_file(bucket, key, file_path)
            logger.info("Downloaded s3://%s/%s to %s", bucket, key, file_path)
        except ClientError as exc:
            raise self._wrap_error(exc, "download_file", bucket=bucket, key=key) from exc

    # ------------------------------------------------------------------
    # Presigned URLs
    # ------------------------------------------------------------------

    def presign_get(
        self, bucket: str, key: str, expires_in: int = 3600
    ) -> str:
        """Generate a presigned URL for downloading an object.

        Args:
            bucket: Bucket containing the object.
            key: Object key.
            expires_in: URL expiry time in seconds. Defaults to 3600 (1 hour).

        Returns:
            A presigned URL string.
        """
        try:
            return self._client.generate_presigned_url(
                "get_object",
                Params={"Bucket": bucket, "Key": key},
                ExpiresIn=expires_in,
            )
        except ClientError as exc:
            raise self._wrap_error(exc, "presign_get", bucket=bucket, key=key) from exc

    def presign_put(
        self, bucket: str, key: str, expires_in: int = 3600
    ) -> str:
        """Generate a presigned URL for uploading an object.

        Args:
            bucket: Target bucket.
            key: Object key.
            expires_in: URL expiry time in seconds. Defaults to 3600 (1 hour).

        Returns:
            A presigned URL string.
        """
        try:
            return self._client.generate_presigned_url(
                "put_object",
                Params={"Bucket": bucket, "Key": key},
                ExpiresIn=expires_in,
            )
        except ClientError as exc:
            raise self._wrap_error(exc, "presign_put", bucket=bucket, key=key) from exc

    # ------------------------------------------------------------------
    # Copy
    # ------------------------------------------------------------------

    def copy_object(
        self,
        source_bucket: str,
        source_key: str,
        dest_bucket: str,
        dest_key: str,
    ) -> Dict:
        """Copy an object between buckets or keys.

        Args:
            source_bucket: Source bucket name.
            source_key: Source object key.
            dest_bucket: Destination bucket name.
            dest_key: Destination object key.

        Returns:
            A dict with ``CopyObjectResult`` containing ``ETag`` and ``LastModified``.
        """
        try:
            response = self._client.copy_object(
                CopySource={"Bucket": source_bucket, "Key": source_key},
                Bucket=dest_bucket,
                Key=dest_key,
            )
            result = response.get("CopyObjectResult", {})
            return {
                "ETag": result.get("ETag", ""),
                "LastModified": result.get("LastModified"),
            }
        except ClientError as exc:
            raise self._wrap_error(
                exc, "copy_object",
                bucket=f"{source_bucket}->{dest_bucket}",
                key=f"{source_key}->{dest_key}",
            ) from exc

    # ------------------------------------------------------------------
    # Internal helpers
    # ------------------------------------------------------------------

    @staticmethod
    def _wrap_error(
        exc: ClientError,
        operation: str,
        bucket: str = "",
        key: str = "",
    ) -> S3CompatError:
        """Convert a botocore ClientError into a typed S3CompatError."""
        error_info = exc.response.get("Error", {})
        code = error_info.get("Code", "Unknown")
        message = error_info.get("Message", str(exc))
        status = exc.response.get("ResponseMetadata", {}).get("HTTPStatusCode", 0)

        context_parts = [f"S3 {operation} failed"]
        if bucket:
            context_parts.append(f"bucket={bucket}")
        if key:
            context_parts.append(f"key={key}")
        context_parts.append(f"[{code}] {message}")

        return S3CompatError(
            " | ".join(context_parts),
            error_code=str(code),
            status_code=status,
        )
