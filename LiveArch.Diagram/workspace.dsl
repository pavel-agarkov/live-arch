workspace EnterpriseArchitecture {
    description "An example of an enterprise architecture model using Structurizr DSL with Microsoft Azure components"

    model {
        orderService = softwareSystem "Order Microservice" {
            orderDb = container "Order Database" {
                tags "Microsoft Azure - Production Ready Database"
                technology "azurerm_sql_database"
            }
            orderApi = container "Order API" {
                tags "Microsoft Azure - App Services"
                technology "azurerm_app_service"
                -> orderDb "Uses"
                component "Order API Component" {
                    technology ".NET 8"
                }
            }
        }

        development = deploymentEnvironment "Development" {
        }

        production = deploymentEnvironment "Production" {
            deploymentNode "Azure Subscription" {
                tags "Microsoft Azure - Subscriptions"
                technology "data_azurerm_subscription"
                properties {
                    subscription_id "$PROD_SUBSCRIPTION_ID"
                    tenant_id       "$PROD_TENANT_ID"
                }

                deploymentNode "Resource Group" {
                    tags "Microsoft Azure - Resource Groups"
                    technology "data_azurerm_resource_group"
                    properties {
                        name     "$PROD_RESOURCE_GROUP_NAME"
                        location "$PROD_LOCATION_NAME"
                    }

                    prodKeyVault = infrastructureNode "Key Vault" {
                        tags "Microsoft Azure - Key Vaults"
                        technology "data_azurerm_key_vault"
                        properties {
                            name    "$PROD_KEY_VAULT_NAME"
                        }
                    }

                    prodMi = infrastructureNode "Managed Identity" {
                        tags "Microsoft Azure - Managed Identities"
                        technology "azurerm_user_assigned_identity"
                        properties {
                            name   "prod-app-service-mi"
                        }
                    }

                    infrastructureNode "Key Vault Role Assignment" {
                        tags "Microsoft Azure - Entra Managed Identities"
                        technology "azurerm_role_assignment"
                        properties {
                            name                 "prod-app-service-mi-keyvault-reader"
                            role_definition_name "Key Vault Reader"
                        }
                        -> prodMi "Identity"
                        -> prodKeyVault "Resource"
                    }

                    deploymentNode "Virtual Network" {
                        tags "Microsoft Azure - Virtual Networks"
                        technology "data_azurerm_virtual_network"
                        properties {
                            name    "$PROD_VNET_NAME"
                        }

                        deploymentNode "App Service Plan" {
                            tags "Microsoft Azure - App Service Plans"
                            technology "azurerm_app_service_plan"
                            properties {
                                name    "prod-app-service-plan"
                            }

                            containerInstance orderApi mainProdRg {
                                -> prodMi "Identity"
                                properties {
                                    name    "prod-order-api"
                                }
                            }
                        }
                    }

                    deploymentNode "SQL Server" {
                        tags "Microsoft Azure - Azure SQL"
                        technology "data_azurerm_sql_server"
                        properties {
                            name   "$PROD_SQL_SERVER_NAME"
                        }
                        containerInstance orderDb mainProdRg {
                            properties {
                                name    "prod-order-db"
                            }
                        }
                    }
                }
            }
        }
    }

    views {

        container orderService order-service {
            include *
            autolayout
        }

        component orderApi order-api {
            include *
            autolayout
        }

        deployment * production prod {
            include *
            autolayout
        }

        theme microsoft-azure-2025.11/theme.json
    }

}
