// Package dwclient provides Go client bindings for DataWarehouse's S3-compatible
// storage API. It supports both low-level S3 operations via [S3Client] and
// high-level dw:// URI addressing via [Client].
package dwclient

import (
	"context"
	"fmt"
	"io"
	"time"

	"github.com/aws/aws-sdk-go-v2/aws"
	"github.com/aws/aws-sdk-go-v2/credentials"
	"github.com/aws/aws-sdk-go-v2/service/s3"
	"github.com/aws/aws-sdk-go-v2/service/s3/types"
	"github.com/aws/smithy-go/logging"
)

// S3Client wraps an aws-sdk-go-v2 S3 client configured to communicate with
// a DataWarehouse S3-compatible endpoint.
type S3Client struct {
	client   *s3.Client
	psClient *s3.PresignClient
	region   string
}

// S3Config holds connection parameters for the DataWarehouse S3 endpoint.
type S3Config struct {
	// Endpoint is the base URL of the DataWarehouse S3 HTTP server (e.g. "http://localhost:9000").
	Endpoint string

	// AccessKey is the S3 access key ID for authentication.
	AccessKey string

	// SecretKey is the S3 secret access key for authentication.
	SecretKey string

	// Region is the AWS region identifier. Defaults to "us-east-1" if empty.
	Region string

	// UseSSL indicates whether the endpoint uses HTTPS. Currently informational;
	// the caller should include the correct scheme in Endpoint.
	UseSSL bool
}

// ObjectInfo describes an object stored in an S3-compatible bucket.
type ObjectInfo struct {
	// Key is the object key (path within the bucket).
	Key string

	// Size is the object size in bytes.
	Size int64

	// LastModified is the timestamp when the object was last modified.
	LastModified time.Time

	// ETag is the entity tag (typically an MD5 hash of the object content).
	ETag string

	// ContentType is the MIME type of the object content.
	ContentType string

	// Metadata contains user-defined key-value metadata pairs.
	Metadata map[string]string
}

// PutOption configures optional parameters on a PutObject request.
type PutOption func(*s3.PutObjectInput)

// ListOption configures optional parameters on a ListObjectsV2 request.
type ListOption func(*s3.ListObjectsV2Input)

// WithContentType sets the content type on a PutObject request.
func WithContentType(ct string) PutOption {
	return func(input *s3.PutObjectInput) {
		input.ContentType = aws.String(ct)
	}
}

// WithMetadata sets user-defined metadata on a PutObject request.
func WithMetadata(m map[string]string) PutOption {
	return func(input *s3.PutObjectInput) {
		input.Metadata = m
	}
}

// WithPrefix filters list results to objects matching the given prefix.
func WithPrefix(prefix string) ListOption {
	return func(input *s3.ListObjectsV2Input) {
		input.Prefix = aws.String(prefix)
	}
}

// WithDelimiter sets the delimiter for grouping list results (e.g. "/" for
// directory-like listing).
func WithDelimiter(d string) ListOption {
	return func(input *s3.ListObjectsV2Input) {
		input.Delimiter = aws.String(d)
	}
}

// WithMaxKeys limits the number of keys returned per list request.
func WithMaxKeys(n int32) ListOption {
	return func(input *s3.ListObjectsV2Input) {
		input.MaxKeys = aws.Int32(n)
	}
}

// PutObjectOutput contains the result of a successful PutObject operation.
type PutObjectOutput struct {
	// ETag is the entity tag of the uploaded object.
	ETag string

	// VersionID is the version ID of the uploaded object (empty if versioning is disabled).
	VersionID string
}

// NewS3Client creates a new S3Client configured to communicate with a
// DataWarehouse S3-compatible endpoint. It uses static credentials and
// path-style addressing (required for custom S3-compatible servers).
func NewS3Client(cfg S3Config) (*S3Client, error) {
	if cfg.Endpoint == "" {
		return nil, fmt.Errorf("dwclient: endpoint is required")
	}

	region := cfg.Region
	if region == "" {
		region = "us-east-1"
	}

	staticCreds := credentials.NewStaticCredentialsProvider(cfg.AccessKey, cfg.SecretKey, "")

	s3Client := s3.New(s3.Options{
		Region:       region,
		Credentials:  staticCreds,
		BaseEndpoint: aws.String(cfg.Endpoint),
		UsePathStyle: true,
		Logger:       logging.Nop{},
	})

	psClient := s3.NewPresignClient(s3Client)

	return &S3Client{
		client:   s3Client,
		psClient: psClient,
		region:   region,
	}, nil
}

