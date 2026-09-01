using System.Windows;
using System.Windows.Controls;
using WeaselPanel.App.ViewModels;

namespace WeaselPanel.App.Views;

public partial class SchemaView : UserControl
{
    private bool _loaded;

    public SchemaView()
    {
        InitializeComponent();
    }

    public SchemaView(SchemaViewModel viewModel) : this()
    {
        DataContext = viewModel;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;
        if (DataContext is SchemaViewModel vm && !vm.HasLoaded) vm.Load();
    }

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is SchemaViewModel vm) vm.Load();
    }
}