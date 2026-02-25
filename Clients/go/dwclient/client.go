package dwclient

import (
	"context"
	"fmt"
	"io"
	"strings"
	"time"

	"github.com/aws/aws-sdk-go-v2/service/s3/types"
)

// Client is the high-level DataWarehouse client that translates dw:// URIs
// into S3 operations against the DataWarehouse storage fabric.
//
// Use [New] to create a Client instance:
//
//	client, err := dwclient.New(dwclient.ClientConfig{
//	    Endpoint:  "http://localhost:9000",
//	    AccessKey: "my-key",
//	    SecretKey: "my-secret",
//	})
//	if err != nil {
//	    log.Fatal(err)
//	}
//
//	err = client.Store(ctx, "dw://my-bucket/data.json", strings.NewReader(`{"hello":"world"}`))
type Client struct {
	s3     *S3Client
	config ClientConfig
}

// ClientConfig holds connection parameters for the high-level DataWarehouse
// client. The underlying transport uses S3-compatible protocol.
type ClientConfig struct {
	// Endpoint is the base URL of the DataWarehouse S3 HTTP server.
	Endpoint string

	// AccessKey is the S3 access key ID for authentication.
	AccessKey string

	// SecretKey is the S3 secret access key for authentication.
	SecretKey string

	// Region is the AWS region identifier. Defaults to "us-east-1" if empty.
	Region string
}

// New creates a new high-level DataWarehouse client. The client communicates
// with the DataWarehouse S3-compatible endpoint specified in cfg.
func New(cfg ClientConfig) (*Client, error) {
	s3Client, err := NewS3Client(S3Config{
		Endpoint:  cfg.Endpoint,
		AccessKey: cfg.AccessKey,
		SecretKey: cfg.SecretKey,
		Region:    cfg.Region,
	})
	if err != nil {
		return nil, fmt.Errorf("creating S3 client: %w", err)
	}
	return &Client{s3: s3Client, config: cfg}, nil
}

// Store writes data from body to the object identified by the given dw:// URI.
//
// The URI format is dw://bucket/key or s3://bucket/key. Optional [PutOption]
// values can set content type, metadata, and other S3 parameters.
func (c *Client) Store(ctx context.Context, dwURI string, body io.Reader, opts ...PutOption) error {
	bucket, key, err := parseDwURI(dwURI)
	if err != nil {
		return err
	}
	if key == "" {
		return fmt.Errorf("dwclient: Store requires an object key in URI: %s", dwURI)
	}
	_, err = c.s3.PutObject(ctx, bucket, key, body, opts...)
	return err
}

// Retrieve reads the object at the given dw:// URI. The caller is responsible
// for closing the returned [io.ReadCloser].
func (c *Client) Retrieve(ctx context.Context, dwURI string) (io.ReadCloser, error) {
	bucket, key, err := parseDwURI(dwURI)
	if err != nil {
		return nil, err
	}
	if key == "" {
		return nil, fmt.Errorf("dwclient: Retrieve requires an object key in URI: %s", dwURI)
	}
	body, _, err := c.s3.GetObject(ctx, bucket, key)
	if err != nil {
		return nil, err
	}
	return body, nil
}

// Delete removes the object at the given dw:// URI.
func (c *Client) Delete(ctx context.Context, dwURI string) error {
	bucket, key, err := parseDwURI(dwURI)
	if err != nil {
		return err
	}
	if key == "" {
		return fmt.Errorf("dwclient: Delete requires an object key in URI: %s", dwURI)
	}
	return c.s3.DeleteObject(ctx, bucket, key)
}

// Exists checks whether an object exists at the given dw:// URI.
func (c *Client) Exists(ctx context.Context, dwURI string) (bool, error) {
	bucket, key, err := parseDwURI(dwURI)
	if err != nil {
		return false, err
	}
	if key == "" {
		// If only a bucket is specified, check bucket existence.
		return c.s3.BucketExists(ctx, bucket)
	}
	return c.s3.ObjectExists(ctx, bucket, key)
}

