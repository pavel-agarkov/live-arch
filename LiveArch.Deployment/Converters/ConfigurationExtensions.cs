using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LiveArch.Deployment.Converters
{
    public static class ConfigurationExtensions
    {
        public static IServiceCollection AddTypedValueConverter<TConverter>(this IServiceCollection services)
            where TConverter : class, ITypedValueConverter
        {
            services.AddTransient<ITypedValueConverter, TConverter>();
            services.TryAddTransient<ConversionEngine>();
            services.TryAddTransient<IConversionEngine>(serviceProvider => serviceProvider.GetRequiredService<ConversionEngine>());
            return services;
        }

        public static IServiceCollection AddNamedValueConverter<TConverter>(this IServiceCollection services)
            where TConverter : class, INamedValueConverter
        {
            services.AddTransient<INamedValueConverter, TConverter>();
            services.TryAddTransient<ConversionEngine>();
            services.TryAddTransient<IConversionEngine>(serviceProvider => serviceProvider.GetRequiredService<ConversionEngine>());
            return services;
        }

        public static IServiceCollection AddDefaultValueConverters(this IServiceCollection services)
        {
            return services
                .AddTypedValueConverter<AssignableValueConverter>()
                .AddTypedValueConverter<ImplicitOperatorValueConverter>()
                .AddTypedValueConverter<PrimitiveValueConverter>()
                .AddTypedValueConverter<PulumiEnumValueConverter>()
                .AddTypedValueConverter<UnionValueConverter>()
                .AddTypedValueConverter<InputUnionValueConverter>()
                .AddTypedValueConverter<InputValueConverter>()
                .AddTypedValueConverter<InputListValueConverter>()
                .AddTypedValueConverter<InputMapValueConverter>()
                .AddTypedValueConverter<ProjectedOutputValueConverter>()
                .AddNamedValueConverter<DefaultKeyedListValueConverter>();
        }
    }
}
