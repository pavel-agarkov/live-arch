using Pulumi;
using Pulumi.AzureNative.Authorization;
using Pulumi.AzureNative.ServiceBus;

namespace LiveArch.Resources.Azure.ServiceBus
{
    [Pulumi.ResourceType("azurela:servicebus:ReadableSubscription", "1.0.0")]
    public class ReadableSubscription : Subscription
    {
        [Output("roleAssignmentName")]
        public Output<string> RoleAssignmentName { get; private set; } = null!;

        public ReadableSubscription(string name, ReadableSubscriptionArgs args, CustomResourceOptions? options = null)
            : base(name, args.SubscriptionArgs, options)
        {
            var roleAssignment = new RoleAssignment($"{name}-ra", new RoleAssignmentArgs
            {
                PrincipalId = args.PrincipalId,
                RoleDefinitionId = "4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0",
                Scope = Id
            }, new CustomResourceOptions { DependsOn = this });

            RoleAssignmentName = roleAssignment.Name;
        }
    }

    public sealed class ReadableSubscriptionArgs
    {
        [Input("sub", required: true)]
        public required SubscriptionArgs SubscriptionArgs { get; set; }

        [Input("principalId", required: true)]
        public required Input<string> PrincipalId { get; set; }
    }
}
