-- Khudha prototype: initial SQL Server schema and seed data.
-- Save as UTF-8; run from the repository root:
-- sqlcmd -S "." -E -C -d KhudhaPrototype -f 65001 -b -i "docs/khudha-schema.sql"
-- Creates missing tables and seed rows. Does not delete or overwrite existing data.
-- Student attempts/reviews are saved by the application; none are fabricated here.

SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() <> N'KhudhaPrototype'
    THROW 50001, 'Run this script against KhudhaPrototype only.', 1;

BEGIN TRY
    BEGIN TRANSACTION;

    IF OBJECT_ID(N'dbo.Fields', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.Fields (
            Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Fields PRIMARY KEY,
            Slug varchar(80) NOT NULL CONSTRAINT UQ_Fields_Slug UNIQUE,
            Name nvarchar(120) NOT NULL,
            Overview nvarchar(max) NOT NULL
        );
    END;

    IF OBJECT_ID(N'dbo.FieldReferences', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.FieldReferences (
            Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_FieldReferences PRIMARY KEY,
            FieldId int NOT NULL,
            Title nvarchar(200) NOT NULL,
            SourceUrl varchar(450) NOT NULL,
            ReferenceText nvarchar(max) NOT NULL,
            CONSTRAINT FK_FieldReferences_Fields FOREIGN KEY (FieldId) REFERENCES dbo.Fields(Id),
            CONSTRAINT UQ_FieldReferences_Source UNIQUE (FieldId, SourceUrl)
        );
    END;

    IF OBJECT_ID(N'dbo.TaskTemplates', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.TaskTemplates (
            Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_TaskTemplates PRIMARY KEY,
            FieldId int NOT NULL,
            Code varchar(80) NOT NULL CONSTRAINT UQ_TaskTemplates_Code UNIQUE,
            Difficulty int NOT NULL,
            TemplateJson nvarchar(max) NOT NULL,
            CONSTRAINT FK_TaskTemplates_Fields FOREIGN KEY (FieldId) REFERENCES dbo.Fields(Id),
            CONSTRAINT CK_TaskTemplates_Difficulty CHECK (Difficulty >= 1),
            CONSTRAINT CK_TaskTemplates_Json CHECK (ISJSON(TemplateJson) = 1)
        );
    END;

    IF OBJECT_ID(N'dbo.Sessions', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.Sessions (
            Id uniqueidentifier NOT NULL CONSTRAINT DF_Sessions_Id DEFAULT NEWID()
                CONSTRAINT PK_Sessions PRIMARY KEY,
            StudentStage nvarchar(40) NOT NULL,
            FieldId int NULL,
            DiscoveryAnswersJson nvarchar(max) NULL,
            CreatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_Sessions_Created DEFAULT SYSUTCDATETIME(),
            CONSTRAINT FK_Sessions_Fields FOREIGN KEY (FieldId) REFERENCES dbo.Fields(Id),
            CONSTRAINT CK_Sessions_Answers CHECK
                (DiscoveryAnswersJson IS NULL OR ISJSON(DiscoveryAnswersJson) = 1)
        );
    END;

    IF OBJECT_ID(N'dbo.Tasks', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.Tasks (
            Id uniqueidentifier NOT NULL CONSTRAINT DF_Tasks_Id DEFAULT NEWID()
                CONSTRAINT PK_Tasks PRIMARY KEY,
            SessionId uniqueidentifier NOT NULL,
            FieldId int NOT NULL,
            TemplateId int NULL,
            SequenceNumber int NOT NULL,
            Difficulty int NOT NULL,
            TaskJson nvarchar(max) NOT NULL,
            Status varchar(20) NOT NULL CONSTRAINT DF_Tasks_Status DEFAULT 'Active',
            CreatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_Tasks_Created DEFAULT SYSUTCDATETIME(),
            CONSTRAINT FK_Tasks_Sessions FOREIGN KEY (SessionId) REFERENCES dbo.Sessions(Id),
            CONSTRAINT FK_Tasks_Fields FOREIGN KEY (FieldId) REFERENCES dbo.Fields(Id),
            CONSTRAINT FK_Tasks_Templates FOREIGN KEY (TemplateId) REFERENCES dbo.TaskTemplates(Id),
            CONSTRAINT UQ_Tasks_Sequence UNIQUE (SessionId, SequenceNumber),
            CONSTRAINT UQ_Tasks_SessionPair UNIQUE (Id, SessionId),
            CONSTRAINT CK_Tasks_Number CHECK (SequenceNumber >= 1 AND Difficulty >= 1),
            CONSTRAINT CK_Tasks_Status CHECK (Status IN ('Active', 'Completed')),
            CONSTRAINT CK_Tasks_Json CHECK (ISJSON(TaskJson) = 1)
        );
    END;

    IF OBJECT_ID(N'dbo.Attempts', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.Attempts (
            Id uniqueidentifier NOT NULL CONSTRAINT DF_Attempts_Id DEFAULT NEWID()
                CONSTRAINT PK_Attempts PRIMARY KEY,
            TaskId uniqueidentifier NOT NULL,
            SubmissionText nvarchar(max) NOT NULL,
            ReviewJson nvarchar(max) NULL,
            Status varchar(20) NOT NULL CONSTRAINT DF_Attempts_Status DEFAULT 'Pending',
            CreatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_Attempts_Created DEFAULT SYSUTCDATETIME(),
            UpdatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_Attempts_Updated DEFAULT SYSUTCDATETIME(),
            CONSTRAINT FK_Attempts_Tasks FOREIGN KEY (TaskId) REFERENCES dbo.Tasks(Id),
            CONSTRAINT CK_Attempts_Submission CHECK (LEN(LTRIM(RTRIM(SubmissionText))) > 0),
            CONSTRAINT CK_Attempts_Status CHECK (Status IN ('Pending', 'Reviewed', 'Failed')),
            CONSTRAINT CK_Attempts_Json CHECK (ReviewJson IS NULL OR ISJSON(ReviewJson) = 1),
            CONSTRAINT CK_Attempts_ReviewState CHECK (Status <> 'Reviewed' OR ReviewJson IS NOT NULL)
        );
        CREATE INDEX IX_Attempts_Task_Created ON dbo.Attempts(TaskId, CreatedAtUtc);
    END;

    IF OBJECT_ID(N'dbo.Messages', N'U') IS NULL
    BEGIN
        CREATE TABLE dbo.Messages (
            Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_Messages PRIMARY KEY,
            SessionId uniqueidentifier NOT NULL,
            TaskId uniqueidentifier NULL,
            MessageRole varchar(12) NOT NULL,
            Content nvarchar(max) NOT NULL,
            CreatedAtUtc datetime2(0) NOT NULL CONSTRAINT DF_Messages_Created DEFAULT SYSUTCDATETIME(),
            CONSTRAINT FK_Messages_Sessions FOREIGN KEY (SessionId) REFERENCES dbo.Sessions(Id),
            CONSTRAINT FK_Messages_TaskSession FOREIGN KEY (TaskId, SessionId)
                REFERENCES dbo.Tasks(Id, SessionId),
            CONSTRAINT CK_Messages_Role CHECK (MessageRole IN ('user', 'assistant')),
            CONSTRAINT CK_Messages_Content CHECK (LEN(LTRIM(RTRIM(Content))) > 0)
        );
        CREATE INDEX IX_Messages_Session_Id ON dbo.Messages(SessionId, Id);
    END;

    -- Initial reference material and team-prepared templates; not generated student results.
    IF NOT EXISTS (SELECT 1 FROM dbo.Fields WHERE Slug = 'web-development')
        INSERT dbo.Fields (Slug, Name, Overview)
        VALUES ('web-development', N'تطوير المواقع', N'تجربة تعريفية بتطوير صفحات الويب، تبدأ بتنظيم المحتوى باستخدام HTML. ستجرب إنشاء عنوان ووصف، ثم رابط وقائمة، مع مساعدة الوكيل ومراجعة محاولتك.');

    DECLARE @FieldId int = (SELECT Id FROM dbo.Fields WHERE Slug = 'web-development');

    IF NOT EXISTS (SELECT 1 FROM dbo.FieldReferences WHERE FieldId = @FieldId AND SourceUrl = 'https://developer.mozilla.org/en-US/docs/Web/HTML/Reference/Elements/Heading_Elements')
        INSERT dbo.FieldReferences (FieldId, Title, SourceUrl, ReferenceText)
        VALUES (@FieldId, N'MDN: HTML headings', 'https://developer.mozilla.org/en-US/docs/Web/HTML/Reference/Elements/Heading_Elements', N'تمثل h1 إلى h6 مستويات العناوين في HTML؛ h1 هو المستوى الأعلى. في المهمة التمهيدية يُطلب عنوان رئيسي واضح. تغيير شكل العنوان ليس مطلوباً في هذه المهمة.');

    IF NOT EXISTS (SELECT 1 FROM dbo.FieldReferences WHERE FieldId = @FieldId AND SourceUrl = 'https://developer.mozilla.org/en-US/docs/Web/HTML/Reference/Elements/p')
        INSERT dbo.FieldReferences (FieldId, Title, SourceUrl, ReferenceText)
        VALUES (@FieldId, N'MDN: HTML paragraphs', 'https://developer.mozilla.org/en-US/docs/Web/HTML/Reference/Elements/p', N'يمثل p فقرة نصية. في المهمة التمهيدية نطلب فقرة تصف المتجر، مع كتابة وسم الفتح والإغلاق للتدرب عليهما.');

    IF NOT EXISTS (SELECT 1 FROM dbo.FieldReferences WHERE FieldId = @FieldId AND SourceUrl = 'https://developer.mozilla.org/en-US/docs/Web/HTML/Reference/Elements/a')
        INSERT dbo.FieldReferences (FieldId, Title, SourceUrl, ReferenceText)
        VALUES (@FieldId, N'MDN: HTML links', 'https://developer.mozilla.org/en-US/docs/Web/HTML/Reference/Elements/a', N'ينشئ العنصر a رابطاً عند تحديد وجهته في الخاصية href. يوضع بين وسمي الفتح والإغلاق نص يوضح وجهة الرابط.');

    IF NOT EXISTS (SELECT 1 FROM dbo.FieldReferences WHERE FieldId = @FieldId AND SourceUrl = 'https://developer.mozilla.org/en-US/docs/Web/HTML/Reference/Elements/ul')
        INSERT dbo.FieldReferences (FieldId, Title, SourceUrl, ReferenceText)
        VALUES (@FieldId, N'MDN: HTML unordered lists', 'https://developer.mozilla.org/en-US/docs/Web/HTML/Reference/Elements/ul', N'يمثل ul قائمة غير مرتبة. تمثل عناصر li العناصر الفردية داخل القائمة. يمكن استخدامهما لعرض ثلاثة منتجات لمتجر خيالي.');

    IF NOT EXISTS (SELECT 1 FROM dbo.TaskTemplates WHERE Code = 'html-store-intro')
        INSERT dbo.TaskTemplates (FieldId, Code, Difficulty, TemplateJson)
        VALUES (@FieldId, 'html-store-intro', 1, N'{"title":"عرّف بمتجرك الخيالي","instructions":["اختر اسماً لمتجر خيالي واكتب عنوانه باستخدام h1.","اكتب وصفاً قصيراً للمتجر باستخدام p.","في هذه التجربة اكتب وسمي الفتح والإغلاق لكل عنصر، دون CSS أو JavaScript."],"deliverable":"كود HTML كنص يحتوي على عنوان وفقرة وصف.","criteria":["وجود عنوان مناسب باستخدام h1 مع فتح الوسم وإغلاقه.","وجود وصف باستخدام p مع فتح الوسم وإغلاقه، دون CSS أو JavaScript."],"hint":"ابدأ بالعنوان ثم الفقرة. ضع محتوى كل عنصر بين وسم الفتح ووسم الإغلاق."}');

    IF NOT EXISTS (SELECT 1 FROM dbo.TaskTemplates WHERE Code = 'html-store-link')
        INSERT dbo.TaskTemplates (FieldId, Code, Difficulty, TemplateJson)
        VALUES (@FieldId, 'html-store-link', 2, N'{"title":"أضف رابطاً مفيداً للمتجر","instructions":["أكمل صفحة المتجر التي أنشأتها في المهمة السابقة.","أضف عنصراً من نوع a يشير إلى https://example.com باستخدام الخاصية href.","اكتب داخل الرابط نصاً واضحاً يشرح وجهته، واحتفظ بالعنوان والفقرة."],"deliverable":"كود HTML للعنوان والفقرة والرابط.","criteria":["الاحتفاظ بعنوان المتجر وفقرة وصفه.","وجود رابط a له href بالقيمة https://example.com ونص واضح ووسم إغلاق."],"hint":"تحدد href الوجهة، بينما يوضح النص بين الوسمين ما سيظهر للزائر."}');

    IF NOT EXISTS (SELECT 1 FROM dbo.TaskTemplates WHERE Code = 'html-store-list')
        INSERT dbo.TaskTemplates (FieldId, Code, Difficulty, TemplateJson)
        VALUES (@FieldId, 'html-store-list', 3, N'{"title":"اعرض منتجات المتجر في قائمة","instructions":["أكمل صفحة المتجر مع الاحتفاظ بالعنوان والفقرة والرابط.","أضف قائمة غير مرتبة باستخدام ul.","أدرج ثلاثة منتجات خيالية داخلها، كل منتج في عنصر li مستقل."],"deliverable":"كود HTML للصفحة مع قائمة بثلاثة منتجات.","criteria":["وجود ul تحتوي على ثلاثة عناصر li ذات أسماء منتجات، مع وسوم فتح وإغلاق.","الاحتفاظ بالعنوان والفقرة والرابط من التجربة السابقة، دون إضافة CSS أو JavaScript."],"hint":"تجمع ul القائمة كاملة، وتضع اسم كل منتج داخل عنصر li منفصل."}');

    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;

SELECT N'Khudha schema and seed are ready.' AS Result;
SELECT N'Fields' AS TableName, COUNT(*) AS [RowCount] FROM dbo.Fields
UNION ALL SELECT N'FieldReferences', COUNT(*) FROM dbo.FieldReferences
UNION ALL SELECT N'TaskTemplates', COUNT(*) FROM dbo.TaskTemplates
UNION ALL SELECT N'Sessions', COUNT(*) FROM dbo.Sessions
UNION ALL SELECT N'Tasks', COUNT(*) FROM dbo.Tasks
UNION ALL SELECT N'Attempts', COUNT(*) FROM dbo.Attempts
UNION ALL SELECT N'Messages', COUNT(*) FROM dbo.Messages;

