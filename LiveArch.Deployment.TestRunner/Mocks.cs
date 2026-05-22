using Pulumi;
using Pulumi.AzureNative.Web;
using Pulumi.DockerBuild;
using Pulumi.Testing;
using System.Collections;
using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;


namespace LiveArch.Deployment
{
    public class Mocks : IMocks
    {
        private static readonly PropertyInfo inputAttrNameProp = typeof(InputAttribute).GetProperty("Name", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private readonly Dictionary<string, List<Func<MockResourceArgs, IReadOnlyDictionary<string, object>>>> resourceBuilders = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<Func<MockCallArgs, IReadOnlyDictionary<string, object>>>> callBuilders = new(StringComparer.OrdinalIgnoreCase);

        public Mocks()
        {
            AddCreateResourceMock<WebApp, WebAppArgs>(context =>
            {
                var outputs = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                {
                    ["defaultHostName"] = $"{context.Name}.azurewebsites.net",
                };

                if (TryGetValue(context.RawInputs, "identity", out var identityValue) && TryGetDictionary(identityValue, out var identityInputs))
                {
                    var identityOutputs = ToDictionary(identityInputs);
                    identityOutputs["tenantId"] = "mock-tenant-id";
                    identityOutputs["principalId"] = $"{context.Name}-principal-id";
                    outputs["identity"] = identityOutputs;
                }

                return outputs;
            });

            AddCreateResourceMock<AppServicePlan, AppServicePlanArgs>(context => new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = context.Id,
                ["name"] = context.Name,
                ["kind"] = GetString(context.RawInputs, "kind") ?? string.Empty,
                ["location"] = GetString(context.RawInputs, "location") ?? "test-location",
                ["provisioningState"] = "Succeeded",
                ["status"] = "Ready",
                ["type"] = context.Token,
                ["resourceGroup"] = context.Inputs.ResourceGroupName,
                ["geoRegion"] = GetString(context.RawInputs, "location") ?? "test-location",
                ["azureApiVersion"] = "2024-11-01",
                ["maximumNumberOfWorkers"] = 1,
                ["numberOfSites"] = 0,
                ["numberOfWorkers"] = 1,
                ["subscription"] = "mock-subscription",
            });

            AddCreateResourceMock<Image, ImageArgs>(context => new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["ref"] = GetFirstString(context.RawInputs, "tags") ?? $"some.test/{context.Name}:latest"
            });

            AddGetResourceMock<Pulumi.AzureNative.AppConfiguration.GetKeyValueArgs>(typeof(Pulumi.AzureNative.AppConfiguration.GetKeyValue), context => new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["name"] = GetString(context.RawInputs, "key") ?? "test-key",
                ["value"] = "sa1, sa2, sa3",
            });

