using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using CineKros.Api.RealProviders;
using CineKros.Api.Search;
using Microsoft.Extensions.Logging;

namespace CineKros.Api.Tests.RealProviders;

[TestClass]
public sealed class Phase5LiveParserSmokeTests
{
    private const string GateVariable = "CINEKROS_RUN_SR_P5_LIVE_PARSER";
    private const string ResumeGateVariable = "CINEKROS_SR_P5_LIVE_RESUME";
    private const string RecoveryGateVariable = "CINEKROS_SR_P5_LIVE_RECOVERY";
    private const string PlanPath = @"D:\Repozitorijum\CineKros\.local\planning\reports\sr-phase-05\live-parser-plan.json";
    private const string JournalPath = @"D:\Repozitorijum\CineKros\.local\planning\reports\sr-phase-05-runtime\live-parser-smoke-evidence.json";
    private const string RecoveryJournalPath = @"D:\Repozitorijum\CineKros\.local\planning\reports\sr-phase-05-runtime\live-parser-recovery-01.json";
    private const string ExpectedPromptSha256 = "EFCDB69530926AFF149A8F9FE6F7740DC7ECDA4D28B035294E8D7CF9DDA6A1D4";
    private const string ExpectedSchemaSha256 = "2C9AAB879BF4072D18868488656239F47AFBD06362257BCA382CF86190440EEC";
    private const int MaxHttpAttempts = 20;
    private const int MaxPrimaryCases = 17;
    private const int MaxTransientRetries = 3;
    private static readonly TimeSpan MaxRunDuration = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan MinimumRequestSpacing = TimeSpan.FromSeconds(5);

