messageBroker = softwareSystem "Message Broker" {
    orderEventsTopic = container "Order Events" {
        tags "Microsoft Azure - Azure Service Bus"
        technology "azure-native:servicebus:getTopic"
        orderPlacedMessage = component "Order Placed Message" {
            technology "message"
            properties {
                typeName    "OrderPlacedMessage"
            }
        }
    }
    deliveryEventsTopic = container "Delivery Events" {
        tags "Microsoft Azure - Azure Service Bus"
        technology "azure-native:servicebus:getTopic"
        deliveryCompletedMessage = component "Delivery Completed Message" {
            technology "message"
            properties {
                typeName    "DeliveryCompletedMessage"
            }
        }
    }
}
