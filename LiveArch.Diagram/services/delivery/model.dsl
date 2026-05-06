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
            deliveryCompletedMessagePublisher = component "Delivery Completed Message" {
                technology "message"
                properties {
                    typeName    "DeliveryCompletedMessage"
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
            orderConsumer = component "Order Placed Message" {
                technology "message"
                properties {
                    typeName    "OrderPlacedMessage"
                }
            }
        }
        orderEventsTopic -> orderConsumer "consume Order Placed Message"
        deliveryCompletedMessagePublisher -> deliveryEventsTopic "publish Delivery Completed Message"
    }
}