/*
 * Creator: Justin L. Ross | Farmington, Maine. | Remote Location: Iceland | Black Star Research Facility.
 */

using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Elektra.Audio;
using Elektra.Configuration;
using Elektra.Learning;
using Elektra.UI.Assets;
using Elektra.UI.Interop;
using Application = System.Windows.Application;
using Clipboard = System.Windows.Clipboard;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace Elektra.UI;

public partial class MainWindow : Window
{
    private static readonly Duration WindowFadeDuration = new(TimeSpan.FromMilliseconds(200));
    private static readonly Duration HudEnterDuration = new(TimeSpan.FromMilliseconds(220));
    private static readonly Duration HudExitDuration = new(TimeSpan.FromMilliseconds(180));

    private readonly DispatcherTimer _hudHideTimer;
    private readonly DispatcherTimer _copyFeedbackTimer;
    private readonly AudioCaptureService _audioService;
    private readonly AudioTelemetryBridge _telemetry;
    private readonly UserPreferencesService _prefsService;
    private readonly LocalLearningService _learning;
    private readonly ILogger<MainWindow> _logger;
    private GlobalHotKeyManager? _hotKeyManager;
    private Func<string, Task>? _locationSearchHandler;

    public event Action? VoicePracticeLessonRequested;

    private string _currentAnswerSource = string.Empty;
    private string _currentFeedbackQuery = string.Empty;
    private bool _isUpdatingLearningConsent;
    private bool _isRestoringPosition;
    private bool _isTransitioningToHidden;
    private bool _isHidingHud;
    private bool _isClosed;
    private int _windowAnimGeneration;
    private int _hudAnimGeneration;

    public MainWindow(
        AudioTelemetryBridge telemetry,
        AudioCaptureService audioService,
        VoiceSynthesisService ttsService,
        UserPreferencesService prefsService,
        LocalLearningService learning,
        ILogger<MainWindow> logger)
    {
        InitializeComponent();
        _telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
        _audioService = audioService ?? throw new ArgumentNullException(nameof(audioService));
        _prefsService = prefsService ?? throw new ArgumentNullException(nameof(prefsService));
        _learning = learning ?? throw new ArgumentNullException(nameof(learning));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        Icon = PlasmaIconGenerator.CreateWpfWindowIcon(PlasmaState.Idle);

        PlasmaOrb.BindServices(
            telemetry, audioService, ttsService, prefsService, learning,
            onHide: HideToTray,
            onExit: () => Application.Current.Shutdown(),
            onVoicePracticeLesson: () => VoicePracticeLessonRequested?.Invoke());

        _hudHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
        _hudHideTimer.Tick += (_, _) =>
        {
            _hudHideTimer.Stop();
            HideResponseHud();
        };

        _copyFeedbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _copyFeedbackTimer.Tick += (_, _) =>
        {
            CopyAnswerButton.Tag = null;
            CopyAnswerButton.ToolTip = "Copy answer (Ctrl+C)";
            _copyFeedbackTimer.Stop();
        };

        LocationChanged += OnOrbLocationChanged;
        Loaded += OnWindowLoaded;
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        if (_prefsService.Current?.StartHiddenInTray == true)
        {
            Opacity = 0.0;
            OrbScaleTransform.ScaleX = 0.88;
            OrbScaleTransform.ScaleY = 0.88;
            Hide();
            return;
        }

        AnimateWindowIn();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        RestoreOrbPosition();
        _hotKeyManager = new GlobalHotKeyManager(this, () =>
        {
            if (IsVisible && !_isTransitioningToHidden)
                HideToTray();
            else
                RestoreFromTray();
        });
        if (!_hotKeyManager.RegisterWinAltE())
            _logger.LogWarning("Global hotkey Win+Alt+E could not be registered; it may already be in use.");
    }

