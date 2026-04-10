# Configuration reference

This page documents every public API method in `Aspire.Hosting.DocumentDB` with usage examples and default values.

## AddDocumentDB

The extension adds a DocumentDB server resource to the Aspire application model. A container is started for local development.

```csharp
// Minimal -- random port, generated credentials
var server = builder.AddDocumentDB("documentdb");

// Fixed host port
var server = builder.AddDocumentDB("documentdb", port: 10260);

// Custom credentials via Aspire parameters
var user = builder.AddParameter("db-user");
var pass = builder.AddParameter("db-pass", secret: true);
var server = builder.AddDocumentDB("documentdb", userName: user, password: pass);
```

| Parameter | Type | Default | Description |
|---|---|---|---|
| `name` | `string` | (required) | Resource name. Also used as the connection string name when referenced by services. |
| `port` | `int?` | `null` (random) | Host port to expose. When `null`, Aspire assigns a random available port. |
| `userName` | `IResourceBuilder<ParameterResource>?` | `null` | Custom username parameter. When `null`, defaults to `admin`. |
| `password` | `IResourceBuilder<ParameterResource>?` | `null` | Custom password parameter. When `null`, a random password is generated. |

## AddDatabase

Adds a named database as a child resource of a DocumentDB server. Services reference the database resource to get a connection string that includes the database name.

```csharp
var db = builder.AddDocumentDB("documentdb")
                .AddDatabase("mydb");

// Custom database name (resource name differs from database name)
var db = builder.AddDocumentDB("documentdb")
                .AddDatabase("db-resource", databaseName: "actual_database_name");

// Multiple databases on the same server
var server = builder.AddDocumentDB("documentdb");
var ordersDb = server.AddDatabase("orders");
var usersDb = server.AddDatabase("users");
```

| Parameter | Type | Default | Description |
|---|---|---|---|
| `name` | `string` | (required) | Resource name. Used as the connection string name when referenced. |
| `databaseName` | `string?` | Same as `name` | The actual database name in DocumentDB. Defaults to the resource `name` if not specified. |

## WithHostPort

Binds the DocumentDB container to a specific host port instead of a randomly assigned one. Useful for development when you want a predictable port.

```csharp
var server = builder.AddDocumentDB("documentdb")
                    .WithHostPort(10260);
```

| Parameter | Type | Default | Description |
|---|---|---|---|
| `port` | `int?` | `null` (random) | The host port to bind. `null` reverts to a random port. |

## WithDataVolume

Attaches a Docker named volume to persist DocumentDB data across container restarts.

```csharp
// Auto-generated volume name
var server = builder.AddDocumentDB("documentdb")
                    .WithDataVolume();

// Explicit volume name
var server = builder.AddDocumentDB("documentdb")
                    .WithDataVolume(name: "documentdb-data");
```

| Parameter | Type | Default | Description |
|---|---|---|---|
| `name` | `string?` | Auto-generated | Docker volume name. When `null`, a name is generated from the application and resource names. |
| `isReadOnly` | `bool` | `false` | Mount the volume as read-only. |
| `targetPath` | `string?` | `/home/documentdb/postgresql/data` | Path inside the container where the volume is mounted. |

The method also sets the `DATA_PATH` environment variable inside the container to match `targetPath`.

## WithDataBindMount

Mounts a host directory into the container for data persistence. Prefer `WithDataVolume` for most cases; bind mounts are useful when you need direct access to the data files on the host.

```csharp
var server = builder.AddDocumentDB("documentdb")
                    .WithDataBindMount("./data/documentdb");
```

| Parameter | Type | Default | Description |
|---|---|---|---|
| `source` | `string` | (required) | Path on the host machine to mount. |
| `isReadOnly` | `bool` | `false` | Mount as read-only. |

The data is mounted at `/home/documentdb/postgresql/data` inside the container. The `DATA_PATH` environment variable is set accordingly.

## UseTls

Controls whether TLS is included in the generated connection string. TLS is **enabled by default** because the DocumentDB Local container requires TLS connections.

