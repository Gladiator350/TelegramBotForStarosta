# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# 1. Копируем файлы проекта
COPY ["TelegramBotStarosta/TelegramBotStarosta.csproj", "TelegramBotStarosta/"]
COPY ["TelegramBotStarosta.sln", "."]

# 2. Восстанавливаем зависимости
RUN dotnet restore "TelegramBotStarosta.sln"

# 3. Копируем весь код
COPY . .

# 4. Собираем релиз
RUN dotnet publish -c Release -o /app/publish

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "TelegramBotStarosta.dll"]