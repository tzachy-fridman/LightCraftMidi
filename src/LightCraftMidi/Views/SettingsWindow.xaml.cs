using System.Windows;
using LightCraftMidi.ViewModels;

namespace LightCraftMidi.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