// List lists objects under the given dw:// prefix. The URI may specify just
// a bucket (dw://bucket) or a bucket with key prefix (dw://bucket/prefix/).
func (c *Client) List(ctx context.Context, dwURI string) ([]ObjectInfo, error) {
	bucket, prefix, err := parseDwURI(dwURI)
	if err != nil {
		return nil, err
	}

	var opts []ListOption
	if prefix != "" {
		opts = append(opts, WithPrefix(prefix))
	}

	return c.s3.ListObjects(ctx, bucket, opts...)
}

// Copy copies an object from srcURI to dstURI. Both must be dw:// or s3://
// URIs that include a bucket and key.
func (c *Client) Copy(ctx context.Context, srcURI, dstURI string) error {
	srcBucket, srcKey, err := parseDwURI(srcURI)
	if err != nil {
		return fmt.Errorf("source: %w", err)
	}
	if srcKey == "" {
		return fmt.Errorf("dwclient: Copy source requires an object key: %s", srcURI)
	}

	dstBucket, dstKey, err := parseDwURI(dstURI)
	if err != nil {
		return fmt.Errorf("destination: %w", err)
	}
	if dstKey == "" {
		return fmt.Errorf("dwclient: Copy destination requires an object key: %s", dstURI)
	}

	return c.s3.CopyObject(ctx, srcBucket, srcKey, dstBucket, dstKey)
}

// PresignGet generates a presigned GET URL for downloading the object at the
// given dw:// URI. The URL is valid for the specified duration.
func (c *Client) PresignGet(ctx context.Context, dwURI string, expiry time.Duration) (string, error) {
	bucket, key, err := parseDwURI(dwURI)
	if err != nil {
		return "", err
	}
	if key == "" {
		return "", fmt.Errorf("dwclient: PresignGet requires an object key in URI: %s", dwURI)
	}
	return c.s3.PresignGetObject(ctx, bucket, key, expiry)
}

// CreateBucket creates a new bucket with the given name in the DataWarehouse
// storage fabric.
func (c *Client) CreateBucket(ctx context.Context, name string) error {
	return c.s3.CreateBucket(ctx, name)
}

// DeleteBucket deletes the bucket with the given name. The bucket must be empty.
func (c *Client) DeleteBucket(ctx context.Context, name string) error {
	return c.s3.DeleteBucket(ctx, name)
}

// ListBuckets returns all buckets accessible with the configured credentials.
func (c *Client) ListBuckets(ctx context.Context) ([]types.Bucket, error) {
	return c.s3.ListBuckets(ctx)
}

// S3 returns the underlying [S3Client] for advanced operations that require
// direct S3 API access beyond what the dw:// addressing provides.
func (c *Client) S3() *S3Client {
	return c.s3
}

// parseDwURI splits a dw:// or s3:// URI into bucket and key components.
//
// Accepted formats:
//   - dw://bucket
//   - dw://bucket/key
//   - dw://bucket/path/to/key
//   - s3://bucket/key
//
// Returns an error if the URI is missing a scheme or bucket.
func parseDwURI(uri string) (bucket, key string, err error) {
	raw := uri

	// Strip known schemes.
	switch {
	case strings.HasPrefix(uri, "dw://"):
		uri = strings.TrimPrefix(uri, "dw://")
	case strings.HasPrefix(uri, "s3://"):
		uri = strings.TrimPrefix(uri, "s3://")
	default:
		return "", "", fmt.Errorf("dwclient: invalid URI scheme (expected dw:// or s3://): %s", raw)
	}

	// Remove leading slashes that may result from triple-slash variants.
	uri = strings.TrimLeft(uri, "/")

	if uri == "" {
		return "", "", fmt.Errorf("dwclient: missing bucket in URI: %s", raw)
	}

	parts := strings.SplitN(uri, "/", 2)
	bucket = parts[0]
	if bucket == "" {
		return "", "", fmt.Errorf("dwclient: empty bucket name in URI: %s", raw)
	}
	if len(parts) > 1 {
		key = parts[1]
	}
	return bucket, key, nil
}
