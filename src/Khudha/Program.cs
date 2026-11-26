using System.Text.Encodings.Web;
using System.Text.Unicode;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;

var builder = WebApplication.CreateBuilder(args);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Encoder = JavaScriptEncoder.Create(UnicodeRanges.All));
builder.Services.AddDataProtection();
builder.Services.AddSingleton<KhudhaAi>();
builder.Services.AddSingleton<KhudhaFlow>();
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 65536);

var app = builder.Build();
app.Use(async (context, next) =>
{
    try { await next(context); }
    catch (FlowFault error)
    {
        context.Response.StatusCode = error.Status;
        await context.Response.WriteAsJsonAsync(new { error = error.Message, details = error.Details });
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    catch (SqlException error)
    {
        app.Logger.LogError(error, "SQL request failed");
        context.Response.StatusCode = 503;
        await context.Response.WriteAsJsonAsync(new { error = "تعذر الاتصال بقاعدة البيانات. أعيدي المحاولة." });
    }
    catch (Exception error)
    {
        app.Logger.LogError(error, "Request failed");
        context.Response.StatusCode = 500;
        await context.Response.WriteAsJsonAsync(new { error = "تعذر إكمال الطلب. أعيدي المحاولة؛ والتفاصيل في تيرمنال تشغيل الموقع." });
    }
});

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/api/fields", async (HttpContext h, KhudhaFlow flow) =>
    Results.Ok(await flow.FieldsAsync(h.RequestAborted)));
app.MapGet("/api/session", async (HttpContext h, KhudhaFlow flow) =>
    Results.Ok(await flow.SnapshotAsync(h)));
app.MapPost("/api/session", async (StartInput input, HttpContext h, KhudhaFlow flow) =>
    Results.Ok(await flow.StartAsync(input, h)));
app.MapPost("/api/tasks", async (HttpContext h, KhudhaFlow flow) =>
    Results.Ok(await flow.NewTaskAsync(h)));
app.MapPost("/api/tasks/{taskId:guid}/attempts", async
    (Guid taskId, AttemptInput input, HttpContext h, KhudhaFlow flow) =>
    Results.Ok(await flow.ReviewAsync(taskId, input, h)));
app.MapPost("/api/tasks/{taskId:guid}/hint", async
    (Guid taskId, HintInput input, HttpContext h, KhudhaFlow flow) =>
    Results.Ok(await flow.HintAsync(taskId, input, h)));

// Keep the connection probe that has already been verified locally.
app.MapGeminiEndpoints();
app.Run();