            AddGetResourceMock<Pulumi.AzureNative.Storage.GetStorageAccountArgs>(typeof(Pulumi.AzureNative.Storage.GetStorageAccount), context => new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["name"] = GetString(context.RawInputs, "accountName") ?? "test-storage-account",
            });
        }

        public Task<(string? id, object state)> NewResourceAsync(MockResourceArgs args)
        {
            args.Id ??= $"{args.Name}_id";

            var outputs = BuildDefaultResourceOutputs(args);
            if (resourceBuilders.TryGetValue(args.Type, out var builders))
            {
                foreach (var builder in builders)
                {
                    DeepMerge(outputs, builder(args));
                }
            }

            return Task.FromResult((args.Id, (object)outputs.ToImmutableDictionary()));
        }

        public Task<object> CallAsync(MockCallArgs args)
        {
            var outputs = BuildDefaultCallOutputs(args);
            if (callBuilders.TryGetValue(args.Token, out var builders))
            {
                foreach (var builder in builders)
                {
                    DeepMerge(outputs, builder(args));
                }
            }

            return Task.FromResult((object)outputs.ToImmutableDictionary());
        }

        public Mocks AddCreateResourceMock<TResource, TArgs>(Func<ResourceMockContext<TArgs>, IReadOnlyDictionary<string, object>> builder)
        {
            var token = GetResourceToken(typeof(TResource));
            if (!resourceBuilders.TryGetValue(token, out var builders))
            {
                builders = [];
                resourceBuilders[token] = builders;
            }

            builders.Add(args => builder(new ResourceMockContext<TArgs>(args)));
            return this;
        }

        public Mocks AddGetResourceMock<TArgs>(Type invokeType, Func<CallMockContext<TArgs>, IReadOnlyDictionary<string, object>> builder)
        {
            var token = GetInvokeToken(invokeType);
            if (!callBuilders.TryGetValue(token, out var builders))
            {
                builders = [];
                callBuilders[token] = builders;
            }

            builders.Add(args => builder(new CallMockContext<TArgs>(args)));
            return this;
        }

        private static Dictionary<string, object> BuildDefaultResourceOutputs(MockResourceArgs args)
        {
            var outputs = ToDictionary(args.Inputs);

            outputs["id"] = args.Id!;
            outputs.TryAdd("name", InferName(outputs, args.Name));
            outputs.TryAdd("type", args.Type);
            outputs.TryAdd("resourceGroup", args.Inputs.TryGetValue("resourceGroupName", out var rg) ? rg : "test-resource-group");
            outputs.TryAdd("location", GetString(outputs, "location") ?? "test-location");

            if (outputs.TryGetValue("identity", out var identity))
            {
                var identityOutputs = ToDictionary(identity);
                identityOutputs.TryAdd("tenantId", "mock-tenant-id");
                identityOutputs.TryAdd("principalId", $"{outputs["name"]}-principal-id");
                outputs["identity"] = identityOutputs;
            }

            return outputs;
        }

        private static Dictionary<string, object> BuildDefaultCallOutputs(MockCallArgs args)
        {
            var outputs = ToDictionary(args.Args);

            outputs.TryAdd("name", InferName(outputs, "test-name"));
            outputs.TryAdd("location", "test-location");
            outputs.TryAdd("id", (args.Token?.Split(':').Last() ?? string.Empty) + Guid.NewGuid());
            outputs.TryAdd("serverFarmId", "test-app-service-plan");
            outputs.TryAdd("serverName", "test-server-name");
            outputs.TryAdd("ref", "some.test/docker-image:tag");

            return outputs;
        }

        private static string InferName(IReadOnlyDictionary<string, object> values, string fallback)
        {
            var knownKeys = new[]
            {
                "name",
                "accountName",
                "databaseName",
                "serverName",
                "topicName",
                "namespaceName",
                "virtualNetworkName",
                "sqlServerRegistrationName",
                "sqlServerName",
                "appConfigurationName",
                "resourceGroupName",
            };

            foreach (var key in knownKeys)
            {
                if (values.TryGetValue(key, out var value) && value is string str && !string.IsNullOrWhiteSpace(str))
                {
                    return str;
                }
            }

            var genericName = values.FirstOrDefault(kv => kv.Key.EndsWith("Name", StringComparison.OrdinalIgnoreCase) && kv.Value is string);
            return genericName.Value as string ?? fallback;
        }

        private static string? GetString(IReadOnlyDictionary<string, object> values, string key)
        {
            return TryGetValue(values, key, out var value) ? value?.ToString() : null;
        }

        private static string? GetFirstString(IReadOnlyDictionary<string, object> values, string key)
        {
            if (!TryGetValue(values, key, out var value) || value is string)
            {
                return value?.ToString();
            }

            return value is IEnumerable enumerable
                ? enumerable.Cast<object?>().Select(item => item?.ToString()).FirstOrDefault(item => !string.IsNullOrWhiteSpace(item))
                : value?.ToString();
        }

        private static string GetResourceToken(Type resourceType)
        {
            var attribute = resourceType.GetCustomAttribute<ResourceTypeAttribute>(false)
                ?? throw new InvalidOperationException($"Type '{resourceType.FullName}' does not have a resource token attribute.");

            return attribute.Type;
        }

        private static string GetInvokeToken(Type invokeType)
        {
            var invokeAsync = invokeType.GetMethod("InvokeAsync", BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException($"Type '{invokeType.FullName}' does not expose InvokeAsync.");

            var body = invokeAsync.GetMethodBody()
                ?? throw new InvalidOperationException($"Type '{invokeType.FullName}' does not have an invokable body.");
            var il = body.GetILAsByteArray();
            for (var i = 0; i < il.Length - 1; i++)
            {
                if (il[i] != 0x72)
                {
                    continue;
                }

                var metadataToken = BitConverter.ToInt32(il, i + 1);
                var token = invokeAsync.Module.ResolveString(metadataToken);
                if (token.Contains(':') && token.Count(ch => ch == ':') >= 2)
                {
                    return token;
                }
            }

            throw new InvalidOperationException($"Unable to extract invoke token for '{invokeType.FullName}'.");
        }

        private static void DeepMerge(IDictionary<string, object> target, IReadOnlyDictionary<string, object> source)
        {
            foreach (var (key, value) in source)
            {
                if (target.TryGetValue(key, out var existing) && existing != null && value != null && IsDictionary(existing) && IsDictionary(value))
                {
                    var nested = ToDictionary(existing);
                    DeepMerge(nested, ToDictionary(value));
                    target[key] = nested;
                    continue;
                }

                target[key] = value;
            }
        }

        private static bool IsDictionary(object value)
        {
            return value is IDictionary ||
                value is IReadOnlyDictionary<string, object> ||
                value is IEnumerable<KeyValuePair<string, object>>;
        }

        private static Dictionary<string, object> ToDictionary(object? value)
        {
            if (value == null)
            {
                return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            }

            if (value is IReadOnlyDictionary<string, object> readOnlyDictionary)
            {
                return readOnlyDictionary.ToDictionary(kv => kv.Key, kv => NormalizeValue(kv.Value), StringComparer.OrdinalIgnoreCase);
            }

            if (value is IEnumerable<KeyValuePair<string, object>> keyValuePairs)
            {
                return keyValuePairs.ToDictionary(kv => kv.Key, kv => NormalizeValue(kv.Value), StringComparer.OrdinalIgnoreCase);
            }

            if (value is IDictionary dictionary)
            {
                var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (entry.Key is string key)
                    {
                        result[key] = NormalizeValue(entry.Value);
                    }
                }

                return result;
            }

            var properties = value.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(property => property.CanRead);
            return properties.ToDictionary(
                property => GetPropertyName(property),
                property => NormalizeValue(property.GetValue(value)),
                StringComparer.OrdinalIgnoreCase);
        }

        private static object NormalizeValue(object? value)
        {
            if (value == null)
            {
                return null!;
            }

            if (value is string or ValueType)
            {
                return value;
            }

            if (IsDictionary(value))
            {
                return ToDictionary(value);
            }

            if (value is IEnumerable enumerable)
            {
                return enumerable.Cast<object?>().Select(NormalizeValue).ToList();
            }

            return ToDictionary(value);
        }

        private static string GetPropertyName(PropertyInfo property)
        {
            var inputAttribute = property.GetCustomAttribute<InputAttribute>();
            if (inputAttribute != null)
            {
                return (string)inputAttrNameProp.GetValue(inputAttribute)!;
            }

            return ToCamelCase(property.Name);
        }

        private static string ToCamelCase(string name)
        {
            if (string.IsNullOrEmpty(name) || char.IsLower(name[0]))
            {
                return name;
            }

            return char.ToLowerInvariant(name[0]) + name[1..];
        }

        private static object? ConvertToType(Type targetType, object? rawValue)
        {
            if (rawValue == null)
            {
                return null;
            }

            var effectiveType = Nullable.GetUnderlyingType(targetType) ?? targetType;
            if (effectiveType == typeof(object) || effectiveType.IsInstanceOfType(rawValue))
            {
                return rawValue;
            }

            if (IsInput(effectiveType))
            {
                var innerType = effectiveType.GetGenericArguments()[0];
                var value = ConvertToType(innerType, rawValue);
                return WrapInput(innerType, value!);
            }

            if (IsInputList(effectiveType))
            {
                var elementType = effectiveType.GetGenericArguments()[0];
                return WrapInputList(elementType, ConvertToList(elementType, rawValue));
            }

            if (IsInputMap(effectiveType))
            {
                var valueType = effectiveType.GetGenericArguments()[0];
                return WrapInputMap(valueType, ConvertToDictionary(valueType, rawValue));
            }

            if (IsUnion(effectiveType))
            {
                return ConvertToUnion(effectiveType, rawValue);
            }

            if (effectiveType.IsEnum)
            {
                return Enum.Parse(effectiveType, rawValue.ToString()!, true);
            }

            if (IsPulumiEnum(effectiveType))
            {
                return ConvertPulumiEnum(effectiveType, rawValue);
            }

            if (effectiveType == typeof(string))
            {
                return rawValue.ToString()!;
            }

            if (effectiveType == typeof(int))
            {
                return Convert.ToInt32(rawValue, CultureInfo.InvariantCulture);
            }

            if (effectiveType == typeof(bool))
            {
                return Convert.ToBoolean(rawValue, CultureInfo.InvariantCulture);
            }

            if (TryGetDictionary(rawValue, out var dictionary))
            {
                return CreateObject(effectiveType, dictionary);
            }

            return Convert.ChangeType(rawValue, effectiveType, CultureInfo.InvariantCulture);
        }

        private static T Bind<T>(IReadOnlyDictionary<string, object> values)
        {
            return (T)CreateObject(typeof(T), values);
        }

        private static object CreateObject(Type targetType, IReadOnlyDictionary<string, object> values)
        {
            var instance = Activator.CreateInstance(targetType)
                ?? throw new InvalidOperationException($"Unable to create mock instance of {targetType.FullName}");

            foreach (var property in targetType.GetProperties(BindingFlags.Instance | BindingFlags.Public).Where(property => property.CanWrite))
            {
                var propertyName = GetPropertyName(property);
                if (!TryGetValue(values, propertyName, out var rawValue))
                {
                    continue;
                }

                property.SetValue(instance, ConvertToType(property.PropertyType, rawValue));
            }

            return instance;
        }

        private static bool TryGetDictionary(object rawValue, out IReadOnlyDictionary<string, object> dictionary)
        {
            if (rawValue is IReadOnlyDictionary<string, object> readOnlyDictionary)
            {
                dictionary = readOnlyDictionary;
                return true;
            }

            if (rawValue is IEnumerable<KeyValuePair<string, object>> keyValuePairs)
            {
                dictionary = keyValuePairs.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
                return true;
            }

            if (rawValue is IDictionary nonGenericDictionary)
            {
                var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                foreach (DictionaryEntry item in nonGenericDictionary)
                {
                    if (item.Key is string key && item.Value != null)
                    {
                        values[key] = item.Value;
                    }
                }

                dictionary = values;
                return true;
            }

            dictionary = null!;
            return false;
        }

        private static bool TryGetValue(IReadOnlyDictionary<string, object> values, string key, out object value)
        {
            if (values.TryGetValue(key, out value!))
            {
                return true;
            }

            var match = values.FirstOrDefault(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(match.Key))
            {
                value = match.Value;
                return true;
            }

            value = null!;
            return false;
        }

        private static bool IsInput(Type type) => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Input<>);
        private static bool IsInputList(Type type) => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(InputList<>);
        private static bool IsInputMap(Type type) => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(InputMap<>);
        private static bool IsUnion(Type type) => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Union<,>);

        private static bool IsPulumiEnum(Type type)
        {
            return type.GetCustomAttribute<EnumTypeAttribute>() != null;
        }

        private static object ConvertPulumiEnum(Type enumType, object rawValue)
        {
            var raw = rawValue.ToString();
            foreach (var property in enumType.GetProperties(BindingFlags.Public | BindingFlags.Static))
            {
                var candidate = property.GetValue(null)!;
                var valueField = enumType.GetField("_value", BindingFlags.NonPublic | BindingFlags.Instance);
                if (valueField?.GetValue(candidate)?.ToString() == raw)
                {
                    return candidate;
                }
            }

            throw new InvalidOperationException($"Unable to convert '{rawValue}' to {enumType.Name}.");
        }

        private static object WrapInput(Type innerType, object value)
        {
            var inputType = typeof(Input<>).MakeGenericType(innerType);
            var implicitOperator = inputType.GetMethod("op_Implicit", [innerType]);
            if (implicitOperator != null)
            {
                return implicitOperator.Invoke(null, [value])!;
            }

            throw new InvalidOperationException($"Unable to wrap {innerType.Name} into Input<{innerType.Name}>.");
        }

        private static object WrapInputList(Type elementType, object list)
        {
            var listType = typeof(List<>).MakeGenericType(elementType);
            var inputListType = typeof(InputList<>).MakeGenericType(elementType);
            var implicitOperator = inputListType.GetMethod("op_Implicit", BindingFlags.Public | BindingFlags.Static, null, [listType], null)
                ?? throw new InvalidOperationException($"Unable to wrap List<{elementType.Name}> into InputList<{elementType.Name}>.");

            return implicitOperator.Invoke(null, [list])!;
        }

        private static object WrapInputMap(Type valueType, object dictionary)
        {
            var dictionaryType = typeof(Dictionary<,>).MakeGenericType(typeof(string), valueType);
            var inputMapType = typeof(InputMap<>).MakeGenericType(valueType);
            var implicitOperator = inputMapType.GetMethod("op_Implicit", BindingFlags.Public | BindingFlags.Static, null, [dictionaryType], null)
                ?? throw new InvalidOperationException($"Unable to wrap Dictionary<string,{valueType.Name}> into InputMap<{valueType.Name}>.");

            return implicitOperator.Invoke(null, [dictionary])!;
        }

        private static object ConvertToUnion(Type unionType, object rawValue)
        {
            foreach (var (methodName, targetType) in new[]
            {
                ("FromT0", unionType.GetGenericArguments()[0]),
                ("FromT1", unionType.GetGenericArguments()[1]),
            })
            {
                try
                {
                    var value = ConvertToType(targetType, rawValue);
                    return unionType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [value])!;
                }
                catch
                {
                }
            }

            throw new InvalidOperationException($"Unable to convert '{rawValue}' to {unionType.Name}.");
        }

        private static object ConvertToList(Type elementType, object rawValue)
        {
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elementType))!;
            if (rawValue is IEnumerable enumerable and not string)
            {
                foreach (var item in enumerable)
                {
                    list.Add(ConvertToType(elementType, item));
                }

                return list;
            }

            list.Add(ConvertToType(elementType, rawValue));
            return list;
        }

        private static object ConvertToDictionary(Type valueType, object rawValue)
        {
            if (!TryGetDictionary(rawValue, out var rawDictionary))
            {
                throw new InvalidOperationException($"Unable to convert '{rawValue}' to Dictionary<string,{valueType.Name}>.");
            }

            var dictionary = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string), valueType))!;
            foreach (var (key, value) in rawDictionary)
            {
                dictionary[key] = ConvertToType(valueType, value);
            }

            return dictionary;
        }

        public sealed class ResourceMockContext<TArgs>(MockResourceArgs args)
        {
            public string Name { get; } = args.Name;
            public string Token { get; } = args.Type;
            public string Id { get; } = args.Id ?? $"{args.Name}_id";
            public TArgs Inputs => Bind<TArgs>(args.Inputs);
            public IReadOnlyDictionary<string, object> RawInputs { get; } = args.Inputs;
        }

        public sealed class CallMockContext<TArgs>(MockCallArgs args)
        {
            public string Token { get; } = args.Token;
            public TArgs Inputs => Bind<TArgs>(args.Args);
            public IReadOnlyDictionary<string, object> RawInputs { get; } = args.Args;
        }

    }
}
