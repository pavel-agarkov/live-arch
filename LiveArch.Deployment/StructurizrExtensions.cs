using Structurizr;
using System;
using System.Collections.Generic;
using System.Linq;

namespace LiveArch.Deployment
{
    public static class StructurizrExtensions
    {
        public static IEnumerable<T> On<T>(this IEnumerable<T> elements, string env, Func<string, object> substituteVariables) where T : DeploymentElement
        {
            return elements.Where(x => substituteVariables(x.Environment).ToString() == env);
        }
    }
}
