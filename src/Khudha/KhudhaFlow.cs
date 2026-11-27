using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;

public sealed class FlowFault(int status, string message, object? details = null) : Exception(message)
{
    public int Status { get; } = status;
    public object? Details { get; } = details;
}
public sealed record StartInput(string Stage, string? Major = null, string? MajorSlug = null, int? FieldId = null);
public sealed record TrackInput(string TrackSlug);
public sealed record ExploreInput(string Question);
public sealed record TaskStartInput(string? LessonSlug = null);
public sealed record AttemptInput(Guid RequestId, string Submission);
public sealed record HintInput(string Question, string? Draft);
internal sealed record FieldRow(int Id, string Slug, string Name, string Overview);
internal sealed record SessionRow(Guid Id, string Stage, int? FieldId, string? Major, string? MajorSlug, string? TrackSlug);
internal sealed record TaskRow(Guid Id, Guid SessionId, int FieldId, int Number, int Difficulty, string Json, string Status);
internal sealed record AttemptRow(Guid Id, Guid TaskId, string Submission, string? Review, string Status);
internal sealed record MessageRow(long Id, Guid? TaskId, string Role, string Content);
internal sealed record TemplateRow(int Id, int Difficulty, string Json);

public sealed class KhudhaFlow
{
    private readonly string _connectionString;
    private readonly IDataProtector _protector;
    private readonly KhudhaAi _ai;
    private readonly KhudhaCatalog _catalog;
    private readonly ILogger<KhudhaFlow> _logger;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();
    private readonly SemaphoreSlim _catalogGate = new(1, 1);
    private volatile bool _catalogReady;
    private const string CookieName = "khudha.session";
    private static readonly Dictionary<string, string> Stages = new()
    {
        ["high-school"] = "طالب في المرحلة الثانوية",
        ["graduate"] = "طالب ثانوي أو خريج",
        ["university"] = "طالب جامعي أو خريج"
    };

    public KhudhaFlow(IConfiguration configuration, IDataProtectionProvider protection,
        KhudhaAi ai, KhudhaCatalog catalog, ILogger<KhudhaFlow> logger)
    {
        _connectionString = configuration.GetConnectionString("Khudha")
            ?? throw new InvalidOperationException("Missing ConnectionStrings:Khudha");
        _protector = protection.CreateProtector("Khudha.AnonymousSession.v1");
        _ai = ai; _catalog = catalog; _logger = logger;
    }

    public async Task<object> CatalogAsync(CancellationToken ct)
    {
        await EnsureCatalogAsync(ct);
        return _catalog.Majors;
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
        if (input.Stage is not ("graduate" or "university"))
            throw new FlowFault(400, "اختاري مرحلتك الدراسية أولاً.");
        var major = _catalog.FindMajor(input.MajorSlug ?? input.Major)
            ?? throw new FlowFault(400, "اختاري علوم الحاسب أو إدارة الأعمال من التخصصات المتاحة.");
        await EnsureCatalogAsync(h.RequestAborted);
        // University learners choose their track after the exploration chat.
        int? fieldId = input.Stage == "university" ? null :
            (await FieldBySlugAsync(major.IntroExperienceSlug, h.RequestAborted)).Id;
        var id = Guid.NewGuid();
        var profile = JsonSerializer.Serialize(new
            { major = major.Name, majorSlug = major.Slug, trackSlug = (string?)null }, KhudhaAi.Json);
        await ExecuteAsync("""
            INSERT dbo.Sessions(Id, StudentStage, FieldId, DiscoveryAnswersJson)
            VALUES(@id, @stage, @field, @profile);
            """, h.RequestAborted, ("@id", id), ("@stage", input.Stage),
            ("@field", fieldId), ("@profile", profile));
        h.Response.Cookies.Append(CookieName, _protector.Protect(id.ToString("D")), new CookieOptions
        {
            HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = h.Request.IsHttps,
            MaxAge = TimeSpan.FromDays(7), Path = "/"
        });
        return await SnapshotByIdAsync(id, h.RequestAborted);
    }

