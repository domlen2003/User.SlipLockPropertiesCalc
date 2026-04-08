using System.Windows;
using System.Windows.Controls;

namespace User.SlipLockPropertiesCalc
{
    public partial class SettingsControl : UserControl
    {
        public SlipLockPropertiesCalc Plugin { get; }

        public SettingsControl()
        {
            InitializeComponent();
        }

        public SettingsControl(SlipLockPropertiesCalc plugin) : this()
        {
            this.Plugin = plugin;
            this.DataContext = plugin;
        }

        private void RetestButton_Click(object sender, RoutedEventArgs e)
        {
            Plugin.RequestRetest();
        }

        private void GenerateProfileButton_Click(object sender, RoutedEventArgs e)
        {
            Plugin.GenerateShakeITProfile();
        }

        private void GenerateHapticButton_Click(object sender, RoutedEventArgs e)
        {
            Plugin.GenerateHapticPedalProfile();
        }
    }
}
