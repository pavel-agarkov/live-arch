using LiveArch.Deployment.Converters;
using Pulumi;
using Pulumi.AzureNative.Web;
using Pulumi.AzureNative.Web.Inputs;
using System;

namespace LiveArch.Deployment.Azure.Converters
{
    public sealed class AzureSqlConnectionStringConverter : INamedValueConverter
    {
        public string Name => AzureKnownConverterNames.AzureSqlConnectionString;

        public bool CanConvert(IConversionRequest request)
        {
            return !ConversionTypeHelpers.IsOutput(request.SourceType) && request.TargetType == typeof(ConnStringInfoArgs);
        }

        public object Convert(ConversionRequest request)
        {
            return new ConnStringInfoArgs
            {
                ConnectionString = request.SourceValue as Input<string> ?? (Input<string>)request.SourceValue.ToString()!,
                Type = ConnectionStringType.SQLAzure,
            };
        }
    }
}
