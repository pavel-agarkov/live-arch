# live-arch

Architecture and Infrastructure as Code built around `Structurizr` models and `Pulumi` provisioning.

## Overview

`live-arch` treats architecture diagrams as an executable source of truth.

The main idea is:

1. describe systems, containers, deployment nodes, infrastructure nodes, and relationships in `Structurizr`
2. annotate deployment elements and relationships with Pulumi resource types and properties
3. transform the deployment view into real infrastructure using `Pulumi`

This allows a single model to express:

- architectural intent
- deployment topology
- resource dependencies
- value propagation between resources
- repeated infrastructure patterns through loops
- inline transformation pipelines for mapped values
- extensible conversion and transformation behavior through DI

## Core Concept

Each deployment element can represent:

- a new resource to create
- an existing resource to reference through a Pulumi `Invoke`
- a control element such as `foreach:loop` / `foreach:source`

Relationships can represent:

- data mapping from one resource output to another resource input
- explicit creation ordering
- a standalone relationship-resource such as `RoleAssignment`

In practice this means a diagram like:

```text
Managed Identity -> Storage Account
```

can describe not only architecture, but also:

- copy one value into another resource argument
- create an access policy / role assignment resource
- repeat the relationship for each loop iteration

## How It Works

At runtime, the deployment processor:

1. loads a `Structurizr` workspace from JSON
2. selects a deployment view for the target environment
3. walks deployment nodes in scope order
4. resolves dependencies between elements and relationships
5. creates Pulumi resources or invokes `Get*` functions
6. propagates parent values and relationship-mapped values
7. expands `foreach` loops into child scopes
8. materializes relationship-resources in the proper scope

The scope system is important because it allows the engine to distinguish between:

- globally visible resources
- resources created inside a loop iteration
- relationship-resources that must be repeated per iteration

## Extensibility Model

The deployment engine is designed to be extended through dependency injection.

Two extension points are especially important:

- named value converters
- named transformers

### Converters

Converters are responsible for turning raw DSL values, resource outputs, dictionaries, lists,
and Pulumi outputs into the exact input type required by a target resource property.

The engine supports both:

- automatic typed converters
- named converters selected explicitly from DSL metadata

Named converters are useful when a target input shape cannot be inferred from the target type alone.

For example, a keyed `InputList<T>` item may need a specialized converter that knows how to build the payload object and populate its `Value` property.

### Transformers

Transformers are applied before the final target-type conversion step.

They are used when a mapped source value must first be reshaped, parsed, split, formatted, filtered, or scaled before it is written into the target property.

Transformers are resolved by name from an injected `ITransformerRegistry`.

Built-in transformers are always available by default, and custom registrations can:

- add entirely new transformer names
- override an existing built-in transformer name such as `split`

This means a consumer can register a single custom transformer and keep all built-in behavior without re-registering the full built-in set.

## DSL Style

The syntax is standard `Structurizr DSL` enriched with Pulumi-specific `technology` and `properties`.

### Variables

`live-arch` extends the usual `Structurizr` variable usage by resolving variables not only in the model text, but also during resource argument conversion.

Variables use the familiar syntax:

```text
${ENV}
${RESOURCE_GROUP_NAME}
${saName}
```

They can be used in:

- resource properties
- relationship properties
- loop-scoped values
- string templates embedded inside larger values

Examples:

```text
properties {
    resourceName ${ENV}-order-service-mi
    vaultName ${KEY_VAULT_NAME}
    var order-service-${saName}-contributor
}
```

Unlike a plain text-only substitution model, the processor also preserves direct variable values when the whole value is exactly a single placeholder.
This makes it possible to pass non-string values through the same syntax when needed.

### Reference an existing resource

```text
deploymentNode "Resource Group" {
    technology "azure-native:resources:getResourceGroup"
    properties {
        resourceGroupName ${RESOURCE_GROUP_NAME}
    }
}
```

This means:

- do not create a new resource group
- call the matching Pulumi `get` function
- expose its outputs to child elements and relationships

### Create a new resource

```text
infrastructureNode "Managed Identity" {
    technology "azure-native:managedidentity:UserAssignedIdentity"
    properties {
        var "order-service-mi"
        resourceName ${ENV}-order-service-mi
    }
}
```

This means:

- create a new Pulumi resource
- use `var` as the logical resource name
- map DSL properties to Pulumi input arguments

### Map values through a relationship

