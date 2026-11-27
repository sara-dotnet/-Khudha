using System.Text.Encodings.Web;
using System.Text.Unicode;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;

var selfTest = args.Contains("--self-test", StringComparer.Ordinal);
var builder = WebApplication.CreateBuilder(args.Where(arg => arg != "--self-test").ToArray());
if (selfTest)
{
    KhudhaSelfTests.Run(new KhudhaCatalog(builder.Environment));
    return;
}
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Encoder = JavaScriptEncoder.Create(UnicodeRanges.All));
builder.Services.AddDataProtection();
builder.Services.AddSingleton<KhudhaAi>();
builder.Services.AddSingleton<KhudhaCatalog>();
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
app.MapGet("/api/catalog", async (HttpContext h, KhudhaFlow flow) =>
    Results.Ok(await flow.CatalogAsync(h.RequestAborted)));
app.MapGet("/api/fields", async (HttpContext h, KhudhaFlow flow) =>
    Results.Ok(await flow.FieldsAsync(h.RequestAborted)));
app.MapGet("/api/session", async (HttpContext h, KhudhaFlow flow) =>
    Results.Ok(await flow.SnapshotAsync(h)));
app.MapPost("/api/session", async (StartInput input, HttpContext h, KhudhaFlow flow) =>
    Results.Ok(await flow.StartAsync(input, h)));
app.MapPost("/api/session/track", async (TrackInput input, HttpContext h, KhudhaFlow flow) =>
    Results.Ok(await flow.SelectTrackAsync(input, h)));
app.MapPost("/api/session/chat", async (ExploreInput input, HttpContext h, KhudhaFlow flow) =>
    Results.Ok(await flow.ExploreAsync(input, h)));
app.MapPost("/api/tasks", async (TaskStartInput input, HttpContext h, KhudhaFlow flow) =>
    Results.Ok(await flow.NewTaskAsync(input, h)));
app.MapPost("/api/tasks/{taskId:guid}/attempts", async
    (Guid taskId, AttemptInput input, HttpContext h, KhudhaFlow flow) =>
    Results.Ok(await flow.ReviewAsync(taskId, input, h)));
app.MapPost("/api/tasks/{taskId:guid}/hint", async
    (Guid taskId, HintInput input, HttpContext h, KhudhaFlow flow) =>
    Results.Ok(await flow.HintAsync(taskId, input, h)));

// Keep the connection probe that has already been verified locally.
app.MapGeminiEndpoints();
app.Run();
