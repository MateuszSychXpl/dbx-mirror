# XBPPA Endpoint Mirror - Azure Functions

.NET 8 Azure Functions application for endpoint mirroring and request forwarding.

## Prerequisites

- .NET 8 SDK
- Azure Functions Core Tools
- Visual Studio 2022 or VS Code with Azure Functions extension

## Project Structure

- `src/` - Source code
  - `Program.cs` - Function app startup and configuration
  - `ProxyFunction.cs` - HTTP trigger functions for request proxying
  - `MirrorConfig.cs` - Configuration models
- `host.json` - Azure Functions host configuration
- `azure-pipelines.yml` - Azure DevOps CI/CD pipeline
- `PIPELINE_SETUP.md` - Pipeline configuration guide

## Local Development

1. Copy `local.settings.json.example` to `local.settings.json` (if exists)
2. Configure your settings
3. Run the function app:
   ```bash
   func start
   ```

## Building

```bash
dotnet build
```

## Deployment

### Automated Deployment (Recommended)

The project includes an Azure DevOps pipeline that automatically deploys to Azure Function App `fa-ea4d-mirror` when changes are pushed to the `dev` branch.

See [PIPELINE_SETUP.md](./PIPELINE_SETUP.md) for configuration instructions.

### Manual Deployment

Use the build and push script:
```bash
./build-and-push.cmd
```

Or deploy using Azure Functions Core Tools:
```bash
func azure functionapp publish fa-ea4d-mirror
```

## Configuration

The application uses Azure Functions configuration system. Key settings:
- Application Insights for monitoring
- HTTP trigger functions for request handling
- Isolated worker process model for .NET 8