messageBroker = softwareSystem "Message Broker" {
    orderEventsTopic = container "Order Events" {
        tags "Microsoft Azure - Messaging"
        technology "azure-native:servicebus:Topic"
    }
}
