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
            orderPublisher = component "Order Placed Message" {
                technology "message"
                properties {
                    typeName    "OrderPlacedMessage"
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
            deliveryCompletedMessageConsumer = component "Delivery Completed Message" {
                technology "message"
                properties {
                    typeName    "DeliveryCompletedMessage"
                }
            }
        }

        orderPublisher -> orderEventsTopic "publish Order Placed Message"
        deliveryEventsTopic -> deliveryCompletedMessageConsumer "consume Delivery Completed Message"
    }
}