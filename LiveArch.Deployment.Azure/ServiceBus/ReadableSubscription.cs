using Pulumi;
using Pulumi.AzureNative.ServiceBus;
using System;
using System.Collections.Generic;
using System.Text;

namespace LiveArch.Deployment.Azure.ServiceBus
{
    public class ReadableSubscription
    {
    }

    public sealed class ReadableSubscriptionArgs
    {
        /// <summary>
        /// 
        /// </summary>
        [Input("sub", required: true)]
        public required SubscriptionArgs SubscriptionArgs { get; set; }

        /// <summary>
        /// The principal ID.
        /// </summary>
        [Input("principalId", required: true)]
        public required Input<string> PrincipalId { get; set; }

    }
}
