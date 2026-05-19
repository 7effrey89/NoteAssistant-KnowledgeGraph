@description('Name prefix for the Azure Database for PostgreSQL Flexible Server. Final name will be <prefix>-pg.')
param namePrefix string

@description('Azure region for the server.')
param location string = resourceGroup().location

@description('Administrator login name.')
param administratorLogin string

@description('Administrator login password.')
@secure()
param administratorLoginPassword string

@description('PostgreSQL major version. 18 matches Azure default / local dev image.')
@allowed([ '14', '15', '16', '17', '18' ])
param postgresVersion string = '18'

@description('SKU name, e.g. Standard_B1ms (Burstable), Standard_D2ds_v5 (General Purpose).')
param skuName string = 'Standard_B1ms'

@description('SKU tier: Burstable, GeneralPurpose, or MemoryOptimized.')
@allowed([ 'Burstable', 'GeneralPurpose', 'MemoryOptimized' ])
param skuTier string = 'Burstable'

@description('Storage size in GB.')
param storageSizeGB int = 32

@description('Database name to create.')
param databaseName string = 'noteassistant'

@description('Client IPv4 address allowed to connect (your dev workstation). Use 0.0.0.0 to disable.')
param clientIpAddress string = '0.0.0.0'

var serverName = '${namePrefix}-pg'

resource postgres 'Microsoft.DBforPostgreSQL/flexibleServers@2024-08-01' = {
  name: serverName
  location: location
  sku: {
    name: skuName
    tier: skuTier
  }
  properties: {
    version: postgresVersion
    administratorLogin: administratorLogin
    administratorLoginPassword: administratorLoginPassword
    storage: {
      storageSizeGB: storageSizeGB
      autoGrow: 'Enabled'
    }
    backup: {
      backupRetentionDays: 7
      geoRedundantBackup: 'Disabled'
    }
    highAvailability: {
      mode: 'Disabled'
    }
    network: {
      publicNetworkAccess: 'Enabled'
    }
  }
}

// Allowlist age, vector, pg_diskann so CREATE EXTENSION works.
resource extensionsParam 'Microsoft.DBforPostgreSQL/flexibleServers/configurations@2024-08-01' = {
  parent: postgres
  name: 'azure.extensions'
  properties: {
    value: 'age,vector,pg_diskann'
    source: 'user-override'
  }
}

// AGE requires its library to be preloaded (per Microsoft Learn note).
resource preloadParam 'Microsoft.DBforPostgreSQL/flexibleServers/configurations@2024-08-01' = {
  parent: postgres
  name: 'shared_preload_libraries'
  properties: {
    value: 'age'
    source: 'user-override'
  }
  dependsOn: [ extensionsParam ]
}

// Optional: allow a single client IP so you can run the bootstrap script.
resource firewallClient 'Microsoft.DBforPostgreSQL/flexibleServers/firewallRules@2024-08-01' = if (clientIpAddress != '0.0.0.0') {
  parent: postgres
  name: 'allow-client'
  properties: {
    startIpAddress: clientIpAddress
    endIpAddress: clientIpAddress
  }
}

// Allow Azure-internal services (App Service, Functions, etc.) to connect.
resource firewallAzure 'Microsoft.DBforPostgreSQL/flexibleServers/firewallRules@2024-08-01' = {
  parent: postgres
  name: 'allow-azure-services'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

resource database 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2024-08-01' = {
  parent: postgres
  name: databaseName
  properties: {
    charset: 'UTF8'
    collation: 'en_US.utf8'
  }
  dependsOn: [ preloadParam ]
}

output serverName string = postgres.name
output fullyQualifiedDomainName string = postgres.properties.fullyQualifiedDomainName
output databaseName string = databaseName
output connectionStringTemplate string = 'Host=${postgres.properties.fullyQualifiedDomainName};Port=5432;Database=${databaseName};Username=${administratorLogin};Password=<password>;SslMode=Require;Trust Server Certificate=true'
