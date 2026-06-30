using System;

namespace LiveArch.Deployment.Converters
{
    /// <summary>
    /// Describes type metadata for conversion planning.
    /// </summary>
    public interface IConversionRequest
    {
        /// <summary>
        /// Gets the target CLR type to produce.
        /// </summary>
        Type TargetType { get; }

        /// <summary>
        /// Gets the source CLR type to convert from.
        /// </summary>
        Type SourceType { get; }
    }

    /// <summary>
    /// Describes a single conversion request with runtime value for execution.
    /// </summary>
    public readonly record struct ConversionRequest(
        Type TargetType,
        Type SourceType,
        object SourceValue) : IConversionRequest
    {
        /// <summary>
        /// Creates a conversion request from target type and source value, deriving source type automatically.
        /// </summary>
        public ConversionRequest(Type targetType, object sourceValue)
            : this(targetType, sourceValue.GetType(), sourceValue)
        {
        }
    }

    /// <summary>
    /// Resolves the effective conversion plan for a request using current DI registrations.
    /// </summary>
    public interface IConversionResolver
    {
        /// <summary>
        /// Resolves the effective conversion plan for the supplied request.
        /// </summary>
        /// <param name="request">Conversion request to inspect.</param>
        /// <param name="converterName">Optional named converter to force for this conversion.</param>
        /// <returns>The resolved conversion plan, or <c>null</c> when the request is unsupported.</returns>
        ConversionPlan? Resolve(IConversionRequest request, string? converterName = null);

        /// <summary>
        /// Creates the structural fallback plan for keyed-list item assignment.
        /// </summary>
        /// <param name="itemType">Target item type to instantiate.</param>
        /// <param name="sourceType">Source CLR type to convert.</param>
        /// <returns>The resolved fallback plan.</returns>
        ConversionPlan CreateKeyedListItemPlan(Type itemType, Type sourceType);
    }

    /// <summary>
    /// Converts values from the DSL into target CLR or Pulumi input shapes.
    /// </summary>
    public interface IConversionEngine
    {
        /// <summary>
        /// Converts a source value into the requested target type.
        /// </summary>
        /// <param name="targetType">Destination type to produce.</param>
        /// <param name="sourceValue">Source value to convert.</param>
        /// <param name="converterName">Optional named converter to force for this conversion.</param>
        /// 
        /// <returns>The converted value.</returns>
        object ConvertValue(Type targetType, object sourceValue, string? converterName = null);

        /// <summary>
        /// Executes a previously resolved conversion plan for the supplied request.
        /// </summary>
        /// <param name="plan">Plan to execute.</param>
        /// <param name="targetType">Destination type to produce.</param>
        /// <param name="sourceValue">Source value to convert.</param>
        /// <returns>The converted value.</returns>
        object ConvertValue(ConversionPlan plan, Type targetType, object sourceValue);
    }

    /// <summary>
    /// Marks a converter that is selected explicitly by name from the DSL.
    /// </summary>
    public interface INamedValueConverter
    {
        /// <summary>
        /// Gets the DSL-visible converter name.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Determines whether the converter can handle the supplied request.
        /// </summary>
        /// <param name="request">Conversion request to inspect.</param>
        /// <returns><c>true</c> when the converter can handle the request; otherwise <c>false</c>.</returns>
        bool CanConvert(IConversionRequest request);

        /// <summary>
        /// Converts the supplied request.
        /// </summary>
        /// <param name="request">Conversion request to process.</param>
        /// <returns>The converted value.</returns>
        object Convert(ConversionRequest request);
    }
}
