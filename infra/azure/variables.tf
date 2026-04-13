# ── Existing resources to reuse ──

variable "resource_group_name" {
  description = "Name of the existing resource group"
  type        = string
}

variable "app_service_plan_name" {
  description = "Name of the existing App Service Plan to host the Function App"
  type        = string
}

variable "app_configuration_name" {
  description = "Name of the existing Azure App Configuration instance"
  type        = string
}

variable "application_insights_name" {
  description = "Name of the existing Application Insights instance"
  type        = string
}

# ── New resources to create ──

variable "project_name" {
  description = "Project name used as prefix for new resources (e.g., 'elwood')"
  type        = string
  default     = "elwood"
}

variable "environment" {
  description = "Environment name (dev, qa, prod)"
  type        = string
  default     = "dev"
}

variable "location" {
  description = "Azure region (must match existing resource group)"
  type        = string
  default     = "westeurope"
}

variable "redis_sku" {
  description = "Redis SKU: Basic_C0 (dev), Standard_C1 (prod)"
  type        = string
  default     = "Basic_C0"
}

variable "dotnet_version" {
  description = ".NET version for the Function App"
  type        = string
  default     = "v10.0"
}
