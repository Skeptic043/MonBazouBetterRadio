using BepInEx.Configuration;

namespace RadioEnhancements
{
    // A global preference only. Playback position and history never enter game saves.
    internal sealed class ShufflePreference
    {
        private readonly ConfigFile config;
        private readonly ConfigEntry<bool> entry;
        internal ShufflePreference(ConfigFile config)
        {
            this.config = config;
            entry = config.Bind("Music", "Shuffle", false, "Remember shuffle across save changes and game restarts. Preset 6 updates this setting while playing CDs. False plays songs in order.");
        }
        internal bool Value { get { return entry.Value; } }
        internal void Remember(bool value)
        {
            entry.Value = value;
            // Also persist when another configuration tool has disabled SaveOnConfigSet.
            config.Save();
        }
    }
}
