
using System;
using System.Net.Http;
using System.IO;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SendGrid.Helpers.Mail;

/* 
 * AzureFunction-ApplicationInsights-AutomatedReport
 * SendAppInsightsSummary
 * April 27, 2019, Toni Pohl, atwork.at
 * Demo based on Azure Functions v1.0 Sample, adapted for v2.0 with someminor adaptions.
 * Generates a summarized report sent as email with availability and telemetry data of an App with Azure Function v2 and App Insights
 * App Insights is queried with Kusto query language, see https://docs.microsoft.com/en-us/azure/kusto/query/
 */
namespace SendAppInsightsSummary
{
    public static class AIReport
    {
        private const string AppInsightsApi = "https://api.applicationinsights.io/v1/apps";
        private static readonly string AiAppId = Environment.GetEnvironmentVariable("AI_APP_ID");
        private static readonly string AiAppKey = Environment.GetEnvironmentVariable("AI_APP_KEY");
        private static readonly string SendGridAPI = Environment.GetEnvironmentVariable("AzureWebJobsSendGridApiKey");


        [FunctionName("RunIt")]
        public static async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)]HttpRequest req, ILogger log)
        {
            string name = req.Query["name"];
            if (name == "") { name = "RBI"; }

            DigestResult result = await GetDigestResult(GetQueryString(name), log: log);
            var message = await SendEMail(result, name, log: log);

            return (ActionResult)new OkObjectResult($"Running, {result}");
        }


        private static async Task<DigestResult> GetDigestResult(string query, ILogger log)
        {
            DigestResult result = new DigestResult();

            // generate request ID to allow issue tracking
            string requestId = Guid.NewGuid().ToString();

            try
            {
                using (var httpClient = new HttpClient())
                {
                    httpClient.DefaultRequestHeaders.Add("x-api-key", AiAppKey);
                    httpClient.DefaultRequestHeaders.Add("x-ms-app", "FunctionTemplate");
                    httpClient.DefaultRequestHeaders.Add("x-ms-client-request-id", requestId);

                    string apiPath = $"{AppInsightsApi}/{AiAppId}/query?clientId={requestId}&timespan=P1W&query={query}";

                    using (var httpResponse = await httpClient.GetAsync(apiPath))
                    {
                        // throw exception when unable to determine the metric value
                        httpResponse.EnsureSuccessStatusCode();
                        var resultJson = await httpResponse.Content.ReadAsAsync<JToken>();

                        result = new DigestResult
                        {
                            TotalRequests = resultJson.SelectToken("tables[0].rows[0][0]")?.ToObject<long>().ToString("N0"),
                            FailedRequests = resultJson.SelectToken("tables[0].rows[0][1]")?.ToObject<long>().ToString("N0"),
                            RequestsDuration = resultJson.SelectToken("tables[0].rows[0][2]")?.ToString(),
                            TotalDependencies = resultJson.SelectToken("tables[0].rows[0][3]")?.ToObject<long>().ToString("N0"),
                            FailedDependencies = resultJson.SelectToken("tables[0].rows[0][4]")?.ToObject<long>().ToString("N0"),
                            DependenciesDuration = resultJson.SelectToken("tables[0].rows[0][5]")?.ToString(),
                            TotalViews = resultJson.SelectToken("tables[0].rows[0][6]")?.ToObject<long>().ToString("N0"),
                            TotalExceptions = resultJson.SelectToken("tables[0].rows[0][7]")?.ToObject<long>().ToString("N0"),
                            OverallAvailability = resultJson.SelectToken("tables[0].rows[0][8]")?.ToString(),
                            AvailabilityDuration = resultJson.SelectToken("tables[0].rows[0][9]")?.ToString()
                        };
                        return result;
                    }
                }
            }
            catch (Exception ex)
            {
                //log.Error($"[Error]: Client Request ID {requestId}: {ex.Message}");
                // optional - throw to fail the function
                return result;
            }
        }


        private static async Task<SendGrid.Helpers.Mail.SendGridMessage> SendEMail(DigestResult result, string name, ILogger log)
        {
            // https://www.jankowskimichal.pl/en/2018/11/sending-emails-from-azure-functions-v2-sendgrid/
            // https://sendgrid.com/docs/for-developers/sending-email/v2-csharp-code-example/#using-sendgrids-c-library
            // Microsoft.Azure.WebJobs.Extensions.SendGrid
            var today = DateTime.Today.ToShortDateString();

            var message = new SendGridMessage
            {
                Subject = $"Msg ({today})!",
                From = MailHelper.StringToEmailAddress("<SOME_SENDER_EMAIL>")
            };
            message.AddTo("<YOUR_EMAIL>");

            var body = GetHtmlContentValue(name, today, result);
            message.AddContent("text/html", body);

            var transportWeb = new SendGrid.SendGridClient(SendGridAPI);
            await transportWeb.SendEmailAsync(message);

            return message;
        }

        private struct DigestResult
        {
            public string TotalRequests;
            public string FailedRequests;
            public string RequestsDuration;
            public string TotalDependencies;
            public string FailedDependencies;
            public string DependenciesDuration;
            public string TotalViews;
            public string TotalExceptions;
            public string OverallAvailability;
            public string AvailabilityDuration;
        }


        private static string GetQueryString(string name)
        {
            // update the query accordingly for your need (be sure to run it against Application Insights Analytics portal first for validation)
            // [Application Insights Analytics] https://docs.microsoft.com/en-us/azure/application-insights/app-insights-analytics
            return $@"
requests
| where timestamp > ago(1d)
| summarize Row = 1, TotalRequests = sum(itemCount), FailedRequests = sum(toint(success == 'False')),
    RequestsDuration = iff(isnan(avg(duration)), '------', tostring(toint(avg(duration) * 100) / 100.0))
| join (
dependencies
| where timestamp > ago(1d)
| summarize Row = 1, TotalDependencies = sum(itemCount), FailedDependencies = sum(success == 'False'),
    DependenciesDuration = iff(isnan(avg(duration)), '------', tostring(toint(avg(duration) * 100) / 100.0))
) on Row | join (
pageViews
| where timestamp > ago(1d)
| summarize Row = 1, TotalViews = sum(itemCount)
) on Row | join (
exceptions
| where timestamp > ago(1d)
| summarize Row = 1, TotalExceptions = sum(itemCount)
) on Row | join (
availabilityResults
| where timestamp > ago(1d)
| where name startswith '{name}'
| summarize Row = 1, OverallAvailability = iff(isnan(avg(toint(success))), '------', tostring(toint(avg(toint(success)) * 10000) / 100.0)),
    AvailabilityDuration = iff(isnan(avg(duration)), '------', tostring(toint(avg(duration) * 100) / 100.0))
) on Row
| project TotalRequests, FailedRequests, RequestsDuration, TotalDependencies, FailedDependencies, DependenciesDuration, TotalViews, TotalExceptions, OverallAvailability, AvailabilityDuration";
        }

        private static string GetHtmlContentValue(string appName, string today, DigestResult result)
        {
            // update the HTML template accordingly for your need
            return $@"
<html><body>
<p style='text-align: center;'><strong>{appName} daily telemetry report {today}</strong></p>
<p style='text-align: center;'>The following data shows insights based on telemetry from last 24 hours.</p>
<table align='center' style='width: 95%; max-width: 480px;'><tbody>
<tr>
<td style='min-width: 150px; text-align: left;'><strong>Total requests</strong></td>
<td style='min-width: 100px; text-align: right;'><strong>{result.TotalRequests}</strong></td>
</tr>
<tr>
<td style='min-width: 120px; padding-left: 5%; text-align: left;'>Failed requests</td>
<td style='min-width: 100px; text-align: right;'>{result.FailedRequests}</td>
</tr>
<tr>
<td style='min-width: 120px; padding-left: 5%; text-align: left;'>Average response time</td>
<td style='min-width: 100px; text-align: right;'>{result.RequestsDuration} ms</td>
</tr>
<tr>
<td colspan='2'><hr /></td>
</tr>
<tr>
<td style='min-width: 150px; text-align: left;'><strong>Total dependencies</strong></td>
<td style='min-width: 100px; text-align: right;'><strong>{result.TotalDependencies}</strong></td>
</tr>
<tr>
<td style='min-width: 120px; padding-left: 5%; text-align: left;'>Failed dependencies</td>
<td style='min-width: 100px; text-align: right;'>{result.FailedDependencies}</td>
</tr>
<tr>
<td style='min-width: 120px; padding-left: 5%; text-align: left;'>Average response time</td>
<td style='min-width: 100px; text-align: right;'>{result.DependenciesDuration} ms</td>
</tr>
<tr>
<td colspan='2'><hr /></td>
</tr>
<tr>
<td style='min-width: 150px; text-align: left;'><strong>Total views</strong></td>
<td style='min-width: 100px; text-align: right;'><strong>{result.TotalViews}</strong></td>
</tr>
<tr>
<td style='min-width: 150px; text-align: left;'><strong>Total exceptions</strong></td>
<td style='min-width: 100px; text-align: right;'><strong>{result.TotalExceptions}</strong></td>
</tr>
<tr>
<td colspan='2'><hr /></td>
</tr>
<tr>
<td style='min-width: 150px; text-align: left;'><strong>Overall Availability</strong></td>
<td style='min-width: 100px; text-align: right;'><strong>{result.OverallAvailability} %</strong></td>
</tr>
<tr>
<td style='min-width: 120px; padding-left: 5%; text-align: left;'>Average response time</td>
<td style='min-width: 100px; text-align: right;'>{result.AvailabilityDuration} ms</td>
</tr>
</tbody></table>
</body></html>
";
        }

    }
}
