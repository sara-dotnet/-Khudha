using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;

public sealed class FlowFault(int status, string message, object? details = null) : Exception(message)
{
    public int Status { get; } = status;
    public object? Details { get; } = details;
}
public sealed record StartInput(string Stage, int FieldId);
public sealed record AttemptInput(Guid RequestId, string Submission);
public sealed record HintInput(string Question, string? Draft);
internal sealed record FieldRow(int Id, string Slug, string Name, string Overview);
internal sealed record SessionRow(Guid Id, string Stage, int FieldId);
internal sealed record TaskRow(Guid Id, Guid SessionId, int Number, int Difficulty, string Json, string Status);
internal sealed record AttemptRow(Guid Id, Guid TaskId, string Submission, string? Review, string Status);
internal sealed record MessageRow(long Id, Guid? TaskId, string Role, string Content);
internal sealed record TemplateRow(int Id, int Difficulty, string Json);

public sealed class KhudhaFlow
{
    private readonly string _connectionString;
    private readonly IDataProtector _protector;
    private readonly KhudhaAi _ai;
    private readonly ILogger<KhudhaFlow> _logger;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();
    private const string CookieName = "khudha.session";
    private static readonly Dictionary<string, string> Stages = new()
    {
        ["high-school"] = "طالب في المرحلة الثانوية",
        ["graduate"] = "خريج ثانوي",
        ["university"] = "طالب جامعي"
    };

    public KhudhaFlow(IConfiguration configuration, IDataProtectionProvider protection,
        KhudhaAi ai, ILogger<KhudhaFlow> logger)
    {
        _connectionString = configuration.GetConnectionString("Khudha")
            ?? throw new InvalidOperationException("Missing ConnectionStrings:Khudha");
        _protector = protection.CreateProtector("Khudha.AnonymousSession.v1");
        _ai = ai; _logger = logger;
    }

