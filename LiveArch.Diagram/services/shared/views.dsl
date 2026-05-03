systemLandscape enterprise {
    include *
    autoLayout
}

deployment * cloud env {
    include *
    exclude sharedRg
    autolayout
}

deployment * cloud shared-env "Shared resources" {
    include sharedRg
    autolayout
}

container messageBroker message-broker {
    include *
    autolayout
}
theme ../../microsoft-azure-2025.11/theme.json