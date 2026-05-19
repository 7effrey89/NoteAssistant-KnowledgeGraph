using './main.bicep'

param namePrefix = 'noteassistant'
param administratorLogin = 'pgadmin'
// Set via env var: $env:BICEP_PARAM_administratorLoginPassword = '...'
param administratorLoginPassword = readEnvironmentVariable('PG_ADMIN_PASSWORD', '')
param postgresVersion = '18'
param skuName = 'Standard_B1ms'
param skuTier = 'Burstable'
param storageSizeGB = 32
param databaseName = 'noteassistant'
// Override at deploy time with your public IP.
param clientIpAddress = readEnvironmentVariable('PG_CLIENT_IP', '0.0.0.0')
