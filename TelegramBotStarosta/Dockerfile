# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# 1. Копируем только файл проекта
COPY ["TelegramBotStarosta/TelegramBotStarosta.csproj", "TelegramBotStarosta/"]
RUN dotnet restore "TelegramBotStarosta/TelegramBotStarosta.csproj"

# 2. Копируем остальные файлы
COPY . .

# 3. Собираем приложение
RUN dotnet publish "TelegramBotStarosta/TelegramBotStarosta.csproj" -c Release -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "TelegramBotStarosta.dll"]