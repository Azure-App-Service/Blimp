using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Linq;
using Newtonsoft.Json;
using System.Text;
using System.Threading.Tasks;
using RestSharp;

namespace BlimpUpdateBaseImageConsole
{
    class Program
    {
        static void Main(string[] args)
        {
            makeRequest("20190701.1");
            while (true)    //sleep until user exits
            {
                System.Threading.Thread.Sleep(5000);
            }
        }

        static async void makeRequest(String newTag)
        {
            String secretKey = File.ReadAllText("../../secret.txt");
            //String url = String.Format("https://blimpfunc.azurewebsites.net/api/HttpUpdateBaseImage_HttpStart?code={0}", secretKey);
            String url = "http://localhost:7071/api/HttpUpdateBaseImage_HttpStart";

            String body = String.Format("{{ \"NewBaseImage\": \"{0}\" }}", newTag);
            var client = new RestClient(url);
            client.Timeout = 1000 * 60; // 1min
            var request = new RestRequest(Method.POST);
            request.AddHeader("cache -control", "no-cache");
            request.AddHeader("Content-Type", "application/json");
            request.AddParameter("undefined", body, ParameterType.RequestBody);
            IRestResponse response = client.Execute(request);
            Console.WriteLine(response.StatusCode.ToString());
            if (!response.StatusCode.ToString().ToLower().Contains("accepted"))
            {
                return;
            }
            //Console.WriteLine(response.Content.ToString());

            var result = JsonConvert.DeserializeObject<dynamic>(response.Content.ToString());
            String statusURL = result.statusQueryGetUri;
            while (true)
            {
                client = new RestClient(statusURL);
                client.Timeout = 1000 * 60; // 1min
                request = new RestRequest(Method.GET);
                request.AddHeader("cache-control", "no-cache");
                request.AddHeader("Content-Type", "application/json");
                request.AddParameter("undefined", body, ParameterType.RequestBody);
                response = client.Execute(request);
                //Console.WriteLine(response.StatusCode.ToString());
                //Console.WriteLine(response.Content.ToString());
                result = JsonConvert.DeserializeObject<dynamic>(response.Content.ToString());
                String status = result.runtimeStatus;
                if (!status.ToLower().Equals("running"))
                {
                    Console.WriteLine("completed");
                    try
                    {
                        if (result.output.ToString().ToLower().Contains("success"))
                        {
                            Console.WriteLine("success");
                        }
                        else
                        {
                            Console.WriteLine("failure");
                            Console.WriteLine(result.output.ToString());
                        }
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine("failure");
                        Console.WriteLine(e.ToString());
                    }
                    break;
                }
                else
                {
                    System.Threading.Thread.Sleep(1 * 60 * 1000); // sleep 1 mins between builds
                }
            }
        }
    }
}
