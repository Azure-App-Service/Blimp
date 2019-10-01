using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net;
using System.Threading.Tasks;
using blimp;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace BlimpFunc
{
    public static class HttpUpdateBaseImage
    {
        [FunctionName("HttpUpdateBaseImage_UpdateStack")]
        public static async Task<string> updateStack([ActivityTrigger] UpdateBaseImageRequest request, ILogger log)
        {
            SecretsUtils _secretsUtils = new SecretsUtils();
            await _secretsUtils.GetSecrets();
            GitHubUtils _githubUtils = new GitHubUtils(_secretsUtils._gitToken);

            String timeStamp = DateTime.Now.ToString("yyyyMMddHHmmss");
            String random = new Random().Next(0, 9999).ToString();
            String prname = String.Format("blimp{0}{1}", timeStamp, random);
            String parent = String.Format("D:\\local\\Temp\\blimp{0}{1}", timeStamp, random);

            String repoUrl = String.Format("https://github.com/Azure-App-Service/{0}-template.git", request.stack);
            String repoName = String.Format("{0}-template", request.stack); 
            _githubUtils.CreateDir(parent);

            String localTemplateRepoPath = String.Format("{0}\\{1}", parent, repoName);

            _githubUtils.Clone(repoUrl, localTemplateRepoPath, "dev");
            _githubUtils.Checkout(localTemplateRepoPath, prname);

            // edit the configs
            await updateConfig(String.Format("{0}\\{1}", localTemplateRepoPath, "blessedImageConfig-dev.json"), request.NewBaseImage);
            await updateConfig(String.Format("{0}\\{1}", localTemplateRepoPath, "blessedImageConfig-master.json"), request.NewBaseImage);
            await updateConfig(String.Format("{0}\\{1}", localTemplateRepoPath, "blessedImageConfig-temp.json"), request.NewBaseImage);
            await updateConfig(String.Format("{0}\\{1}", localTemplateRepoPath, "blessedImageConfig-save.json"), request.NewBaseImage);
            
            _githubUtils.Stage(localTemplateRepoPath, "*");
            _githubUtils.CommitAndPush(localTemplateRepoPath, prname, String.Format("[blimp] new base image {0}", request.NewBaseImage));
            String pullRequestURL = String.Format("https://api.github.com/repos/{0}/{1}-template/pulls?access_token={2}", "azure-app-service", request.stack, _secretsUtils._gitToken);
            String body =
                "{ " +
                    "\"title\": " + JsonConvert.SerializeObject("[blimp] Update Base Image") + ", " +
                    "\"body\": " + JsonConvert.SerializeObject("[blimp] auto generated Update Base Image") + ", " +
                    "\"head\": " + JsonConvert.SerializeObject("azure-app-service:" + prname) + ", " +
                    "\"base\": " + JsonConvert.SerializeObject("dev") +
                "}";
            HttpClient httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("patricklee2");
            HttpResponseMessage response = null;
            response = await httpClient.PostAsync(pullRequestURL, new StringContent(body)); // fails on empty commits

            String result = await response.Content.ReadAsStringAsync();
            System.Console.WriteLine(response.ToString());
            System.Console.WriteLine(result);
            //if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
            //{
            //    System.Console.WriteLine("Unable to make PR due to no differnce");
            //}

            _githubUtils.gitDispose(localTemplateRepoPath);
            _githubUtils.Delete(parent);

            return "";
        }
        public static async Task<string> updateConfig(String path, String newBaseImage)
        {
            string json = File.ReadAllText(path);
            List<BuildRequest> requests = JsonConvert.DeserializeObject<List<BuildRequest>>(json);
            foreach(BuildRequest r in requests)
            {
                r.BaseImageName = r.BaseImageName.Split('-')[0] + "-" + newBaseImage;
            }


            JsonSerializerSettings jsonConfig = new JsonSerializerSettings();
            jsonConfig.NullValueHandling = NullValueHandling.Ignore;

            json = JsonConvert.SerializeObject(requests,Formatting.Indented, jsonConfig);
            File.WriteAllText(path, json);
            return "";
        }


        [FunctionName("HttpUpdateBaseImage")]
        public static async Task<List<string>> RunOrchestrator(
            [OrchestrationTrigger] DurableOrchestrationContext context)
        {
            var outputs = new List<string>();

            String content = context.GetInput<String>();
            UpdateBaseImageRequest request = JsonConvert.DeserializeObject<UpdateBaseImageRequest>(content);

            request.stack = "php";
            outputs.Add(await context.CallActivityAsync<string>("HttpUpdateBaseImage_UpdateStack", request));
            request.stack = "dotnetcore";
            outputs.Add(await context.CallActivityAsync<string>("HttpUpdateBaseImage_UpdateStack", request));
            request.stack = "python";
            outputs.Add(await context.CallActivityAsync<string>("HttpUpdateBaseImage_UpdateStack", request));
            request.stack = "node";
            outputs.Add(await context.CallActivityAsync<string>("HttpUpdateBaseImage_UpdateStack", request));

            return outputs;
        }

        [FunctionName("HttpUpdateBaseImage_HttpStart")]
        public static async Task<HttpResponseMessage> HttpStart(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")]HttpRequestMessage req,
            [OrchestrationClient]DurableOrchestrationClient starter,
            ILogger log)
        {
            String content = await req.Content.ReadAsStringAsync();

            // Function input comes from the request content.
            string instanceId = await starter.StartNewAsync("HttpUpdateBaseImage", content);

            log.LogInformation($"Started orchestration with ID = '{instanceId}'.");

            return starter.CreateCheckStatusResponse(req, instanceId);
        }
    }
}