// --- Bucket operations ---

// ListBuckets returns all buckets accessible with the configured credentials.
func (c *S3Client) ListBuckets(ctx context.Context) ([]types.Bucket, error) {
	resp, err := c.client.ListBuckets(ctx, &s3.ListBucketsInput{})
	if err != nil {
		return nil, fmt.Errorf("list buckets: %w", err)
	}
	return resp.Buckets, nil
}

// CreateBucket creates a new bucket with the given name.
func (c *S3Client) CreateBucket(ctx context.Context, name string) error {
	input := &s3.CreateBucketInput{
		Bucket: aws.String(name),
	}

	// Only set LocationConstraint if region is not us-east-1 (S3 convention).
	if c.region != "us-east-1" {
		input.CreateBucketConfiguration = &types.CreateBucketConfiguration{
			LocationConstraint: types.BucketLocationConstraint(c.region),
		}
	}

	_, err := c.client.CreateBucket(ctx, input)
	if err != nil {
		return fmt.Errorf("create bucket %q: %w", name, err)
	}
	return nil
}

// DeleteBucket deletes the bucket with the given name. The bucket must be empty.
func (c *S3Client) DeleteBucket(ctx context.Context, name string) error {
	_, err := c.client.DeleteBucket(ctx, &s3.DeleteBucketInput{
		Bucket: aws.String(name),
	})
	if err != nil {
		return fmt.Errorf("delete bucket %q: %w", name, err)
	}
	return nil
}

// BucketExists checks whether a bucket with the given name exists.
func (c *S3Client) BucketExists(ctx context.Context, name string) (bool, error) {
	_, err := c.client.HeadBucket(ctx, &s3.HeadBucketInput{
		Bucket: aws.String(name),
	})
	if err != nil {
		// HeadBucket returns a 404-style error if the bucket does not exist.
		// We treat any error as "not found" since the SDK wraps HTTP status codes.
		return false, nil
	}
	return true, nil
}

// --- Object operations ---

// PutObject uploads an object to the specified bucket and key.
func (c *S3Client) PutObject(ctx context.Context, bucket, key string, body io.Reader, opts ...PutOption) (*PutObjectOutput, error) {
	input := &s3.PutObjectInput{
		Bucket: aws.String(bucket),
		Key:    aws.String(key),
		Body:   body,
	}

	for _, opt := range opts {
		opt(input)
	}

	resp, err := c.client.PutObject(ctx, input)
	if err != nil {
		return nil, fmt.Errorf("put object %s/%s: %w", bucket, key, err)
	}

	out := &PutObjectOutput{}
	if resp.ETag != nil {
		out.ETag = *resp.ETag
	}
	if resp.VersionId != nil {
		out.VersionID = *resp.VersionId
	}
	return out, nil
}

// GetObject retrieves an object from the specified bucket and key. The caller
// is responsible for closing the returned io.ReadCloser.
func (c *S3Client) GetObject(ctx context.Context, bucket, key string) (io.ReadCloser, *ObjectInfo, error) {
	resp, err := c.client.GetObject(ctx, &s3.GetObjectInput{
		Bucket: aws.String(bucket),
		Key:    aws.String(key),
	})
	if err != nil {
		return nil, nil, fmt.Errorf("get object %s/%s: %w", bucket, key, err)
	}

	info := &ObjectInfo{
		Key:      key,
		Size:     aws.ToInt64(resp.ContentLength),
		ETag:     aws.ToString(resp.ETag),
		Metadata: resp.Metadata,
	}
	if resp.LastModified != nil {
		info.LastModified = *resp.LastModified
	}
	if resp.ContentType != nil {
		info.ContentType = *resp.ContentType
	}

	return resp.Body, info, nil
}

// DeleteObject removes an object from the specified bucket.
func (c *S3Client) DeleteObject(ctx context.Context, bucket, key string) error {
	_, err := c.client.DeleteObject(ctx, &s3.DeleteObjectInput{
		Bucket: aws.String(bucket),
		Key:    aws.String(key),
	})
	if err != nil {
		return fmt.Errorf("delete object %s/%s: %w", bucket, key, err)
	}
	return nil
}

