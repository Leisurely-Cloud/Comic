using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Comic.WinUI.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Comic.WinUI.ViewModels;

public partial class LibraryPageViewModel
{
    public ObservableCollection<LocalBookshelf> Shelves { get; } = [];

    [ObservableProperty]
    public partial LocalBookshelf? SelectedShelf { get; set; }

    [ObservableProperty]
    public partial string TagFilter { get; set; } = string.Empty;

    public string SelectedOrganizationText => SelectedItem?.OrganizationText ?? string.Empty;

    public void RefreshShelves()
    {
        var selectedId = SelectedShelf?.Id;
        Shelves.Clear();
        Shelves.Add(new("", "全部书架"));
        foreach (var shelf in Organization.GetShelves()) Shelves.Add(shelf);
        SelectedShelf = Shelves.FirstOrDefault(shelf => shelf.Id == selectedId) ?? Shelves[0];
    }

    public string DescribeOrganization(string root)
    {
        var book = Organization.Get(root);
        var shelfNames = Organization.GetShelves().Where(shelf => book.ShelfIds.Contains(shelf.Id)).Select(shelf => shelf.Name);
        return string.Join(" · ", shelfNames.Concat(book.Tags.Select(tag => "#" + tag)));
    }

    public void OrganizeBooks(IEnumerable<string> roots, IEnumerable<string> shelves, IEnumerable<string> tags) => Organization.SetBooks(roots, shelves, tags);

    partial void OnSelectedShelfChanged(LocalBookshelf? value) => _currentPage = 1;
}
