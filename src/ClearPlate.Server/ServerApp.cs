using ClearPlate.Core;
using System.Text.Json;
using System.Threading.RateLimiting;

namespace ClearPlate.Server;

public static class ServerApp
{
    private sealed record Login(string Password);
    private sealed record Preview(string Token);
    private sealed record Confirmation(string Id, bool Accepted);
    private sealed record Settings(string GroupId, string Location, bool ScheduleEnabled);
    private sealed record Submission(string Slot);
    public static WebApplication Build(string[] args, ServerOptions options, HttpClient? http = null, Func<DateTimeOffset>? clock = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Logging.ClearProviders(); builder.Logging.AddSimpleConsole(); builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(server => { server.Limits.MaxRequestBodySize = 21 * 1024 * 1024; server.AddServerHeader = false; });
        builder.Services.AddSingleton(options); builder.Services.AddSingleton<WebSessions>();
        var timeoutSec = int.TryParse(Environment.GetEnvironmentVariable("CLEARPLATE_HTTP_TIMEOUT"), out var s) && s > 0 ? s : 90;
        builder.Services.AddSingleton(http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(timeoutSec) });
        builder.Services.AddSingleton(services => new ServerController(options, services.GetRequiredService<HttpClient>(), clock));
        builder.Services.AddHostedService<PlanWorker>();
        builder.Services.AddRateLimiter(rates =>
        {
            rates.RejectionStatusCode = 429;
            rates.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
        });
        var app = builder.Build();
        _ = app.Services.GetRequiredService<ServerController>(); // refuse unreadable state before accepting traffic
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["X-Frame-Options"] = "DENY";
            context.Response.Headers["Referrer-Policy"] = "same-origin";
            context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
            try
            {
                if (context.Request.Path.StartsWithSegments("/api") && context.Request.Path != "/api/login")
                {
                    var sessions = context.RequestServices.GetRequiredService<WebSessions>();
                    var session = sessions.Find(context.Request.Cookies[WebSessions.Cookie]);
                    if (session is null) { await Error(context, 401, "请先登录管理页面。"); return; }
                    if (context.Request.Method != "GET" && (!sessions.ValidOrigin(context.Request)
                        || !WebSessions.Equal(context.Request.Headers["X-CSRF-Token"].ToString(), session.Csrf)))
                    { await Error(context, 403, "页面验证已失效，请刷新后重试。"); return; }
                    context.Items["session"] = session;
                }
                await next();
            }
            catch (ServerProblem problem) { await Error(context, problem.Status, problem.Message); }
            catch (JsonException) { await Error(context, 400, "请求格式不正确。"); }
            catch (BadHttpRequestException) { await Error(context, 400, "请求不完整或超过大小限制。"); }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
            catch { await Error(context, 503, "操作未完成，请检查服务状态和持久化目录；没有确认成功的操作请先核对记录。"); }
        });
        app.UseRateLimiter();
        app.UseDefaultFiles(); app.UseStaticFiles();
        app.MapGet("/healthz", () => Results.Json(new { status = "ok" }));
        app.MapPost("/api/login", async (HttpContext context, WebSessions sessions) =>
        {
            if (!sessions.ValidOrigin(context.Request)) return Results.Json(new { error = "来源不匹配。" }, statusCode: 403);
            var body = await Json<Login>(context.Request);
            if (body.Password is null || !sessions.ValidPassword(body.Password)) return Results.Json(new { error = "管理密码不正确。" }, statusCode: 401);
            var cookie = sessions.Create(); context.Response.Cookies.Append(WebSessions.Cookie, cookie, sessions.CookieOptions(context.Request));
            return Results.Json(new { ok = true });
        }).RequireRateLimiting("login");
        app.MapPost("/api/logout", (HttpContext context, WebSessions sessions) =>
        {
            sessions.Remove(context.Request.Cookies[WebSessions.Cookie]);
            context.Response.Cookies.Delete(WebSessions.Cookie, sessions.CookieOptions(context.Request)); return Results.Json(new { ok = true });
        });
        app.MapGet("/api/state", (HttpContext context, ServerController server) => Results.Json(new { state = server.View, csrf = ((WebSession)context.Items["session"]!).Csrf }));
        app.MapPost("/api/check", async (ServerController server) =>
        {
            var result = await server.CheckAsync(); return Results.Json(new { state = server.View, needsUpdate = result == ConnectionResult.NeedsCapture });
        });
        app.MapPost("/api/session/preview", async (HttpRequest request, ServerController server) =>
        {
            var body = await Json<Preview>(request);
            if (body.Token is null) throw new ServerProblem(400, "请填写 token。");
            return Results.Json(await server.PreviewAsync(body.Token));
        });
        app.MapPost("/api/session/confirm", async (HttpRequest request, ServerController server) =>
        {
            var body = await Json<Confirmation>(request); await server.ConfirmAsync(body.Id, body.Accepted); return Results.Json(new { state = server.View });
        });
        app.MapPost("/api/settings", async (HttpRequest request, ServerController server) =>
        {
            var body = await Json<Settings>(request);
            await server.SettingsAsync(body.GroupId ?? "", body.Location ?? "", body.ScheduleEnabled); return Results.Json(new { state = server.View });
        });
        app.MapPost("/api/submit", async (HttpRequest request, ServerController server) =>
        {
            var body = await Json<Submission>(request); await server.SubmitAsync(body.Slot); return Results.Json(new { state = server.View });
        });
        app.MapGet("/api/photo", (ServerController server) => { var value = server.Photo; return Results.Bytes(value.Bytes, value.Mime); });
        app.MapPost("/api/photo", async (HttpRequest request, ServerController server) =>
        {
            var bytes = await Bounded(request, 20 * 1024 * 1024);
            var name = Uri.UnescapeDataString(request.Headers["X-Photo-Name"].ToString());
            await server.PhotoAsync(bytes, name); return Results.Json(new { state = server.View });
        });
        return app;
    }
    private static async Task<T> Json<T>(HttpRequest request)
    {
        if (!request.HasJsonContentType()) throw new ServerProblem(415, "请使用 JSON 请求。");
        return JsonSerializer.Deserialize<T>(await Bounded(request, 8192), new JsonSerializerOptions(JsonSerializerDefaults.Web) { MaxDepth = 8 })
            ?? throw new ServerProblem(400, "请求不能为空。");
    }
    private static async Task<byte[]> Bounded(HttpRequest request, int limit)
    {
        if (request.ContentLength > limit) throw new ServerProblem(413, "内容超过大小限制。");
        using var buffer = new MemoryStream(); var block = new byte[32768];
        while (true)
        {
            var read = await request.Body.ReadAsync(block, request.HttpContext.RequestAborted);
            if (read == 0) return buffer.ToArray();
            if (buffer.Length + read > limit) throw new ServerProblem(413, "内容超过大小限制。");
            buffer.Write(block, 0, read);
        }
    }
    private static async Task Error(HttpContext context, int status, string message)
    {
        if (context.Response.HasStarted) return;
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new { error = message });
    }
}
public sealed class PlanWorker(ServerController server) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        try { while (await timer.WaitForNextTickAsync(stoppingToken)) await server.TickAsync(); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
