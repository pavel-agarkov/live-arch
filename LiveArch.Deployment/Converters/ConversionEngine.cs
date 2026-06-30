using System;
using Type = System.Type;

namespace LiveArch.Deployment.Converters
{
    /// <summary>
    /// Converts raw DSL values into the CLR and Pulumi input shapes expected by target resources.
    /// </summary>
    public sealed class ConversionEngine(
        IConversionResolver conversionResolver,
        ConversionPlanExecutor conversionPlanExecutor) : IConversionEngine
    {
        private readonly IConversionResolver conversionResolver = conversionResolver;
        private readonly ConversionPlanExecutor conversionPlanExecutor = conversionPlanExecutor;

        /// <inheritdoc />
        public object ConvertValue(Type targetType, object sourceValue, string? converterName = null)
        {
            if (sourceValue == null)
            {
                return null!;
            }

            var normalizedTargetType = Nullable.GetUnderlyingType(targetType) ?? targetType;
            var request = new ConversionRequest(normalizedTargetType, sourceValue);

            var plan = conversionResolver.Resolve(request, converterName);
            if (plan != null)
            {
                return conversionPlanExecutor.Execute(plan, request);
            }

            throw CreateNotSupportedException(request, converterName);
        }

        /// <inheritdoc />
        public object ConvertValue(ConversionPlan plan, Type targetType, object sourceValue)
        {
            ArgumentNullException.ThrowIfNull(plan);

            if (sourceValue == null)
            {
                return null!;
            }

            var normalizedTargetType = Nullable.GetUnderlyingType(targetType) ?? targetType;
            var request = new ConversionRequest(normalizedTargetType, sourceValue);
            return conversionPlanExecutor.Execute(plan, request);
        }

        private static NotSupportedException CreateNotSupportedException(ConversionRequest request, string? converterName)
        {
            return string.IsNullOrWhiteSpace(converterName)
                ? new NotSupportedException($"Cannot convert '{request.SourceValue}' ({request.SourceType.FullName}) to {request.TargetType.FullName}")
                : new NotSupportedException($"Named converter '{converterName}' cannot convert '{request.SourceValue}' ({request.SourceType.FullName}) to {request.TargetType.FullName}");
        }
    }
}
