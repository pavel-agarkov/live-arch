using System;

namespace LiveArch.Deployment.Converters
{
    public sealed class ConversionContext(Func<string, object>? stringValueResolver = null)
    {
        public static ConversionContext Empty { get; } = new();

        public Func<string, object> StringValueResolver { get; } = stringValueResolver ?? (static value => value);

        public object ResolveString(string value) => StringValueResolver(value);
    }

    public readonly record struct ConversionRequest(
        Type TargetType,
        object SourceValue,
        ConversionContext Context)
    {
        public Type SourceType => SourceValue.GetType();
    }

    public interface IConversionEngine
    {
        object ConvertValue(Type targetType, object sourceValue, ConversionContext context, string? converterName = null);
    }

    public interface IValueConverter
    {
        bool CanConvert(ConversionRequest request);

        object Convert(ConversionRequest request, IConversionEngine engine);
    }

    public interface ITypedValueConverter : IValueConverter
    {
    }

    public interface INamedValueConverter : IValueConverter
    {
        string Name { get; }
    }
}
