# Azure Infrastructure (Postgres Flexible Server + AGE + pgvector)

Provisions Azure Database for PostgreSQL Flexible Server preconfigured with the
three extensions this app needs:

- `age` 1.6.0 (Preview) — graph database
- `vector` 0.8.2 — embeddings
- `pg_diskann` 0.6.4 — ANN index

## Files

- [main.bicep](main.bicep) — server, `azure.extensions` allowlist, `shared_preload_libraries=age`, firewall rules, database.
- [main.bicepparam](main.bicepparam) — defaults; reads `PG_ADMIN_PASSWORD` and `PG_CLIENT_IP` from env.
- [deploy.ps1](deploy.ps1) — `az deployment group create` + runs the init SQL via `psql`.

## Quick start

```pwsh
az login
az account set --subscription <sub-id>

$env:PG_ADMIN_PASSWORD = 'StrongP@ssw0rd!'
$env:PG_CLIENT_IP      = (Invoke-RestMethod https://api.ipify.org)

./infra/deploy.ps1 -ResourceGroup rg-noteassistant -Location eastus2
```

The script writes the connection string for `ConnectionStrings:AgeDatabase` at the end.

## Notes

- AGE is **Preview** on Azure Flexible Server. Don't use for production-critical data without testing.
- `shared_preload_libraries=age` requires a server restart — the Bicep deploy triggers it automatically.
- `pg_diskann` is optional; the init script only uses pgvector's `ivfflat`/`hnsw`. Leave it allowlisted in case you want to switch later.
- For private networking use `network.publicNetworkAccess = 'Disabled'` and add a delegated subnet/private endpoint (not included here to keep the template minimal).
