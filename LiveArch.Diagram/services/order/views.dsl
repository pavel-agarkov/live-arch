systemContext orderService order {
    include *
    autoLayout tb
}

container orderService order-service {
    include *
    autolayout
}
# component orderApi order-api {
#     include *
#     autolayout
# }

deployment * cloud order-env {
    include orderRg
    include orderEventsTopicReference
    include deliveryEventsTopicReference
    autolayout tb
}