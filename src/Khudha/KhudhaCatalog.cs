using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

public sealed record IntroSection(string Title, string[] Items);
public sealed record ReferenceDefinition(string Title, string Url, string Notes);
public sealed record TrackDefinition(string Slug, string Name, string Description,
    string Example, string[] Skills, string Fit);
public sealed record MajorDefinition(string Slug, string Name, string[] Aliases,
    string Summary, string IntroExperienceSlug, IntroSection[] Sections,
    ReferenceDefinition[] Sources, TrackDefinition[] Tracks);
public sealed record TemplateDefinition(string Code, int Difficulty, TaskContent Content);
public sealed record WorkedExample(string Title, string Scenario, string[] Steps, string Takeaway, string? Code);
public sealed record LessonDefinition(string Title, string Goal, IntroSection[] Sections,
    WorkedExample Example, string[] Mechanism);
public sealed record TaskPreview(int Number, string Title, string Deliverable);
public sealed record LearningPlanView(string ExperienceSlug, string ExperienceName, int TaskCount,
    LessonDefinition Lesson, TaskPreview[] Tasks);
public sealed record ExperienceDefinition(string Slug, string Name, string Overview,
    string InputKind, ReferenceDefinition[] References, TemplateDefinition[] Templates, LessonDefinition Lesson);
public sealed record CatalogDefinition(MajorDefinition[] Majors, ExperienceDefinition[] Experiences);

public sealed class KhudhaCatalog
{
    private readonly CatalogDefinition _data;
    public IReadOnlyList<MajorDefinition> Majors => _data.Majors;
    public IReadOnlyList<ExperienceDefinition> Experiences => _data.Experiences;

    public KhudhaCatalog(IWebHostEnvironment environment)
    {
        var path = Path.Combine(environment.ContentRootPath, "Content", "catalog.json");
        _data = JsonSerializer.Deserialize<CatalogDefinition>(File.ReadAllText(path), KhudhaAi.Json)
            ?? throw new InvalidOperationException("The Khudha catalog is missing or invalid.");
        if (_data.Majors.Length != 2 || _data.Majors.Select(m => m.Slug).Distinct().Count() != 2)
            throw new InvalidOperationException("The prototype must contain two distinct majors.");
        foreach (var experience in _data.Experiences)
        {
            if (experience.Templates.Length != 3 || experience.References.Length == 0 ||
                experience.Lesson is null || experience.Lesson.Sections.Length == 0 ||
                experience.Templates.Any(t => !KhudhaAi.ValidTask(t.Content)))
                throw new InvalidOperationException($"Invalid experience: {experience.Slug}");
            if (experience.References.Any(r => !Uri.TryCreate(r.Url, UriKind.Absolute, out var url) || url.Scheme != "https"))
                throw new InvalidOperationException("Reference URLs must use HTTPS.");
        }
        foreach (var major in _data.Majors)
        {
            if (FindExperience(major.IntroExperienceSlug) is null || major.Tracks.Length < 2 ||
                major.Tracks.Any(t => FindExperience(t.Slug) is null))
                throw new InvalidOperationException($"Invalid major routes: {major.Slug}");
        }
    }

    public MajorDefinition? FindMajor(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var normalized = Normalize(input);
        return Majors.FirstOrDefault(m => Normalize(m.Slug) == normalized ||
            Normalize(m.Name) == normalized || m.Aliases.Any(a => Normalize(a) == normalized));
    }

    public ExperienceDefinition? FindExperience(string? slug) =>
        Experiences.FirstOrDefault(e => e.Slug == slug);

    public LearningPlanView? LearningPlan(string? slug)
    {
        var experience = FindExperience(slug);
        return experience is null ? null : new LearningPlanView(experience.Slug, experience.Name,
            experience.Templates.Length, experience.Lesson,
            experience.Templates.OrderBy(t => t.Difficulty)
                .Select((t, index) => new TaskPreview(index + 1, t.Content.Title, t.Content.Deliverable)).ToArray());
    }

    private static string Normalize(string value) => Regex.Replace(
        value.Normalize(NormalizationForm.FormKC), "[\u064B-\u065F\u0670]", "")
        .Replace('أ', 'ا').Replace('إ', 'ا').Replace('آ', 'ا').Replace('ٱ', 'ا')
        .Replace('ى', 'ي').Replace('ة', 'ه').Trim().ToLowerInvariant();
}
