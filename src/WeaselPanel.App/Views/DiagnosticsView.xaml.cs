using System.Windows.Controls;
using WeaselPanel.App.ViewModels;

namespace WeaselPanel.App.Views;

public partial class DiagnosticsView : UserControl
{
    public DiagnosticsView()
    {
        InitializeComponent();
    }

    public DiagnosticsView(DiagnosticsViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }
}
