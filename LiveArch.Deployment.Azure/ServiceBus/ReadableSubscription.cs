using Pulumi;
using Pulumi.AzureNative.Authorization;
using Pulumi.AzureNative.ServiceBus;

namespace LiveArch.Deployment.Azure.ServiceBus
{
    /// <summary>
    /// Represents a read-only Azure Service Bus subscription resource with data receiver role assignment.
    /// </summary>
    /// <remarks>This class provisions an Azure Service Bus subscription and assigns the Azure Service Bus
    /// Data Receiver role to the specified principal. Use this type when you need to grant read access to a Service Bus
    /// subscription within your Pulumi infrastructure as code workflows.</remarks>
    [Pulumi.ResourceType("azurela:servicebus:ReadableSubscription", "1.0.0")]
    public class ReadableSubscription : Subscription
    {
        /// <summary>
        /// The role assignment name.
        /// </summary>
        [Output("roleAssignmentName")]
        public Output<string> RoleAssignmentName { get; private set; } = null!;

        public ReadableSubscription(string name, ReadableSubscriptionArgs args, CustomResourceOptions? options = null) : base(name, args.SubscriptionArgs, options)
        {
            var roleAssignment = new RoleAssignment($"{name}-ra", new RoleAssignmentArgs
            {
                PrincipalId = args.PrincipalId,
                RoleDefinitionId = "4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0", // Azure Service Bus Data Receiver
                Scope = Id
            }, new CustomResourceOptions { DependsOn = this });

            RoleAssignmentName = roleAssignment.Name;
        }
    }

    public sealed class ReadableSubscriptionArgs
    {
        /// <summary>
        /// Underlining subscription arguments.
        /// </summary>
        [Input("sub", required: true)]
        public required SubscriptionArgs SubscriptionArgs { get; set; }

        /// <summary>
        /// The principal ID to grant the Azure Service Bus Data Receiver role.
        /// </summary>
        [Input("principalId", required: true)]
        public required Input<string> PrincipalId { get; set; }

    }
}
