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

    orderPublisher -> orderEventsTopic "publishes Order Placed Message to"
}