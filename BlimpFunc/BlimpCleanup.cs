using System;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Extensions.Logging;
using Microsoft.Azure.Management.ContainerRegistry;
using Microsoft.Azure.Management.WebSites;
using Microsoft.Azure.Management.WebSites.Models;
using blimp
    ;
using System.Collections.Generic;
using System.Linq;

namespace BlimpFunc
{
    
    public static class BlimpCleanup
    {
        public static SecretsUtils secretsUtils { get; private set; }
        public static PipelineUtils pipelineUtils { get; private set; }
        public static string resourceGroupName { get; private set; }
        public static WebSiteManagementClient webClient { get; private set; }

        [FunctionName("BlimpCleanup")]
        public static void Run([TimerTrigger("0 0 * * * 0")]TimerInfo myTimer, ILogger log)
        {
            // clean up blimp app every sunday
            secretsUtils = new SecretsUtils();
            secretsUtils.GetSecrets();
            pipelineUtils = new PipelineUtils(
                new ContainerRegistryManagementClient(secretsUtils._credentials),
                new WebSiteManagementClient(secretsUtils._credentials),
                secretsUtils._subId
            );
            resourceGroupName = "blimprg";
            webClient = new WebSiteManagementClient(secretsUtils._credentials) { SubscriptionId = secretsUtils._subId };

            DeleteApps("blimp-dotnetcore-plan");
            DeleteApps("blimp-node-plan");
            DeleteApps("blimp-php-plan");
            DeleteApps("blimp-python-plan");
            DeleteApps("blimp-ruby-plan");
        }

        private static void DeleteApps(String planName)
        {
            AppServicePlan appServicePlan = webClient.AppServicePlans.Get(resourceGroupName, planName);
            List<Site> apps = webClient.WebApps.ListByResourceGroup(resourceGroupName) //Get all of the apps in the resource group
                                                   .Where(x => x.ServerFarmId.ToLower() == appServicePlan.Id.ToLower()) //Get all of the apps in the given app service plan
                                                   .Where(x => !SaveApp(x.Name))
                                                   .ToList();
            foreach (Site site in apps)
            {
                pipelineUtils.DeleteWebapp(site.Name, planName);
            }
        }

        private static bool SaveApp(String name)
        {
            return (name.EndsWith("dev") || name.EndsWith("master") || name.EndsWith("save"));
        }
    }
}
