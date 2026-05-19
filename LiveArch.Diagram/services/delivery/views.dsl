systemContext deliveryService delivery {
    include *
    autoLayout tb
}

container deliveryService delivery-service {
    include *
    autolayout tb
}
# component deliveryWorker delivery-worker {
#      include *
#      autolayout
# }

deployment * cloud delivery-env {
    include deliveryRg 
    include orderEventsTopicReference
    include deliveryEventsTopicReference
    autolayout tb
}