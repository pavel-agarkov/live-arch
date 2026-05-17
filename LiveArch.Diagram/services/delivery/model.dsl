group "Delivery" {
    deliveryService = softwareSystem "Delivery Microservice" {
        deliveryDb = container "Delivery Database" {
            tags "Microsoft Azure - Production Ready Database"
            technology "azure-native:sql:Database"
        }
        deliveryApi = container "Delivery API" {
            tags "Microsoft Azure - App Services"
            technology "azure-native:web:WebApp"
            properties {
                buildTechnology "docker-build:index:Image"
                context.location "../LiveArch.Delivery.Api/"
                dockerfile.location "../.Dockerfile"
                push "true"
            }
            -> deliveryDb "uses"
            -> deliveryEventsTopic "publish Delivery Events" {
                properties {
                    messageTypes "DeliveryCompletedMessage,DeliveryFailedMessage"
                }
            }
        }
        deliveryWorker = container "Delivery Worker" {
            tags "Microsoft Azure - App Services"
            technology "azure-native:web:WebApp"
            properties {
                buildTechnology "docker-build:index:Image"
                context.location "../LiveArch.Delivery.Worker/"
                dockerfile.location "../.Dockerfile"
                push "true"
            }
            -> deliveryDb "uses" {
                properties {
                    source  "name"
                    target  "siteConfig.connectionStrings:DeliveryDatabase"
                    format  "Server=tcp:${SQL_SERVER_NAME}.database.windows.net,1433;Initial Catalog={0};"
                    converter "azure-sql-connection-string"
                }
            }
            -> orderEventsTopic "subscribe to Order Placed Message" "azurela:servicebus:ReadableSubscription" {
                properties {
                    var "delivery-worker-subscription-to-order-events-topic"
                    subscriptionName   "${ENV}-delivery-worker-subscription-to-order-events-topic"
                    messageTypes "OrderPlacedMessage"
                }
            }
        }
    }
}