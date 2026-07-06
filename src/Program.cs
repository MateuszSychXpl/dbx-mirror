using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Azure.Functions.Worker.Builder;
using XBPPA.EndpointMirror;

var builder = FunctionsApplication.CreateBuilder(args);

builder.Services.AddHttpClient<ProxyFunction>()
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AllowAutoRedirect = false, // Don't follow redirects automatically
        AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
        UseCookies = false // Don't manage cookies
    })
    .SetHandlerLifetime(TimeSpan.FromMinutes(5)); // Keep handler alive longer

builder.ConfigureFunctionsWebApplication();

builder.Build().Run();
