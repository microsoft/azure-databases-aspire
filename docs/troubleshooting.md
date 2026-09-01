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

### "Another DocumentDB container is already using the data directory"

**Symptom:** A container exits immediately after start with:

```text
Error: another DocumentDB container is already using the data directory /data. Refusing to start: ...
```

Or the application fails to start with an `InvalidOperationException` saying two DocumentDB resources mount the same volume or host directory.

**Cause:** Two PostgreSQL instances on one data directory would corrupt it. From `0.116.0` the container claims the directory with an exclusive lock, so the second container refuses to start rather than taking over, and the container that already holds it keeps serving, unaffected. Images at or below `0.114.0` have no such lock — nothing refuses the second start, and the corruption happens silently — which is why the application-model check is stricter for them.

**Solutions:**
1. **Stop whatever already holds the directory.** A leftover `docker run` session, a second AppHost instance, or a container from a previous debug session that was not removed — `docker ps --filter volume=<volume-name>` finds it.
2. **Give each resource its own storage.** Two DocumentDB resources in one application model cannot use the same volume name or bind-mount source as their *data directory*; use `WithDataVolume(name: "<resource>-data")` per resource. Sharing a directory as read-only *input* (`WithInitData(...)`, `WithTlsCertificate(...)`) is fine.
3. **Do not run the AppHost twice against the same volume.** Only one instance can hold the data directory at a time.
4. **`WithExplicitStart()` is not an escape hatch on older images.** The pair is downgraded to a warning only when both resources resolve to a recognised `0.116.0`-or-later tag, where the container itself refuses the overlap. Otherwise it remains a hard failure.

### Read-only data volume or bind mount is rejected

**Symptom:** `WithDataVolume(isReadOnly: true)` or `WithDataBindMount(..., isReadOnly: true)` throws an `ArgumentException`; a read-only mount added with the raw `WithVolume`/`WithBindMount` APIs fails the resource start with an `InvalidOperationException`.

**Cause:** DocumentDB cannot run against a read-only data directory. The entrypoint takes ownership of it, and `initdb` must change its permissions before PostgreSQL can create the cluster or write WAL. Allowed through, the container logs `chown: changing ownership of '/data': Read-only file system` and `initdb: error: could not change permissions of directory "/data": Read-only file system` inside interleaved log streams, then waits a full minute before exiting with the misleading `PostgreSQL failed to start within 60 seconds` banner.

**Solution:** Mount the data directory writable. Read-only mounts are correct for *input* only — `WithInitData(...)` mounts seed scripts read-only at `/init_doc_db.d`, and `WithTlsCertificate(...)` mounts the certificate and key read-only.

### "Directory /data exists but doesn't appear to contain a valid PostgreSQL data directory"

**Symptom:** A container using a bind-mounted data directory logs

```text
Warning: Directory /data exists but doesn't appear to contain a valid PostgreSQL data directory.
Use -c flag to force cleanup and re-initialization, or specify a different directory with -d.
```

then never becomes healthy and exits `1` about a minute later with `PostgreSQL failed to start within 60 seconds`.

**Cause:** The data directory is not empty but does not hold a PostgreSQL cluster (no `PG_VERSION` file). The container refuses to initialise over unrecognised contents rather than deleting them, and PostgreSQL never starts — so the only failure you see is the generic 60-second timeout banner. One stray file is enough. Common culprits are a `.gitkeep` committed so the empty directory survives in source control, a `.DS_Store` written by macOS Finder, or leftovers from an unrelated tool.

**Solutions:**
1. **Empty the host directory** (or point `WithDataBindMount(...)` at a genuinely empty one) and start again. Nothing was deleted, so anything you need is still there.
2. **Keep the directory out of source control** rather than seeding it with a placeholder file — add it to `.gitignore` and let the first run create it.
3. **Prefer `WithDataVolume()`** when you do not need host access to the files: a fresh Docker volume is always empty.

### Initialization scripts do not re-run after being fixed

**Symptom:** A custom initialization script mounted with `WithInitData(...)` failed or was corrected, but later runs log `Custom data already initialized ...; skipping` or `a previous custom data initialization was attempted but its success was not recorded` and the data never appears.

**Cause:** DocumentDB `0.116.0` makes initialization one-shot per data directory. Markers under `<data-path>/.documentdb-local/` (`custom_data_attempted`, `custom_data_succeeded`, `sample_data_initialized`) live inside the persisted data, so they survive restarts, `docker compose down && up`, host reboots, and volume backups. The attempt marker is written *before* the first user script runs, so a non-idempotent script that failed part way is never retried — re-running it against half-seeded data caused restart loops.

**Solution:** Fix the scripts, then start against a **fresh** data directory — a new volume name, or `docker volume rm <name>` / an emptied bind-mount directory. Editing script contents alone never re-triggers initialization. Writing idempotent scripts avoids the problem in the first place.

> [!NOTE]
> On `0.114.0` and earlier the opposite is true: there are no markers, and the requested initialization runs on **every** container start. Against a persisted data directory the same scripts are replayed over data they already seeded, so duplicated documents — not missing ones — are the symptom to expect there. Idempotent scripts (and `WithoutSampleData()` for the built-in import) are the fix.

### Orphaned anonymous volumes accumulate

**Symptom:** `docker system df` shows a growing number of unused local volumes with random 64-character hexadecimal names, roughly the size of a PostgreSQL data directory each.

**Cause:** DocumentDB `0.116.0` and later declare `/data` as an image `VOLUME`. Any run that does not mount storage there gets a fresh anonymous volume, and removing the container without `-v` strands it. Neither Docker nor Aspire can un-declare an image volume. Images at or below `0.114.0` declare no volume and do not produce these.

**Solutions:**
1. **Mount your own storage on `/data`.** `WithDataVolume()` and `WithDataBindMount(...)` do this by default, which suppresses the anonymous volume. A non-default `targetPath` cannot: the declared `/data` volume is still created, and the resource logs a warning saying so (only on images known to declare it — recognised `0.116.0`-or-later tags).
2. **Reclaim what has accumulated:** `docker volume prune` (removes *all* unused local volumes — check `docker volume ls -f dangling=true` first).

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
