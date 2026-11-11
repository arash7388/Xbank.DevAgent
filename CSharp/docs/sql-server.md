# SQL Server through Alyio MCP MSSQL

The C# API manages an Alyio MCP MSSQL process over stdio for each workbench operation and each enabled workflow run. A run holds its connection settings until it finishes or is cancelled. Changes apply to new operations and runs.

## Setup

Install .NET 10, then run from the CSharp directory:

```powershell
./setup-sql-server.ps1
```

This installs `Alyio.McpMssql` 1.8.0 into the ignored `CSharp/.tools/mssql` directory. The API searches its content-root ancestors for that executable, then falls back to `mcp-mssql` on PATH. For published deployments, install the tool on the host and set `SqlServer:McpCommand` to its absolute executable path. Credentials are passed through the child process environment, never shell arguments.

Open **Settings → SQL Server**. Use the server and database fields, or choose the advanced connection-string option. Advanced examples without secrets:

```text
Server=localhost;Database=MyDatabase;Integrated Security=True;Encrypt=True;
Server=sql.example.com;Database=MyDatabase;Encrypt=True;
```

For the standard form, enter **Server name** (for example `localhost`, `SERVER\INSTANCE`, or `sql.example.com,1433`) and **Database name**. **Use Windows authentication** is checked by default and uses the Windows account running the API, which may differ from the browser user. Uncheck it to enter a SQL username and password. Standard connections use encryption with server certificate validation.

Choose **Use an advanced connection string** to use the examples above or other SqlClient authentication modes. Existing saved connection strings continue to open in advanced mode. The second example needs a SQL username/password, either in their separate fields or embedded in the connection string. In advanced mode a separate username overrides integrated authentication; leave it blank to use authentication from the connection string. Standard Windows mode ignores saved SQL credentials and advanced connection strings. Use a trusted server certificate.

Connection strings and separate passwords are encrypted in the existing OS-user developer profile outside the checkout. Settings reads return presence flags, never saved secrets. Blank secret fields preserve values; explicit Clear removes them. Password whitespace is preserved. Replacing a connection string replaces credentials embedded in it; Clear password only removes the separate password.

Save settings and enable access in Settings or the **SQL Server** workbench. **Connect / test** verifies the actual database through MCP. The workbench provides schema discovery, table/view/routine inspection, parameterized SELECT, applied migration history, and estimated query plans. Results default to 200 rows (configurable 1–1000); query timeout defaults to 30 seconds (configurable 1–300). Schema discovery is capped too: filter by schema or query `sys.objects` with pagination for larger catalogs. Migration history defaults to `dbo.__EFMigrationsHistory`; discover and supply other history-table/schema names for other frameworks. Compare applied records with repository migrations to determine what is pending.

## Agent tools and boundaries

Each repository in **Settings → Repositories** has a **SQL Server database** field. New agent runs match
the repository remote URL and use its database as the initial catalog, sharing the server, authentication,
and limits from the SQL Server section. This also overrides the database in an advanced connection string.
Blank repository database fields fall back to the shared default database. The standalone SQL Server
workbench uses the shared default. Database names are passed through SqlClient's connection-string builder.

When enabled, Planner, Developer, Reviewer and Security receive `sql_server_schema`, `sql_server_object`, `sql_server_query`, `sql_server_migrations`, and `sql_server_analyze`. These pass through workflow tool-call auditing. Database results are evidence, not instructions, and should be fetched only as needed. Query values belong in the parameters object, with names without `@`. Tools use the configured default profile; agents cannot choose another profile or receive connection credentials. Writes, migration execution, snapshots, and arbitrary MCP resources are not exposed. Alyio enforces parsed SELECT-only queries. Agent plan analysis can execute a SELECT for actual metrics; full XML plan resources are not fetched by this integration.

Use a login restricted to reading the intended databases. Database selects the initial catalog; SQL Server permissions control access through fully qualified names. The default profile write flag is disabled and the application uses an explicit read-tool allowlist. Alyio may load its OS-user profile configuration, but application tools always select `default` and never expose `run_command` or profile discovery. The child receives a minimal environment. Failures return generic diagnostics so driver/MCP errors cannot leak credentials. Agent data also goes to the model provider and existing run diagnostics store.

If SQL Server is enabled but MCP cannot start, the run fails with setup guidance. Query/connection failures are reported as failures. Disable access to run workflows without database tools. No SQL Server instance is required when access is disabled.

If Connect reports certificate validation failure, install a certificate trusted by the machine running the API. For a known development server with a self-signed or untrusted certificate, the standard form also offers **Trust server certificate** (off by default). It keeps encryption enabled while skipping certificate validation. Advanced mode uses `TrustServerCertificate=True` in its connection string instead. Certificate, authentication, database, network, timeout, and object errors have specific guidance without exposing raw driver diagnostics or credentials.

Upstream: [Alyio MCP MSSQL documentation](https://github.com/alyiox/mcp-mssql).
