using System.Collections.ObjectModel;
using IoFtp.Core.Models;
using IoFtp.Mobile.Services;
using ConnectionProfile = IoFtp.Core.Models.ConnectionProfile;

namespace IoFtp.Mobile;

public sealed class SiteManagerPage : ContentPage
{
    private readonly SiteStore _store;
    private readonly ObservableCollection<ConnectionProfile> _sites = [];
    private readonly CollectionView _list;

    public SiteManagerPage(SiteStore store)
    {
        _store = store;
        Title = "Site Manager";
        _list = new CollectionView
        {
            ItemsSource = _sites,
            SelectionMode = SelectionMode.Single,
            ItemTemplate = new DataTemplate(() =>
            {
                var name = new Label { FontAttributes = FontAttributes.Bold };
                name.SetBinding(Label.TextProperty, nameof(ConnectionProfile.Name));
                var address = new Label { FontSize = 12, TextColor = Colors.Gray };
                address.SetBinding(Label.TextProperty, nameof(ConnectionProfile.Host));
                return new VerticalStackLayout { Padding = 12, Children = { name, address } };
            })
        };
        var add = new Button { Text = "Ny site" };
        var edit = new Button { Text = "Redigera" };
        var delete = new Button { Text = "Ta bort" };
        add.Clicked += async (_, _) => await EditAsync(null);
        edit.Clicked += async (_, _) => await EditAsync(_list.SelectedItem as ConnectionProfile);
        delete.Clicked += OnDelete;
        var buttons = new HorizontalStackLayout
        {
            Spacing = 8,
            HorizontalOptions = LayoutOptions.Center,
            Children = { add, edit, delete }
        };
        Grid.SetRow(buttons, 1);
        Content = new Grid
        {
            Padding = 14,
            RowDefinitions = { new RowDefinition(GridLength.Star), new RowDefinition(GridLength.Auto) },
            Children = { _list, buttons }
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        _sites.Clear();
        foreach (var site in await _store.LoadAsync()) _sites.Add(site);
    }

    private async Task EditAsync(ConnectionProfile? profile)
    {
        var editor = new SiteEditorPage(profile);
        editor.Saved += async saved =>
        {
            var all = (await _store.LoadAsync()).Where(x => x.Id != saved.Id).Append(saved);
            await _store.SaveAsync(all);
            await Navigation.PopModalAsync();
            await ReloadAsync();
        };
        await Navigation.PushModalAsync(new NavigationPage(editor));
    }

    private async void OnDelete(object? sender, EventArgs e)
    {
        if (_list.SelectedItem is not ConnectionProfile profile) return;
        if (!await DisplayAlert("Ta bort site", $"Ta bort {profile.Name}?", "Ta bort", "Avbryt")) return;
        await _store.DeleteAsync(profile);
        await ReloadAsync();
    }
}
