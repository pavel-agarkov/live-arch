using LiveArch.Deployment.Expressions;
using LiveArch.Deployment.Observability;
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
            object args,
            CustomResourceOptions? options,
            IReadOnlyCollection<Resource> dependsOn,
            CreatedResourceExpressionModel expressionModel)
        {
            observedResources.Add(new CreatedResourceObservation(node, scope.Id, resource, resourceType, resourceName, args, options, dependsOn, expressionModel));
        }

        public void OnResourceReferenced(
            ModelItem node,
            StructurizrDeploymentProcessor.ResourceScope scope,
            object resource,
            string resourceName,
            MethodInfo invokeMethod,
            object args,
            InvokeOptions? options,
            IReadOnlyCollection<Resource> dependsOn,
            ReferencedResourceExpressionModel expressionModel)
        {
            observedResources.Add(new ReferencedResourceObservation(node, scope.Id, resource, resourceName, invokeMethod, args, options, dependsOn, expressionModel));
        }

        public CSharpPulumiProjectExport Export(string deployment, CSharpPulumiProjectExporterOptions? options = null)
        {
            options ??= new CSharpPulumiProjectExporterOptions();

            var projectName = string.IsNullOrWhiteSpace(options.ProjectName)
                ? CreateDefaultProjectName(deployment)
                : options.ProjectName.Trim();

            var keyByResource = observedResources
                .GroupBy(resource => resource.Resource, ReferenceEqualityComparer.Instance)
                .ToDictionary(group => group.Key, group => CreateResourceKey(group.First()), ReferenceEqualityComparer.Instance);

            var variableNameByKey = CreateVariableNames(observedResources);

            var model = new CSharpPulumiProjectModel(
                deployment,
                projectName,
                options.RootNamespace,
                NormalizeNamespaces(options.AdditionalNamespaces),
                ResolvePackageReferences(observedResources, options.AdditionalPackageReferences),
                ResolveProjectReferences(observedResources),
                observedResources.OfType<CreatedResourceObservation>().Count(),
                observedResources.OfType<ReferencedResourceObservation>().Count(),
                [.. observedResources.Select((resource, index) => BuildResourceModel(resource, index, keyByResource, variableNameByKey))]);

            return CSharpPulumiProjectWriter.Export(model);
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
            var argsCode = RenderObjectInitializer(resource.Args, resource.Args.GetType(), 2, resource.ExpressionModel, string.Empty, keyByResource, variableNameByKey);
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
            var argsCode = RenderObjectInitializer(resource.Args, resource.Args.GetType(), 2, resource.ExpressionModel, string.Empty, keyByResource, variableNameByKey);
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

        private static string RenderObjectInitializer(
            object instance,
            Type declaredType,
            int indentLevel,
            ResourceExpressionModel? expressionModel,
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

                var value = property.GetValue(instance);
                if (value == null)
                {
                    continue;
                }

                var inputName = GetInputName(property) ?? property.Name;
                var propertyPath = CombinePath(currentPath, inputName);
                var propertyValueCode = expressionModel != null && expressionModel.Assignments.TryGetValue(propertyPath, out var expression)
                    ? RenderExpression(expression, property.PropertyType, indentLevel + 1, keyByResource, variableNameByKey)
                    : RenderValue(value, property.PropertyType, indentLevel + 1, expressionModel, propertyPath, keyByResource, variableNameByKey);

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
            ResourceExpressionModel? expressionModel,
            string currentPath,
            IReadOnlyDictionary<object, string> keyByResource,
            IReadOnlyDictionary<string, string> variableNameByKey)
        {
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
                    .Select(item => RenderValue(item!, itemType, indentLevel + 1, expressionModel, currentPath, keyByResource, variableNameByKey))
                    .ToArray();

                return renderedItems.Length == 0
                    ? "[]"
                    : $"[{string.Join(", ", renderedItems)}]";
            }

            if (CanRenderAsObjectInitializer(declaredType, value.GetType()))
            {
                return RenderObjectInitializer(value, value.GetType(), indentLevel + 1, expressionModel, currentPath, keyByResource, variableNameByKey);
            }

            return "default!";
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
                : RenderValue(expression.Value, expression.Value.GetType(), indentLevel, null, string.Empty, keyByResource, variableNameByKey);

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

        private static bool CanRenderAsObjectInitializer(Type declaredType, Type runtimeType)
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
                ? "Generated.Pulumi.Project"
                : $"Generated.{safeDeploymentName}.Pulumi";
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
                var suffix = 2;

                while (!usedNames.Add(variableName))
                {
                    variableName = $"{baseName}_{suffix++}";
                }

                variableNameByKey[key] = variableName;
            }

            return variableNameByKey;
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

        private static string ToCSharpString(string value)
        {
            return $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
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
            object Args,
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
            object Args,
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
        public static CSharpPulumiProjectExport Export(CSharpPulumiProjectModel model)
        {
            var directory = CreateOutputDirectory(model.Deployment);
            Directory.CreateDirectory(directory);

            var projectFilePath = Path.Combine(directory, $"{model.ProjectName}.csproj");
            var deploymentFilePath = Path.Combine(directory, "ExportedDeployment.cs");

            File.WriteAllText(projectFilePath, CreateProjectFile(directory, model), Encoding.UTF8);
            File.WriteAllText(deploymentFilePath, CreateDeploymentFile(model), Encoding.UTF8);

            return new CSharpPulumiProjectExport(directory, projectFilePath, deploymentFilePath, model);
        }

        private static string CreateOutputDirectory(string deployment)
        {
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
                    var fullProjectPath = Path.GetFullPath(projectReference.ProjectPath, AppContext.BaseDirectory);
                    var relativePath = Path.GetRelativePath(outputDirectory, fullProjectPath);
                    builder.AppendLine($"    <ProjectReference Include=\"{relativePath.Replace("\\", "\\\\")}\" />");
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
            builder.AppendLine();
            builder.AppendLine("    public static IReadOnlyCollection<ExportedResourceDescriptor> DescribeResources() =>");
            builder.AppendLine("    [");
            foreach (var resource in model.Resources)
            {
                builder.AppendLine("        new ExportedResourceDescriptor(");
                builder.AppendLine($"            {ToCSharpString(resource.Key)},");
                builder.AppendLine($"            {ToCSharpString(resource.Kind)},");
                builder.AppendLine($"            {ToCSharpString(resource.Node)},");
                builder.AppendLine($"            {resource.ScopeId},");
                builder.AppendLine($"            {ToCSharpString(resource.VariableName)},");
                builder.AppendLine($"            {ToCSharpString(resource.ResourceTypeName)},");
                builder.Append("            [");
                builder.Append(string.Join(", ", resource.DependsOn.Select(ToCSharpString)));
                builder.AppendLine("]),");
            }
            builder.AppendLine("    ];");
            builder.AppendLine("}");
            builder.AppendLine();
            builder.AppendLine("public sealed record ExportedResourceDescriptor(");
            builder.AppendLine("    string Key,");
            builder.AppendLine("    string Kind,");
            builder.AppendLine("    string Node,");
            builder.AppendLine("    int ScopeId,");
            builder.AppendLine("    string VariableName,");
            builder.AppendLine("    string ResourceTypeName,");
            builder.AppendLine("    IReadOnlyCollection<string> DependsOn);");

            return builder.ToString();
        }

        private static string ToCSharpString(string value)
        {
            return $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
        }
    }

    public sealed record CSharpPulumiProjectExporterOptions(
        string? ProjectName = null,
        string RootNamespace = "Generated.Pulumi",
        IReadOnlyCollection<string>? AdditionalNamespaces = null,
        IReadOnlyCollection<CSharpPackageReference>? AdditionalPackageReferences = null)
    {
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
        public static IReadOnlyCollection<CSharpPackageReference> Resolve(Type type)
        {
            var packageReferences = new Dictionary<string, CSharpPackageReference>(StringComparer.OrdinalIgnoreCase);
            foreach (var currentType in EnumerateTypeClosure(type))
            {
                var fullName = currentType.FullName ?? currentType.Name;
                if (fullName.StartsWith("Pulumi.AzureNative.", StringComparison.Ordinal))
                {
                    packageReferences["Pulumi.AzureNative"] = new CSharpPackageReference("Pulumi.AzureNative", "3.18.0");
                }

                if (fullName.StartsWith("Pulumi.DockerBuild.", StringComparison.Ordinal))
                {
                    packageReferences["Pulumi.DockerBuild"] = new CSharpPackageReference("Pulumi.DockerBuild", "0.0.16");
                }
            }

            return [.. packageReferences.Values];
        }

        public static IReadOnlyCollection<CSharpProjectReference> ResolveProjectReferences(Type type)
        {
            var projectReferences = new Dictionary<string, CSharpProjectReference>(StringComparer.OrdinalIgnoreCase);
            foreach (var currentType in EnumerateTypeClosure(type))
            {
                if (currentType.Assembly.GetName().Name == "LiveArch.Pulumi.Azure")
                {
                    projectReferences["LiveArch.Pulumi.Azure"] = new CSharpProjectReference("..\\..\\..\\..\\LiveArch.Pulumi.Azure\\LiveArch.Pulumi.Azure.csproj");
                }
            }

            return [.. projectReferences.Values];
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
