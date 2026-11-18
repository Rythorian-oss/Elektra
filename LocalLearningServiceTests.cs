using Elektra.Configuration;
using Elektra.Learning;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Elektra.Tests;

[TestClass]
public sealed class LocalLearningServiceTests
{
    [TestMethod]
    public async Task TrainIfReadyAsync_RequiresOptInAndPromotesOnlyValidatedModelWithRollback()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"ElektraLearningTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            using var preferences = new UserPreferencesService(NullLogger<UserPreferencesService>.Instance, directory);
            using var learning = new LocalLearningService(
                preferences,
                Options.Create(new AssistantOptions
                {
                    LearningMinExamples = 20,
                    LearningTrainingIntervalHours = 1,
                    LearningIntentConfidenceThreshold = 0.5f,
                    LearningMinimumValidationAccuracy = 0.7f
                }),
                NullLogger<LocalLearningService>.Instance,
                directory);

            await learning.RecordWorkedAsync("please navigate to local landmark place 0", LocalLearningService.LocationIntent, "Location");
            Assert.AreEqual(0, (await learning.GetMetricsAsync()).ExampleCount);

            learning.SetLearningEnabled(true);
            for (int i = 0; i < 12; i++)
            {
                await learning.RecordWorkedAsync($"please navigate to local landmark place {i}", LocalLearningService.LocationIntent, "Location");
                await learning.RecordWorkedAsync($"tell me about local knowledge topic {i}", LocalLearningService.KnowledgeIntent, "Wikipedia");
            }
            await learning.RecordWorkedAsync("tell me about local knowledge topic 0", LocalLearningService.KnowledgeIntent, "Wikipedia");

            LearningMetrics collected = await learning.GetMetricsAsync();
            Assert.AreEqual(24, collected.ExampleCount, "Repeated feedback for the same query must not add training weight.");
            Assert.AreEqual(13d / 14d, learning.GetProviderScore("Wikipedia"), 0.000001,
                "A repeated rating for one query must count once in provider ranking.");
            Assert.IsTrue(await learning.TrainIfReadyAsync(CancellationToken.None));

            LearningMetrics trained = await learning.GetMetricsAsync();
            Assert.IsNotNull(trained.ModelAccuracy);
            Assert.IsNotNull(trained.BaselineAccuracy);
            Assert.IsNotNull(trained.MacroPrecision);
            Assert.IsNotNull(trained.MacroRecall);
            Assert.IsTrue(trained.ModelAccuracy > trained.BaselineAccuracy,
                "A promoted model must beat the deterministic baseline on held-out data.");
            Assert.IsFalse(await learning.TrainIfReadyAsync(CancellationToken.None), "The cooldown must prevent repeated training.");
            Assert.IsFalse(await learning.RollbackAsync(), "There is no previous model to roll back on first promotion.");

            var prediction = learning.PredictIntent("please navigate to local landmark place unseen");
            Assert.IsNotNull(prediction);
            Assert.AreEqual(LocalLearningService.LocationIntent, prediction.Label);
            Assert.IsTrue(prediction.Confidence >= 0.5f);

            await learning.RecordCorrectionAsync("Take me to River Park", "River Park, Maine",
                LocalLearningService.LocationIntent, string.Empty);
            Assert.AreEqual("River Park, Maine", learning.GetCorrectedLocation("  TAKE me to river park  "));

            learning.SetLearningEnabled(false);
            Assert.IsNull(learning.PredictIntent("please navigate to local landmark place unseen"));
            Assert.AreEqual(0.5, learning.GetProviderScore("Wikipedia"), 0.000001);
            learning.SetLearningEnabled(true);
            Assert.AreEqual(LocalLearningService.LocationIntent,
                learning.PredictIntent("please navigate to local landmark place unseen")?.Label);

