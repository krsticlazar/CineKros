using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using CineKros.Catalog.Importer;
using Npgsql;
using NpgsqlTypes;
using Pgvector;
using Vector = Pgvector.Vector;

namespace CineKros.VectorImporter;

public enum ActorImportFaultV6 { None, AfterMigration, PartialCast, PartialVectors, PartialState }
public sealed record ActorImportValidationV6(int MovieCount, int EnCount, int SrCount, string BaselineSha256, string PairedDescriptorSha256);

/// <summary>Isolated-only 001+002 to V6 upgrade. All release writes and verification share one transaction.</summary>
public static class ActorPairedImporterV6
{
    public const string PairSha256 = "68011ca794b2adfb1ddc5058aa940d40b82cd8fb7ae64a7da40f8ea583da3bc5";
    public const string EnSha256 = "4593ce51cc7b981e07dba50cfe950d0daa3d1cc6a74d43a47c1038e341c5ff09";
    public const string SrSha256 = "5f62b1351d691204b273b55693ab54e37e04ece57a50df9d2cc8e68306d98275";
    private const string EnManifestSha256 = "31c4115873dfcad9e36c756f8497140eea1ae7f73f0c933bb69384dbb945032c";
    private const string SrManifestSha256 = "08e3a92c4c569cc3650fb13c33d624528b3100625c30b97d013f6f70931dcb95";
    private const string V001 = "001_initial_schema", V002 = "002_serbian_search_vectors", V003 = "003_actor_hard_filters";
    private static readonly JsonSerializerOptions MappingOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private sealed record Inputs(ActorImportBaselineV6 Baseline, ActorCatalogV6Document Catalog,
        ActorVectorArtifactV6 En, ActorVectorArtifactV6 Sr, string Migration003);

    public static async Task<ActorImportValidationV6> ValidateInputsAsync(string baseline, string catalog, string manifest,
        string dictionary, string sourceCatalog, string pairRoot, string migrations, CancellationToken cancellationToken = default)
    {
        var i = await LoadAsync(baseline, catalog, manifest, dictionary, sourceCatalog, pairRoot, migrations, cancellationToken);
        return new(i.Catalog.Movies.Count, i.En.Records.Count, i.Sr.Records.Count, i.Baseline.DescriptorSha256, PairSha256);
    }

    private static async Task<Inputs> LoadAsync(string baselinePath, string catalogPath, string manifestPath,
        string dictionaryPath, string sourceCatalogPath, string pairRoot, string migrations, CancellationToken token)
    {
        if (new[] { baselinePath, catalogPath, manifestPath, dictionaryPath, sourceCatalogPath, pairRoot, migrations }.Any(x => !Path.IsPathFullyQualified(x)))
            throw new ArgumentException("All V6 import paths must be absolute.");
        token.ThrowIfCancellationRequested();
        var baseline = await ActorImportBaselineV6.LoadAsync(baselinePath, token);
        var catalog = await ActorCatalogV6Reader.LoadAsync(catalogPath, manifestPath, dictionaryPath, sourceCatalogPath, token);
        await ActorVectorArtifactV6Validator.ValidatePairAsync(pairRoot, catalog, token);
        var en = await ActorVectorArtifactV6Validator.LoadAsync(Path.Combine(pairRoot, "en", "published"), catalog, "en", token);
        var sr = await ActorVectorArtifactV6Validator.LoadAsync(Path.Combine(pairRoot, "sr", "published"), catalog, "sr", token);
        var pair = await File.ReadAllBytesAsync(Path.Combine(pairRoot, "paired-release.json"), token);
        if (ActorImportBaselineV6.Hash(pair) != PairSha256 || en.ArtifactSha256 != EnSha256 || sr.ArtifactSha256 != SrSha256 ||
            en.ManifestSha256 != EnManifestSha256 || sr.ManifestSha256 != SrManifestSha256 ||
            catalog.Movies.Count != 9730 || en.Records.Count != 9730 || sr.Records.Count != 9730 ||
            catalog.Movies.Any(x => x.Metadata is null) || catalog.Movies.Count(x => x.Cast is null) != 79)
            throw new InvalidDataException("V6 import inputs differ from the exact approved full catalog/vector pair.");
        // All provenance is independently validated; these exact pins prevent accepting a relabeled fixture.
        if (catalog.ActorVocabularySha256 != "7427b92ae216dfc59f53d46e0a936971fab93015ce5807e3991e59b01e960de3" ||
            catalog.OrderedCastMappingSha256 != "7de357a36207770d772ea351fedbeea7146b4dc6e13a41d3a518189523b2c0c8" ||
            catalog.OverridesSha256 != "3078b4c0ddbe59d6e6f5087d0ea7254d764e36a6f905c1c91ce699c3f38ba33d" ||
            catalog.CastRulesSha256 != "9c34722165a8e6d15a950c95bccf0eed27d1bffc85ee71c346c73cabddd4ec0d")
            throw new InvalidDataException("V6 actor provenance differs from approved release pins.");
        await ReadMigrationAsync(migrations, V001 + ".sql", ActorImportBaselineV6.Migration001Sha256, token);
        await ReadMigrationAsync(migrations, V002 + ".sql", ActorImportBaselineV6.Migration002Sha256, token);
        var migration = await ReadMigrationAsync(migrations, V003 + ".sql", ActorImportBaselineV6.Migration003Sha256, token);
        return new(baseline, catalog, en, sr, migration);
    }

