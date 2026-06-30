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
        /// Adds a named converter that can be selected explicitly from the DSL.
        /// </summary>
        /// <typeparam name="TConverter">Named converter implementation.</typeparam>
        /// <param name="services">Service collection to extend.</param>
        /// <returns>The updated service collection.</returns>
        public static IServiceCollection AddNamedValueConverter<TConverter>(this IServiceCollection services)
            where TConverter : class, INamedValueConverter
        {
            services.AddTransient<INamedValueConverter, TConverter>();
            services.TryAddTransient<IConversionResolver, ConversionResolver>();
            services.TryAddTransient<ConversionPlanExecutor>();
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
            services.TryAddTransient<IConversionResolver, ConversionResolver>();
            services.TryAddTransient<ConversionPlanExecutor>();
            services.TryAddTransient<ConversionEngine>();
            services.TryAddTransient<IConversionEngine>(serviceProvider => serviceProvider.GetRequiredService<ConversionEngine>());
            return services;
        }
    }
}
