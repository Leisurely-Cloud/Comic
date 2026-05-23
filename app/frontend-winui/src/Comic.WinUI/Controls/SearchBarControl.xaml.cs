using Comic.WinUI.Models;
using Comic.WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Comic.WinUI.Controls;

public sealed partial class SearchBarControl : UserControl
{
    private bool _suppressNextSearch;

    public static readonly DependencyProperty ViewModelProperty =
        DependencyProperty.Register(nameof(ViewModel), typeof(DownloadPageViewModel), typeof(SearchBarControl), new PropertyMetadata(null, OnViewModelChanged));

    public DownloadPageViewModel ViewModel
    {
        get => (DownloadPageViewModel)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public SearchBarControl()
    {
        InitializeComponent();
    }

    private static void OnViewModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((SearchBarControl)d).Bindings.Update();
    }

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        if (!sender.FocusState.HasFlag(FocusState.Programmatic) && sender.FocusState == FocusState.Unfocused) return;

        ViewModel.SearchKeyword = sender.Text;

        if (string.IsNullOrWhiteSpace(sender.Text))
        {
            ViewModel.LoadSearchHistory();
        }
        else
        {
            ViewModel.FilterSearchHistory(sender.Text);
        }
    }

    private void OnSuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is SearchHistoryEntry entry)
        {
            _suppressNextSearch = true;
            sender.Text = entry.Keyword;
            ViewModel.SearchKeyword = entry.Keyword;
            ViewModel.SelectedSite = entry.SiteName;
        }
    }

    private void OnQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (_suppressNextSearch)
        {
            _suppressNextSearch = false;
            _ = ViewModel?.SearchCommand.ExecuteAsync(null);
            return;
        }

        if (args.ChosenSuggestion is SearchHistoryEntry entry)
        {
            ViewModel.SearchKeyword = entry.Keyword;
            ViewModel.SelectedSite = entry.SiteName;
            _ = ViewModel?.SearchCommand.ExecuteAsync(null);
        }
        else if (!string.IsNullOrWhiteSpace(args.QueryText))
        {
            ViewModel.SearchKeyword = args.QueryText;
            _ = ViewModel?.SearchCommand.ExecuteAsync(null);
        }
    }

    private void OnSearchBoxGotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is AutoSuggestBox box && string.IsNullOrWhiteSpace(box.Text))
        {
            ViewModel.LoadSearchHistory();
            if (ViewModel.HasSearchHistory)
            {
                box.IsSuggestionListOpen = true;
            }
        }
    }

    private void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            if (sender is AutoSuggestBox box)
            {
                box.Text = string.Empty;
                ViewModel.SearchKeyword = string.Empty;
                box.Focus(FocusState.Programmatic);
            }
            e.Handled = true;
        }
    }
}
