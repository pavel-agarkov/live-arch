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

        public bool CanConvert(ConversionRequest request)
        {
            return !ConversionTypeHelpers.IsOutput(request.SourceType) && request.TargetType == typeof(ConnStringInfoArgs);
        }

        public object Convert(ConversionRequest request, IConversionEngine engine)
        {
            return new ConnStringInfoArgs
            {
                ConnectionString = (Input<string>)engine.ConvertValue(typeof(Input<string>), request.SourceValue, request.Context),
                Type = ConnectionStringType.SQLAzure,
            };
        }
    }
}
