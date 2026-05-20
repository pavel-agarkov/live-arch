using LiveArch.Deployment.Converters;
using Microsoft.Extensions.DependencyInjection;

namespace LiveArch.Deployment.Azure.Converters
{
    /// <summary>
    /// Registers Azure-specific named value converters.
    /// </summary>
    public static class ConfigurationExtensions
    {
        /// <summary>
        /// Adds the default Azure-specific value converters used by the deployment engine.
        /// </summary>
        /// <param name="services">Service collection to extend.</param>
        /// <returns>The updated service collection.</returns>
        public static IServiceCollection AddAzureValueConverters(this IServiceCollection services)
        {
            return services.AddNamedValueConverter<AzureSqlConnectionStringConverter>();
        }
    }
}
