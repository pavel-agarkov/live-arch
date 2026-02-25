workspace EnterpriseArchitecture {
    description "An example of an enterprise architecture model using Structurizr DSL with Microsoft Azure components"

    model {
        orderService = softwareSystem "Order Microservice" {
            orderDb = container "Order Database" {
                tags "Microsoft Azure - Production Ready Database"
                technology "azure-native:sql:Database"
            }
            orderApi = container "Order API" {
                tags "Microsoft Azure - App Services"
                technology "azure-native:web:WebApp"
                properties {
                    buildTechnology "docker-build:index:Image"
                    context.location "../LiveArch.OrderApi/"
                    dockerfile.location "../.Dockerfile"
                    identity.type   "SystemAssigned"
                    push "true"
                }
                -> orderDb "uses"
            }
        }

        development = deploymentEnvironment "Development" {
        }

        production = deploymentEnvironment "Production" {
            deploymentNode "Resource Group" {
                tags "Microsoft Azure - Resource Groups"
                technology "azure-native:resources:getResourceGroup"
                properties {
                    resourceGroupName     "$PROD_RESOURCE_GROUP_NAME"
                }

                prodKeyVault = infrastructureNode "Key Vault" {
                    tags "Microsoft Azure - Key Vaults"
                    technology "azure-native:keyvault:getVault"
                    properties {
                        vaultName    "$PROD_KEY_VAULT_NAME"
                    }
                }

                prodMi = infrastructureNode "Managed Identity" {
                    tags "Microsoft Azure - Managed Identities"
                    technology "azure-native:managedidentity:UserAssignedIdentity"
                    properties {
                        var "prod-order-service-mi"
                        resourceName   "prod-order-service-mi"
                    }
                }

                infrastructureNode "Key Vault Access Policy" {
                    tags "Microsoft Azure - Entra Managed Identities"
                    technology "azure-native:keyvault:AccessPolicy"
                    properties {
                        var "prod-order-service-kv-access-policy"
                        policy.tenantId    "$PROD_TENANT_ID"
                        policy.permissions.secrets  "get, list"
                    }
                    -> prodMi "principal" {
                        properties {
                            source "principalId"
                            target "policy.objectId"
                        }
                    }
                    -> prodKeyVault "vault" {
                        properties {
                            source "name"
                            target "vaultName"
                        }
                    }
                }

                deploymentNode "Virtual Network" {
                    tags "Microsoft Azure - Virtual Networks"
                    technology "azure-native:network:getVirtualNetwork"
                    properties {
                        virtualNetworkName    "$PROD_VNET_NAME"
                    }

                    deploymentNode "App Service Plan" {
                        tags "Microsoft Azure - App Service Plans"
                        technology "azure-native:web:getAppServicePlan"
                        properties {
                            name    "prod-app-service-plan"
                        }

                        containerInstance orderApi mainProdRg {
                            properties {
                                var "prod-order-api"
                                name            "prod-order-api"
                                identity.type   "UserAssigned"
                            }
                            -> prodMi "identity" {
                                properties {
                                    source  "id"
                                    target  "identity.userAssignedIdentities"
                                }
                            }
                        }
                    }
                }

                deploymentNode "SQL Server Registration" {
                    tags "Microsoft Azure - SQL Server Registries"
                    technology "azure-native:azuredata:getSqlServerRegistration"
                    properties {
                        sqlServerRegistrationName   "$PROD_SQL_SERVER_REGISTRATION_NAME"
                    }
                    deploymentNode "SQL Server" {
                        tags "Microsoft Azure - Azure SQL"
                        technology "azure-native:azuredata:getSqlServer"
                        properties {
                            sqlServerName   "$PROD_SQL_SERVER_NAME"
                        }
                        deploymentNode "Elastic Pool" {
                            tags "Microsoft Azure - SQL Elastic Pools"
                            technology "azure-native:sql:getElasticPool"
                            properties {
                                elasticPoolName   "$PROD_SQL_ELASTIC_POOL_NAME"
                            }
                            containerInstance orderDb mainProdRg {
                                properties {
                                    var "prod-order-db"
                                    name    "prod-order-db"
                                }
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