```text
orderApiInstance -> prodMi "identity" {
    properties {
        source "id"
        target "identity.userAssignedIdentities"
    }
}
```

This means:

- read `id` from the destination resource
- assign it to `identity.userAssignedIdentities` on the source resource arguments

Property mapping can also use custom transformers registered in the deployment engine.
Each transformer reads the mapped source value, applies custom logic,
and then passes the transformed result into the normal conversion pipeline that binds the final target input type.

This is useful for cases such as:

- formatting values
- regex-based extraction
- numeric scaling or multiplication
- splitting text into collections
- adapting one resource output into another resource's expected input shape

Transformers can be declared either as standalone relationship properties:

```text
workerComponent -> cacheSizeConfig "cache size" {
    properties {
        source "value"
        target "cacheSizeInBytes"
        multiply "1048576"
    }
}
```

or as an inline pipeline embedded directly inside a property value:

```text
deliveryMi -> deliveryKeyVault reads "azure-native:keyvault:AccessPolicy" {
    properties {
        var "delivery-service-kv-access-policy"
        policy.permissions.secrets "get, list | split ,"
    }
}
```

Inline pipelines are parsed left to right.
The segment before the first pipe is treated as the source value, and each following segment is resolved as a named transformer.

Realistic examples:

#### Example 1: Convert megabytes from configuration into bytes

```text
cacheSizeConfig = infrastructureNode "Cache Size Config" {
    technology "azure-native:appconfiguration:getKeyValue"
    properties {
        configStoreName ${APP_CONFIG_NAME}
        keyValueName "worker:cacheSizeMb"
    }
}

workerComponent -> cacheSizeConfig "cache size" {
    properties {
        source "value"
        target "cacheSizeInBytes"
        multiply "1048576"
    }
}
```

Practical meaning:

- App Configuration stores a human-friendly value such as `512`
- the target component expects bytes, not megabytes
- the `multiply` transformer converts `512` into `536870912`
- the transformed value is written into `cacheSizeInBytes`

This is useful when operational settings are maintained in simple units,
but the consuming resource expects a lower-level numeric value.

#### Example 2: Build a standard Azure App Service URL from the app name

```text
orderApiInstance = containerInstance orderApi {
    properties {
        name ${ENV}-order-api
    }
}

deliveryApiInstance -> orderApiInstance "Order API URL" {
    properties {
        source "name"
        target "siteConfig.appSettings:OrderApi__BaseUrl"
        format "https://{0}.azurewebsites.net"
    }
}
```

Practical meaning:

- the Web App resource exposes only its resource name, for example `prod-order-api`
- another component needs the public base URL
- the `format` transformer converts `prod-order-api` into `https://prod-order-api.azurewebsites.net`
- the final value is written into `OrderApi__BaseUrl`

This is useful when one resource exposes a short Azure resource name,
while another resource or application setting expects a fully formed URL.

#### Example 3: Split a comma-separated permission list through an inline pipeline

```text
deliveryMi -> deliveryKeyVault reads "azure-native:keyvault:AccessPolicy" {
    properties {
        var "delivery-service-kv-access-policy"
        policy.permissions.secrets "get, list | split ,"
    }
}
```

Practical meaning:

- the access policy expects a list-like secrets permission input
- the DSL keeps the source value readable as `get, list`
- the inline `split` transformer converts it into individual permission items
- the normal conversion pipeline then binds the result to the target Pulumi input type

This keeps simple data-shaping logic close to the DSL value that needs it.

### Create a relationship-resource

```text
testMi -> testSa "Contribute" "azure-native:authorization:RoleAssignment" {
    properties {
        principalType ServicePrincipal
        roleDefinitionId "${storageBlobDataContributor}"
    }
}
```

This means:

- create a real `RoleAssignment` resource
- use both ends of the relationship as inputs
- propagate parent/resource values such as `scope` and `principalId`

### Repeat resources with `foreach`

```text
saList = infrastructureNode "Storage Accounts" {
    technology "azure-native:appconfiguration:getKeyValue"
    properties {
        configStoreName ${APP_CONFIG_NAME}
        keyValueName "storageAccounts"
    }
}

saName = deploymentNode "Foreach Storage Account in Config" {
    technology "foreach:loop"
    infrastructureNode "Source" {
        technology "foreach:source"
        -> saList "take" {
            properties {
                source "value"
                target "source"
            }
        }
    }

    sa = infrastructureNode "Storage Account" {
        technology "azure-native:storage:getStorageAccount"
        properties {
            var "storage-account"
            accountName ${saName}
        }
    }
}
```

