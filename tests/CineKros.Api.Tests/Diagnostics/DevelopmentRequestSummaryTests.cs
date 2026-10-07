using CineKros.Api.Diagnostics;
using CineKros.Api.RealProviders;
using Microsoft.Extensions.Logging;

namespace CineKros.Api.Tests.Diagnostics;

[TestClass]
public sealed class DevelopmentRequestSummaryTests
{
    [TestMethod]
    public void FormatIncludesOriginalQueryValidatedDtoDurationAndFinalCode()
    {
        var parsed = new RealParserResult("query", new RealParsedQuery(new RealHardFilters(YearMin: 2011), "quiet mystery"));

        var output = DevelopmentRequestSummary.Format("film posle 2010", parsed, 42, "SUCCESS");

        StringAssert.Contains(output, "Query: film posle 2010");
        StringAssert.Contains(output, "Validated parser DTO: {");
        StringAssert.Contains(output, "\"yearMin\":2011");
        StringAssert.Contains(output, "\"semanticQuery\":\"quiet mystery\"");
        var dtoLine = output.Split('\n').Single(line => line.StartsWith("Validated parser DTO:", StringComparison.Ordinal));
        Assert.IsTrue(dtoLine.EndsWith('}'), "The canonical DTO must fit on one console line.");
        Assert.IsFalse(output.Contains("alertCode", StringComparison.Ordinal));
        StringAssert.Contains(output, "Duration: 42 ms");
        StringAssert.Contains(output, "Code: SUCCESS");
    }

    [TestMethod]
    public void WriterIsDevelopmentOnlyAndEmitsFormattedValidatedDto()
    {
        var previous = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            var parsed = new RealParserResult("alert", AlertCode: "QUERY_UNCLEAR");

            DevelopmentRequestSummary.WriteIfDevelopment(false, "secret production query", parsed, 12, "QUERY_UNCLEAR");
            Assert.AreEqual(string.Empty, output.ToString(), "Production gate must suppress query and DTO output.");

            DevelopmentRequestSummary.WriteIfDevelopment(true, "original dev query", parsed, 12, "QUERY_UNCLEAR");
            StringAssert.Contains(output.ToString(), "original dev query");
            StringAssert.Contains(output.ToString(), "\"alertCode\":\"QUERY_UNCLEAR\"");
            Assert.IsFalse(output.ToString().Contains("query\":", StringComparison.Ordinal));
            StringAssert.Contains(output.ToString(), "Duration: 12 ms");
            StringAssert.Contains(output.ToString(), "Code: QUERY_UNCLEAR");
        }
        finally
        {
            Console.SetOut(previous);
        }
    }

    [TestMethod]
    public void FailedParserSummaryShowsNoDtoAndRemainsDevelopmentOnly()
    {
        var previous = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            DevelopmentRequestSummary.WriteFailureIfDevelopment(false, "secret production query", 12, "PARSER_INVALID_RESPONSE");
            Assert.AreEqual(string.Empty, output.ToString());

            DevelopmentRequestSummary.WriteFailureIfDevelopment(true, "under 110 minutes and under 90 minutes", 12, "PARSER_INVALID_RESPONSE");
            StringAssert.Contains(output.ToString(), "Query: under 110 minutes and under 90 minutes");
            StringAssert.Contains(output.ToString(), "Parser DTO: unavailable (rejected before validation)");
            StringAssert.Contains(output.ToString(), "Duration: 12 ms");
            StringAssert.Contains(output.ToString(), "Code: PARSER_INVALID_RESPONSE");
            Assert.IsFalse(output.ToString().Contains("secret production query", StringComparison.Ordinal));
            Assert.IsFalse(output.ToString().Contains("Validated parser DTO:", StringComparison.Ordinal));
        }
        finally
        {
            Console.SetOut(previous);
        }
    }

    [TestMethod]
    public void CompletionLogIsDebugInDevelopmentAndInformationElsewhere()
    {
        var logger = new RecordingLogger();

        DevelopmentRequestSummary.LogCompletion(logger, true, "trace-dev", 15, "SUCCESS");
        Assert.AreEqual(LogLevel.Debug, logger.Level);
        StringAssert.Contains(logger.Message!, "CorrelationId=trace-dev");
        StringAssert.Contains(logger.Message!, "StageDurationMs=15");

        DevelopmentRequestSummary.LogCompletion(logger, false, "trace-prod", 16, "SUCCESS");
        Assert.AreEqual(LogLevel.Information, logger.Level);
        StringAssert.Contains(logger.Message!, "CorrelationId=trace-prod");
    }

    private sealed class RecordingLogger : ILogger
    {
        public LogLevel Level { get; private set; }
        public string? Message { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Level = logLevel;
            Message = formatter(state, exception);
        }
    }
}