    public static async Task<string> ImportAsync(string connectionString, string expectedDatabase, string baselinePath,
        string catalogPath, string manifestPath, string dictionaryPath, string sourceCatalogPath, string pairRoot,
        string migrationDirectory, ActorImportFaultV6 testFault = ActorImportFaultV6.None, CancellationToken cancellationToken = default)
    {
        // Reject production, wrong connection DB and invalid fault values before even reading files.
        ActorImportBaselineV6.RequireConnectionTarget(connectionString, expectedDatabase);
        if (!Enum.IsDefined(testFault)) throw new ArgumentOutOfRangeException(nameof(testFault));
        var inputs = await LoadAsync(baselinePath, catalogPath, manifestPath, dictionaryPath, sourceCatalogPath, pairRoot, migrationDirectory, cancellationToken);
        var builder = new NpgsqlDataSourceBuilder(connectionString); builder.UseVector();
        await using var source = builder.Build();
        await using var c = await source.OpenConnectionAsync(cancellationToken);
        await using var tx = await c.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
        try
        {
            await ExecuteAsync(c, tx, "SET LOCAL search_path = public, pg_catalog; SELECT pg_advisory_xact_lock(684321097); SELECT pg_advisory_xact_lock(684321098); SELECT pg_advisory_xact_lock(684321099)", cancellationToken);
            var identity = await ActorImportBaselineV6.ReadIdentityAsync(c, tx, cancellationToken);
            if (!ActorImportBaselineV6.IsAllowedExecutionTarget(identity.Database, expectedDatabase) || identity.Schema != "public" ||
                identity.System != inputs.Baseline.ServerSystemIdentifier || identity.Major != inputs.Baseline.PostgresMajor || identity.Vector != inputs.Baseline.PgvectorVersion)
                throw new InvalidOperationException("Actual database/cluster/schema/version differs from the exact approved isolated target and baseline.");
            await ExecuteAsync(c, tx, "LOCK TABLE public.movies,public.movie_embeddings,public.catalog_import_state,public.embedding_set_state,public.schema_migrations IN ACCESS EXCLUSIVE MODE", cancellationToken);
            var has003 = await ScalarAsync(c, tx, "SELECT EXISTS(SELECT 1 FROM public.schema_migrations WHERE version='003_actor_hard_filters')::text", cancellationToken) == "true";
            await VerifyLedgerAsync(c, tx, has003, cancellationToken);
            if (has003)
            {
                await ExecuteAsync(c, tx, "LOCK TABLE public.actor_catalog_state IN ACCESS EXCLUSIVE MODE", cancellationToken);
                await VerifyFinalAsync(c, tx, inputs, cancellationToken);
                await tx.CommitAsync(cancellationToken);
                return "Verified identical complete V6 release; no changes made.";
            }
            var schema = await ActorSchemaV6.ReadAsync(c, tx, false, cancellationToken);
            ActorSchemaV6.ValidateBaseSnapshot(schema);
            if (schema != inputs.Baseline.SchemaSnapshot) throw new InvalidOperationException("Pre-import schema differs from the complete approved 001+002 baseline.");
            var digests = await ActorImportBaselineV6.ReadDigestsAsync(c, tx, cancellationToken);
            if (inputs.Baseline.Digests.Any(x => digests.GetValueOrDefault(x.Key) != x.Value))
                throw new InvalidOperationException("Pre-import full table digests differ from the fresh approved baseline.");
            await VerifyStatesAsync(c, tx, null, null, null, cancellationToken);
            await VerifyMoviesAsync(c, tx, inputs.Catalog.Movies, false, cancellationToken);

            await ExecuteAsync(c, tx, inputs.Migration003, cancellationToken);
            await using (var ledger = new NpgsqlCommand("INSERT INTO public.schema_migrations(version,sha256,applied_at) VALUES(@version,@sha,now())", c, tx))
            {
                ledger.Parameters.AddWithValue("version", V003); ledger.Parameters.AddWithValue("sha", ActorImportBaselineV6.Migration003Sha256);
                await ledger.ExecuteNonQueryAsync(cancellationToken);
            }
            ThrowFault(testFault, ActorImportFaultV6.AfterMigration);
            await WriteCastAsync(c, tx, inputs.Catalog, testFault, cancellationToken);
            await WriteVectorsAsync(c, tx, inputs.En, inputs.Sr, testFault, cancellationToken);
            await WriteStatesAsync(c, tx, inputs, testFault, cancellationToken);
            await VerifyFinalAsync(c, tx, inputs, cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return "Imported all 9,730 cast mappings and both complete V6 vector sets atomically into the isolated database.";
        }
        catch
        {
            await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    internal static async Task VerifyLedgerAsync(NpgsqlConnection c, NpgsqlTransaction tx, bool has003, CancellationToken token)
    {
        await using var cmd = new NpgsqlCommand("SELECT version,sha256 FROM public.schema_migrations ORDER BY version", c, tx);
        await using var r = await cmd.ExecuteReaderAsync(token);
        var expected = new List<(string, string)> { (V001, ActorImportBaselineV6.Migration001Sha256), (V002, ActorImportBaselineV6.Migration002Sha256) };
        if (has003) expected.Add((V003, ActorImportBaselineV6.Migration003Sha256));
        foreach (var e in expected)
            if (!await r.ReadAsync(token) || r.GetString(0) != e.Item1 || r.GetString(1) != e.Item2)
                throw new InvalidOperationException("Migration ledger differs from exact canonical 001+002[/003] checksums.");
        if (await r.ReadAsync(token)) throw new InvalidOperationException("Migration ledger contains an unexpected version.");
    }

    internal static async Task VerifyStatesAsync(NpgsqlConnection c, NpgsqlTransaction tx, ActorCatalogV6Document? catalog,
        ActorVectorArtifactV6? en, ActorVectorArtifactV6? sr, CancellationToken token)
    {
        var old = catalog is null;
        var catalogVersion = catalog?.CatalogVersion ?? ActorImportBaselineV6.OldCatalogVersion;
        var catalogSha = catalog?.CatalogSha256 ?? ActorImportBaselineV6.OldCatalogSha256;
        var identity = catalog?.IdentitySha256 ?? ActorImportBaselineV6.OldIdentity;
        var enArtifact = en?.ArtifactSha256 ?? ActorImportBaselineV6.OldEnArtifact;
        var srArtifact = sr?.ArtifactSha256 ?? ActorImportBaselineV6.OldSrArtifact;
        const string counts = "SELECT (SELECT count(*) FROM public.movies),(SELECT count(*) FROM public.movie_embeddings),(SELECT count(*) FROM public.movie_embeddings WHERE embedding IS NOT NULL AND embedding_sr IS NOT NULL AND document_fingerprint IS NOT NULL AND document_fingerprint_sr IS NOT NULL),(SELECT count(*) FROM public.catalog_import_state),(SELECT count(*) FROM public.embedding_set_state)";
        await using (var cmd = new NpgsqlCommand(counts, c, tx))
        await using (var r = await cmd.ExecuteReaderAsync(token))
            if (!await r.ReadAsync(token) || r.GetInt64(0) != 9730 || r.GetInt64(1) != 9730 || r.GetInt64(2) != 9730 || r.GetInt64(3) != 1 || r.GetInt64(4) != 2)
                throw new InvalidOperationException("Expected exactly 9,730 movies, full paired vectors and singleton/catalog plus two language states.");
        await using (var cmd = new NpgsqlCommand("SELECT catalog_version,catalog_jsonl_sha256,catalog_content_fingerprint,movie_count,embedding_artifact_sha256,embedding_profile_fingerprint,embedded_count FROM public.catalog_import_state WHERE id=1", c, tx))
        await using (var r = await cmd.ExecuteReaderAsync(token))
            if (!await r.ReadAsync(token) || r.GetString(0) != catalogVersion || r.GetString(1) != catalogSha || r.GetString(2) != identity || r.GetInt32(3) != 9730 || r.IsDBNull(4) || r.GetString(4) != enArtifact || r.IsDBNull(5) || r.GetString(5) != FullBilingualCatalog.ProfileFingerprint || r.IsDBNull(6) || r.GetInt32(6) != 9730)
                throw new InvalidOperationException("Catalog state differs from the exact complete expected release.");
        await using (var cmd = new NpgsqlCommand("SELECT language,profile_fingerprint,catalog_content_fingerprint,corpus_sha256,text_format_version,artifact_sha256,translation_dictionary_sha256,dimension,embedded_count FROM public.embedding_set_state ORDER BY language", c, tx))
        await using (var r = await cmd.ExecuteReaderAsync(token))
        {
            foreach (var language in new[] { "en", "sr" })
            {
                var isSr = language == "sr";
                var corpus = old ? (isSr ? ActorImportBaselineV6.OldSrCorpus : ActorImportBaselineV6.OldEnCorpus) : (isSr ? catalog!.SrCorpusSha256 : catalog!.EnCorpusSha256);
                var format = old ? (isSr ? ActorImportBaselineV6.OldSrFormat : ActorImportBaselineV6.OldEnFormat) : (isSr ? catalog!.SrTextFormatVersion : catalog!.EnTextFormatVersion);
                if (!await r.ReadAsync(token) || r.GetString(0) != language || r.GetString(1) != FullBilingualCatalog.ProfileFingerprint || r.GetString(2) != identity ||
                    r.GetString(3) != corpus || r.GetString(4) != format || r.GetString(5) != (isSr ? srArtifact : enArtifact) ||
                    (r.IsDBNull(6) ? null : r.GetString(6)) != (isSr ? FullBilingualCatalog.DictionarySha256 : null) || r.GetInt32(7) != 768 || r.GetInt32(8) != 9730)
                    throw new InvalidOperationException("Language state differs from the exact complete expected release.");
            }
            if (await r.ReadAsync(token)) throw new InvalidOperationException("Unexpected extra language state.");
        }
    }

    private static async Task VerifyMoviesAsync(NpgsqlConnection c, NpgsqlTransaction tx, IReadOnlyList<ActorMovieV6> movies, bool withCast, CancellationToken token)
    {
        var sql = "SELECT movie_lens_id,imdb_id,tmdb_id,title,year,runtime_minutes,original_language,genres,average_rating::text,rating_count,poster_path" + (withCast ? ",\"cast\"" : "") + " FROM public.movies ORDER BY movie_lens_id";
        await using var cmd = new NpgsqlCommand(sql, c, tx); await using var r = await cmd.ExecuteReaderAsync(token);
        using var map = new MemoryStream();
        foreach (var movie in movies)
        {
            var m = movie.Metadata ?? throw new InvalidDataException("V6 metadata projection missing.");
            if (!await r.ReadAsync(token) || r.GetInt64(0) != m.Id || r.GetString(1) != m.ImdbId ||
                (r.IsDBNull(2) ? null : r.GetInt64(2)) != m.TmdbId || r.GetString(3) != m.Title || r.GetInt32(4) != m.Year ||
                (r.IsDBNull(5) ? null : r.GetInt32(5)) != m.RuntimeMinutes || (r.IsDBNull(6) ? null : r.GetString(6)) != m.OriginalLanguage ||
                !ArrayEqual(r.IsDBNull(7) ? null : r.GetFieldValue<string[]>(7), m.Genres) || !NumericEqual(r.IsDBNull(8) ? null : r.GetString(8), m.AverageRating) ||
                (r.IsDBNull(9) ? null : r.GetInt64(9)) != m.RatingCount || (r.IsDBNull(10) ? null : r.GetString(10)) != m.PosterPath)
                throw new InvalidOperationException($"Stored old metadata differs from approved V6 catalog at movieLensId {m.Id}.");
            if (withCast)
            {
                var cast = r.IsDBNull(11) ? null : r.GetFieldValue<string[]>(11);
                if (!ArrayEqual(cast, movie.Cast)) throw new InvalidOperationException($"Stored cast mapping differs at movieLensId {m.Id}.");
                map.Write(JsonSerializer.SerializeToUtf8Bytes(new { movieLensId = m.Id, cast }, MappingOptions)); map.WriteByte(10);
            }
        }
        if (await r.ReadAsync(token)) throw new InvalidOperationException("Unexpected extra movie ID.");
        if (withCast && ActorImportBaselineV6.Hash(map.ToArray()) != "7de357a36207770d772ea351fedbeea7146b4dc6e13a41d3a518189523b2c0c8")
            throw new InvalidOperationException("Complete persisted ordered cast mapping digest differs from the approved release.");
    }

    private static async Task WriteCastAsync(NpgsqlConnection c, NpgsqlTransaction tx, ActorCatalogV6Document catalog, ActorImportFaultV6 fault, CancellationToken token)
    {
        await using var cmd = new NpgsqlCommand("UPDATE public.movies SET \"cast\"=@cast WHERE movie_lens_id=@id", c, tx);
        var id = cmd.Parameters.Add("id", NpgsqlDbType.Bigint); var cast = cmd.Parameters.Add("cast", NpgsqlDbType.Array | NpgsqlDbType.Text);
        for (var i = 0; i < catalog.Movies.Count; i++)
        {
            var m = catalog.Movies[i]; id.Value = m.MovieLensId; cast.Value = m.Cast is null ? DBNull.Value : m.Cast.ToArray();
            if (await cmd.ExecuteNonQueryAsync(token) != 1) throw new InvalidOperationException("Cast update did not match exactly one movie.");
            if (i == 0) ThrowFault(fault, ActorImportFaultV6.PartialCast);
        }
    }

    private static async Task WriteVectorsAsync(NpgsqlConnection c, NpgsqlTransaction tx, ActorVectorArtifactV6 en, ActorVectorArtifactV6 sr, ActorImportFaultV6 fault, CancellationToken token)
    {
        await using var cmd = new NpgsqlCommand("UPDATE public.movie_embeddings SET embedding=@en,document_fingerprint=@enfp,embedding_sr=@sr,document_fingerprint_sr=@srfp WHERE movie_lens_id=@id", c, tx);
        var id = cmd.Parameters.Add("id", NpgsqlDbType.Bigint); var a = cmd.Parameters.AddWithValue("en", new Vector(new float[768]));
        var afp = cmd.Parameters.Add("enfp", NpgsqlDbType.Text); var b = cmd.Parameters.AddWithValue("sr", new Vector(new float[768])); var bfp = cmd.Parameters.Add("srfp", NpgsqlDbType.Text);
        for (var i = 0; i < en.Records.Count; i++)
        {
            id.Value = en.Records[i].MovieLensId; a.Value = new Vector(en.Records[i].Values); afp.Value = en.Records[i].Fingerprint;
            b.Value = new Vector(sr.Records[i].Values); bfp.Value = sr.Records[i].Fingerprint;
            if (await cmd.ExecuteNonQueryAsync(token) != 1) throw new InvalidOperationException("Paired vector update did not match exactly one movie.");
            if (i == 0) ThrowFault(fault, ActorImportFaultV6.PartialVectors);
        }
    }

    private static async Task WriteStatesAsync(NpgsqlConnection c, NpgsqlTransaction tx, Inputs i, ActorImportFaultV6 fault, CancellationToken token)
    {
        var catalog = i.Catalog;
        await using (var cmd = new NpgsqlCommand("UPDATE public.catalog_import_state SET catalog_version=@version,catalog_jsonl_sha256=@sha,catalog_content_fingerprint=@identity,embedding_artifact_sha256=@artifact,embedding_profile_fingerprint=@profile WHERE id=1", c, tx))
        {
            cmd.Parameters.AddWithValue("version", catalog.CatalogVersion); cmd.Parameters.AddWithValue("sha", catalog.CatalogSha256);
            cmd.Parameters.AddWithValue("identity", catalog.IdentitySha256); cmd.Parameters.AddWithValue("artifact", i.En.ArtifactSha256); cmd.Parameters.AddWithValue("profile", catalog.ProfileFingerprint);
            if (await cmd.ExecuteNonQueryAsync(token) != 1) throw new InvalidOperationException("Catalog state update did not match exactly one row.");
        }
        ThrowFault(fault, ActorImportFaultV6.PartialState);
        foreach (var artifact in new[] { i.En, i.Sr })
        {
            var isSr = artifact.Language == "sr";
            await using var cmd = new NpgsqlCommand("UPDATE public.embedding_set_state SET profile_fingerprint=@profile,catalog_content_fingerprint=@identity,corpus_sha256=@corpus,text_format_version=@format,artifact_sha256=@artifact,translation_dictionary_sha256=@dictionary WHERE language=@language", c, tx);
            cmd.Parameters.AddWithValue("profile", catalog.ProfileFingerprint); cmd.Parameters.AddWithValue("identity", catalog.IdentitySha256);
            cmd.Parameters.AddWithValue("corpus", isSr ? catalog.SrCorpusSha256 : catalog.EnCorpusSha256); cmd.Parameters.AddWithValue("format", isSr ? catalog.SrTextFormatVersion : catalog.EnTextFormatVersion);
            cmd.Parameters.AddWithValue("artifact", artifact.ArtifactSha256); cmd.Parameters.AddWithValue("language", artifact.Language);
            cmd.Parameters.Add("dictionary", NpgsqlDbType.Text).Value = isSr ? catalog.DictionarySha256 : DBNull.Value;
            if (await cmd.ExecuteNonQueryAsync(token) != 1) throw new InvalidOperationException("Language state update did not match exactly one row.");
        }
        var schema = await ActorSchemaV6.ReadAsync(c, tx, false, token); ActorSchemaV6.ValidateFinalSnapshot(schema, i.Migration003);
        await using var actor = new NpgsqlCommand("INSERT INTO public.actor_catalog_state(id,schema_version,release_version,catalog_identity_sha256,actor_vocabulary_sha256,ordered_cast_mapping_sha256,overrides_sha256,cast_rules_sha256,paired_descriptor_sha256,baseline_descriptor_sha256,schema_sha256) VALUES(1,'actor-catalog-state-v1',@version,@identity,@vocabulary,@mapping,@overrides,@rules,@pair,@baseline,@schema)", c, tx);
        actor.Parameters.AddWithValue("version", catalog.CatalogVersion); actor.Parameters.AddWithValue("identity", catalog.IdentitySha256);
        actor.Parameters.AddWithValue("vocabulary", catalog.ActorVocabularySha256); actor.Parameters.AddWithValue("mapping", catalog.OrderedCastMappingSha256);
        actor.Parameters.AddWithValue("overrides", catalog.OverridesSha256); actor.Parameters.AddWithValue("rules", catalog.CastRulesSha256);
        actor.Parameters.AddWithValue("pair", PairSha256); actor.Parameters.AddWithValue("baseline", i.Baseline.DescriptorSha256); actor.Parameters.AddWithValue("schema", ActorImportBaselineV6.Hash(schema));
        await actor.ExecuteNonQueryAsync(token);
    }

    private static async Task VerifyFinalAsync(NpgsqlConnection c, NpgsqlTransaction tx, Inputs i, CancellationToken token)
    {
        await VerifyLedgerAsync(c, tx, true, token);
        var baseSchema = await ActorSchemaV6.ReadAsync(c, tx, true, token);
        ActorSchemaV6.ValidateBaseSnapshot(baseSchema);
        if (baseSchema != i.Baseline.SchemaSnapshot) throw new InvalidOperationException("Existing 001+002 schema objects were changed or unexpected objects exist.");
        var schema = await ActorSchemaV6.ReadAsync(c, tx, false, token); ActorSchemaV6.ValidateFinalSnapshot(schema, i.Migration003);
        var digest = await ScalarAsync(c, tx, $"SELECT md5(string_agg(md5(row_to_json(t)::text), ',' ORDER BY movie_lens_id)) FROM (SELECT {ActorImportBaselineV6.OldMovieColumns} FROM public.movies) t", token);
        if (digest != i.Baseline.Digests["oldMovieColumns"]) throw new InvalidOperationException("Original eleven metadata columns changed.");
        var ledger = await ScalarAsync(c, tx, "SELECT md5(string_agg(row_to_json(t)::text, ',' ORDER BY version)) FROM (SELECT * FROM public.schema_migrations WHERE version IN ('001_initial_schema','002_serbian_search_vectors')) t", token);
        if (ledger != i.Baseline.Digests["migrations"]) throw new InvalidOperationException("Original migration rows/timestamps changed.");
        await VerifyMoviesAsync(c, tx, i.Catalog.Movies, true, token);
        await VerifyStatesAsync(c, tx, i.Catalog, i.En, i.Sr, token);
        await using (var cmd = new NpgsqlCommand("SELECT schema_version,release_version,catalog_identity_sha256,actor_vocabulary_sha256,ordered_cast_mapping_sha256,overrides_sha256,cast_rules_sha256,paired_descriptor_sha256,baseline_descriptor_sha256,schema_sha256 FROM public.actor_catalog_state ORDER BY id", c, tx))
        await using (var r = await cmd.ExecuteReaderAsync(token))
        {
            var expected = new[] { "actor-catalog-state-v1", i.Catalog.CatalogVersion, i.Catalog.IdentitySha256, i.Catalog.ActorVocabularySha256,
                i.Catalog.OrderedCastMappingSha256, i.Catalog.OverridesSha256, i.Catalog.CastRulesSha256, PairSha256, i.Baseline.DescriptorSha256, ActorImportBaselineV6.Hash(schema) };
            if (!await r.ReadAsync(token) || expected.Where((x, n) => r.IsDBNull(n) || r.GetString(n) != x).Any() || await r.ReadAsync(token))
                throw new InvalidOperationException("Actor state differs from the complete manifest-linked V6 release and baseline.");
        }
        await using (var cmd = new NpgsqlCommand("SELECT movie_lens_id,document_fingerprint,embedding,document_fingerprint_sr,embedding_sr FROM public.movie_embeddings ORDER BY movie_lens_id", c, tx))
        await using (var r = await cmd.ExecuteReaderAsync(token))
        {
            for (var n = 0; n < i.En.Records.Count; n++)
            {
                var en = i.En.Records[n]; var sr = i.Sr.Records[n];
                if (!await r.ReadAsync(token) || r.GetInt64(0) != en.MovieLensId || r.IsDBNull(1) || r.GetString(1) != en.Fingerprint || r.IsDBNull(2) || !r.GetFieldValue<Vector>(2).Memory.Span.SequenceEqual(en.Values) ||
                    r.IsDBNull(3) || r.GetString(3) != sr.Fingerprint || r.IsDBNull(4) || !r.GetFieldValue<Vector>(4).Memory.Span.SequenceEqual(sr.Values))
                    throw new InvalidOperationException($"Full paired vector/fingerprint verification failed at movieLensId {en.MovieLensId}.");
            }
            if (await r.ReadAsync(token)) throw new InvalidOperationException("Unexpected extra paired vector row.");
        }
    }

    internal static bool NumericEqual(string? a, string? b) => a is null ? b is null : b is not null && CanonicalNumber(a) == CanonicalNumber(b);
    private static (BigInteger Mantissa, int Scale) CanonicalNumber(string value)
    {
        // Exact arbitrary-precision comparison. No decimal/double conversion or source rounding.
        var parts = value.Split(['e', 'E']);
        var exponent = parts.Length == 2 ? int.Parse(parts[1], CultureInfo.InvariantCulture) : 0;
        var dot = parts[0].IndexOf('.'); var scale = (dot < 0 ? 0 : parts[0].Length - dot - 1) - exponent;
        var mantissa = BigInteger.Parse(parts[0].Replace(".", "", StringComparison.Ordinal), CultureInfo.InvariantCulture);
        if (mantissa.IsZero) return (BigInteger.Zero, 0);
        while (mantissa % 10 == 0) { mantissa /= 10; scale--; }
        return (mantissa, scale);
    }
    private static bool ArrayEqual(IReadOnlyList<string>? a, IReadOnlyList<string>? b) => a is null ? b is null : b is not null && a.SequenceEqual(b, StringComparer.Ordinal);
    private static void ThrowFault(ActorImportFaultV6 actual, ActorImportFaultV6 point)
    { if (actual == point) throw new InvalidOperationException($"Test-only V6 fault at {point}; the transaction will roll back."); }
    internal static async Task<string> ReadMigrationAsync(string directory, string file, string sha, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("Migration directory must be absolute.");
        var bytes = await File.ReadAllBytesAsync(Path.Combine(directory, file), token);
        if (ActorImportBaselineV6.Hash(bytes) != sha) throw new InvalidDataException($"Migration {file} bytes differ from the exact approved checksum.");
        return new UTF8Encoding(false, true).GetString(bytes);
    }
    internal static async Task ExecuteAsync(NpgsqlConnection c, NpgsqlTransaction tx, string sql, CancellationToken token)
    { await using var cmd = new NpgsqlCommand(sql, c, tx); await cmd.ExecuteNonQueryAsync(token); }
    internal static async Task<string> ScalarAsync(NpgsqlConnection c, NpgsqlTransaction tx, string sql, CancellationToken token)
    { await using var cmd = new NpgsqlCommand(sql, c, tx); return Convert.ToString(await cmd.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) ?? ""; }
}