This means:

- load a source collection
- create one child scope per item
- expose the current item as `${saName}`
- create the inner resources once per iteration

### Relationship-resource from outside a loop into the loop

```text
prodMi -> sa "Contribute" "azure-native:authorization:RoleAssignment" {
    properties {
        var order-service-${saName}-contributor
        roleDefinitionId ${storageBlobDataContributor}
        principalType ServicePrincipal
    }
}
```

This means:

- `prodMi` is outside the loop
- `sa` is created inside each loop iteration
- the engine repeats the `RoleAssignment` resource inside each iteration scope

For example, if the source contains `sa1, sa2, sa3`, the engine creates:

- `RoleAssignment(prodMi -> sa1)`
- `RoleAssignment(prodMi -> sa2)`
- `RoleAssignment(prodMi -> sa3)`

## Common Property Patterns

Simple assignment:

```text
properties {
    accountName my-storage-account
}
```

Nested assignment:

```text
properties {
    identity.type UserAssigned
}
```

Map entry assignment:

```text
properties {
    siteConfig.AppSettings:WEBSITES_PORT "8080"
}
```

Append to list:

```text
properties {
    siteConfig.Cors.allowedOrigins+= "https://app.example.com"
}
```

Explicit split into a list:

```text
properties {
    siteConfig.Cors.allowedOrigins "https://web.example.com,https://api.example.com | split ,"
}
```

If a target expects a list-like Pulumi input, the `split` transformer should be declared explicitly.

Loop sources commonly use the same idea through a `foreach:source` relationship:

```text
saList = infrastructureNode "Storage Accounts" {
    tags "Microsoft Azure - App Configuration"
    technology "azure-native:appconfiguration:getKeyValue"
    properties {
        configStoreName ${APP_CONFIG_NAME}
        keyValueName "storageAccounts"
    }
}

saName = deploymentNode "Foreach Storage Account in Config" {
    technology "foreach:loop"
    infrastructureNode "Source" {
        technology "foreach:source"
        -> saList "take" {
            properties {
                source "value"
                target "source"
                split ","
            }
        }
    }
}
```

This lets the loop source be converted into a typed collection before iteration begins.

Inline pipelines are useful when the transformation should live directly inside a single property value:

```text
deliveryMi -> deliveryKeyVault reads "azure-native:keyvault:AccessPolicy" {
    properties {
        var "delivery-service-kv-access-policy"
        policy.permissions.secrets "get, list | split ,"
    }
}
```

This is useful when the split behavior must be made explicit or replaced by a custom transformer implementation.

## Customizing Transformers and Converters

The recommended way to customize behavior is through dependency injection in the hosting application.

### Register built-in transformers

```csharp
services.AddDefaultTransformers();
```

### Add a new custom transformer

```csharp
services.AddNamedTransformer("prefix", parameter => new MyPrefixTransformer(parameter));
```

### Override a built-in transformer implementation

```csharp
services.AddNamedTransformer("split", parameter => new MyCustomSplitTransformer(parameter));
```

In this case:

- the custom `split` overrides the built-in one
- other built-in transformers remain available automatically

### Register converters

```csharp
services
    .AddDefaultValueConverters()
    .AddAzureValueConverters();
```

Custom converters can be registered either as:

- typed converters, which participate automatically based on source and target types
- named converters, which are selected explicitly through DSL metadata such as `converter`

Example:

```text
properties {
    converter "default-keyed-list-value"
}
```

This allows a host application to adapt the deployment engine without modifying the core processor.

## When to Use This Approach

This approach is useful when you want to:

- architecture and IaC to stay in sync
- infrastructure relationships to be visible in diagrams
- repeated deployment patterns to be modeled once and expanded automatically
- resource wiring to be described declaratively through relationships instead of imperative code

## Related Documentation

- Structurizr: <https://structurizr.com/help>
- Structurizr DSL: <https://docs.structurizr.com/dsl>
- Pulumi docs: <https://www.pulumi.com/docs/>
- Pulumi Inputs and Outputs: <https://www.pulumi.com/docs/iac/concepts/inputs-outputs/>
- Pulumi Invoke functions: <https://www.pulumi.com/docs/iac/concepts/functions/provider-functions/>
