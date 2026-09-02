# Troubleshooting

Common issues when using `Aspire.Hosting.DocumentDB` and how to resolve them.

## Docker issues

### Docker is not running

**Symptom:** The DocumentDB resource fails to start with an error about the Docker daemon.

**Solution:** Start Docker Desktop or your Docker daemon. Aspire requires a running Docker runtime to start container resources.

```bash
# Verify Docker is running
docker info
```

### Container image pull fails

**Symptom:** Timeout or network error pulling `ghcr.io/documentdb/documentdb/documentdb-local`.

**Solution:**
1. Verify network connectivity: `docker pull ghcr.io/documentdb/documentdb/documentdb-local:pg17-0.116.0`
2. Check if you need to authenticate to GitHub Container Registry (public images should not require auth)
3. If behind a corporate proxy, configure Docker's proxy settings

### Container fails to start

**Symptom:** The resource shows as "Failed" in the Aspire dashboard.

**Solution:** Check the container logs in the Aspire dashboard or via Docker:

```bash
# Find the container
docker ps -a | grep documentdb

# View logs
docker logs <container-id>
```

Common causes:
- Port already in use (see [Port conflicts](#port-conflicts) below)
- Insufficient Docker resources (memory, disk)
- Corrupted data volume (remove the volume and restart)
- A username beginning with a reserved DocumentDB `0.116.0` prefix (`documentdb`, `citus`, `pg`, or `internal_role`)

### The container image changed after it was sealed

**Symptom:** Startup fails with:

```
DocumentDB resource 'documentdb' changed the container image it will run after this
package sealed it. Aspire snapshots the image into the DCP container spec while it
prepares the application's containers, ...
Sealed reference: 'ghcr.io/documentdb/documentdb/documentdb-local:pg17-0.111.0'.
Current reference: 'ghcr.io/documentdb/documentdb/documentdb-local:pg17-0.116.0'.
```

**Cause:** Aspire composes the image reference once, while it prepares the
application's containers, and writes it into the DCP container spec. That happens
before endpoints are allocated and before `BeforeResourceStartedEvent` is published,
so an image chosen after that point is only visible to the application model — the
container runtime still launches the earlier one. Rather than let the model and the
running container disagree in either direction, the integration refuses the run.

**Solution:** Choose the image while the application model is being built:

```csharp
var db = builder.AddDocumentDB("documentdb")
                .WithDocumentDBVersion(DocumentDBVersion.V0_116_0);
```

`WithImageTag(...)`, `WithImageRegistry(...)`, `WithImage(...)`, `WithImageSHA256(...)`
and `WithDockerfile(...)` are all still honoured anywhere during model building,
including from a `BeforeStartEvent` subscriber. Only a change made from a
`ResourceEndpointsAllocatedEvent` / `BeforeResourceStartedEvent` subscriber, or from a
lifecycle hook that runs after this package's, is refused. Publish mode is unaffected:
no container is prepared, so the manifest is written from the model as usual.

## Connection issues

### TLS certificate errors

**Symptom:** `MongoAuthenticationException` or `SslPolicyErrors.RemoteCertificateChainErrors` when connecting from your service.

**Cause:** The DocumentDB container uses a self-signed TLS certificate. The .NET MongoDB driver rejects it unless `tlsInsecure=true` is in the connection string.

**Solution:** This should work automatically. The extension adds `tls=true&tlsInsecure=true` to the connection string by default. If you see this error:

1. Verify you are referencing the resource correctly: `.WithReference(db)` where `db` is the database resource from `AddDatabase()`.
2. If you manually constructed a connection string, add `tlsInsecure=true` (not `tlsAllowInvalidCertificates=true` — the .NET driver does not fully honor the latter for self-signed certificates).
3. If connecting from `mongosh` or another MongoDB CLI tool outside of Aspire, use this format:
   ```
   mongodb://admin:<password>@localhost:<port>/?authSource=admin&authMechanism=SCRAM-SHA-256&tls=true&tlsAllowInvalidCertificates=true
   ```
   `mongosh` accepts `tlsAllowInvalidCertificates=true`. For .NET applications, prefer `tlsInsecure=true`.

### Plain (non-TLS) connections are refused

**Symptom:** `UseTls(false)` is set, but the client or health check cannot connect to the container.

**Causes and solutions:**
1. **Container image is older than `0.114.0`.** Images up to and including `0.113.0` always enforced TLS on the gateway and rejected plain connections, regardless of the `TLS_MODE` setting. Use the default (`0.116.0`) image tag, another `0.114.0` or newer image, or keep TLS enabled in the connection string.
2. **`TLS_MODE` is set to `requireTLS`.** This makes the container reject plain connections by design, which contradicts `UseTls(false)`. Remove the environment variable to fall back to the default `allowTLS` (accepts both plain and TLS connections), or re-enable TLS with `UseTls(true)`.

   ```csharp
   // Only if you want plain connections rejected — do NOT combine with UseTls(false).
   var server = builder.AddDocumentDB("documentdb")
                       .WithEnvironment("TLS_MODE", "requireTLS");
   ```

   `TLS_MODE=disabled` is not a plain-only mode; the gateway treats it exactly like `allowTLS` and the entrypoint logs a warning.
3. **`TLS_MODE` is misspelled.** The value is case-sensitive (`allowTLS`, `requireTLS`, `disabled`). The entrypoint rejects anything else and exits, so the container never starts — check the container logs for the rejected value.

### Connection refused / timeout

**Symptom:** `MongoConnectionException` with "connection refused" or timeout.

**Causes and solutions:**
1. **Container not ready yet.** DocumentDB takes a few seconds to initialize. Use `.WaitFor(db)` in your AppHost to improve startup ordering.

> [!IMPORTANT]
> This integration registers an authenticated MongoDB `ping` health check. It proves that the
> gateway is accepting authenticated requests, but on DocumentDB `0.116.0` the gateway can become
> reachable before one-shot custom or sample initialization has completed. A dependent resource
> that requires seeded data should still retry that data access rather than treating `WaitFor` as
> proof that initialization scripts have finished.
2. **Wrong port.** By default, Aspire assigns a random host port. Do not hardcode ports in your service — use `WithReference()` to inject the connection string automatically.
3. **Firewall or network issue.** If running Docker in a VM or WSL2, ensure port forwarding is configured.

### Authentication failures

**Symptom:** `MongoAuthenticationException: Unable to authenticate`.

**Causes and solutions:**
1. **Wrong credentials.** The extension generates a random password on each run (unless you provide fixed parameters). Always use the Aspire-injected connection string rather than hardcoding credentials. If you persist data with `WithDataVolume()`/`WithDataBindMount()`, a generated password also stops matching the role stored in the volume — see [Authentication fails on the second run after adding a data volume](#authentication-fails-on-the-second-run-after-adding-a-data-volume).
2. **Auth mechanism mismatch.** DocumentDB uses `SCRAM-SHA-256`. The connection string includes `authMechanism=SCRAM-SHA-256` automatically. If you construct your own connection string, include this parameter.
3. **Auth database.** Credentials are created in the `admin` database. The connection string includes `authSource=admin` automatically.

## Port conflicts

**Symptom:** `Bind for 0.0.0.0:10260 failed: port is already allocated`.

**Solution:**
1. By default, Aspire uses a random port, so this only happens if you used `WithHostPort(10260)` or `port: 10260`.
2. Either remove the fixed port to let Aspire pick a random one, or stop whatever is using port 10260:
   ```bash
   # Find what's using the port
   lsof -i :10260   # macOS/Linux
   netstat -ano | findstr :10260   # Windows
   ```

## Wrong resource reference

**Symptom:** MongoDB operations fail with errors about missing database name, or data is written to an unexpected database.

**Cause:** You are referencing the *server* resource instead of the *database* resource. The server connection string does not include a database name in the path.

**Solution:** Always reference the database resource returned by `AddDatabase()`:

```csharp
var server = builder.AddDocumentDB("documentdb");
var db = server.AddDatabase("mydb");

builder.AddProject<Projects.MyService>("myservice")
       // Correct -- connection string includes /mydb
       .WithReference(db);

       // Wrong -- connection string has no database name
       // .WithReference(server);
```

## Data persistence

### Data lost after container restart

**Symptom:** All documents disappear when the Aspire application or Docker restarts.

**Cause:** By default, DocumentDB stores data inside the container filesystem. This storage is ephemeral.

**Solution:** Use `WithDataVolume()` to persist data in a Docker named volume, and pin the credentials at the same time (see the next entry for why):

```csharp
var userName = builder.AddParameter("documentdb-user");
var password = builder.AddParameter("documentdb-password", secret: true);

var server = builder.AddDocumentDB("documentdb", userName: userName, password: password)
                    .WithDataVolume();
```

Or use `WithDataBindMount()` to persist data to a specific host directory:

```csharp
var server = builder.AddDocumentDB("documentdb", userName: userName, password: password)
                    .WithDataBindMount("./data/documentdb");
```

### Authentication fails on the second run after adding a data volume

**Symptom:** The first run works. Every later run fails with `MongoAuthenticationException: Unable to authenticate using sasl protocol mechanism SCRAM-SHA-256` and an inner `Command saslContinue failed: Invalid key`. Removing the volume "fixes" it — and loses the data.

**Cause:** The container hashes the configured password into a PostgreSQL role the first time it initialises a data directory, and that role is stored in the volume. `AddDocumentDB` generates a random password when you do not supply one, so the next run presents a password that no longer matches the persisted role. The data is intact; the credentials no longer open it.

**Solutions:**
1. **Supply explicit credential parameters** (the fix — see [Data lost after container restart](#data-lost-after-container-restart) above). Persisted data needs a password whose lifetime you control.
2. **Already stuck with a volume you want to keep?** Change the role's password through the PostgreSQL backend instead of discarding the volume: enable [`WithPostgresEndpoint()`](configuration.md#withpostgresendpoint), connect with the credentials the volume was created with, and `ALTER ROLE ... PASSWORD ...`.
3. **Do not need the data?** `docker volume rm <name>` and start again with pinned credentials.

### Corrupted data volume

**Symptom:** Container fails to start with errors about `PG_VERSION` or data directory corruption.

**Solution:** Remove the existing volume and let DocumentDB recreate it:

```bash
# Find and remove the volume
docker volume ls | grep documentdb
docker volume rm <volume-name>
```

## Debugging with mongosh

You can connect to the running DocumentDB container directly using `mongosh` for debugging:

```bash
# Find the allocated port from the Aspire dashboard, or:
docker ps | grep documentdb

# Connect with mongosh
mongosh "mongodb://admin:<password>@localhost:<port>/?authSource=admin&authMechanism=SCRAM-SHA-256&tls=true&tlsAllowInvalidCertificates=true"
```

Replace `<password>` and `<port>` with the values from the Aspire dashboard (click on the resource to see its connection string).
For `mongosh`, `tlsAllowInvalidCertificates=true` matches the upstream DocumentDB documentation. For .NET applications, use the Aspire-generated connection string, which uses `tlsInsecure=true`.

### Useful `mongosh` commands

```javascript
// List databases
show dbs

// Switch to your database
use mydb

// List collections
show collections

// Find documents
db.mycollection.find()

// Check server status
db.runCommand({ ping: 1 })
```

## Viewing container logs

DocumentDB container logs can help diagnose startup and runtime issues:

1. **Aspire Dashboard:** Click on the DocumentDB resource and switch to the "Logs" tab.
2. **Docker CLI:**
   ```bash
   docker ps | grep documentdb
   docker logs <container-id>

   # Follow logs in real-time
   docker logs -f <container-id>
   ```

## Known limitations

- **Health readiness is gateway readiness.** The built-in authenticated MongoDB health check does
  not prove that DocumentDB `0.116.0` one-shot initialization scripts have completed.
- **No built-in backup/restore.** For development data, use `WithDataVolume()` for persistence. For important data, use `mongodump` / `mongorestore` manually.
- **Single server only.** The extension does not support replica sets or sharded clusters. It runs a single DocumentDB container intended for local development.
