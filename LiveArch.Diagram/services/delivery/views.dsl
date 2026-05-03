container deliveryService delivery-service {
    include *
    autolayout
}
component deliveryApi delivery-api {
     include *
     autolayout
}

deployment * cloud delivery-env {
    include deliveryRg 
    include orderEventsTopicReference
    autolayout
}