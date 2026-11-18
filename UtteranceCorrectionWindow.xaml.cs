using System.Windows;

namespace Elektra.UI;

public partial class UtteranceCorrectionWindow : Window
{
    public string? CorrectedUtterance { get; private set; }

    public UtteranceCorrectionWindow(string recognizedUtterance)
    {
        InitializeComponent();
        RecognizedText.Text = $"Elektra heard: \"{recognizedUtterance}\". Type the question you intended. This correction is stored locally when learning is enabled.";
        Loaded += (_, _) => CorrectionInput.Focus();
    }

    private void OnUseCorrectionClicked(object sender, RoutedEventArgs e)
    {
        string corrected = CorrectionInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(corrected))
        {
            CorrectionInput.Focus();
            return;
        }

        CorrectedUtterance = corrected;
        DialogResult = true;
    }
}