    public async Task<object> FieldsAsync(CancellationToken ct) => await ReadAsync(
        "SELECT Id, Slug, Name, Overview FROM dbo.Fields ORDER BY Id;",
        r => new FieldRow(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3)), ct);

    public async Task<object> SnapshotAsync(HttpContext h)
    {
        var id = ReadCookie(h);
        return id.HasValue ? await SnapshotByIdAsync(id.Value, h.RequestAborted) : new { session = (object?)null };
    }

    public async Task<object> StartAsync(StartInput input, HttpContext h)
    {
        if (input.Stage is null || !Stages.ContainsKey(input.Stage))
            throw new FlowFault(400, "اختاري مرحلتك الدراسية أولاً.");
        await FieldAsync(input.FieldId, h.RequestAborted);
        var id = Guid.NewGuid();
        await ExecuteAsync("INSERT dbo.Sessions(Id, StudentStage, FieldId) VALUES(@id, @stage, @field);",
            h.RequestAborted, ("@id", id), ("@stage", input.Stage), ("@field", input.FieldId));
        h.Response.Cookies.Append(CookieName, _protector.Protect(id.ToString("D")), new CookieOptions
        {
            HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = h.Request.IsHttps,
            MaxAge = TimeSpan.FromDays(7), Path = "/"
        });
        return await SnapshotByIdAsync(id, h.RequestAborted);
    }

    public async Task<object> NewTaskAsync(HttpContext h)
    {
        var session = await RequireSessionAsync(h);
        var ct = h.RequestAborted;
        var gate = _locks.GetOrAdd(session.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var tasks = await TasksAsync(session.Id, ct);
            if (tasks.Any(t => t.Status == "Active")) return await SnapshotByIdAsync(session.Id, ct);
            var number = tasks.Count == 0 ? 1 : tasks.Max(t => t.Number) + 1;
            var difficulty = Math.Min(number, 3);
            var templates = await ReadAsync("""
                SELECT Id, Difficulty, TemplateJson FROM dbo.TaskTemplates
                WHERE FieldId=@field AND Difficulty<=@difficulty
                ORDER BY Difficulty DESC, Id;
                """, r => new TemplateRow(r.GetInt32(0), r.GetInt32(1), r.GetString(2)),
                ct, ("@field", session.FieldId), ("@difficulty", difficulty));
            var template = templates.FirstOrDefault() ?? throw new FlowFault(503, "قوالب المجال غير جاهزة.");
            var prepared = ParseTask(template.Json);
            var references = await ReferencesAsync(session.FieldId, ct);
            var previous = await ReadAsync("""
                SELECT TOP (1) a.SubmissionText FROM dbo.Attempts a
                INNER JOIN dbo.Tasks t ON t.Id=a.TaskId
                WHERE t.SessionId=@session AND t.Status='Completed'
                  AND a.Status='Reviewed' AND JSON_VALUE(a.ReviewJson,'$.completed')='true'
                ORDER BY t.SequenceNumber DESC, a.CreatedAtUtc DESC;
                """, r => r.GetString(0), ct, ("@session", session.Id));
            TaskContent content;
            try
            {
                content = await _ai.TaskAsync(Stages[session.Stage], references.Notes, prepared,
                    number, previous.FirstOrDefault() ?? "لا توجد محاولة سابقة.", ct);
            }
            catch (AiFailure error)
            {
                _logger.LogWarning("Task generation unavailable: {Reason}", error.Message);
                content = prepared with { Source = "template" };
            }
            content = content with { StarterCode = previous.FirstOrDefault() };
            await ExecuteAsync("""
                INSERT dbo.Tasks(Id, SessionId, FieldId, TemplateId, SequenceNumber, Difficulty, TaskJson)
                VALUES(@id,@session,@field,@template,@number,@difficulty,@json);
                """, ct, ("@id", Guid.NewGuid()), ("@session", session.Id), ("@field", session.FieldId),
                ("@template", template.Id), ("@number", number), ("@difficulty", template.Difficulty),
                ("@json", JsonSerializer.Serialize(content, KhudhaAi.Json)));
            return await SnapshotByIdAsync(session.Id, ct);
        }
        finally { gate.Release(); }
    }

    public async Task<object> ReviewAsync(Guid taskId, AttemptInput input, HttpContext h)
    {
        var submission = input.Submission?.Trim() ?? "";
        if (submission.Length == 0) throw new FlowFault(400, "اكتبي محاولتك قبل إرسالها للمراجعة.");
        if (submission.Length > 10000) throw new FlowFault(400, "الحد الأقصى للمحاولة 10000 حرف.");
        if (input.RequestId == Guid.Empty) throw new FlowFault(400, "أعيدي إرسال المحاولة.");
        var session = await RequireSessionAsync(h);
        var ct = h.RequestAborted;
        var gate = _locks.GetOrAdd(session.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var task = (await TasksAsync(session.Id, ct)).FirstOrDefault(t => t.Id == taskId)
                ?? throw new FlowFault(404, "المهمة غير موجودة في تجربتك.");
            var existing = await ReadAsync("""
                SELECT Id, TaskId, SubmissionText, ReviewJson, Status
                FROM dbo.Attempts WHERE Id=@id;
                """, MapAttempt, ct, ("@id", input.RequestId));
            if (existing.Count > 0)
            {
                if (existing[0].TaskId != taskId || existing[0].Submission != submission)
                    throw new FlowFault(409, "تغيّر نص المحاولة؛ أرسليها كمحاولة جديدة.");
                if (existing[0].Status == "Reviewed") return await SnapshotByIdAsync(session.Id, ct);
                await ExecuteAsync("UPDATE dbo.Attempts SET Status='Pending', UpdatedAtUtc=SYSUTCDATETIME() WHERE Id=@id;",
                    ct, ("@id", input.RequestId));
            }
            else
            {
                // Persist before the provider request so a failed request cannot lose the attempt.
                await ExecuteAsync("INSERT dbo.Attempts(Id,TaskId,SubmissionText) VALUES(@id,@task,@text);",
                    ct, ("@id", input.RequestId), ("@task", taskId), ("@text", submission));
            }

            ReviewContent review;
            try
            {
                var references = await ReferencesAsync(session.FieldId, ct);
                review = await _ai.ReviewAsync(Stages[session.Stage], references.Notes,
                    ParseTask(task.Json), submission, ct);
            }
            catch (AiFailure error)
            {
                _logger.LogWarning("Review unavailable: {Reason}", error.Message);
                await ExecuteAsync("UPDATE dbo.Attempts SET Status='Failed', UpdatedAtUtc=SYSUTCDATETIME() WHERE Id=@id;",
                    CancellationToken.None, ("@id", input.RequestId));
                throw new FlowFault(503, "محاولتك محفوظة، لكن الوكيل تعذر عليه مراجعتها الآن. اضغطي إعادة المراجعة.",
                    new { attemptId = input.RequestId });
            }

            await ExecuteAsync("""
                SET XACT_ABORT ON;
                BEGIN TRANSACTION;
                UPDATE dbo.Attempts SET ReviewJson=@review, Status='Reviewed', UpdatedAtUtc=SYSUTCDATETIME()
                    WHERE Id=@attempt;
                IF @completed=1 UPDATE dbo.Tasks SET Status='Completed' WHERE Id=@task;
                COMMIT TRANSACTION;
                """, ct, ("@review", JsonSerializer.Serialize(review, KhudhaAi.Json)),
                ("@attempt", input.RequestId), ("@task", taskId), ("@completed", review.Completed));
            return await SnapshotByIdAsync(session.Id, ct);
        }
        finally { gate.Release(); }
    }

    public async Task<object> HintAsync(Guid taskId, HintInput input, HttpContext h)
    {
        var question = input.Question?.Trim() ?? "";
        var draft = input.Draft ?? "";
        if (question.Length == 0) throw new FlowFault(400, "اكتبي سؤالك أولاً.");
        if (question.Length > 1000 || draft.Length > 10000)
            throw new FlowFault(400, "اختصري السؤال إلى 1000 حرف والمحاولة إلى 10000 حرف.");
        var session = await RequireSessionAsync(h);
        var ct = h.RequestAborted;
        var gate = _locks.GetOrAdd(session.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var task = (await TasksAsync(session.Id, ct)).FirstOrDefault(t => t.Id == taskId)
                ?? throw new FlowFault(404, "المهمة غير موجودة في تجربتك.");
            var messages = await MessagesAsync(session.Id, taskId, ct);
            var references = await ReferencesAsync(session.FieldId, ct);
            await ExecuteAsync("""
                INSERT dbo.Messages(SessionId,TaskId,MessageRole,Content)
                VALUES(@session,@task,'user',@text);
                """, ct, ("@session", session.Id), ("@task", taskId), ("@text", question));
            string answer;
            try
            {
                answer = await _ai.HintAsync(Stages[session.Stage], references.Notes, ParseTask(task.Json),
                    question, draft, JsonSerializer.Serialize(messages.TakeLast(8), KhudhaAi.Json), ct);
            }
            catch (AiFailure error)
            {
                _logger.LogWarning("Hint unavailable: {Reason}", error.Message);
                throw new FlowFault(503, "تعذر الحصول على رد من الوكيل الآن. سؤالك محفوظ ويمكنك المحاولة مجدداً.");
            }
            await ExecuteAsync("""
                INSERT dbo.Messages(SessionId,TaskId,MessageRole,Content)
                VALUES(@session,@task,'assistant',@text);
                """, ct, ("@session", session.Id), ("@task", taskId), ("@text", answer));
            return await SnapshotByIdAsync(session.Id, ct);
        }
        finally { gate.Release(); }
    }

    private async Task<object> SnapshotByIdAsync(Guid id, CancellationToken ct)
    {
        var sessions = await ReadAsync("SELECT Id,StudentStage,FieldId FROM dbo.Sessions WHERE Id=@id;",
            r => new SessionRow(r.GetGuid(0), r.GetString(1), r.GetInt32(2)), ct, ("@id", id));
        if (sessions.Count == 0) return new { session = (object?)null };
        var session = sessions[0];
        var field = await FieldAsync(session.FieldId, ct);
        var references = await ReferencesAsync(session.FieldId, ct);
        var tasks = await TasksAsync(id, ct);
        var task = tasks.OrderByDescending(t => t.Number).FirstOrDefault();
        var attempts = task is null ? new List<AttemptRow>() : await ReadAsync("""
            SELECT Id,TaskId,SubmissionText,ReviewJson,Status FROM dbo.Attempts
            WHERE TaskId=@task ORDER BY CreatedAtUtc DESC, UpdatedAtUtc DESC;
            """, MapAttempt, ct, ("@task", task.Id));
        var attempt = attempts.FirstOrDefault();
        var messages = task is null ? new List<MessageRow>() : await MessagesAsync(id, task.Id, ct);
        return new
        {
            session = new { id = session.Id, stage = session.Stage, stageLabel = Stages.GetValueOrDefault(session.Stage, session.Stage), field },
            progress = new { completed = tasks.Count(t => t.Status == "Completed"), generated = tasks.Count },
            currentTask = task is null ? null : new
                { id = task.Id, number = task.Number, status = task.Status, content = ParseTask(task.Json) },
            latestAttempt = attempt is null ? null : new
            {
                id = attempt.Id, taskId = attempt.TaskId, submission = attempt.Submission,
                status = attempt.Status,
                review = attempt.Review is null ? null : JsonSerializer.Deserialize<ReviewContent>(attempt.Review, KhudhaAi.Json)
            },
            history = tasks.OrderByDescending(t => t.Number).Select(t => new
                { id = t.Id, number = t.Number, title = ParseTask(t.Json).Title, status = t.Status }),
            messages,
            references = references.Sources
        };
    }

    private async Task<SessionRow> RequireSessionAsync(HttpContext h)
    {
        var id = ReadCookie(h) ?? throw new FlowFault(401, "ابدئي تجربة أولاً.");
        var rows = await ReadAsync("SELECT Id,StudentStage,FieldId FROM dbo.Sessions WHERE Id=@id;",
            r => new SessionRow(r.GetGuid(0), r.GetString(1), r.GetInt32(2)), h.RequestAborted, ("@id", id));
        return rows.FirstOrDefault() ?? throw new FlowFault(401, "ابدئي تجربة جديدة.");
    }

    private Guid? ReadCookie(HttpContext h)
    {
        if (!h.Request.Cookies.TryGetValue(CookieName, out var value)) return null;
        try { return Guid.TryParse(_protector.Unprotect(value), out var id) ? id : null; }
        catch (System.Security.Cryptography.CryptographicException) { return null; }
    }

    private async Task<FieldRow> FieldAsync(int id, CancellationToken ct)
    {
        var rows = await ReadAsync("SELECT Id,Slug,Name,Overview FROM dbo.Fields WHERE Id=@id;",
            r => new FieldRow(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3)), ct, ("@id", id));
        return rows.FirstOrDefault() ?? throw new FlowFault(400, "اختاري مجالاً متاحاً.");
    }

    private async Task<(string Notes, object Sources)> ReferencesAsync(int field, CancellationToken ct)
    {
        var rows = await ReadAsync("SELECT Title,ReferenceText,SourceUrl FROM dbo.FieldReferences WHERE FieldId=@id ORDER BY Id;",
            r => new { title = r.GetString(0), text = r.GetString(1), url = r.GetString(2) }, ct, ("@id", field));
        if (rows.Count == 0) throw new FlowFault(503, "مراجع المجال غير جاهزة.");
        return (string.Join("\n\n", rows.Select(r => $"{r.title}: {r.text}\nSource: {r.url}")),
            rows.Select(r => new { r.title, r.url }).ToArray());
    }

    private Task<List<TaskRow>> TasksAsync(Guid session, CancellationToken ct) => ReadAsync("""
        SELECT Id,SessionId,SequenceNumber,Difficulty,TaskJson,Status
        FROM dbo.Tasks WHERE SessionId=@session ORDER BY SequenceNumber;
        """, r => new TaskRow(r.GetGuid(0), r.GetGuid(1), r.GetInt32(2), r.GetInt32(3), r.GetString(4), r.GetString(5)),
        ct, ("@session", session));

    private Task<List<MessageRow>> MessagesAsync(Guid session, Guid task, CancellationToken ct) => ReadAsync("""
        SELECT Id,TaskId,MessageRole,Content FROM dbo.Messages
        WHERE SessionId=@session AND TaskId=@task ORDER BY Id;
        """, r => new MessageRow(r.GetInt64(0), r.IsDBNull(1) ? null : r.GetGuid(1), r.GetString(2), r.GetString(3)),
        ct, ("@session", session), ("@task", task));

    private static AttemptRow MapAttempt(SqlDataReader r) =>
        new(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4));
    private static TaskContent ParseTask(string json) =>
        JsonSerializer.Deserialize<TaskContent>(json, KhudhaAi.Json)
        ?? throw new InvalidOperationException("Invalid saved task");

    private async Task<List<T>> ReadAsync<T>(string sql, Func<SqlDataReader, T> map,
        CancellationToken ct, params (string Name, object? Value)[] arguments)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = BuildCommand(connection, sql, arguments);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<T>();
        while (await reader.ReadAsync(ct)) rows.Add(map(reader));
        return rows;
    }

    private async Task ExecuteAsync(string sql, CancellationToken ct,
        params (string Name, object? Value)[] arguments)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = BuildCommand(connection, sql, arguments);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static SqlCommand BuildCommand(SqlConnection connection, string sql,
        (string Name, object? Value)[] arguments)
    {
        var command = new SqlCommand(sql, connection) { CommandTimeout = 15 };
        foreach (var argument in arguments)
            command.Parameters.AddWithValue(argument.Name, argument.Value ?? DBNull.Value);
        return command;
    }
}
