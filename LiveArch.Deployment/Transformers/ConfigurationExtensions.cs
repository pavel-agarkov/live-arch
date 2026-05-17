using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LiveArch.Deployment.Transformers
{
    /// <summary>
    /// Registers transformer factories, registries, and parsing services in dependency injection.
    /// </summary>
    public static class ConfigurationExtensions
    {
        private static IServiceCollection EnsureTransformerServices(this IServiceCollection services)
        {
            services.TryAddSingleton<TransformerRegistry>();
            services.TryAddSingleton<ITransformerRegistry>(serviceProvider => serviceProvider.GetRequiredService<TransformerRegistry>());
            services.TryAddSingleton<TransformerPipeline>();
            return services;
        }

        /// <summary>
        /// Registers a custom named transformer factory type.
        /// </summary>
        /// <typeparam name="TFactory">Factory implementation type.</typeparam>
        /// <param name="services">Service collection to extend.</param>
        /// <returns>The updated service collection.</returns>
        /// <remarks>
        /// Built-in transformers remain available automatically. If the custom factory uses the same name as a built-in transformer,
        /// the custom implementation overrides the built-in one.
        /// </remarks>
        public static IServiceCollection AddNamedTransformer<TFactory>(this IServiceCollection services)
            where TFactory : class, INamedTransformerFactory
        {
            services.AddSingleton<INamedTransformerFactory, TFactory>();
            return services.EnsureTransformerServices();
        }

        /// <summary>
        /// Registers a custom named transformer using a delegate-based factory.
        /// </summary>
        /// <param name="services">Service collection to extend.</param>
        /// <param name="name">DSL name of the transformer.</param>
        /// <param name="factory">Factory delegate that creates the transformer from its DSL parameter.</param>
        /// <returns>The updated service collection.</returns>
        /// <remarks>
        /// Built-in transformers remain available automatically. If the custom factory uses the same name as a built-in transformer,
        /// the custom implementation overrides the built-in one.
        /// </remarks>
        public static IServiceCollection AddNamedTransformer(this IServiceCollection services, string name, Func<string, ITransformer> factory)
        {
            services.AddSingleton<INamedTransformerFactory>(new NamedTransformerFactory(name, factory));
            return services.EnsureTransformerServices();
        }

        /// <summary>
        /// Registers transformer services together with the built-in transformer set.
        /// </summary>
        /// <param name="services">Service collection to extend.</param>
        /// <returns>The updated service collection.</returns>
        public static IServiceCollection AddDefaultTransformers(this IServiceCollection services)
        {
            return services.EnsureTransformerServices();
        }
    }
}
