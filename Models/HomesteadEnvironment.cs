namespace HomesteadLauncher.Models
{
    /// <summary>
    /// Describes the Homestead environment the launcher drives.
    /// </summary>
    public class HomesteadEnvironment
    {
        public string Name { get; set; } = "Laravel Homestead";

        /// <summary>
        /// Directory that contains the Homestead Vagrantfile.
        /// </summary>
        public string Directory { get; set; } = "";
    }
}
