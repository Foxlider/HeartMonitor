using System;
using System.ComponentModel;
using System.Windows.Input;

namespace HeartMonitorWPF
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow
    {


        public MainWindow()
        {
            InitializeComponent();
        }

        private void MetroWindow_Closing(object sender, CancelEventArgs e)
        {
            if(DataContext is SpeedTestVm context)
            {
                context.StopCommand.Execute(null);
            }
        }
    }

    public class RelayCommand : ICommand
    {
        private Action _action;

        public RelayCommand(Action action)
        {
            _action = action;
        }

        public bool CanExecute(object parameter)
        {
            return true;
        }

        public void Execute(object parameter)
        {
            _action();
        }

        public event EventHandler CanExecuteChanged;
    }
}
