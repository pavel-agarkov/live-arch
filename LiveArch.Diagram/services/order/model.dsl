group "Ordering" {
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
                context.location "../LiveArch.Order.Api/"
                dockerfile.location "../.Dockerfile"
                push "true"
            }
            -> orderDb "uses"
            -> orderEventsTopic "publish Order Placed Message" {
                properties {
                    messageTypes "OrderPlacedMessage"
                }
            }
        }
        orderWorker = container "Order Worker" {
            tags "Microsoft Azure - App Services"
            technology "azure-native:web:WebApp"
            properties {
                buildTechnology "docker-build:index:Image"
                context.location "../LiveArch.Order.Worker/"
                dockerfile.location "../.Dockerfile"
                push "true"
            }
            -> orderDb "uses"
            -> deliveryEventsTopic "consume Delivery Completed Message" "azurela:servicebus:ReadableSubscription" {
                properties {
                    var "order-worker-subscription-to-delivery-events-topic"
                    subscriptionName   "${ENV}-order-worker-subscription-to-delivery-events-topic"
                    messageTypes "DeliveryCompletedMessage,DeliveryFailedMessage"
                }
            }
        }
    }

    
}