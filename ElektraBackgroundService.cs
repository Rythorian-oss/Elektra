/*
 * Creator: Justin L. Ross | Farmington, Maine. | Remote Location: Iceland | Black Star Research Facility.
 */

using Elektra.Audio;
using Elektra.Configuration;
using Elektra.Knowledge;
using Elektra.Knowledge.Context;
using Elektra.Knowledge.Providers;
using Elektra.Learning;
using Elektra.Platform;
using Elektra.Security;
using Elektra.UI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Channels;

namespace Elektra.Workers;

public sealed class ElektraBackgroundService(
    ChannelReader<string> utteranceReader,
    ChannelReader<byte> voicePracticeRequestReader,
    ChannelWriter<byte> voicePracticeRequestWriter,
    AudioCaptureService audioCapture,
    VoiceSynthesisService tts,
    QueryOrchestrator orchestrator,
    ConversationContextTracker contextTracker,
    AudioTelemetryBridge telemetry,
    MainWindow mainWindow,
    DirectoryEncryptionService directoryEncryption,
    WindowsToolLauncher toolLauncher,
    LocationSearchService locationSearch,
    LocalLearningService learning,
    IOptions<AssistantOptions> options,
    ILogger<ElektraBackgroundService> logger) : BackgroundService
{
    private static readonly string[] SecurityFolderSuffixes = [" directory", " folder"];
    private static readonly string[] LocationCommandPrefixes = ["open ", "show ", "find ", "locate "];
    private static readonly string[] LocationCommandSuffixes = [" in google earth", " on google earth", " in earth", " on earth"];
    private static readonly string[] LocationSearchPrefixes = ["find ", "locate ", "search for ", "take me to ", "go to ", "show me ", "google earth "];

    private string? _pendingWikipediaUrl;
    private DateTimeOffset _googleEarthLocationPromptExpiresAt;
    private string? _pendingGoogleEarthLocation;
    private string? _pendingLocationCorrectionSource;
    private string? _pendingTranscriptConfirmation;
    private string? _pendingLocationFeedbackUtterance;
    private string? _pendingVoicePracticePhrase;
    private DateTimeOffset _voicePracticeExpiresAt;
    private DateTimeOffset _transcriptConfirmationExpiresAt;
    private bool _awaitingGoogleEarthLocation;
    private bool _awaitingGoogleEarthLocationConfirmation;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string modelPath = AppContext.BaseDirectory;
        mainWindow.SetLocationSearchHandler(place =>
            SearchAndOpenLocationAsync(learning.GetCorrectedLocation(place) ?? place, stoppingToken, place));
        mainWindow.VoicePracticeLessonRequested += RequestVoicePracticeLesson;
        audioCapture.OnStopWordDetected += tts.StopSpeaking;
        try
        {
            modelPath = await Task.Run(
                () => VoskModelPathResolver.Resolve(options.Value.VoskModelPath, AppContext.BaseDirectory),
                stoppingToken).ConfigureAwait(false);
            if (Directory.Exists(Path.Combine(modelPath, "am")) && Directory.Exists(Path.Combine(modelPath, "conf")))
                logger.LogInformation("Using Vosk speech model at '{ModelPath}'.", modelPath);
            audioCapture.OnWakeWordDetected += () => System.Media.SystemSounds.Asterisk.Play();

            bool initialized = await Task.Run(
                () => audioCapture.InitializeAndStart(modelPath),
                stoppingToken).ConfigureAwait(false);
            if (!initialized)
            {
                mainWindow.DisplayAnswer(
                    "Speech Model Missing",
                    $"No valid Vosk model was found. Elektra checked '{modelPath}', the app's Models folder, and nearby build output folders. " +
                    "Set Assistant:VoskModelPath in appsettings.json or place a model folder under Models beside Elektra.exe.",
                    "System");
                return;
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Speech recognition failed to initialize with Vosk model at '{Path}'.", modelPath);
            audioCapture.Dispose();
            mainWindow.DisplayAnswer(
                "Speech Initialization Failed",
                $"Elektra could not initialize speech recognition: {ex.Message}",
                "System");
            return;
        }

        Task<bool> utteranceReady = utteranceReader.WaitToReadAsync(stoppingToken).AsTask();
        Task<bool> practiceRequestReady = voicePracticeRequestReader.WaitToReadAsync(stoppingToken).AsTask();
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.WhenAny(utteranceReady, practiceRequestReady).ConfigureAwait(false);
            if (practiceRequestReady.IsCompleted)
            {
                if (!await practiceRequestReady.ConfigureAwait(false))
                    break;
                while (voicePracticeRequestReader.TryRead(out _))
                {
                    try
                    {
                        await StartVoicePracticeLessonAsync(null, stoppingToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Could not start a voice-practice lesson.");
                        mainWindow.DisplayAnswer("Voice practice unavailable", "Elektra couldn't prepare the lesson. Check the application log and try again.", "System");
                    }
                }
                practiceRequestReady = voicePracticeRequestReader.WaitToReadAsync(stoppingToken).AsTask();
            }

            if (utteranceReady.IsCompleted)
            {
                if (!await utteranceReady.ConfigureAwait(false))
                    break;
                while (utteranceReader.TryRead(out string? utterance))
                    await ProcessUtteranceAsync(utterance, stoppingToken).ConfigureAwait(false);
                utteranceReady = utteranceReader.WaitToReadAsync(stoppingToken).AsTask();
            }
        }
    }

    private void RequestVoicePracticeLesson()
    {
        if (!voicePracticeRequestWriter.TryWrite(0))
            mainWindow.DisplayAnswer("Voice practice", "A voice-practice request is already queued.", "System");
    }

    private async Task ProcessUtteranceAsync(string utterance, CancellationToken ct)
    {
        try
        {
            string normalizedUtterance = NormalizeShortReply(utterance);
            if (await HandleVoicePracticeFollowUpAsync(utterance, normalizedUtterance, ct).ConfigureAwait(false))
                return;

            if (await HandleTranscriptConfirmationFollowUpAsync(normalizedUtterance, ct).ConfigureAwait(false))
                return;

            if (await HandleWikipediaFollowUpAsync(normalizedUtterance, ct).ConfigureAwait(false))
                return;

            if (await HandleGoogleEarthLocationFollowUpAsync(normalizedUtterance, ct).ConfigureAwait(false))
                return;

            if (await HandleGoogleEarthOpenCommandAsync(normalizedUtterance, ct).ConfigureAwait(false))
                return;

            if (await HandleDirectorySecurityCommandAsync(normalizedUtterance, ct).ConfigureAwait(false))
                return;

            if (await HandleWindowsToolCommandAsync(normalizedUtterance, ct).ConfigureAwait(false))
                return;

            if (await HandleLocationSearchCommandAsync(normalizedUtterance, ct).ConfigureAwait(false))
                return;

            if (HandleLocalCommand(normalizedUtterance))
                return;

            if (learning.IsEnabled)
            {
                _pendingTranscriptConfirmation = utterance;
                await PromptForTranscriptConfirmationAsync(utterance, ct).ConfigureAwait(false);
                return;
            }

            await ProcessKnowledgeQuestionAsync(utterance, utterance, transcriptConfirmed: false, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing spoken utterance.");
            telemetry.CurrentState = PlasmaState.Idle;
        }
    }

    private async Task<bool> HandleVoicePracticeFollowUpAsync(string recognizedUtterance, string response, CancellationToken ct)
    {
        if (_pendingVoicePracticePhrase is not string phrase)
            return false;

        if (DateTimeOffset.UtcNow > _voicePracticeExpiresAt)
        {
            _pendingVoicePracticePhrase = null;
            audioCapture.DiscardVoiceSample(recognizedUtterance);
            await tts.SpeakAsync("That voice-practice lesson expired. You can start another from the orb menu.", ct).ConfigureAwait(false);
            return false;
        }

        if (response is "skip" or "cancel" or "stop" or "never mind" or "nevermind")
        {
            _pendingVoicePracticePhrase = null;
            audioCapture.DiscardVoiceSample(recognizedUtterance);
            await tts.SpeakAsync("Okay, I ended the voice-practice lesson.", ct).ConfigureAwait(false);
            return true;
        }

        if (NormalizeShortReply(response) == NormalizeShortReply(phrase))
        {
            _pendingVoicePracticePhrase = null;
            byte[]? pcm = audioCapture.TakeVoiceSample(recognizedUtterance);
            if (pcm == null)
            {
                await tts.SpeakAsync("I recognized the repeat, but no eligible microphone recording was available to save. Check the microphone and voice-sample consent setting.", ct).ConfigureAwait(false);
                return true;
            }

            bool saved;
            try
            {
                saved = await learning.RecordVerifiedVoiceSampleAsync(pcm, response, phrase, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not save the verified voice-practice sample.");
                await tts.SpeakAsync("I recognized your repeat, but couldn't save the recording. Check the application log for details.", ct).ConfigureAwait(false);
                return true;
            }

            await tts.SpeakAsync(saved
                ? "Well done. I saved that verified practice recording locally for future voice adaptation. The Vosk model itself is not changed automatically."
                : "The practice recording was not saved. Check local learning and voice-sample consent.", ct).ConfigureAwait(false);
            return true;
        }

        audioCapture.DiscardVoiceSample(recognizedUtterance);
        await tts.SpeakAsync($"I heard {response}. Please repeat: {phrase}. Say skip to end the lesson.", ct).ConfigureAwait(false);
        _voicePracticeExpiresAt = DateTimeOffset.UtcNow.AddSeconds(options.Value.ActiveListeningTimeoutSeconds);
        audioCapture.TriggerAwakeMode();
        return true;
    }

    private async Task StartVoicePracticeLessonAsync(string? preferredPhrase, CancellationToken ct)
    {
        if (!learning.IsVoiceSampleCollectionEnabled)
        {
            mainWindow.DisplayAnswer("Voice practice unavailable", "Turn on Local Learning and the separate local voice-sample consent before starting a microphone practice lesson.", "System");
            return;
        }

        if (_pendingVoicePracticePhrase != null || _pendingTranscriptConfirmation != null ||
            _pendingWikipediaUrl != null || _awaitingGoogleEarthLocation || _awaitingGoogleEarthLocationConfirmation)
        {
            mainWindow.DisplayAnswer("Voice practice", "Finish the current voice interaction before starting a practice lesson.", "System");
            return;
        }

        string phrase = await learning.GetVoicePracticePhraseAsync(preferredPhrase, ct).ConfigureAwait(false);
        audioCapture.ClearBufferedVoiceSamples();
        string prompt = $"Voice practice. Please repeat: {phrase}. Say skip to end the lesson.";
        mainWindow.DisplayAnswer("Voice practice", prompt, "System");
        await tts.SpeakAsync(prompt, ct).ConfigureAwait(false);
        _pendingVoicePracticePhrase = phrase;
        _voicePracticeExpiresAt = DateTimeOffset.UtcNow.AddSeconds(options.Value.ActiveListeningTimeoutSeconds);
        audioCapture.TriggerAwakeMode();
    }

    private async Task<bool> HandleTranscriptConfirmationFollowUpAsync(string response, CancellationToken ct)
    {
        if (_pendingTranscriptConfirmation == null)
            return false;

        if (DateTimeOffset.UtcNow > _transcriptConfirmationExpiresAt)
        {
            _pendingTranscriptConfirmation = null;
            if (IsAffirmativeReply(response) || IsNegativeReply(response))
            {
                await tts.SpeakAsync("That confirmation expired. Please ask your question again.", ct).ConfigureAwait(false);
                return true;
            }
            return false;
        }

        if (IsAffirmativeReply(response) || response is "correct" or "that's right" or "that is right")
        {
            string recognized = _pendingTranscriptConfirmation;
            _pendingTranscriptConfirmation = null;
            string corrected = learning.GetCorrectedSpeechUtterance(recognized) ?? recognized;
            await ProcessKnowledgeQuestionAsync(corrected, recognized, transcriptConfirmed: true, ct).ConfigureAwait(false);
            return true;
        }

        if (IsNegativeReply(response) || response is "wrong" or "incorrect")
        {
            string recognized = _pendingTranscriptConfirmation;
            _pendingTranscriptConfirmation = null;
            audioCapture.EnterWakeWordMode();
            string? corrected = await mainWindow.PromptForUtteranceCorrectionAsync(recognized).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(corrected))
            {
                await tts.SpeakAsync("Okay, I cancelled that question.", ct).ConfigureAwait(false);
                return true;
            }

            if (learning.IsEnabled)
            {
                try
                {
                    await learning.RecordSpeechCorrectionAsync(recognized, corrected, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not save the user's spoken-query correction.");
                }
            }

            await ProcessKnowledgeQuestionAsync(corrected, recognized, transcriptConfirmed: true, ct: ct,
                startPracticeLessonAfterAnswer: learning.IsAutomaticVoicePracticeEnabled).ConfigureAwait(false);
            return true;
        }

        await PromptForTranscriptConfirmationAsync(_pendingTranscriptConfirmation, ct).ConfigureAwait(false);
        return true;
    }

    private async Task PromptForTranscriptConfirmationAsync(string utterance, CancellationToken ct)
    {
        string prompt = $"I heard: {utterance}. Is that correct? Say yes or no.";
        mainWindow.DisplayAnswer("Confirm recognized question", prompt, "System");
        await tts.SpeakAsync(prompt, ct).ConfigureAwait(false);
        _transcriptConfirmationExpiresAt = DateTimeOffset.UtcNow.AddSeconds(options.Value.ActiveListeningTimeoutSeconds);
        audioCapture.TriggerAwakeMode();
    }

    private async Task ProcessKnowledgeQuestionAsync(string queryUtterance, string feedbackUtterance, bool transcriptConfirmed, CancellationToken ct,
        bool startPracticeLessonAfterAnswer = false)
    {
        string correctedQuery = learning.GetCorrectedSpeechUtterance(queryUtterance) ?? queryUtterance;
        if (TryParseLocationSearchCommand(correctedQuery.Trim().ToLowerInvariant(), out string correctedPlace))
        {
            _pendingLocationFeedbackUtterance = transcriptConfirmed ? feedbackUtterance : null;
            await OfferGoogleEarthLocationForConfirmationAsync(correctedPlace, ct).ConfigureAwait(false);
            return;
        }

        IntentPrediction? intent = learning.PredictIntent(correctedQuery);
        if (intent?.Label == LocalLearningService.LocationIntent)
        {
            _pendingLocationFeedbackUtterance = transcriptConfirmed ? feedbackUtterance : null;
            string suggestedPlace = learning.GetCorrectedLocation(feedbackUtterance) ?? correctedQuery;
            await OfferGoogleEarthLocationForConfirmationAsync(suggestedPlace, ct).ConfigureAwait(false);
            return;
        }

        if (transcriptConfirmed && learning.IsEnabled)
        {
            try
            {
                await learning.RecordWorkedAsync(correctedQuery, LocalLearningService.KnowledgeIntent, string.Empty, ct).ConfigureAwait(false);
                await SaveVerifiedVoiceSampleAsync(feedbackUtterance, correctedQuery, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not save the confirmed knowledge-question example.");
            }
        }

        telemetry.CurrentState = PlasmaState.Thinking;
        var response = await orchestrator.ResolveAsync(correctedQuery, ct).ConfigureAwait(false);

        mainWindow.DisplayAnswer(response.Query, response.Answer, response.Source, feedbackQuery: feedbackUtterance);
        await tts.SpeakAsync(response.Answer, ct).ConfigureAwait(false);
        if (startPracticeLessonAfterAnswer && response.SourceUrl == null)
            await StartVoicePracticeLessonAsync(correctedQuery, ct).ConfigureAwait(false);
        if (response.SourceUrl != null && Uri.TryCreate(response.SourceUrl, UriKind.Absolute, out var sourceUri) &&
            sourceUri.Scheme == Uri.UriSchemeHttps && sourceUri.Host.Equals("en.wikipedia.org", StringComparison.OrdinalIgnoreCase))
        {
            _pendingWikipediaUrl = sourceUri.AbsoluteUri;
            await tts.SpeakAsync(
                "Would you like me to open the Wikipedia page? Say yes or no.",
                ct).ConfigureAwait(false);
            audioCapture.TriggerAwakeMode();
        }
    }

    private async Task SaveVerifiedVoiceSampleAsync(string recognizedText, string verifiedText, CancellationToken ct)
    {
        if (!learning.IsVoiceSampleCollectionEnabled)
            return;

        byte[]? pcm = audioCapture.TakeVoiceSample(recognizedText);
        if (pcm == null)
        {
            logger.LogDebug("No eligible in-memory microphone sample is available for this confirmed transcript.");
            return;
        }

        await learning.RecordVerifiedVoiceSampleAsync(pcm, recognizedText, verifiedText, ct).ConfigureAwait(false);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        audioCapture.OnStopWordDetected -= tts.StopSpeaking;
        mainWindow.VoicePracticeLessonRequested -= RequestVoicePracticeLesson;
        mainWindow.SetLocationSearchHandler(null);
        await base.StopAsync(cancellationToken);
    }

    private async Task<bool> HandleWikipediaFollowUpAsync(string response, CancellationToken ct)
    {
        if (_pendingWikipediaUrl == null)
            return false;

        if (IsAffirmativeReply(response))
        {
            string url = _pendingWikipediaUrl;
            _pendingWikipediaUrl = null;
            try
            {
                using Process? browser = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                if (browser == null)
                    throw new InvalidOperationException("Windows did not start the default web browser.");
                await tts.SpeakAsync("Opening the Wikipedia page.", ct);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                logger.LogError(ex, "Could not open Wikipedia page {Url}.", url);
                await tts.SpeakAsync("I couldn't open the Wikipedia page.", ct);
            }
            return true;
        }

        if (IsNegativeReply(response))
        {
            _pendingWikipediaUrl = null;
            await tts.SpeakAsync("Okay, I won't open it.", ct);
            return true;
        }

        _pendingWikipediaUrl = null;
        return false;
    }

    private static string NormalizeShortReply(string utterance)
    {
        int start = 0;
        int end = utterance.Length;
        while (start < end && char.IsWhiteSpace(utterance[start]))
            start++;
        while (end > start && char.IsWhiteSpace(utterance[end - 1]))
            end--;

        int outputLength = 0;
        bool pendingSpace = false;
        for (int i = start; i < end; i++)
        {
            char character = utterance[i] == '’' ? '\'' : utterance[i];
            if (!char.IsLetterOrDigit(character) && character != '\'')
            {
                pendingSpace |= outputLength > 0;
                continue;
            }

            if (pendingSpace)
            {
                outputLength++;
                pendingSpace = false;
            }
            outputLength++;
        }

        return string.Create(outputLength, (utterance, start, end), static (destination, state) =>
        {
            int written = 0;
            bool needsSpace = false;
            for (int i = state.start; i < state.end; i++)
            {
                char character = state.utterance[i] == '’' ? '\'' : state.utterance[i];
                if (!char.IsLetterOrDigit(character) && character != '\'')
                {
                    needsSpace |= written > 0;
                    continue;
                }

                if (needsSpace)
                {
                    destination[written++] = ' ';
                    needsSpace = false;
                }
                destination[written++] = char.ToLowerInvariant(character);
            }
        });
    }

    private static bool IsAffirmativeReply(string response) => response is
        "yes" or "yeah" or "sure" or "okay" or "ok" or "of course" or "please do" or
        "yes please" or "sure please" or "open it" or "pull it up" or "show me" or "for yes";

    private static bool IsNegativeReply(string response) => response is
        "no" or "no thanks" or "no thank you" or "nope" or "im all set" or "i'm all set" or
        "im good" or "i'm good" or "i am all set" or "i am good" or "not now" or "maybe later" or
        "no im good" or "no i'm good";

    private async Task<bool> HandleDirectorySecurityCommandAsync(string command, CancellationToken ct)
    {
        if (!TryParseDirectorySecurityCommand(command, out bool encrypting, out string folderName))
            return false;

        string action = encrypting ? "Encrypt" : "Decrypt";
        string? directory = directoryEncryption.GetProtectedDirectory(folderName);
        if (directory == null)
        {
            await ReportLocalActionAsync($"{action} folder", "For security, I can only encrypt or decrypt Pictures, Videos, Documents, Music, or Downloads.", ct);
            return true;
        }
        if (!Directory.Exists(directory))
        {
            await ReportLocalActionAsync($"{action} {folderName}", $"I couldn't find the {folderName} folder.", ct);
            return true;
        }

        TimeSpan cooldown = directoryEncryption.GetCooldownRemaining();
        if (!encrypting && cooldown > TimeSpan.Zero)
        {
            await ReportLocalActionAsync("Security cooldown", $"Too many incorrect password attempts. Try again in {(int)Math.Ceiling(cooldown.TotalSeconds)} seconds.", ct);
            return true;
        }

        DirectoryEncryptionResult result;
        try
        {
            while (true)
            {
                string? password = await mainWindow.PromptForSecurityPasswordAsync(folderName, encrypting, confirmPassword: encrypting);
                if (password == null)
                {
                    await ReportLocalActionAsync($"{action} cancelled", "Okay, I cancelled the folder operation.", ct);
                    return true;
                }

                telemetry.CurrentState = PlasmaState.Warning;
                result = await Task.Run(
                    () => encrypting
                        ? directoryEncryption.EncryptAsync(folderName, password, ct)
                        : directoryEncryption.DecryptAsync(folderName, password, ct), ct).ConfigureAwait(false);
                password = null;

                if (result.Status != DirectoryEncryptionStatus.IncorrectPassword)
                    break;

                telemetry.CurrentState = PlasmaState.Idle;
                await tts.SpeakAsync(
                    $"That password was incorrect. {directoryEncryption.GetAttemptsRemaining()} attempt(s) remain. Please try again.",
                    ct).ConfigureAwait(false);
            }

            string answer = result.Status switch
            {
                DirectoryEncryptionStatus.Completed => $"{action}ed {result.FilesProcessed} file(s) in {folderName} using AES-256-GCM. Elektra does not store the password. Keep it safe; encrypted files cannot be recovered without it.",
                DirectoryEncryptionStatus.NoFiles => encrypting
                    ? $"There are no files to encrypt in {folderName}."
                    : $"I couldn't find any .elektra files in {folderName} to decrypt.",
                DirectoryEncryptionStatus.DestinationExists => $"I stopped safely after processing {result.FilesProcessed} file(s) because a destination file already exists. Existing files were not overwritten.",
                DirectoryEncryptionStatus.IncorrectPassword => $"That password was incorrect, or the first encrypted file is damaged. {directoryEncryption.GetAttemptsRemaining()} attempt(s) remain. No files were decrypted.",
                DirectoryEncryptionStatus.CoolingDown => $"Too many incorrect password attempts. Try again in {(int)Math.Ceiling(result.CooldownRemaining.TotalSeconds)} seconds.",
                _ => "I couldn't complete the folder operation."
            };
            telemetry.CurrentState = PlasmaState.Idle;
            mainWindow.DisplayAnswer($"{action} {folderName}", answer, "Security");
            await tts.SpeakAsync(answer, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            telemetry.CurrentState = PlasmaState.Idle;
            logger.LogError(ex, "Could not {Action} the protected {Folder} folder.", action.ToLowerInvariant(), folderName);
            string answer = $"I couldn't complete the {action.ToLowerInvariant()} operation for {folderName}. No original file is removed unless its encrypted or decrypted replacement is safely written.";
            mainWindow.DisplayAnswer($"{action} {folderName}", answer, "Security");
            await tts.SpeakAsync(answer, ct).ConfigureAwait(false);
        }
        return true;
    }

    private static bool TryParseDirectorySecurityCommand(string command, out bool encrypting, out string folderName)
    {
        encrypting = command.StartsWith("encrypt ", StringComparison.Ordinal);
        bool decrypting = command.StartsWith("decrypt ", StringComparison.Ordinal);
        folderName = string.Empty;
        if (!encrypting && !decrypting)
            return false;

        string folder = command[8..].Trim();
        if (folder.StartsWith("the ", StringComparison.Ordinal))
            folder = folder[4..].Trim();
        foreach (string suffix in SecurityFolderSuffixes)
        {
            if (folder.EndsWith(suffix, StringComparison.Ordinal))
            {
                folder = folder[..^suffix.Length].Trim();
                break;
            }
        }
        folderName = folder;
        return true;
    }

    private async Task<bool> HandleGoogleEarthOpenCommandAsync(string command, CancellationToken ct)
    {
        if (!WindowsToolLauncher.IsGoogleEarthOpenCommand(command))
            return false;

        toolLauncher.TryHandle(command, out string action, out string error);
        string answer = string.IsNullOrWhiteSpace(error) ? action : error;
        mainWindow.DisplayAnswer(action, answer, "Windows");
        await tts.SpeakAsync(answer, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(error))
            return true;
        if (!options.Value.EnableOnlineLookups)
        {
            await ReportLocalActionAsync("Google Earth", "Online lookups are disabled, so I can't search for a place. Enable online lookups in appsettings.json to use location search.", ct);
            return true;
        }

        _awaitingGoogleEarthLocation = true;
        await PromptForGoogleEarthLocationAsync("Google Earth is open. What location would you like me to find? Please say the street number, street, city, and state. I'll repeat it back before searching.", ct).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> HandleGoogleEarthLocationFollowUpAsync(string response, CancellationToken ct)
    {
        if (!_awaitingGoogleEarthLocation && !_awaitingGoogleEarthLocationConfirmation)
            return false;

        if (DateTimeOffset.UtcNow > _googleEarthLocationPromptExpiresAt)
        {
            _awaitingGoogleEarthLocation = false;
            _awaitingGoogleEarthLocationConfirmation = false;
            _pendingGoogleEarthLocation = null;
            _pendingLocationCorrectionSource = null;
            _pendingLocationFeedbackUtterance = null;
            return false;
        }

        if (response is "cancel" or "never mind" or "nevermind" or "stop")
        {
            _awaitingGoogleEarthLocation = false;
            _awaitingGoogleEarthLocationConfirmation = false;
            _pendingGoogleEarthLocation = null;
            _pendingLocationCorrectionSource = null;
            _pendingLocationFeedbackUtterance = null;
            await ReportLocalActionAsync("Google Earth search cancelled", "Okay, I cancelled the location search.", ct);
            return true;
        }

        if (_awaitingGoogleEarthLocationConfirmation)
        {
            _awaitingGoogleEarthLocationConfirmation = false;
            if (response is "yes" or "yeah" or "correct" or "that's right" or "that is right")
            {
                string confirmedPlace = _pendingGoogleEarthLocation!;
                _pendingGoogleEarthLocation = null;
                if (_pendingLocationCorrectionSource != null)
                {
                    await SaveLocationLearningAsync(_pendingLocationCorrectionSource, confirmedPlace, "Location", ct).ConfigureAwait(false);
                    _pendingLocationCorrectionSource = null;
                }
                if (_pendingLocationFeedbackUtterance is string confirmedUtterance && learning.IsEnabled)
                {
                    try
                    {
                        await learning.RecordWorkedAsync(confirmedUtterance, LocalLearningService.LocationIntent, string.Empty, ct).ConfigureAwait(false);
                        await SaveVerifiedVoiceSampleAsync(confirmedUtterance, confirmedPlace, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Could not save the confirmed location-intent example or voice sample.");
                    }
                }
                _pendingLocationFeedbackUtterance = null;
                await SearchAndOpenLocationAsync(confirmedPlace, ct, confirmedPlace, userConfirmed: true).ConfigureAwait(false);
                return true;
            }

            string? rejectedLocation = _pendingGoogleEarthLocation;
            _pendingGoogleEarthLocation = null;
            if (response is "no" or "nope" or "wrong")
            {
                _pendingLocationCorrectionSource = rejectedLocation;
                _awaitingGoogleEarthLocation = true;
                await PromptForGoogleEarthLocationAsync("Okay. What location should I search for?", ct).ConfigureAwait(false);
                return true;
            }

            _pendingLocationCorrectionSource = rejectedLocation;
            _awaitingGoogleEarthLocation = true;
            await OfferGoogleEarthLocationForConfirmationAsync(response, ct).ConfigureAwait(false);
            return true;
        }

        _awaitingGoogleEarthLocation = false;
        if (TryParseLocationSearchCommand(response, out string parsedPlace))
            response = parsedPlace;
        _pendingLocationFeedbackUtterance = learning.IsVoiceSampleCollectionEnabled ? response : null;
        await OfferGoogleEarthLocationForConfirmationAsync(response, ct).ConfigureAwait(false);
        return true;
    }

    private async Task OfferGoogleEarthLocationForConfirmationAsync(string place, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(place))
        {
            _awaitingGoogleEarthLocation = true;
            await PromptForGoogleEarthLocationAsync("I didn't catch the location. Please say the street address and city again.", ct).ConfigureAwait(false);
            return;
        }

        if (place.Length > 200)
        {
            _awaitingGoogleEarthLocation = true;
            await PromptForGoogleEarthLocationAsync("That location was too long. Please say a shorter place name or address.", ct).ConfigureAwait(false);
            return;
        }

        _pendingGoogleEarthLocation = place;
        _awaitingGoogleEarthLocationConfirmation = true;
        await PromptForGoogleEarthLocationAsync($"I heard {place}. Is that correct? Say yes to search, no to repeat it, or say the corrected location.", ct).ConfigureAwait(false);
    }

    private async Task PromptForGoogleEarthLocationAsync(string prompt, CancellationToken ct)
    {
        mainWindow.DisplayAnswer("Google Earth location", prompt, "System");
        await tts.SpeakAsync(prompt, ct).ConfigureAwait(false);
        _googleEarthLocationPromptExpiresAt = DateTimeOffset.UtcNow.AddSeconds(options.Value.ActiveListeningTimeoutSeconds);
        audioCapture.TriggerAwakeMode();
    }

    private async Task SaveLocationLearningAsync(string utterance, string place, string provider, CancellationToken ct)
    {
        try
        {
            await learning.RecordCorrectionAsync(utterance, place, LocalLearningService.LocationIntent, provider, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not save an opt-in location correction; continuing the confirmed search.");
        }
    }

    private async Task<bool> HandleLocationSearchCommandAsync(string command, CancellationToken ct)
    {
        if (!TryParseLocationSearchCommand(command, out string place))
            return false;

        _pendingLocationFeedbackUtterance = learning.IsVoiceSampleCollectionEnabled ? command : null;
        string correctedPlace = learning.GetCorrectedLocation(command) ?? place;
        await OfferGoogleEarthLocationForConfirmationAsync(correctedPlace, ct).ConfigureAwait(false);
        return true;
    }

    private async Task SearchAndOpenLocationAsync(string place, CancellationToken ct, string? feedbackQuery = null, bool userConfirmed = false)
    {
        if (!options.Value.EnableOnlineLookups)
        {
            await ReportLocalActionAsync("Find location", "Online lookups are disabled. Enable Assistant:EnableOnlineLookups in appsettings.json to search for places.", ct);
            return;
        }

        if (place.Length > 200)
        {
            await ReportLocalActionAsync("Find location", "Please search for a place name or address of 200 characters or fewer.", ct);
            return;
        }

        try
        {
            telemetry.CurrentState = PlasmaState.Thinking;
            GeocodedLocation? result = await locationSearch.FindAsync(place, ct).ConfigureAwait(false);
            if (result == null)
            {
                await ReportLocalActionAsync("Find location", $"I couldn't find a matching place for {place}.", ct);
                return;
            }

            if (userConfirmed && feedbackQuery != null)
            {
                await SaveLocationLearningAsync(feedbackQuery, result.DisplayName, string.Empty, ct).ConfigureAwait(false);
            }

            string answer = $"Found {result.DisplayName}. Going to that location in Google Earth. Location data © OpenStreetMap contributors.";
            toolLauncher.TryOpenGoogleEarth(result.Latitude, result.Longitude, out string action, out string error);
            if (!string.IsNullOrWhiteSpace(error))
                answer = $"Found {result.DisplayName}, but I couldn't navigate to it in Google Earth: {error}. Location data © OpenStreetMap contributors.";

            await ReportLocalActionAsync(action, answer, ct, "Location", feedbackQuery ?? place);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or ArgumentException or TaskCanceledException)
        {
            telemetry.CurrentState = PlasmaState.Idle;
            logger.LogWarning(ex, "Location lookup failed for a {QueryLength}-character place search.", place.Length);
            await ReportLocalActionAsync("Find location", "I couldn't retrieve that location right now. Check the internet connection or try again later.", ct);
        }
    }

    private static bool TryParseLocationSearchCommand(string command, out string place)
    {
        place = string.Empty;
        foreach (string prefix in LocationCommandPrefixes)
        {
            if (!command.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            string remaining = command[prefix.Length..].Trim();
            foreach (string suffix in LocationCommandSuffixes)
            {
                if (!remaining.EndsWith(suffix, StringComparison.Ordinal))
                    continue;

                place = remaining[..^suffix.Length].Trim();
                return place.Length > 0;
            }
        }

        foreach (string prefix in LocationSearchPrefixes)
        {
            if (!command.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            place = command[prefix.Length..].Trim();
            if (place.StartsWith("find ", StringComparison.Ordinal))
                place = place[5..].Trim();
            return place.Length > 0;
        }

        return false;
    }

    private async Task<bool> HandleWindowsToolCommandAsync(string normalizedUtterance, CancellationToken ct)
    {
        if (!toolLauncher.TryHandle(normalizedUtterance, out string action, out string error))
            return false;

        string answer = string.IsNullOrWhiteSpace(error) ? action : error;
        mainWindow.DisplayAnswer(action, answer, "Windows");
        await tts.SpeakAsync(answer, ct).ConfigureAwait(false);
        return true;
    }

    private async Task ReportLocalActionAsync(string query, string answer, CancellationToken ct, string source = "System", string? feedbackQuery = null)
    {
        telemetry.CurrentState = PlasmaState.Idle;
        mainWindow.DisplayAnswer(query, answer, source, feedbackQuery);
        await tts.SpeakAsync(answer, ct).ConfigureAwait(false);
    }

    private bool HandleLocalCommand(string cmd)
    {
        if (cmd is "exit" or "exit elektra" or "quit" or "quit elektra" or
            "shut down" or "shut down elektra" or "shutdown" or "shutdown elektra" or
            "close elektra" or "terminate elektra" or "turn off elektra")
        {
            mainWindow.ExitApplication();
            return true;
        }
        if (cmd is "hide elektra" or "minimize" or "go to tray") { mainWindow.HideToTray(); return true; }
        if (cmd is "show elektra" or "wake up") { mainWindow.RestoreFromTray(); return true; }
        if (cmd is "dismiss" or "clear" or "close answer") { mainWindow.DismissAnswerHud(); return true; }
        if (cmd is "stop" or "stop speaking" or "quiet") { tts.StopSpeaking(); return true; }
        if (cmd is "forget context" or "new topic" or "reset memory")
        {
            contextTracker.Clear();
            mainWindow.DisplayAnswer("Context Reset", "Conversational memory cleared.", "System");
            return true;
        }
        return false;
    }
}