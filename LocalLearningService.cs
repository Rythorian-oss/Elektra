using System.Collections.Concurrent;
using System.Globalization;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML;
using Microsoft.ML.Data;
using Elektra.Configuration;

namespace Elektra.Learning;

public sealed record IntentPrediction(string Label, float Confidence);
public sealed record LearningMetrics(
    int ExampleCount,
    double? ModelAccuracy,
    double? BaselineAccuracy,
    DateTimeOffset? LastTrainingUtc,
    bool HasPreviousModel,
    double? MacroPrecision = null,
    double? MacroRecall = null,
    IReadOnlyDictionary<string, int>? ExamplesByIntent = null);
public sealed record LearningReviewItem(long Id, string Kind, string Query, string Value, string? Label, DateTimeOffset CreatedAtUtc);
public sealed record UserMemory(long Id, string Key, string Value, DateTimeOffset UpdatedAtUtc);
public sealed record VoiceSampleMetrics(int TotalSamples, int TrainingSamples, int ValidationSamples);

public sealed class LocalLearningService : IDisposable
{
    public const string KnowledgeIntent = "KnowledgeQuestion";
    public const string LocationIntent = "LocationSearch";

    private const int MinimumExamplesPerIntent = 8;
    private const int MaximumVoiceSamples = 500;
    private const int VoiceSampleRate = 16000;
    private static readonly string[] LocationPrefixes = ["find ", "locate ", "search for ", "take me to ", "go to ", "show me ", "google earth ", "open ", "show "];
    private readonly UserPreferencesService _preferences;
    private readonly ILogger<LocalLearningService> _logger;
    private readonly string _voiceSamplesDirectory;
    private readonly string _connectionString;
    private readonly string _modelPath;
    private readonly string _previousModelPath;
    private readonly int _minimumExamples;
    private readonly int _minimumTrainingIntervalHours;
    private readonly float _minimumConfidence;
    private readonly float _minimumAccuracy;
    private readonly SemaphoreSlim _dbLock = new(1, 1);
    private readonly SemaphoreSlim _modelLock = new(1, 1);
    private readonly ConcurrentDictionary<string, string> _locationCorrections = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _speechCorrections = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, double> _providerScoreCache = new(StringComparer.Ordinal);
    private readonly MLContext _mlContext = new(seed: 41);
    private ITransformer? _model;
    private PredictionEngine<IntentExample, IntentPredictionOutput>? _predictionEngine;
    private int _storageAvailable;
    private string _status = "Local learning is off. Opt in from a response to begin.";

