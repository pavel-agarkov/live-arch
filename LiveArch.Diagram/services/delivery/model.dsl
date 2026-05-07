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
            -> deliveryDb "uses"
        }
        orderEventsTopic -> deliveryWorker "consume Order Placed Message" "azure-native:servicebus:Subscription" {
            properties {
                var "delivery-worker-subscription-to-order-events-topic"
                subscriptionName   "${ENV}-delivery-worker-subscription-to-order-events-topic"
                messageTypes "OrderPlacedMessage"
            }
        }
    }
}