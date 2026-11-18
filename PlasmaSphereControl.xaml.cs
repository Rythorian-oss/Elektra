/*
 * Creator: Justin L. Ross | Farmington, Maine. | Remote Location: Iceland | Black Star Research Facility.
 */

using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NAudio.Wave;
using Elektra.Audio;
using Elektra.Configuration;
using Elektra.Learning;
using Elektra.UI.Effects;
using Color = System.Windows.Media.Color;

namespace Elektra.UI.Controls
{
    public partial class PlasmaSphereControl : System.Windows.Controls.UserControl
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly PlasmaOrbEffect _plasmaShader = new PlasmaOrbEffect();
        private AudioTelemetryBridge? _telemetry;
        private AudioCaptureService? _audioService;
        private VoiceSynthesisService? _ttsService;
        private UserPreferencesService? _preferencesService;
        private LocalLearningService? _learningService;
        private Action? _onHide;
        private Action? _onExit;
        private Action? _onVoicePracticeLesson;
        private float _smoothedEnergy;
        private PlasmaState? _lastRenderedState;

        private bool _isRenderingSubscribed;

        public PlasmaSphereControl()
        {
            InitializeComponent();
            ShaderSurface.Effect = _plasmaShader;
            Loaded += (_, _) => UpdateRenderingSubscription();
            Unloaded += (_, _) => SetRenderingSubscription(false);
            IsVisibleChanged += (_, _) => UpdateRenderingSubscription();
        }

        private void UpdateRenderingSubscription() =>
            SetRenderingSubscription(IsLoaded && IsVisible);

        private void SetRenderingSubscription(bool subscribe)
        {
            if (_isRenderingSubscribed == subscribe)
                return;

            if (subscribe)
                CompositionTarget.Rendering += OnRenderFrame;
            else
                CompositionTarget.Rendering -= OnRenderFrame;

            _isRenderingSubscribed = subscribe;
        }

        public void BindServices(
            AudioTelemetryBridge telemetry,
            AudioCaptureService audio,
            VoiceSynthesisService tts,
            UserPreferencesService preferences,
            LocalLearningService learning,
            Action onHide,
            Action onExit,
            Action onVoicePracticeLesson)
        {
            _telemetry = telemetry;
            _audioService = audio;
            _ttsService = tts;
            _preferencesService = preferences;
            _learningService = learning;
            _onHide = onHide;
            _onExit = onExit;
            _onVoicePracticeLesson = onVoicePracticeLesson;
        }

        private void OnRenderFrame(object? sender, EventArgs e)
        {
            _plasmaShader.Time = (float)_clock.Elapsed.TotalSeconds;
            if (_telemetry == null) return;

            float target = _telemetry.CurrentRms;
            _smoothedEnergy += (target - _smoothedEnergy) * (target > _smoothedEnergy ? 0.35f : 0.12f);
            _plasmaShader.AudioEnergy = _smoothedEnergy;

            PlasmaState state = _telemetry.CurrentState;
            if (_lastRenderedState == state)
                return;

            (Color p, Color s) = state switch
            {
                PlasmaState.Listening => (Color.FromRgb(6, 182, 212), Color.FromRgb(103, 232, 249)),
                PlasmaState.Thinking => (Color.FromRgb(236, 72, 153), Color.FromRgb(253, 186, 116)),
                PlasmaState.Speaking => (Color.FromRgb(16, 185, 129), Color.FromRgb(110, 231, 183)),
                PlasmaState.Warning => (Color.FromRgb(220, 38, 38), Color.FromRgb(252, 129, 129)),
                _ => (Color.FromRgb(124, 58, 237), Color.FromRgb(196, 181, 253))
            };

            _plasmaShader.PrimaryColor = p;
            _plasmaShader.SecondaryColor = s;
            _lastRenderedState = state;
        }