    public LocalLearningService(
        UserPreferencesService preferences,
        IOptions<AssistantOptions> options,
        ILogger<LocalLearningService> logger)
        : this(preferences, options, logger,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Elektra"))
    {
    }

    public LocalLearningService(
        UserPreferencesService preferences,
        IOptions<AssistantOptions> options,
        ILogger<LocalLearningService> logger,
        string storageDirectory)
    {
        _preferences = preferences;
        _logger = logger;
        string directory = storageDirectory;
        _voiceSamplesDirectory = Path.Combine(directory, "voice-samples");
        _connectionString = $"Data Source={Path.Combine(directory, "learning.db")};Pooling=True;";
        _modelPath = Path.Combine(directory, "intent-model.zip");
        _previousModelPath = Path.Combine(directory, "intent-model.previous.zip");
        _minimumExamples = Math.Clamp(options.Value.LearningMinExamples, 12, 100_000);
        _minimumTrainingIntervalHours = Math.Clamp(options.Value.LearningTrainingIntervalHours, 1, 24 * 30);
        _minimumConfidence = Math.Clamp(options.Value.LearningIntentConfidenceThreshold, 0.5f, 0.99f);
        _minimumAccuracy = Math.Clamp(options.Value.LearningMinimumValidationAccuracy, 0.5f, 0.99f);

        try
        {
            Directory.CreateDirectory(directory);
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
            CREATE TABLE IF NOT EXISTS TrainingExamples (
                Id INTEGER PRIMARY KEY,
                Utterance TEXT NOT NULL,
                Label TEXT NOT NULL,
                FeedbackType TEXT NOT NULL DEFAULT 'IntentCorrection',
                CreatedAtUtc INTEGER NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_TrainingExamples_Utterance
                ON TrainingExamples (lower(trim(Utterance)));
            CREATE TABLE IF NOT EXISTS LocationCorrections (
                UtteranceKey TEXT PRIMARY KEY,
                CorrectedPlace TEXT NOT NULL,
                UpdatedAtUtc INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS SpeechCorrections (
                UtteranceKey TEXT PRIMARY KEY,
                CorrectedUtterance TEXT NOT NULL,
                UpdatedAtUtc INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS UserMemories (
                Id INTEGER PRIMARY KEY,
                MemoryKey TEXT NOT NULL UNIQUE,
                MemoryValue TEXT NOT NULL,
                UpdatedAtUtc INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS FeedbackEvents (
                UtteranceKey TEXT PRIMARY KEY,
                FeedbackType TEXT NOT NULL,
                CreatedAtUtc INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS ProviderFeedbackEvents (
                UtteranceKey TEXT NOT NULL,
                Provider TEXT NOT NULL,
                WasHelpful INTEGER NOT NULL,
                CreatedAtUtc INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (UtteranceKey, Provider)
            );
            CREATE TABLE IF NOT EXISTS LearningState (
                Id INTEGER PRIMARY KEY CHECK (Id = 1),
                LastTrainingUtc INTEGER NULL,
                ModelAccuracy REAL NULL,
                BaselineAccuracy REAL NULL,
                MacroPrecision REAL NULL,
                MacroRecall REAL NULL,
                ExampleCount INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS VoiceSamples (
                Id INTEGER PRIMARY KEY,
                RecognizedText TEXT NOT NULL,
                VerifiedText TEXT NOT NULL,
                FileName TEXT NOT NULL UNIQUE,
                DataSplit TEXT NOT NULL CHECK (DataSplit IN ('Training', 'Validation')),
                CreatedAtUtc INTEGER NOT NULL
            );
            INSERT OR IGNORE INTO LearningState (Id) VALUES (1);
            """;
            command.ExecuteNonQuery();
            EnsureColumn(connection, "TrainingExamples", "FeedbackType", "TEXT NOT NULL DEFAULT 'IntentCorrection'");
            EnsureColumn(connection, "LocationCorrections", "UpdatedAtUtc", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(connection, "ProviderFeedbackEvents", "CreatedAtUtc", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(connection, "LearningState", "MacroPrecision", "REAL NULL");
            EnsureColumn(connection, "LearningState", "MacroRecall", "REAL NULL");
            if (_preferences.Current.EnableLocalLearning)
            {
                LoadCorrectionCache(connection, "LocationCorrections", "CorrectedPlace", _locationCorrections);
                LoadCorrectionCache(connection, "SpeechCorrections", "CorrectedUtterance", _speechCorrections);
            }
            Volatile.Write(ref _storageAvailable, 1);
            Interlocked.Exchange(ref _status, File.Exists(_modelPath)
                ? "A locally validated model is available and will load on first inference."
                : "Local learning is enabled; collecting labeled examples for evaluation.");
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Local learning storage could not initialize; deterministic behavior remains active.");
            Interlocked.Exchange(ref _status, "Local learning storage is unavailable; see the application log. Deterministic behavior remains active.");
        }
    }

    public bool IsEnabled => _preferences.Current.EnableLocalLearning && Volatile.Read(ref _storageAvailable) != 0;
    public bool IsVoiceSampleCollectionEnabled => IsEnabled && _preferences.Current.EnableVoskVoiceSamples;
    public bool IsAutomaticVoicePracticeEnabled =>
        IsVoiceSampleCollectionEnabled && _preferences.Current.EnableAutomaticVoicePracticeLessons;
    public bool IsTrainingPaused => _preferences.Current.PauseLearningTraining;

    public void SetLearningEnabled(bool enabled)
    {
        _preferences.Update(preferences => preferences with
        {
            EnableLocalLearning = enabled,
            EnableVoskVoiceSamples = enabled && preferences.EnableVoskVoiceSamples,
            EnableAutomaticVoicePracticeLessons = enabled && preferences.EnableVoskVoiceSamples && preferences.EnableAutomaticVoicePracticeLessons
        });
        if (!enabled)
        {
            _locationCorrections.Clear();
            _speechCorrections.Clear();
            _providerScoreCache.Clear();
            lock (_modelLock)
            {
                _model = null;
                _predictionEngine = null;
            }
            return;
        }

        if (Volatile.Read(ref _storageAvailable) == 0)
            return;
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            LoadCorrectionCache(connection, "LocationCorrections", "CorrectedPlace", _locationCorrections);
            LoadCorrectionCache(connection, "SpeechCorrections", "CorrectedUtterance", _speechCorrections);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not load local correction vocabulary after learning was enabled.");
            Interlocked.Exchange(ref _status, "Correction data could not be loaded; see the application log.");
        }
    }

    public string Status => !_preferences.Current.EnableLocalLearning
        ? "Local learning is off. Opt in from a response to begin."
        : Volatile.Read(ref _status);

    public IntentPrediction? PredictIntent(string utterance)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(utterance))
            return null;

        lock (_modelLock)
        {
            try
            {
                if (_predictionEngine == null && File.Exists(_modelPath))
                    LoadModelLocked();
                if (_predictionEngine == null)
                    return null;
                IntentPredictionOutput prediction = _predictionEngine.Predict(new IntentExample { Text = utterance });
                float confidence = SoftmaxConfidence(prediction.Score);
                return confidence >= _minimumConfidence
                    ? new IntentPrediction(prediction.PredictedLabel, confidence)
                    : null;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not AccessViolationException)
            {
                _predictionEngine = null;
                _model = null;
                _logger.LogError(ex, "Intent inference failed; deterministic command handling will be used.");
                Interlocked.Exchange(ref _status, "Intent inference failed; deterministic command handling remains active.");
                return null;
            }
        }
    }

    public string? GetCorrectedLocation(string utterance) =>
        IsEnabled && !string.IsNullOrWhiteSpace(utterance) && _locationCorrections.TryGetValue(Normalize(utterance), out string? place)
            ? place
            : null;

    public string? GetCorrectedSpeechUtterance(string utterance) =>
        IsEnabled && !string.IsNullOrWhiteSpace(utterance) && _speechCorrections.TryGetValue(Normalize(utterance), out string? corrected)
            ? corrected
            : null;

    public async Task RecordSpeechCorrectionAsync(string recognizedUtterance, string correctedUtterance, CancellationToken ct = default)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(recognizedUtterance) || string.IsNullOrWhiteSpace(correctedUtterance))
            return;
        if (recognizedUtterance.Length > 500 || correctedUtterance.Length > 500)
            throw new ArgumentException("Speech corrections must be 500 characters or fewer.");

        string key = Normalize(recognizedUtterance);
        string value = correctedUtterance.Trim();
        await _dbLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO SpeechCorrections (UtteranceKey, CorrectedUtterance, UpdatedAtUtc) VALUES ($key, $value, $time) ON CONFLICT(UtteranceKey) DO UPDATE SET CorrectedUtterance = excluded.CorrectedUtterance, UpdatedAtUtc = excluded.UpdatedAtUtc; DELETE FROM SpeechCorrections WHERE UtteranceKey NOT IN (SELECT UtteranceKey FROM SpeechCorrections ORDER BY UpdatedAtUtc DESC LIMIT 1000);";
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value);
            command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            _speechCorrections.Clear();
            LoadCorrectionCache(connection, "SpeechCorrections", "CorrectedUtterance", _speechCorrections);
            Interlocked.Exchange(ref _status, "Speech correction saved locally and will apply to exact matching transcripts.");
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public async Task<bool> RecordVerifiedVoiceSampleAsync(byte[] pcm16KhzMono, string recognizedText, string verifiedText, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pcm16KhzMono);
        if (!IsVoiceSampleCollectionEnabled || string.IsNullOrWhiteSpace(recognizedText) || string.IsNullOrWhiteSpace(verifiedText))
            return false;
        if (recognizedText.Length > 500 || verifiedText.Length > 500 || pcm16KhzMono.Length < VoiceSampleRate * sizeof(short) / 5 ||
            pcm16KhzMono.Length > VoiceSampleRate * sizeof(short) * 15 || pcm16KhzMono.Length % sizeof(short) != 0)
            throw new ArgumentException("Voice samples must be 0.2 to 15 seconds of mono 16 kHz PCM and have transcripts no longer than 500 characters.");

        await _dbLock.WaitAsync(ct).ConfigureAwait(false);
        string fileName = $"{Guid.NewGuid():N}.wav";
        string destination = Path.Combine(_voiceSamplesDirectory, fileName);
        string temporary = destination + ".tmp";
        bool persisted = false;
        try
        {
            if (!IsVoiceSampleCollectionEnabled)
                return false;

            Directory.CreateDirectory(_voiceSamplesDirectory);
            await File.WriteAllBytesAsync(temporary, CreateWaveFile(pcm16KhzMono), ct).ConfigureAwait(false);
            if (!IsVoiceSampleCollectionEnabled)
                return false;
            File.Move(temporary, destination);

            var expiredFiles = new List<string>();
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

            string normalizedVerifiedText = Normalize(verifiedText);
            await using var existing = connection.CreateCommand();
            existing.Transaction = transaction;
            existing.CommandText = "SELECT DataSplit FROM VoiceSamples WHERE lower(trim(VerifiedText)) = lower(trim($text)) LIMIT 1;";
            existing.Parameters.AddWithValue("$text", normalizedVerifiedText);
            string? existingSplit = await existing.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
            string split = existingSplit ?? await GetNextVoiceSampleSplitAsync(connection, transaction, ct).ConfigureAwait(false);

            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO VoiceSamples (RecognizedText, VerifiedText, FileName, DataSplit, CreatedAtUtc) VALUES ($recognized, $verified, $file, $split, $time);";
            insert.Parameters.AddWithValue("$recognized", recognizedText.Trim());
            insert.Parameters.AddWithValue("$verified", verifiedText.Trim());
            insert.Parameters.AddWithValue("$file", fileName);
            insert.Parameters.AddWithValue("$split", split);
            insert.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            await using (var expired = connection.CreateCommand())
            {
                expired.Transaction = transaction;
                expired.CommandText = "SELECT FileName FROM VoiceSamples ORDER BY Id DESC LIMIT -1 OFFSET $limit;";
                expired.Parameters.AddWithValue("$limit", MaximumVoiceSamples);
                await using var reader = await expired.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    expiredFiles.Add(reader.GetString(0));
            }
            await using (var prune = connection.CreateCommand())
            {
                prune.Transaction = transaction;
                prune.CommandText = "DELETE FROM VoiceSamples WHERE Id NOT IN (SELECT Id FROM VoiceSamples ORDER BY Id DESC LIMIT $limit);";
                prune.Parameters.AddWithValue("$limit", MaximumVoiceSamples);
                await prune.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            if (!IsVoiceSampleCollectionEnabled)
                return false;
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            persisted = true;

            foreach (string expiredFile in expiredFiles)
                File.Delete(Path.Combine(_voiceSamplesDirectory, expiredFile));
            Interlocked.Exchange(ref _status, $"Verified voice sample saved locally ({split.ToLowerInvariant()} set). Vosk model training requires a separate compatible offline Kaldi/Vosk toolchain.");
            return true;
        }
        finally
        {
            if (!persisted)
            {
                if (File.Exists(temporary)) File.Delete(temporary);
                if (File.Exists(destination)) File.Delete(destination);
            }
            _dbLock.Release();
        }
    }

    public async Task<string> GetVoicePracticePhraseAsync(string? preferredPhrase = null, CancellationToken ct = default)
    {
        if (!IsVoiceSampleCollectionEnabled)
            throw new InvalidOperationException("Voice practice requires local learning and separate voice-sample consent.");

        if (IsEligibleVoicePracticePhrase(preferredPhrase))
            return preferredPhrase!.Trim();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var correction = connection.CreateCommand();
        correction.CommandText = "SELECT CorrectedUtterance FROM SpeechCorrections WHERE length(trim(CorrectedUtterance)) BETWEEN 1 AND 160 ORDER BY UpdatedAtUtc DESC LIMIT 1;";
        string? phrase = await correction.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        if (IsEligibleVoicePracticePhrase(phrase))
            return phrase!.Trim();

        await using var sample = connection.CreateCommand();
        sample.CommandText = "SELECT VerifiedText FROM VoiceSamples WHERE length(trim(VerifiedText)) BETWEEN 1 AND 160 ORDER BY Id DESC LIMIT 1;";
        phrase = await sample.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        return IsEligibleVoicePracticePhrase(phrase) ? phrase!.Trim() : "Elektra, what is a wolf?";
    }

    private static bool IsEligibleVoicePracticePhrase(string? phrase) =>
        !string.IsNullOrWhiteSpace(phrase) && phrase.Trim().Length <= 160 && phrase.All(character => !char.IsControl(character));

    public async Task<VoiceSampleMetrics> GetVoiceSampleMetricsAsync(CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*), SUM(CASE WHEN DataSplit = 'Training' THEN 1 ELSE 0 END), SUM(CASE WHEN DataSplit = 'Validation' THEN 1 ELSE 0 END) FROM VoiceSamples;";
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await reader.ReadAsync(ct).ConfigureAwait(false);
        return new VoiceSampleMetrics(reader.GetInt32(0), reader.IsDBNull(1) ? 0 : reader.GetInt32(1), reader.IsDBNull(2) ? 0 : reader.GetInt32(2));
    }

    private static async Task<string> GetNextVoiceSampleSplitAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        await using var count = connection.CreateCommand();
        count.Transaction = transaction;
        count.CommandText = "SELECT COUNT(DISTINCT lower(trim(VerifiedText))) FROM VoiceSamples;";
        long existingCount = (long)(await count.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0L);
        return existingCount % 5 == 4 ? "Validation" : "Training";
    }

    private static byte[] CreateWaveFile(byte[] pcm)
    {
        byte[] wave = new byte[44 + pcm.Length];
        "RIFF"u8.CopyTo(wave);
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(4, 4), 36 + pcm.Length);
        "WAVEfmt "u8.CopyTo(wave.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(22, 2), 1);
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(24, 4), VoiceSampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(28, 4), VoiceSampleRate * sizeof(short));
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(32, 2), sizeof(short));
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(34, 2), sizeof(short) * 8);
        "data"u8.CopyTo(wave.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(40, 4), pcm.Length);
        pcm.CopyTo(wave, 44);
        return wave;
    }

    public async Task RecordNegativeFeedbackAsync(string utterance, string feedbackType, string provider = "", CancellationToken ct = default)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(utterance) || utterance.Length > 500 ||
            feedbackType is not ("WrongAnswer" or "WrongIntent"))
            return;

        await _dbLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO FeedbackEvents (UtteranceKey, FeedbackType, CreatedAtUtc) VALUES ($key, $type, $time) ON CONFLICT(UtteranceKey) DO UPDATE SET FeedbackType = excluded.FeedbackType, CreatedAtUtc = excluded.CreatedAtUtc; DELETE FROM FeedbackEvents WHERE UtteranceKey NOT IN (SELECT UtteranceKey FROM FeedbackEvents ORDER BY CreatedAtUtc DESC LIMIT 10000);";
            command.Parameters.AddWithValue("$key", Normalize(utterance));
            command.Parameters.AddWithValue("$type", feedbackType);
            command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await RecordProviderFeedbackAsync(connection, utterance, provider, positive: false, ct, transaction).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(provider))
                _providerScoreCache.TryRemove(NormalizeProviderName(provider), out _);
            Interlocked.Exchange(ref _status, "Negative feedback and provider rating saved locally; no model is changed automatically.");
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public async Task SaveMemoryAsync(string key, string value, CancellationToken ct = default)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
            return;
        if (key.Length > 120 || value.Length > 1000)
            throw new ArgumentException("Memory keys must be 120 characters or fewer and values 1,000 characters or fewer.");

        await _dbLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO UserMemories (MemoryKey, MemoryValue, UpdatedAtUtc) VALUES ($key, $value, $time) ON CONFLICT(MemoryKey) DO UPDATE SET MemoryValue = excluded.MemoryValue, UpdatedAtUtc = excluded.UpdatedAtUtc; DELETE FROM UserMemories WHERE Id NOT IN (SELECT Id FROM UserMemories ORDER BY UpdatedAtUtc DESC LIMIT 500);";
            command.Parameters.AddWithValue("$key", key.Trim());
            command.Parameters.AddWithValue("$value", value.Trim());
            command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            Interlocked.Exchange(ref _status, "Explicit memory saved locally. You can review or delete it at any time.");
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public async Task<IReadOnlyList<UserMemory>> SearchMemoriesAsync(string searchText, CancellationToken ct = default)
    {
        if (!IsEnabled)
            return [];

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, MemoryKey, MemoryValue, UpdatedAtUtc FROM UserMemories WHERE $search = '' OR MemoryKey LIKE $pattern ESCAPE '\\' OR MemoryValue LIKE $pattern ESCAPE '\\' ORDER BY UpdatedAtUtc DESC LIMIT 100;";
        string escaped = EscapeLikePattern(searchText.Trim());
        command.Parameters.AddWithValue("$search", escaped);
        command.Parameters.AddWithValue("$pattern", $"%{escaped}%");
        var memories = new List<UserMemory>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            memories.Add(new UserMemory(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(3))));
        return memories;
    }

    public async Task DeleteMemoryAsync(long id, CancellationToken ct = default)
    {
        await _dbLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM UserMemories WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", id);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public async Task RecordWorkedAsync(string utterance, string label, string provider, CancellationToken ct = default)
    {
        if (!IsEnabled || !IsSupportedLabel(label) || string.IsNullOrWhiteSpace(utterance))
            return;

        await _dbLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await InsertExampleAsync(connection, utterance, label, ct, feedbackType: "Worked").ConfigureAwait(false);
            await RecordProviderFeedbackAsync(connection, utterance, provider, positive: true, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(provider))
                _providerScoreCache.TryRemove(NormalizeProviderName(provider), out _);
            Interlocked.Exchange(ref _status, "Feedback saved locally; the next scheduled evaluation will decide whether to update the model.");
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public async Task RecordCorrectionAsync(string originalUtterance, string correctedUtterance, string label, string provider, CancellationToken ct = default)
    {
        if (!IsEnabled || !IsSupportedLabel(label) || string.IsNullOrWhiteSpace(originalUtterance) || string.IsNullOrWhiteSpace(correctedUtterance))
            return;
        if (originalUtterance.Length > 500 || correctedUtterance.Length > 500)
            throw new ArgumentException("Feedback phrases must be 500 characters or fewer.");

        await _dbLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
            await InsertExampleAsync(connection, correctedUtterance, label, ct, transaction).ConfigureAwait(false);
            await InsertExampleAsync(connection, originalUtterance, label, ct, transaction).ConfigureAwait(false);
            if (label == LocationIntent)
            {
                await using var correction = connection.CreateCommand();
                correction.Transaction = transaction;
                correction.CommandText = "INSERT INTO LocationCorrections (UtteranceKey, CorrectedPlace, UpdatedAtUtc) VALUES ($key, $place, $time) ON CONFLICT(UtteranceKey) DO UPDATE SET CorrectedPlace = excluded.CorrectedPlace, UpdatedAtUtc = excluded.UpdatedAtUtc; DELETE FROM LocationCorrections WHERE UtteranceKey NOT IN (SELECT UtteranceKey FROM LocationCorrections ORDER BY UpdatedAtUtc DESC LIMIT 1000);";
                string key = Normalize(originalUtterance);
                string place = correctedUtterance.Trim();
                correction.Parameters.AddWithValue("$key", key);
                correction.Parameters.AddWithValue("$place", place);
                correction.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                await correction.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                _locationCorrections.Clear();
                LoadCorrectionCache(connection, "LocationCorrections", "CorrectedPlace", _locationCorrections);
            }
            await RecordProviderFeedbackAsync(connection, originalUtterance, provider, positive: false, ct, transaction).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(provider))
                _providerScoreCache.TryRemove(NormalizeProviderName(provider), out _);
            Interlocked.Exchange(ref _status, "Correction saved locally; the next scheduled evaluation will decide whether to update the model.");
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public double GetProviderScore(string provider)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(provider))
            return 0.5;
        provider = NormalizeProviderName(provider);
        if (_providerScoreCache.TryGetValue(provider, out double cachedScore))
            return cachedScore;

        try
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COALESCE(SUM(WasHelpful), 0), COUNT(*) - COALESCE(SUM(WasHelpful), 0) FROM ProviderFeedbackEvents WHERE Provider = $provider;";
            command.Parameters.AddWithValue("$provider", provider);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
                return 0.5;
            double score = (reader.GetInt32(0) + 1d) / (reader.GetInt32(0) + reader.GetInt32(1) + 2d);
            _providerScoreCache[provider] = score;
            return score;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read local provider feedback; using the default provider order.");
            return 0.5;
        }
    }

    public async Task<IReadOnlyList<LearningReviewItem>> GetLearningReviewItemsAsync(CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, 'Intent example', Utterance, Label, FeedbackType, CreatedAtUtc FROM TrainingExamples
            UNION ALL
            SELECT rowid, 'Speech correction', UtteranceKey, CorrectedUtterance, NULL, UpdatedAtUtc FROM SpeechCorrections
            UNION ALL
            SELECT rowid, 'Location correction', UtteranceKey, CorrectedPlace, NULL, UpdatedAtUtc FROM LocationCorrections
            UNION ALL
            SELECT Id, 'Explicit memory', MemoryKey, MemoryValue, NULL, UpdatedAtUtc FROM UserMemories
            UNION ALL
            SELECT rowid, 'Answer feedback', UtteranceKey, FeedbackType, NULL, CreatedAtUtc FROM FeedbackEvents
            UNION ALL
            SELECT rowid, 'Provider feedback', UtteranceKey, Provider, CASE WasHelpful WHEN 1 THEN 'Helpful' ELSE 'Not helpful' END, CreatedAtUtc FROM ProviderFeedbackEvents
            UNION ALL
            SELECT Id, 'Verified voice sample', RecognizedText, VerifiedText || ' (' || DataSplit || ')', DataSplit, CreatedAtUtc FROM VoiceSamples
            ORDER BY 6 DESC LIMIT 1000;
            """;
        var items = new List<LearningReviewItem>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            DateTimeOffset created = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(5));
            items.Add(new LearningReviewItem(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), created));
        }
        return items;
    }

    public async Task DeleteLearningReviewItemAsync(LearningReviewItem item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        await _dbLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(ct).ConfigureAwait(false);
            if (item.Kind is "Speech correction" or "Location correction")
            {
                string table = item.Kind == "Speech correction" ? "SpeechCorrections" : "LocationCorrections";
                ConcurrentDictionary<string, string> cache = item.Kind == "Speech correction" ? _speechCorrections : _locationCorrections;
                await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
                await using var lookup = connection.CreateCommand();
                lookup.Transaction = transaction;
                lookup.CommandText = $"SELECT UtteranceKey FROM {table} WHERE rowid = $id;";
                lookup.Parameters.AddWithValue("$id", item.Id);
                string? key = await lookup.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
                await using var deleteCorrection = connection.CreateCommand();
                deleteCorrection.Transaction = transaction;
                deleteCorrection.CommandText = $"DELETE FROM {table} WHERE rowid = $id;";
                deleteCorrection.Parameters.AddWithValue("$id", item.Id);
                await deleteCorrection.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                await transaction.CommitAsync(ct).ConfigureAwait(false);
                if (key != null)
                    cache.TryRemove(key, out _);
                return;
            }

            if (item.Kind == "Verified voice sample")
            {
                await using var lookup = connection.CreateCommand();
                lookup.CommandText = "SELECT FileName FROM VoiceSamples WHERE Id = $id;";
                lookup.Parameters.AddWithValue("$id", item.Id);
                string? fileName = await lookup.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
                if (fileName != null)
                    File.Delete(Path.Combine(_voiceSamplesDirectory, fileName));
                await using var deleteSample = connection.CreateCommand();
                deleteSample.CommandText = "DELETE FROM VoiceSamples WHERE Id = $id;";
                deleteSample.Parameters.AddWithValue("$id", item.Id);
                await deleteSample.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                return;
            }

            string deleteSql = item.Kind switch
            {
                "Intent example" => "DELETE FROM TrainingExamples WHERE Id = $id;",
                "Explicit memory" => "DELETE FROM UserMemories WHERE Id = $id;",
                "Answer feedback" => "DELETE FROM FeedbackEvents WHERE rowid = $id;",
                "Provider feedback" => "DELETE FROM ProviderFeedbackEvents WHERE rowid = $id;",
                _ => throw new ArgumentOutOfRangeException(nameof(item), item.Kind, "Unsupported learning review item type.")
            };
            await using var command = connection.CreateCommand();
            command.CommandText = deleteSql;
            command.Parameters.AddWithValue("$id", item.Id);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            if (item.Kind == "Provider feedback")
                _providerScoreCache.Clear();
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public async Task<LearningMetrics> GetMetricsAsync(CancellationToken ct = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT (SELECT COUNT(*) FROM TrainingExamples), ModelAccuracy, BaselineAccuracy,
                LastTrainingUtc, MacroPrecision, MacroRecall
            FROM LearningState WHERE Id = 1;
            """;
        int exampleCount;
        double? modelAccuracy;
        double? baselineAccuracy;
        DateTimeOffset? lastTraining;
        double? macroPrecision;
        double? macroRecall;
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            await reader.ReadAsync(ct).ConfigureAwait(false);
            exampleCount = reader.GetInt32(0);
            modelAccuracy = reader.IsDBNull(1) ? null : reader.GetDouble(1);
            baselineAccuracy = reader.IsDBNull(2) ? null : reader.GetDouble(2);
            lastTraining = reader.IsDBNull(3) ? null : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(3));
            macroPrecision = reader.IsDBNull(4) ? null : reader.GetDouble(4);
            macroRecall = reader.IsDBNull(5) ? null : reader.GetDouble(5);
        }
        var examplesByIntent = new Dictionary<string, int>(StringComparer.Ordinal);
        await using (var counts = connection.CreateCommand())
        {
            counts.CommandText = "SELECT Label, COUNT(*) FROM TrainingExamples GROUP BY Label;";
            await using var reader = await counts.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                examplesByIntent[reader.GetString(0)] = reader.GetInt32(1);
        }
        return new LearningMetrics(exampleCount, modelAccuracy, baselineAccuracy, lastTraining,
            File.Exists(_previousModelPath), macroPrecision, macroRecall, examplesByIntent);
    }

    public async Task<bool> TrainIfReadyAsync(CancellationToken ct)
    {
        if (!IsEnabled)
            return false;
        if (IsTrainingPaused)
        {
            Interlocked.Exchange(ref _status, "Scheduled training is paused. Existing models remain active and new feedback stays queued.");
            return false;
        }

        await _dbLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(ct).ConfigureAwait(false);
            var examples = new List<IntentExample>();
            await using (var read = connection.CreateCommand())
            {
                read.CommandText = "SELECT Utterance, Label FROM TrainingExamples ORDER BY Id;";
                await using var reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    examples.Add(new IntentExample { Text = reader.GetString(0), Label = reader.GetString(1) });
            }

            var counts = examples.GroupBy(e => e.Label).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            if (examples.Count < _minimumExamples || counts.Count < 2 || counts.Values.Any(count => count < MinimumExamplesPerIntent))
            {
                Interlocked.Exchange(ref _status, $"Collecting opt-in examples: {examples.Count}/{_minimumExamples}; at least {MinimumExamplesPerIntent} per intent are required.");
                return false;
            }

            await using (var lastTrain = connection.CreateCommand())
            {
                lastTrain.CommandText = "SELECT LastTrainingUtc FROM LearningState WHERE Id = 1;";
                object? value = await lastTrain.ExecuteScalarAsync(ct).ConfigureAwait(false);
                if (value is long timestamp && DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(timestamp) < TimeSpan.FromHours(_minimumTrainingIntervalHours))
                {
                    Interlocked.Exchange(ref _status, "Training is on cooldown to avoid overfitting. New feedback remains queued.");
                    return false;
                }
            }

            var trainExamples = new List<IntentExample>();
            var testExamples = new List<IntentExample>();
            var random = new Random(41);
            foreach (var group in examples.GroupBy(example => example.Label, StringComparer.Ordinal))
            {
                var shuffled = group.OrderBy(_ => random.Next()).ToArray();
                for (int i = 0; i < shuffled.Length; i++)
                {
                    if (i % 4 == 0)
                        testExamples.Add(shuffled[i]);
                    else
                        trainExamples.Add(shuffled[i]);
                }
            }
            IDataView trainingData = _mlContext.Data.LoadFromEnumerable(trainExamples);
            IDataView testData = _mlContext.Data.LoadFromEnumerable(testExamples);
            var pipeline = _mlContext.Transforms.Conversion.MapValueToKey("Label")
                .Append(_mlContext.Transforms.Text.FeaturizeText("Features", nameof(IntentExample.Text)))
                .Append(_mlContext.MulticlassClassification.Trainers.SdcaMaximumEntropy("Label", "Features"))
                .Append(_mlContext.Transforms.Conversion.MapKeyToValue("PredictedLabel"));
            ITransformer candidate = pipeline.Fit(trainingData);
            MulticlassClassificationMetrics metrics = _mlContext.MulticlassClassification.Evaluate(candidate.Transform(testData));
            (double macroPrecision, double macroRecall) = MeasureMacroPrecisionRecall(candidate, testExamples);
            double baselineAccuracy = MeasureBaselineAccuracy(testData);
            double modelAccuracy = metrics.MacroAccuracy;
            if (!IsEnabled)
                return false;
            if (modelAccuracy < _minimumAccuracy || modelAccuracy <= baselineAccuracy + 1e-9)
            {
                Interlocked.Exchange(ref _status, string.Format(CultureInfo.InvariantCulture,
                    "Candidate rejected: validation {0:P1} did not exceed the deterministic baseline {1:P1} or the minimum threshold; model unchanged.", modelAccuracy, baselineAccuracy));
                await SaveMetricsAsync(connection, examples.Count, modelAccuracy, baselineAccuracy,
                    macroPrecision, macroRecall, ct, updateTrainingTime: true).ConfigureAwait(false);
                return false;
            }

            string candidatePath = _modelPath + ".candidate";
            if (File.Exists(candidatePath))
                File.Delete(candidatePath);
            _mlContext.Model.Save(candidate, trainingData.Schema, candidatePath);
            if (File.Exists(_modelPath))
                File.Copy(_modelPath, _previousModelPath, overwrite: true);
            File.Move(candidatePath, _modelPath, overwrite: true);
            await _modelLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                _model = candidate;
                _predictionEngine = _mlContext.Model.CreatePredictionEngine<IntentExample, IntentPredictionOutput>(_model);
            }
            finally
            {
                _modelLock.Release();
            }
            await SaveMetricsAsync(connection, examples.Count, modelAccuracy, baselineAccuracy,
                macroPrecision, macroRecall, ct, updateTrainingTime: true).ConfigureAwait(false);
            string rollbackStatus = File.Exists(_previousModelPath)
                ? "The previous model is available for rollback."
                : "No earlier model existed to retain for rollback.";
            Interlocked.Exchange(ref _status, string.Format(CultureInfo.InvariantCulture,
                "Model updated: validation {0:P1} vs deterministic baseline {1:P1} on held-out examples. {2}", modelAccuracy, baselineAccuracy, rollbackStatus));
            _logger.LogInformation("Intent model promoted after held-out validation: {Accuracy:P1} accuracy vs {Baseline:P1} baseline across {ExampleCount} examples.", modelAccuracy, baselineAccuracy, examples.Count);
            return true;
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public async Task<bool> RollbackAsync(CancellationToken ct = default)
    {
        if (!IsEnabled || !File.Exists(_previousModelPath))
            return false;

        await _dbLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _modelLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                string rollbackPath = _modelPath + ".rollback";
                File.Copy(_modelPath, rollbackPath, overwrite: true);
                File.Copy(_previousModelPath, _modelPath, overwrite: true);
                File.Copy(rollbackPath, _previousModelPath, overwrite: true);
                File.Delete(rollbackPath);
                LoadModelLocked();

                await using var connection = new SqliteConnection(_connectionString);
                await connection.OpenAsync(ct).ConfigureAwait(false);
                await using var command = connection.CreateCommand();
                command.CommandText = "UPDATE LearningState SET ModelAccuracy = NULL, MacroPrecision = NULL, MacroRecall = NULL WHERE Id = 1;";
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                Interlocked.Exchange(ref _status, "Rolled back to the previous locally validated intent model. Validation metrics were cleared because they belong to the replaced model.");
                return true;
            }
            finally
            {
                _modelLock.Release();
            }
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public async Task ClearLearningDataAsync(CancellationToken ct = default)
    {
        await _dbLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(ct).ConfigureAwait(false);
            List<string> voiceFiles = [];
            await using (var listVoiceFiles = connection.CreateCommand())
            {
                listVoiceFiles.CommandText = "SELECT FileName FROM VoiceSamples;";
                await using var reader = await listVoiceFiles.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    voiceFiles.Add(reader.GetString(0));
            }
            foreach (string voiceFile in voiceFiles)
                File.Delete(Path.Combine(_voiceSamplesDirectory, voiceFile));
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM TrainingExamples; DELETE FROM LocationCorrections; DELETE FROM SpeechCorrections; DELETE FROM UserMemories; DELETE FROM ProviderFeedbackEvents; DELETE FROM FeedbackEvents; DELETE FROM VoiceSamples; UPDATE LearningState SET LastTrainingUtc = NULL, ModelAccuracy = NULL, BaselineAccuracy = NULL, MacroPrecision = NULL, MacroRecall = NULL, ExampleCount = 0 WHERE Id = 1;";
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            _locationCorrections.Clear();
            _speechCorrections.Clear();
            _providerScoreCache.Clear();
            await _modelLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                _model = null;
                _predictionEngine = null;
                if (File.Exists(_modelPath)) File.Delete(_modelPath);
                if (File.Exists(_previousModelPath)) File.Delete(_previousModelPath);
            }
            finally
            {
                _modelLock.Release();
            }
            Interlocked.Exchange(ref _status, "Local feedback, corrections, provider rankings, and trained models were deleted.");
        }
        finally
        {
            _dbLock.Release();
        }
    }

    public static string InferIntent(string source) =>
        source.Contains("Location", StringComparison.OrdinalIgnoreCase) ? LocationIntent : KnowledgeIntent;

    private void LoadModelLocked()
    {
        if (!File.Exists(_modelPath))
        {
            _model = null;
            _predictionEngine = null;
            return;
        }
        _model = _mlContext.Model.Load(_modelPath, out _);
        _predictionEngine = _mlContext.Model.CreatePredictionEngine<IntentExample, IntentPredictionOutput>(_model);
    }

    private static bool IsSupportedLabel(string label) => label is KnowledgeIntent or LocationIntent;

    private static void EnsureColumn(SqliteConnection connection, string table, string column, string definition)
    {
        using var check = connection.CreateCommand();
        check.CommandText = $"PRAGMA table_info({table});";
        using var reader = check.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return;
        }
        reader.Close();
        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        alter.ExecuteNonQuery();
    }

    private static void LoadCorrectionCache(SqliteConnection connection, string table, string valueColumn, ConcurrentDictionary<string, string> cache)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT UtteranceKey, {valueColumn} FROM {table} ORDER BY UpdatedAtUtc DESC LIMIT 1000;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
            cache[reader.GetString(0)] = reader.GetString(1);
    }

    private static string EscapeLikePattern(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (char character in text)
        {
            if (character is '\\' or '%' or '_')
                builder.Append('\\');
            builder.Append(character);
        }
        return builder.ToString();
    }

    private static string Normalize(string text) => string.Join(' ', text.Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static float SoftmaxConfidence(float[]? scores)
    {
        if (scores == null || scores.Length == 0)
            return 0;

        float maximum = float.NegativeInfinity;
        bool nonnegative = true;
        double total = 0;
        foreach (float score in scores)
        {
            if (!float.IsFinite(score))
                return 0;
            maximum = Math.Max(maximum, score);
            nonnegative &= score >= 0;
            total += score;
        }

        if (nonnegative)
            return total > 0 ? (float)(maximum / total) : 0;

        double exponentSum = 0;
        foreach (float score in scores)
            exponentSum += Math.Exp(score - maximum);
        return exponentSum > 0 ? (float)(1d / exponentSum) : 0;
    }

    private static string NormalizeProviderName(string provider)
    {
        string name = provider.Trim();
        if (name.StartsWith("Wikipedia", StringComparison.OrdinalIgnoreCase))
            return "Wikipedia";
        if (name.StartsWith("DuckDuckGo", StringComparison.OrdinalIgnoreCase))
            return "DuckDuckGo";
        if (name.StartsWith("Basic arithmetic", StringComparison.OrdinalIgnoreCase))
            return "Basic arithmetic";
        if (name.StartsWith("Open-Meteo", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Local clock", StringComparison.OrdinalIgnoreCase))
            return "Waterville local time and Open-Meteo";
        return name;
    }

    private (double Precision, double Recall) MeasureMacroPrecisionRecall(ITransformer candidate, IReadOnlyList<IntentExample> examples)
    {
        using var engine = _mlContext.Model.CreatePredictionEngine<IntentExample, IntentPredictionOutput>(candidate);
        var counts = examples.Select(example => (Actual: example.Label, Predicted: engine.Predict(example).PredictedLabel))
            .ToArray();
        string[] labels = examples.Select(example => example.Label).Distinct(StringComparer.Ordinal).ToArray();
        double precision = 0;
        double recall = 0;
        foreach (string label in labels)
        {
            int truePositive = counts.Count(item => item.Actual == label && item.Predicted == label);
            int falsePositive = counts.Count(item => item.Actual != label && item.Predicted == label);
            int falseNegative = counts.Count(item => item.Actual == label && item.Predicted != label);
            precision += truePositive + falsePositive == 0 ? 0 : truePositive / (double)(truePositive + falsePositive);
            recall += truePositive + falseNegative == 0 ? 0 : truePositive / (double)(truePositive + falseNegative);
        }
        return labels.Length == 0 ? (0, 0) : (precision / labels.Length, recall / labels.Length);
    }

    private double MeasureBaselineAccuracy(IDataView testSet)
    {
        var rows = _mlContext.Data.CreateEnumerable<IntentExample>(testSet, reuseRowObject: false).ToArray();
        if (rows.Length == 0)
            return 0;
        return rows.GroupBy(example => example.Label, StringComparer.Ordinal)
            .Average(group => group.Count(example => BaselineIntent(example.Text) == example.Label) / (double)group.Count());
    }

    private static string BaselineIntent(string text) =>
        LocationPrefixes.Any(prefix => text.TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            ? LocationIntent
            : KnowledgeIntent;

    private static async Task InsertExampleAsync(SqliteConnection connection, string utterance, string label, CancellationToken ct,
        SqliteTransaction? transaction = null, string feedbackType = "IntentCorrection")
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT OR REPLACE INTO TrainingExamples (Utterance, Label, FeedbackType, CreatedAtUtc) VALUES ($utterance, $label, $type, $created); DELETE FROM TrainingExamples WHERE Id NOT IN (SELECT Id FROM TrainingExamples ORDER BY Id DESC LIMIT 10000);";
        command.Parameters.AddWithValue("$utterance", utterance.Trim());
        command.Parameters.AddWithValue("$label", label);
        command.Parameters.AddWithValue("$type", feedbackType);
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task RecordProviderFeedbackAsync(SqliteConnection connection, string utterance, string provider, bool positive, CancellationToken ct, SqliteTransaction? transaction = null)
    {
        if (string.IsNullOrWhiteSpace(provider))
            return;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO ProviderFeedbackEvents (UtteranceKey, Provider, WasHelpful, CreatedAtUtc) VALUES ($key, $provider, $helpful, $time) ON CONFLICT(UtteranceKey, Provider) DO UPDATE SET WasHelpful = excluded.WasHelpful, CreatedAtUtc = excluded.CreatedAtUtc; DELETE FROM ProviderFeedbackEvents WHERE rowid NOT IN (SELECT rowid FROM ProviderFeedbackEvents ORDER BY CreatedAtUtc DESC LIMIT 10000);";
        command.Parameters.AddWithValue("$key", Normalize(utterance));
        command.Parameters.AddWithValue("$provider", NormalizeProviderName(provider));
        command.Parameters.AddWithValue("$helpful", positive ? 1 : 0);
        command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task SaveMetricsAsync(SqliteConnection connection, int count, double accuracy, double baseline,
        double precision, double recall, CancellationToken ct, bool updateTrainingTime)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = updateTrainingTime
            ? "UPDATE LearningState SET LastTrainingUtc = $time, ModelAccuracy = $accuracy, BaselineAccuracy = $baseline, MacroPrecision = $precision, MacroRecall = $recall, ExampleCount = $count WHERE Id = 1;"
            : "UPDATE LearningState SET ModelAccuracy = $accuracy, BaselineAccuracy = $baseline, MacroPrecision = $precision, MacroRecall = $recall, ExampleCount = $count WHERE Id = 1;";
        if (updateTrainingTime)
            command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$accuracy", accuracy);
        command.Parameters.AddWithValue("$baseline", baseline);
        command.Parameters.AddWithValue("$precision", precision);
        command.Parameters.AddWithValue("$recall", recall);
        command.Parameters.AddWithValue("$count", count);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _dbLock.Dispose();
        _modelLock.Dispose();
    }

    private sealed class IntentExample
    {
        [LoadColumn(0)]
        public string Text { get; set; } = string.Empty;
        [LoadColumn(1)]
        public string Label { get; set; } = string.Empty;
    }

    private sealed class IntentPredictionOutput
    {
        [ColumnName("PredictedLabel")]
        public string PredictedLabel { get; set; } = string.Empty;
        public float[] Score { get; set; } = [];
    }
}
