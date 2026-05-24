using Pulumi;
using Pulumi.Testing;
using System.Collections;
using System.Collections.Immutable;
using System.Reflection;

namespace LiveArch.Deployment.Export.Testing
{
    public sealed class RecordingMocks : IMocks
    {
        private static readonly PropertyInfo InputAttributeNameProperty = typeof(InputAttribute).GetProperty("Name", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private readonly Dictionary<string, List<Func<MockResourceArgs, IReadOnlyDictionary<string, object>>>> resourceBuilders = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<Func<MockCallArgs, IReadOnlyDictionary<string, object>>>> callBuilders = new(StringComparer.OrdinalIgnoreCase);

        public List<RecordedResource> Resources { get; } = [];
        public List<RecordedInvoke> Invokes { get; } = [];

        public Task<(string? id, object state)> NewResourceAsync(MockResourceArgs args)
        {
            args.Id ??= $"{args.Name}_id";

            var inputs = ToDictionary(args.Inputs).ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);
            var state = BuildDefaultResourceState(args, inputs.ToDictionary()).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
            ApplyBuilders(args.Type, args, state);
            var immutableState = state.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);

            Resources.Add(new RecordedResource(args.Name, args.Type, args.Id, inputs, immutableState));
            return Task.FromResult((args.Id, (object)immutableState));
        }

        public Task<object> CallAsync(MockCallArgs args)
        {
            var arguments = ToDictionary(args.Args).ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);
            var result = BuildDefaultCallState(args, arguments.ToDictionary()).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
            ApplyBuilders(args.Token, args, result);
            var immutableResult = result.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);

            Invokes.Add(new RecordedInvoke(args.Token, arguments, immutableResult));
            return Task.FromResult((object)immutableResult);
        }

        public RecordingMocks AddResourceBuilder(string token, Func<MockResourceArgs, IReadOnlyDictionary<string, object>> builder)
        {
            if (!resourceBuilders.TryGetValue(token, out var builders))
            {
                builders = [];
                resourceBuilders[token] = builders;
            }

            builders.Add(builder);
            return this;
        }

        public RecordingMocks AddCallBuilder(string token, Func<MockCallArgs, IReadOnlyDictionary<string, object>> builder)
        {
            if (!callBuilders.TryGetValue(token, out var builders))
            {
                builders = [];
                callBuilders[token] = builders;
            }

            builders.Add(builder);
            return this;
        }

        private void ApplyBuilders(string token, MockResourceArgs args, Dictionary<string, object> state)
        {
            if (!resourceBuilders.TryGetValue(token, out var builders))
            {
                return;
            }

            foreach (var builder in builders)
            {
                MergeInto(state, builder(args));
            }
        }

        private void ApplyBuilders(string token, MockCallArgs args, Dictionary<string, object> state)
        {
            if (!callBuilders.TryGetValue(token, out var builders))
            {
                return;
            }

            foreach (var builder in builders)
            {
                MergeInto(state, builder(args));
            }
        }

        private static Dictionary<string, object> BuildDefaultResourceState(MockResourceArgs args, Dictionary<string, object> inputs)
        {
            var state = new Dictionary<string, object>(inputs, StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = args.Id!,
                ["name"] = args.Name,
                ["type"] = args.Type
            };

            return state;
        }

        private static Dictionary<string, object> BuildDefaultCallState(MockCallArgs args, Dictionary<string, object> arguments)
        {
            var fallbackName = (args.Token ?? "invoke").Split(':').LastOrDefault() ?? "invoke";
            var state = new Dictionary<string, object>(arguments, StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = $"{args.Token}-id",
                ["name"] = InferName(arguments, fallbackName)
            };

            return state;
        }

        private static void MergeInto(IDictionary<string, object> target, IReadOnlyDictionary<string, object> source)
        {
            foreach (var (key, value) in source)
            {
                target[key] = NormalizeValue(value);
            }
        }

        private static Dictionary<string, object> ToDictionary(object? value)
        {
            if (value == null)
            {
                return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            }

            if (value is ImmutableDictionary<string, object> immutableDictionary)
            {
                return immutableDictionary.ToDictionary(kv => kv.Key, kv => NormalizeValue(kv.Value), StringComparer.OrdinalIgnoreCase);
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

            if (value is IEnumerable enumerable and not string)
            {
                if (value is IDictionary || value is IReadOnlyDictionary<string, object> || value is IEnumerable<KeyValuePair<string, object>>)
                {
                    return ToDictionary(value);
                }

                return enumerable.Cast<object?>().Select(NormalizeValue).ToList();
            }

            return ToDictionary(value);
        }

        private static string GetPropertyName(PropertyInfo property)
        {
            var inputAttribute = property.GetCustomAttribute<InputAttribute>();
            if (inputAttribute != null)
            {
                return (string)InputAttributeNameProperty.GetValue(inputAttribute)!;
            }

            return char.ToLowerInvariant(property.Name[0]) + property.Name[1..];
        }

        private static string InferName(IReadOnlyDictionary<string, object> values, string fallback)
        {
            var nameEntry = values.FirstOrDefault(kv => kv.Key.EndsWith("Name", StringComparison.OrdinalIgnoreCase) && kv.Value is string stringValue && !string.IsNullOrWhiteSpace(stringValue));
            return nameEntry.Value as string ?? fallback;
        }
    }
}
