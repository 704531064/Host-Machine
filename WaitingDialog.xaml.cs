using System.Windows;

namespace ModbusDataReceiver
{
    public partial class WaitingDialog : Window
    {
        public WaitingDialog()
        {
            InitializeComponent();
        }

        public void SetStatus(string status)
        {
            StatusText.Text = status;
        }
    }
}