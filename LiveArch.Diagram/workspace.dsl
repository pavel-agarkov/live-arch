workspace EnterpriseArchitecture {
    description "An example of an enterprise architecture model using Structurizr DSL with Microsoft Azure components"

    model {
        !include services/shared/model.dsl
        !include services/order/model.dsl
        !include services/delivery/model.dsl

        cloud = deploymentEnvironment ${ENV} {
            !include services/shared/deployment.dsl
            !include services/order/deployment.dsl
            !include services/delivery/deployment.dsl
        }
    }

    views {
        !include services/shared/views.dsl
        !include services/order/views.dsl
        !include services/delivery/views.dsl
    }
}
