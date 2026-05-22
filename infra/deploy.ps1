<#
.SYNOPSIS
  Provisions Azure Database for PostgreSQL Flexible Server with AGE + pgvector + pg_diskann,
  then runs the init SQL to create the graph schema.

.PREREQUISITES
  - Azure CLI logged in (az login) and correct subscription selected.
  - psql.exe on PATH (install: winget install PostgreSQL.PostgreSQL.16 -- or use Chocolatey).

.EXAMPLE
  $env:PG_ADMIN_PASSWORD = 'StrongP@ssw0rd!'
  $env:PG_CLIENT_IP      = (Invoke-RestMethod https://api.ipify.org)
  ./infra/deploy.ps1 -ResourceGroup rg-noteassistant -Location eastus2
#>

[CmdletBinding()]
param(
  [Parameter(Mandatory = $true)] [string] $ResourceGroup,
  [Parameter(Mandatory = $true)] [string] $Location,
  [string] $ParamFile = "$PSScriptRoot/main.bicepparam",
  [string] $InitSql   = "$PSScriptRoot/../NoteAssistant.KnowledgeGraph.Backend/Deployment/init/01-age-init.sql"
)

$ErrorActionPreference = 'Stop'

if (-not $env:PG_ADMIN_PASSWORD) {
  throw "Set `$env:PG_ADMIN_PASSWORD before running (it is read by main.bicepparam)."
}

Write-Host "==> Ensuring resource group $ResourceGroup in $Location" -ForegroundColor Cyan
az group create --name $ResourceGroup --location $Location --output none

Write-Host "==> Deploying Bicep" -ForegroundColor Cyan
$deployment = az deployment group create `
  --resource-group $ResourceGroup `
  --parameters $ParamFile `
  --query 'properties.outputs' `
  --output json | ConvertFrom-Json

$fqdn     = $deployment.fullyQualifiedDomainName.value
$dbName   = $deployment.databaseName.value
$adminUser = 'pgadmin'

Write-Host "==> Server: $fqdn  DB: $dbName" -ForegroundColor Green

if (-not (Get-Command psql -ErrorAction SilentlyContinue)) {
  Write-Warning "psql not found on PATH. Skipping init SQL. Run it manually:"
  Write-Host "  psql `"host=$fqdn port=5432 dbname=$dbName user=$adminUser sslmode=require`" -f `"$InitSql`""
  return
}

Write-Host "==> Running init SQL ($InitSql)" -ForegroundColor Cyan
$env:PGPASSWORD = $env:PG_ADMIN_PASSWORD
try {
  psql "host=$fqdn port=5432 dbname=$dbName user=$adminUser sslmode=require" -v ON_ERROR_STOP=1 -f $InitSql
} finally {
  Remove-Item Env:PGPASSWORD
}

$connString = "Host=$fqdn;Port=5432;Database=$dbName;Username=$adminUser;Password=<your-password>;SslMode=Require;Trust Server Certificate=true"
Write-Host ""
Write-Host "==> Done. Set this in appsettings (or user-secrets):" -ForegroundColor Green
Write-Host "    ConnectionStrings:AgeDatabase = $connString"
