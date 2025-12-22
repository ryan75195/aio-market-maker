@description('The environment name (dev, staging, prod)')
param environment string = 'dev'

@description('The Azure region for all resources')
param location string = resourceGroup().location

@description('The base name for resources')
param baseName string = 'aiomarketmaker'

@description('SQL Server administrator login')
param sqlAdminLogin string = 'sqladmin'

@secure()
@description('SQL Server administrator password')
param sqlAdminPassword string

@secure()
@description('OpenAI API key')
param openAiApiKey string

@secure()
@description('Pinecone API key')
param pineconeApiKey string

@description('Pinecone index name')
param pineconeIndexName string = 'arbitrage'

@description('Scraper API base URL')
param scraperApiBaseUrl string = ''

@secure()
@description('Scraper API key')
param scraperApiKey string = ''

// Resource naming
var resourceSuffix = '${baseName}-${environment}'
var uniqueSuffix = uniqueString(resourceGroup().id, baseName, environment)
var storageAccountName = 'st${replace(baseName, '-', '')}${uniqueSuffix}'
var functionAppName = 'func-${resourceSuffix}'
var appInsightsName = 'appi-${resourceSuffix}'
var logAnalyticsName = 'log-${resourceSuffix}'
var sqlServerName = 'sql-${resourceSuffix}'
var sqlDatabaseName = 'etl'

// Common tags
var tags = {
  Environment: environment
  Application: baseName
  ManagedBy: 'Bicep'
}

// Application Insights
module appInsights 'modules/app-insights.bicep' = {
  name: 'appInsightsDeployment'
  params: {
    name: appInsightsName
    location: location
    tags: tags
    logAnalyticsWorkspaceName: logAnalyticsName
  }
}

// SQL Database
module sqlDatabase 'modules/sql-database.bicep' = {
  name: 'sqlDatabaseDeployment'
  params: {
    serverName: sqlServerName
    databaseName: sqlDatabaseName
    location: location
    tags: tags
    adminLogin: sqlAdminLogin
    adminPassword: sqlAdminPassword
  }
}

// Function App
module functionApp 'modules/function-app.bicep' = {
  name: 'functionAppDeployment'
  params: {
    name: functionAppName
    location: location
    tags: tags
    storageAccountName: storageAccountName
    appInsightsConnectionString: appInsights.outputs.connectionString
    sqlConnectionString: sqlDatabase.outputs.sqlConnectionStringWithPassword
    openAiApiKey: openAiApiKey
    pineconeApiKey: pineconeApiKey
    pineconeIndexName: pineconeIndexName
    scraperApiBaseUrl: scraperApiBaseUrl
    scraperApiKey: scraperApiKey
  }
}

// Outputs
output functionAppName string = functionApp.outputs.functionAppName
output functionAppHostName string = functionApp.outputs.functionAppHostName
output appInsightsConnectionString string = appInsights.outputs.connectionString
output sqlServerFqdn string = sqlDatabase.outputs.serverFqdn
