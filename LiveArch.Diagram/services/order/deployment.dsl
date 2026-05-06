orderRg = deploymentNode "Order Resource Group" {
    tags "Microsoft Azure - Resource Groups"
    technology "azure-native:resources:getResourceGroup"
    properties {
        resourceGroupName     ${RESOURCE_GROUP_NAME}
    }
    deploymentNode "SQL Server Registration" {
        tags "Microsoft Azure - SQL Server Registries"
        technology "azure-native:azuredata:getSqlServerRegistration"
        properties {
            sqlServerRegistrationName   ${SQL_SERVER_REGISTRATION_NAME}
        }
        deploymentNode "SQL Server" {
            tags "Microsoft Azure - Azure SQL"
            technology "azure-native:azuredata:getSqlServer"
            properties {
                sqlServerName   ${SQL_SERVER_NAME}
            }
            deploymentNode "Elastic Pool" {
                tags "Microsoft Azure - SQL Elastic Pools"
                technology "azure-native:sql:getElasticPool"
                properties {
                    elasticPoolName   ${SQL_ELASTIC_POOL_NAME}
                }
                containerInstance orderDb {
                    properties {
                        var "order-db"
                        name    ${ENV}-order-db
                    }
                }
            }
        }
    }
    prodKeyVault = infrastructureNode "Key Vault" {
        tags "Microsoft Azure - Key Vaults"
        technology "azure-native:keyvault:getVault"
        properties {
            vaultName    ${KEY_VAULT_NAME}
        }
    }
    prodMi = infrastructureNode "Managed Identity" {
        tags "Microsoft Azure - Managed Identities"
        technology "azure-native:managedidentity:UserAssignedIdentity"
        properties {
            var "order-service-mi"
            resourceName   ${ENV}-order-service-mi
        }
    }
    saList = infrastructureNode "Storage Accounts" {
        tags "Microsoft Azure - App Configuration"
        technology "azure-native:appconfiguration:getKeyValue"
        properties {
            configStoreName   ${APP_CONFIG_NAME}
            keyValueName      "storageAccounts"
        }
    }
    saName = deploymentNode "Foreach Storage Account in Config" {
        technology "foreach:loop"
        infrastructureNode "Source" {
            technology "foreach:source"
            -> saList "take" {
                properties {
                    source  "value"
                    target  "source"
                }
            }
        }
        sa = infrastructureNode "Storage Account" {
            tags "Microsoft Azure - Storage Accounts"
            technology "azure-native:storage:getStorageAccount"
            properties {
                var "storage-account"
                accountName    ${saName}
            }
        }
        infrastructureNode "Storage Account Access Role Assignment" {
            tags "Microsoft Azure - Entra Managed Identities"
            technology "azure-native:authorization:RoleAssignment"
            properties {
                var order-service-${saName}-access-policy
                roleDefinitionId    "ba92f5b4-2d11-453d-a403-e96b0029c9fe"
            }
            -> sa "scope" {
                properties {
                    source "id"
                    target "scope"
                }
            }
            -> prodMi "principal" {
                properties {
                    source "principalId"
                    target "principalId"
                }
            }
        }
    }
    infrastructureNode "Key Vault Access Policy" {
        tags "Microsoft Azure - Entra Managed Identities"
        technology "azure-native:keyvault:AccessPolicy"
        properties {
            var "order-service-kv-access-policy"
            policy.tenantId    ${TENANT_ID}
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
            virtualNetworkName    ${VNET_NAME}
        }
        deploymentNode "App Service Plan" {
            tags "Microsoft Azure - App Service Plans"
            technology "azure-native:web:getAppServicePlan"
            properties {
                name    ${ENV}-app-service-plan
            }
            containerInstance orderApi {
                properties {
                    var                                   "order-api"
                    name                                  ${ENV}-order-api
                    identity.type                         "UserAssigned"
                    siteConfig.AppSettings:WEBSITES_PORT  "8080"
                    siteConfig.Cors.allowedOrigins        "https://web-${ENV}.azurewebsites.net,https://mobile-${ENV}.azurewebsites.net"
                    siteConfig.Cors.allowedOrigins+=      "https://${ENV}-order-api.azurewebsites.net,https://${ENV}-delivery-api.azurewebsites.net"
                }
                -> prodMi "identity" {
                    properties {
                        source  "id"
                        target  "identity.userAssignedIdentities"
                    }
                }
            }
            containerInstance orderWorker {
                properties {
                    var "order-worker"
                    name            ${ENV}-order-worker
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
}
