using LiveArch.Deployment.Converters;
using Pulumi;
using System.Reflection;
using Type = System.Type;

namespace LiveArch.Deployment
{
    public sealed class OutputValueReader
    {
        private readonly Dictionary<Type, Dictionary<string, MemberInfo>> outputMembersCache = new();

        public object? GetValue(object source, string path)
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentException.ThrowIfNullOrWhiteSpace(path);

            return GetValueCore(source, path);
        }

        private object? GetValueCore(object source, string path)
        {
            var parts = path.Split('.', 2);
            var head = parts[0];
            var tail = parts.Length > 1 ? parts[1] : null;

            if (!TryGetOutputMember(source.GetType(), head, out var member))
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

        private bool TryGetOutputMember(Type type, string name, out MemberInfo? member)
        {
            member = GetOutputMembers(type).GetValueOrDefault(name);
            return member != null;
        }

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

        private static object? ReadMemberValue(MemberInfo member, object source)
        {
            return member switch
            {
                PropertyInfo property => property.GetValue(source),
                FieldInfo field => field.GetValue(source),
                _ => null,
            };
        }

        private static Type GetMemberType(MemberInfo member)
        {
            return member switch
            {
                PropertyInfo property => property.PropertyType,
                FieldInfo field => field.FieldType,
                _ => throw new InvalidOperationException($"Unsupported member type {member.MemberType}."),
            };
        }

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
