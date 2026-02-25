using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Xml.Linq;
using DataWarehouse.SDK.Storage.Fabric;

namespace DataWarehouse.Plugins.UniversalFabric.S3Server;

/// <summary>
/// Writes S3-compatible XML responses and sets appropriate HTTP headers on
/// <see cref="HttpListenerResponse"/> objects.
/// </summary>
/// <remarks>
/// All XML responses conform to the S3 XML namespace
/// <c>http://s3.amazonaws.com/doc/2006-03-01/</c> for client compatibility.
/// Error responses follow the standard S3 error XML schema with codes such as
/// NoSuchBucket, NoSuchKey, InvalidBucketName, BucketAlreadyExists, InvalidArgument,
/// InternalError, and AccessDenied.
/// </remarks>
public sealed class S3ResponseWriter
{
    private static readonly XNamespace S3Ns = "http://s3.amazonaws.com/doc/2006-03-01/";

    /// <summary>
    /// Writes a ListBuckets XML response.
    /// </summary>
    public void WriteListBucketsResponse(HttpListenerResponse resp, S3ListBucketsResponse data)
    {
        ArgumentNullException.ThrowIfNull(resp);
        ArgumentNullException.ThrowIfNull(data);

        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement(S3Ns + "ListAllMyBucketsResult",
                new XElement(S3Ns + "Owner",
                    new XElement(S3Ns + "ID", data.Owner),
                    new XElement(S3Ns + "DisplayName", data.Owner)),
                new XElement(S3Ns + "Buckets",
                    BuildBucketElements(data.Buckets))));

