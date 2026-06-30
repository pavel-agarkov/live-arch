using System;
using System.Collections.Generic;
using System.Collections;
using System.Collections.Immutable;
using System.Linq;

namespace LiveArch.Deployment.Converters
{
    /// <summary>
    /// Resolves explicit conversion plans using the current converter registrations.
    /// </summary>
    public sealed class ConversionResolver(
        IEnumerable<INamedValueConverter> namedConverters) : IConversionResolver
    {
        private readonly IReadOnlyDictionary<string, INamedValueConverter> namedConverterLookup = namedConverters
            .GroupBy(converter => converter.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                grouping => grouping.Key,
                grouping => grouping.Single(),
                StringComparer.OrdinalIgnoreCase);

        /// <inheritdoc />
        public ConversionPlan? Resolve(IConversionRequest request, string? converterName = null)
        {
            if (string.IsNullOrWhiteSpace(converterName))
            {
                return ResolveAutomatic(request);
            }

            return ResolveNamed(request, converterName);
        }

        /// <summary>
        /// Creates a keyed-list fallback conversion plan that instantiates the target item and populates its <c>Value</c> property.
        /// </summary>
        /// <param name="itemType">Target item type to instantiate.</param>
        /// <param name="sourceType">Source value type to convert.</param>
        /// <returns>The keyed-list fallback conversion plan.</returns>
        public ConversionPlan CreateKeyedListItemPlan(Type itemType, Type sourceType)
        {
            var valueProperty = itemType.GetProperty("Value", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                ?? throw new InvalidOperationException($"{itemType.Name} must contain a public Value property for keyed-list fallback");

            var valuePlan = ResolveAutomatic(CreatePlanningRequest(valueProperty.PropertyType, sourceType));
            var valueStep = valuePlan?.RootStep
                ?? throw CreateAutomaticPlanningException(valueProperty.PropertyType, sourceType);

            return new ConversionPlan(new KeyedListItemConversionStep(itemType, valueProperty.PropertyType, valueStep));
        }

        private ConversionPlan? ResolveAutomatic(IConversionRequest request)
        {
            return TryCreateBuiltInPlan(request);
        }

        private ConversionPlan? ResolveNamed(IConversionRequest request, string converterName)
        {
            if (!namedConverterLookup.TryGetValue(converterName, out var converter))
            {
                return null;
            }

            if (converter.CanConvert(request))
            {
                return new ConversionPlan(new NamedConverterStep(converter.GetType(), request.TargetType));
            }

            if (ConversionTypeHelpers.IsOutput(request.SourceType))
            {
                var descriptor = ConversionTypeHelpers.GetOutputProjectionDescriptor(request.TargetType);
                return new ConversionPlan(new ProjectedOutputConversionStep(
                    descriptor.ProjectedTargetType,
                    new NamedConverterStep(converter.GetType(), descriptor.ProjectedTargetType)));
            }

            return null;
        }

        private ConversionPlan? TryCreateBuiltInPlan(IConversionRequest request)
        {
            if (request.TargetType == typeof(object) || request.TargetType.IsAssignableFrom(request.SourceType))
            {
                return new ConversionPlan(new AssignableConversionStep());
            }

            if (!ConversionTypeHelpers.IsOutput(request.SourceType) && CanConvertPrimitive(request))
            {
                return new ConversionPlan(new PrimitiveConversionStep());
            }

            if (!ConversionTypeHelpers.IsOutput(request.SourceType) && ConversionTypeHelpers.IsPulumiEnum(request.TargetType))
            {
                return new ConversionPlan(new PulumiEnumConversionStep());
            }

            if (!ConversionTypeHelpers.IsOutput(request.SourceType) && ConversionTypeHelpers.IsGenericUnion(request.TargetType))
            {
                return new ConversionPlan(CreateUnionPlan(request));
            }

            if (!ConversionTypeHelpers.IsOutput(request.SourceType) && ConversionTypeHelpers.IsGenericInputUnion(request.TargetType))
            {
                return new ConversionPlan(CreateInputUnionPlan(request));
            }

            if (!ConversionTypeHelpers.IsOutput(request.SourceType) && ConversionTypeHelpers.IsGenericInput(request.TargetType))
            {
                return new ConversionPlan(CreateInputStep(request));
            }

            if (!ConversionTypeHelpers.IsOutput(request.SourceType) && ConversionTypeHelpers.IsGenericInputList(request.TargetType))
            {
                return new ConversionPlan(CreateInputListStep(request));
            }

            if (!ConversionTypeHelpers.IsOutput(request.SourceType) && IsImmutableArrayType(request.TargetType))
            {
                return new ConversionPlan(CreateImmutableArrayStep(request));
            }

            if (!ConversionTypeHelpers.IsOutput(request.SourceType) && IsImmutableDictionaryType(request))
            {
                return new ConversionPlan(CreateImmutableDictionaryStep(request));
            }

            if (ConversionTypeHelpers.TryGetImplicitOperator(request.TargetType, request.SourceType, out _))
            {
                return new ConversionPlan(new ImplicitOperatorConversionStep(request.TargetType));
            }

            if (ConversionTypeHelpers.IsOutput(request.SourceType))
            {
                return new ConversionPlan(CreateProjectedOutputStep(request));
            }

            return null;
        }

        private ConversionStep CreateInputStep(IConversionRequest request)
        {
            var innerType = request.TargetType.GetGenericArguments()[0];
            var innerPlan = ResolveAutomatic(CreatePlanningRequest(innerType, request.SourceType));
            return new InputConversionStep(innerType, innerPlan?.RootStep ?? throw CreateAutomaticPlanningException(innerType, request.SourceType));
        }

        private ConversionStep CreateInputListStep(IConversionRequest request)
        {
            var elementType = request.TargetType.GetGenericArguments()[0];
            var sourceElementType = TryGetEnumerableElementType(request.SourceType)
                ?? throw new NotSupportedException($"Cannot derive element type from source type {request.SourceType.FullName}");
            var elementPlan = ResolveAutomatic(CreatePlanningRequest(elementType, sourceElementType));
            return new InputListConversionStep(elementType, elementPlan?.RootStep ?? throw CreateAutomaticPlanningException(elementType, sourceElementType));
        }

        private ConversionStep CreateImmutableArrayStep(IConversionRequest request)
        {
            var elementType = request.TargetType.GetGenericArguments()[0];
            var sourceElementType = TryGetEnumerableElementType(request.SourceType)
                ?? throw new NotSupportedException($"Cannot derive element type from source type {request.SourceType.FullName}");
            var elementPlan = ResolveAutomatic(CreatePlanningRequest(elementType, sourceElementType));
            return new ImmutableArrayConversionStep(elementType, elementPlan?.RootStep ?? throw CreateAutomaticPlanningException(elementType, sourceElementType));
        }

        private ConversionStep CreateImmutableDictionaryStep(IConversionRequest request)
        {
            var valueType = request.TargetType.GetGenericArguments()[1];
            var sourceValueType = TryGetDictionaryValueType(request.SourceType)
                ?? throw new NotSupportedException($"Cannot derive dictionary value type from source type {request.SourceType.FullName}");
            var valuePlan = ResolveAutomatic(CreatePlanningRequest(valueType, sourceValueType));
            return new ImmutableDictionaryConversionStep(valueType, valuePlan?.RootStep ?? throw CreateAutomaticPlanningException(valueType, sourceValueType));
        }

        private StringEnumUnionConversionStep CreateUnionPlan(IConversionRequest request)
        {
            var unionArgs = request.TargetType.GetGenericArguments();
            if (unionArgs.Length != 2 || unionArgs[0] != typeof(string))
            {
                throw new NotSupportedException($"Union type {request.TargetType} must be Union<string, TEnum>");
            }

            var enumType = unionArgs[1];
            var enumPlan = ResolveAutomatic(CreatePlanningRequest(enumType, request.SourceType));
            var enumStep = enumPlan?.RootStep 
                ?? throw CreateAutomaticPlanningException(enumType, request.SourceType);

            return new StringEnumUnionConversionStep(enumType, enumStep);
        }

        private StringEnumInputUnionConversionStep CreateInputUnionPlan(IConversionRequest request)
        {
            var unionArgs = request.TargetType.GetGenericArguments();
            if (unionArgs.Length != 2 || unionArgs[0] != typeof(string))
            {
                throw new NotSupportedException($"InputUnion type {request.TargetType} must be InputUnion<string, TEnum>");
            }

            var enumType = unionArgs[1];
            var enumPlan = ResolveAutomatic(CreatePlanningRequest(enumType, request.SourceType));
            var enumStep = enumPlan?.RootStep 
                ?? throw CreateAutomaticPlanningException(enumType, request.SourceType);

            return new StringEnumInputUnionConversionStep(enumType, enumStep);
        }

        private ConversionStep CreateProjectedOutputStep(IConversionRequest request)
        {
            var descriptor = ConversionTypeHelpers.GetOutputProjectionDescriptor(request.TargetType);
            var sourceInnerType = request.SourceType.GetGenericArguments()[0];
            var projectedPlan = ResolveAutomatic(CreatePlanningRequest(descriptor.ProjectedTargetType, sourceInnerType));
            return new ProjectedOutputConversionStep(
                descriptor.ProjectedTargetType,
                projectedPlan?.RootStep ?? throw CreateAutomaticPlanningException(descriptor.ProjectedTargetType, sourceInnerType));
        }

        private bool CanConvertPrimitive(IConversionRequest request)
        {
            return request.TargetType == typeof(string) ||
                request.TargetType == typeof(bool) ||
                NumericTypes.Contains(request.TargetType) && IsConvertibleType(request.SourceType);
        }

        private bool IsImmutableArrayType(Type targetType)
        {
            return targetType.IsGenericType &&
                targetType.GetGenericTypeDefinition() == typeof(System.Collections.Immutable.ImmutableArray<>);
        }

        private bool IsImmutableDictionaryType(IConversionRequest request)
        {
            return request.TargetType.IsGenericType &&
                request.TargetType.GetGenericTypeDefinition() == typeof(System.Collections.Immutable.ImmutableDictionary<,>) &&
                request.TargetType.GetGenericArguments()[0] == typeof(string) &&
                typeof(IDictionary).IsAssignableFrom(request.SourceType);
        }

        private InvalidOperationException CreateAutomaticPlanningException(Type targetType, Type sourceType)
        {
            return new InvalidOperationException($"Cannot build automatic conversion plan from {sourceType.FullName} to {targetType.FullName}");
        }

        private static IConversionRequest CreatePlanningRequest(Type targetType, Type sourceType)
        {
            return new ConversionRequest(targetType, sourceType, null!);
        }

        private static bool IsConvertibleType(Type type)
        {
            return typeof(IConvertible).IsAssignableFrom(type);
        }

        private static readonly HashSet<Type> NumericTypes =
        [
            typeof(byte),
            typeof(sbyte),
            typeof(short),
            typeof(ushort),
            typeof(int),
            typeof(uint),
            typeof(long),
            typeof(ulong),
            typeof(float),
            typeof(double),
            typeof(decimal)
        ];

        private static Type? TryGetEnumerableElementType(Type sourceType)
        {
            if (sourceType.IsArray)
            {
                return sourceType.GetElementType();
            }

            if (sourceType.IsGenericType)
            {
                var genericDefinition = sourceType.GetGenericTypeDefinition();
                if (genericDefinition == typeof(ImmutableArray<>) ||
                    genericDefinition == typeof(IEnumerable<>) ||
                    genericDefinition == typeof(ICollection<>) ||
                    genericDefinition == typeof(IList<>) ||
                    genericDefinition == typeof(List<>))
                {
                    return sourceType.GetGenericArguments()[0];
                }
            }

            var enumerableInterface = sourceType
                .GetInterfaces()
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
            if (enumerableInterface != null)
            {
                return enumerableInterface.GetGenericArguments()[0];
            }

            return null;
        }

        private static Type? TryGetDictionaryValueType(Type sourceType)
        {
            if (sourceType.IsGenericType && sourceType.GetGenericTypeDefinition() == typeof(ImmutableDictionary<,>))
            {
                var args = sourceType.GetGenericArguments();
                if (args[0] == typeof(string))
                {
                    return args[1];
                }
            }

            var dictionaryInterface = sourceType
                .GetInterfaces()
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>));
            if (dictionaryInterface != null)
            {
                var args = dictionaryInterface.GetGenericArguments();
                if (args[0] == typeof(string))
                {
                    return args[1];
                }
            }

            return null;
        }
    }
}
