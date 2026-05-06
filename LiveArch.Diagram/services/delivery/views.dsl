container deliveryService delivery-service {
    include *
    autolayout
}
component deliveryWorker delivery-worker {
     include *
     autolayout
}

deployment * cloud delivery-env {
    include deliveryRg 
    include orderEventsTopicReference
    include deliveryEventsTopicReference
    autolayout
}