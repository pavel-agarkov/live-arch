using LiveArch.Deployment.Expressions;
using LiveArch.Deployment.Observability;
using LiveArch.Deployment.Converters;
using Pulumi;
using Structurizr;
using System.Collections;
using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace LiveArch.Deployment.Export.CSharp
{
    public sealed class CSharpPulumiProjectExporter : IStructurizrDeploymentObserver
    {
        private static readonly PropertyInfo InputAttrNameProp = typeof(InputAttribute).GetProperty("Name", BindingFlags.Instance | BindingFlags.NonPublic)!;
        private static readonly Regex VariableRegex = new(@"\$\{([a-zA-Z0-9_\.\:\-]+)\}", RegexOptions.Compiled, TimeSpan.FromMilliseconds(1000));
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
            var rootNamespace = string.IsNullOrWhiteSpace(options.RootNamespace)
                ? projectName
                : options.RootNamespace.Trim();
            var variablesModel = CreateVariablesModel(projectName, options.VariableValues);

            var exportableResources = observedResources
                .Where(ShouldExportResource)
                .ToList();
            var diagnostics = new List<CSharpExportDiagnostic>();
            var dependencies = ResolveDependencies(exportableResources, options.AdditionalPackageReferences, diagnostics);

            var keyByResource = exportableResources
                .GroupBy(resource => resource.Resource, ReferenceEqualityComparer.Instance)
                .ToDictionary(group => group.Key, group => CreateResourceKey(group.First()), ReferenceEqualityComparer.Instance);
            var observedByKey = exportableResources.ToDictionary(CreateResourceKey, StringComparer.Ordinal);

            var variableNameByKey = CreateVariableNames(exportableResources);

            var model = new CSharpPulumiProjectModel(
                deployment,
                projectName,
                rootNamespace,
                variablesModel,
                NormalizeNamespaces(options.AdditionalNamespaces),
                dependencies,
                diagnostics,
                exportableResources.OfType<CreatedResourceObservation>().Count(),
                exportableResources.OfType<ReferencedResourceObservation>().Count(),
                [.. exportableResources.Select((resource, index) => BuildResourceModel(resource, index, keyByResource, variableNameByKey, variablesModel, observedByKey, diagnostics))]);

            var testProjectName = string.IsNullOrWhiteSpace(options.TestProjectName)
                ? projectName + ".Tests"
                : options.TestProjectName.Trim();
            var testRootNamespace = string.IsNullOrWhiteSpace(options.TestRootNamespace)
                ? testProjectName
                : options.TestRootNamespace.Trim();
            var testProjectModel = options.GenerateTestProject
                ? new CSharpPulumiTestProjectModel(testProjectName, testRootNamespace, dependencies)
                : null;

            return CSharpPulumiProjectWriter.Export(model, options.OutputDirectory, testProjectModel, options.TestOutputDirectory, options.CleanOutputDirectories);
        }

        private static CSharpPulumiResourceModel BuildResourceModel(
            IObservedResource resource,
            int index,
            IReadOnlyDictionary<object, string> keyByResource,
            IReadOnlyDictionary<string, string> variableNameByKey,
            CSharpPulumiVariablesModel? variablesModel,
            IReadOnlyDictionary<string, IObservedResource> observedByKey,
            List<CSharpExportDiagnostic> diagnostics)
        {
            var key = CreateResourceKey(resource);
            var dependsOnKeys = resource.DependsOn
                .Select(dependency => keyByResource.TryGetValue(dependency, out var dependencyKey) ? dependencyKey : dependency.GetResourceName())
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            var variableName = variableNameByKey[key];
            var creationStatement = resource switch
            {
                CreatedResourceObservation created => RenderCreatedResource(created, variableName, dependsOnKeys, keyByResource, variableNameByKey, variablesModel, observedByKey, diagnostics),
                ReferencedResourceObservation referenced => RenderReferencedResource(referenced, variableName, dependsOnKeys, keyByResource, variableNameByKey, variablesModel, observedByKey, diagnostics),
                _ => RenderUnsupportedStatement($"Unsupported observed resource type '{resource.GetType().FullName}'.", diagnostics, key)
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
            IReadOnlyDictionary<string, string> variableNameByKey,
            CSharpPulumiVariablesModel? variablesModel,
            IReadOnlyDictionary<string, IObservedResource> observedByKey,
            List<CSharpExportDiagnostic> diagnostics)
        {
            var optionsCode = RenderCustomResourceOptions(dependsOnKeys, variableNameByKey);
            var argsCode = RenderTrackedObjectInitializer(GetCreatedArgsType(resource.ResourceType), 2, resource.ExpressionModel, string.Empty, keyByResource, variableNameByKey, variablesModel, observedByKey, diagnostics);
            var resourceTypeName = GetGlobalTypeName(resource.ResourceType);

            return $"var {variableName} = new {resourceTypeName}({ToCSharpString(resource.ResourceName)}, {argsCode}, {optionsCode});";
        }

        private static string RenderReferencedResource(
            ReferencedResourceObservation resource,
            string variableName,
            IReadOnlyCollection<string> dependsOnKeys,
            IReadOnlyDictionary<object, string> keyByResource,
            IReadOnlyDictionary<string, string> variableNameByKey,
            CSharpPulumiVariablesModel? variablesModel,
            IReadOnlyDictionary<string, IObservedResource> observedByKey,
            List<CSharpExportDiagnostic> diagnostics)
        {
            var argsCode = RenderTrackedObjectInitializer(resource.InvokeMethod.GetParameters()[0].ParameterType, 2, resource.ExpressionModel, string.Empty, keyByResource, variableNameByKey, variablesModel, observedByKey, diagnostics);
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
            IReadOnlyDictionary<string, string> variableNameByKey,
            CSharpPulumiVariablesModel? variablesModel,
            IReadOnlyDictionary<string, IObservedResource> observedByKey,
            List<CSharpExportDiagnostic> diagnostics)
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
                    propertyValueCode = RenderExpression(expression, property.PropertyType, indentLevel + 1, keyByResource, variableNameByKey, variablesModel, observedByKey, diagnostics, expressionModel, propertyPath);
                }
                else if (HasNestedAssignments(expressionModel, propertyPath))
                {
                    var nestedType = GetUnderlyingArgsType(property.PropertyType);
                    propertyValueCode = RenderTrackedObjectInitializer(nestedType, indentLevel + 1, expressionModel, propertyPath, keyByResource, variableNameByKey, variablesModel, observedByKey, diagnostics);
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

        internal static string RenderValue(
            object value,
            Type declaredType,
            int indentLevel,
            IReadOnlyDictionary<object, string> keyByResource,
            IReadOnlyDictionary<string, string> variableNameByKey,
            CSharpPulumiVariablesModel? variablesModel)
        {
            return RenderValue(value, declaredType, indentLevel, keyByResource, variableNameByKey, variablesModel, null, null, null);
        }

        private static string RenderValue(
            object value,
            Type declaredType,
            int indentLevel,
            IReadOnlyDictionary<object, string> keyByResource,
            IReadOnlyDictionary<string, string> variableNameByKey,
            CSharpPulumiVariablesModel? variablesModel,
            List<CSharpExportDiagnostic>? diagnostics,
            ResourceExpressionModel? expressionModel,
            string? propertyPath)
        {
            if (TryRenderVariableSubstitution(value, declaredType, variablesModel, out var variableSubstitution))
            {
                return variableSubstitution;
            }

            if (TryRenderTypedValue(value, declaredType, keyByResource, variableNameByKey, indentLevel, out var typedValue))
            {
                return typedValue;
            }

            if (IsPulumiWrapperType(value.GetType()) || IsPulumiWrapperType(declaredType))
            {
                return RenderUnsupportedValue(declaredType, $"Cannot render Pulumi wrapper value of runtime type '{value.GetType().FullName}' for declared type '{declaredType.FullName}'.", diagnostics, expressionModel, propertyPath);
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
                    .Select(item => RenderValue(item!, itemType, indentLevel + 1, keyByResource, variableNameByKey, variablesModel, diagnostics, expressionModel, propertyPath))
                    .ToArray();

                return renderedItems.Length == 0
                    ? "[]"
                    : $"[{string.Join(", ", renderedItems)}]";
            }

            if (CanRenderAsLiteralObjectInitializer(declaredType, value.GetType()))
            {
                return RenderLiteralObjectInitializer(value, value.GetType(), indentLevel + 1, keyByResource, variableNameByKey, variablesModel, diagnostics, expressionModel, propertyPath);
            }

            return RenderUnsupportedValue(declaredType, $"Cannot render value of runtime type '{value.GetType().FullName}' for declared type '{declaredType.FullName}'.", diagnostics, expressionModel, propertyPath);
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
            IReadOnlyDictionary<string, string> variableNameByKey,
            CSharpPulumiVariablesModel? variablesModel,
            List<CSharpExportDiagnostic>? diagnostics,
            ResourceExpressionModel? expressionModel,
            string? propertyPath)
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

                entries.Add($"{childIndent}{property.Name} = {RenderValue(value, property.PropertyType, indentLevel + 1, keyByResource, variableNameByKey, variablesModel, diagnostics, expressionModel, propertyPath)}");
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
            IReadOnlyDictionary<string, string> variableNameByKey,
            CSharpPulumiVariablesModel? variablesModel,
            IReadOnlyDictionary<string, IObservedResource> observedByKey,
            List<CSharpExportDiagnostic> diagnostics,
            ResourceExpressionModel expressionModel,
            string propertyPath)
        {
            return expression switch
            {
                DirectValueExpressionModel direct => RenderDirectExpression(direct, declaredType, indentLevel, keyByResource, variableNameByKey, variablesModel, diagnostics, expressionModel, propertyPath),
                DependencyValueExpressionModel dependency => RenderDependencyExpression(dependency, declaredType, keyByResource, variableNameByKey, observedByKey, diagnostics, expressionModel, propertyPath),
                _ => RenderUnsupportedExpression(declaredType, $"Unsupported expression model '{expression.GetType().FullName}' for '{expressionModel.Node}' property '{propertyPath}'.", diagnostics, expressionModel, propertyPath)
            };
        }

        private static string RenderDirectExpression(
            DirectValueExpressionModel expression,
            Type declaredType,
            int indentLevel,
            IReadOnlyDictionary<object, string> keyByResource,
            IReadOnlyDictionary<string, string> variableNameByKey,
            CSharpPulumiVariablesModel? variablesModel,
            List<CSharpExportDiagnostic> diagnostics,
            ResourceExpressionModel expressionModel,
            string propertyPath)
        {
            var rendered = expression.Value == null
                ? "null"
                : RenderValue(expression.Value, declaredType, indentLevel, keyByResource, variableNameByKey, variablesModel, diagnostics, expressionModel, propertyPath);

            if (string.IsNullOrWhiteSpace(expression.ConverterName))
            {
                return rendered;
            }

            return $"{rendered} /* converter: {expression.ConverterName} */";
        }

        private static string RenderDependencyExpression(
            DependencyValueExpressionModel expression,
            Type declaredType,
            IReadOnlyDictionary<object, string> keyByResource,
            IReadOnlyDictionary<string, string> variableNameByKey,
            IReadOnlyDictionary<string, IObservedResource> observedByKey,
            List<CSharpExportDiagnostic> diagnostics,
            ResourceExpressionModel expressionModel,
            string propertyPath)
        {
            var sourceName = keyByResource.TryGetValue(expression.SourceResource, out var resourceKey) && variableNameByKey.TryGetValue(resourceKey, out var variableName)
                ? variableName
                : expression.SourceResource is Resource resource
                    ? resource.GetResourceName()
                    : "resource";
            var observedSource = resourceKey != null && observedByKey.TryGetValue(resourceKey, out var foundObservedSource)
                ? foundObservedSource
                : null;
            var sourceType = observedSource?.CodeGenType ?? expression.SourceResource.GetType();

            var renderedSourceAccess = TryRenderDependencySourceAccess(sourceName, sourceType, expression.SourcePath, observedSource, out var renderedAccess)
                ? renderedAccess
                : null;

            var suffix = new StringBuilder();
            if (expression.Transformers.Count > 0)
            {
                suffix.Append($" | transformers: {string.Join(" -> ", expression.Transformers)}");
            }

            if (!string.IsNullOrWhiteSpace(expression.ConverterName))
            {
                suffix.Append($" | converter: {expression.ConverterName}");
            }

            if (renderedSourceAccess != null && suffix.Length == 0)
            {
                return renderedSourceAccess;
            }

            if (renderedSourceAccess != null)
            {
                return $"{renderedSourceAccess} /*{suffix} */";
            }

            var message = $"Cannot render dependency source access '{sourceName}.{expression.SourcePath}' for '{expressionModel.Node}' property '{propertyPath}'.{suffix}";
            return RenderUnsupportedExpression(declaredType, message, diagnostics, expressionModel, propertyPath);
        }

        private static bool TryRenderDependencySourceAccess(string sourceExpression, Type sourceType, string sourcePath, IObservedResource? observedSource, out string rendered)
        {
            var segments = sourcePath.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length == 0)
            {
                rendered = sourceExpression;
                return true;
            }

            if (TryRenderHeuristicDependencySourceAccess(sourceExpression, segments, observedSource, out rendered))
            {
                return true;
            }

            if (TryRenderHeuristicDependencySourceAccess(sourceExpression, sourceType, segments, out rendered))
            {
                return true;
            }

            return TryRenderAccessExpression(sourceExpression, sourceType, segments, 0, out rendered);
        }

        private static bool TryRenderHeuristicDependencySourceAccess(string sourceExpression, IReadOnlyList<string> segments, IObservedResource? observedSource, out string rendered)
        {
            if (observedSource == null)
            {
                rendered = string.Empty;
                return false;
            }

            if (observedSource.Kind == "Referenced")
            {
                rendered = $"{sourceExpression}.Apply(value => value.{string.Join('.', segments.Select(ToPascalCaseSegment))})";
                return true;
            }

            if (segments.Count == 1)
            {
                rendered = $"{sourceExpression}.{ToPascalCaseSegment(segments[0])}";
                return true;
            }

            rendered = $"{sourceExpression}.{ToPascalCaseSegment(segments[0])}.Apply(value => value!.{string.Join('.', segments.Skip(1).Select(ToPascalCaseSegment))})";
            return true;
        }

        private static bool TryRenderHeuristicDependencySourceAccess(string sourceExpression, Type sourceType, IReadOnlyList<string> segments, out string rendered)
        {
            var effectiveType = Nullable.GetUnderlyingType(sourceType) ?? sourceType;
            if (ConversionTypeHelpers.IsOutput(effectiveType))
            {
                rendered = $"{sourceExpression}.Apply(value => value.{string.Join('.', segments.Select(ToPascalCaseSegment))})";
                return true;
            }

            if (segments.Count == 1)
            {
                rendered = $"{sourceExpression}.{ToPascalCaseSegment(segments[0])}";
                return true;
            }

            rendered = $"{sourceExpression}.{ToPascalCaseSegment(segments[0])}.Apply(value => value!.{string.Join('.', segments.Skip(1).Select(ToPascalCaseSegment))})";
            return true;
        }

        private static string ToPascalCaseSegment(string segment)
        {
            return string.IsNullOrWhiteSpace(segment)
                ? segment
                : char.ToUpperInvariant(segment[0]) + segment[1..];
        }

        private static bool TryRenderAccessExpression(string sourceExpression, Type sourceType, IReadOnlyList<string> segments, int index, out string rendered)
        {
            if (index >= segments.Count)
            {
                rendered = sourceExpression;
                return true;
            }

            var effectiveType = Nullable.GetUnderlyingType(sourceType) ?? sourceType;
            if (ConversionTypeHelpers.IsOutput(effectiveType))
            {
                var innerType = effectiveType.GetGenericArguments()[0];
                if (!TryRenderAccessExpression("value!", innerType, segments, index, out var lambdaBody))
                {
                    rendered = string.Empty;
                    return false;
                }

                rendered = $"{sourceExpression}.Apply(value => {lambdaBody})";
                return true;
            }

            var property = effectiveType.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(candidate => string.Equals(candidate.Name, segments[index], StringComparison.OrdinalIgnoreCase));
            if (property == null)
            {
                rendered = string.Empty;
                return false;
            }

            return TryRenderAccessExpression($"{sourceExpression}.{property.Name}", property.PropertyType, segments, index + 1, out rendered);
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

        private static string RenderUnsupportedStatement(string message, List<CSharpExportDiagnostic> diagnostics, string resourceKey)
        {
            diagnostics.Add(new CSharpExportDiagnostic(CSharpExportDiagnosticSeverity.Warning, message, resourceKey, null));
            return $"throw new global::System.NotSupportedException({ToCSharpString(message)});";
        }

        private static string RenderUnsupportedExpression(
            Type declaredType,
            string message,
            List<CSharpExportDiagnostic> diagnostics,
            ResourceExpressionModel expressionModel,
            string propertyPath)
        {
            diagnostics.Add(new CSharpExportDiagnostic(
                CSharpExportDiagnosticSeverity.Warning,
                message,
                $"{expressionModel.ResourceName}:{expressionModel.ScopeId}:{expressionModel.Node}",
                propertyPath));

            return RenderUnsupportedExpression(declaredType, message);
        }

        private static string RenderUnsupportedValue(
            Type declaredType,
            string message,
            List<CSharpExportDiagnostic>? diagnostics,
            ResourceExpressionModel? expressionModel,
            string? propertyPath)
        {
            if (diagnostics != null && expressionModel != null && propertyPath != null)
            {
                diagnostics.Add(new CSharpExportDiagnostic(
                    CSharpExportDiagnosticSeverity.Warning,
                    message,
                    $"{expressionModel.ResourceName}:{expressionModel.ScopeId}:{expressionModel.Node}",
                    propertyPath));
            }

            return RenderUnsupportedExpression(declaredType, message);
        }

        private static string RenderUnsupportedExpression(Type declaredType, string message)
        {
            return $"ThrowUnsupported<{GetGlobalTypeName(declaredType)}>({ToCSharpString(message)})";
        }

        internal static string GetGlobalTypeName(Type type)
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

        private static CSharpPulumiProjectDependencies ResolveDependencies(
            IReadOnlyCollection<IObservedResource> resources,
            IReadOnlyCollection<CSharpPackageReference> additionalPackageReferences,
            List<CSharpExportDiagnostic> diagnostics)
        {
            return new CSharpPulumiProjectDependencies(
                ResolvePackageReferences(resources, additionalPackageReferences, diagnostics),
                ResolveProjectReferences(resources));
        }

        private static IReadOnlyCollection<CSharpPackageReference> ResolvePackageReferences(
            IReadOnlyCollection<IObservedResource> resources,
            IReadOnlyCollection<CSharpPackageReference> additionalPackageReferences,
            List<CSharpExportDiagnostic> diagnostics)
        {
            var packageReferences = new Dictionary<string, CSharpPackageReference>(StringComparer.OrdinalIgnoreCase)
            {
                [CSharpPulumiDefaultPackageCatalog.MicrosoftNetTestSdk] = CSharpPulumiDefaultPackageCatalog.Resolve(CSharpPulumiDefaultPackageCatalog.MicrosoftNetTestSdk),
                [CSharpPulumiDefaultPackageCatalog.Pulumi] = CSharpPulumiDefaultPackageCatalog.Resolve(CSharpPulumiDefaultPackageCatalog.Pulumi),
                [CSharpPulumiDefaultPackageCatalog.XunitRunnerVisualStudio] = CSharpPulumiDefaultPackageCatalog.Resolve(CSharpPulumiDefaultPackageCatalog.XunitRunnerVisualStudio),
                [CSharpPulumiDefaultPackageCatalog.XunitV3] = CSharpPulumiDefaultPackageCatalog.Resolve(CSharpPulumiDefaultPackageCatalog.XunitV3)
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
                if (CSharpRuntimePackageVersionResolver.TryResolve(packageReference.PackageId, packageReference.Version, out var resolvedPackageReference))
                {
                    packageReferences[packageReference.PackageId] = resolvedPackageReference;
                    continue;
                }

                diagnostics.Add(new CSharpExportDiagnostic(
                    CSharpExportDiagnosticSeverity.Error,
                    $"Package reference '{packageReference.PackageId}' does not specify a version and no loaded assembly version could be resolved. Configure an explicit package version to make the generated project restore successfully.",
                    null,
                    null));
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

        private static CSharpPulumiVariablesModel? CreateVariablesModel(string projectName, IReadOnlyDictionary<string, object>? variableValues)
        {
            if (variableValues == null)
            {
                return null;
            }

            var usedPropertyNames = new HashSet<string>(StringComparer.Ordinal);
            var variables = new List<CSharpPulumiVariableModel>();
            foreach (var (key, value) in variableValues.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                var propertyName = CreateIndexedVariableName(usedPropertyNames, CreateVariablePropertyName(key));
                var variableType = value?.GetType() ?? typeof(object);
                variables.Add(new CSharpPulumiVariableModel(key, propertyName, variableType, value));
            }

            var className = CreateVariableClassName(projectName);
            return new CSharpPulumiVariablesModel(className, variables);
        }

        private static string CreateVariablePropertyName(string key)
        {
            var parts = Regex.Split(key, "[^a-zA-Z0-9]+")
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .ToArray();

            if (parts.Length == 0)
            {
                return "Variable";
            }

            var builder = new StringBuilder();
            foreach (var part in parts)
            {
                builder.Append(char.ToUpperInvariant(part[0]));
                if (part.Length > 1)
                {
                    builder.Append(part.All(char.IsUpper)
                        ? part[1..].ToLowerInvariant()
                        : part[1..]);
                }
            }

            if (char.IsDigit(builder[0]))
            {
                builder.Insert(0, '_');
            }

            return builder.ToString();
        }

        private static string CreateVariableClassName(string projectName)
        {
            return CreateVariablePropertyName(projectName) + "Variables";
        }

        private static bool TryRenderVariableSubstitution(object value, Type declaredType, CSharpPulumiVariablesModel? variablesModel, out string rendered)
        {
            if (variablesModel == null || value is not string text)
            {
                rendered = string.Empty;
                return false;
            }

            var matches = VariableRegex.Matches(text);
            if (matches.Count == 0)
            {
                rendered = string.Empty;
                return false;
            }

            var variablesByKey = variablesModel.Variables.ToDictionary(variable => variable.Key, StringComparer.Ordinal);
            if (matches.Cast<Match>().Any(match => !variablesByKey.ContainsKey(match.Groups[1].Value)))
            {
                rendered = string.Empty;
                return false;
            }

            var targetType = GetUnderlyingArgsType(Nullable.GetUnderlyingType(declaredType) ?? declaredType);
            if (matches.Count == 1 && matches[0].Index == 0 && matches[0].Length == text.Length)
            {
                var variable = variablesByKey[matches[0].Groups[1].Value];
                if (targetType == typeof(string) && variable.VariableType != typeof(string))
                {
                    rendered = BuildInterpolatedString(text, variablesByKey);
                    return true;
                }

                rendered = $"vars.{variable.PropertyName}";
                return true;
            }

            if (targetType != typeof(string))
            {
                rendered = string.Empty;
                return false;
            }

            rendered = BuildInterpolatedString(text, variablesByKey);
            return true;
        }

        private static string BuildInterpolatedString(string template, IReadOnlyDictionary<string, CSharpPulumiVariableModel> variablesByKey)
        {
            var builder = new StringBuilder();
            builder.Append("$");
            builder.Append('"');

            var currentIndex = 0;
            foreach (Match match in VariableRegex.Matches(template))
            {
                builder.Append(EscapeInterpolatedStringSegment(template[currentIndex..match.Index]));
                builder.Append("{vars.");
                builder.Append(variablesByKey[match.Groups[1].Value].PropertyName);
                builder.Append('}');
                currentIndex = match.Index + match.Length;
            }

            builder.Append(EscapeInterpolatedStringSegment(template[currentIndex..]));
            builder.Append('"');
            return builder.ToString();
        }

        private static string EscapeInterpolatedStringSegment(string value)
        {
            return value
                .Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal)
                .Replace("{", "{{", StringComparison.Ordinal)
                .Replace("}", "}}", StringComparison.Ordinal);
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

        public static CSharpPulumiProjectExport Export(CSharpPulumiProjectModel model, string? outputDirectory = null, CSharpPulumiTestProjectModel? testProjectModel = null, string? testOutputDirectory = null, bool cleanOutputDirectories = true)
        {
            var directory = ResolveOutputDirectory(model.Deployment, outputDirectory);
            PrepareOutputDirectory(directory, cleanOutputDirectories);
            Directory.CreateDirectory(directory);

            var projectFilePath = Path.Combine(directory, $"{model.ProjectName}.csproj");
            var deploymentFilePath = Path.Combine(directory, "ExportedDeployment.cs");

            File.WriteAllText(projectFilePath, NormalizeGeneratedText(CreateProjectFile(directory, model)), Utf8WithoutBom);
            File.WriteAllText(deploymentFilePath, NormalizeGeneratedText(CreateDeploymentFile(model)), Utf8WithoutBom);

            string? testDirectoryPath = null;
            string? testProjectFilePath = null;
            string? testFilePath = null;

            if (testProjectModel != null)
            {
                testDirectoryPath = ResolveTestOutputDirectory(directory, testProjectModel.ProjectName, testOutputDirectory);
                PrepareOutputDirectory(testDirectoryPath, cleanOutputDirectories);
                Directory.CreateDirectory(testDirectoryPath);

                testProjectFilePath = Path.Combine(testDirectoryPath, $"{testProjectModel.ProjectName}.csproj");
                testFilePath = Path.Combine(testDirectoryPath, "ExportedDeploymentTests.cs");

                var resourceProjectReferencePath = NormalizePath(Path.GetRelativePath(testDirectoryPath, projectFilePath));
                var testingProjectReferencePath = NormalizePath(Path.GetRelativePath(testDirectoryPath, Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "LiveArch.Deployment.Export.Testing", "LiveArch.Deployment.Export.Testing.csproj"))));

                File.WriteAllText(testProjectFilePath, NormalizeGeneratedText(CreateTestProjectFile(testProjectModel, resourceProjectReferencePath, testingProjectReferencePath)), Utf8WithoutBom);
                File.WriteAllText(testFilePath, NormalizeGeneratedText(CreateTestDeploymentFile(model, testProjectModel)), Utf8WithoutBom);
            }

            return new CSharpPulumiProjectExport(directory, projectFilePath, deploymentFilePath, testDirectoryPath, testProjectFilePath, testFilePath, model, testProjectModel);
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

        private static string NormalizePath(string path)
        {
            return path.Replace("/", "\\", StringComparison.Ordinal);
        }

        private static void PrepareOutputDirectory(string outputDirectory, bool cleanOutputDirectory)
        {
            if (!cleanOutputDirectory || !Directory.Exists(outputDirectory))
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

            if (model.Dependencies.PackageReferences.Count > 0)
            {
                builder.AppendLine("  <ItemGroup>");
                foreach (var packageReference in model.Dependencies.PackageReferences)
                {
                    AppendPackageReference(builder, packageReference);
                }
                builder.AppendLine("  </ItemGroup>");
                builder.AppendLine();
            }

            if (model.Dependencies.ProjectReferences.Count > 0)
            {
                builder.AppendLine("  <ItemGroup>");
                foreach (var projectReference in model.Dependencies.ProjectReferences)
                {
                    builder.AppendLine($"    <ProjectReference Include=\"{projectReference.ProjectPath.Replace("\\", "\\\\")}\" />");
                }
                builder.AppendLine("  </ItemGroup>");
                builder.AppendLine();
            }

            builder.AppendLine("</Project>");
            return builder.ToString();
        }

        private static string ResolveTestOutputDirectory(string resourceOutputDirectory, string testProjectName, string? testOutputDirectory)
        {
            return !string.IsNullOrWhiteSpace(testOutputDirectory)
                ? Path.GetFullPath(testOutputDirectory.Trim())
                : Path.Combine(Directory.GetParent(resourceOutputDirectory)!.FullName, testProjectName);
        }

        private static string CreateTestProjectFile(CSharpPulumiTestProjectModel model, string resourceProjectReferencePath, string testingProjectReferencePath)
        {
            var builder = new StringBuilder();
            builder.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">");
            builder.AppendLine();
            builder.AppendLine("  <PropertyGroup>");
            builder.AppendLine("    <TargetFramework>net10.0</TargetFramework>");
            builder.AppendLine("    <ImplicitUsings>enable</ImplicitUsings>");
            builder.AppendLine("    <Nullable>enable</Nullable>");
            builder.AppendLine("    <IsPackable>false</IsPackable>");
            builder.AppendLine("  </PropertyGroup>");
            builder.AppendLine();

            if (model.Dependencies.PackageReferences.Count > 0)
            {
                builder.AppendLine("  <ItemGroup>");
                foreach (var packageReference in model.Dependencies.PackageReferences)
                {
                    AppendPackageReference(builder, packageReference);
                }
                builder.AppendLine("  </ItemGroup>");
                builder.AppendLine();
            }

            builder.AppendLine("  <ItemGroup>");
            builder.AppendLine($"    <ProjectReference Include=\"{resourceProjectReferencePath.Replace("\\", "\\\\")}\" />");
            builder.AppendLine($"    <ProjectReference Include=\"{testingProjectReferencePath.Replace("\\", "\\\\")}\" />");
            foreach (var projectReference in model.Dependencies.ProjectReferences)
            {
                builder.AppendLine($"    <ProjectReference Include=\"{projectReference.ProjectPath.Replace("\\", "\\\\")}\" />");
            }
            builder.AppendLine("  </ItemGroup>");
            builder.AppendLine();
            builder.AppendLine("</Project>");
            return builder.ToString();
        }

        private static void AppendPackageReference(StringBuilder builder, CSharpPackageReference packageReference)
        {
            if (string.IsNullOrWhiteSpace(packageReference.Version))
            {
                builder.AppendLine($"    <!-- PackageReference '{packageReference.PackageId}' needs an explicit Version because the exporter could not resolve one from loaded assemblies. -->");
                builder.AppendLine($"    <PackageReference Include=\"{packageReference.PackageId}\" />");
                return;
            }

            builder.AppendLine($"    <PackageReference Include=\"{packageReference.PackageId}\" Version=\"{packageReference.Version}\" />");
        }

        private static string CreateTestDeploymentFile(CSharpPulumiProjectModel resourceModel, CSharpPulumiTestProjectModel testModel)
        {
            var builder = new StringBuilder();
            builder.AppendLine("using LiveArch.Deployment.Export.Testing;");
            builder.AppendLine("using Xunit;");
            builder.AppendLine();
            builder.AppendLine($"namespace {testModel.RootNamespace};");
            builder.AppendLine();
            builder.AppendLine("public class ExportedDeploymentTests");
            builder.AppendLine("{");
            builder.AppendLine("    [Fact]");
            builder.AppendLine("    public async Task ProcessAsync_Should_Create_Resources()");
            builder.AppendLine("    {");
            builder.AppendLine($"        var mocks = await ExportedDeploymentTestHost.ExecuteAsync(() => global::{resourceModel.RootNamespace}.ExportedDeployment.ProcessAsync(CreateVariables()));");
            builder.AppendLine();
            builder.AppendLine("        Assert.NotEmpty(mocks.Resources);");
            builder.AppendLine("    }");

            if (resourceModel.VariablesModel != null)
            {
                builder.AppendLine();
                builder.AppendLine($"    private static global::{resourceModel.RootNamespace}.{resourceModel.VariablesModel.ClassName} CreateVariables() => new()");
                builder.AppendLine("    {");
                foreach (var variable in resourceModel.VariablesModel.Variables)
                {
                    var renderedValue = CSharpPulumiProjectExporter.RenderValue(variable.Value ?? string.Empty, variable.VariableType, 2, new Dictionary<object, string>(ReferenceEqualityComparer.Instance), new Dictionary<string, string>(StringComparer.Ordinal), null);
                    builder.AppendLine($"        {variable.PropertyName} = {renderedValue},");
                }
                builder.AppendLine("    };");
            }
            else
            {
                builder.AppendLine();
                builder.AppendLine($"    private static global::{resourceModel.RootNamespace}.DeploymentVariables CreateVariables() => new();");
            }

            builder.AppendLine("}");
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
            builder.Append(RenderVariablesClass(model));
            builder.AppendLine();
            builder.AppendLine("public static class ExportedDeployment");
            builder.AppendLine("{");
            builder.AppendLine($"    public const string DeploymentName = {ToCSharpString(model.Deployment)};");
            builder.AppendLine();
            builder.AppendLine($"    public static Task ProcessAsync({model.VariablesModel?.ClassName ?? "DeploymentVariables"} vars, CancellationToken cancellationToken = default)");
            builder.AppendLine("    {");
            foreach (var resource in model.Resources)
            {
                builder.AppendLine($"        {resource.CreationStatement}");
            }
            builder.AppendLine();
            builder.AppendLine("        return Task.CompletedTask;");
            builder.AppendLine("    }");
            builder.AppendLine();
            builder.AppendLine("    private static T ThrowUnsupported<T>(string message)");
            builder.AppendLine("    {");
            builder.AppendLine("        throw new global::System.NotSupportedException(message);");
            builder.AppendLine("    }");
            builder.AppendLine("}");

            return builder.ToString();
        }

        private static string RenderVariablesClass(CSharpPulumiProjectModel model)
        {
            var builder = new StringBuilder();
            var className = model.VariablesModel?.ClassName ?? "DeploymentVariables";
            builder.AppendLine($"public sealed class {className}");
            builder.AppendLine("{");

            if (model.VariablesModel != null)
            {
                foreach (var variable in model.VariablesModel.Variables)
                {
                    builder.AppendLine($"    public required {CSharpPulumiProjectExporter.GetGlobalTypeName(variable.VariableType)} {variable.PropertyName} {{ get; init; }}");
                }
            }

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
        IReadOnlyDictionary<string, object>? VariableValues = null,
        bool CleanOutputDirectories = true,
        bool GenerateTestProject = true,
        string? TestProjectName = null,
        string? TestRootNamespace = null,
        string? TestOutputDirectory = null,
        IReadOnlyCollection<string>? AdditionalNamespaces = null,
        IReadOnlyCollection<CSharpPackageReference>? AdditionalPackageReferences = null)
    {
        public string? OutputDirectory { get; init; } = OutputDirectory;
        public IReadOnlyDictionary<string, object>? VariableValues { get; init; } = VariableValues;
        public bool CleanOutputDirectories { get; init; } = CleanOutputDirectories;
        public bool GenerateTestProject { get; init; } = GenerateTestProject;
        public string? TestProjectName { get; init; } = TestProjectName;
        public string? TestRootNamespace { get; init; } = TestRootNamespace;
        public string? TestOutputDirectory { get; init; } = TestOutputDirectory;
        public IReadOnlyCollection<string> AdditionalNamespaces { get; init; } = AdditionalNamespaces ?? [];
        public IReadOnlyCollection<CSharpPackageReference> AdditionalPackageReferences { get; init; } = AdditionalPackageReferences ?? [];
    }

    public sealed record CSharpPulumiProjectExport(
        string DirectoryPath,
        string ProjectFilePath,
        string DeploymentFilePath,
        string? TestDirectoryPath,
        string? TestProjectFilePath,
        string? TestFilePath,
        CSharpPulumiProjectModel Model,
        CSharpPulumiTestProjectModel? TestModel);

    public sealed record CSharpPulumiProjectModel(
        string Deployment,
        string ProjectName,
        string RootNamespace,
        CSharpPulumiVariablesModel? VariablesModel,
        IReadOnlyCollection<string> AdditionalNamespaces,
        CSharpPulumiProjectDependencies Dependencies,
        IReadOnlyCollection<CSharpExportDiagnostic> Diagnostics,
        int CreatedCount,
        int ReferencedCount,
        IReadOnlyCollection<CSharpPulumiResourceModel> Resources);

    public sealed record CSharpPulumiProjectDependencies(
        IReadOnlyCollection<CSharpPackageReference> PackageReferences,
        IReadOnlyCollection<CSharpProjectReference> ProjectReferences);

    public sealed record CSharpExportDiagnostic(
        CSharpExportDiagnosticSeverity Severity,
        string Message,
        string? ResourceKey,
        string? PropertyPath);

    public enum CSharpExportDiagnosticSeverity
    {
        Info,
        Warning,
        Error
    }

    public sealed record CSharpPulumiResourceModel(
        string Key,
        string Kind,
        string Node,
        int ScopeId,
        string ResourceTypeName,
        string VariableName,
        string CreationStatement,
        IReadOnlyCollection<string> DependsOn);

    public sealed record CSharpPulumiTestProjectModel(
        string ProjectName,
        string RootNamespace,
        CSharpPulumiProjectDependencies Dependencies);

    public sealed record CSharpPulumiVariablesModel(
        string ClassName,
        IReadOnlyCollection<CSharpPulumiVariableModel> Variables);

    public sealed record CSharpPulumiVariableModel(
        string Key,
        string PropertyName,
        Type VariableType,
        object? Value);

    public sealed record CSharpPackageReference(string PackageId, string? Version);

    public sealed record CSharpProjectReference(string ProjectPath);

    internal static class CSharpPulumiDefaultPackageCatalog
    {
        public const string MicrosoftNetTestSdk = "Microsoft.NET.Test.Sdk";
        public const string Pulumi = "Pulumi";
        public const string PulumiAzureNative = "Pulumi.AzureNative";
        public const string PulumiDockerBuild = "Pulumi.DockerBuild";
        public const string XunitRunnerVisualStudio = "xunit.runner.visualstudio";
        public const string XunitV3 = "xunit.v3";

        private static readonly IReadOnlyDictionary<string, string> FallbackVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [MicrosoftNetTestSdk] = "18.5.1",
            [Pulumi] = "3.107.2",
            [PulumiAzureNative] = "3.19.0",
            [PulumiDockerBuild] = "0.0.16",
            [XunitRunnerVisualStudio] = "3.1.5",
            [XunitV3] = "3.2.2"
        };

        public static CSharpPackageReference Resolve(string packageId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(packageId);

            return CSharpRuntimePackageVersionResolver.Resolve(packageId, GetFallbackVersion(packageId));
        }

        public static string? GetFallbackVersion(string packageId)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(packageId);

            return FallbackVersions.TryGetValue(packageId, out var version) ? version : null;
        }
    }

    internal static class CSharpRuntimePackageVersionResolver
    {
        public static CSharpPackageReference Resolve(string packageId, string? version)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(packageId);

            if (TryResolve(packageId, version, out var resolvedPackageReference))
            {
                return resolvedPackageReference;
            }

            throw new InvalidOperationException($"Package reference '{packageId}' does not specify a version and no loaded assembly version could be resolved.");
        }

        public static bool TryResolve(string packageId, string? version, [NotNullWhen(true)] out CSharpPackageReference? packageReference)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(packageId);

            if (TryResolveLoadedAssemblyVersion(packageId, out var resolvedVersion))
            {
                packageReference = new CSharpPackageReference(packageId, resolvedVersion);
                return true;
            }

            if (string.IsNullOrWhiteSpace(version))
            {
                packageReference = null;
                return false;
            }

            packageReference = new CSharpPackageReference(packageId, version);
            return true;
        }

        private static bool TryResolveLoadedAssemblyVersion(string packageId, [NotNullWhen(true)] out string? version)
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(assembly => IsPackageAssemblyMatch(assembly, packageId));

            version = assembly?.GetName().Version?.ToString();
            return !string.IsNullOrWhiteSpace(version);
        }

        private static bool IsPackageAssemblyMatch(Assembly assembly, string packageId)
        {
            var assemblyName = assembly.GetName().Name;
            return string.Equals(assemblyName, packageId, StringComparison.OrdinalIgnoreCase) ||
                assembly.GetName().Name?.StartsWith(packageId + ".", StringComparison.OrdinalIgnoreCase) == true;
        }
    }

    internal static class KnownPackageRegistry
    {
        private static readonly ExportReferenceRule[] Rules =
        [
            new(
                ["Pulumi.AzureNative."],
                [CSharpPulumiDefaultPackageCatalog.Resolve(CSharpPulumiDefaultPackageCatalog.PulumiAzureNative)],
                []),
            new(
                ["Pulumi.DockerBuild."],
                [CSharpPulumiDefaultPackageCatalog.Resolve(CSharpPulumiDefaultPackageCatalog.PulumiDockerBuild)],
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
