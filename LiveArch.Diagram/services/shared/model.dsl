orderMsgSys = softwareSystem "Order Messaging System" {
    orderEventsTopic = container "Order Events" {
        tags "Microsoft Azure - Azure Service Bus"
        technology "azure-native:servicebus:getTopic"
    }
}

deliveryMsgSys = softwareSystem "Delivery Messaging System" {
    deliveryEventsTopic = container "Delivery Events" {
        tags "Microsoft Azure - Azure Service Bus"
        technology "azure-native:servicebus:getTopic"
    }
}
