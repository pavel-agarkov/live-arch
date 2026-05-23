using LiveArch.Deployment.Expressions;
using LiveArch.Deployment.Observability;
using LiveArch.Deployment.Converters;
using Pulumi;
using Structurizr;
using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace LiveArch.Deployment.Export.CSharp
{
    public sealed class CSharpPulumiProjectExporter : IStructurizrDeploymentObserver
    {
        private static readonly PropertyInfo InputAttrNameProp = typeof(InputAttribute).GetProperty("Name", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private readonly List<IObservedResource> observedResources = [];

        public void OnResourceCreated(
            ModelItem node,
            StructurizrDeploymentProcessor.ResourceScope scope,
            object resource,
            Type resourceType,
            string resourceName,
            CustomResourceOptions? options,
            IReadOnlyCollection<Resource> dependsOn,
            CreatedResourceExpressionModel expressionModel)
        {
            observedResources.Add(new CreatedResourceObservation(node, scope.Id, resource, resourceType, resourceName, options, dependsOn, expressionModel));
        }

        public void OnResourceReferenced(
            ModelItem node,
            StructurizrDeploymentProcessor.ResourceScope scope,
            object resource,
            string resourceName,
            MethodInfo invokeMethod,
            InvokeOptions? options,
            IReadOnlyCollection<Resource> dependsOn,
            ReferencedResourceExpressionModel expressionModel)
        {
            observedResources.Add(new ReferencedResourceObservation(node, scope.Id, resource, resourceName, invokeMethod, options, dependsOn, expressionModel));
        }

        public CSharpPulumiProjectExport Export(string deployment, CSharpPulumiProjectExporterOptions? options = null)
        {
            options ??= new CSharpPulumiProjectExporterOptions();

            var projectName = string.IsNullOrWhiteSpace(options.ProjectName)
                ? CreateDefaultProjectName(deployment)
                : options.ProjectName.Trim();

            var exportableResources = observedResources
                .Where(ShouldExportResource)
                .ToList();

            var keyByResource = exportableResources
                .GroupBy(resource => resource.Resource, ReferenceEqualityComparer.Instance)
                .ToDictionary(group => group.Key, group => CreateResourceKey(group.First()), ReferenceEqualityComparer.Instance);

            var variableNameByKey = CreateVariableNames(exportableResources);

            var model = new CSharpPulumiProjectModel(
                deployment,
                projectName,
                options.RootNamespace,
                NormalizeNamespaces(options.AdditionalNamespaces),
                ResolvePackageReferences(exportableResources, options.AdditionalPackageReferences),
                ResolveProjectReferences(exportableResources),
                exportableResources.OfType<CreatedResourceObservation>().Count(),
                exportableResources.OfType<ReferencedResourceObservation>().Count(),
                [.. exportableResources.Select((resource, index) => BuildResourceModel(resource, index, keyByResource, variableNameByKey))]);

            return CSharpPulumiProjectWriter.Export(model, options.OutputDirectory);
        }

        private static CSharpPulumiResourceModel BuildResourceModel(
            IObservedResource resource,
            int index,
            IReadOnlyDictionary<object, string> keyByResource,
            IReadOnlyDictionary<string, string> variableNameByKey)
        {
            var key = CreateResourceKey(resource);
            var dependsOnKeys = resource.DependsOn
                .Select(dependency => keyByResource.TryGetValue(dependency, out var dependencyKey) ? dependencyKey : dependency.GetResourceName())
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            var variableName = variableNameByKey[key];
            var creationStatement = resource switch
            {
                CreatedResourceObservation created => RenderCreatedResource(created, variableName, dependsOnKeys, keyByResource, variableNameByKey),
                ReferencedResourceObservation referenced => RenderReferencedResource(referenced, variableName, dependsOnKeys, keyByResource, variableNameByKey),
                _ => throw new NotSupportedException($"Unsupported observed resource type '{resource.GetType().FullName}'.")
            };

            return new CSharpPulumiResourceModel(
                key,
                resource.Kind,
                resource.Node.ToString(),
                resource.ScopeId,
                resource.CodeGenType.FullName ?? resource.CodeGenType.Name,
                variableName,
                creationStatement,
                dependsOnKeys);
        }

        private static string RenderCreatedResource(
            CreatedResourceObservation resource,
            string variableName,
            IReadOnlyCollection<string> dependsOnKeys,
            IReadOnlyDictionary<object, string> keyByResource,
            IReadOnlyDictionary<string, string> variableNameByKey)
        {
            var optionsCode = RenderCustomResourceOptions(dependsOnKeys, variableNameByKey);
            var argsCode = RenderTrackedObjectInitializer(GetCreatedArgsType(resource.ResourceType), 2, resource.ExpressionModel, string.Empty, keyByResource, variableNameByKey);
            var resourceTypeName = GetGlobalTypeName(resource.ResourceType);

            return $"var {variableName} = new {resourceTypeName}({ToCSharpString(resource.ResourceName)}, {argsCode}, {optionsCode});";
        }

        private static string RenderReferencedResource(
            ReferencedResourceObservation resource,
            string variableName,
            IReadOnlyCollection<string> dependsOnKeys,
            IReadOnlyDictionary<object, string> keyByResource,
            IReadOnlyDictionary<string, string> variableNameByKey)
        {
            var argsCode = RenderTrackedObjectInitializer(resource.InvokeMethod.GetParameters()[0].ParameterType, 2, resource.ExpressionModel, string.Empty, keyByResource, variableNameByKey);
            var optionsCode = RenderInvokeOptions(resource.InvokeMethod, dependsOnKeys, variableNameByKey);
            var declaringTypeName = GetGlobalTypeName(resource.InvokeMethod.DeclaringType!);
            var invocation = optionsCode == null
                ? $"{declaringTypeName}.{resource.InvokeMethod.Name}({argsCode})"
                : $"{declaringTypeName}.{resource.InvokeMethod.Name}({argsCode}, {optionsCode})";

            return $"var {variableName} = {invocation};";
        }

        private static string RenderCustomResourceOptions(IReadOnlyCollection<string> dependsOnKeys, IReadOnlyDictionary<string, string> variableNameByKey)
        {
            if (dependsOnKeys.Count == 0)
            {
                return "null";
            }

            var dependsOnCode = string.Join(", ", dependsOnKeys.Select(key => variableNameByKey[key]));
            return $"new global::Pulumi.CustomResourceOptions {{ DependsOn = {{ {dependsOnCode} }} }}";
        }

        private static string? RenderInvokeOptions(MethodInfo invokeMethod, IReadOnlyCollection<string> dependsOnKeys, IReadOnlyDictionary<string, string> variableNameByKey)
        {
            var optionsType = invokeMethod.GetParameters()[1].ParameterType;
            if (optionsType == typeof(InvokeOutputOptions) && dependsOnKeys.Count > 0)
            {
                var dependsOnCode = string.Join(", ", dependsOnKeys.Select(key => variableNameByKey[key]));
                return $"new global::Pulumi.InvokeOutputOptions {{ DependsOn = {{ {dependsOnCode} }} }}";
            }

            return null;
        }

        private static string RenderTrackedObjectInitializer(
            Type declaredType,
            int indentLevel,
            ResourceExpressionModel expressionModel,
            string currentPath,
            IReadOnlyDictionary<object, string> keyByResource,
            IReadOnlyDictionary<string, string> variableNameByKey)
        {
            var indent = new string(' ', indentLevel * 4);
            var childIndent = new string(' ', (indentLevel + 1) * 4);
            var entries = new List<string>();

            foreach (var property in declaredType.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (!property.CanRead)
                {
                    continue;
                }

                var inputName = GetInputName(property) ?? property.Name;
                var propertyPath = CombinePath(currentPath, inputName);
                string? propertyValueCode = null;

                if (expressionModel.Assignments.TryGetValue(propertyPath, out var expression))
                {
                    propertyValueCode = RenderExpression(expression, property.PropertyType, indentLevel + 1, keyByResource, variableNameByKey);
                }
                else if (HasNestedAssignments(expressionModel, propertyPath))
                {
                    var nestedType = GetUnderlyingArgsType(property.PropertyType);
                    propertyValueCode = RenderTrackedObjectInitializer(nestedType, indentLevel + 1, expressionModel, propertyPath, keyByResource, variableNameByKey);
                }

                if (propertyValueCode == null)
                {
                    continue;
                }

                entries.Add($"{childIndent}{property.Name} = {propertyValueCode}");
            }

            if (entries.Count == 0)
            {
                return $"new {GetGlobalTypeName(declaredType)}()";
            }

            return $"new {GetGlobalTypeName(declaredType)}()\n{indent}{{\n{string.Join(",\n", entries)}\n{indent}}}";
        }

        private static string RenderValue(
            object value,
            Type declaredType,
            int indentLevel,
            IReadOnlyDictionary<object, string> keyByResource,
            IReadOnlyDictionary<string, string> variableNameByKey)
        {
            if (TryRenderTypedValue(value, declaredType, keyByResource, variableNameByKey, indentLevel, out var typedValue))
            {
                return typedValue;
            }

            if (IsPulumiWrapperType(value.GetType()) || IsPulumiWrapperType(declaredType))
            {
                return "default!";
            }

            if (value is string text)
            {
                return ToCSharpString(text);
            }

            if (value is bool boolean)
            {
                return boolean ? "true" : "false";
            }

            if (value is Enum enumValue)
            {
                return $"{GetGlobalTypeName(enumValue.GetType())}.{enumValue}";
            }

            if (IsNumeric(value))
            {
                return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "0";
            }

            if (value is IEnumerable enumerable && value is not string)
            {
                var itemType = GetCollectionItemType(declaredType) ?? typeof(object);
                var renderedItems = enumerable.Cast<object?>()
                    .Where(item => item != null)
                    .Select(item => RenderValue(item!, itemType, indentLevel + 1, keyByResource, variableNameByKey))
                    .ToArray();

                return renderedItems.Length == 0
                    ? "[]"
                    : $"[{string.Join(", ", renderedItems)}]";
            }

            if (CanRenderAsLiteralObjectInitializer(declaredType, value.GetType()))
            {
                return RenderLiteralObjectInitializer(value, value.GetType(), indentLevel + 1, keyByResource, variableNameByKey);
            }

            return "default!";
        }

        private static bool TryRenderTypedValue(
            object value,
            Type declaredType,
            IReadOnlyDictionary<object, string> keyByResource,
            IReadOnlyDictionary<string, string> variableNameByKey,
            int indentLevel,
            out string rendered)
        {
            var renderTargetType = GetPreferredRenderType(declaredType, value);

            if (renderTargetType == typeof(string) && value is string text)
            {
                rendered = ToCSharpString(text);
                return true;
            }

            if (renderTargetType == typeof(bool))
            {
                var boolValue = value is bool boolean
                    ? boolean
                    : bool.Parse(value.ToString()!);
                rendered = boolValue ? "true" : "false";
                return true;
            }

            if (renderTargetType.IsEnum)
            {
                var enumValue = value is Enum enumInstance
                    ? enumInstance
                    : Enum.Parse(renderTargetType, value.ToString()!, ignoreCase: true) as Enum
                        ?? throw new InvalidOperationException($"Cannot render enum value '{value}' for {renderTargetType.FullName}.");
                rendered = $"{GetGlobalTypeName(renderTargetType)}.{enumValue}";
                return true;
            }

            if (ConversionTypeHelpers.IsPulumiEnum(renderTargetType))
            {
                rendered = RenderPulumiEnumValue(renderTargetType, value);
                return true;
            }

            if (IsNumericTargetType(renderTargetType))
            {
                var numericValue = value is string numericText
                    ? Convert.ChangeType(numericText, renderTargetType, CultureInfo.InvariantCulture)
                    : value;
                rendered = Convert.ToString(numericValue, CultureInfo.InvariantCulture) ?? "0";
                return true;
            }

            rendered = string.Empty;
            return false;
        }

        private static string RenderLiteralObjectInitializer(
            object instance,
            Type declaredType,
            int indentLevel,
            IReadOnlyDictionary<object, string> keyByResource,
            IReadOnlyDictionary<string, string> variableNameByKey)
        {
            var indent = new string(' ', indentLevel * 4);
            var childIndent = new string(' ', (indentLevel + 1) * 4);
            var entries = new List<string>();

            foreach (var property in declaredType.GetProperties(BindingFlags.Instance | BindingFlags.Public))
            {
                if (!property.CanRead)
                {
                    continue;
                }

                var value = property.GetValue(instance);
                if (value == null)
                {
                    continue;
                }

                entries.Add($"{childIndent}{property.Name} = {RenderValue(value, property.PropertyType, indentLevel + 1, keyByResource, variableNameByKey)}");
            }

            if (entries.Count == 0)
            {
                return $"new {GetGlobalTypeName(declaredType)}()";
            }

            return $"new {GetGlobalTypeName(declaredType)}()\n{indent}{{\n{string.Join(",\n", entries)}\n{indent}}}";
        }

        private static string RenderExpression(
            ValueExpressionModel expression,
            Type declaredType,
            int indentLevel,
            IReadOnlyDictionary<object, string> keyByResource,
            IReadOnlyDictionary<string, string> variableNameByKey)
        {
            return expression switch
            {
                DirectValueExpressionModel direct => RenderDirectExpression(direct, declaredType, indentLevel, keyByResource, variableNameByKey),
                DependencyValueExpressionModel dependency => RenderDependencyExpression(dependency, keyByResource, variableNameByKey),
                _ => "default!"
            };
        }

        private static string RenderDirectExpression(
            DirectValueExpressionModel expression,
            Type declaredType,
            int indentLevel,
            IReadOnlyDictionary<object, string> keyByResource,
            IReadOnlyDictionary<string, string> variableNameByKey)
        {
            var rendered = expression.Value == null
                ? "null"
                : RenderValue(expression.Value, declaredType, indentLevel, keyByResource, variableNameByKey);

            if (string.IsNullOrWhiteSpace(expression.ConverterName))
            {
                return rendered;
            }

            return $"{rendered} /* converter: {expression.ConverterName} */";
        }

        private static string RenderDependencyExpression(
            DependencyValueExpressionModel expression,
            IReadOnlyDictionary<object, string> keyByResource,
            IReadOnlyDictionary<string, string> variableNameByKey)
        {
            var sourceName = keyByResource.TryGetValue(expression.SourceResource, out var resourceKey) && variableNameByKey.TryGetValue(resourceKey, out var variableName)
                ? variableName
                : expression.SourceResource is Resource resource
                    ? resource.GetResourceName()
                    : "resource";

            var suffix = new StringBuilder();
            if (expression.Transformers.Count > 0)
            {
                suffix.Append($" | transformers: {string.Join(" -> ", expression.Transformers)}");
            }

            if (!string.IsNullOrWhiteSpace(expression.ConverterName))
            {
                suffix.Append($" | converter: {expression.ConverterName}");
            }

            return $"default! /* {sourceName}.{expression.SourcePath}{suffix} */";
        }

        private static bool CanRenderAsLiteralObjectInitializer(Type declaredType, Type runtimeType)
        {
            var effectiveType = Nullable.GetUnderlyingType(runtimeType) ?? runtimeType;
            if (effectiveType == typeof(object) || effectiveType == typeof(Type))
            {
                return false;
            }

            if (effectiveType.Namespace == null)
            {
                return false;
            }

            if (effectiveType.Namespace.StartsWith("System", StringComparison.Ordinal))
            {
                return false;
            }

            return effectiveType.GetConstructor(Type.EmptyTypes) != null;
        }

        private static Type GetPreferredRenderType(Type declaredType, object value)
        {
            var currentType = Nullable.GetUnderlyingType(declaredType) ?? declaredType;

            while (currentType.IsGenericType && currentType.GetGenericTypeDefinition() == typeof(Input<>))
            {
                currentType = currentType.GetGenericArguments()[0];
            }

            if (ConversionTypeHelpers.IsGenericInputUnion(currentType) || ConversionTypeHelpers.IsGenericUnion(currentType))
            {
                foreach (var candidateType in currentType.GetGenericArguments().Select(argument => Nullable.GetUnderlyingType(argument) ?? argument))
                {
                    if (ConversionTypeHelpers.IsPulumiEnum(candidateType) && TryRenderPulumiEnumValue(candidateType, value, out _))
                    {
                        return candidateType;
                    }

                    if (candidateType == typeof(bool) && (value is bool || bool.TryParse(value.ToString(), out _)))
                    {
                        return candidateType;
                    }
                }
            }

            return currentType;
        }

        private static string RenderPulumiEnumValue(Type enumType, object value)
        {
            return TryRenderPulumiEnumValue(enumType, value, out var rendered)
                ? rendered
                : throw new InvalidOperationException($"Cannot render Pulumi enum value '{value}' for {enumType.FullName}.");
        }

        private static bool TryRenderPulumiEnumValue(Type enumType, object value, out string rendered)
        {
            var stringValue = value.ToString();
            var valueField = enumType.GetField("_value", BindingFlags.NonPublic | BindingFlags.Instance);

            foreach (var property in enumType.GetProperties(BindingFlags.Public | BindingFlags.Static))
            {
                var enumValue = property.GetValue(null)!;
                var enumString = valueField?.GetValue(enumValue)?.ToString();
                if (string.Equals(enumString, stringValue, StringComparison.OrdinalIgnoreCase))
                {
                    rendered = $"{GetGlobalTypeName(enumType)}.{property.Name}";
                    return true;
                }
            }

            rendered = string.Empty;
            return false;
        }

        private static bool IsNumericTargetType(Type type)
        {
            return type == typeof(byte) ||
                type == typeof(sbyte) ||
                type == typeof(short) ||
                type == typeof(ushort) ||
                type == typeof(int) ||
                type == typeof(uint) ||
                type == typeof(long) ||
                type == typeof(ulong) ||
                type == typeof(float) ||
                type == typeof(double) ||
                type == typeof(decimal);
        }

        private static Type GetCreatedArgsType(Type resourceType)
        {
            return resourceType.GetConstructors()[0].GetParameters()[1].ParameterType;
        }

        private static bool HasNestedAssignments(ResourceExpressionModel expressionModel, string propertyPath)
        {
            var prefix = propertyPath + ".";
            return expressionModel.Assignments.Keys.Any(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }

        private static Type GetUnderlyingArgsType(Type type)
        {
            return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Input<>)
                ? type.GetGenericArguments()[0]
                : type;
        }

        private static bool IsPulumiWrapperType(Type type)
        {
            var effectiveType = Nullable.GetUnderlyingType(type) ?? type;
            var fullName = effectiveType.FullName ?? effectiveType.Name;

            if (fullName.StartsWith("Pulumi.Input", StringComparison.Ordinal) ||
                fullName.StartsWith("Pulumi.Output", StringComparison.Ordinal) ||
                fullName.StartsWith("Pulumi.Union", StringComparison.Ordinal))
            {
                return true;
            }

            return effectiveType.Namespace == "Pulumi";
        }

        private static Type? GetCollectionItemType(Type declaredType)
        {
            if (declaredType.IsArray)
            {
                return declaredType.GetElementType();
            }

            if (declaredType.IsGenericType)
            {
                return declaredType.GetGenericArguments().FirstOrDefault();
            }

            return typeof(object);
        }

        private static bool IsNumeric(object value)
        {
            return value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;
        }

        private static string GetGlobalTypeName(Type type)
        {
            if (!type.IsGenericType)
            {
                return $"global::{type.FullName!.Replace('+', '.')}";
            }

            var genericTypeDefinitionName = type.GetGenericTypeDefinition().FullName!;
            var tickIndex = genericTypeDefinitionName.IndexOf('`');
            if (tickIndex >= 0)
            {
                genericTypeDefinitionName = genericTypeDefinitionName[..tickIndex];
            }

            var genericArguments = string.Join(", ", type.GetGenericArguments().Select(GetGlobalTypeName));
            return $"global::{genericTypeDefinitionName.Replace('+', '.')}<{genericArguments}>";
        }

        private static IReadOnlyCollection<string> NormalizeNamespaces(IReadOnlyCollection<string> namespaces)
        {
            return [.. namespaces
                .Where(@namespace => !string.IsNullOrWhiteSpace(@namespace))
                .Select(@namespace => @namespace.Trim())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(@namespace => @namespace, StringComparer.Ordinal)];
        }

        private static IReadOnlyCollection<CSharpPackageReference> ResolvePackageReferences(
            IReadOnlyCollection<IObservedResource> resources,
            IReadOnlyCollection<CSharpPackageReference> additionalPackageReferences)
        {
            var packageReferences = new Dictionary<string, CSharpPackageReference>(StringComparer.OrdinalIgnoreCase)
            {
                ["Pulumi"] = new CSharpPackageReference("Pulumi", "3.106.2")
            };

            foreach (var resource in resources)
            {
                foreach (var packageReference in KnownPackageRegistry.Resolve(resource.CodeGenType))
                {
                    packageReferences[packageReference.PackageId] = packageReference;
                }
            }

            foreach (var packageReference in additionalPackageReferences)
            {
                packageReferences[packageReference.PackageId] = packageReference;
            }

            return [.. packageReferences.Values.OrderBy(reference => reference.PackageId, StringComparer.OrdinalIgnoreCase)];
        }

        private static IReadOnlyCollection<CSharpProjectReference> ResolveProjectReferences(IReadOnlyCollection<IObservedResource> resources)
        {
            var projectReferences = new Dictionary<string, CSharpProjectReference>(StringComparer.OrdinalIgnoreCase);
            foreach (var resource in resources)
            {
                foreach (var projectReference in KnownPackageRegistry.ResolveProjectReferences(resource.CodeGenType))
                {
                    projectReferences[projectReference.ProjectPath] = projectReference;
                }
            }

            return [.. projectReferences.Values.OrderBy(reference => reference.ProjectPath, StringComparer.OrdinalIgnoreCase)];
        }

        private static string CreateDefaultProjectName(string deployment)
        {
            var safeDeploymentName = Regex.Replace(deployment, "[^a-zA-Z0-9_-]", "-").Trim('-');
            return string.IsNullOrWhiteSpace(safeDeploymentName)
                ? "Generated.Deployment.Project"
                : $"Generated.{safeDeploymentName}.Deployment";
        }

        private static IReadOnlyDictionary<string, string> CreateVariableNames(IReadOnlyList<IObservedResource> resources)
        {
            var usedNames = new HashSet<string>(StringComparer.Ordinal);
            var variableNameByKey = new Dictionary<string, string>(StringComparer.Ordinal);

            for (var index = 0; index < resources.Count; index++)
            {
                var resource = resources[index];
                var key = CreateResourceKey(resource);
                var baseName = CreateVariableNameCandidate(resource);
                var variableName = baseName;

                if (!usedNames.Add(variableName))
                {
                    var typedCandidate = baseName + CreateTypeSuffix(resource.CodeGenType);
                    variableName = usedNames.Add(typedCandidate)
                        ? typedCandidate
                        : CreateIndexedVariableName(usedNames, typedCandidate);
                }

                variableNameByKey[key] = variableName;
            }

            return variableNameByKey;
        }

        private static string CreateIndexedVariableName(HashSet<string> usedNames, string baseName)
        {
            var suffix = 2;
            var candidate = baseName;
            while (!usedNames.Add(candidate))
            {
                candidate = $"{baseName}{suffix++}";
            }

            return candidate;
        }

        private static string CreateResourceKey(IObservedResource resource)
        {
            return $"{resource.Kind}:{resource.ScopeId}:{resource.Node}";
        }

        private static string CombinePath(string currentPath, string nextSegment)
        {
            return string.IsNullOrWhiteSpace(currentPath) ? nextSegment : $"{currentPath}.{nextSegment}";
        }

        private static string? GetInputName(PropertyInfo property)
        {
            var inputAttribute = property.GetCustomAttribute<InputAttribute>();
            return inputAttribute == null ? null : (string?)InputAttrNameProp.GetValue(inputAttribute);
        }

        private static string CreateVariableNameCandidate(IObservedResource resource)
        {
            var source = string.IsNullOrWhiteSpace(resource.ResourceName)
                ? resource.Node.ToString()
                : resource.ResourceName;

            var builder = new StringBuilder();
            var uppercaseNext = false;
            foreach (var ch in source)
            {
                if (char.IsLetterOrDigit(ch))
                {
                    if (builder.Length == 0)
                    {
                        if (char.IsDigit(ch))
                        {
                            builder.Append('_');
                        }

                        builder.Append(char.ToLowerInvariant(ch));
                    }
                    else
                    {
                        builder.Append(uppercaseNext ? char.ToUpperInvariant(ch) : ch);
                    }

                    uppercaseNext = false;
                    continue;
                }

                if (ch is '-' or '.' or ' ')
                {
                    uppercaseNext = builder.Length > 0;
                    continue;
                }

                if (builder.Length == 0 || builder[^1] != '_')
                {
                    builder.Append('_');
                }

                uppercaseNext = false;
            }

            var variableName = builder.ToString().Trim('_');
            return string.IsNullOrWhiteSpace(variableName) ? "resource" : variableName;
        }

        private static string CreateTypeSuffix(Type codeGenType)
        {
            var type = codeGenType;
            while (ConversionTypeHelpers.IsOutput(type) || ConversionTypeHelpers.IsGenericInput(type))
            {
                type = type.GetGenericArguments()[0];
            }

            var name = type.Name;
            var tickIndex = name.IndexOf('`');
            if (tickIndex >= 0)
            {
                name = name[..tickIndex];
            }

            return string.IsNullOrWhiteSpace(name) ? "Type" : name;
        }

        private static string ToCSharpString(string value)
        {
            return $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
        }

        private static bool ShouldExportResource(IObservedResource resource)
        {
            var fullName = resource.CodeGenType.FullName ?? resource.CodeGenType.Name;
            return !fullName.StartsWith("LiveArch.Deployment.Controls.", StringComparison.Ordinal);
        }

        private interface IObservedResource
        {
            string Kind { get; }
            ModelItem Node { get; }
            int ScopeId { get; }
            object Resource { get; }
            string ResourceName { get; }
            Type CodeGenType { get; }
            IReadOnlyCollection<Resource> DependsOn { get; }
            ResourceExpressionModel ExpressionModel { get; }
        }

        private sealed record CreatedResourceObservation(
            ModelItem Node,
            int ScopeId,
            object Resource,
            Type ResourceType,
            string ResourceName,
            CustomResourceOptions? Options,
            IReadOnlyCollection<Resource> DependsOn,
            CreatedResourceExpressionModel ExpressionModel) : IObservedResource
        {
            public string Kind => "Created";
            string IObservedResource.ResourceName => ResourceName;
            public Type CodeGenType => ResourceType;
            ResourceExpressionModel IObservedResource.ExpressionModel => ExpressionModel;
        }

        private sealed record ReferencedResourceObservation(
            ModelItem Node,
            int ScopeId,
            object Resource,
            string ResourceName,
            MethodInfo InvokeMethod,
            InvokeOptions? Options,
            IReadOnlyCollection<Resource> DependsOn,
            ReferencedResourceExpressionModel ExpressionModel) : IObservedResource
        {
            public string Kind => "Referenced";
            public Type CodeGenType => InvokeMethod.ReturnType;
            ResourceExpressionModel IObservedResource.ExpressionModel => ExpressionModel;
        }
    }

    public static class CSharpPulumiProjectWriter
    {
        private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

        public static CSharpPulumiProjectExport Export(CSharpPulumiProjectModel model, string? outputDirectory = null)
        {
            var directory = ResolveOutputDirectory(model.Deployment, outputDirectory);
            PrepareOutputDirectory(directory);
            Directory.CreateDirectory(directory);

            var projectFilePath = Path.Combine(directory, $"{model.ProjectName}.csproj");
            var deploymentFilePath = Path.Combine(directory, "ExportedDeployment.cs");

            File.WriteAllText(projectFilePath, NormalizeGeneratedText(CreateProjectFile(directory, model)), Utf8WithoutBom);
            File.WriteAllText(deploymentFilePath, NormalizeGeneratedText(CreateDeploymentFile(model)), Utf8WithoutBom);

            return new CSharpPulumiProjectExport(directory, projectFilePath, deploymentFilePath, model);
        }

        private static string ResolveOutputDirectory(string deployment, string? outputDirectory)
        {
            if (!string.IsNullOrWhiteSpace(outputDirectory))
            {
                return Path.GetFullPath(outputDirectory.Trim());
            }

            var safeDeploymentName = Regex.Replace(deployment, "[^a-zA-Z0-9_-]", "-");
            var baseDirectory = Path.Combine(AppContext.BaseDirectory, "TestResults", "GeneratedProjects");
            var timestamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
            var candidate = Path.Combine(baseDirectory, $"{safeDeploymentName}-{timestamp}");
            var suffix = 2;

            while (Directory.Exists(candidate))
            {
                candidate = Path.Combine(baseDirectory, $"{safeDeploymentName}-{timestamp}-{suffix++}");
            }

            return candidate;
        }

        private static string NormalizeGeneratedText(string text)
        {
            return text.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace("\r", "\n", StringComparison.Ordinal)
                .Replace("\n", "\r\n", StringComparison.Ordinal);
        }

        private static void PrepareOutputDirectory(string outputDirectory)
        {
            if (!Directory.Exists(outputDirectory))
            {
                return;
            }

            Directory.Delete(outputDirectory, recursive: true);
        }

        private static string CreateProjectFile(string outputDirectory, CSharpPulumiProjectModel model)
        {
            var builder = new StringBuilder();
            builder.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">");
            builder.AppendLine();
            builder.AppendLine("  <PropertyGroup>");
            builder.AppendLine("    <TargetFramework>net10.0</TargetFramework>");
            builder.AppendLine("    <ImplicitUsings>enable</ImplicitUsings>");
            builder.AppendLine("    <Nullable>enable</Nullable>");
            builder.AppendLine("  </PropertyGroup>");
            builder.AppendLine();

            if (model.PackageReferences.Count > 0)
            {
                builder.AppendLine("  <ItemGroup>");
                foreach (var packageReference in model.PackageReferences)
                {
                    builder.AppendLine($"    <PackageReference Include=\"{packageReference.PackageId}\" Version=\"{packageReference.Version}\" />");
                }
                builder.AppendLine("  </ItemGroup>");
                builder.AppendLine();
            }

            if (model.ProjectReferences.Count > 0)
            {
                builder.AppendLine("  <ItemGroup>");
                foreach (var projectReference in model.ProjectReferences)
                {
                    builder.AppendLine($"    <ProjectReference Include=\"{projectReference.ProjectPath.Replace("\\", "\\\\")}\" />");
                }
                builder.AppendLine("  </ItemGroup>");
                builder.AppendLine();
            }

            builder.AppendLine("</Project>");
            return builder.ToString();
        }

        private static string CreateDeploymentFile(CSharpPulumiProjectModel model)
        {
            var builder = new StringBuilder();
            builder.AppendLine("using System.Collections.Generic;");
            builder.AppendLine("using System.Threading;");
            builder.AppendLine("using System.Threading.Tasks;");
            foreach (var namespaceName in model.AdditionalNamespaces)
            {
                builder.AppendLine($"using {namespaceName};");
            }
            builder.AppendLine();
            builder.AppendLine($"namespace {model.RootNamespace};");
            builder.AppendLine();
            builder.AppendLine("public static class ExportedDeployment");
            builder.AppendLine("{");
            builder.AppendLine($"    public const string DeploymentName = {ToCSharpString(model.Deployment)};");
            builder.AppendLine();
            builder.AppendLine("    public static Task ProcessAsync(CancellationToken cancellationToken = default)");
            builder.AppendLine("    {");
            foreach (var resource in model.Resources)
            {
                builder.AppendLine($"        {resource.CreationStatement}");
            }
            builder.AppendLine();
            builder.AppendLine("        return Task.CompletedTask;");
            builder.AppendLine("    }");
            builder.AppendLine("}");

            return builder.ToString();
        }

        private static string ToCSharpString(string value)
        {
            return $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
        }
    }

    public sealed record CSharpPulumiProjectExporterOptions(
        string? ProjectName = null,
        string RootNamespace = "Generated.Deployment",
        string? OutputDirectory = null,
        IReadOnlyCollection<string>? AdditionalNamespaces = null,
        IReadOnlyCollection<CSharpPackageReference>? AdditionalPackageReferences = null)
    {
        public string? OutputDirectory { get; init; } = OutputDirectory;
        public IReadOnlyCollection<string> AdditionalNamespaces { get; init; } = AdditionalNamespaces ?? [];
        public IReadOnlyCollection<CSharpPackageReference> AdditionalPackageReferences { get; init; } = AdditionalPackageReferences ?? [];
    }

    public sealed record CSharpPulumiProjectExport(
        string DirectoryPath,
        string ProjectFilePath,
        string DeploymentFilePath,
        CSharpPulumiProjectModel Model);

    public sealed record CSharpPulumiProjectModel(
        string Deployment,
        string ProjectName,
        string RootNamespace,
        IReadOnlyCollection<string> AdditionalNamespaces,
        IReadOnlyCollection<CSharpPackageReference> PackageReferences,
        IReadOnlyCollection<CSharpProjectReference> ProjectReferences,
        int CreatedCount,
        int ReferencedCount,
        IReadOnlyCollection<CSharpPulumiResourceModel> Resources);

    public sealed record CSharpPulumiResourceModel(
        string Key,
        string Kind,
        string Node,
        int ScopeId,
        string ResourceTypeName,
        string VariableName,
        string CreationStatement,
        IReadOnlyCollection<string> DependsOn);

    public sealed record CSharpPackageReference(string PackageId, string Version);

    public sealed record CSharpProjectReference(string ProjectPath);

    internal static class KnownPackageRegistry
    {
        private static readonly ExportReferenceRule[] Rules =
        [
            new(
                ["Pulumi.AzureNative."],
                [new CSharpPackageReference("Pulumi.AzureNative", "3.18.0")],
                []),
            new(
                ["Pulumi.DockerBuild."],
                [new CSharpPackageReference("Pulumi.DockerBuild", "0.0.16")],
                []),
            new(
                ["LiveArch.Resources.Azure."],
                [],
                [new CSharpProjectReference("..\\LiveArch.Resources.Azure\\LiveArch.Resources.Azure.csproj")])
        ];

        public static IReadOnlyCollection<CSharpPackageReference> Resolve(Type type)
        {
            var packageReferences = new Dictionary<string, CSharpPackageReference>(StringComparer.OrdinalIgnoreCase);
            foreach (var currentType in EnumerateTypeClosure(type))
            {
                foreach (var rule in Rules.Where(rule => rule.IsMatch(currentType)))
                {
                    foreach (var packageReference in rule.PackageReferences)
                    {
                        packageReferences[packageReference.PackageId] = packageReference;
                    }
                }
            }

            return [.. packageReferences.Values];
        }

        public static IReadOnlyCollection<CSharpProjectReference> ResolveProjectReferences(Type type)
        {
            var projectReferences = new Dictionary<string, CSharpProjectReference>(StringComparer.OrdinalIgnoreCase);
            foreach (var currentType in EnumerateTypeClosure(type))
            {
                foreach (var rule in Rules.Where(rule => rule.IsMatch(currentType)))
                {
                    foreach (var projectReference in rule.ProjectReferences)
                    {
                        projectReferences[projectReference.ProjectPath] = projectReference;
                    }
                }
            }

            return [.. projectReferences.Values];
        }

        private sealed record ExportReferenceRule(
            IReadOnlyCollection<string> Prefixes,
            IReadOnlyCollection<CSharpPackageReference> PackageReferences,
            IReadOnlyCollection<CSharpProjectReference> ProjectReferences)
        {
            public bool IsMatch(Type type)
            {
                var fullName = type.FullName ?? type.Name;
                var @namespace = type.Namespace ?? string.Empty;
                var assemblyName = type.Assembly.GetName().Name ?? string.Empty;

                return Prefixes.Any(prefix =>
                    fullName.StartsWith(prefix, StringComparison.Ordinal) ||
                    @namespace.StartsWith(prefix, StringComparison.Ordinal) ||
                    assemblyName.StartsWith(prefix.TrimEnd('.'), StringComparison.Ordinal));
            }
        }

        private static IEnumerable<Type> EnumerateTypeClosure(Type type)
        {
            yield return type;

            if (!type.IsGenericType)
            {
                yield break;
            }

            foreach (var genericArgument in type.GetGenericArguments())
            {
                foreach (var currentType in EnumerateTypeClosure(genericArgument))
                {
                    yield return currentType;
                }
            }
        }
    }
}
