using LiveArch.Deployment.Converters;
using Pulumi;
using System.Reflection;
using Type = System.Type;

namespace LiveArch.Deployment
{
    /// <summary>
    /// Resolves dot-separated output paths from Pulumi resources, invoke results, and output payload objects.
    /// </summary>
    /// <remarks>
    /// The reader understands both resource members annotated with <see cref="OutputAttribute"/> and
    /// output payload members exposed through <see cref="OutputTypeAttribute"/>. Nested output traversal is
    /// projected through <c>Apply</c> so callers can continue working with Pulumi outputs.
    /// </remarks>
    public sealed class OutputValueReader
    {
        private readonly Dictionary<Type, Dictionary<string, MemberInfo>> outputMembersCache = new();

        /// <summary>
        /// Resolves a value from a source object using a dot-separated output path.
        /// </summary>
        /// <param name="source">Resource, invoke result, or output payload object.</param>
        /// <param name="path">Output path such as <c>identity.principalId</c>.</param>
        /// <returns>The resolved value, a projected <c>Output&lt;T&gt;</c>, or <c>null</c> when the path does not exist.</returns>
        public object? GetValue(object source, string path)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentException.ThrowIfNullOrWhiteSpace(path);

            return GetValueCore(source, path);
        }

        /// <summary>
        /// Resolves a path segment by segment, switching to output projection when an <c>Output&lt;T&gt;</c> is encountered.
        /// </summary>
        private object? GetValueCore(object source, string path)
        {
            var sourceType = source.GetType();
            if (ConversionTypeHelpers.IsOutput(sourceType))
            {
                var innerType = sourceType.GetGenericArguments()[0];
                return ProjectNestedOutput(source, innerType, path);
            }

            var parts = path.Split('.', 2);
            var head = parts[0];
            var tail = parts.Length > 1 ? parts[1] : null;

            if (!TryGetOutputMember(sourceType, head, out var member))
            {
                return null;
            }

            var value = ReadMemberValue(member!, source);
            if (value == null)
            {
                return null;
            }

            if (tail == null)
            {
                return value;
            }

            if (ConversionTypeHelpers.IsOutput(value.GetType()))
            {
                var innerType = value.GetType().GetGenericArguments()[0];
                return ProjectNestedOutput(value, innerType, tail);
            }

            return GetValueCore(value, tail);
        }

        /// <summary>
        /// Projects a nested member path from an <c>Output&lt;T&gt;</c> into another output using <c>Apply</c>.
        /// </summary>
        /// <param name="outputObj">Source output object.</param>
        /// <param name="innerType">Inner payload type carried by the output.</param>
        /// <param name="tailPath">Remaining path to evaluate inside the payload.</param>
        /// <returns>A projected output for the requested nested member.</returns>
        private object ProjectNestedOutput(object outputObj, Type innerType, string tailPath)
        {
            var parts = tailPath.Split('.', 2);
            var head = parts[0];
            var tail = parts.Length > 1 ? parts[1] : null;

            if (!TryGetOutputMember(innerType, head, out var member))
            {
                throw new InvalidOperationException($"Output member '{head}' was not found on type {innerType.FullName}.");
            }

            var memberType = GetMemberType(member!);
            if (ConversionTypeHelpers.IsOutput(memberType))
            {
                throw new NotSupportedException($"Nested Output members inside projected output paths are not supported: '{tailPath}'.");
            }

            var projected = ConversionTypeHelpers.ProjectOutput(
                outputObj,
                innerType,
                memberType,
                current => current == null ? null! : ReadMemberValue(member!, current)!);

            return tail == null
                ? projected
                : ProjectNestedOutput(projected, memberType, tail);
        }

        /// <summary>
        /// Attempts to locate a readable output member by its logical output name.
        /// </summary>
        private bool TryGetOutputMember(Type type, string name, out MemberInfo? member)
        {
            member = GetOutputMembers(type).GetValueOrDefault(name);
            return member != null;
        }

        /// <summary>
        /// Builds and caches a case-insensitive map of logical output names to CLR members.
        /// </summary>
        private Dictionary<string, MemberInfo> GetOutputMembers(Type type)
        {
            if (outputMembersCache.TryGetValue(type, out var cached))
            {
                return cached;
            }

            var members = new Dictionary<string, MemberInfo>(StringComparer.OrdinalIgnoreCase);

            foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                var outputAttribute = property.GetCustomAttribute<OutputAttribute>();
                if (outputAttribute != null)
                {
                    members[outputAttribute.Name] = property;
                }
            }

            if (type.GetCustomAttribute<OutputTypeAttribute>() != null)
            {
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public))
                {
                    members[ToCamelCase(field.Name)] = field;
                }

                foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
                {
                    members[ToCamelCase(property.Name)] = property;
                }
            }

            outputMembersCache[type] = members;
            return members;
        }

        /// <summary>
        /// Reads the value of a reflected field or property from the supplied source instance.
        /// </summary>
        private static object? ReadMemberValue(MemberInfo member, object source)
        {
            return member switch
            {
                PropertyInfo property => property.GetValue(source),
                FieldInfo field => field.GetValue(source),
                _ => null,
            };
        }

        /// <summary>
        /// Returns the CLR type represented by a reflected field or property.
        /// </summary>
        private static Type GetMemberType(MemberInfo member)
        {
            return member switch
            {
                PropertyInfo property => property.PropertyType,
                FieldInfo field => field.FieldType,
                _ => throw new InvalidOperationException($"Unsupported member type {member.MemberType}."),
            };
        }

        /// <summary>
        /// Converts a CLR member name to the camelCase shape commonly used by Pulumi output payloads.
        /// </summary>
        private static string ToCamelCase(string name)
        {
            if (string.IsNullOrEmpty(name) || char.IsLower(name[0]))
            {
                return name;
            }

            return char.ToLowerInvariant(name[0]) + name[1..];
        }
    }
}
