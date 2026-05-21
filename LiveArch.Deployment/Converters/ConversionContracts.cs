using System;

namespace LiveArch.Deployment.Converters
{
    /// <summary>
    /// Describes a single conversion request.
    /// </summary>
    public readonly record struct ConversionRequest(
        Type TargetType,
        object SourceValue)
    {
        /// <summary>
        /// Gets the CLR type of the source value.
        /// </summary>
        public Type SourceType => SourceValue.GetType();
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
    }

    /// <summary>
    /// Defines a value converter used by the conversion engine.
    /// </summary>
    public interface IValueConverter
    {
        /// <summary>
        /// Determines whether the converter can handle the supplied request.
        /// </summary>
        /// <param name="request">Conversion request to inspect.</param>
        /// <returns><c>true</c> when the converter can handle the request; otherwise <c>false</c>.</returns>
        bool CanConvert(ConversionRequest request);

        /// <summary>
        /// Converts the supplied request.
        /// </summary>
        /// <param name="request">Conversion request to process.</param>
        /// <param name="engine">Conversion engine for nested conversions.</param>
        /// <returns>The converted value.</returns>
        object Convert(ConversionRequest request, IConversionEngine engine);
    }

    /// <summary>
    /// Marks a converter that participates in the automatic converter pipeline.
    /// </summary>
    public interface ITypedValueConverter : IValueConverter
    {
    }

    /// <summary>
    /// Marks a converter that is selected explicitly by name from the DSL.
    /// </summary>
    public interface INamedValueConverter : IValueConverter
    {
        /// <summary>
        /// Gets the DSL-visible converter name.
        /// </summary>
        string Name { get; }
    }
}
