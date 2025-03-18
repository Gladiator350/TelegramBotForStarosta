# Сборка приложения
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Копируем только файл проекта для оптимизации кэша
COPY ["TelegramBotStarosta/TelegramBotStarosta.csproj", "TelegramBotStarosta/"]
RUN dotnet restore "TelegramBotStarosta/TelegramBotStarosta.csproj"

# Копируем остальные файлы
COPY . .

# Публикуем приложение
RUN dotnet publish "TelegramBotStarosta/TelegramBotStarosta.csproj" \
    -c Release \
    -o /app/publish

# Финальный образ
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

# Копируем результат публикации
COPY --from=build /app/publish .

# Запускаем приложение
ENTRYPOINT ["dotnet", "TelegramBotStarosta.dll"]