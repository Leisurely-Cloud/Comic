using System;
using Microsoft.UI.Xaml.Controls;

namespace Comic_WinUI.Services;

public sealed class NavigationService
{
    private Frame? _frame;

    public void SetFrame(Frame frame)
    {
        _frame = frame;
    }

    public bool Navigate(Type pageType, object? parameter = null)
    {
        if (_frame is null)
        {
            return false;
        }

        return _frame.Navigate(pageType, parameter);
    }

    public bool CanGoBack => _frame?.CanGoBack ?? false;

    public void GoBack()
    {
        if (_frame?.CanGoBack == true)
        {
            _frame.GoBack();
        }
    }
}
