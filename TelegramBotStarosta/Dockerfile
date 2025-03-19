# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ["TelegramBotStarosta.csproj", "."]
RUN dotnet restore "TelegramBotStarosta.csproj"

COPY . .
RUN dotnet publish -c Release -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:10000 \
    DOTNET_ENVIRONMENT=Production
EXPOSE 10000

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "TelegramBotStarosta.dll"]