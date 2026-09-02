using System.Windows;
using System.Windows.Controls;
using WeaselPanel.App.ViewModels;

namespace WeaselPanel.App.Views;

public partial class BackupView : UserControl
{
    public BackupView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 传入 ViewModel 构造。与 MainWindow 的约定一致：
    /// 视图在窗口 ctor 里一次性创建并复用，避免切页时重复扫描磁盘。
    /// </summary>
    public BackupView(BackupViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }
}