        private void OnOrbContextMenuOpened(object sender, RoutedEventArgs e)
        {
            StopSpeakingMenuItem.IsEnabled = _telemetry?.IsAssistantSpeaking ?? false;
            bool awake = _audioService?.IsAwake ?? false;
            WakeMenuItem.Header = awake ? "Listening..." : "Listen Now (Ask Question)";
            WakeMenuItem.IsEnabled = !awake;
            bool learningEnabled = _preferencesService?.Current.EnableLocalLearning ?? false;
            LocalLearningMenuItem.IsChecked = learningEnabled;
            LocalLearningMenuItem.Header = learningEnabled ? "Local Learning: On" : "Local Learning: Off";
            LocalLearningMenuItem.ToolTip = _learningService?.Status;
            bool voicePracticeEnabled = _learningService?.IsVoiceSampleCollectionEnabled ?? false;
            VoicePracticeMenuItem.IsEnabled = voicePracticeEnabled && !awake;
            VoicePracticeMenuItem.ToolTip = voicePracticeEnabled
                ? "Speak a phrase, listen for your repeat, and save it locally when recognized."
                : "Enable Local Learning and local voice-sample consent in an answer panel first.";
            bool automaticPractice = _preferencesService?.Current.EnableAutomaticVoicePracticeLessons ?? false;
            AutomaticVoicePracticeMenuItem.IsChecked = automaticPractice;
            AutomaticVoicePracticeMenuItem.Header = automaticPractice
                ? "Automatic Voice Practice: On"
                : "Automatic Voice Practice: Off";
            AutomaticVoicePracticeMenuItem.IsEnabled = voicePracticeEnabled;
            AutomaticVoicePracticeMenuItem.ToolTip = "After you correct a spoken question, Elektra can offer a repeat-after-me lesson using that correction.";
        }

        private void OnLocalLearningClicked(object sender, RoutedEventArgs e)
        {
            bool enabled = LocalLearningMenuItem.IsChecked;
            _learningService?.SetLearningEnabled(enabled);
            LocalLearningMenuItem.Header = enabled ? "Local Learning: On" : "Local Learning: Off";
            LocalLearningMenuItem.ToolTip = _learningService?.Status;
        }

        private void OnVoicePracticeClicked(object sender, RoutedEventArgs e) => _onVoicePracticeLesson?.Invoke();

        private void OnAutomaticVoicePracticeClicked(object sender, RoutedEventArgs e)
        {
            if (_preferencesService == null || _learningService?.IsEnabled != true || !_preferencesService.Current.EnableVoskVoiceSamples)
                return;

            bool enabled = AutomaticVoicePracticeMenuItem.IsChecked;
            _preferencesService.Update(preferences => preferences with { EnableAutomaticVoicePracticeLessons = enabled });
            AutomaticVoicePracticeMenuItem.Header = enabled
                ? "Automatic Voice Practice: On"
                : "Automatic Voice Practice: Off";
        }

        private void OnWakeClicked(object sender, RoutedEventArgs e) => _audioService?.TriggerAwakeMode();
        private void OnStopSpeakingClicked(object sender, RoutedEventArgs e) => _ttsService?.StopSpeaking();
        private void OnHideClicked(object sender, RoutedEventArgs e) => _onHide?.Invoke();
        private void OnExitClicked(object sender, RoutedEventArgs e) => _onExit?.Invoke();

        private void OnMicrophoneSubmenuOpened(object sender, RoutedEventArgs e)
        {
            MicrophoneSubMenu.Items.Clear();
            int count = WaveInEvent.DeviceCount;
            var style = (Style)FindResource("ElektraGlassMenuItem");

            if (count == 0)
            {
                MicrophoneSubMenu.Items.Add(new System.Windows.Controls.MenuItem
                {
                    Header = "No microphones detected",
                    IsEnabled = false,
                    Style = style
                });
                return;
            }

            int active = _audioService?.CurrentDeviceNumber ?? 0;
            for (int i = 0; i < count; i++)
            {
                int idx = i;
                var item = new System.Windows.Controls.MenuItem
                {
                    Header = $"{idx + 1}. {WaveInEvent.GetCapabilities(idx).ProductName.Trim()}",
                    IsChecked = idx == active,
                    Style = style
                };
                item.Click += (_, _) =>
                {
                    if (idx != _audioService?.CurrentDeviceNumber)
                        _audioService?.SwitchDevice(idx);
                };
                MicrophoneSubMenu.Items.Add(item);
            }
        }
    }
}