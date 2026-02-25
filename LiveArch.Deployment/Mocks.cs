using Pulumi.Testing;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading.Tasks;


namespace LiveArch.Deployment
{
    public class Mocks : IMocks
    {
        public Task<(string? id, object state)> NewResourceAsync(MockResourceArgs args)
        {
            var outputs = ImmutableDictionary.CreateBuilder<string, object>();

            // Forward all input parameters as resource outputs, so that we could test them.
            outputs.AddRange(args.Inputs);

            // <-- We'll customize the mocks here

            // Default the resource ID to `{name}_id`.
            args.Id ??= $"{args.Name}_id";
            return Task.FromResult((args.Id, (object)outputs));
        }

        public Task<object> CallAsync(MockCallArgs args)
        {
            var outputDic = new Dictionary<string, object>() {
                { "name", "test-name" },
                { "location", "test-location" },
                { "id", Guid.NewGuid().ToString() },
                { "serverFarmId", "test-app-service-plan" },
                { "serverName", "test-server-name" },
                { "ref", "some.test/docker-image:tag" }
            };

            foreach ((var key, var val) in args.Args)
            {
                outputDic[key] = val;
            }

            return Task.FromResult((object)outputDic);
        }
    }
}
