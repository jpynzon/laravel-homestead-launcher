using System.Collections.Generic;

namespace HomesteadLauncher.Models
{
    public enum HomesteadState
    {
        Unknown,
        NotCreated,
        Running,
        PoweredOff,
        Saved,
        Aborted
    }

    /// <summary>
    /// The parsed result of "vagrant status --machine-readable".
    /// </summary>
    public class HomesteadStatus
    {
        public HomesteadState State { get; set; } = HomesteadState.Unknown;
        public string MachineName { get; set; } = "";
        public string Provider { get; set; } = "";

        public bool IsRunning => State == HomesteadState.Running;

        /// <summary>
        /// Parses machine-readable status lines of the form
        /// "timestamp,target,type,data".
        /// </summary>
        public static HomesteadStatus Parse(IEnumerable<string> lines)
        {
            var status = new HomesteadStatus();

            foreach (string line in lines)
            {
                if (string.IsNullOrEmpty(line)) continue;

                string[] parts = line.Split(',');
                if (parts.Length < 4) continue;

                string target = parts[1];
                string type = parts[2];
                string data = string.Join(",", parts, 3, parts.Length - 3).Trim();

                if (type == "state")
                {
                    status.State = MapState(data);
                    if (!string.IsNullOrWhiteSpace(target)) status.MachineName = target;
                }
                else if (type == "provider-name")
                {
                    status.Provider = data;
                }
            }

            return status;
        }

        private static HomesteadState MapState(string value)
        {
            switch (value)
            {
                case "running": return HomesteadState.Running;
                case "poweroff": return HomesteadState.PoweredOff;
                case "saved": return HomesteadState.Saved;
                case "aborted": return HomesteadState.Aborted;
                case "not_created": return HomesteadState.NotCreated;
                default: return HomesteadState.Unknown;
            }
        }
    }
}
