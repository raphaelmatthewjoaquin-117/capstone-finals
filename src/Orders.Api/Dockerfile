FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
WORKDIR /src
COPY src/ src/
COPY contracts/ contracts/
RUN dotnet publish src/Orders.Api/Orders.Api.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:11.0 AS runtime
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
HEALTHCHECK --interval=10s --timeout=3s --retries=10 CMD curl --fail http://localhost:8080/health || exit 1
ENTRYPOINT ["dotnet", "Orders.Api.dll"]
