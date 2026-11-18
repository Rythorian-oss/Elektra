using System.Windows;
using Elektra.Learning;

namespace Elektra.UI;

public partial class LearningReviewWindow : Window
{
    private readonly LocalLearningService _learning;

    public LearningReviewWindow(LocalLearningService learning)
    {
        InitializeComponent();
        _learning = learning;
        Loaded += async (_, _) => await RefreshAsync();
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void OnDeleteClicked(object sender, RoutedEventArgs e)
    {
        if (LearningRecordsGrid.SelectedItem is not LearningReviewItem item)
        {
            ReviewStatusText.Text = "Select a record to delete.";
            return;
        }

        if (System.Windows.MessageBox.Show(this, $"Delete this {item.Kind.ToLowerInvariant()} record?", "Delete learning record",
                System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning) != System.Windows.MessageBoxResult.Yes)
            return;

        try
        {
            await _learning.DeleteLearningReviewItemAsync(item);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            ReviewStatusText.Text = $"Could not delete the record: {ex.Message}";
        }
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

    private async Task RefreshAsync()
    {
        try
        {
            LearningRecordsGrid.ItemsSource = await _learning.GetLearningReviewItemsAsync();
            ReviewStatusText.Text = $"Showing {LearningRecordsGrid.Items.Count} most recent records.";
        }
        catch (Exception ex)
        {
            ReviewStatusText.Text = $"Could not load learning records: {ex.Message}";
        }
    }
}
