deliveryRg = deploymentNode "Delivery Resource Group" {
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
                containerInstance deliveryDb mainProdRg {
                    properties {
                        var "delivery-db"
                        name    ${ENV}-delivery-db
                    }
                }
            }
        }
    }
    deliveryKeyVault = infrastructureNode "Key Vault" {
        tags "Microsoft Azure - Key Vaults"
        technology "azure-native:keyvault:getVault"
        properties {
            vaultName    ${KEY_VAULT_NAME}
        }
    }
    deliveryMi = infrastructureNode "Managed Identity" {
        tags "Microsoft Azure - Managed Identities"
        technology "azure-native:managedidentity:UserAssignedIdentity"
        properties {
            var "delivery-service-mi"
            resourceName   ${ENV}-delivery-service-mi
        }
    }
    infrastructureNode "Key Vault Access Policy" {
        tags "Microsoft Azure - Entra Managed Identities"
        technology "azure-native:keyvault:AccessPolicy"
        properties {
            var "delivery-service-kv-access-policy"
            policy.tenantId    ${TENANT_ID}
            policy.permissions.secrets  "get, list"
        }
        -> deliveryMi "principal" {
            properties {
                source "principalId"
                target "policy.objectId"
            }
        }
        -> deliveryKeyVault "vault" {
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
            containerInstance deliveryApi {
                properties {
                    var "delivery-api"
                    name            ${ENV}-delivery-api
                    identity.type   "UserAssigned"
                }
                -> deliveryMi "identity" {
                    properties {
                        source  "id"
                        target  "identity.userAssignedIdentities"
                    }
                }
            }
        }
    }
}
