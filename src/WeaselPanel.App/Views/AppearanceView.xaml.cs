using System.Windows.Controls;
using WeaselPanel.App.ViewModels;

namespace WeaselPanel.App.Views;

public partial class AppearanceView : UserControl
{
    public AppearanceView()
    {
        InitializeComponent();
    }

    public AppearanceView(AppearanceViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }
}
