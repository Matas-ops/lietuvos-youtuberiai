# runtime + browser, also used alone by IDE fast mode
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS base
RUN apk add --no-cache chromium chromium-chromedriver
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENV Selenium__ChromeBinaryPath=/usr/bin/chromium-browser
ENV Selenium__ChromeDriverPath=/usr/bin/chromedriver

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish -c Release -o /app

FROM base AS final
COPY --from=build /app .

# run as the image's built-in non-root user (only here, IDE fast mode uses the base stage with packages under /root)
USER $APP_UID

ENTRYPOINT ["dotnet","YoutubersBackend.dll"]
