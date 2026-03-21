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
    }
}