        WriteXmlResponse(resp, 200, doc);
    }

    /// <summary>
    /// Writes a ListObjectsV2 XML response.
    /// </summary>
    public void WriteListObjectsResponse(HttpListenerResponse resp, S3ListObjectsResponse data, string bucketName)
    {
        ArgumentNullException.ThrowIfNull(resp);
        ArgumentNullException.ThrowIfNull(data);

        var elements = new List<object>
        {
            new XElement(S3Ns + "Name", bucketName),
            new XElement(S3Ns + "KeyCount", data.KeyCount),
            new XElement(S3Ns + "MaxKeys", 1000),
            new XElement(S3Ns + "IsTruncated", data.IsTruncated.ToString().ToLowerInvariant())
        };

        if (data.NextContinuationToken != null)
        {
            elements.Add(new XElement(S3Ns + "NextContinuationToken", data.NextContinuationToken));
        }

        foreach (var obj in data.Contents)
        {
            elements.Add(new XElement(S3Ns + "Contents",
                new XElement(S3Ns + "Key", obj.Key),
                new XElement(S3Ns + "LastModified", obj.LastModified.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")),
                new XElement(S3Ns + "ETag", $"\"{obj.ETag}\""),
                new XElement(S3Ns + "Size", obj.Size),
                new XElement(S3Ns + "StorageClass", obj.StorageClass)));
        }

        foreach (var prefix in data.CommonPrefixes)
        {
            elements.Add(new XElement(S3Ns + "CommonPrefixes",
                new XElement(S3Ns + "Prefix", prefix)));
        }

        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement(S3Ns + "ListBucketResult", elements.ToArray()));

        WriteXmlResponse(resp, 200, doc);
    }

    /// <summary>
    /// Writes an InitiateMultipartUpload XML response.
    /// </summary>
    public void WriteInitiateMultipartResponse(HttpListenerResponse resp, S3InitiateMultipartResponse data)
    {
        ArgumentNullException.ThrowIfNull(resp);
        ArgumentNullException.ThrowIfNull(data);

        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement(S3Ns + "InitiateMultipartUploadResult",
                new XElement(S3Ns + "Bucket", data.BucketName),
                new XElement(S3Ns + "Key", data.Key),
                new XElement(S3Ns + "UploadId", data.UploadId)));

        WriteXmlResponse(resp, 200, doc);
    }

    /// <summary>
    /// Writes a CompleteMultipartUpload XML response.
    /// </summary>
    public void WriteCompleteMultipartResponse(HttpListenerResponse resp, S3CompleteMultipartResponse data)
    {
        ArgumentNullException.ThrowIfNull(resp);
        ArgumentNullException.ThrowIfNull(data);

        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement(S3Ns + "CompleteMultipartUploadResult",
                new XElement(S3Ns + "Bucket", data.BucketName),
                new XElement(S3Ns + "Key", data.Key),
                new XElement(S3Ns + "ETag", $"\"{data.ETag}\"")));

        WriteXmlResponse(resp, 200, doc);
    }

    /// <summary>
    /// Writes a CopyObject XML response.
    /// </summary>
    public void WriteCopyObjectResponse(HttpListenerResponse resp, S3CopyObjectResponse data)
    {
        ArgumentNullException.ThrowIfNull(resp);
        ArgumentNullException.ThrowIfNull(data);

        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement(S3Ns + "CopyObjectResult",
                new XElement(S3Ns + "LastModified", data.LastModified.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")),
                new XElement(S3Ns + "ETag", $"\"{data.ETag}\"")));

        WriteXmlResponse(resp, 200, doc);
    }

    /// <summary>
    /// Writes a CreateBucket location response.
    /// </summary>
    public void WriteCreateBucketResponse(HttpListenerResponse resp, S3CreateBucketResponse data)
    {
        ArgumentNullException.ThrowIfNull(resp);
        ArgumentNullException.ThrowIfNull(data);

        resp.StatusCode = 200;
        resp.Headers.Set("Location", $"/{data.BucketName}");
        resp.ContentLength64 = 0;
        resp.Close();
    }

    /// <summary>
    /// Writes a standard S3 error response in XML format.
    /// </summary>
    /// <param name="resp">The HTTP listener response.</param>
    /// <param name="statusCode">The HTTP status code (e.g., 404, 403, 500).</param>
    /// <param name="errorCode">The S3 error code (e.g., NoSuchBucket, NoSuchKey, AccessDenied).</param>
    /// <param name="message">A human-readable error message.</param>
    /// <param name="resource">The optional resource (bucket or key) that caused the error.</param>
    public void WriteErrorResponse(HttpListenerResponse resp, int statusCode,
        string errorCode, string message, string? resource = null)
    {
        ArgumentNullException.ThrowIfNull(resp);

        var elements = new List<object>
        {
            new XElement("Code", errorCode),
            new XElement("Message", message),
            new XElement("RequestId", Guid.NewGuid().ToString("N"))
        };

        if (resource != null)
        {
            elements.Add(new XElement("Resource", resource));
        }

        // S3 error responses do NOT use the S3 namespace
        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement("Error", elements.ToArray()));

        WriteXmlResponse(resp, statusCode, doc);
    }

    /// <summary>
    /// Sets standard S3 object headers (Content-Type, Content-Length, ETag, Last-Modified, metadata)
    /// on the response from a HEAD object result.
    /// </summary>
    public void SetObjectHeaders(HttpListenerResponse resp, S3HeadObjectResponse metadata)
    {
        ArgumentNullException.ThrowIfNull(resp);
        ArgumentNullException.ThrowIfNull(metadata);

        resp.ContentType = metadata.ContentType;
        resp.ContentLength64 = metadata.ContentLength;
        resp.Headers.Set("ETag", $"\"{metadata.ETag}\"");
        resp.Headers.Set("Last-Modified", metadata.LastModified.ToUniversalTime().ToString("R"));

        if (metadata.Metadata != null)
        {
            foreach (var kvp in metadata.Metadata)
            {
                resp.Headers.Set($"x-amz-meta-{kvp.Key}", kvp.Value);
            }
        }
    }

    /// <summary>
    /// Sets standard S3 object headers from a GET object response and streams the body to the client.
    /// </summary>
    public void SetGetObjectHeaders(HttpListenerResponse resp, S3GetObjectResponse data)
    {
        ArgumentNullException.ThrowIfNull(resp);
        ArgumentNullException.ThrowIfNull(data);

        resp.ContentType = data.ContentType;
        resp.ContentLength64 = data.ContentLength;
        resp.Headers.Set("ETag", $"\"{data.ETag}\"");
        resp.Headers.Set("Last-Modified", data.LastModified.ToUniversalTime().ToString("R"));

        if (data.Metadata != null)
        {
            foreach (var kvp in data.Metadata)
            {
                resp.Headers.Set($"x-amz-meta-{kvp.Key}", kvp.Value);
            }
        }
    }

    /// <summary>
    /// Writes a simple 200 OK with no body (used for DELETE, etc.).
    /// </summary>
    public void WriteNoContentResponse(HttpListenerResponse resp, int statusCode = 204)
    {
        resp.StatusCode = statusCode;
        resp.ContentLength64 = 0;
        resp.Close();
    }

    /// <summary>
    /// Writes a PutObject success response with ETag header.
    /// </summary>
    public void WritePutObjectResponse(HttpListenerResponse resp, S3PutObjectResponse data)
    {
        ArgumentNullException.ThrowIfNull(resp);
        ArgumentNullException.ThrowIfNull(data);

        resp.StatusCode = 200;
        resp.Headers.Set("ETag", $"\"{data.ETag}\"");
        if (data.VersionId != null)
        {
            resp.Headers.Set("x-amz-version-id", data.VersionId);
        }
        resp.ContentLength64 = 0;
        resp.Close();
    }

    /// <summary>
    /// Writes an UploadPart success response with ETag header.
    /// </summary>
    public void WriteUploadPartResponse(HttpListenerResponse resp, S3UploadPartResponse data)
    {
        ArgumentNullException.ThrowIfNull(resp);
        ArgumentNullException.ThrowIfNull(data);

        resp.StatusCode = 200;
        resp.Headers.Set("ETag", $"\"{data.ETag}\"");
        resp.ContentLength64 = 0;
        resp.Close();
    }

    #region Private Helpers

    private static IEnumerable<XElement> BuildBucketElements(IReadOnlyList<S3Bucket> buckets)
    {
        foreach (var b in buckets)
        {
            yield return new XElement(S3Ns + "Bucket",
                new XElement(S3Ns + "Name", b.Name),
                new XElement(S3Ns + "CreationDate", b.CreationDate.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")));
        }
    }

    private static void WriteXmlResponse(HttpListenerResponse resp, int statusCode, XDocument doc)
    {
        resp.StatusCode = statusCode;
        resp.ContentType = "application/xml; charset=utf-8";

        var sb = new StringBuilder();
        using (var writer = new StringWriter(sb))
        {
            doc.Save(writer);
        }

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        resp.ContentLength64 = bytes.Length;

        try
        {
            resp.OutputStream.Write(bytes, 0, bytes.Length);
            resp.OutputStream.Flush();
        }
        finally
        {
            resp.Close();
        }
    }

    #endregion
}
