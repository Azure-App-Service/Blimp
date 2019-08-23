using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using Newtonsoft.Json;
using LibGit2Sharp;
using System.IO;
using LibGit2Sharp.Handlers;
using Microsoft.Azure.Management.Fluent;
using Microsoft.Rest.Azure;
using Microsoft.Azure.Management.ResourceManager.Fluent.Authentication;
using Microsoft.Azure.Management.ResourceManager;
using Microsoft.Azure.Management.Storage;
using Microsoft.Azure.Management.ContainerRegistry;
using Microsoft.Azure.Management.ContainerRegistry.Models;
using Microsoft.Azure.Management.WebSites;
using Microsoft.Azure.Management.WebSites.Models;
using Microsoft.Azure.KeyVault;
using System.Xml.Linq;
using Microsoft.Azure.KeyVault.Models;
using Microsoft.IdentityModel.Clients.ActiveDirectory;
using SendGrid;
using SendGrid.Helpers.Mail;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using RestSharp;
using NameValuePair = Microsoft.Azure.Management.WebSites.Models.NameValuePair;

namespace blimp
{
    public class PipelineUtils
    {
        public ILogger _log { get; set; }
        private ContainerRegistryManagementClient _registryClient;
        private WebSiteManagementClient _webappClient;
        private String _subscriptionID;

        private String _rgName = "blimpRG";
        private String _acrName = "blimpacr";

        public PipelineUtils(ContainerRegistryManagementClient registryClient, WebSiteManagementClient webappClient, String subscriptionID)
        {
            _registryClient = registryClient;
            _registryClient.SubscriptionId = subscriptionID;
            _webappClient = webappClient;
            _webappClient.SubscriptionId = subscriptionID;
        }

