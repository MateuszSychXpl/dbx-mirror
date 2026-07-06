using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Text.Json;

namespace XBPPA.EndpointMirror
{
    public class ConfigFunction
    {
        [Function("Config")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Function, "get", Route = "config")] HttpRequestData req,
            FunctionContext context)
        {
            var log = context.GetLogger("Config");

            var configJson = Environment.GetEnvironmentVariable("MIRROR_CONFIG_JSON");
            if (string.IsNullOrWhiteSpace(configJson))
            {
                var notFound = req.CreateResponse(HttpStatusCode.BadRequest);
                await notFound.WriteStringAsync("Missing MIRROR_CONFIG_JSON");
                return notFound;
            }

            log.LogInformation("Returning current mirror config");

            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/json; charset=utf-8");

            // Re-serialize to return normalized/pretty JSON
            var config = JsonSerializer.Deserialize<MirrorConfig>(configJson);
            var options = new JsonSerializerOptions { WriteIndented = true };
            await response.WriteStringAsync(JsonSerializer.Serialize(config, options));
            return response;
        }
    }
}