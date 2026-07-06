using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using System.Net.Http;
using System.Text.Json;
using System.Text;
using System.Net.Http.Headers;

namespace XBPPA.EndpointMirror
{
    public class ProxyFunction
    {
        private readonly HttpClient _httpClient;

        // Headers related to content
        private static readonly HashSet<string> contentHeaderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Content-Length", "Content-Type", "Content-Disposition", "Content-Encoding", 
            "Content-Language", "Content-Location", "Content-MD5", "Content-Range"
        };

        // Headers to skip when proxying
        private static readonly HashSet<string> skipHeaderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Host", "Transfer-Encoding", "Connection", "Keep-Alive",
            "Accept-Encoding", "Cookie", "User-Agent",
            "X-Original-Host", "X-Original-For", "x-ms-invocation-id"
        };

        public ProxyFunction(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        [Function("Proxy")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Function, "get", "post", "put", "delete", "patch", Route = "{*path}")] HttpRequestData req,
            string path,
            FunctionContext context)
        {
            var log = context.GetLogger("Proxy");

            // Correlation/request ID for tracing
            var requestId = req.Headers.TryGetValues("x-request-id", out var reqIdValues) 
                ? reqIdValues.FirstOrDefault() 
                : Guid.NewGuid().ToString();
            log.LogInformation($"Processing request {requestId} for path: {path}");

            var configJson = Environment.GetEnvironmentVariable("MIRROR_CONFIG_JSON");
            if (string.IsNullOrWhiteSpace(configJson))
            {
                var badResponse = req.CreateResponse(System.Net.HttpStatusCode.BadRequest);
                await badResponse.WriteStringAsync("Missing MIRROR_CONFIG_JSON");
                return badResponse;
            }

            var config = JsonSerializer.Deserialize<MirrorConfig>(configJson);

            path = path ?? "";
            var targetUri = new Uri($"{config?.Default.TrimEnd('/')}/{path}{req.Url.Query}");
            log.LogInformation($"Proxying request to: {targetUri}");
            
            var proxyRequest = new HttpRequestMessage
            {
                Method = new HttpMethod(req.Method),
                RequestUri = targetUri,
                Version = new Version(1, 1)
            };

            // Read request body if present
            byte[] bodyContent = null;
            if (req.Body != null)
            {
                using var memoryStream = new MemoryStream();
                await req.Body.CopyToAsync(memoryStream);
                bodyContent = memoryStream.ToArray();
                log.LogInformation($"Request body length: {bodyContent.Length} bytes");
            }

            // Set timeout for the main proxy request
            var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

            // Copy headers except skipped/content headers
            foreach (var header in req.Headers)
            {
                if (!contentHeaderNames.Contains(header.Key) && !skipHeaderNames.Contains(header.Key))
                {
                    proxyRequest.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
                }
            }

            // Forwarded headers
            string clientIp = req.Headers.TryGetValues("X-Forwarded-For", out var forwardedFor) 
                ? forwardedFor.First()
                : GetClientIpAddress(req);

            if (!string.IsNullOrEmpty(clientIp))
            {
                proxyRequest.Headers.TryAddWithoutValidation("X-Forwarded-For", clientIp);
            }

            string scheme = req.Url.Scheme;
            string host = req.Headers.TryGetValues("Host", out var hostValues) ? hostValues.First() : req.Url.Host;

            proxyRequest.Headers.TryAddWithoutValidation("X-Forwarded-Proto", scheme);
            proxyRequest.Headers.TryAddWithoutValidation("X-Forwarded-Host", host);
            proxyRequest.Headers.TryAddWithoutValidation("X-Request-ID", requestId);

            // Add content if appropriate
            if (bodyContent != null && bodyContent.Length > 0 && req.Method.ToUpperInvariant() != "GET")
            {
                var requestContent = new ByteArrayContent(bodyContent);

                if (req.Headers.TryGetValues("Content-Type", out var contentTypeValues))
                {
                    var contentType = contentTypeValues.First();
                    requestContent.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
                    log.LogInformation($"Setting content type: {contentType}");
                }

                foreach (var header in req.Headers)
                {
                    if (contentHeaderNames.Contains(header.Key) && header.Key != "Content-Type")
                    {
                        requestContent.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
                    }
                }

                proxyRequest.Content = requestContent;
            }

            HttpResponseMessage response;
            try
            {
                log.LogInformation($"Sending {req.Method} request to {targetUri}");
                log.LogInformation($"Outgoing Proxy Request Method: {proxyRequest.Method}");
                log.LogInformation($"Outgoing Proxy Request Uri: {proxyRequest.RequestUri}");
                log.LogInformation($"Outgoing Proxy Request Version: {proxyRequest.Version}");
                log.LogInformation("Outgoing Proxy Request Headers:");
                foreach (var header in proxyRequest.Headers)
                {
                    log.LogInformation($"  {header.Key}: {string.Join(", ", header.Value)}");
                }
                if (proxyRequest.Content != null)
                {
                    log.LogInformation("Outgoing Proxy Request Content Headers:");
                    foreach (var header in proxyRequest.Content.Headers)
                    {
                        log.LogInformation($"  {header.Key}: {string.Join(", ", header.Value)}");
                    }
                }

                response = await _httpClient.SendAsync(proxyRequest, cts.Token);
                log.LogInformation($"Received response with status: {response.StatusCode} from {targetUri}");
            }
            catch (Exception ex)
            {
                log.LogError($"Error calling target endpoint: {ex.Message}");
                var errorResponse = req.CreateResponse(System.Net.HttpStatusCode.BadGateway);
                await errorResponse.WriteStringAsync($"Failed to proxy request: {ex.Message}");
                return errorResponse;
            }

            // Fire-and-forget mirroring (do not await)
            if (config?.Mirrors != null)
            {
                foreach (var mirror in config.Mirrors)
                {
                    await MirrorAsync(mirror, path, req, bodyContent ?? Array.Empty<byte>(), clientIp, scheme, host, requestId ?? Guid.NewGuid().ToString(), log);
                }
            }

            var content = await response.Content.ReadAsStringAsync();
            var functionResponse = req.CreateResponse(response.StatusCode);

            // Copy response content-type header
            if (response.Content.Headers.ContentType != null)
            {
                functionResponse.Headers.Add("Content-Type", response.Content.Headers.ContentType.ToString());
            }

            await functionResponse.WriteStringAsync(content);

            cts.Dispose();

            return functionResponse;
        }

        private string GetClientIpAddress(HttpRequestData request)
        {
            // Try to get client IP from common headers
            if (request.Headers.TryGetValues("X-Forwarded-For", out var forwardedFor))
            {
                var ips = forwardedFor.First().Split(',');
                return ips[0].Trim();
            }
            if (request.Headers.TryGetValues("X-Real-IP", out var realIp))
            {
                return realIp.First();
            }
            if (request.Headers.TryGetValues("X-Azure-ClientIP", out var azureClientIp))
            {
                return azureClientIp.First();
            }
            return string.Empty;
        }

        // Mirror request asynchronously, fire-and-forget
        private async Task MirrorAsync(
            string mirror,
            string path,
            HttpRequestData req,
            byte[] bodyContent,
            string clientIp,
            string scheme,
            string host,
            string requestId,
            ILogger log)
        {
            try
            {
                var mirrorUri = new Uri($"{mirror.TrimEnd('/')}/{path}{req.Url.Query}");
                log.LogInformation($"Mirroring to: {mirrorUri}");

                var mirrorRequest = new HttpRequestMessage(new HttpMethod(req.Method), mirrorUri);

                foreach (var header in req.Headers)
                {
                    if (!contentHeaderNames.Contains(header.Key) && !skipHeaderNames.Contains(header.Key))
                    {
                        mirrorRequest.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
                    }
                }

                if (!string.IsNullOrEmpty(clientIp))
                {
                    mirrorRequest.Headers.TryAddWithoutValidation("X-Forwarded-For", clientIp);
                }
                mirrorRequest.Headers.TryAddWithoutValidation("X-Forwarded-Proto", scheme);
                mirrorRequest.Headers.TryAddWithoutValidation("X-Forwarded-Host", host);
                mirrorRequest.Headers.TryAddWithoutValidation("X-Request-ID", requestId);

                if (bodyContent != null && bodyContent.Length > 0 && req.Method.ToUpperInvariant() != "GET")
                {
                    var mirrorContent = new ByteArrayContent(bodyContent);

                    if (req.Headers.TryGetValues("Content-Type", out var ctValues))
                    {
                        var contentType = ctValues.First();
                        mirrorContent.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
                    }

                    mirrorRequest.Content = mirrorContent;
                }

                using var mirrorCts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                try
                {
                    await _httpClient.SendAsync(mirrorRequest, mirrorCts.Token);
                    log.LogInformation($"Mirror to {mirror} completed");
                }
                catch (TaskCanceledException tcex)
                {
                    log.LogWarning($"Mirror to {mirror} timed out: {tcex.Message}");
                }
                catch (Exception ex)
                {
                    log.LogError($"Mirror to {mirror} failed: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                log.LogError($"Mirror to {mirror} failed: {ex.Message}");
            }
        }
    }
}