    [TestMethod]
    public async Task FrozenLivePlanAndParserAssetsMatchPinnedContract()
    {
        _ = ReadAndValidatePlan();
        var prompt = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "RealProviders", "query-parser-v5.md"));
        var schema = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "RealProviders", "query-parser-v5.schema.json"));
        // Current approved asset; historical live/resume paths keep their original run pin.
        Assert.AreEqual("0AB18CFB00A41E8D6B5E38E9030C7612C8B8602290EB0C2F2CD460CF01497401", Sha256(prompt));
        Assert.AreEqual(ExpectedSchemaSha256, Sha256(schema));
    }

    [TestMethod]
    public void AttemptBudgetEnforcesPrimaryRetryAndTotalCaps()
    {
        var budget = new AttemptBudget(MaxHttpAttempts, MaxPrimaryCases, MaxTransientRetries);
        for (var index = 0; index < MaxPrimaryCases; index++) Assert.IsTrue(budget.TryBegin(primary: true, TimeSpan.Zero));
        for (var index = 0; index < MaxTransientRetries; index++) Assert.IsTrue(budget.TryBegin(primary: false, TimeSpan.Zero));
        Assert.IsFalse(budget.TryBegin(primary: false, TimeSpan.Zero));
        Assert.AreEqual(MaxHttpAttempts, budget.Total);
        Assert.AreEqual(MaxPrimaryCases, budget.Primary);
        Assert.AreEqual(MaxTransientRetries, budget.Retries);
        Assert.IsFalse(budget.TryBegin(primary: true, TimeSpan.Zero));
        Assert.IsFalse(budget.TryBegin(primary: true, MaxRunDuration));
        Assert.AreEqual(TimeSpan.FromSeconds(5), ComputeRetryWait(null, 1));
        Assert.AreEqual(TimeSpan.FromSeconds(10), ComputeRetryWait(null, 2));
        Assert.AreEqual(TimeSpan.FromSeconds(20), ComputeRetryWait(null, 3));
        Assert.AreEqual(TimeSpan.FromSeconds(30), ComputeRetryWait(new ResponseMetadata(503, TimeSpan.FromSeconds(30), null), 1));
    }

    [TestMethod]
    public void RecoveryGateSelectsSeparateJournalAndRejectsResumeCombination()
    {
        Assert.AreEqual(LiveRunMode.Recovery, ResolveLiveMode(standardGate: false, resumeGate: false, recoveryGate: true));
        Assert.AreEqual(RecoveryJournalPath, SelectJournalPath(LiveRunMode.Recovery));
        Assert.ThrowsExactly<InvalidOperationException>(() => ResolveLiveMode(standardGate: true, resumeGate: true, recoveryGate: true));
        Assert.AreEqual(LiveRunMode.Resume, ResolveLiveMode(standardGate: true, resumeGate: true, recoveryGate: false));
        Assert.AreEqual(JournalPath, SelectJournalPath(LiveRunMode.Standard));
    }

    [TestMethod]
    public void RecoveryRetriesOnlyConfirmedTimeoutNetworkAndServerErrors()
    {
        Assert.IsTrue(IsRetryableRecoveryFailure("PROVIDER_UNAVAILABLE", null, "timeout"));
        Assert.IsTrue(IsRetryableRecoveryFailure("PROVIDER_UNAVAILABLE", null, "network"));
        Assert.IsTrue(IsRetryableRecoveryFailure("PROVIDER_UNAVAILABLE", 503, "http-status-503", "UNAVAILABLE"));
        Assert.IsTrue(IsRetryableRecoveryFailure("PROVIDER_UNAVAILABLE", 503, "http-status-503", null), "A received 5xx from the pinned endpoint is retryable even without a JSON provider envelope.");
        Assert.IsFalse(IsRetryableRecoveryFailure("PROVIDER_UNAVAILABLE", 429, "http-status-429", "UNAVAILABLE"));
        Assert.IsFalse(IsRetryableRecoveryFailure("PARSER_INVALID_RESPONSE", null, "malformed"));
        Assert.IsFalse(IsRetryableRecoveryFailure("INVALID_REQUEST", null, null));
        Assert.IsFalse(IsRetryableRecoveryFailure("PROVIDER_UNAVAILABLE", null, "other"));
        Assert.IsFalse(IsRetryableRecoveryFailure("PROVIDER_UNAVAILABLE", 503, "http-status-503", "RESOURCE_EXHAUSTED"));
        Assert.IsFalse(IsRetryableRecoveryFailure("PROVIDER_UNAVAILABLE", 400, "http-status-400", "UNAVAILABLE"));
        Assert.IsFalse(IsRetryableRecoveryFailure("PROVIDER_UNAVAILABLE", 403, "http-status-403", null));
    }

    [TestMethod]
    public void ProviderErrorMetadataKeepsOnlyAllowlistedStatusAndNumericCode()
    {
        var safe = ProviderErrorMetadata.Parse("""{"error":{"code":503,"status":"UNAVAILABLE","message":"do not persist this text"}}""");
        Assert.AreEqual("UNAVAILABLE", safe.Status);
        Assert.AreEqual(503, safe.Code);
        var serialized = JsonSerializer.Serialize(safe);
        Assert.IsFalse(serialized.Contains("do not persist", StringComparison.Ordinal));

        var unknown = ProviderErrorMetadata.Parse("""{"error":{"code":503,"status":"UNKNOWN","message":"also not retained"}}""");
        Assert.IsNull(unknown.Status);
        Assert.AreEqual(503, unknown.Code);
    }

    [TestMethod]
    public void RecoveryCannotAdvanceUntilBothInitialLanguagesPass()
    {
        Assert.IsTrue(CanStartRecoveryCase("P5-01", new HashSet<string>(StringComparer.Ordinal)));
        Assert.IsTrue(CanStartRecoveryCase("P5-02", new HashSet<string>(StringComparer.Ordinal) { "P5-01" }));
        Assert.IsFalse(CanStartRecoveryCase("P5-03", new HashSet<string>(StringComparer.Ordinal) { "P5-01" }));
        Assert.IsFalse(CanStartRecoveryCase("P5-03", new HashSet<string>(StringComparer.Ordinal) { "P5-02" }));
        Assert.IsTrue(CanStartRecoveryCase("P5-03", new HashSet<string>(StringComparer.Ordinal) { "P5-01", "P5-02" }));
    }

    [TestMethod]
    public void DiagnosticLoggerCapturesOnlySanitizedAdapterCause()
    {
        var logger = new DiagnosticCauseCaptureLogger<GeminiRealQueryParser>();
        logger.Log(LogLevel.Warning, new EventId(), new[] { new KeyValuePair<string, object?>("Cause", "timeout") }, null, static (_, _) => "untrusted rendered text");
        Assert.AreEqual("timeout", logger.LastCause);
        logger.Clear();
        logger.Log(LogLevel.Warning, new EventId(), new[] { new KeyValuePair<string, object?>("Cause", "raw provider response text") }, null, static (_, _) => "untrusted rendered text");
        Assert.IsNull(logger.LastCause);
    }

    [TestMethod]
    public async Task RecoveryJournalUsesNewPathAndLeavesLegacyJournalUntouched()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"CineKrosRecoveryJournalTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        try
        {
            var oldPath = Path.Combine(tempDirectory, "live-parser-smoke-evidence.json");
            var recoveryPath = Path.Combine(tempDirectory, "live-parser-recovery-01.json");
            await File.WriteAllTextAsync(oldPath, "legacy-journal-must-not-change");
            var originalBytes = await File.ReadAllBytesAsync(oldPath);
            using (var store = new SmokeJournalStore(recoveryPath))
            {
                await store.WriteAsync(new SmokeJournal("recovery", GeminiRealQueryParser.Model, ExpectedPromptSha256, ExpectedSchemaSha256));
            }
            CollectionAssert.AreEqual(originalBytes, await File.ReadAllBytesAsync(oldPath));
            Assert.IsTrue(File.Exists(recoveryPath));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [TestMethod]
    public void ResumeStateKeepsPriorBudgetSkipsValidatedSuccessAndRetriesOnlyTransientFailure()
    {
        var plan = BuildTestPlan();
        var now = new DateTimeOffset(2026, 10, 8, 15, 40, 0, TimeSpan.Zero);
        var journal = new SmokeJournal(plan.SchemaVersion, GeminiRealQueryParser.Model, ExpectedPromptSha256, ExpectedSchemaSha256)
        {
            TotalAttempts = 2,
            PrimaryCasesStarted = 2,
            TransientRetries = 0,
            ElapsedMilliseconds = 19_184,
            Outcome = "failed_or_stopped",
            Attempts =
            [
                new AttemptRecord("P5-01", 0, now.AddSeconds(-20))
                {
                    Http = new HttpRecord(200, null, null), ResultType = "query", LanguageCheck = "match",
                    ProviderDurationMilliseconds = 9000,
                    ProviderChecklistJson = "{\"languageCheck\":\"match\",\"checklist\":{\"type\":\"query\"}}",
                    CanonicalHardFiltersJson = "{\"YearMin\":null,\"YearMax\":null,\"RuntimeMin\":null,\"RuntimeMax\":null,\"Genres\":null,\"RatingMin\":null,\"OriginalLanguage\":null,\"RatingOperator\":null}",
                    SemanticQuery = "validated semantic query"
                },
                new AttemptRecord("P5-02", 0, now.AddSeconds(-10))
                {
                    Http = HttpRecord.NetworkFailure, SanitizedErrorCode = "PROVIDER_UNAVAILABLE"
                }
            ]
        };

        var state = BuildResumeState(plan, journal, ExpectedPromptSha256, ExpectedSchemaSha256, now);
        Assert.AreEqual(2, state.Budget.Total);
        Assert.AreEqual(2, state.Budget.Primary);
        Assert.AreEqual(0, state.Budget.Retries);
        Assert.AreEqual(16, state.PendingCases.Length);
        Assert.AreEqual("P5-02", state.PendingCases[0].TestCase.Id);
        Assert.AreEqual(1, state.PendingCases[0].PriorAttempts);
        Assert.AreEqual("P5-03", state.PendingCases[1].TestCase.Id);
        Assert.AreEqual(now.AddSeconds(-20), state.EarliestStartedUtc);
        Assert.AreEqual(now.AddSeconds(-20) + MaxRunDuration, state.DeadlineUtc);
        Assert.IsTrue(state.AdoptedPlanHash);
        Assert.IsFalse(state.PendingCases.Any(item => item.TestCase.Id == "P5-01"));
        Assert.IsTrue(state.Budget.TryBegin(primary: false, TimeSpan.Zero));
        Assert.AreEqual(3, state.Budget.Total);
        Assert.AreEqual(1, state.Budget.Retries);
    }

    [TestMethod]
    public void ResumeStateRejectsChangedPinsAndExpiredAbsoluteDeadline()
    {
        var plan = BuildTestPlan();
        var started = new DateTimeOffset(2026, 10, 8, 15, 36, 46, TimeSpan.Zero);
        var journal = new SmokeJournal(plan.SchemaVersion, GeminiRealQueryParser.Model, ExpectedPromptSha256, ExpectedSchemaSha256)
        {
            PlanSha256 = plan.Sha256,
            TotalAttempts = 1,
            PrimaryCasesStarted = 1,
            Attempts = [new AttemptRecord("P5-01", 0, started) { Http = new HttpRecord(200, null, null), ProviderDurationMilliseconds = 8000, ResultType = "query", LanguageCheck = "match", ProviderChecklistJson = "{\"languageCheck\":\"match\",\"checklist\":{\"type\":\"query\"}}", CanonicalHardFiltersJson = "{\"YearMin\":null,\"YearMax\":null,\"RuntimeMin\":null,\"RuntimeMax\":null,\"Genres\":null,\"RatingMin\":null,\"OriginalLanguage\":null,\"RatingOperator\":null}", SemanticQuery = "validated semantic query" }]
        };

        Assert.ThrowsExactly<InvalidOperationException>(() => BuildResumeState(plan with { Sha256 = "different" }, journal, ExpectedPromptSha256, ExpectedSchemaSha256, started.AddMinutes(1)));
        Assert.ThrowsExactly<InvalidOperationException>(() => BuildResumeState(plan, journal, "different", ExpectedSchemaSha256, started.AddMinutes(1)));
        Assert.ThrowsExactly<InvalidOperationException>(() => BuildResumeState(plan, journal, ExpectedPromptSha256, ExpectedSchemaSha256, started.Add(MaxRunDuration)));
    }

    [TestMethod]
    public void RatingEvidenceAcceptsUnspecifiedScaleForBareEightAndKeepsStrictGt()
    {
        var result = new RealParserResult("query", new RealParsedQuery(new RealHardFilters(RatingMin: 4m, RatingOperator: "gt"), "science fiction"), LanguageCheck: "match");
        var evidence = new GeminiParserEvidenceResult(result,
            """{"languageCheck":"match","checklist":{"type":"query","rating":{"status":"present","value":8,"operator":"gt","scale":"unspecified"}}}""",
            TimeSpan.Zero);

        AssertRatingNormalization(new LiveCase("P5-15", "sr", "", "query", "match", null), evidence);
    }

    [TestMethod]
    public async Task JournalStoreWritesAtomicallyAndRefusesAutomaticRestart()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"CineKrosSmokeJournalTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);
        try
        {
            var path = Path.Combine(tempDirectory, "journal.json");
            var journal = new SmokeJournal("test", GeminiRealQueryParser.Model, ExpectedPromptSha256, ExpectedSchemaSha256);
            using (var store = new SmokeJournalStore(path))
            {
                journal.Outcome = "started";
                await store.WriteAsync(journal);
                journal.Outcome = "finished";
                await store.WriteAsync(journal);
                Assert.IsTrue(File.Exists(path));
                Assert.IsFalse(File.Exists(path + ".tmp"));
            }
            var originalBytes = await File.ReadAllBytesAsync(path);
            string backupPath;
            using (var resumeStore = new SmokeJournalStore(path, allowResume: true))
            {
                backupPath = resumeStore.BackupPath!;
                journal.Outcome = "resumed";
                await resumeStore.WriteAsync(journal);
                CollectionAssert.AreEqual(originalBytes, await File.ReadAllBytesAsync(backupPath));
            }
            Assert.ThrowsExactly<InvalidOperationException>(() => new SmokeJournalStore(path));
            using var persisted = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.AreEqual("resumed", persisted.RootElement.GetProperty("Outcome").GetString());
            using var original = JsonDocument.Parse(await File.ReadAllTextAsync(backupPath));
            Assert.AreEqual("finished", original.RootElement.GetProperty("Outcome").GetString());
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [TestMethod]
    public async Task ExplicitlyGatedLiveParserSmokeUsesFrozenPlanAndBoundedAttempts()
    {
        var standardGate = string.Equals(Environment.GetEnvironmentVariable(GateVariable), "true", StringComparison.Ordinal);
        var resumeGate = string.Equals(Environment.GetEnvironmentVariable(ResumeGateVariable), "true", StringComparison.Ordinal);
        var recoveryGate = string.Equals(Environment.GetEnvironmentVariable(RecoveryGateVariable), "true", StringComparison.Ordinal);
        var mode = ResolveLiveMode(standardGate, resumeGate, recoveryGate);
        if (mode == LiveRunMode.Disabled)
            Assert.Inconclusive($"Live provider calls are disabled. MAIN must explicitly set process variable {GateVariable}=true for an authorized run.");
        var resume = mode == LiveRunMode.Resume;
        var recovery = mode == LiveRunMode.Recovery;
        var apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey)) Assert.Inconclusive("The explicitly gated run requires GEMINI_API_KEY in the process environment.");

        var plan = ReadAndValidatePlan();
        var prompt = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "RealProviders", "query-parser-v5.md"));
        var schema = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "RealProviders", "query-parser-v5.schema.json"));
        Assert.AreEqual(ExpectedPromptSha256, Sha256(prompt));
        Assert.AreEqual(ExpectedSchemaSha256, Sha256(schema));

        SmokeJournal journal;
        AttemptBudget budget;
        PendingCase[] casesToRun;
        DateTimeOffset? earliestStartedUtc;
        DateTimeOffset? deadlineUtc;
        var runJournalPath = SelectJournalPath(mode);
        string? expectedPriorJournalSha256 = null;
        var now = DateTimeOffset.UtcNow;
        if (resume)
        {
            if (!File.Exists(runJournalPath)) Assert.Fail("Explicit resume requested, but no existing smoke journal is available.");
            var priorJournalBytes = await File.ReadAllBytesAsync(runJournalPath);
            expectedPriorJournalSha256 = Sha256(priorJournalBytes);
            journal = JsonSerializer.Deserialize<SmokeJournal>(priorJournalBytes)
                ?? throw new InvalidOperationException("The prior smoke journal could not be parsed.");
            var state = BuildResumeState(plan, journal, ExpectedPromptSha256, ExpectedSchemaSha256, now);
            budget = state.Budget;
            casesToRun = state.PendingCases;
            earliestStartedUtc = state.EarliestStartedUtc;
            deadlineUtc = state.DeadlineUtc;
        }
        else
        {
            journal = new SmokeJournal(plan.SchemaVersion, GeminiRealQueryParser.Model, ExpectedPromptSha256, ExpectedSchemaSha256) { PlanSha256 = plan.Sha256, RunMode = recovery ? "recovery" : "standard" };
            budget = new AttemptBudget(MaxHttpAttempts, MaxPrimaryCases, MaxTransientRetries);
            casesToRun = plan.Cases.Select(testCase => new PendingCase(testCase, 0)).ToArray();
            earliestStartedUtc = null;
            deadlineUtc = null;
        }

        using var journalStore = new SmokeJournalStore(runJournalPath, allowResume: resume, expectedExistingSha256: expectedPriorJournalSha256);
        if (resume)
        {
            var state = BuildResumeState(plan, journal, ExpectedPromptSha256, ExpectedSchemaSha256, now);
            journal.PlanHashAdoptedFromLegacyJournal |= state.AdoptedPlanHash;
            journal.PlanSha256 = plan.Sha256;
            if (state.AdoptedPlanHash) journal.PlanHashAdoptionUtc = now;
            journal.ResumeCount++;
            journal.LastResumeStartedUtc = now;
            journal.ResumeBackupPath = journalStore.BackupPath;
            journal.OriginalJournalBackupSha256 = journalStore.BackupSha256;
            journal.Outcome = "resuming";
            await journalStore.WriteAsync(journal);
        }
        else journal.Outcome = "running";

        using var runDeadline = new CancellationTokenSource();
        if (deadlineUtc is { } fixedDeadline)
        {
            var remaining = fixedDeadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) Assert.Fail("The original 20-minute live-run deadline has expired.");
            runDeadline.CancelAfter(remaining);
        }
        DateTimeOffset? previousRequestStarted = journal.Attempts.Count == 0 ? null : journal.Attempts.MaxBy(attempt => attempt.StartedUtc)!.StartedUtc;
        var transport = new ResponseMetadataHandler(new SocketsHttpHandler { AllowAutoRedirect = false });
        using var httpClient = new HttpClient(transport, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        var diagnosticLogger = recovery ? new DiagnosticCauseCaptureLogger<GeminiRealQueryParser>() : null;
        var parser = new GeminiRealQueryParser(httpClient, apiKey, prompt, schema, new RealParsedQueryValidator(), diagnosticLogger, isDevelopment: recovery, languageAware: true);
        var successfulRecoveryCases = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            foreach (var pendingCase in casesToRun)
            {
                var testCase = pendingCase.TestCase;
                var retriesForCase = pendingCase.PriorAttempts;
                if (recovery && !CanStartRecoveryCase(testCase.Id, successfulRecoveryCases))
                    throw new InvalidOperationException("Recovery cannot advance beyond the initial EN/SR pair until both have passed.");
                while (true)
                {
                    EnsureRemainingTime(ElapsedSince(earliestStartedUtc));
                    if (previousRequestStarted is { } prior)
                    {
                        var wait = MinimumRequestSpacing - (DateTimeOffset.UtcNow - prior);
                        if (wait > TimeSpan.Zero) await Task.Delay(wait, runDeadline.Token);
                    }
                    EnsureRemainingTime(ElapsedSince(earliestStartedUtc));
                    previousRequestStarted = DateTimeOffset.UtcNow;
                    if (earliestStartedUtc is null)
                    {
                        earliestStartedUtc = previousRequestStarted;
                        deadlineUtc = previousRequestStarted + MaxRunDuration;
                        journal.EarliestStartedUtc = earliestStartedUtc;
                        runDeadline.CancelAfter(deadlineUtc.Value - DateTimeOffset.UtcNow);
                    }
                    Assert.IsTrue(budget.TryBegin(primary: retriesForCase == 0, ElapsedSince(earliestStartedUtc)), "Attempt caps or original 20-minute duration reached.");
                    transport.LastResponse = null;
                    diagnosticLogger?.Clear();
                    var attempt = new AttemptRecord(testCase.Id, retriesForCase, previousRequestStarted.Value);
                    journal.Attempts.Add(attempt);
                    UpdateJournalCounters(journal, budget, ElapsedSince(earliestStartedUtc));
                    await journalStore.WriteAsync(journal);
                    var attemptTimer = Stopwatch.StartNew();
                    try
                    {
                        var evidence = await parser.ParseWithEvidenceAsync(testCase.Language, testCase.Message, runDeadline.Token);
                        attemptTimer.Stop();
                        attempt.Http = transport.LastResponse?.ToRecord() ?? HttpRecord.NetworkFailure;
                        attempt.ElapsedWallMilliseconds = (long)attemptTimer.Elapsed.TotalMilliseconds;
                        attempt.ProviderDurationMilliseconds = (long)evidence.ProviderDuration.TotalMilliseconds;
                        attempt.ResultType = evidence.Result.Type;
                        attempt.LanguageCheck = evidence.Result.LanguageCheck;
                        attempt.AlertCode = evidence.Result.AlertCode;
                        attempt.ProviderChecklistJson = evidence.ProviderChecklistJson;
                        attempt.CanonicalHardFiltersJson = evidence.Result.Query is null ? null : JsonSerializer.Serialize(evidence.Result.Query.HardFilters);
                        attempt.SemanticQuery = evidence.Result.Query?.SemanticQuery;
                        attempt.DiagnosticCause = diagnosticLogger?.LastCause;
                        UpdateJournalCounters(journal, budget, ElapsedSince(earliestStartedUtc));
                        await journalStore.WriteAsync(journal);
                        AssertExpected(testCase, evidence.Result);
                        AssertHardOnlySemantics(testCase, evidence.Result);
                        AssertRatingNormalization(testCase, evidence);
                        if (recovery) successfulRecoveryCases.Add(testCase.Id);
                        break;
                    }
                    catch (RealProviderException exception)
                    {
                        attempt.DiagnosticCause = diagnosticLogger?.LastCause;
                        attempt.ElapsedWallMilliseconds ??= (long)attemptTimer.Elapsed.TotalMilliseconds;
                        attempt.Http = transport.LastResponse?.ToRecord() ?? HttpRecord.NetworkFailure;
                        attempt.SanitizedErrorCode = exception.Code;
                        UpdateJournalCounters(journal, budget, ElapsedSince(earliestStartedUtc));
                        await journalStore.WriteAsync(journal);
                        var retryable = recovery
                            ? IsRetryableRecoveryFailure(exception.Code, transport.LastResponse?.StatusCode, attempt.DiagnosticCause, transport.LastResponse?.ProviderErrorStatus)
                            : IsRetryableServerError(transport.LastResponse);
                        if (retriesForCase >= MaxTransientRetries || !retryable)
                            throw;
                        retriesForCase++;
                        var retryWait = ComputeRetryWait(transport.LastResponse, retriesForCase);
                        EnsureRemainingTime(ElapsedSince(earliestStartedUtc) + retryWait);
                        await Task.Delay(retryWait, runDeadline.Token);
                    }
                    catch
                    {
                        attempt.DiagnosticCause = diagnosticLogger?.LastCause;
                        attempt.ElapsedWallMilliseconds ??= (long)attemptTimer.Elapsed.TotalMilliseconds;
                        attempt.Http ??= transport.LastResponse?.ToRecord() ?? HttpRecord.NetworkFailure;
                        UpdateJournalCounters(journal, budget, ElapsedSince(earliestStartedUtc));
                        await journalStore.WriteAsync(journal);
                        throw;
                    }
                }
            }
            journal.Outcome = "passed";
        }
        catch
        {
            journal.Outcome = "failed_or_stopped";
            throw;
        }
        finally
        {
            UpdateJournalCounters(journal, budget, ElapsedSince(earliestStartedUtc));
            await journalStore.WriteAsync(journal);
        }
    }

    private static LivePlan ReadAndValidatePlan()
    {
        var planBytes = File.ReadAllBytes(PlanPath);
        using var document = JsonDocument.Parse(planBytes);
        var root = document.RootElement;
        Assert.AreEqual("sr-p5-live-parser-smoke-plan-v1", root.GetProperty("schemaVersion").GetString());
        Assert.AreEqual(MaxHttpAttempts, root.GetProperty("maxHttpAttempts").GetInt32());
        Assert.AreEqual(MaxPrimaryCases, root.GetProperty("maxPrimaryCases").GetInt32());
        Assert.AreEqual(MaxTransientRetries, root.GetProperty("maxTransientRetries").GetInt32());
        Assert.AreEqual(20, root.GetProperty("maxDurationMinutes").GetInt32());
        Assert.AreEqual(5, root.GetProperty("minSpacingSeconds").GetInt32());
        Assert.AreEqual(GeminiRealQueryParser.Model, root.GetProperty("model").GetString());
        var cases = root.GetProperty("cases").Deserialize<LiveCase[]>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.IsNotNull(cases);
        Assert.AreEqual(MaxPrimaryCases, cases.Length);
        CollectionAssert.AreEqual(Enumerable.Range(1, MaxPrimaryCases).Select(index => $"P5-{index:00}").ToArray(), cases.Select(item => item.Id).ToArray());
        return new LivePlan(root.GetProperty("schemaVersion").GetString()!, cases, Convert.ToHexString(SHA256.HashData(planBytes)));
    }

    private static ResumeState BuildResumeState(LivePlan plan, SmokeJournal journal, string promptSha256, string schemaSha256, DateTimeOffset now)
    {
        if (journal.PlanVersion != plan.SchemaVersion || journal.Model != GeminiRealQueryParser.Model ||
            journal.PromptSha256 != promptSha256 || journal.SchemaSha256 != schemaSha256)
            throw new InvalidOperationException("Prior smoke journal prompt/schema/model/plan-version pins do not match this run.");
        if (journal.PlanSha256 is not null && journal.PlanSha256 != plan.Sha256)
            throw new InvalidOperationException("Prior smoke journal plan hash does not match the current canonical plan.");
        var adoptLegacyPlanHash = journal.PlanSha256 is null;
        if (adoptLegacyPlanHash && (journal.Outcome != "failed_or_stopped" || journal.ResumeCount != 0 || journal.Attempts.Count != 2 || journal.Attempts[0].CaseId != "P5-01" || journal.Attempts[0].RetryIndex != 0 ||
            journal.Attempts[1].CaseId != "P5-02" || journal.Attempts[1].RetryIndex != 0 || journal.TotalAttempts != 2 ||
            journal.PrimaryCasesStarted != 2 || journal.TransientRetries != 0 || journal.PlanHashAdoptedFromLegacyJournal))
            throw new InvalidOperationException("Only the original two-attempt P5-01 success / P5-02 transient-timeout journal may adopt the plan hash.");
        if (journal.Outcome == "passed" || journal.Attempts.Count == 0 || journal.TotalAttempts != journal.Attempts.Count ||
            journal.TotalAttempts < 0 || journal.PrimaryCasesStarted < 0 || journal.TransientRetries < 0 ||
            journal.TotalAttempts > MaxHttpAttempts || journal.PrimaryCasesStarted > MaxPrimaryCases || journal.TransientRetries > MaxTransientRetries ||
            journal.PrimaryCasesStarted != journal.Attempts.Count(attempt => attempt.RetryIndex == 0) ||
            journal.TransientRetries != journal.Attempts.Count(attempt => attempt.RetryIndex > 0))
            throw new InvalidOperationException("Prior smoke journal counters or outcome are invalid for resume.");

        var caseById = plan.Cases.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var previousPlanIndex = -1;
        var previousCaseId = string.Empty;
        for (var index = 0; index < journal.Attempts.Count; index++)
        {
            var attempt = journal.Attempts[index];
            if (!caseById.TryGetValue(attempt.CaseId, out _) || attempt.RetryIndex < 0)
                throw new InvalidOperationException("Prior journal contains an unknown case or invalid retry index.");
            var planIndex = Array.FindIndex(plan.Cases, item => item.Id == attempt.CaseId);
            if (planIndex < previousPlanIndex || (planIndex == previousPlanIndex && attempt.CaseId != previousCaseId))
                throw new InvalidOperationException("Prior journal case order is inconsistent with the pinned plan.");
            if (index > 0 && (attempt.StartedUtc < journal.Attempts[index - 1].StartedUtc ||
                attempt.StartedUtc - journal.Attempts[index - 1].StartedUtc < MinimumRequestSpacing))
                throw new InvalidOperationException("Prior attempt timestamps violate chronological/minimum-spacing constraints.");
            previousPlanIndex = planIndex;
            previousCaseId = attempt.CaseId;
        }
        var grouped = journal.Attempts.GroupBy(attempt => attempt.CaseId, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.OrderBy(item => item.RetryIndex).ToArray(), StringComparer.Ordinal);
        var completed = new HashSet<string>(StringComparer.Ordinal);
        var pendingById = new Dictionary<string, PendingCase>(StringComparer.Ordinal);
        foreach (var (caseId, attempts) in grouped)
        {
            if (!caseById.TryGetValue(caseId, out var testCase)) throw new InvalidOperationException("Prior journal contains a case absent from the pinned plan.");
            for (var index = 0; index < attempts.Length; index++)
            {
                if (attempts[index].RetryIndex != index) throw new InvalidOperationException("Prior journal retry indices are not contiguous.");
                if (index < attempts.Length - 1 && !IsRetryablePriorFailure(attempts[index]))
                    throw new InvalidOperationException("Prior journal contains a retry after a non-transient or returned result.");
            }

            var last = attempts[^1];
            if (last.ResultType is not null)
            {
                ValidatePriorSuccess(testCase, last);
                completed.Add(caseId);
            }
            else if (IsRetryablePriorFailure(last))
            {
                pendingById.Add(caseId, new PendingCase(testCase, attempts.Length));
            }
            else throw new InvalidOperationException("Prior journal has an incomplete/non-retryable case; MAIN must inspect it manually.");
        }

        var pending = plan.Cases.Where(item => !completed.Contains(item.Id))
            .Select(item => pendingById.TryGetValue(item.Id, out var failed) ? failed : new PendingCase(item, 0)).ToArray();
        var earliest = journal.EarliestStartedUtc ?? journal.Attempts.MinBy(attempt => attempt.StartedUtc)!.StartedUtc;
        if (journal.EarliestStartedUtc is { } recordedEarliest && recordedEarliest != journal.Attempts.MinBy(attempt => attempt.StartedUtc)!.StartedUtc)
            throw new InvalidOperationException("Journal earliest start does not match the recorded attempt history.");
        if (journal.Attempts.Any(attempt => attempt.StartedUtc < earliest)) throw new InvalidOperationException("Journal earliest start is inconsistent with recorded attempts.");
        var deadline = earliest + MaxRunDuration;
        if (now >= deadline) throw new InvalidOperationException("The original 20-minute live-run deadline has expired; resume would extend the authorized window.");
        var budget = new AttemptBudget(MaxHttpAttempts, MaxPrimaryCases, MaxTransientRetries, journal.TotalAttempts, journal.PrimaryCasesStarted, journal.TransientRetries);
        if (pending.Length == 0) throw new InvalidOperationException("No pending parser cases remain; automatic re-entry is disabled.");
        if (adoptLegacyPlanHash && (pending.Length != 16 || pending[0].TestCase.Id != "P5-02" || pending[0].PriorAttempts != 1 || completed.Count != 1 || !completed.Contains("P5-01")))
            throw new InvalidOperationException("Legacy plan-hash adoption is limited to the reviewed P5-01/P5-02 recovery state.");
        return new ResumeState(budget, pending, earliest, deadline, adoptLegacyPlanHash);
    }

    private static bool IsRetryablePriorFailure(AttemptRecord attempt) =>
        attempt.ResultType is null && attempt.SanitizedErrorCode == "PROVIDER_UNAVAILABLE" &&
        (attempt.Http?.StatusCode is null || attempt.Http.StatusCode is >= 500 and <= 599);

    private static void ValidatePriorSuccess(LiveCase testCase, AttemptRecord attempt)
    {
        if (attempt.SanitizedErrorCode is not null || attempt.ResultType != testCase.ExpectedType ||
            attempt.LanguageCheck != testCase.ExpectedLanguageCheck || attempt.AlertCode != testCase.ExpectedAlertCode ||
            attempt.Http?.StatusCode != 200 || attempt.ProviderDurationMilliseconds is null or <= 0 ||
            string.IsNullOrWhiteSpace(attempt.ProviderChecklistJson))
            throw new InvalidOperationException("A journal result does not satisfy its pinned case expectation; it cannot be skipped or retried automatically.");
        using var evidence = JsonDocument.Parse(attempt.ProviderChecklistJson);
        var root = evidence.RootElement;
        var checklist = root.GetProperty("checklist");
        var checklistAlert = checklist.TryGetProperty("alertCode", out var alertCode) && alertCode.ValueKind == JsonValueKind.String ? alertCode.GetString() : null;
        if (root.GetProperty("languageCheck").GetString() != testCase.ExpectedLanguageCheck ||
            checklist.GetProperty("type").GetString() != testCase.ExpectedType || checklistAlert != testCase.ExpectedAlertCode)
            throw new InvalidOperationException("Prior validated checklist evidence does not match its expected language/type.");
        if (testCase.ExpectedType == "query")
        {
            if (string.IsNullOrWhiteSpace(attempt.CanonicalHardFiltersJson)) throw new InvalidOperationException("Validated hard filters are absent from a successful query record.");
            var hardFilters = JsonSerializer.Deserialize<RealHardFilters>(attempt.CanonicalHardFiltersJson);
            if (attempt.SemanticQuery is null && (hardFilters is null || !RealParsedQueryValidator.HasActiveFilter(hardFilters)))
                throw new InvalidOperationException("A hard-only prior result has no validated active filter.");
            if (attempt.SemanticQuery is not null && string.IsNullOrWhiteSpace(attempt.SemanticQuery))
                throw new InvalidOperationException("A successful semantic query record contains blank semantic text.");
        }
    }

    private static void AssertExpected(LiveCase testCase, RealParserResult result)
    {
        Assert.AreEqual(testCase.ExpectedType, result.Type, testCase.Id);
        Assert.AreEqual(testCase.ExpectedLanguageCheck, result.LanguageCheck, testCase.Id);
        Assert.AreEqual(testCase.ExpectedAlertCode, result.AlertCode, testCase.Id);
    }

    private static void AssertHardOnlySemantics(LiveCase testCase, RealParserResult result)
    {
        if (testCase.Id is not ("P5-13" or "P5-14")) return;
        Assert.AreEqual("query", result.Type, testCase.Id);
        Assert.IsNotNull(result.Query, testCase.Id);
        Assert.IsNull(result.Query.SemanticQuery, $"{testCase.Id}: hard-only request must have zero semantic query.");
        Assert.IsTrue(RealParsedQueryValidator.HasActiveFilter(result.Query.HardFilters), $"{testCase.Id}: hard-only request must retain filters.");
    }

    private static void AssertRatingNormalization(LiveCase testCase, GeminiParserEvidenceResult evidence)
    {
        if (testCase.Id != "P5-15") return;
        var parsed = evidence.Result.Query ?? ReturnMissingQuery("P5-15 must be a query");
        Assert.AreEqual(4m, parsed.HardFilters.RatingMin, "Raw scale-10 threshold 8 must normalize to 4 on the canonical scale.");
        Assert.AreEqual("gt", parsed.HardFilters.RatingOperator);
        using var checklist = JsonDocument.Parse(evidence.ProviderChecklistJson);
        var rating = checklist.RootElement.GetProperty("checklist").GetProperty("rating");
        Assert.AreEqual(8m, rating.GetProperty("value").GetDecimal());
        Assert.AreEqual("gt", rating.GetProperty("operator").GetString());
        Assert.IsTrue(rating.GetProperty("scale").GetString() is "unspecified" or "ten",
            "A bare 8 may use the unspecified scale; ten is also valid if the parser explicitly identifies it.");
    }

    private static RealParsedQuery ReturnMissingQuery(string message)
    {
        Assert.Fail(message);
        return null!;
    }

    private static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
    private static string Sha256(byte[] value) => Convert.ToHexString(SHA256.HashData(value));

    private static LivePlan BuildTestPlan() => new("sr-p5-live-parser-smoke-plan-v1",
        Enumerable.Range(1, MaxPrimaryCases).Select(index => new LiveCase($"P5-{index:00}", index is 2 or 3 or 4 or 6 or 8 or 10 or 11 or 12 or 14 or 15 or 16 ? "sr" : "en", "test", "query", "match", null)).ToArray(), "plan-hash");

    private static bool IsRetryableServerError(ResponseMetadata? response) => response?.StatusCode is >= 500 and <= 599;

    private enum LiveRunMode { Disabled, Standard, Resume, Recovery }

    private static LiveRunMode ResolveLiveMode(bool standardGate, bool resumeGate, bool recoveryGate)
    {
        if (recoveryGate && (standardGate || resumeGate)) throw new InvalidOperationException("Recovery cannot be combined with standard or resume gates.");
        if (resumeGate && !standardGate) throw new InvalidOperationException("Resume requires the standard live gate.");
        if (recoveryGate) return LiveRunMode.Recovery;
        if (resumeGate) return LiveRunMode.Resume;
        return standardGate ? LiveRunMode.Standard : LiveRunMode.Disabled;
    }

    private static string SelectJournalPath(LiveRunMode mode) => mode switch
    {
        LiveRunMode.Recovery => RecoveryJournalPath,
        LiveRunMode.Standard or LiveRunMode.Resume => JournalPath,
        _ => throw new InvalidOperationException("Disabled live mode has no journal.")
    };

    private static bool CanStartRecoveryCase(string caseId, HashSet<string> successfulCases) =>
        caseId is "P5-01" or "P5-02" || successfulCases.Contains("P5-01") && successfulCases.Contains("P5-02");

    private static bool IsRetryableRecoveryFailure(string? errorCode, int? statusCode, string? cause, string? providerStatus = null)
    {
        if (errorCode != "PROVIDER_UNAVAILABLE") return false;
        if (statusCode == 429) return false;
        if (statusCode is null) return cause is "timeout" or "network";
        if (providerStatus == "RESOURCE_EXHAUSTED") return false;
        return statusCode is >= 500 and <= 599;
    }

    private static TimeSpan ComputeRetryWait(ResponseMetadata? response, int retryNumber)
    {
        var retryAfter = response?.RetryAfter;
        var exponentialFallback = TimeSpan.FromSeconds(5 * (1 << Math.Clamp(retryNumber - 1, 0, MaxTransientRetries - 1)));
        return retryAfter is { } delay && delay > exponentialFallback ? delay : exponentialFallback;
    }

    private static void EnsureRemainingTime(TimeSpan elapsed)
    {
        if (elapsed >= MaxRunDuration) Assert.Fail("The live parser run reached the 20-minute wall-clock limit.");
    }

    private static TimeSpan ElapsedSince(DateTimeOffset? startedUtc) =>
        startedUtc is null ? TimeSpan.Zero : DateTimeOffset.UtcNow - startedUtc.Value;

    private static void UpdateJournalCounters(SmokeJournal journal, AttemptBudget budget, TimeSpan elapsed)
    {
        journal.TotalAttempts = budget.Total;
        journal.PrimaryCasesStarted = budget.Primary;
        journal.TransientRetries = budget.Retries;
        journal.ElapsedMilliseconds = (long)elapsed.TotalMilliseconds;
    }

    private sealed record LivePlan(string SchemaVersion, LiveCase[] Cases, string Sha256);
    private sealed record PendingCase(LiveCase TestCase, int PriorAttempts);
    private sealed record ResumeState(AttemptBudget Budget, PendingCase[] PendingCases, DateTimeOffset EarliestStartedUtc, DateTimeOffset DeadlineUtc, bool AdoptedPlanHash);
    private sealed record LiveCase(string Id, string Language, string Message, string ExpectedType, string ExpectedLanguageCheck, string? ExpectedAlertCode);

    private sealed class AttemptBudget(int maxTotal, int maxPrimary, int maxRetries, int total = 0, int primary = 0, int retries = 0)
    {
        public int Total { get; private set; } = total;
        public int Primary { get; private set; } = primary;
        public int Retries { get; private set; } = retries;

        public bool TryBegin(bool primary, TimeSpan elapsed)
        {
            if (elapsed >= MaxRunDuration || Total >= maxTotal) return false;
            if (primary ? Primary >= maxPrimary : Retries >= maxRetries) return false;
            Total++;
            if (primary) Primary++; else Retries++;
            return true;
        }
    }

    private sealed class ResponseMetadataHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public ResponseMetadata? LastResponse { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            LastResponse = ResponseMetadata.From(response);
            if (!response.IsSuccessStatusCode)
            {
                var providerError = await ProviderErrorMetadata.ReadBoundedAsync(response.Content, cancellationToken);
                LastResponse = LastResponse with { ProviderErrorStatus = providerError.Status, ProviderErrorCode = providerError.Code };
            }
            return response;
        }
    }

    private sealed record ResponseMetadata(int StatusCode, TimeSpan? RetryAfter, string? Remaining, string? ProviderErrorStatus = null, int? ProviderErrorCode = null)
    {
        public static ResponseMetadata From(HttpResponseMessage response)
        {
            var retry = response.Headers.RetryAfter;
            TimeSpan? retryAfter = retry?.Delta;
            if (retryAfter is null && retry?.Date is { } date) retryAfter = date - DateTimeOffset.UtcNow;
            return new ResponseMetadata((int)response.StatusCode, retryAfter,
                HeaderValue(response.Headers, "x-ratelimit-remaining") ?? HeaderValue(response.Headers, "x-goog-ratelimit-remaining"));
        }

        private static string? HeaderValue(HttpResponseHeaders headers, string name) =>
            headers.TryGetValues(name, out var values) && int.TryParse(values.FirstOrDefault(), out var remaining)
                ? remaining.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : null;

        public HttpRecord ToRecord()
        {
            var seconds = RetryAfter is null ? (int?)null : (int)Math.Clamp(Math.Ceiling(RetryAfter.Value.TotalSeconds), 0, int.MaxValue);
            return new HttpRecord(StatusCode, seconds, Remaining, ProviderErrorStatus, ProviderErrorCode);
        }
    }

    private sealed record ProviderErrorMetadata(string? Status, int? Code)
    {
        private static readonly HashSet<string> AllowedStatuses = new(StringComparer.Ordinal)
            { "UNAVAILABLE", "RESOURCE_EXHAUSTED", "INVALID_ARGUMENT", "PERMISSION_DENIED", "UNAUTHENTICATED" };

        public static ProviderErrorMetadata Parse(string json)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                if (!document.RootElement.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object)
                    return new(null, null);
                var code = error.TryGetProperty("code", out var codeElement) && codeElement.TryGetInt32(out var number) ? number : (int?)null;
                var status = error.TryGetProperty("status", out var statusElement) && statusElement.ValueKind == JsonValueKind.String
                    ? statusElement.GetString() : null;
                return new(status is not null && AllowedStatuses.Contains(status) ? status : null, code);
            }
            catch (JsonException) { return new(null, null); }
        }

        public static async Task<ProviderErrorMetadata> ReadBoundedAsync(HttpContent? content, CancellationToken cancellationToken)
        {
            if (content is null) return new(null, null);
            try
            {
                await using var input = await content.ReadAsStreamAsync(cancellationToken);
                var buffer = new byte[16 * 1024 + 1];
                var length = 0;
                while (length < buffer.Length)
                {
                    var read = await input.ReadAsync(buffer.AsMemory(length, buffer.Length - length), cancellationToken);
                    if (read == 0) break;
                    length += read;
                }
                if (length > 16 * 1024) return new(null, null);
                return Parse(Encoding.UTF8.GetString(buffer, 0, length));
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or JsonException or OperationCanceledException)
            { return new(null, null); }
        }
    }

    private sealed class DiagnosticCauseCaptureLogger<T> : ILogger<T>
    {
        public string? LastCause { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Clear() => LastCause = null;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> properties)
            {
                var raw = properties.FirstOrDefault(pair => pair.Key == "Cause").Value as string;
                LastCause = raw switch
                {
                    "timeout" => "timeout",
                    "network exception HttpRequestException" => "network",
                    "malformed response/validation" => "malformed",
                    "other sanitized adapter failure" => "other",
                    _ when raw is not null && raw.StartsWith("HTTP status ", StringComparison.Ordinal) && int.TryParse(raw.AsSpan("HTTP status ".Length), out var status) => $"http-status-{status}",
                    _ => null
                };
            }
        }
    }

    private sealed class SmokeJournal
    {
        public SmokeJournal() { }
        public SmokeJournal(string planVersion, string model, string promptSha256, string schemaSha256)
        { PlanVersion = planVersion; Model = model; PromptSha256 = promptSha256; SchemaSha256 = schemaSha256; }
        public string PlanVersion { get; set; } = "";
        public string Model { get; set; } = "";
        public string PromptSha256 { get; set; } = "";
        public string SchemaSha256 { get; set; } = "";
        public string? PlanSha256 { get; set; }
        public string? RunMode { get; set; }
        public bool PlanHashAdoptedFromLegacyJournal { get; set; }
        public DateTimeOffset? PlanHashAdoptionUtc { get; set; }
        public string? OriginalJournalBackupSha256 { get; set; }
        public DateTimeOffset? EarliestStartedUtc { get; set; }
        public int ResumeCount { get; set; }
        public DateTimeOffset? LastResumeStartedUtc { get; set; }
        public string? ResumeBackupPath { get; set; }
        public string? Outcome { get; set; }
        public int TotalAttempts { get; set; }
        public int PrimaryCasesStarted { get; set; }
        public int TransientRetries { get; set; }
        public long ElapsedMilliseconds { get; set; }
        public List<AttemptRecord> Attempts { get; set; } = [];
    }

    private sealed class AttemptRecord
    {
        public AttemptRecord() { }
        public AttemptRecord(string caseId, int retryIndex, DateTimeOffset startedUtc)
        { CaseId = caseId; RetryIndex = retryIndex; StartedUtc = startedUtc; }
        public string CaseId { get; set; } = "";
        public int RetryIndex { get; set; }
        public DateTimeOffset StartedUtc { get; set; }
        public long? ElapsedWallMilliseconds { get; set; }
        public string? DiagnosticCause { get; set; }
        public HttpRecord? Http { get; set; }
        public long? ProviderDurationMilliseconds { get; set; }
        public string? ResultType { get; set; }
        public string? LanguageCheck { get; set; }
        public string? AlertCode { get; set; }
        public string? SanitizedErrorCode { get; set; }
        public string? ProviderChecklistJson { get; set; }
        public string? CanonicalHardFiltersJson { get; set; }
        public string? SemanticQuery { get; set; }
    }

    private sealed record HttpRecord(int? StatusCode, int? RetryAfterSeconds, string? RateRemaining, string? ProviderErrorStatus = null, int? ProviderErrorCode = null)
    {
        public static HttpRecord NetworkFailure { get; } = new(null, null, null);
    }

    private sealed class SmokeJournalStore : IDisposable
    {
        private readonly string path;
        private readonly string lockPath;
        private readonly FileStream runLock;
        private bool hasWritten;
        public string? BackupPath { get; }
        public string? BackupSha256 { get; }

        public SmokeJournalStore(string path, bool allowResume = false, string? expectedExistingSha256 = null)
        {
            this.path = path;
            lockPath = path + ".lock";
            if (File.Exists(path) != allowResume)
                throw new InvalidOperationException(allowResume ? "Explicit resume requires an existing journal." : "A smoke-test journal already exists; automatic reruns are disabled. MAIN must explicitly review/resume.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            runLock = new FileStream(lockPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            if (File.Exists(path) != allowResume)
            {
                runLock.Dispose();
                File.Delete(lockPath);
                throw new InvalidOperationException("The smoke-test journal state changed while acquiring its run lock.");
            }
            if (allowResume)
            {
                BackupPath = path + ".before-resume-" + DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ", System.Globalization.CultureInfo.InvariantCulture) + ".bak";
                try { File.Copy(path, BackupPath, overwrite: false); }
                catch
                {
                    runLock.Dispose();
                    File.Delete(lockPath);
                    throw;
                }
                BackupSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(BackupPath)));
                if (expectedExistingSha256 is not null && !string.Equals(expectedExistingSha256, BackupSha256, StringComparison.Ordinal))
                {
                    runLock.Dispose();
                    File.Delete(lockPath);
                    File.Delete(BackupPath);
                    throw new InvalidOperationException("The prior journal changed after validation; resume was stopped before any provider call.");
                }
                hasWritten = true;
            }
        }

        public async Task WriteAsync(SmokeJournal journal)
        {
            var tempPath = path + ".tmp";
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, journal, new JsonSerializerOptions { WriteIndented = true });
                await stream.FlushAsync();
                stream.Flush(flushToDisk: true);
            }
            File.Move(tempPath, path, overwrite: hasWritten);
            hasWritten = true;
        }

        public void Dispose()
        {
            runLock.Dispose();
            File.Delete(lockPath);
        }
    }
}
