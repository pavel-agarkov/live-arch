using System;
using System.Collections.Generic;
using System.Linq;
using Type = System.Type;

namespace LiveArch.Deployment.Converters
{
    /// <summary>
    /// Converts raw DSL values into the CLR and Pulumi input shapes expected by target resources.
    /// </summary>
    public sealed class ConversionEngine(
        IEnumerable<ITypedValueConverter> typedConverters,
        IEnumerable<INamedValueConverter> namedConverters) : IConversionEngine
    {
        private readonly IReadOnlyList<ITypedValueConverter> automaticConverters = [.. typedConverters];

        private readonly IReadOnlyDictionary<string, INamedValueConverter> namedConverterLookup = namedConverters
            .GroupBy(converter => converter.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                grouping => grouping.Key,
                grouping => grouping.Single(),
                StringComparer.OrdinalIgnoreCase);

        /// <inheritdoc />
        public object ConvertValue(Type targetType, object sourceValue, string? converterName = null)
        {
            if (sourceValue == null)
            {
                return null!;
            }

            var normalizedTargetType = Nullable.GetUnderlyingType(targetType) ?? targetType;
            var request = new ConversionRequest(normalizedTargetType, sourceValue);

            return string.IsNullOrWhiteSpace(converterName)
                ? ConvertUsingAutomaticConverters(request)
                : ConvertUsingNamedConverters(request, converterName!);
        }

        private object ConvertUsingAutomaticConverters(ConversionRequest request)
        {
            foreach (var converter in automaticConverters)
            {
                if (converter.CanConvert(request))
                {
                    return converter.Convert(request, this);
                }
            }

            throw new NotSupportedException($"Cannot convert '{request.SourceValue}' ({request.SourceType.FullName}) to {request.TargetType.FullName}");
        }

        private object ConvertUsingNamedConverters(ConversionRequest request, string converterName)
        {
            if (namedConverterLookup.TryGetValue(converterName, out var converter) && converter.CanConvert(request))
            {
                return converter.Convert(request, this);
            }

            if (ConversionTypeHelpers.IsOutput(request.SourceType))
            {
                var descriptor = ConversionTypeHelpers.GetOutputProjectionDescriptor(request.TargetType);
                var sourceInnerType = request.SourceType.GetGenericArguments()[0];
                var projected = ConversionTypeHelpers.ProjectOutput(
                    request.SourceValue,
                    sourceInnerType,
                    descriptor.ProjectedTargetType,
                    value => ConvertValue(descriptor.ProjectedTargetType, value, converterName));

                return descriptor.WrapProjectedOutput(projected);
            }

            throw new NotSupportedException($"Named converter '{converterName}' cannot convert '{request.SourceValue}' ({request.SourceType.FullName}) to {request.TargetType.FullName}");
        }
    }
}
