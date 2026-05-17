using LiveArch.Deployment.Converters;
using Microsoft.Extensions.DependencyInjection;

namespace LiveArch.Deployment.Azure.Converters
{
    public static class ConfigurationExtensions
    {
        public static IServiceCollection AddAzureValueConverters(this IServiceCollection services)
        {
            return services.AddNamedValueConverter<AzureSqlConnectionStringConverter>();
        }
    }
}
