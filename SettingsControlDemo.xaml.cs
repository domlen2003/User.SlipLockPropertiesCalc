using System.Windows.Controls;

namespace DDFRacerPlugin
{
    public partial class SettingsControl : UserControl
    {
        public DDFRacerPlugin Plugin { get; }

        public SettingsControl()
        {
            InitializeComponent();
        }

        public SettingsControl(DDFRacerPlugin plugin) : this()
        {
            this.Plugin = plugin;
            this.DataContext = plugin;
        }
    }
}