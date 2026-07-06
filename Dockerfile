# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY XBPPA-endpoint-mirror.csproj ./
RUN dotnet restore "XBPPA-endpoint-mirror.csproj"

COPY . ./
RUN dotnet publish "XBPPA-endpoint-mirror.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Runtime stage — Azure Functions v4 runtime on .NET 8 (isolated worker)
FROM mcr.microsoft.com/azure-functions/dotnet:4-dotnet8
ENV AzureFunctionsJobHost__Worker__Runtime=dotnet \
    AzureFunctionsJobHost__Worker__RuntimeVersion=8.0 \
    FUNCTIONS_WORKER_RUNTIME=dotnet \
    FUNCTIONS_WORKER_PROCESS_COUNT=4

COPY --from=build /app/publish /home/site/wwwroot

EXPOSE 80