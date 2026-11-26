# Linux image from Microsoft with .NET and the Playwright browsers pre-installed.
# The tag must match the Microsoft.Playwright package version in HotstarScraper.csproj.
FROM mcr.microsoft.com/playwright/dotnet:v1.60.0-noble AS build
WORKDIR /src
COPY HotstarScraper.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app --no-restore

FROM mcr.microsoft.com/playwright/dotnet:v1.60.0-noble AS final
WORKDIR /app
COPY --from=build /app .

# Results are written to /app/output - mount a folder there to keep them:
#   docker run --rm -v "$PWD/output:/app/output" hotstar-scraper
ENTRYPOINT ["dotnet", "HotstarScraper.dll"]
