# Build
FROM mcr.microsoft.com/dotnet/sdk:7.0 AS build
WORKDIR /app
COPY ./NGigGossip4Nostr/ .
RUN dotnet restore
RUN dotnet publish -c Release -o out ./GigLNDWalletAPI/GigLNDWalletAPI.csproj

# Run
FROM mcr.microsoft.com/dotnet/aspnet:7.0
RUN apt update && apt install -y gettext
WORKDIR /app
RUN mkdir -p /app/data/
COPY ./docker/entrypoint.sh .
COPY --from=build /app/out .
COPY ./docker/wallet.conf.template /app/data/wallet.conf.template

ENV ListenHost="http://0.0.0.0:80/"
EXPOSE 80
ENTRYPOINT ["./entrypoint.sh", "GigDebugLoggerAPI.dll", "/app/data/giglog.conf"]