// HeadObject retrieves metadata about an object without downloading its content.
func (c *S3Client) HeadObject(ctx context.Context, bucket, key string) (*ObjectInfo, error) {
	resp, err := c.client.HeadObject(ctx, &s3.HeadObjectInput{
		Bucket: aws.String(bucket),
		Key:    aws.String(key),
	})
	if err != nil {
		return nil, fmt.Errorf("head object %s/%s: %w", bucket, key, err)
	}

	info := &ObjectInfo{
		Key:      key,
		Size:     aws.ToInt64(resp.ContentLength),
		ETag:     aws.ToString(resp.ETag),
		Metadata: resp.Metadata,
	}
	if resp.LastModified != nil {
		info.LastModified = *resp.LastModified
	}
	if resp.ContentType != nil {
		info.ContentType = *resp.ContentType
	}

	return info, nil
}

// ListObjects lists objects in the specified bucket with optional filtering.
// All pages are collected and returned as a single slice.
func (c *S3Client) ListObjects(ctx context.Context, bucket string, opts ...ListOption) ([]ObjectInfo, error) {
	input := &s3.ListObjectsV2Input{
		Bucket: aws.String(bucket),
	}

	for _, opt := range opts {
		opt(input)
	}

	var objects []ObjectInfo
	paginator := s3.NewListObjectsV2Paginator(c.client, input)

	for paginator.HasMorePages() {
		page, err := paginator.NextPage(ctx)
		if err != nil {
			return nil, fmt.Errorf("list objects in %s: %w", bucket, err)
		}

		for _, obj := range page.Contents {
			info := ObjectInfo{
				Key:  aws.ToString(obj.Key),
				Size: aws.ToInt64(obj.Size),
				ETag: aws.ToString(obj.ETag),
			}
			if obj.LastModified != nil {
				info.LastModified = *obj.LastModified
			}
			objects = append(objects, info)
		}
	}

	return objects, nil
}

// ObjectExists checks whether an object exists at the specified bucket and key.
func (c *S3Client) ObjectExists(ctx context.Context, bucket, key string) (bool, error) {
	_, err := c.client.HeadObject(ctx, &s3.HeadObjectInput{
		Bucket: aws.String(bucket),
		Key:    aws.String(key),
	})
	if err != nil {
		return false, nil
	}
	return true, nil
}

// --- Copy ---

// CopyObject copies an object from one location to another within the same
// or across buckets.
func (c *S3Client) CopyObject(ctx context.Context, srcBucket, srcKey, dstBucket, dstKey string) error {
	copySource := fmt.Sprintf("%s/%s", srcBucket, srcKey)
	_, err := c.client.CopyObject(ctx, &s3.CopyObjectInput{
		Bucket:     aws.String(dstBucket),
		Key:        aws.String(dstKey),
		CopySource: aws.String(copySource),
	})
	if err != nil {
		return fmt.Errorf("copy object %s/%s -> %s/%s: %w", srcBucket, srcKey, dstBucket, dstKey, err)
	}
	return nil
}

// --- Presigned URLs ---

// PresignGetObject generates a presigned URL for downloading an object.
// The URL expires after the given duration.
func (c *S3Client) PresignGetObject(ctx context.Context, bucket, key string, expiry time.Duration) (string, error) {
	req, err := c.psClient.PresignGetObject(ctx, &s3.GetObjectInput{
		Bucket: aws.String(bucket),
		Key:    aws.String(key),
	}, s3.WithPresignExpires(expiry))
	if err != nil {
		return "", fmt.Errorf("presign get %s/%s: %w", bucket, key, err)
	}
	return req.URL, nil
}

// PresignPutObject generates a presigned URL for uploading an object.
// The URL expires after the given duration.
func (c *S3Client) PresignPutObject(ctx context.Context, bucket, key string, expiry time.Duration) (string, error) {
	req, err := c.psClient.PresignPutObject(ctx, &s3.PutObjectInput{
		Bucket: aws.String(bucket),
		Key:    aws.String(key),
	}, s3.WithPresignExpires(expiry))
	if err != nil {
		return "", fmt.Errorf("presign put %s/%s: %w", bucket, key, err)
	}
	return req.URL, nil
}
