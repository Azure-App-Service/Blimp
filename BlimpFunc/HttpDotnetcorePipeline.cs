using Microsoft.ApplicationInsights;
using Microsoft.Azure.Management.ContainerRegistry;
using Microsoft.Azure.Management.WebSites;
using Microsoft.Extensions.Logging;
using SendGrid;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace blimp
{
    public static class HttpDotnetcorePipeline
    {
        private static ILogger _log;
        private static SecretsUtils _secretsUtils;
        private static MailUtils _mailUtils;
        private static DockerhubUtils _dockerhubUtils;
        private static GitHubUtils _githubUtils;
        private static PipelineUtils _pipelineUtils;
        private static StringBuilder _emailLog;
        private static TelemetryClient _telemetry;

        public static async Task<String> Run(BuildRequest br, ILogger log)
        {
            _telemetry = new TelemetryClient();
            _telemetry.TrackEvent("HttpDotnetcorePipeline started");
            await InitUtils(log);

            LogInfo("HttpDotnetcorePipeline request received");

            try
            {
                _mailUtils._buildRequest = br;
                LogInfo($"HttpDotnetcorePipeline executed at: { DateTime.Now }");
                LogInfo(String.Format("new Dotnetcore BuildRequest found {0}", br.ToString()));

                Boolean success = await MakePipeline(br, log);

                await _mailUtils.SendSuccessMail(new List<String> { br.Version }, GetLog());
                String successMsg =
                    $@"{{
                        ""status"": ""success"",
                        ""input"" : {JsonConvert.SerializeObject(br)}
                    }}";
                return successMsg;
            }
            catch (Exception e)
            {
                LogInfo(e.ToString());
                _telemetry.TrackException(e);
                await _mailUtils.SendFailureMail(e.ToString(), GetLog());
                String failureMsg =
                    $@"{{
                        ""status"": ""failure"",
                        ""error"": ""{e.ToString()}"",
                        ""input"" : {JsonConvert.SerializeObject(br)}
                    }}";
                return failureMsg;
            }
            finally
            {
                if (!br.SaveArtifacts)
                {
                    await DeletePipeline(br, log);
                }
            }
        }

        public static void LogInfo(String message)
        {
            _emailLog.Append(message);
            _log.LogInformation(message);
            _telemetry.TrackEvent(message);
        }
        public static String GetLog()
        {
            return _emailLog.ToString();
        }

        public static async System.Threading.Tasks.Task InitUtils(ILogger log)
        {
            _emailLog = new StringBuilder();
            _secretsUtils = new SecretsUtils();
            await _secretsUtils.GetSecrets();
            _mailUtils = new MailUtils(new SendGridClient(_secretsUtils._sendGridApiKey), "Dotnetcore");
            _dockerhubUtils = new DockerhubUtils();
            _githubUtils = new GitHubUtils(_secretsUtils._gitToken);
            _pipelineUtils = new PipelineUtils(
                new ContainerRegistryManagementClient(_secretsUtils._credentials),
                new WebSiteManagementClient(_secretsUtils._credentials),
                _secretsUtils._subId
                );

            _log = log;
            _mailUtils._log = log;
            _dockerhubUtils._log = log;
            _githubUtils._log = log;
            _pipelineUtils._log = log;
        }

        public static async Task<Boolean> DeletePipeline(BuildRequest br, ILogger log)
        {
            // delete github repo
            await _githubUtils.DeleteGithubAsync(br.OutputRepoOrgName, br.OutputRepoName);

            // delete acr image
            /*_pipelineUtils.DeleteImage(
                "blimpacr",
                br.OutputImageName.Split(':')[0],
                br.OutputImageName.Split(':')[1],
                "blimpacr",
                _secretsUtils._acrPassword
                );*/

            // delete webapp
            //_pipelineUtils.DeleteWebapp(br.WebAppName, "blimp-dotnetcore-plan");
            return true;
        }

        public static async Task<Boolean> MakePipeline(BuildRequest br, ILogger log)
        {
            int tries = br.Tries;
            while (true)
            {
                try
                {
                    tries--;
                    _mailUtils._version = br.Version;
                    LogInfo("creating pipeline for Dotnetcore " + br.Version);
                    await PushGithubAsync(br);
                    await CreateDotnetcoreHostingStartPipeline(br);
                    LogInfo(String.Format("Dotnetcore {0} built", br.Version));
                    return true;
                }
                catch (Exception e)
                {
                    LogInfo(e.ToString());
                    if (tries <= 0)
                    {
                        LogInfo(String.Format("Dotnetcore {0} failed", br.Version));
                        throw e;
                    }
                    LogInfo("trying again");
                    System.Threading.Thread.Sleep(1 * 60 * 1000);  //1 min
                }
            }
        }

        public static async Task<Boolean> CreateDotnetcoreHostingStartPipeline(BuildRequest br)
        {
            String dotnetcoreVersionDash = br.Version.Replace(".", "-");
            String taskName = String.Format("blimp-dotnetcore-hostingstart-{0}-task", dotnetcoreVersionDash);
            String planName = "blimp-dotnetcore-plan";

            LogInfo("creating acr task for dotnetcore hostingstart " + br.Version);
            String acrPassword = _pipelineUtils.CreateTask(taskName, br.OutputRepoURL, br.OutputRepoBranchName, br.OutputRepoName,
                _secretsUtils._gitToken, br.OutputImageName, _secretsUtils._pipelineToken, useCache: br.UseCache);
            LogInfo("done creating acr task for dotnetcore hostingstart " + br.Version);

            LogInfo("creating webapp for dotnetcore hostingstart " + br.Version);
            String cdUrl = _pipelineUtils.CreateWebapp(br.Version, _secretsUtils._acrPassword, br.WebAppName, br.OutputImageName, planName);
            LogInfo("done creating webapp for dotnetcore hostingstart " + br.Version);

            return true;
        }

        private static String getZip(String version)
        {
            return version;
        }

        private static async Task<Boolean> PushGithubAsync(BuildRequest br)
        {
            LogInfo("creating github files for dotnetcore " + br.Version);
            String timeStamp = DateTime.Now.ToString("yyyyMMddHHmmss");
            String random = new Random().Next(0, 9999).ToString();
            String parent = String.Format("D:\\local\\Temp\\blimp{0}{1}", timeStamp, random);
            _githubUtils.CreateDir(parent);

            String localTemplateRepoPath = String.Format("{0}\\{1}", parent, br.TemplateRepoName);
            String localOutputRepoPath = String.Format("{0}\\{1}", parent, br.OutputRepoName);

            _githubUtils.Clone(br.TemplateRepoURL, localTemplateRepoPath, br.TemplateRepoBranchName, br.PullRepo, br.PullId);
            _githubUtils.CreateDir(localOutputRepoPath);
            if (await _githubUtils.RepoExistsAsync(br.OutputRepoOrgName, br.OutputRepoName))
            {
                _githubUtils.Clone(
                    br.OutputRepoURL,
                    localOutputRepoPath,
                    br.OutputRepoBranchName);
                _githubUtils.Checkout(localOutputRepoPath, br.OutputRepoBranchName);
            }
            else
            {
                await _githubUtils.InitGithubAsync(br.OutputRepoOrgName, br.OutputRepoName);
                _githubUtils.Init(localOutputRepoPath);
                _githubUtils.AddRemote(localOutputRepoPath, br.OutputRepoOrgName, br.OutputRepoName);
            }
            _githubUtils.Delete(localOutputRepoPath, skipGit: true);
            _githubUtils.DeepCopy(
                String.Format("{0}\\{1}", localTemplateRepoPath, br.TemplateName),
                localOutputRepoPath);
            _githubUtils.DeepCopy(
                String.Format("{0}\\src\\{1}", localTemplateRepoPath, getZip(br.Version)),
                localOutputRepoPath);
            _githubUtils.CopyFile(
                String.Format("{0}\\src\\{1}\\bin.zip", localTemplateRepoPath, getZip(br.Version)),
                String.Format("{0}\\bin.zip", localOutputRepoPath),
                force: true);

            _githubUtils.FillTemplate(
                String.Format("{0}\\DockerFile", localOutputRepoPath),
                new List<String> { String.Format("FROM {0}", br.BaseImageName) },
                new List<int> { 1 }
            );

            _githubUtils.Stage(localOutputRepoPath, "*");
            _githubUtils.CommitAndPush(localOutputRepoPath, br.OutputRepoBranchName, String.Format("[blimp] Add dotnetcore {0}", br.Version));
            _githubUtils.gitDispose(localOutputRepoPath);
            _githubUtils.gitDispose(localTemplateRepoPath);
            _githubUtils.Delete(parent);
            LogInfo("done creating github files for dotnetcore " + br.Version);

            return true;
        }
    }
}
