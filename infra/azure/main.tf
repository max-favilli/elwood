terraform {
  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.0"
    }
  }
}

provider "azurerm" {
  features {}
}

locals {
  prefix = "${var.project_name}-${var.environment}"
  tags = {
    project     = var.project_name
    environment = var.environment
    managed_by  = "terraform"
  }
}

# ── Existing resources (data sources — read-only, not managed by Terraform) ──

data "azurerm_resource_group" "existing" {
  name = var.resource_group_name
}

data "azurerm_service_plan" "existing" {
  name                = var.app_service_plan_name
  resource_group_name = data.azurerm_resource_group.existing.name
}

data "azurerm_app_configuration" "existing" {
  name                = var.app_configuration_name
  resource_group_name = data.azurerm_resource_group.existing.name
}

data "azurerm_application_insights" "existing" {
  name                = var.application_insights_name
  resource_group_name = data.azurerm_resource_group.existing.name
}

# ── Storage Account (new — for Function App runtime + pipeline blob store) ──

resource "azurerm_storage_account" "elwood" {
  name                     = replace("st${local.prefix}", "-", "")
  resource_group_name      = data.azurerm_resource_group.existing.name
  location                 = var.location
  account_tier             = "Standard"
  account_replication_type = "LRS"
  tags                     = local.tags
}

resource "azurerm_storage_container" "pipelines" {
  name                  = "elwood-pipelines"
  storage_account_id    = azurerm_storage_account.elwood.id
  container_access_type = "private"
}

resource "azurerm_storage_container" "documents" {
  name                  = "elwood-documents"
  storage_account_id    = azurerm_storage_account.elwood.id
  container_access_type = "private"
}

# ── Redis (new — pipeline content cache + route table) ──

locals {
  redis_parts = split("_", var.redis_sku) # e.g., "Basic_C0" → ["Basic", "C0"]
  redis_family   = substr(local.redis_parts[1], 0, 1) # "C"
  redis_capacity = tonumber(substr(local.redis_parts[1], 1, 1)) # 0
}

resource "azurerm_redis_cache" "elwood" {
  name                = "redis-${local.prefix}"
  resource_group_name = data.azurerm_resource_group.existing.name
  location            = var.location
  capacity            = local.redis_capacity
  family              = local.redis_family
  sku_name            = local.redis_parts[0]
  non_ssl_port_enabled = false
  minimum_tls_version  = "1.2"
  tags                 = local.tags
}

# ── Function App (new — HTTP trigger for pipeline execution) ──

resource "azurerm_linux_function_app" "elwood" {
  name                = "func-${local.prefix}"
  resource_group_name = data.azurerm_resource_group.existing.name
  location            = var.location
  service_plan_id     = data.azurerm_service_plan.existing.id

  storage_account_name       = azurerm_storage_account.elwood.name
  storage_account_access_key = azurerm_storage_account.elwood.primary_access_key

  tags = local.tags

  site_config {
    application_stack {
      dotnet_version              = var.dotnet_version
      use_dotnet_isolated_runtime = true
    }

    cors {
      allowed_origins = ["*"]
    }
  }

  app_settings = {
    # Application Insights
    "APPLICATIONINSIGHTS_CONNECTION_STRING" = data.azurerm_application_insights.existing.connection_string

    # App Configuration (secrets + config)
    "Elwood__AppConfiguration"      = data.azurerm_app_configuration.existing.primary_read_key[0].connection_string
    "Elwood__AppConfigurationLabel" = var.environment

    # Redis (pipeline content cache + route table)
    "Elwood__RedisConnection" = azurerm_redis_cache.elwood.primary_connection_string

    # Blob Storage (pipeline store + document store)
    "Elwood__BlobConnection"          = azurerm_storage_account.elwood.primary_connection_string
    "Elwood__PipelinesContainer"      = azurerm_storage_container.pipelines.name
    "Elwood__DocumentsContainer"      = azurerm_storage_container.documents.name

    # Functions runtime
    "FUNCTIONS_WORKER_RUNTIME" = "dotnet-isolated"
  }
}
