# Build stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:10000
EXPOSE 10000
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "TelegramBotStarosta.dll"]

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