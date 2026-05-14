sharedRg = deploymentNode "Shared Resource Group" {
    tags "Microsoft Azure - Resource Groups"
    technology "azure-native:resources:ResourceGroup"
    properties {
        resourceGroupName     ${RESOURCE_GROUP_NAME}
        location              ${LOCATION}
    }
    deploymentNode "Service Bus Namespace" {
        tags "Microsoft Azure - Azure Service Bus"
        technology "azure-native:servicebus:Namespace"
        properties {
            namespaceName     "${ENV}-sbns"
        }
        infrastructureNode "Order Events Topic" {
            tags "Microsoft Azure - Azure Service Bus"
            technology "azure-native:servicebus:Topic"
            properties {
                var "order-events-topic"
                topicName    "${ENV}-order-events-topic"
            }
        }
        infrastructureNode "Delivery Events Topic" {
            tags "Microsoft Azure - Azure Service Bus"
            technology "azure-native:servicebus:Topic"
            properties {
                var "delivery-events-topic"
                topicName    "${ENV}-delivery-events-topic"
            }
        }
    }

    # deploymentNode "App Service Plan" {
    #     tags "Microsoft Azure - App Service Plans"
    #     technology "azure-native:appservice:Plan"
    #     properties {
    #         name      "${ENV}-app-service-plan"
    #         kind      "linux"
    #         sku.name  "B1"
    #         sku.tier  "Basic"
    #     }
    # }
}

sharedRgReference = deploymentNode "Shared Resource Group Reference" {
    tags "Microsoft Azure - Resource Groups"
    technology "azure-native:resources:getResourceGroup"
    properties {
        resourceGroupName     ${RESOURCE_GROUP_NAME}
    }
    deploymentNode "Service Bus Namespace" {
        tags "Microsoft Azure - Azure Service Bus"
        technology "azure-native:servicebus:getNamespace"
        properties {
            namespaceName     "${ENV}-sbns"
        }
        orderEventsTopicReference = containerInstance orderEventsTopic {
            properties {
                var "order-events-topic"
                topicName    "${ENV}-order-events-topic"
            }
        }
        deliveryEventsTopicReference = containerInstance deliveryEventsTopic {
            properties {
                var "delivery-events-topic"
                topicName    "${ENV}-delivery-events-topic"
            }
        }
    }
}

sandbox = deploymentNode "Sandbox" {
    technology "azure-native:resources:getResourceGroup"
    properties {
        resourceGroupName     sandbox
    }
    testSa = infrastructureNode "Test Storage Account" {
        tags "Microsoft Azure - Storage Accounts"
        technology "azure-native:storage:StorageAccount"
        properties {
            accountName                 testtatrasa
            allowBlobPublicAccess       false
            minimumTlsVersion           TLS1_2
            sku.name                    Standard_LRS
            kind                        StorageV2
            accessTier                  Cool
        }
    }
    testMi = infrastructureNode "Test Managed Identity" {
        tags "Microsoft Azure - Managed Identities"
        technology "azure-native:managedidentity:UserAssignedIdentity"
        properties {
            resourceName   test-tatra-mi
        }
        -> testSa "Contribute" "azure-native:authorization:RoleAssignment" {
            properties {
                principalType ServicePrincipal
                roleDefinitionId "/providers/Microsoft.Authorization/roleDefinitions/ba92f5b4-2d11-453d-a403-e96b0029c9fe"
            }
        }
    }
}