    public async Task<object> SelectTrackAsync(TrackInput input, HttpContext h)
    {
        var session = await RequireSessionAsync(h);
        var ct = h.RequestAborted;
        var gate = _locks.GetOrAdd(session.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            session = await RequireSessionAsync(h);
            if (session.Stage != "university")
                throw new FlowFault(400, "التجربة التمهيدية جاهزة لمرحلتك؛ لا تحتاجين اختيار مسار جامعي.");
            var major = _catalog.FindMajor(session.MajorSlug ?? session.Major)
                ?? throw new FlowFault(400, "ابدئي تجربة بأحد التخصصين المتاحين أولاً.");
            var track = major.Tracks.FirstOrDefault(t => t.Slug == input.TrackSlug)
                ?? throw new FlowFault(400, "اختاري مساراً تابعاً لتخصصك من الخيارات المعروضة.");
            if ((await TasksAsync(session.Id, ct)).Count > 0)
                throw new FlowFault(409, "بدأت المهام بالفعل. لاستكشاف مسار آخر ابدئي تجربة جديدة.");
            await EnsureCatalogAsync(ct);
            var field = await FieldBySlugAsync(track.Slug, ct);
            await ExecuteAsync("""
                UPDATE dbo.Sessions SET FieldId=@field,
                    DiscoveryAnswersJson=JSON_MODIFY(DiscoveryAnswersJson,'$.trackSlug',@track)
                WHERE Id=@id;
                """, ct, ("@field", field.Id), ("@track", track.Slug), ("@id", session.Id));
            return await SnapshotByIdAsync(session.Id, ct);
        }
        finally { gate.Release(); }
    }

    public async Task<object> ExploreAsync(ExploreInput input, HttpContext h)
    {
        var question = input.Question?.Trim() ?? "";
        if (question.Length == 0 || question.Length > 1000)
            throw new FlowFault(400, "اكتبي سؤالك في حد أقصى 1000 حرف.");
        var session = await RequireSessionAsync(h);
        var ct = h.RequestAborted;
        var gate = _locks.GetOrAdd(session.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            session = await RequireSessionAsync(h);
            var major = _catalog.FindMajor(session.MajorSlug ?? session.Major)
                ?? throw new FlowFault(400, "ابدئي تجربة بأحد التخصصين للاستفسار عنه.");
            var references = await ExplorationReferencesAsync(major, ct, session.Stage == "university");
            var messages = await ExplorationMessagesAsync(session.Id, ct);
            // A retry after provider failure reuses the pending question.
            var retry = IsPendingQuestion(messages, question);
            if (!retry)
                await ExecuteAsync("""
                    INSERT dbo.Messages(SessionId,TaskId,MessageRole,Content)
                    VALUES(@session,NULL,'user',@question);
                    """, ct, ("@session", session.Id), ("@question", question));
            var contextMessages = retry ? messages.SkipLast(1) : messages;
            var track = major.Tracks.FirstOrDefault(t => t.Slug == session.TrackSlug);
            string answer;
            try
            {
                answer = await _ai.ExploreAsync(session.Stage, Stages[session.Stage], major, track,
                    references.Notes, question,
                    JsonSerializer.Serialize(contextMessages.TakeLast(8), KhudhaAi.Json), ct,
                    (session.Stage == "university" ? major.Tracks.Select(t => t.Slug) : new[] { major.IntroExperienceSlug })
                        .Select(slug => _catalog.LearningPlan(slug)!).ToArray());
            }
            catch (AiFailure error)
            {
                _logger.LogWarning("Exploration unavailable: {Reason}", error.Message);
                throw new FlowFault(503, "تعذر رد الوكيل الآن. سؤالك محفوظ؛ يمكنك إعادة إرساله.");
            }
            await ExecuteAsync("""
                INSERT dbo.Messages(SessionId,TaskId,MessageRole,Content)
                VALUES(@session,NULL,'assistant',@answer);
                """, ct, ("@session", session.Id), ("@answer", answer));
            return await SnapshotByIdAsync(session.Id, ct);
        }
        finally { gate.Release(); }
    }

