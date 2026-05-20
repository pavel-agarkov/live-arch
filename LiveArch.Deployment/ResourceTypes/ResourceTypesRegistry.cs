using Pulumi;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;

namespace LiveArch.Deployment.ResourceTypes
{
    /// <summary>
    /// Resolves Pulumi resource types and invoke methods from registered provider assemblies.
    /// </summary>
    public class ResourceTypesRegistry
    {
        private readonly Dictionary<string, Type> resourceTypes = new();
        private Dictionary<string, MethodInfo> invokeMethods = new();

        /// <summary>
        /// Initializes the registry by scanning the assemblies referenced by the supplied markers.
        /// </summary>
        /// <param name="assemblyMarkers">Marker types that identify assemblies to scan.</param>
        public ResourceTypesRegistry(IEnumerable<ResourceTypesAssemblyMarker> assemblyMarkers)
        {
            foreach (var marker in assemblyMarkers)
            {
                CachePulumiTypes(marker.AssemblyMarker);
            }
        }

        /// <summary>
        /// Tries to resolve a Pulumi resource CLR type for the given Pulumi token.
        /// </summary>
        /// <param name="token">Pulumi type token such as <c>azure-native:storage:StorageAccount</c>.</param>
        /// <param name="type">Resolved CLR type when found.</param>
        /// <returns><c>true</c> when the token is known; otherwise <c>false</c>.</returns>
        public bool TryGetResourceType(string token, out Type? type)
        {
            return resourceTypes.TryGetValue(token, out type);
        }

        /// <summary>
        /// Tries to resolve a Pulumi invoke method for the given Pulumi token.
        /// </summary>
        /// <param name="token">Pulumi invoke token such as <c>azure-native:resources:getResourceGroup</c>.</param>
        /// <param name="method">Resolved static invoke method when found.</param>
        /// <returns><c>true</c> when the token is known; otherwise <c>false</c>.</returns>
        public bool TryGetInvokeMethod(string token, out MethodInfo? method)
        {
            return invokeMethods.TryGetValue(token, out method);
        }

        private void CachePulumiTypes(params Type[] entryTypes)
        {
            entryTypes.Select(x => x.Assembly).Distinct().ToList().ForEach(CacheAssamblyTypes);
        }

        private void CacheAssamblyTypes(Assembly assembly)
        {
            var types = assembly.GetTypes();
            foreach (var resType in types)
            {
                var attr = resType.GetCustomAttribute<ResourceTypeAttribute>(false);
                if (attr != null)
                {
                    resourceTypes.Add(attr.Type, resType);
                }
            }

            foreach (var type in types)
            {
                if (!type.IsAbstract || !type.IsSealed) continue;
                if (!type.Name.StartsWith("Get")) continue;

                var invoke = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "Invoke" && m.GetParameters().Last().ParameterType == typeof(InvokeOptions));

                if (invoke == null) continue;

                var token = ExtractInvokeToken(invoke);
                if (token == null) continue;

                invokeMethods[token] = invoke;
                resourceTypes[token] = type;
            }
        }

        private static string? ExtractInvokeToken(MethodInfo method)
        {
            // Ищем вызов Deployment.Instance.InvokeAsync<T>(token, ...)
            var body = method.GetMethodBody();
            if (body == null) return null;

            // Ищем строковые литералы в IL
            var module = method.Module;
            var il = body.GetILAsByteArray();

            for (int i = 0; i < il.Length - 1; i++)
            {
                // ldstr = 0x72
                if (il[i] == 0x72)
                {
                    int metadataToken = BitConverter.ToInt32(il, i + 1);
                    var str = module.ResolveString(metadataToken);

                    // Ищем строки вида "azure-native:keyvault:getVault"
                    if (str.Contains(":") && str.Count(c => c == ':') >= 2)
                        return str;
                }
            }

            return null;
        }

        /// <summary>
        /// Returns every distinct resource or invoke result type discovered in the registry.
        /// </summary>
        /// <returns>A de-duplicated collection of CLR types used by the deployment engine.</returns>
        public IReadOnlyCollection<Type> GetAllResourceTypes()
        {
            return [
                .. resourceTypes.Values.Distinct(),
                .. invokeMethods.Values.Select(m => m.ReturnType.GenericTypeArguments.FirstOrDefault()!).Where(t => t != null).Distinct(),
            ];
        }
    }
}
