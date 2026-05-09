using Microsoft.Extensions.Configuration;
using System.Text.RegularExpressions;

namespace LiveArch.Deployment.Runner
{
    internal sealed class DeploymentVariablesProvider
    {
        private static readonly Regex DeploymentVariableKeyRegex = new("^[A-Z0-9_\\.:-]+$", RegexOptions.Compiled);

        private static readonly HashSet<string> ReservedKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "environment",
            "deployment",
            "workspace-path",
            "workspacePath",
            "workspace",
            "WORKSPACE_PATH"
        };

        private readonly IConfiguration configuration;

        public DeploymentVariablesProvider(IConfiguration configuration)
        {
            this.configuration = configuration;
        }

        public IReadOnlyDictionary<string, object> GetVariables()
        {
            var variables = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            foreach (var pair in configuration.AsEnumerable())
            {
                if (string.IsNullOrWhiteSpace(pair.Value))
                {
                    continue;
                }

                if (pair.Key.StartsWith("variables:", StringComparison.OrdinalIgnoreCase))
                {
                    variables[pair.Key["variables:".Length..]] = pair.Value;
                    continue;
                }

                if (ReservedKeys.Contains(pair.Key))
                {
                    continue;
                }

                if (DeploymentVariableKeyRegex.IsMatch(pair.Key))
                {
                    variables[pair.Key] = pair.Value;
                }
            }

            return variables;
        }
    }
}
