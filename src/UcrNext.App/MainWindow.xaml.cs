using System.ComponentModel;
using System.Windows;

namespace UcrNext.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _vm.IsMapping = false;
        _vm.Flush();
        base.OnClosing(e);
    }

    private void AddProfile_Click(object sender, RoutedEventArgs e) => _vm.AddProfile();

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Selected is not { } p) return;
        if (MessageBox.Show(this, $"Delete profile \"{p.Name}\"?", "UCR-Next", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            _vm.DeleteProfile(p);
    }

    private void AddBinding_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Selected is not { } p) return;
        var b = p.AddBinding();
        BindInput(b);
    }

    private void DeleteBinding_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BindingVm b) b.Owner.RemoveBinding(b);
    }

    private void BindInput_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BindingVm b) BindInput(b);
    }

    private void BindInput(BindingVm binding)
    {
        var dlg = new BindDialog(_vm.Services.Inputs) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.Captured is { } input) binding.SetInput(input);
    }
}
