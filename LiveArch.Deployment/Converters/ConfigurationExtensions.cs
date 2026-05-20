using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LiveArch.Deployment.Converters
{
    /// <summary>
    /// Registers conversion engine services and value converters in dependency injection.
    /// </summary>
    public static class ConfigurationExtensions
    {
        /// <summary>
        /// Adds an automatically selected typed converter.
        /// </summary>
        /// <typeparam name="TConverter">Typed converter implementation.</typeparam>
        /// <param name="services">Service collection to extend.</param>
        /// <returns>The updated service collection.</returns>
        public static IServiceCollection AddTypedValueConverter<TConverter>(this IServiceCollection services)
            where TConverter : class, ITypedValueConverter
        {
            services.AddTransient<ITypedValueConverter, TConverter>();
            services.TryAddTransient<ConversionEngine>();
            services.TryAddTransient<IConversionEngine>(serviceProvider => serviceProvider.GetRequiredService<ConversionEngine>());
            return services;
        }

        /// <summary>
        /// Adds a named converter that can be selected explicitly from the DSL.
        /// </summary>
        /// <typeparam name="TConverter">Named converter implementation.</typeparam>
        /// <param name="services">Service collection to extend.</param>
        /// <returns>The updated service collection.</returns>
        public static IServiceCollection AddNamedValueConverter<TConverter>(this IServiceCollection services)
            where TConverter : class, INamedValueConverter
        {
            services.AddTransient<INamedValueConverter, TConverter>();
            services.TryAddTransient<ConversionEngine>();
            services.TryAddTransient<IConversionEngine>(serviceProvider => serviceProvider.GetRequiredService<ConversionEngine>());
            return services;
        }

        /// <summary>
        /// Registers the default converter set used by the deployment engine.
        /// </summary>
        /// <param name="services">Service collection to extend.</param>
        /// <returns>The updated service collection.</returns>
        public static IServiceCollection AddDefaultValueConverters(this IServiceCollection services)
        {
            return services
                .AddTypedValueConverter<AssignableValueConverter>()
                .AddTypedValueConverter<PrimitiveValueConverter>()
                .AddTypedValueConverter<PulumiEnumValueConverter>()
                .AddTypedValueConverter<UnionValueConverter>()
                .AddTypedValueConverter<InputUnionValueConverter>()
                .AddTypedValueConverter<InputValueConverter>()
                .AddTypedValueConverter<InputListValueConverter>()
                .AddTypedValueConverter<ImmutableArrayValueConverter>()
                .AddTypedValueConverter<ImmutableDictionaryValueConverter>()
                .AddTypedValueConverter<ImplicitOperatorValueConverter>()
                .AddTypedValueConverter<ProjectedOutputValueConverter>()
                .AddNamedValueConverter<DefaultKeyedListValueConverter>();
        }
    }
}
