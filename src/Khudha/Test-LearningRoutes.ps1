param([string]$KhudhaBaseUrl = "http://localhost:5174")

# Runs against the real local server. Creates six isolated anonymous test sessions.
# Does not change browser cookies and does not call Gemini or create attempts.
$khudhaCases = @(
    @{ Stage = "graduate"; Major = "computer-science"; Track = $null; Expected = "cs-foundations" },
    @{ Stage = "graduate"; Major = "business-administration"; Track = $null; Expected = "business-foundations" },
    @{ Stage = "university"; Major = "computer-science"; Track = "web-development"; Expected = "web-development" },
    @{ Stage = "university"; Major = "computer-science"; Track = "data-analysis"; Expected = "data-analysis" },
    @{ Stage = "university"; Major = "business-administration"; Track = "marketing"; Expected = "marketing" },
    @{ Stage = "university"; Major = "business-administration"; Track = "operations"; Expected = "operations" }
)

$khudhaCatalog = Invoke-RestMethod -Uri "$KhudhaBaseUrl/api/catalog" -ErrorAction Stop
if (@($khudhaCatalog).Count -ne 2) { throw "Expected exactly two majors." }
$khudhaResults = foreach ($khudhaCase in $khudhaCases) {
    $khudhaBody = @{ stage = $khudhaCase.Stage; majorSlug = $khudhaCase.Major } | ConvertTo-Json -Compress
    $khudhaResult = Invoke-RestMethod -Method Post -Uri "$KhudhaBaseUrl/api/session" `
        -ContentType "application/json" -Body $khudhaBody -SessionVariable khudhaRouteSession -ErrorAction Stop
    if ($khudhaCase.Stage -eq "university" -and $null -ne $khudhaResult.session.field) {
        throw "University session selected an experience before a track was chosen."
    }
    if ($khudhaCase.Track) {
        $khudhaBody = @{ trackSlug = $khudhaCase.Track } | ConvertTo-Json -Compress
        $khudhaResult = Invoke-RestMethod -Method Post -Uri "$KhudhaBaseUrl/api/session/track" `
            -ContentType "application/json" -Body $khudhaBody -WebSession $khudhaRouteSession -ErrorAction Stop
    }
    if ($khudhaResult.routingIssue) { throw $khudhaResult.routingIssue }
    if ($khudhaResult.session.field.slug -ne $khudhaCase.Expected) {
        throw "Incorrect experience: expected $($khudhaCase.Expected), got $($khudhaResult.session.field.slug)."
    }
    if ($khudhaResult.learningPlan.experienceSlug -ne $khudhaCase.Expected) {
        throw "The lesson does not match the selected experience."
    }
    if ($khudhaResult.learningPlan.taskCount -ne 3 -or @($khudhaResult.learningPlan.tasks).Count -ne 3) {
        throw "Expected a learning plan containing exactly three tasks."
    }
    if (-not $khudhaResult.learningPlan.lesson.example.scenario) { throw "Missing worked example." }
    if ($null -ne $khudhaResult.currentTask) { throw "A task was created before the lesson was started." }
    [pscustomobject]@{
        Stage = $khudhaCase.Stage
        Major = $khudhaCase.Major
        Experience = $khudhaResult.session.field.slug
        Tasks = $khudhaResult.learningPlan.taskCount
        Passed = $true
    }
}
$khudhaResults | Format-Table -AutoSize
Write-Host "PASS: six real session routes and matching three-task lessons. Gemini was not called."
