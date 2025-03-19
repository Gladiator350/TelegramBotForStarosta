using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Polly;
using Microsoft.Extensions.Caching.Memory;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot.Types.ReplyMarkups;

// Конфигурация
var botToken = "8107055966:AAEyU-mnIvNK-J2hDxQJ3bno1z5PAiHCf7Q";
var apiUrl = "https://telegram-bot-starosta-backend.onrender.com/api/v1/schedule";
var adminWhitelist = new List<long> { 1563759837, 960762871 };
var userChatIds = new HashSet<long>();
var httpClient = new HttpClient();
var builder = WebApplication.CreateBuilder(args);

// Регистрируем зависимости
builder.Services.AddSingleton<ITelegramBotClient>(new TelegramBotClient(botToken));
builder.Services.AddHttpClient(); // Регистрируем HttpClient
builder.Services.AddMemoryCache(); // Регистрируем MemoryCache

// Регистрируем фоновый сервис
builder.Services.AddHostedService<BotBackgroundService>();

var app = builder.Build();

// Настройка маршрутов для HTTP-запросов
app.MapGet("/", async context =>
{
    await context.Response.WriteAsync("Бот запущен и работает!");
});

app.Run();


public class ScheduleItem
{
    [JsonPropertyName("subjectName")]
    public string SubjectName { get; set; } = null!;
    
    [JsonPropertyName("startTime")]
    public string StartTime { get; set; } = null!;
    
    [JsonPropertyName("endTime")]
    public string EndTime { get; set; } = null!;
    
    [JsonPropertyName("classroom")]
    public string Classroom { get; set; } = null!;
    
    [JsonPropertyName("teacherName")]
    public string TeacherName { get; set; } = null!;
    
    [JsonPropertyName("lessonType")]
    public string LessonType { get; set; } = null!;
    
    [JsonPropertyName("lessonDate")]
    public string LessonDate { get; set; } = null!;
}