            await using (var connection = new SqliteConnection($"Data Source={Path.Combine(directory, "learning.db")};Pooling=True;"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "UPDATE LearningState SET LastTrainingUtc = 0 WHERE Id = 1;";
                await command.ExecuteNonQueryAsync();
            }

            Assert.IsTrue(await learning.TrainIfReadyAsync(CancellationToken.None));
            Assert.IsTrue(await learning.RollbackAsync());
            LearningMetrics rolledBack = await learning.GetMetricsAsync();
            Assert.IsTrue(rolledBack.HasPreviousModel);
            Assert.IsNull(rolledBack.ModelAccuracy, "Metrics for the replaced model must not be presented as scores for the restored model.");
            Assert.IsNull(rolledBack.MacroPrecision);
            Assert.IsNull(rolledBack.MacroRecall);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task LocalCorrectionsMemoriesAndTrainingPause_RespectConsentAndRemainManageable()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"ElektraLearningTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            using var preferences = new UserPreferencesService(NullLogger<UserPreferencesService>.Instance, directory);
            using var learning = new LocalLearningService(
                preferences,
                Options.Create(new AssistantOptions()),
                NullLogger<LocalLearningService>.Instance,
                directory);

            await learning.RecordSpeechCorrectionAsync("recognized phrase", "corrected phrase");
            Assert.IsNull(learning.GetCorrectedSpeechUtterance("recognized phrase"), "Corrections must not be used before consent.");

            learning.SetLearningEnabled(true);
            preferences.Update(current => current with { PauseLearningTraining = true }, debounceMs: 60_000);
            await learning.RecordSpeechCorrectionAsync("Recognized phrase", "Corrected phrase");
            Assert.AreEqual("Corrected phrase", learning.GetCorrectedSpeechUtterance("  recognized   phrase "));
            Assert.IsFalse(await learning.TrainIfReadyAsync(CancellationToken.None));
            StringAssert.Contains(learning.Status, "paused");

            await learning.SaveMemoryAsync("favorite place", "Waterville library");
            IReadOnlyList<UserMemory> matches = await learning.SearchMemoriesAsync("favorite");
            Assert.AreEqual(1, matches.Count);
            Assert.AreEqual("Waterville library", matches[0].Value);
            await learning.DeleteMemoryAsync(matches[0].Id);
            Assert.AreEqual(0, (await learning.SearchMemoriesAsync(string.Empty)).Count);

            await learning.RecordNegativeFeedbackAsync("question text", "WrongAnswer", "Wikipedia");
            Assert.AreEqual(1d / 3d, learning.GetProviderScore("Wikipedia"), 0.000001);
            IReadOnlyList<LearningReviewItem> reviewItems = await learning.GetLearningReviewItemsAsync();
            Assert.IsTrue(reviewItems.Any(item => item.Kind == "Speech correction"));
            Assert.IsTrue(reviewItems.Any(item => item.Kind == "Answer feedback"));
            LearningReviewItem speechItem = reviewItems.Single(item => item.Kind == "Speech correction");
            await learning.DeleteLearningReviewItemAsync(speechItem);
            Assert.IsNull(learning.GetCorrectedSpeechUtterance("recognized phrase"));
            Assert.AreEqual(0, (await learning.SearchMemoriesAsync("%")).Count, "LIKE metacharacters must be treated as literal search text.");

            await using var connection = new SqliteConnection($"Data Source={Path.Combine(directory, "learning.db")};Pooling=True;");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT FeedbackType FROM FeedbackEvents WHERE UtteranceKey = 'question text';";
            Assert.AreEqual("WrongAnswer", await command.ExecuteScalarAsync());

            await learning.ClearLearningDataAsync();
            Assert.IsNull(learning.GetCorrectedSpeechUtterance("recognized phrase"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task GetVoicePracticePhraseAsync_RequiresVoiceConsentAndUsesCorrectedPhrases()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"ElektraLearningTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            using var preferences = new UserPreferencesService(NullLogger<UserPreferencesService>.Instance, directory);
            using var learning = new LocalLearningService(
                preferences,
                Options.Create(new AssistantOptions()),
                NullLogger<LocalLearningService>.Instance,
                directory);

            learning.SetLearningEnabled(true);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => learning.GetVoicePracticePhraseAsync());
            preferences.Update(current => current with
            {
                EnableVoskVoiceSamples = true,
                EnableAutomaticVoicePracticeLessons = true
            }, debounceMs: 60_000);
            Assert.IsTrue(learning.IsAutomaticVoicePracticeEnabled);
            await learning.RecordSpeechCorrectionAsync("recognized phrase", "Corrected practice phrase");

            Assert.AreEqual("User-selected phrase", await learning.GetVoicePracticePhraseAsync(" User-selected phrase "));
            Assert.AreEqual("Corrected practice phrase", await learning.GetVoicePracticePhraseAsync());

            preferences.Update(current => current with { EnableVoskVoiceSamples = false }, debounceMs: 60_000);
            Assert.IsFalse(learning.IsAutomaticVoicePracticeEnabled,
                "Automatic lessons must stop when separate voice-sample consent is withdrawn.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task RecordVerifiedVoiceSampleAsync_RequiresSeparateConsentAndKeepsTranscriptGroupsInOneSplit()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"ElektraLearningTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            using var preferences = new UserPreferencesService(NullLogger<UserPreferencesService>.Instance, directory);
            using var learning = new LocalLearningService(
                preferences,
                Options.Create(new AssistantOptions()),
                NullLogger<LocalLearningService>.Instance,
                directory);
            byte[] pcm = new byte[6400];

            learning.SetLearningEnabled(true);
            Assert.IsFalse(await learning.RecordVerifiedVoiceSampleAsync(pcm, "recognized", "verified"),
                "Voice samples require their own opt-in, separate from ordinary learning consent.");
            preferences.Update(current => current with { EnableVoskVoiceSamples = true }, debounceMs: 60_000);

            Assert.IsTrue(await learning.RecordVerifiedVoiceSampleAsync(pcm, "alpha", "alpha"));
            Assert.IsTrue(await learning.RecordVerifiedVoiceSampleAsync(pcm, "alpha", "alpha"));
            Assert.IsTrue(await learning.RecordVerifiedVoiceSampleAsync(pcm, "beta", "beta"));
            Assert.IsTrue(await learning.RecordVerifiedVoiceSampleAsync(pcm, "gamma", "gamma"));
            Assert.IsTrue(await learning.RecordVerifiedVoiceSampleAsync(pcm, "delta", "delta"));
            Assert.IsTrue(await learning.RecordVerifiedVoiceSampleAsync(pcm, "epsilon", "epsilon"));

            VoiceSampleMetrics metrics = await learning.GetVoiceSampleMetricsAsync();
            Assert.AreEqual(6, metrics.TotalSamples);
            Assert.AreEqual(5, metrics.TrainingSamples);
            Assert.AreEqual(1, metrics.ValidationSamples);
            string[] waveFiles = Directory.GetFiles(Path.Combine(directory, "voice-samples"), "*.wav");
            Assert.AreEqual(6, waveFiles.Length);
            byte[] wave = await File.ReadAllBytesAsync(waveFiles[0]);
            CollectionAssert.AreEqual("RIFF"u8.ToArray(), wave[..4]);
            CollectionAssert.AreEqual("WAVE"u8.ToArray(), wave[8..12]);

            LearningReviewItem sample = (await learning.GetLearningReviewItemsAsync()).First(item => item.Kind == "Verified voice sample");
            await learning.DeleteLearningReviewItemAsync(sample);
            Assert.AreEqual(5, Directory.GetFiles(Path.Combine(directory, "voice-samples"), "*.wav").Length);
            learning.SetLearningEnabled(false);
            Assert.IsFalse(preferences.Current.EnableVoskVoiceSamples,
                "Disabling local learning must withdraw the separate audio-recording consent too.");
            await learning.ClearLearningDataAsync();
            Assert.AreEqual(0, Directory.GetFiles(Path.Combine(directory, "voice-samples"), "*.wav").Length);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task RecordWorkedAsync_WithConfirmedTranscript_StoresDeduplicatedIntentWithoutProviderRating()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"ElektraLearningTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            using var preferences = new UserPreferencesService(NullLogger<UserPreferencesService>.Instance, directory);
            using var learning = new LocalLearningService(
                preferences,
                Options.Create(new AssistantOptions()),
                NullLogger<LocalLearningService>.Instance,
                directory);

            learning.SetLearningEnabled(true);
            await learning.RecordWorkedAsync("What is a wolf?", LocalLearningService.KnowledgeIntent, string.Empty);
            await learning.RecordWorkedAsync("What is a wolf?", LocalLearningService.KnowledgeIntent, string.Empty);

            Assert.AreEqual(1, (await learning.GetMetricsAsync()).ExampleCount);
            Assert.AreEqual(0.5, learning.GetProviderScore("Wikipedia"), 0.000001,
                "Confirming transcript wording must not rate the answer provider.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
