sharedRg = deploymentNode "Shared Resource Group" {
    tags "Microsoft Azure - Resource Groups"
    technology "azure-native:resources:ResourceGroup"
    properties {
        resourceGroupName     ${RESOURCE_GROUP_NAME}
    }
    deploymentNode "Service Bus Namespace" {
        tags "Microsoft Azure - Service Bus"
        technology "azure-native:servicebus:Namespace"
        properties {
            namespaceName     "${ENV}-sbns"
        }
        containerInstance orderEventsTopic {
            properties {
                var "order-events-topic"
                name    "${ENV}-order-events-topic"
            }
        }
    }

    deploymentNode "App Service Plan" {
        tags "Microsoft Azure - App Service Plans"
        technology "azure-native:appservice:Plan"
        properties {
            name      "${ENV}-app-service-plan"
            kind      "linux"
            sku.name  "B1"
            sku.tier  "Basic"
        }
    }
}

deploymentNode "Shared Resource Group Reference" {
    tags "Microsoft Azure - Resource Groups"
    technology "azure-native:resources:getResourceGroup"
    properties {
        resourceGroupName     ${RESOURCE_GROUP_NAME}
        isDisabled true
    }
    deploymentNode "Service Bus Namespace" {
        tags "Microsoft Azure - Service Bus"
        technology "azure-native:servicebus:getNamespace"
        properties {
            namespaceName     "${ENV}-sbns"
            isDisabled true
        }
        orderEventsTopicReference = containerInstance orderEventsTopic {
            properties {
                var "order-events-topic"
                name    "${ENV}-order-events-topic"
                isDisabled true
            }
        }
    }
}
