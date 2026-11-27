public static class KhudhaSelfTests
{
    public static void Run(KhudhaCatalog catalog)
    {
        var checks = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException("FAIL: " + label);
            checks++;
            Console.WriteLine("PASS: " + label);
        }
        void Reject(Action action, Func<Exception, bool> expected, string label)
        {
            try { action(); }
            catch (Exception error) when (expected(error)) { Check(true, label); return; }
            throw new InvalidOperationException("FAIL: " + label);
        }

        Check(catalog.Majors.Count == 2 && catalog.Experiences.Count == 6,
            "two majors and six prepared experiences");
        var templates = 0;
        foreach (var experience in catalog.Experiences)
        foreach (var entry in experience.Templates)
        {
            var original = entry.Content;
            var expectedInstructions = original.Instructions.ToArray();
            var expectedCriteria = original.Criteria.ToArray();
            var hint = "قسّم المطلوب إلى خطوات، وراجع المعايير قبل إرسال محاولتك.";
            var guided = KhudhaAi.GuideTemplate(original, hint, experience.Slug);
            Check(guided.Title == original.Title && guided.Deliverable == original.Deliverable &&
                guided.Instructions.SequenceEqual(expectedInstructions) &&
                guided.Criteria.SequenceEqual(expectedCriteria) &&
                original.Instructions.SequenceEqual(expectedInstructions) &&
                original.Criteria.SequenceEqual(expectedCriteria) && guided.Hint == hint &&
                guided.Source == "guided" && guided.StarterCode is null,
                "unchanged requirements and rubric: " + entry.Code);
            templates++;
        }
        Check(templates == 18, "all eighteen templates checked");

        var business = catalog.FindExperience("business-foundations")!.Templates[0].Content;
        var web = catalog.FindExperience("web-development")!.Templates[0].Content;
        Reject(() => KhudhaAi.GuideTemplate(business, "غيّر العدد إلى 999.", "business-foundations"),
            error => error is AiFailure, "reject changed numeric values in an AI hint");
        Reject(() => KhudhaAi.GuideTemplate(business, "اكتب HTML للمهمة.", "business-foundations"),
            error => error is AiFailure, "reject HTML guidance outside the web route");
        Check(KhudhaAi.GuideTemplate(web, "ضع اسم المتجر داخل h1 مع وسم الإغلاق.", "web-development").Source == "guided",
            "allow legitimate h1 guidance in the web route");

        var task = new TaskRow(Guid.NewGuid(), Guid.NewGuid(), 1, 1, 1, "{}", "Active");
        KhudhaFlow.AssertReviewable(task, 1);
        Check(true, "accept a review of the current active task");
        Reject(() => KhudhaFlow.AssertReviewable(task with { Status = "Completed" }, 1),
            error => error is FlowFault fault && fault.Status == 409,
            "reject a new attempt on a completed task");
        Reject(() => KhudhaFlow.AssertReviewable(task, 2),
            error => error is FlowFault fault && fault.Status == 409,
            "reject an attempt on an older task");

        var question = "اشرح الخطوة بطريقة أخرى.";
        var user = new MessageRow(1, task.Id, "user", question);
        var assistant = new MessageRow(2, task.Id, "assistant", "تلميح توضيحي.");
        Check(KhudhaFlow.IsPendingQuestion(new[] { user }, question),
            "reuse a pending question when retrying");
        Check(!KhudhaFlow.IsPendingQuestion(new[] { user, assistant }, question),
            "allow the same question again after an answer");
        Check(!KhudhaFlow.IsPendingQuestion(new[] { user }, "سؤال مختلف."),
            "keep a different question as a new message");

        foreach (var major in catalog.Majors)
        {
            var allPlans = new[] { catalog.LearningPlan(major.IntroExperienceSlug)! }
                .Concat(major.Tracks.Select(track => catalog.LearningPlan(track.Slug)!)).ToArray();
            var secondary = System.Text.Json.JsonSerializer.SerializeToElement(
                KhudhaAi.ExplorationData("graduate", major, major.Tracks[0], allPlans), KhudhaAi.Json);
            Check(!secondary.TryGetProperty("availableTracks", out _) &&
                !secondary.TryGetProperty("selectedTrack", out _) &&
                secondary.GetProperty("phase").GetString() == "secondary-major-introduction",
                "secondary context has no track-choice menu: " + major.Slug);
            Check(secondary.GetProperty("introductoryLearningPlan").GetProperty("experienceSlug").GetString() == major.IntroExperienceSlug,
                "secondary context uses the introductory lesson only: " + major.Slug);
            var university = System.Text.Json.JsonSerializer.SerializeToElement(
                KhudhaAi.ExplorationData("university", major, major.Tracks[0], allPlans), KhudhaAi.Json);
            Check(university.GetProperty("availableTracks").GetArrayLength() == 2 &&
                university.GetProperty("learningPlans").GetArrayLength() == 2 &&
                university.GetProperty("selectedTrack").GetProperty("slug").GetString() == major.Tracks[0].Slug &&
                university.GetProperty("learningPlans").EnumerateArray()
                    .All(plan => plan.GetProperty("experienceSlug").GetString() != major.IntroExperienceSlug),
                "university context contains its two track choices only: " + major.Slug);
        }
        Check(KhudhaAi.ExplorationRules("graduate").Contains("NO track-selection step", StringComparison.Ordinal) &&
            KhudhaAi.ExplorationRules("high-school") == KhudhaAi.ExplorationRules("graduate"),
            "secondary and legacy school stage use the major-introduction rule");
        Check(KhudhaAi.ExplorationRules("university").Contains("university-track-exploration", StringComparison.Ordinal),
            "university stage receives its separate track rule");
        Console.WriteLine($"SUCCESS: {checks} checks passed. No SQL Server or Gemini requests were made.");
    }
}
