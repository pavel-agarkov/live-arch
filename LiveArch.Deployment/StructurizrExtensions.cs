using Structurizr;
using System.Collections.Generic;
using System.Linq;

namespace LiveArch.Deployment
{
    public static class StructurizrExtensions
    {
        public static IEnumerable<T> On<T>(this IEnumerable<T> elements, string env) where T : DeploymentElement
        {
            return elements.Where(x => x.Environment == env);
        }
    }
}
