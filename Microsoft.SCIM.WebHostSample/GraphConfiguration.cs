using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Graph;
using Microsoft.Graph.Auth;
using Microsoft.Identity.Client;

namespace Microsoft.SCIM.WebHostSample
{
    public static class GraphConfiguration
    {
        public static GraphServiceClient AddGraphComponent(this IServiceCollection services, IConfiguration configuration)
        {
            var graphConfig = configuration.GetSection("AzureAD");

            IConfidentialClientApplication confidentialClientApplication = ConfidentialClientApplicationBuilder
                .Create(graphConfig["ClientId"])
                .WithTenantId(graphConfig["TenantId"])
                .WithClientSecret(graphConfig["ClientSecret"])
                .Build();

            var authenticationProvider = new ClientCredentialProvider(confidentialClientApplication);

            var client = new GraphServiceClient(authenticationProvider);
            //you can use a single client instance for the lifetime of the application
            services.AddSingleton(sp =>
            {
                return client;
            });

            return client;
        }
    }
}