```csharp
// Disable TLS (for example, connecting to a non-TLS endpoint)
var server = builder.AddDocumentDB("documentdb")
                    .UseTls(false);

// Explicitly enable TLS (this is the default)
var server = builder.AddDocumentDB("documentdb")
                    .UseTls(true);
```

| Parameter | Type | Default | Description |
|---|---|---|---|
| `useTls` | `bool` | `true` | Whether to add `tls=true` to the connection string. |

## AllowInsecureTls

Controls whether `tlsInsecure=true` is added to the connection string, which disables certificate validation. This is **enabled by default** so the .NET MongoDB driver can connect to the self-signed certificate used by the DocumentDB Local container.

```csharp
// Require valid certificates (for example, production with real certs)
var server = builder.AddDocumentDB("documentdb")
                    .AllowInsecureTls(false);
```

| Parameter | Type | Default | Description |
|---|---|---|---|
| `allowInsecureTls` | `bool` | `true` | Whether to add `tlsInsecure=true` to the connection string. |

> [!NOTE]
> The extension uses `tlsInsecure=true` rather than `tlsAllowInvalidCertificates=true` because the .NET MongoDB driver does not fully honor `tlsAllowInvalidCertificates` for self-signed certificates and raises `UntrustedRoot` errors. `tlsInsecure=true` disables both certificate validation and hostname verification, which is the correct setting for local development containers.

## Connection string format

The extension generates a MongoDB connection string with the following format:

```
mongodb://<username>:<password>@<host>:<port>[/<database>]?authSource=admin&authMechanism=SCRAM-SHA-256&tls=true&tlsInsecure=true
```

### Breakdown

| Component | Value | Source |
|---|---|---|
| Protocol | `mongodb://` | Always MongoDB wire protocol |
| Username | `admin` (default) or custom | `userName` parameter or default |
| Password | Auto-generated or custom | `password` parameter or generated |
| Host:Port | Allocated by Aspire | Endpoint binding |
| Database | Resource name or `databaseName` | `AddDatabase()` parameter |
| `authSource` | `admin` | Fixed authentication database |
| `authMechanism` | `SCRAM-SHA-256` | DocumentDB authentication |
| `tls` | `true` | Controlled by `UseTls()` |
| `tlsInsecure` | `true` | Controlled by `AllowInsecureTls()` |

## Defaults summary

| Setting | Default Value |
|---|---|
| Container image | `ghcr.io/documentdb/documentdb/documentdb-local` |
| Image tag | `pg17-0.109.0` |
| Container port | `10260` |
| Host port | Random (unless set with `WithHostPort` or `port` parameter) |
| Username | `admin` |
| Password | Auto-generated (no special characters) |
| TLS | Enabled |
| Insecure TLS | Enabled (allows self-signed certificates) |
| Data path (in container) | `/home/documentdb/postgresql/data` |
| Auth mechanism | `SCRAM-SHA-256` |
| Auth database | `admin` |

## Container environment variables

The extension passes these environment variables to the DocumentDB container:

| Variable | Value | Purpose |
|---|---|---|
| `USERNAME` | The configured username | Container creates this user on startup |
| `PASSWORD` | The configured password | Password for the created user |
| `DATA_PATH` | `/home/documentdb/postgresql/data` | Only set when using `WithDataVolume` or `WithDataBindMount` |

## Resource model

The extension defines two resource types:

```
IDistributedApplicationBuilder
  |
  +-- AddDocumentDB("server-name")
        |
        +-- DocumentDBServerResource  (container resource with connection string)
              |
              +-- AddDatabase("db-name")
                    |
                    +-- DocumentDBDatabaseResource  (child resource with connection string)
```

- **DocumentDBServerResource** — Represents the DocumentDB container. Implements `IResourceWithConnectionString`. The server-level connection string does not include a database name.
- **DocumentDBDatabaseResource** — A child resource that represents a specific database on the server. Its connection string includes the database name in the path. This is what services typically reference with `WithReference()`.

## Chaining methods

All configuration methods return the builder, so they can be chained:

```csharp
var db = builder.AddDocumentDB("documentdb")
                .WithHostPort(10260)
                .WithDataVolume()
                .UseTls(true)
                .AllowInsecureTls(true)
                .AddDatabase("mydb");
```