        public String CreateTask(String taskName, String gitPath, String branchName, String repoName, String gitToken, String imageName, String authToken, Boolean useCache = false)
        {
            //_log.Info("creating task: " + taskName);

            RestClient client = new RestClient("https://dev.azure.com/patle/50cbdf79-e1ae-48d8-bb9c-8cc4a4922436/_apis/build/builds?api-version=5.0");
            var request = new RestRequest(Method.POST);
            request.AddHeader("cache-control", "no-cache");
            String token = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(String.Format("{0}:{1}", "patle", authToken)));
            request.AddHeader("Authorization", String.Format("Basic {0}", token));
            request.AddHeader("Content-Type", "application/json");
            String body =
                $@"{{
                    ""definition"": {{
                        ""id"": 19
                    }},
                    ""project"": {{
                        ""id"": ""50cbdf79-e1ae-48d8-bb9c-8cc4a4922436""
                    }},
                    ""sourceBranch"": ""master"",
                    ""sourceVersion"": """",
                    ""reason"": ""manual"",
                    ""demands"": [],
                    ""parameters"": ""{{
                        \""gitURL\"":\""{gitPath}\"",
                        \""branchName\"":\""{branchName}\"",
                        \""repoName\"":\""{repoName}\"",
                        \""imageTag\"":\""{imageName}\"",
                        \""useCache\"": \""{useCache.ToString()}\""
                    }}""
                }}";
            request.AddParameter("undefined", body, ParameterType.RequestBody);
            IRestResponse response = client.Execute(request);

            var json = JsonConvert.DeserializeObject<dynamic>(response.Content.ToString());
            String runId = json.id;
            runId = runId.Replace("\"", "");

            client = new RestClient($"https://dev.azure.com/patle/50cbdf79-e1ae-48d8-bb9c-8cc4a4922436/_apis/build/builds/{runId}?api-version=5.0");
            request = new RestRequest(Method.GET);
            request.AddHeader("Authorization", String.Format("Basic {0}", token));

            while (true)
            {
                //_log.Info("run status : " + run.Status);
                response = client.Execute(request);
                json = JsonConvert.DeserializeObject<dynamic>(response.Content.ToString());
                var status = json.status;
                var result = json.result;
                if (status.ToString().ToLower().Equals("completed"))
                {
                    if (result.ToString().ToLower().Equals("succeeded"))
                    {
                        break;
                    }
                    throw new Exception(
                        $"Create image run failed, runid: {runId}, \n" +
                        $"url: https://dev.azure.com/patle/blimp/_build/results?buildId={runId}, \n" +
                        $"output: {result}");
                }
                System.Threading.Thread.Sleep(10 * 1000);  // 10 sec
            }

            return runId;
        }

        public string CreateWebapp(String version, String acrPassword, String appName, String imageName, String planName)
        {
            int tries = 0;
            while (true)
            {
                try
                {
                    //_log.Info("creating webapp");

                    _webappClient.WebApps.Delete(_rgName, appName, false, false);
                    AppServicePlan plan = _webappClient.AppServicePlans.Get(_rgName, planName);

                    //_log.Info("creating site :" + appName);
                    _webappClient.WebApps.CreateOrUpdate(_rgName, appName,
                        new Site
                        {
                            Location = "westus2",
                            ServerFarmId = plan.Id,
                            SiteConfig = new SiteConfig
                            {
                                LinuxFxVersion = String.Format("DOCKER|{0}.azurecr.io/{1}", _acrName, imageName),
                                AppSettings = new List<NameValuePair>
                                {
                                    new NameValuePair("DOCKER_REGISTRY_SERVER_USERNAME", _acrName),
                                    new NameValuePair("DOCKER_REGISTRY_SERVER_PASSWORD", acrPassword),
                                    new NameValuePair("DOCKER_REGISTRY_SERVER_URL", string.Format("https://{0}.azurecr.io", _acrName)),
                                    new NameValuePair("DOCKER_ENABLE_CI", "false"),
                                    new NameValuePair("WEBSITES_ENABLE_APP_SERVICE_STORAGE", "false")
                                }
                            }
                        });
                    return "";
                }
                catch (Exception e)
                {
                    if (tries >= 3)
                    {
                        throw e;
                    }
                    System.Threading.Thread.Sleep(60 * 1000);
                    tries = tries + 1;
                }
            }
        }

        public string CreateWebappGitDeploy(String version, String acrPassword, String appName,
            String imageName, String planName, String targetRepo, GitHubUtils gitHubUtils)
        {
            int tries = 0;
            while (true)
            {
                try
                {
                    //_log.Info("creating webapp");

                    _webappClient.WebApps.Delete(_rgName, appName, false, false);
                    AppServicePlan plan = _webappClient.AppServicePlans.Get(_rgName, planName);

                    //_log.Info("creating site :" + appName);
                    _webappClient.WebApps.CreateOrUpdate(_rgName, appName,
                        new Site
                        {
                            Location = "westus2",
                            ServerFarmId = plan.Id,
                            SiteConfig = new SiteConfig
                            {
                                LinuxFxVersion = String.Format("DOCKER|{0}.azurecr.io/{1}", _acrName, imageName),
                                AppSettings = new List<NameValuePair>
                                {
                                    //new NameValuePair("DOCKER_REGISTRY_SERVER_USERNAME", _acrName),
                                    new NameValuePair("DOCKER_REGISTRY_SERVER_PASSWORD", acrPassword),
                                    new NameValuePair("DOCKER_REGISTRY_SERVER_URL", string.Format("https://{0}.azurecr.io", _acrName)),
                                    new NameValuePair("DOCKER_ENABLE_CI", "false"),
                                    new NameValuePair("WEBSITES_ENABLE_APP_SERVICE_STORAGE", "false")
                                }
                            }
                        });

                    //get publishing profile
                    var publishingProfile = _webappClient.WebApps.ListPublishingCredentials(_rgName, appName);

                    //clone app repo
                    String timeStamp = DateTime.Now.ToString("yyyyMMddHHmmss");
                    String random = new Random().Next(0, 9999).ToString();
                    String path = String.Format("D:\\local\\Temp\\blimp{0}{1}", timeStamp, random);
                    gitHubUtils.Clone(targetRepo, path, "master");

                    //push repo
                    gitHubUtils.Push(path, "master", publishingProfile.ScmUri + "/" + appName + ".git");
                    gitHubUtils.Delete(path);
                    return "";
                }
                catch (Exception e)
                {
                    if (tries >= 3)
                    {
                        throw e;
                    }
                    System.Threading.Thread.Sleep(60 * 1000);
                    tries = tries + 1;
                }
            }
        }

        public string DeleteWebapp(String appName, String planName)
        {
            int tries = 0;
            while (true)
            {
                try
                {
                    //_log.Info("deleting webapp");

                    _webappClient.WebApps.Delete(_rgName, appName, false, false);
                    return "";
                }
                catch (Exception e)
                {
                    if (tries >= 3)
                    {
                        throw e;
                    }
                    System.Threading.Thread.Sleep(60 * 1000);
                    tries = tries + 1;
                }
            }
        }

        public String DeleteImage(String acr, String repo, String tag, String username, String password)
        {
            int tries = 0;
            while (true)
            {
                try
                {
                    String path = String.Format("https://{0}.azurecr.io/acr/v1/{1}/_tags/{2}", acr, repo, tag);
                    var client = new RestClient(path);
                    var request = new RestRequest(Method.DELETE);
                    request.AddHeader("cache-control", "no-cache");
                    String token = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(String.Format("{0}:{1}", username, password)));
                    request.AddHeader("Authorization", String.Format("Basic {0}", token));
                    IRestResponse response = client.Execute(request);
                    return "";
                }
                catch (Exception e)
                {
                    if (tries >= 3)
                    {
                        throw e;
                    }
                    System.Threading.Thread.Sleep(60 * 1000);
                    tries = tries + 1;
                }
            }
        }

        public List<String> ListImages(String acr, String repo, String username, String password)
        {
            try
            {
                var client = new RestClient($"https://{acr}.azurecr.io/v2/{repo}/tags/list");
                var request = new RestRequest(Method.GET);
                request.AddHeader("cache-control", "no-cache");
                request.AddHeader("Connection", "keep-alive");
                request.AddHeader("Accept-Encoding", "gzip, deflate");
                request.AddHeader("Host", $"{acr}.azurecr.io");
                request.AddHeader("Cache-Control", "no-cache");
                request.AddHeader("Accept", "*/*");
                String token = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(String.Format("{0}:{1}", username, password)));
                request.AddHeader("Authorization", String.Format("Basic {0}", token));
                IRestResponse response = client.Execute(request);
                var json = JsonConvert.DeserializeObject<dynamic>(response.Content.ToString());
                return json.tags.ToObject<List<String>>();
            }
            catch (Exception e)
            {
                return new List<String>();
            }
        }
    }
}
