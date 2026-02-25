// Package dwclient provides Go client bindings for the DataWarehouse
// Universal Storage Fabric.
//
// The package offers two levels of abstraction:
//
//   - [Client] provides high-level operations using dw:// URI addressing,
//     mapping naturally to DataWarehouse's namespace model.
//
//   - [S3Client] provides low-level S3-compatible operations using
//     aws-sdk-go-v2, giving full control over bucket and object operations.
//
// # Quick Start
//
//	import "github.com/datawarehouse/dw-client-go/dwclient"
//
//	client, err := dwclient.New(dwclient.ClientConfig{
//	    Endpoint:  "http://localhost:9000",
//	    AccessKey: "my-access-key",
//	    SecretKey: "my-secret-key",
//	})
//	if err != nil {
//	    log.Fatal(err)
//	}
//
//	// Store data using dw:// addressing
//	err = client.Store(ctx, "dw://my-bucket/data.json",
//	    strings.NewReader(`{"hello":"world"}`),
//	    dwclient.WithContentType("application/json"),
//	)
//
//	// Retrieve data
//	reader, err := client.Retrieve(ctx, "dw://my-bucket/data.json")
//	defer reader.Close()
//
//	// Direct S3 operations
//	s3 := client.S3()
//	info, err := s3.HeadObject(ctx, "my-bucket", "data.json")
//
// # URI Format
//
// The dw:// URI scheme maps to S3 bucket/key addressing:
//
//	dw://bucket-name              -> bucket only (for List, Exists)
//	dw://bucket-name/object-key   -> bucket + key
//	dw://bucket-name/path/to/key  -> bucket + nested key
//	s3://bucket-name/key          -> also accepted
//
// # Connection
//
// The client connects to a DataWarehouse S3-compatible HTTP endpoint
// (see S3HttpServer in the DataWarehouse.Plugins.UniversalFabric package).
// Authentication uses S3 access key / secret key credentials.
package dwclient
