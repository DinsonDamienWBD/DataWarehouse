"""DataWarehouse Universal Storage Client - Python SDK.

Provides S3-compatible and native dw:// addressing clients for
DataWarehouse's Universal Fabric storage layer.
"""

from setuptools import setup, find_packages

setup(
    name="dw-client",
    version="5.0.0",
    description="DataWarehouse Universal Storage Client",
    long_description=open("README.md", encoding="utf-8").read(),
    long_description_content_type="text/markdown",
    author="DataWarehouse Team",
    license="MIT",
    packages=find_packages(),
    install_requires=[
        "boto3>=1.26.0",
        "requests>=2.28.0",
    ],
    python_requires=">=3.9",
    classifiers=[
        "Programming Language :: Python :: 3",
        "Programming Language :: Python :: 3.9",
        "Programming Language :: Python :: 3.10",
        "Programming Language :: Python :: 3.11",
        "Programming Language :: Python :: 3.12",
        "License :: OSI Approved :: MIT License",
        "Operating System :: OS Independent",
        "Topic :: Database",
        "Topic :: System :: Distributed Computing",
    ],
    project_urls={
        "Documentation": "https://github.com/datawarehouse/datawarehouse/tree/master/Clients/python",
        "Source": "https://github.com/datawarehouse/datawarehouse",
    },
)
