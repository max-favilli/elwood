output "function_app_name" {
  description = "Name of the deployed Function App"
  value       = azurerm_linux_function_app.elwood.name
}

output "function_app_url" {
  description = "Default hostname of the Function App"
  value       = "https://${azurerm_linux_function_app.elwood.default_hostname}"
}

output "storage_account_name" {
  description = "Storage account for pipelines and documents"
  value       = azurerm_storage_account.elwood.name
}

output "redis_hostname" {
  description = "Redis cache hostname"
  value       = azurerm_redis_cache.elwood.hostname
}

output "redis_connection_string" {
  description = "Redis primary connection string"
  value       = azurerm_redis_cache.elwood.primary_connection_string
  sensitive   = true
}

output "blob_connection_string" {
  description = "Storage account connection string"
  value       = azurerm_storage_account.elwood.primary_connection_string
  sensitive   = true
}
