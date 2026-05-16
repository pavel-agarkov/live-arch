systemLandscape enterprise {
    include *
    autoLayout tb
}

deployment * cloud env {
    include *
    exclude sharedRg
    autolayout
}

deployment * cloud shared-env "Shared resources for deployment" {
    include sharedRg
    autolayout
}

deployment * cloud shared-ref-env "Shared resources for reference" {
    include sharedRgReference
    autolayout
}

deployment * cloud sandbox "Sandbox resources" {
    include sandbox
    autolayout
}

container orderMsgSys order-message-system {
    include *
    autolayout
}

container deliveryMsgSys delivery-message-system {
    include *
    autolayout
}

# component orderEventsTopic order-events {
#     include *
#     autolayout
# }

# component deliveryEventsTopic delivery-events {
#     include *
#     autolayout
# }


theme ../../microsoft-azure-2025.11/theme.json