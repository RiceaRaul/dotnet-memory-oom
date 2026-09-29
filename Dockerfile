FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/MemoryLab.Api/MemoryLab.Api.csproj src/MemoryLab.Api/
RUN dotnet restore src/MemoryLab.Api/MemoryLab.Api.csproj
COPY src/ src/
RUN dotnet publish src/MemoryLab.Api -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Uat \
    DOTNET_DbgEnableMiniDump=1 \
    DOTNET_DbgMiniDumpType=2 \
    DOTNET_DbgMiniDumpName=/dumps/oom_%p_%t.dmp \
    DOTNET_EnableCrashReport=1 \
    MemoryDiagnostics__DumpDirectory=/dumps
EXPOSE 8080
ENTRYPOINT ["dotnet", "MemoryLab.Api.dll"]
