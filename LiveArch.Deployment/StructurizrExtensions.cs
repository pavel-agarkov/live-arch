using Structurizr;
using System;
using System.Collections.Generic;
using System.Linq;

namespace LiveArch.Deployment
{
    public static class StructurizrExtensions
    {
        public static IEnumerable<T> On<T>(this IEnumerable<T> elements, string env, DeploymentView view, Func<string, object> substituteVariables) where T : DeploymentElement
        {
            return from element in elements
                   join deploymentNode in view.Elements.Select(e => e.Element)
                       on element.Id equals deploymentNode.Id
                   where substituteVariables(element.Environment).ToString() == env
                   select element;
        }

        public static IEnumerable<Relationship> In(this IEnumerable<Relationship> relationships, DeploymentView view)
        {
            return from rel in relationships
                   join relInView in view.Relationships on rel.Id equals relInView.Id
                   select rel;
        }
    }
}
