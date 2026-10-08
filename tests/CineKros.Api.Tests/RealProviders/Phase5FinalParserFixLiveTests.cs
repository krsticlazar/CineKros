using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CineKros.Api.RealProviders;
using CineKros.Api.Search;
using Microsoft.Extensions.Logging;

namespace CineKros.Api.Tests.RealProviders;

[TestClass]
public sealed class Phase5FinalParserFixLiveTests
{
    private const string Gate = "CINEKROS_SR_P5_FINAL_LIVE";
    private const string DiagnosticPromptSha = "EFCDB69530926AFF149A8F9FE6F7740DC7ECDA4D28B035294E8D7CF9DDA6A1D4";
    private const string TestCorrectedPromptSha = "0AB18CFB00A41E8D6B5E38E9030C7612C8B8602290EB0C2F2CD460CF01497401";
    private const string AuthPath = @"D:\Repozitorijum\CineKros\.local\planning\reports\sr-phase-05-final-parser\authorization.json";
    private const string PlanPath = @"D:\Repozitorijum\CineKros\.local\planning\reports\sr-phase-05\live-parser-plan.json";
    private const string JournalDirectory = @"D:\Repozitorijum\CineKros\.local\planning\reports\sr-phase-05-final-parser";
    private const string SchemaSha = "2C9AAB879BF4072D18868488656239F47AFBD06362257BCA382CF86190440EEC";
    private const string PlanSha = "820143BF119F6EBB2749CAD673B226113954F0D1D2EE6E18AE2D728C49D15911";
    private const int GlobalMaxAttempts = 29;
    private const int GlobalMaxRetries = 5;
    private const int GlobalMaxPrimaries = 24;
    private const int MaxMinutes = 20;
    private static readonly TimeSpan MinimumSpacing = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20)];

    [TestMethod]
    public async Task AuthorizationPinsAndStageBudgetsAreClosed()
    {
        Assert.AreEqual(29, MaximumAttempts());
        Assert.AreEqual(24, MaximumPrimaries());
        Assert.AreEqual(5, MaximumRetries());
        Assert.AreEqual(1, StagePrimaryCount("diagnostic"));
        Assert.AreEqual(6, StagePrimaryCount("targeted"));
        Assert.AreEqual(17, StagePrimaryCount("full"));
        Assert.IsTrue(CanEnterStage("targeted", new Authorization { DiagnosticCaptured = false }));
        Assert.IsFalse(CanEnterStage("full", new Authorization { TargetedPassed = false }));
        Assert.IsTrue(CanEnterStage("full", new Authorization { TargetedPassed = true }));
        var approved = TestAuthorization("diagnostic");
        ValidateAuthorization(approved, "diagnostic", DateTimeOffset.UtcNow);
        Assert.ThrowsExactly<InvalidOperationException>(() => ValidateAuthorization(approved, "targeted", DateTimeOffset.UtcNow));
        Assert.ThrowsExactly<InvalidOperationException>(() => ValidateAuthorization(new Authorization { Stage = "diagnostic", Model = "other" }, "diagnostic", DateTimeOffset.UtcNow));
        Assert.ThrowsExactly<InvalidOperationException>(() => ValidateAuthorization(new Authorization { Stage = "diagnostic", Model = GeminiRealQueryParser.Model, PromptSha256 = "wrong", SchemaSha256 = SchemaSha, PlanSha256 = PlanSha, JournalPath = Path.Combine(JournalDirectory, JournalName("diagnostic")) }, "diagnostic", DateTimeOffset.UtcNow));
        await ValidateFrozenPlanHashAsync();
        Assert.IsTrue(IsCredentialSafe("structured JSON only", "API-secret"));
        Assert.IsFalse(IsCredentialSafe("contains API-secret", "API-secret"));
        Assert.IsTrue(CanFitStageBudget(0, 0, "diagnostic"));
        Assert.IsFalse(CanFitStageBudget(24, 28, "diagnostic"));
        Assert.AreEqual(0, StageRetryLimit("diagnostic"));
        Assert.AreEqual(2, StageRetryLimit("targeted"));
        Assert.AreEqual(3, StageRetryLimit("full"));
        var existingJournal = Path.Combine(Path.GetTempPath(), $"stage-journal-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(existingJournal, "preserve");
            Assert.ThrowsExactly<InvalidOperationException>(() => EnsureNewStageJournal(existingJournal));
            Assert.AreEqual("preserve", File.ReadAllText(existingJournal));
        }
        finally { if (File.Exists(existingJournal)) File.Delete(existingJournal); }
        var shape = SummarizeEnvelope("""{"type":"query","languageCheck":"match","alertCode":null,"query":{}}""");
        Assert.AreEqual("query", shape.Type);
        Assert.AreEqual("match", shape.LanguageCheck);
        Assert.AreEqual("object", shape.QueryKind);
        var invalidShape = SummarizeEnvelope("""{"type":"do not persist","languageCheck":"bad","alertCode":"unapproved","query":"raw"}""");
        Assert.AreEqual("invalid", invalidShape.Type);
        Assert.AreEqual("invalid", invalidShape.LanguageCheck);
        Assert.AreEqual("invalid", invalidShape.AlertCode);
        Assert.AreEqual("other", invalidShape.QueryKind);
        Assert.AreEqual("diagnostic-journal.json", JournalName("diagnostic"));
        Assert.AreEqual("targeted-journal.json", JournalName("targeted"));
        Assert.AreEqual("full-journal.json", JournalName("full"));
        Assert.ThrowsExactly<InvalidOperationException>(() => JournalName("other"));
        Assert.IsTrue(IsRetryable("PROVIDER_UNAVAILABLE", null, "timeout", null));
        Assert.IsTrue(IsRetryable("PROVIDER_UNAVAILABLE", null, "network", null));
        Assert.IsTrue(IsRetryable("PROVIDER_UNAVAILABLE", 503, "http-status", null));
        Assert.IsFalse(IsRetryable("PROVIDER_UNAVAILABLE", 429, "http-status", null));
        Assert.IsFalse(IsRetryable("PROVIDER_UNAVAILABLE", 400, "http-status", null));
        Assert.IsFalse(IsRetryable("PARSER_INVALID_RESPONSE", 200, "malformed", null));
        Assert.IsFalse(IsRetryable("PROVIDER_UNAVAILABLE", 503, "http-status", "RESOURCE_EXHAUSTED"));
        Assert.IsFalse(IsRetryable("PROVIDER_UNAVAILABLE", null, "other", null));
    }

    [TestMethod]
    public void PromptPinsAreStageSpecificAndCorrectedStagesShareApprovedHash()
    {
        var diagnostic = TestAuthorization("diagnostic");
        var targeted = TestAuthorization("targeted");
        targeted.PromptSha256 = TestCorrectedPromptSha;
        var full = TestAuthorization("full");
        full.PromptSha256 = targeted.PromptSha256;
        ValidateAuthorization(diagnostic, "diagnostic", DateTimeOffset.UtcNow);
        ValidateAuthorization(targeted, "targeted", DateTimeOffset.UtcNow);
        ValidateAuthorization(full, "full", DateTimeOffset.UtcNow);
        Assert.AreEqual(targeted.PromptSha256, full.PromptSha256);
        Assert.ThrowsExactly<InvalidOperationException>(() => ValidateAuthorization(new Authorization { Stage = "diagnostic", Model = GeminiRealQueryParser.Model, PromptSha256 = TestCorrectedPromptSha, SchemaSha256 = SchemaSha, PlanSha256 = PlanSha, JournalPath = Path.Combine(JournalDirectory, JournalName("diagnostic")) }, "diagnostic", DateTimeOffset.UtcNow));
    }

    [TestMethod]
    public void AuthorizationJsonReadsCamelCaseAndWebWritesSameContract()
    {
        var json = JsonSerializer.Serialize(TestAuthorization("targeted"), JsonSerializerOptions.Web);
        var deserialized = JsonSerializer.Deserialize<Authorization>(json, AuthorizationJsonOptions())!;
        Assert.AreEqual("targeted", deserialized.Stage);
        Assert.AreEqual(GeminiRealQueryParser.Model, deserialized.Model);
        Assert.AreEqual(TestCorrectedPromptSha, deserialized.PromptSha256);
        ValidateAuthorization(deserialized, "targeted", DateTimeOffset.UtcNow);
    }

    [TestMethod]
    public void ExhaustedRetryBudgetStillAllowsPrimaryButBlocksRetry()
    {
        Assert.IsTrue(CanStartAttempt(total: 28, primary: 23, retries: 5, isRetry: false));
        Assert.IsFalse(CanStartAttempt(total: 28, primary: 23, retries: 5, isRetry: true));
        Assert.IsFalse(CanStartAttempt(total: 29, primary: 24, retries: 5, isRetry: false));
        Assert.IsFalse(CanStartAttempt(total: 28, primary: 24, retries: 5, isRetry: false));
        var inconsistent = TestAuthorizationWithCounts("diagnostic", total: 2, primary: 1, retries: 0);
        Assert.ThrowsExactly<InvalidOperationException>(() => ValidateAuthorization(inconsistent, "diagnostic", DateTimeOffset.UtcNow));
    }

    [TestMethod]
    public void FullStageRequiresValidatedTargetedJournalRatherThanBooleanAlone()
    {
        var auth = TestAuthorization("full"); auth.TargetedPassed = true;
        var journal = PassedTargetedJournal(auth);
        Assert.IsTrue(HasPassedTargetedJournal(journal, auth));
        journal.Attempts[0].Type = "query";
        Assert.IsFalse(HasPassedTargetedJournal(journal, auth));
        journal = PassedTargetedJournal(auth); journal.PromptSha256 = "other";
        Assert.IsFalse(HasPassedTargetedJournal(journal, auth));
        journal = PassedTargetedJournal(auth); journal.Attempts.RemoveAt(0);
        Assert.IsFalse(HasPassedTargetedJournal(journal, auth));
        journal = PassedTargetedJournal(auth); journal.Retries = 3;
        Assert.IsFalse(HasPassedTargetedJournal(journal, auth));
    }

    [TestMethod]
    public void TargetedSemanticCasesRequireMeaningfulQueryWithoutHardFilters()
    {
        var semantic = new RealParserResult("query", new RealParsedQuery(new RealHardFilters(), "Brad Pitt films"), LanguageCheck: "match");
        var evidence = new GeminiParserEvidenceResult(semantic, "{}", TimeSpan.Zero);
        Validate(TargetedCases[4], evidence);
        Validate(TargetedCases[5], new GeminiParserEvidenceResult(new RealParserResult("query", new RealParsedQuery(new RealHardFilters(), "films directed by Christopher Nolan"), LanguageCheck: "match"), "{}", TimeSpan.Zero));
        Assert.ThrowsExactly<AssertFailedException>(() => Validate(TargetedCases[4], new GeminiParserEvidenceResult(new RealParserResult("query", new RealParsedQuery(new RealHardFilters(), " "), LanguageCheck: "match"), "{}", TimeSpan.Zero)));
        Assert.ThrowsExactly<AssertFailedException>(() => Validate(TargetedCases[5], new GeminiParserEvidenceResult(new RealParserResult("query", new RealParsedQuery(new RealHardFilters(YearMax: 2000), "films by Christopher Nolan"), LanguageCheck: "match"), "{}", TimeSpan.Zero)));
    }

    [TestMethod]
    public async Task GatedPreFixDiagnosticRunsExactlyOnce()
    {
        await RunAuthorizedStageAsync("diagnostic");
    }

    [TestMethod]
    public async Task GatedTargetedSixCasesRequirePassedDiagnostic()
    {
        await RunAuthorizedStageAsync("targeted");
    }

    [TestMethod]
    public async Task GatedFrozenSeventeenCasesRequirePassedTargetedStage()
    {
        await RunAuthorizedStageAsync("full");
    }

    private static async Task RunAuthorizedStageAsync(string stage)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(Gate), "true", StringComparison.Ordinal))
            Assert.Inconclusive($"Live provider calls are disabled. MAIN must explicitly set {Gate}=true.");
        var authorization = await ReadAuthorizationAsync(stage);
        var key = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        if (string.IsNullOrWhiteSpace(key)) Assert.Inconclusive("MAIN must provide the process credential for its explicitly authorized stage.");
        var promptPath = Path.Combine(AppContext.BaseDirectory, "RealProviders", "query-parser-v5.md");
        var schemaPath = Path.Combine(AppContext.BaseDirectory, "RealProviders", "query-parser-v5.schema.json");
        var prompt = await File.ReadAllTextAsync(promptPath);
        var schema = await File.ReadAllTextAsync(schemaPath);
        var promptSha = Hash(prompt);
        if (stage == "diagnostic") Assert.AreEqual(DiagnosticPromptSha, promptSha);
        else Assert.AreEqual(authorization.PromptSha256, promptSha, "The stage must use MAIN's approved corrected prompt hash.");
        Assert.AreEqual(SchemaSha, Hash(schema));

        var cases = stage switch
        {
            "diagnostic" => new[] { new SmokeCase("DIAG-01", "en", "film without Brad Pitt", "diagnostic", null, null) },
            "targeted" => TargetedCases,
            "full" => await ReadFrozenCasesAsync(),
            _ => throw new InvalidOperationException("Unknown live stage.")
        };
        var journalPath = Path.Combine(JournalDirectory, JournalName(stage));
        if (stage == "full")
        {
            var targetedJournalPath = Path.Combine(JournalDirectory, JournalName("targeted"));
            var targetedJournal = await ReadStageJournalAsync(targetedJournalPath);
            if (!HasPassedTargetedJournal(targetedJournal, authorization))
                throw new InvalidOperationException("Full stage requires a passed 6/6 targeted journal pinned to the same model, prompt, schema and plan.");
        }
        using var runLock = AcquireLock(journalPath);
        EnsureNewStageJournal(journalPath);
        var journal = new StageJournal(stage, authorization, cases.Length);
        await WriteJournalAsync(journalPath, journal);
        var logger = new SanitizedLogger<GeminiRealQueryParser>();
        var transport = new EvidenceHandler(new SocketsHttpHandler { AllowAutoRedirect = false });
        using var http = new HttpClient(transport, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        var parser = new GeminiRealQueryParser(http, key, prompt, schema, new RealParsedQueryValidator(), logger, isDevelopment: true, languageAware: true);
        var previousStart = authorization.LastRequestStartedUtc;
        DateTimeOffset? deadline = authorization.FirstRequestStartedUtc?.AddMinutes(MaxMinutes);
        using var runDeadline = new CancellationTokenSource();
        if (deadline is { } initialDeadline)
        {
            var remaining = initialDeadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) throw new InvalidOperationException("Cumulative 20-minute deadline expired before stage execution.");
            runDeadline.CancelAfter(remaining);
        }
        try
        {
            foreach (var testCase in cases)
            {
                if (!CanEnterStage(stage, authorization)) throw new InvalidOperationException("The prior stage has not been explicitly approved by MAIN.");
                var retryIndex = 0;
                while (true)
                {
                    var utcNow = DateTimeOffset.UtcNow;
                    if (deadline is { } runLimit && utcNow >= runLimit || !CanStartAttempt(authorization.TotalAttempts, authorization.PrimaryCount, authorization.RetryCount, retryIndex > 0))
                        throw new InvalidOperationException("Cumulative Phase 5 request budget or first-call deadline exhausted.");
                    if (previousStart is { } previous)
                    {
                        var spacing = MinimumSpacing - (utcNow - previous);
                        if (spacing > TimeSpan.Zero) await Task.Delay(spacing, runDeadline.Token);
                    }
                    var requestStarted = DateTimeOffset.UtcNow;
                    if (deadline is { } activeDeadline && requestStarted >= activeDeadline) throw new InvalidOperationException("Cumulative 20-minute deadline expired before request.");
                    previousStart = requestStarted;
                    authorization.FirstRequestStartedUtc ??= requestStarted;
                    if (deadline is null)
                    {
                        deadline = authorization.FirstRequestStartedUtc.Value.AddMinutes(MaxMinutes);
                        var remaining = deadline.Value - DateTimeOffset.UtcNow;
                        if (remaining <= TimeSpan.Zero) throw new InvalidOperationException("Cumulative 20-minute deadline expired before first request.");
                        runDeadline.CancelAfter(remaining);
                    }
                    authorization.LastRequestStartedUtc = requestStarted;
                    authorization.TotalAttempts++;
                    if (retryIndex == 0) authorization.PrimaryCount++; else { authorization.RetryCount++; journal.Retries++; }
                    journal.Attempts.Add(new AttemptEvidence(testCase.Id, retryIndex, requestStarted));
                    await WriteJournalAsync(journalPath, journal);
                    await WriteAuthorizationAsync(authorization);
                    var evidence = journal.Attempts[^1];
                    var timer = Stopwatch.StartNew();
                    transport.Last = null;
                    logger.Clear();
                    try
                    {
                        var parsed = await parser.ParseWithEvidenceAsync(testCase.Language, testCase.Message, runDeadline.Token);
                        timer.Stop();
                        evidence.EnvelopeShape = SummarizeEnvelope(transport.LastStructuredText);
                        CaptureValidated(evidence, parsed);
                        evidence.Http = transport.Last;
                        evidence.Cause = logger.LastCause;
                        evidence.WallMilliseconds = timer.ElapsedMilliseconds;
                        if (stage == "diagnostic")
                        {
                            await CaptureDiagnosticRawAsync(transport.LastStructuredText, key);
                            journal.DiagnosticObservation = $"validated:{parsed.Result.Type}:{parsed.Result.AlertCode ?? "none"}";
                            journal.Outcome = "diagnostic_captured";
                            await WriteJournalAsync(journalPath, journal);
                            break;
                        }
                        Validate(testCase, parsed);
                        await WriteJournalAsync(journalPath, journal);
                        break;
                    }
                    catch (RealProviderException ex)
                    {
                        timer.Stop(); evidence.ErrorCode = ex.Code; evidence.Http = transport.Last;
                        evidence.EnvelopeShape = SummarizeEnvelope(transport.LastStructuredText);
                        evidence.Cause = logger.LastCause; evidence.WallMilliseconds = timer.ElapsedMilliseconds;
                        if (stage == "diagnostic" && ex.Code == "PARSER_INVALID_RESPONSE" && transport.LastStructuredText is not null)
                        {
                            await CaptureDiagnosticRawAsync(transport.LastStructuredText, key);
                            journal.DiagnosticObservation = "PARSER_INVALID_RESPONSE";
                            journal.Outcome = "diagnostic_captured";
                            await WriteJournalAsync(journalPath, journal);
                            break;
                        }
                        await WriteJournalAsync(journalPath, journal);
                        if (!IsRetryable(ex.Code, transport.Last?.Status, logger.LastCause, transport.Last?.ProviderStatus) || authorization.RetryCount >= GlobalMaxRetries ||
                            retryIndex >= StageRetryLimit(stage) || journal.Retries >= StageRetryLimit(stage)) throw;
                        retryIndex++;
                        var wait = RetryDelay(transport.Last?.RetryAfter, retryIndex);
                        if (deadline is { } retryDeadline && DateTimeOffset.UtcNow + wait >= retryDeadline) throw new InvalidOperationException("Retry delay would exceed first-call deadline.");
                        await Task.Delay(wait, runDeadline.Token);
                    }
                }
            }
            if (stage != "diagnostic" || journal.DiagnosticObservation is null) journal.Outcome = "passed";
        }
        catch
        {
            journal.Outcome = "failed_or_stopped";
            throw;
        }
        finally
        {
            await WriteJournalAsync(journalPath, journal);
            authorization.LastRequestStartedUtc = previousStart;
            authorization.LastStage = stage;
            await WriteAuthorizationAsync(authorization);
        }
    }

    private static async Task<Authorization> ReadAuthorizationAsync(string requiredStage)
    {
        if (!File.Exists(AuthPath)) throw new InvalidOperationException("MAIN-approved authorization file is required.");
        var auth = JsonSerializer.Deserialize<Authorization>(await File.ReadAllTextAsync(AuthPath), AuthorizationJsonOptions()) ?? throw new InvalidOperationException("Authorization JSON is invalid.");
        ValidateAuthorization(auth, requiredStage, DateTimeOffset.UtcNow);
        await ValidateFrozenPlanHashAsync();
        return auth;
    }

    private static void ValidateAuthorization(Authorization auth, string requiredStage, DateTimeOffset now)
    {
        var promptPinValid = requiredStage == "diagnostic"
            ? auth.PromptSha256 == DiagnosticPromptSha
            : auth.PromptSha256 != DiagnosticPromptSha && auth.PromptSha256.Length == 64 && auth.PromptSha256.All(Uri.IsHexDigit);
        if (auth.Stage != requiredStage || auth.Model != GeminiRealQueryParser.Model || !promptPinValid || auth.SchemaSha256 != SchemaSha || auth.PlanSha256 != PlanSha)
            throw new InvalidOperationException("Authorization stage/model/prompt/schema/plan pins do not match.");
        if (auth.TotalAttempts < 0 || auth.TotalAttempts >= GlobalMaxAttempts || auth.PrimaryCount < 0 || auth.PrimaryCount > GlobalMaxPrimaries || auth.RetryCount < 0 || auth.RetryCount > GlobalMaxRetries)
            throw new InvalidOperationException("Authorization cumulative counters are exhausted or invalid.");
        if (auth.TotalAttempts != auth.PrimaryCount + auth.RetryCount)
            throw new InvalidOperationException("Authorization cumulative attempts must equal primaries plus retries.");
        if (!CanFitStageBudget(auth.PrimaryCount, auth.TotalAttempts, requiredStage))
            throw new InvalidOperationException("Authorization lacks cumulative budget for all stage primaries.");
        if (auth.FirstRequestStartedUtc is { } first && now >= first.AddMinutes(MaxMinutes))
            throw new InvalidOperationException("Authorization first-request deadline expired.");
        var expectedJournal = Path.Combine(JournalDirectory, JournalName(requiredStage));
        if (!Path.GetFullPath(auth.JournalPath).Equals(Path.GetFullPath(expectedJournal), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Authorization journal path is not the stage-specific fixed path.");
    }

    private static bool CanEnterStage(string stage, Authorization authorization) => stage switch
    {
        "diagnostic" or "targeted" => true,
        "full" => authorization.TargetedPassed == true, _ => false
    };
    private static int StagePrimaryCount(string stage) => stage switch { "diagnostic" => 1, "targeted" => 6, "full" => 17, _ => 0 };
    private static int MaximumAttempts() => GlobalMaxAttempts;
    private static int MaximumPrimaries() => GlobalMaxPrimaries;
    private static int MaximumRetries() => GlobalMaxRetries;
    private static bool CanStartAttempt(int total, int primary, int retries, bool isRetry) =>
        total < GlobalMaxAttempts && (isRetry ? retries < GlobalMaxRetries : primary < GlobalMaxPrimaries);
    private static bool CanFitStageBudget(int primaryCount, int totalAttempts, string stage) =>
        primaryCount + StagePrimaryCount(stage) <= GlobalMaxPrimaries && totalAttempts + StagePrimaryCount(stage) <= GlobalMaxAttempts;
    private static void EnsureNewStageJournal(string path)
    {
        if (File.Exists(path)) throw new InvalidOperationException("This stage journal already exists; automatic reruns are prohibited.");
    }
    private static bool IsCredentialSafe(string structuredText, string key) => !string.IsNullOrEmpty(key) && !structuredText.Contains(key, StringComparison.Ordinal);
    private static Authorization TestAuthorization(string stage) => new()
    {
        Stage = stage, Model = GeminiRealQueryParser.Model, PromptSha256 = stage == "diagnostic" ? DiagnosticPromptSha : TestCorrectedPromptSha, SchemaSha256 = SchemaSha, PlanSha256 = PlanSha,
        JournalPath = Path.Combine(JournalDirectory, JournalName(stage))
    };

    private static JsonSerializerOptions AuthorizationJsonOptions() => new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    private static async Task<StageJournal> ReadStageJournalAsync(string path)
    {
        if (!File.Exists(path)) throw new InvalidOperationException("The targeted stage journal is missing.");
        return JsonSerializer.Deserialize<StageJournal>(await File.ReadAllTextAsync(path), AuthorizationJsonOptions())
            ?? throw new InvalidOperationException("The targeted stage journal could not be parsed.");
    }

    private static bool HasPassedTargetedJournal(StageJournal journal, Authorization authorization)
    {
        if (journal.Outcome != "passed" || journal.Stage != "targeted" || journal.Model != GeminiRealQueryParser.Model ||
            journal.PromptSha256 != authorization.PromptSha256 || journal.SchemaSha256 != authorization.SchemaSha256 || journal.PlanSha256 != authorization.PlanSha256 ||
            journal.ExpectedPrimaryCount != 6 || journal.Attempts.Count is < 6 or > 8 || journal.Retries is < 0 or > 2) return false;
        var expected = TargetedCases.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var groups = journal.Attempts.GroupBy(item => item.CaseId, StringComparer.Ordinal).ToArray();
        if (groups.Length != 6 || groups.Any(group => !expected.ContainsKey(group.Key))) return false;
        foreach (var group in groups)
        {
            var last = group.OrderBy(item => item.RetryIndex).Last();
            var testCase = expected[group.Key];
            if (last.ErrorCode is not null || last.Http?.Status != 200 || last.ProviderMilliseconds is null or <= 0 ||
                last.Type != testCase.ExpectedType || last.LanguageCheck != testCase.ExpectedLanguageCheck || last.AlertCode != testCase.ExpectedAlertCode) return false;
            if (testCase.ExpectedType == "query" && (string.IsNullOrWhiteSpace(last.SemanticQuery) || last.HardFiltersJson is null || !HasNoActiveHardFilters(last.HardFiltersJson))) return false;
        }
        return true;
    }

    private static bool HasNoActiveHardFilters(string json)
    {
        try
        {
            var filters = JsonSerializer.Deserialize<RealHardFilters>(json);
            return filters is not null && filters.YearMin is null && filters.YearMax is null && filters.RuntimeMin is null && filters.RuntimeMax is null &&
                filters.Genres is null && filters.RatingMin is null && filters.OriginalLanguage is null && filters.RatingOperator is null;
        }
        catch (JsonException) { return false; }
    }

    private static StageJournal PassedTargetedJournal(Authorization authorization)
    {
        var journal = new StageJournal("targeted", authorization, 6) { Outcome = "passed" };
        foreach (var testCase in TargetedCases)
        {
            var query = testCase.ExpectedType == "query";
            journal.Attempts.Add(new AttemptEvidence(testCase.Id, 0, DateTimeOffset.UtcNow)
            {
                Type = testCase.ExpectedType, LanguageCheck = testCase.ExpectedLanguageCheck, AlertCode = testCase.ExpectedAlertCode,
                Http = new AttemptHttp(200, null, null, null), ProviderMilliseconds = 1,
                SemanticQuery = query ? "validated semantic intent" : null, HardFiltersJson = query ? "{}" : null
            });
        }
        return journal;
    }

    private static Authorization TestAuthorizationWithCounts(string stage, int total, int primary, int retries)
    {
        var authorization = TestAuthorization(stage);
        authorization.TotalAttempts = total; authorization.PrimaryCount = primary; authorization.RetryCount = retries;
        return authorization;
    }

    private static async Task ValidateFrozenPlanHashAsync()
    {
        var frozenPlan = await File.ReadAllBytesAsync(PlanPath);
        if (Hash(frozenPlan) != PlanSha) throw new InvalidOperationException("Frozen 17-case plan hash mismatch.");
    }

    private static async Task<SmokeCase[]> ReadFrozenCasesAsync()
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(PlanPath));
        var cases = document.RootElement.GetProperty("cases").Deserialize<SmokeCase[]>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (cases is null || cases.Length != 17 || cases.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() != 17)
            throw new InvalidOperationException("Frozen plan must contain the unchanged 17 unique case IDs.");
        return cases;
    }

    private static readonly SmokeCase[] TargetedCases =
    [
        new("TF-01", "en", "film without Brad Pitt", "alert", "match", "UNSUPPORTED_REQUEST"),
        new("TF-02", "sr", "Film bez Christophera Nolana", "alert", "match", "UNSUPPORTED_REQUEST"),
        new("TF-03", "sr", "Филм без Бреда Пита", "alert", "match", "UNSUPPORTED_REQUEST"),
        new("TF-04", "en", "A film other than Fight Club", "alert", "match", "UNSUPPORTED_REQUEST"),
        new("TF-05", "en", "A film starring Brad Pitt", "query", "match", null),
        new("TF-06", "sr", "Film koji je režirao Christopher Nolan", "query", "match", null)
    ];

    private static void Validate(SmokeCase testCase, GeminiParserEvidenceResult evidence)
    {
        Assert.AreEqual(testCase.ExpectedType, evidence.Result.Type, testCase.Id);
        if (testCase.ExpectedLanguageCheck is not null) Assert.AreEqual(testCase.ExpectedLanguageCheck, evidence.Result.LanguageCheck, testCase.Id);
        Assert.AreEqual(testCase.ExpectedAlertCode, evidence.Result.AlertCode, testCase.Id);
        if (testCase.ExpectedType == "query")
        {
            Assert.IsNotNull(evidence.Result.Query, testCase.Id);
            if (testCase.Id == "P5-13" || testCase.Id == "P5-14")
            {
                var filters = evidence.Result.Query!.HardFilters;
                Assert.IsTrue(HasGenre(filters.Genres, "Comedy"));
                Assert.AreEqual(2000, filters.YearMin); Assert.AreEqual(2009, filters.YearMax);
                Assert.IsNull(evidence.Result.Query.SemanticQuery);
            }
            if (testCase.Id == "P5-15")
            {
                Assert.IsTrue(HasGenre(evidence.Result.Query!.HardFilters.Genres, "Sci-Fi"));
                Assert.AreEqual(4m, evidence.Result.Query!.HardFilters.RatingMin);
                Assert.AreEqual("gt", evidence.Result.Query.HardFilters.RatingOperator);
                using var doc = JsonDocument.Parse(evidence.ProviderChecklistJson);
                var rating = doc.RootElement.GetProperty("checklist").GetProperty("rating");
                Assert.AreEqual(8m, rating.GetProperty("value").GetDecimal()); Assert.AreEqual("gt", rating.GetProperty("operator").GetString());
            }
            if (testCase.Id == "P5-16")
            {
                Assert.IsTrue(HasGenre(evidence.Result.Query!.HardFilters.Genres, "Sci-Fi"));
                Assert.AreEqual(2008, evidence.Result.Query.HardFilters.YearMax);
                Assert.IsFalse(string.IsNullOrWhiteSpace(evidence.Result.Query.SemanticQuery));
                StringAssert.Contains(evidence.Result.Query.SemanticQuery!, "Brad Pitt");
            }
            if (testCase.Id is "TF-05" or "TF-06")
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(evidence.Result.Query!.SemanticQuery), testCase.Id);
                Assert.IsTrue(HasNoActiveHardFilters(JsonSerializer.Serialize(evidence.Result.Query.HardFilters)), testCase.Id);
            }
        }
    }

    private static void CaptureValidated(AttemptEvidence evidence, GeminiParserEvidenceResult parsed)
    {
        evidence.Type = parsed.Result.Type; evidence.LanguageCheck = parsed.Result.LanguageCheck; evidence.AlertCode = parsed.Result.AlertCode;
        evidence.ChecklistJson = parsed.ProviderChecklistJson;
        evidence.HardFiltersJson = parsed.Result.Query is null ? null : JsonSerializer.Serialize(parsed.Result.Query.HardFilters);
        evidence.SemanticQuery = parsed.Result.Query?.SemanticQuery;
        evidence.ProviderMilliseconds = (long)parsed.ProviderDuration.TotalMilliseconds;
    }

    private static bool HasGenre(RealGenreFilter? genres, string expected) =>
        genres?.All?.Contains(expected, StringComparer.OrdinalIgnoreCase) == true || genres?.Any?.Contains(expected, StringComparer.OrdinalIgnoreCase) == true;

    private static async Task CaptureDiagnosticRawAsync(string? structuredText, string key)
    {
        if (structuredText is null) throw new InvalidOperationException("Diagnostic response did not expose the extracted structured text.");
        if (structuredText.Contains(key, StringComparison.Ordinal)) throw new InvalidOperationException("Diagnostic output failed exact credential exclusion check.");
        var path = Path.Combine(JournalDirectory, "diagnostic-structured-output.json");
        if (File.Exists(path)) throw new InvalidOperationException("Diagnostic raw-output report exists; refusing overwrite.");
        await WriteAtomicAsync(path, JsonSerializer.Serialize(new { capturedUtc = DateTimeOffset.UtcNow, structuredText }));
    }

    private static EnvelopeShape SummarizeEnvelope(string? structuredText)
    {
        if (structuredText is null) return new("invalid", "invalid", "invalid", "invalid");
        try
        {
            using var doc = JsonDocument.Parse(structuredText);
            var root = doc.RootElement;
            var type = SafeEnum(root, "type", false, "query", "alert");
            var language = SafeEnum(root, "languageCheck", false, "match", "mismatch", "unclear");
            var alert = SafeEnum(root, "alertCode", true, "UNSUPPORTED_REQUEST", "LANGUAGE_MISMATCH", "QUERY_UNCLEAR", "NOT_MOVIE_REQUEST");
            var queryKind = !root.TryGetProperty("query", out var query) || query.ValueKind == JsonValueKind.Null ? "null" : query.ValueKind == JsonValueKind.Object ? "object" : "other";
            return new(type, language, alert, queryKind);
        }
        catch (JsonException) { return new("invalid", "invalid", "invalid", "invalid"); }
    }

    private static string SafeEnum(JsonElement root, string property, bool allowNull, params string[] allowed)
    {
        if (!root.TryGetProperty(property, out var value)) return "invalid";
        if (value.ValueKind == JsonValueKind.Null) return allowNull ? "null" : "invalid";
        if (value.ValueKind != JsonValueKind.String) return "invalid";
        var candidate = value.GetString();
        return candidate is not null && allowed.Contains(candidate, StringComparer.Ordinal) ? candidate : "invalid";
    }

    private static bool IsRetryable(string? code, int? status, string? cause, string? providerStatus)
    {
        if (code != "PROVIDER_UNAVAILABLE" || status == 429 || providerStatus == "RESOURCE_EXHAUSTED") return false;
        if (status is >= 400 and <= 499) return false;
        return status is >= 500 and <= 599 || status is null && cause is "timeout" or "network";
    }

    private static TimeSpan RetryDelay(TimeSpan? retryAfter, int retryIndex)
    {
        var fallback = Backoff[Math.Clamp(retryIndex - 1, 0, Backoff.Length - 1)];
        return retryAfter is { } requested && requested > fallback ? requested : fallback;
    }

    private static int StageRetryLimit(string stage) => stage switch { "diagnostic" => 0, "targeted" => 2, "full" => 3, _ => 0 };
    private static string JournalName(string stage) => stage switch { "diagnostic" => "diagnostic-journal.json", "targeted" => "targeted-journal.json", "full" => "full-journal.json", _ => throw new InvalidOperationException("Unknown stage.") };
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value));

    private static FileStream AcquireLock(string journalPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
        return new FileStream(journalPath + ".lock", FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
    }

    private static async Task WriteAuthorizationAsync(Authorization authorization) => await WriteAtomicAsync(AuthPath, JsonSerializer.Serialize(authorization, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
    private static async Task WriteJournalAsync(string path, StageJournal journal) => await WriteAtomicAsync(path, JsonSerializer.Serialize(journal, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
    private static async Task WriteAtomicAsync(string path, string content)
    {
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, content);
        File.Move(temp, path, overwrite: true);
    }

    private sealed class EvidenceHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public AttemptHttp? Last { get; set; }
        public string? LastStructuredText { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastStructuredText = null;
            var response = await base.SendAsync(request, cancellationToken);
            var retry = response.Headers.RetryAfter;
            TimeSpan? retryAfter = retry?.Delta;
            if (retryAfter is null && retry?.Date is { } date) retryAfter = date - DateTimeOffset.UtcNow;
            Last = new AttemptHttp((int)response.StatusCode, retryAfter, null, null);
            if (response.IsSuccessStatusCode)
            {
                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                var envelope = Encoding.UTF8.GetString(bytes);
                LastStructuredText = ExtractStructuredText(envelope);
                response.Content = new ByteArrayContent(bytes);
                response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            }
            else
            {
                var metadata = await ReadProviderErrorAsync(response.Content, cancellationToken);
                Last = Last with { ProviderStatus = metadata.Status, ProviderCode = metadata.Code };
            }
            return response;
        }
        private static string? ExtractStructuredText(string envelope)
        {
            try
            {
                using var doc = JsonDocument.Parse(envelope);
                return doc.RootElement.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString();
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or IndexOutOfRangeException) { return null; }
        }
        private static async Task<(string? Status, int? Code)> ReadProviderErrorAsync(HttpContent content, CancellationToken token)
        {
            try
            {
                var bytes = await content.ReadAsByteArrayAsync(token);
                if (bytes.Length > 16 * 1024) return (null, null);
                using var doc = JsonDocument.Parse(bytes);
                if (!doc.RootElement.TryGetProperty("error", out var error)) return (null, null);
                var status = error.TryGetProperty("status", out var s) ? s.GetString() : null;
                var code = error.TryGetProperty("code", out var c) && c.TryGetInt32(out var n) ? n : (int?)null;
                var allowed = status is "UNAVAILABLE" or "RESOURCE_EXHAUSTED" or "INVALID_ARGUMENT" or "PERMISSION_DENIED" or "UNAUTHENTICATED";
                return (allowed ? status : null, code);
            }
            catch (Exception ex) when (ex is JsonException or OperationCanceledException or IOException or InvalidOperationException) { return (null, null); }
        }
    }

    private sealed class SanitizedLogger<T> : ILogger<T>
    {
        public string? LastCause { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Clear() => LastCause = null;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (state is not IEnumerable<KeyValuePair<string, object?>> values) return;
            var cause = values.FirstOrDefault(item => item.Key == "Cause").Value as string;
            LastCause = cause switch
            {
                "timeout" => "timeout", "network exception HttpRequestException" => "network", "malformed response/validation" => "malformed",
                "other sanitized adapter failure" => "other", _ when cause?.StartsWith("HTTP status ", StringComparison.Ordinal) == true => "http-status", _ => null
            };
        }
    }

    private sealed class Authorization
    {
        public string Stage { get; set; } = ""; public string Model { get; set; } = ""; public string PromptSha256 { get; set; } = ""; public string SchemaSha256 { get; set; } = ""; public string PlanSha256 { get; set; } = ""; public string JournalPath { get; set; } = "";
        public int TotalAttempts { get; set; } public int PrimaryCount { get; set; } public int RetryCount { get; set; }
        public DateTimeOffset? FirstRequestStartedUtc { get; set; } public DateTimeOffset? LastRequestStartedUtc { get; set; }
        public string? LastStage { get; set; } public bool? DiagnosticCaptured { get; set; } public bool? TargetedPassed { get; set; }
    }
    private sealed record SmokeCase(string Id, string Language, string Message, string ExpectedType, string? ExpectedLanguageCheck, string? ExpectedAlertCode);
    private sealed record AttemptHttp(int Status, TimeSpan? RetryAfter, string? ProviderStatus, int? ProviderCode);
    private sealed record EnvelopeShape(string Type, string LanguageCheck, string AlertCode, string QueryKind);
    private sealed class StageJournal
    {
        public StageJournal() { }
        public StageJournal(string stage, Authorization authorization, int expectedCount)
        { Stage = stage; Model = authorization.Model; PromptSha256 = authorization.PromptSha256; SchemaSha256 = authorization.SchemaSha256; PlanSha256 = authorization.PlanSha256; FirstRequestStartedUtc = authorization.FirstRequestStartedUtc; ExpectedPrimaryCount = expectedCount; }
        public string Stage { get; set; } = ""; public string Model { get; set; } = ""; public string PromptSha256 { get; set; } = ""; public string SchemaSha256 { get; set; } = ""; public string PlanSha256 { get; set; } = "";
        public DateTimeOffset? FirstRequestStartedUtc { get; set; } public int ExpectedPrimaryCount { get; set; } public int Retries { get; set; } public string Outcome { get; set; } = "running"; public List<AttemptEvidence> Attempts { get; set; } = [];
        public string? DiagnosticObservation { get; set; }
    }
    private sealed class AttemptEvidence
    {
        public AttemptEvidence() { }
        public AttemptEvidence(string caseId, int retryIndex, DateTimeOffset startedUtc) { CaseId = caseId; RetryIndex = retryIndex; StartedUtc = startedUtc; }
        public string CaseId { get; set; } = ""; public int RetryIndex { get; set; } public DateTimeOffset StartedUtc { get; set; } public long? WallMilliseconds { get; set; } public long? ProviderMilliseconds { get; set; } public AttemptHttp? Http { get; set; } public string? Cause { get; set; } public string? ErrorCode { get; set; } public EnvelopeShape? EnvelopeShape { get; set; } public string? Type { get; set; } public string? LanguageCheck { get; set; } public string? AlertCode { get; set; } public string? ChecklistJson { get; set; } public string? HardFiltersJson { get; set; } public string? SemanticQuery { get; set; }
    }
}