    private void OnOpenLocationSearchClicked(object sender, RoutedEventArgs e)
    {
        _hudHideTimer.Stop();
        if (ResponseHud.Visibility == Visibility.Visible)
            HideResponseHud();

        LocationSearchStatusText.Text = "Your location is sent to the configured geocoding provider.";
        LocationSearchStatusText.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(147, 197, 253));
        LocationSearchHud.BeginAnimation(OpacityProperty, null);
        LocationSearchHud.Opacity = 1;
        LocationSearchHud.Visibility = Visibility.Visible;
        LocationSearchHud.IsHitTestVisible = true;
        Activate();
        Dispatcher.BeginInvoke(() =>
        {
            LocationSearchTextBox.Focus();
            LocationSearchTextBox.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void OnCloseLocationSearchClicked(object sender, RoutedEventArgs e) => HideLocationSearchHud();

    private async void OnSearchLocationClicked(object sender, RoutedEventArgs e)
    {
        string place = LocationSearchTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(place))
        {
            LocationSearchStatusText.Text = "Enter a place name or full address to continue.";
            LocationSearchTextBox.Focus();
            return;
        }

        if (place.Length > 200)
        {
            LocationSearchStatusText.Text = "Location searches must be 200 characters or fewer.";
            LocationSearchTextBox.Focus();
            return;
        }

        if (_locationSearchHandler == null)
        {
            LocationSearchStatusText.Text = "Location search is not ready yet. Please try again in a moment.";
            return;
        }

        SearchLocationButton.IsEnabled = false;
        LocationSearchTextBox.IsEnabled = false;
        SearchLocationButton.Content = "Searching...";
        LocationSearchStatusText.Text = "Looking up the location and opening Google Earth...";
        try
        {
            await _locationSearchHandler(place);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            _logger.LogError(ex, "Typed Google Earth location search failed.");
            LocationSearchStatusText.Text = "The search could not be completed. Check your connection and try again.";
        }
        finally
        {
            SearchLocationButton.IsEnabled = true;
            LocationSearchTextBox.IsEnabled = true;
            SearchLocationButton.Content = "Find in Google Earth";
        }
    }

    private void OnLocationSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            OnSearchLocationClicked(sender, e);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            HideLocationSearchHud();
        }
    }

    private void HideLocationSearchHud()
    {
        LocationSearchHud.IsHitTestVisible = false;
        LocationSearchHud.BeginAnimation(OpacityProperty, null);
        LocationSearchHud.Opacity = 0;
        LocationSearchHud.Visibility = Visibility.Collapsed;
    }

    private void OnOrbMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.LeftButton != MouseButtonState.Pressed)
            return;

        if (e.ClickCount == 2)
        {
            _audioService.TriggerAwakeMode();
            e.Handled = true;
            return;
        }

        try
        {
            DragMove();
            e.Handled = true;
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogDebug(ex, "DragMove ignored because left mouse button was released.");
        }
    }

    public void SetLocationSearchHandler(Func<string, Task>? handler) =>
        RunOnUiThread(() => _locationSearchHandler = handler);

    public void DisplayAnswer(string query, string answer, string source = "Unknown", string? feedbackQuery = null)
    {
        RunOnUiThread(() =>
        {
            if (LocationSearchHud.Visibility == Visibility.Visible)
                HideLocationSearchHud();

            _copyFeedbackTimer.Stop();
            CopyAnswerButton.Tag = null;
            CopyAnswerButton.ToolTip = "Copy answer (Ctrl+C)";

            _currentAnswerSource = source ?? "Unknown";
            _currentFeedbackQuery = string.IsNullOrWhiteSpace(feedbackQuery) ? query : feedbackQuery;
            QueryText.Text = string.IsNullOrWhiteSpace(query) ? string.Empty : $"\"{query.Trim()}\"";
            AnswerText.Text = answer ?? string.Empty;
            FeedbackPanel.Visibility = IsFeedbackEligible(_currentAnswerSource) ? Visibility.Visible : Visibility.Collapsed;
            _isUpdatingLearningConsent = true;
            EnableLearningCheckBox.IsChecked = _prefsService.Current.EnableLocalLearning;
            PauseTrainingCheckBox.IsChecked = _prefsService.Current.PauseLearningTraining;
            EnableVoiceSamplesCheckBox.IsChecked = _prefsService.Current.EnableVoskVoiceSamples;
            EnableVoiceSamplesCheckBox.IsEnabled = _learning.IsEnabled;
            UnitsPreferenceComboBox.SelectedIndex = string.Equals(_prefsService.Current.PreferredUnits, "Metric", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            _isUpdatingLearningConsent = false;
            FeedbackActionsPanel.IsEnabled = _learning.IsEnabled;
            LearningStatusText.Text = _learning.Status;
            CorrectionPanel.Visibility = Visibility.Collapsed;
            CorrectionPanel.IsEnabled = _learning.IsEnabled;
            CorrectionIntentComboBox.SelectedIndex = 0;
            CorrectionTextBox.Text = string.Empty;
            LearningMemoriesList.ItemsSource = null;
            _ = RefreshLearningStatusAsync();
            _ = RefreshLearningMemoriesAsync();

            if (!IsVisible || _isTransitioningToHidden)
            {
                RestoreFromTray();
            }

            ShowResponseHud();
            _hudHideTimer.Stop();
            if (!ResponseHud.IsMouseOver)
            {
                _hudHideTimer.Start();
            }
        });
    }

    private static bool IsFeedbackEligible(string source) =>
        !string.IsNullOrWhiteSpace(source) && source is not "System" and not "Windows" and not "Security" and not "Unknown";

    private void OnLearningConsentChanged(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingLearningConsent)
            return;

        bool enabled = EnableLearningCheckBox.IsChecked == true;
        _learning.SetLearningEnabled(enabled);
        if (!enabled)
        {
            _isUpdatingLearningConsent = true;
            EnableVoiceSamplesCheckBox.IsChecked = false;
            _isUpdatingLearningConsent = false;
            _prefsService.Update(preferences => preferences with { EnableVoskVoiceSamples = false });
            _audioService.ClearBufferedVoiceSamples();
        }
        EnableVoiceSamplesCheckBox.IsEnabled = _learning.IsEnabled;
        FeedbackActionsPanel.IsEnabled = _learning.IsEnabled;
        CorrectionPanel.IsEnabled = _learning.IsEnabled;
        LearningStatusText.Text = _learning.Status;
        _ = RefreshLearningStatusAsync();
    }

    private void OnVoiceSampleConsentChanged(object sender, RoutedEventArgs e)
    {
        if (_isUpdatingLearningConsent)
            return;

        bool enabled = _learning.IsEnabled && EnableVoiceSamplesCheckBox.IsChecked == true;
        _prefsService.Update(preferences => preferences with
        {
            EnableVoskVoiceSamples = enabled,
            EnableAutomaticVoicePracticeLessons = enabled && preferences.EnableAutomaticVoicePracticeLessons
        });
        if (!enabled)
            _audioService.ClearBufferedVoiceSamples();
        LearningStatusText.Text = enabled
            ? "Local voice-sample capture is enabled. Recordings remain on this device and can be deleted in learning review."
            : "Local voice-sample capture is off.";
    }

    private async void OnWorkedFeedbackClicked(object sender, RoutedEventArgs e)
    {
        if (!_learning.IsEnabled || string.IsNullOrWhiteSpace(_currentFeedbackQuery))
            return;

        try
        {
            await _learning.RecordWorkedAsync(_currentFeedbackQuery,
                LocalLearningService.InferIntent(_currentAnswerSource), _currentAnswerSource);
            await RefreshLearningStatusAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not save local positive feedback.");
            LearningStatusText.Text = "Feedback could not be saved.";
        }
    }

    private async void OnNegativeFeedbackClicked(object sender, RoutedEventArgs e)
    {
        if (!_learning.IsEnabled || string.IsNullOrWhiteSpace(_currentFeedbackQuery))
            return;

        try
        {
            await _learning.RecordNegativeFeedbackAsync(_currentFeedbackQuery, "WrongAnswer", _currentAnswerSource);
            await RefreshLearningStatusAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not save local negative feedback.");
            LearningStatusText.Text = "Feedback could not be saved.";
        }
    }

    private void OnPreferredUnitsChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isUpdatingLearningConsent || UnitsPreferenceComboBox.SelectedItem is not System.Windows.Controls.ComboBoxItem item)
            return;

        _prefsService.Update(preferences => preferences with { PreferredUnits = item.Tag?.ToString() });
    }

    private void OnTrainingPauseChanged(object sender, RoutedEventArgs e)
    {
        _prefsService.Update(preferences => preferences with { PauseLearningTraining = PauseTrainingCheckBox.IsChecked == true });
        LearningStatusText.Text = _learning.IsTrainingPaused
            ? "Scheduled training is paused; the current model remains active. " + _learning.Status
            : _learning.Status;
    }

    private async void OnSaveMemoryClicked(object sender, RoutedEventArgs e)
    {
        if (!_learning.IsEnabled)
        {
            LearningStatusText.Text = "Turn on local learning to save a memory.";
            return;
        }

        try
        {
            await _learning.SaveMemoryAsync(MemoryKeyTextBox.Text, MemoryValueTextBox.Text);
            MemoryKeyTextBox.Clear();
            MemoryValueTextBox.Clear();
            await RefreshLearningMemoriesAsync();
            await RefreshLearningStatusAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not save an explicit local memory.");
            LearningStatusText.Text = ex is ArgumentException ? ex.Message : "The memory could not be saved.";
        }
    }

    private async void OnDeleteMemoryClicked(object sender, RoutedEventArgs e)
    {
        if (LearningMemoriesList.SelectedItem is not UserMemory memory)
            return;

        try
        {
            await _learning.DeleteMemoryAsync(memory.Id);
            await RefreshLearningMemoriesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not delete the selected local memory.");
            LearningStatusText.Text = "The selected memory could not be deleted.";
        }
    }

    private void OnReviewLearningClicked(object sender, RoutedEventArgs e)
    {
        var reviewWindow = new LearningReviewWindow(_learning) { Owner = this };
        reviewWindow.ShowDialog();
        _ = RefreshLearningMemoriesAsync();
        _ = RefreshLearningStatusAsync();
    }

    private async Task RefreshLearningMemoriesAsync()
    {
        if (!_learning.IsEnabled)
        {
            LearningMemoriesList.ItemsSource = null;
            return;
        }

        try
        {
            IReadOnlyList<UserMemory> memories = await _learning.SearchMemoriesAsync(string.Empty);
            RunOnUiThread(() => LearningMemoriesList.ItemsSource = memories);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not list explicit local memories.");
        }
    }

    private void OnCorrectionClicked(object sender, RoutedEventArgs e)
    {
        if (!_learning.IsEnabled)
        {
            LearningStatusText.Text = "Turn on local learning to save this correction.";
            return;
        }
        CorrectionPanel.Visibility = Visibility.Visible;
        CorrectionTextBox.Focus();
    }

    private async void OnSaveCorrectionClicked(object sender, RoutedEventArgs e)
    {
        string corrected = CorrectionTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(_currentFeedbackQuery) || string.IsNullOrWhiteSpace(corrected))
        {
            LearningStatusText.Text = "Enter the corrected request or place name first.";
            return;
        }

        try
        {
            if (CorrectionIntentComboBox.SelectedIndex == 2)
            {
                await _learning.RecordSpeechCorrectionAsync(_currentFeedbackQuery, corrected);
            }
            else
            {
                string label = CorrectionIntentComboBox.SelectedIndex == 1
                    ? LocalLearningService.LocationIntent
                    : LocalLearningService.KnowledgeIntent;
                await _learning.RecordCorrectionAsync(_currentFeedbackQuery, corrected, label, _currentAnswerSource);
            }
            CorrectionPanel.Visibility = Visibility.Collapsed;
            await RefreshLearningStatusAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not save local correction.");
            LearningStatusText.Text = "The correction could not be saved.";
        }
    }

    private async void OnRollbackLearningClicked(object sender, RoutedEventArgs e)
    {
        if (!_learning.IsEnabled)
        {
            LearningStatusText.Text = "Turn on local learning to manage its trained model.";
            return;
        }
        try
        {
            bool rolledBack = await _learning.RollbackAsync();
            LearningStatusText.Text = rolledBack ? _learning.Status : "No previous trained model is available to restore.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not roll back the local intent model.");
            LearningStatusText.Text = "The trained model could not be rolled back.";
        }
    }

    private async void OnClearLearningClicked(object sender, RoutedEventArgs e)
    {
        if (System.Windows.MessageBox.Show(this, "Delete saved feedback, corrections, provider rankings, memories, and trained models? This cannot be undone.",
                "Clear local learning data", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        try
        {
            await _learning.ClearLearningDataAsync();
            LearningMemoriesList.ItemsSource = null;
            LearningStatusText.Text = _learning.Status;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not clear local learning data.");
            LearningStatusText.Text = "Local learning data could not be deleted.";
        }
    }

    private async Task RefreshLearningStatusAsync()
    {
        if (!_learning.IsEnabled)
        {
            RunOnUiThread(() => LearningStatusText.Text = _learning.Status);
            return;
        }
        try
        {
            LearningMetrics metrics = await _learning.GetMetricsAsync();
            VoiceSampleMetrics voiceMetrics = await _learning.GetVoiceSampleMetricsAsync();
            string voiceSummary = $" Voice samples: {voiceMetrics.TotalSamples} total, {voiceMetrics.TrainingSamples} training / {voiceMetrics.ValidationSamples} held out.";
            string summary = metrics.ModelAccuracy.HasValue
                ? $"Validation: {metrics.ModelAccuracy:P1} model / {metrics.BaselineAccuracy:P1} baseline; macro precision/recall {metrics.MacroPrecision:P1}/{metrics.MacroRecall:P1} ({metrics.ExampleCount} examples). "
                : $"Training examples: {metrics.ExampleCount} " +
                    (metrics.ExamplesByIntent is { Count: > 0 } counts
                        ? string.Join(", ", counts.Select(pair => $"{pair.Key}: {pair.Value}")) + ". "
                        : ". ");
            RunOnUiThread(() => LearningStatusText.Text = summary + voiceSummary + " " + _learning.Status);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read local learning status.");
        }
    }

    public void DismissAnswerHud() => RunOnUiThread(() =>
    {
        _hudHideTimer.Stop();
        HideResponseHud();
    });

    public void ExitApplication() => RunOnUiThread(() => Application.Current.Shutdown());

    public async Task<string?> PromptForUtteranceCorrectionAsync(string recognizedUtterance)
    {
        return await Dispatcher.InvokeAsync(() =>
        {
            bool ownerWasVisible = IsVisible;
            if (!ownerWasVisible)
                Show();

            try
            {
                var dialog = new UtteranceCorrectionWindow(recognizedUtterance) { Owner = this };
                return dialog.ShowDialog() == true ? dialog.CorrectedUtterance : null;
            }
            finally
            {
                if (!ownerWasVisible)
                    Hide();
            }
        }).Task.ConfigureAwait(false);
    }

    public async Task<string?> PromptForSecurityPasswordAsync(string folderName, bool encrypting, bool confirmPassword)
    {
        return await Dispatcher.InvokeAsync(() =>
        {
            bool ownerWasVisible = IsVisible;
            if (!ownerWasVisible)
                Show();

            PlasmaState previousState = _telemetry.CurrentState;
            _telemetry.CurrentState = PlasmaState.Warning;
            try
            {
                var dialog = new PasswordPromptWindow(folderName, encrypting, confirmPassword) { Owner = this };
                return dialog.ShowDialog() == true ? dialog.Password : null;
            }
            finally
            {
                _telemetry.CurrentState = previousState;
                if (!ownerWasVisible)
                    Hide();
            }
        }).Task.ConfigureAwait(false);
    }

    public void HideToTray()
    {
        RunOnUiThread(() =>
        {
            if (!IsVisible || _isTransitioningToHidden) return;
            _isTransitioningToHidden = true;
            _prefsService.Update(p => p with { StartHiddenInTray = true }, debounceMs: 100);

            int generation = ++_windowAnimGeneration;
            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            var opAnim = new DoubleAnimation
            {
                To = 0.0,
                Duration = WindowFadeDuration,
                EasingFunction = ease
            };
            var scAnim = new DoubleAnimation
            {
                To = 0.88,
                Duration = WindowFadeDuration,
                EasingFunction = ease
            };

            opAnim.Completed += (_, _) =>
            {
                if (_isClosed || generation != _windowAnimGeneration || !_isTransitioningToHidden)
                    return;

                BeginAnimation(OpacityProperty, null);
                OrbScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                OrbScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);

                Opacity = 0.0;
                OrbScaleTransform.ScaleX = 0.88;
                OrbScaleTransform.ScaleY = 0.88;
                Hide();
                _isTransitioningToHidden = false;
            };

            BeginAnimation(OpacityProperty, opAnim, HandoffBehavior.SnapshotAndReplace);
            OrbScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, scAnim, HandoffBehavior.SnapshotAndReplace);
            OrbScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, scAnim, HandoffBehavior.SnapshotAndReplace);
        });
    }

    public void RestoreFromTray()
    {
        RunOnUiThread(() =>
        {
            _isTransitioningToHidden = false;
            _prefsService.Update(p => p with { StartHiddenInTray = false }, debounceMs: 100);

            if (!IsVisible)
            {
                BeginAnimation(OpacityProperty, null);
                OrbScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                OrbScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);

                Opacity = 0.0;
                OrbScaleTransform.ScaleX = 0.88;
                OrbScaleTransform.ScaleY = 0.88;
                Show();
            }

            WindowState = WindowState.Normal;
            Activate();
            Focus();
            AnimateWindowIn();
        });
    }

    private void AnimateWindowIn()
    {
        int generation = ++_windowAnimGeneration;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var opAnim = new DoubleAnimation
        {
            To = 1.0,
            Duration = WindowFadeDuration,
            EasingFunction = ease
        };
        var scAnim = new DoubleAnimation
        {
            To = 1.0,
            Duration = WindowFadeDuration,
            EasingFunction = ease
        };

        opAnim.Completed += (_, _) =>
        {
            if (_isClosed || generation != _windowAnimGeneration)
                return;

            BeginAnimation(OpacityProperty, null);
            OrbScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            OrbScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);

            Opacity = 1.0;
            OrbScaleTransform.ScaleX = 1.0;
            OrbScaleTransform.ScaleY = 1.0;
        };

        BeginAnimation(OpacityProperty, opAnim, HandoffBehavior.SnapshotAndReplace);
        OrbScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, scAnim, HandoffBehavior.SnapshotAndReplace);
        OrbScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, scAnim, HandoffBehavior.SnapshotAndReplace);
    }

    private void ShowResponseHud()
    {
        _isHidingHud = false;
        if (ResponseHud.Visibility != Visibility.Visible)
        {
            ResponseHud.BeginAnimation(OpacityProperty, null);
            HudSlideTransform.BeginAnimation(TranslateTransform.YProperty, null);

            ResponseHud.Opacity = 0.0;
            HudSlideTransform.Y = 12.0;
            ResponseHud.Visibility = Visibility.Visible;
        }

        ResponseHud.IsHitTestVisible = true;
        if (IsVisible) Activate();

        int generation = ++_hudAnimGeneration;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var opAnim = new DoubleAnimation
        {
            To = 1.0,
            Duration = HudEnterDuration,
            EasingFunction = ease
        };
        var slAnim = new DoubleAnimation
        {
            To = 0.0,
            Duration = HudEnterDuration,
            EasingFunction = ease
        };

        opAnim.Completed += (_, _) =>
        {
            if (_isClosed || generation != _hudAnimGeneration || _isHidingHud)
                return;

            ResponseHud.BeginAnimation(OpacityProperty, null);
            HudSlideTransform.BeginAnimation(TranslateTransform.YProperty, null);

            ResponseHud.Opacity = 1.0;
            HudSlideTransform.Y = 0.0;
        };

        ResponseHud.BeginAnimation(OpacityProperty, opAnim, HandoffBehavior.SnapshotAndReplace);
        HudSlideTransform.BeginAnimation(TranslateTransform.YProperty, slAnim, HandoffBehavior.SnapshotAndReplace);
    }

    private void HideResponseHud()
    {
        if (ResponseHud.Visibility != Visibility.Visible || _isHidingHud) return;
        _isHidingHud = true;
        ResponseHud.IsHitTestVisible = false;

        int generation = ++_hudAnimGeneration;
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var opAnim = new DoubleAnimation
        {
            To = 0.0,
            Duration = HudExitDuration,
            EasingFunction = ease
        };
        var slAnim = new DoubleAnimation
        {
            To = 8.0,
            Duration = HudExitDuration,
            EasingFunction = ease
        };

        opAnim.Completed += (_, _) =>
        {
            if (_isClosed || generation != _hudAnimGeneration || !_isHidingHud)
                return;

            ResponseHud.BeginAnimation(OpacityProperty, null);
            HudSlideTransform.BeginAnimation(TranslateTransform.YProperty, null);

            ResponseHud.Opacity = 0.0;
            HudSlideTransform.Y = 12.0;
            ResponseHud.Visibility = Visibility.Collapsed;
            _isHidingHud = false;
        };

        ResponseHud.BeginAnimation(OpacityProperty, opAnim, HandoffBehavior.SnapshotAndReplace);
        HudSlideTransform.BeginAnimation(TranslateTransform.YProperty, slAnim, HandoffBehavior.SnapshotAndReplace);
    }

    private void OnCanExecuteCopyAnswer(object sender, CanExecuteRoutedEventArgs e) =>
        e.CanExecute = ResponseHud.Visibility == Visibility.Visible && !_isHidingHud && !string.IsNullOrWhiteSpace(AnswerText.Text);

    private void OnExecuteCopyAnswer(object sender, ExecutedRoutedEventArgs e)
    {
        CopyCurrentAnswerToClipboard();
        e.Handled = true;
    }

    private void OnCopyAnswerClicked(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        CopyCurrentAnswerToClipboard();
    }

    private void OnCloseAnswerClicked(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        _hudHideTimer.Stop();
        HideResponseHud();
    }

    private void CopyCurrentAnswerToClipboard()
    {
        string raw = AnswerText.Text;
        if (string.IsNullOrWhiteSpace(raw)) return;

        bool isMath = _currentAnswerSource.StartsWith("Math", StringComparison.OrdinalIgnoreCase);
        string toCopy = isMath && raw.LastIndexOf(" = ", StringComparison.Ordinal) is int eq && eq >= 0
            ? raw[(eq + 3)..].Trim()
            : raw.Trim();

        try
        {
            Clipboard.SetDataObject(toCopy, copy: true);
            CopyAnswerButton.Tag = "Copied";
            CopyAnswerButton.ToolTip = isMath ? $"Copied \"{toCopy}\"" : "Copied!";

            if (!ResponseHud.IsMouseOver)
            {
                _hudHideTimer.Stop();
                _hudHideTimer.Start();
            }

            _copyFeedbackTimer.Stop();
            _copyFeedbackTimer.Start();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Clipboard copy failed.");
        }
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        if (LocationSearchHud.Visibility == Visibility.Visible)
        {
            HideLocationSearchHud();
            e.Handled = true;
        }
        else if (ResponseHud.Visibility == Visibility.Visible && !_isHidingHud)
        {
            _hudHideTimer.Stop();
            HideResponseHud();
            e.Handled = true;
        }
    }

    private void OnHudMouseEnter(object sender, MouseEventArgs e) => _hudHideTimer.Stop();

    private void OnHudMouseLeave(object sender, MouseEventArgs e)
    {
        if (ResponseHud.Visibility == Visibility.Visible && !_isHidingHud)
            _hudHideTimer.Start();
    }

    private void RestoreOrbPosition()
    {
        if (_prefsService.Current is not { OrbLeft: double l, OrbTop: double t }) return;
        if (double.IsNaN(l) || double.IsInfinity(l) || double.IsNaN(t) || double.IsInfinity(t)) return;

        double minLeft = SystemParameters.VirtualScreenLeft;
        double maxLeft = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - Width;
        double minTop = SystemParameters.VirtualScreenTop;
        double maxTop = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - Height;

        if (maxLeft < minLeft) maxLeft = minLeft;
        if (maxTop < minTop) maxTop = minTop;

        double cx = l + Width / 2.0;
        double cy = t + Height / 2.0;

        if (cx >= SystemParameters.VirtualScreenLeft &&
            cx <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth &&
            cy >= SystemParameters.VirtualScreenTop &&
            cy <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight)
        {
            _isRestoringPosition = true;
            try
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = Math.Clamp(l, minLeft, maxLeft);
                Top = Math.Clamp(t, minTop, maxTop);
            }
            finally
            {
                _isRestoringPosition = false;
            }
        }
    }

    private void OnOrbLocationChanged(object? sender, EventArgs e)
    {
        if (_isRestoringPosition || _isClosed || WindowState != WindowState.Normal || Left < -10000 || Top < -10000)
            return;

        double l = Left, t = Top;
        _prefsService.Update(p => p with { OrbLeft = l, OrbTop = t }, debounceMs: 500);
    }

    private void RunOnUiThread(Action action)
    {
        if (_isClosed) return;

        if (Dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (!_isClosed) action();
            });
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _isClosed = true;
        LocationChanged -= OnOrbLocationChanged;
        Loaded -= OnWindowLoaded;
        _hudHideTimer.Stop();
        _copyFeedbackTimer.Stop();
        _locationSearchHandler = null;
        _hotKeyManager?.Dispose();
        base.OnClosed(e);
    }
}