    public async Task<object> NewTaskAsync(TaskStartInput input, HttpContext h)
    {
        var session = await RequireSessionAsync(h);
        var ct = h.RequestAborted;
        var gate = _locks.GetOrAdd(session.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            session = await RequireSessionAsync(h);
            var tasks = await TasksAsync(session.Id, ct);
            if (!session.FieldId.HasValue)
                throw new FlowFault(400, "اختاري مساراً أولاً، ثم جهّزي مهمتك الأولى.");
            var field = await FieldAsync(session.FieldId.Value, ct);
            if (RoutingIssue(session, field, tasks) is string issue) throw new FlowFault(409, issue);
            if (tasks.Any(t => t.Status == "Active")) return await SnapshotByIdAsync(session.Id, ct);
            var experience = _catalog.FindExperience(field.Slug)
                ?? throw new FlowFault(400, "ابدئي تجربة بأحد المسارات المجهزة.");
            if (tasks.Count >= experience.Templates.Length) return await SnapshotByIdAsync(session.Id, ct);
            if (tasks.Count == 0)
            {
                if (input.LessonSlug != field.Slug)
                    throw new FlowFault(400, "اطلعي على الدرس والمثال وخطة المهام، ثم اضغطي زر البدء.");
                await ExecuteAsync("""
                    UPDATE dbo.Sessions SET DiscoveryAnswersJson=
                        JSON_MODIFY(COALESCE(DiscoveryAnswersJson,N'{}'),'$.lessonSlug',@slug)
                    WHERE Id=@id;
                    """, ct, ("@slug", field.Slug), ("@id", session.Id));
            }
            var number = tasks.Count == 0 ? 1 : tasks.Max(t => t.Number) + 1;
            var expectedTemplate = experience.Templates.OrderBy(t => t.Difficulty).ElementAt(number - 1);
            var templates = await ReadAsync("""
                SELECT Id, Difficulty, TemplateJson FROM dbo.TaskTemplates
                WHERE FieldId=@field AND Code=@code;
                """, r => new TemplateRow(r.GetInt32(0), r.GetInt32(1), r.GetString(2)),
                ct, ("@field", field.Id), ("@code", expectedTemplate.Code));
            var template = templates.FirstOrDefault() ?? throw new FlowFault(503, "قوالب المجال غير جاهزة.");
            var prepared = ParseTask(template.Json);
            var references = await ReferencesAsync(field.Id, ct);
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
                    number, previous.FirstOrDefault() ?? "لا توجد محاولة سابقة.", ct, session.Major, field.Name, field.Slug);
            }
            catch (AiFailure error)
            {
                _logger.LogWarning("Task generation unavailable: {Reason}", error.Message);
                content = prepared with { Source = "template" };
            }
            // Only the web tasks extend the previous submission as editable code.
            // Independent written tasks start with an empty answer.
            content = content with { StarterCode = field.Slug == "web-development" ? previous.FirstOrDefault() : null };
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
            var field = await RequireValidRouteAsync(session, ct);
            var tasks = await TasksAsync(session.Id, ct);
            var task = tasks.FirstOrDefault(t => t.Id == taskId)
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
            }
            // Idempotent retries above remain valid; new writes require the current active task.
            AssertReviewable(task, tasks.Max(t => t.Number));
            if (existing.Count > 0)
            {
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
                var references = await ReferencesAsync(session.FieldId
                    ?? throw new FlowFault(400, "اختاري مسارك أولاً."), ct);
                review = await _ai.ReviewAsync(Stages[session.Stage], references.Notes,
                    ParseTask(task.Json), submission, ct, session.Major, field.Slug);
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
            await RequireValidRouteAsync(session, ct);
            var task = (await TasksAsync(session.Id, ct)).FirstOrDefault(t => t.Id == taskId)
                ?? throw new FlowFault(404, "المهمة غير موجودة في تجربتك.");
            var messages = await MessagesAsync(session.Id, taskId, ct);
            var references = await ReferencesAsync(session.FieldId
                ?? throw new FlowFault(400, "اختاري مسارك أولاً."), ct);
            var retry = IsPendingQuestion(messages, question);
            if (!retry)
                await ExecuteAsync("""
                    INSERT dbo.Messages(SessionId,TaskId,MessageRole,Content)
                    VALUES(@session,@task,'user',@text);
                    """, ct, ("@session", session.Id), ("@task", taskId), ("@text", question));
            var contextMessages = retry ? messages.SkipLast(1) : messages;
            string answer;
            try
            {
                answer = await _ai.HintAsync(Stages[session.Stage], references.Notes, ParseTask(task.Json),
                    question, draft, JsonSerializer.Serialize(contextMessages.TakeLast(8), KhudhaAi.Json), ct, session.Major);
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
        var sessions = await ReadAsync("""
            SELECT Id,StudentStage,FieldId,JSON_VALUE(DiscoveryAnswersJson,'$.major'),
                JSON_VALUE(DiscoveryAnswersJson,'$.majorSlug'),JSON_VALUE(DiscoveryAnswersJson,'$.trackSlug')
            FROM dbo.Sessions WHERE Id=@id;
            """, MapSession, ct, ("@id", id));
        if (sessions.Count == 0) return new { session = (object?)null };
        var session = sessions[0];
        var major = _catalog.FindMajor(session.MajorSlug ?? session.Major);
        FieldRow? field = session.FieldId.HasValue ? await FieldAsync(session.FieldId.Value, ct) : null;
        (string Notes, object Sources) references;
        if (field is not null) references = await ReferencesAsync(field.Id, ct);
        else if (major is not null) references = await ExplorationReferencesAsync(major, ct, session.Stage == "university");
        else references = ("", Array.Empty<object>());
        var tasks = await TasksAsync(id, ct);
        var routingIssue = RoutingIssue(session, field, tasks);
        var learningPlan = _catalog.LearningPlan(ExpectedExperienceSlug(session, field));
        var total = learningPlan?.TaskCount ?? 3;
        var experienceComplete = routingIssue is null && tasks.Count >= total && tasks.All(t => t.Status == "Completed");
        var task = tasks.OrderByDescending(t => t.Number).FirstOrDefault();
        var attempts = task is null ? new List<AttemptRow>() : await ReadAsync("""
            SELECT Id,TaskId,SubmissionText,ReviewJson,Status FROM dbo.Attempts
            WHERE TaskId=@task
            ORDER BY CASE WHEN @completed=1 AND Status='Reviewed'
                AND JSON_VALUE(ReviewJson,'$.completed')='true' THEN 0 ELSE 1 END,
                CreatedAtUtc DESC, UpdatedAtUtc DESC, Id DESC;
            """, MapAttempt, ct, ("@task", task.Id), ("@completed", task.Status == "Completed"));
        var attempt = attempts.FirstOrDefault();
        var messages = task is null ? new List<MessageRow>() : await MessagesAsync(id, task.Id, ct);
        object? exploration = null;
        if (major is not null)
        {
            var selectedSlug = session.TrackSlug;
            var selectedTrack = major.Tracks.FirstOrDefault(t => t.Slug == selectedSlug);
            var explorationReferences = await ExplorationReferencesAsync(major, ct, session.Stage == "university");
            exploration = new
            {
                major, selectedTrack,
                requiresTrack = session.Stage == "university" && selectedTrack is null && tasks.Count == 0,
                canChooseTrack = routingIssue is null && session.Stage == "university" && tasks.Count == 0,
                canGenerate = routingIssue is null && (task is not null || session.Stage != "university" || selectedTrack is not null),
                messages = await ExplorationMessagesAsync(id, ct),
                references = explorationReferences.Sources
            };
        }
        return new
        {
            session = new
            {
                id = session.Id, stage = session.Stage,
                stageLabel = Stages.GetValueOrDefault(session.Stage, session.Stage),
                major = major?.Name ?? session.Major, majorSlug = major?.Slug ?? session.MajorSlug,
                trackSlug = session.TrackSlug, field
            },
            exploration,
            routingIssue, learningPlan, experienceComplete,
            inputKind = _catalog.FindExperience(field?.Slug)?.InputKind ?? "text",
            progress = new { completed = tasks.Count(t => t.Status == "Completed"), generated = tasks.Count, total = Math.Max(total, tasks.Count) },
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
        var rows = await ReadAsync("""
            SELECT Id,StudentStage,FieldId,JSON_VALUE(DiscoveryAnswersJson,'$.major'),
                JSON_VALUE(DiscoveryAnswersJson,'$.majorSlug'),JSON_VALUE(DiscoveryAnswersJson,'$.trackSlug')
            FROM dbo.Sessions WHERE Id=@id;
            """, MapSession, h.RequestAborted, ("@id", id));
        return rows.FirstOrDefault() ?? throw new FlowFault(401, "ابدئي تجربة جديدة.");
    }

    private Guid? ReadCookie(HttpContext h)
    {
        if (!h.Request.Cookies.TryGetValue(CookieName, out var value)) return null;
        try { return Guid.TryParse(_protector.Unprotect(value), out var id) ? id : null; }
        catch (System.Security.Cryptography.CryptographicException) { return null; }
    }

    private string? ExpectedExperienceSlug(SessionRow session, FieldRow? field)
    {
        var major = _catalog.FindMajor(session.MajorSlug ?? session.Major);
        if (major is null) return field?.Slug; // Preserve correctly labelled legacy web sessions.
        return session.Stage == "university"
            ? major.Tracks.FirstOrDefault(t => t.Slug == session.TrackSlug)?.Slug
            : major.IntroExperienceSlug;
    }

    private string? RoutingIssue(SessionRow session, FieldRow? field, IReadOnlyCollection<TaskRow> tasks)
    {
        var expected = ExpectedExperienceSlug(session, field);
        var wrongField = field?.Slug != expected;
        var wrongTask = tasks.Any(t => t.FieldId != session.FieldId);
        var unrelatedLesson = expected != "web-development" && tasks.Any(t => KhudhaAi.MentionsHtmlLesson(ParseTask(t.Json)));
        return wrongField || wrongTask || unrelatedLesson
            ? "هذه جلسة محفوظة من نسخة سابقة، ومهامها لا تطابق التخصص أو المسار المحدد. محاولاتك السابقة محفوظة؛ ابدأ التجربة الصحيحة."
            : null;
    }

    private async Task<FieldRow> RequireValidRouteAsync(SessionRow session, CancellationToken ct)
    {
        if (!session.FieldId.HasValue) throw new FlowFault(400, "اختاري مسارك أولاً.");
        var field = await FieldAsync(session.FieldId.Value, ct);
        var tasks = await TasksAsync(session.Id, ct);
        if (RoutingIssue(session, field, tasks) is string issue) throw new FlowFault(409, issue);
        return field;
    }

    private async Task<FieldRow> FieldAsync(int id, CancellationToken ct)
    {
        var rows = await ReadAsync("SELECT Id,Slug,Name,Overview FROM dbo.Fields WHERE Id=@id;",
            r => new FieldRow(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3)), ct, ("@id", id));
        return rows.FirstOrDefault() ?? throw new FlowFault(400, "اختاري مجالاً متاحاً.");
    }

    private async Task<FieldRow> FieldBySlugAsync(string slug, CancellationToken ct)
    {
        var rows = await ReadAsync("SELECT Id,Slug,Name,Overview FROM dbo.Fields WHERE Slug=@slug;",
            r => new FieldRow(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3)), ct, ("@slug", slug));
        return rows.FirstOrDefault() ?? throw new FlowFault(503, "محتوى التجربة غير جاهز. أعيدي تحميل الصفحة.");
    }

    private async Task<(string Notes, object Sources)> ReferencesAsync(int field, CancellationToken ct)
    {
        var rows = await ReadAsync("SELECT Title,ReferenceText,SourceUrl FROM dbo.FieldReferences WHERE FieldId=@id ORDER BY Id;",
            r => new { title = r.GetString(0), text = r.GetString(1), url = r.GetString(2) }, ct, ("@id", field));
        if (rows.Count == 0) throw new FlowFault(503, "مراجع المجال غير جاهزة.");
        return (string.Join("\n\n", rows.Select(r => $"{r.title}: {r.text}\nSource: {r.url}")),
            rows.Select(r => new { r.title, r.url }).ToArray());
    }

    private async Task<(string Notes, object Sources)> ExplorationReferencesAsync(MajorDefinition major, CancellationToken ct,
        bool includeTracks)
    {
        var rows = await ReadAsync("""
            SELECT r.Title,r.SourceUrl,r.ReferenceText
            FROM dbo.FieldReferences r INNER JOIN dbo.Fields f ON f.Id=r.FieldId
            WHERE f.Slug IN (@intro,@track1,@track2) ORDER BY r.Id;
            """, r => new ReferenceDefinition(r.GetString(0), r.GetString(1), r.GetString(2)), ct,
            ("@intro", major.IntroExperienceSlug),
            ("@track1", includeTracks ? major.Tracks[0].Slug : major.IntroExperienceSlug),
            ("@track2", includeTracks ? major.Tracks[1].Slug : major.IntroExperienceSlug));
        var unique = rows.DistinctBy(r => r.Url).ToArray();
        if (unique.Length == 0) throw new FlowFault(503, "مراجع التخصص غير جاهزة. أعيدي تحميل الصفحة.");
        return (string.Join("\n\n", unique.Select(r => $"{r.Title}: {r.Notes}\nSource: {r.Url}")),
            unique.Select(r => new { r.Title, r.Url }).ToArray());
    }

    private Task<List<TaskRow>> TasksAsync(Guid session, CancellationToken ct) => ReadAsync("""
        SELECT Id,SessionId,FieldId,SequenceNumber,Difficulty,TaskJson,Status
        FROM dbo.Tasks WHERE SessionId=@session ORDER BY SequenceNumber;
        """, r => new TaskRow(r.GetGuid(0), r.GetGuid(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4), r.GetString(5), r.GetString(6)),
        ct, ("@session", session));

    private Task<List<MessageRow>> MessagesAsync(Guid session, Guid task, CancellationToken ct) => ReadAsync("""
        SELECT Id,TaskId,MessageRole,Content FROM dbo.Messages
        WHERE SessionId=@session AND TaskId=@task ORDER BY Id;
        """, r => new MessageRow(r.GetInt64(0), r.IsDBNull(1) ? null : r.GetGuid(1), r.GetString(2), r.GetString(3)),
        ct, ("@session", session), ("@task", task));

    private Task<List<MessageRow>> ExplorationMessagesAsync(Guid session, CancellationToken ct) => ReadAsync("""
        SELECT Id,TaskId,MessageRole,Content FROM dbo.Messages
        WHERE SessionId=@session AND TaskId IS NULL ORDER BY Id;
        """, r => new MessageRow(r.GetInt64(0), null, r.GetString(2), r.GetString(3)), ct, ("@session", session));

    private static SessionRow MapSession(SqlDataReader r) =>
        new(r.GetGuid(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetInt32(2),
            r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
            r.IsDBNull(5) ? null : r.GetString(5));
    internal static bool IsPendingQuestion(IReadOnlyList<MessageRow> messages, string question) =>
        messages.Count > 0 && messages[^1].Role == "user" && messages[^1].Content == question;

    internal static void AssertReviewable(TaskRow task, int currentNumber)
    {
        if (task.Status == "Completed")
            throw new FlowFault(409, "هذه المهمة مكتملة بالفعل. سنعرض الإجابة المقبولة المحفوظة؛ تابعي للمهمة التالية.");
        if (task.Status != "Active" || task.Number != currentNumber)
            throw new FlowFault(409, "هذه ليست المهمة الحالية. حدّثي التجربة وتابعي المهمة الظاهرة.");
    }
    private static AttemptRow MapAttempt(SqlDataReader r) =>
        new(r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4));
    private static TaskContent ParseTask(string json) =>
        JsonSerializer.Deserialize<TaskContent>(json, KhudhaAi.Json)
        ?? throw new InvalidOperationException("Invalid saved task");

    private async Task EnsureCatalogAsync(CancellationToken ct)
    {
        if (_catalogReady) return;
        await _catalogGate.WaitAsync(ct);
        try
        {
            if (_catalogReady) return;
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(ct);
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);
            async Task SeedAsync(string sql, params (string Name, object? Value)[] arguments)
            {
                await using var command = BuildCommand(connection, sql, arguments);
                command.Transaction = transaction;
                await command.ExecuteNonQueryAsync(ct);
            }
            foreach (var experience in _catalog.Experiences)
            {
                await SeedAsync("""
                    IF NOT EXISTS(SELECT 1 FROM dbo.Fields WITH(UPDLOCK,HOLDLOCK) WHERE Slug=@slug)
                        INSERT dbo.Fields(Slug,Name,Overview) VALUES(@slug,@name,@overview);
                    """, ("@slug", experience.Slug), ("@name", experience.Name), ("@overview", experience.Overview));
                foreach (var reference in experience.References)
                    await SeedAsync("""
                        DECLARE @field int=(SELECT Id FROM dbo.Fields WHERE Slug=@slug);
                        IF NOT EXISTS(SELECT 1 FROM dbo.FieldReferences WITH(UPDLOCK,HOLDLOCK)
                            WHERE FieldId=@field AND SourceUrl=@url)
                            INSERT dbo.FieldReferences(FieldId,Title,SourceUrl,ReferenceText)
                            VALUES(@field,@title,@url,@notes);
                        """, ("@slug", experience.Slug), ("@title", reference.Title),
                        ("@url", reference.Url), ("@notes", reference.Notes));
                foreach (var template in experience.Templates)
                    await SeedAsync("""
                        DECLARE @field int=(SELECT Id FROM dbo.Fields WHERE Slug=@slug);
                        IF NOT EXISTS(SELECT 1 FROM dbo.TaskTemplates WITH(UPDLOCK,HOLDLOCK) WHERE Code=@code)
                            INSERT dbo.TaskTemplates(FieldId,Code,Difficulty,TemplateJson)
                            VALUES(@field,@code,@difficulty,@content);
                        ELSE
                            UPDATE dbo.TaskTemplates SET Difficulty=@difficulty,TemplateJson=@content
                            WHERE Code=@code AND FieldId=@field;
                        """, ("@slug", experience.Slug), ("@code", template.Code),
                        ("@difficulty", template.Difficulty),
                        ("@content", JsonSerializer.Serialize(template.Content, KhudhaAi.Json)));
            }
            await transaction.CommitAsync(ct);
            _catalogReady = true;
        }
        finally { _catalogGate.Release(); }
    }

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
