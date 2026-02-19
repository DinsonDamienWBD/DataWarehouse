"""DataWarehouse Universal Storage Client.

Provides two client interfaces:

- :class:`DataWarehouseClient` -- High-level client with ``dw://`` addressing
- :class:`S3CompatClient` -- Low-level S3-compatible client using boto3

Quick start::

    from dw_client import DataWarehouseClient

    client = DataWarehouseClient(
        endpoint="http://localhost:9000",
        access_key="my-access-key",
        secret_key="my-secret-key",
    )
    client.store("dw://my-bucket/data.json", b'{"hello": "world"}')
    data = client.retrieve("dw://my-bucket/data.json")
"""

from .client import DataWarehouseClient
from .s3_compat import S3CompatClient

__version__ = "5.0.0"
__all__ = ["DataWarehouseClient", "S3CompatClient"]
