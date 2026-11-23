using System;
using System.Collections.Generic;
using System.IO;

namespace BookmarkSync
{
    /// <summary>The settings in config.json, over defaults. A broken file never stops start-up.</summary>
    public sealed class Config
    {
        public bool RemoveDuplicates = false;
        /// <summary>When duplicates are kept, the browser asks before a new duplicate stays.</summary>
        public bool AskAboutDuplicates = true;
        public int AutoBackupsToKeep = 40;
        public int DailiesToKeep = 7;
        public int WeekliesToKeep = 4;
        public int MonthlyToKeep = 12;
        public int YearliesToKeep = 5;
        /// <summary>off, auto, chrome, edge or firefox.</summary>
        public string MasterBrowser = "off";
        public string BrowserPriority = "firefox,chrome,edge";
        public bool StartWithWindows = true;
        /// <summary>On: the close button hides the window to the tray and there is no minimise button. Off: both buttons show and close exits.</summary>
        public bool MinimiseToTray = true;
        /// <summary>Where backups are kept (a local folder, a cloud-synced folder or a network share). Empty means the app folder.</summary>
        public string BackupFolder = "";
        /// <summary>Extra Chrome or Edge extension ids the helper accepts (for example the id the Chrome Web Store assigns), comma separated.</summary>
        public string ExtraExtensionIds = "";
        public int WindowLeft = -1;
        public int WindowTop = -1;

        /// <summary>Set when the file could not be read, so the window can say so.</summary>
        public string Problem;

        public Config Copy()
        {
            return (Config)MemberwiseClone();
        }

        public static Config Load()
        {
            var config = new Config();
            if (!File.Exists(Env.ConfigFile)) return config;
            try
            {
                var root = Json.Obj(Json.Parse(Env.ReadText(Env.ConfigFile)));
                if (root == null) throw new FormatException("The file does not hold a settings object.");
                config.RemoveDuplicates = Json.Bool(Json.Get(root, "RemoveDuplicates"), config.RemoveDuplicates);
                config.AskAboutDuplicates = Json.Bool(Json.Get(root, "AskAboutDuplicates"), config.AskAboutDuplicates);
                config.AutoBackupsToKeep = Json.Int(Json.Get(root, "AutoBackupsToKeep"), config.AutoBackupsToKeep);
                config.DailiesToKeep = Json.Int(Json.Get(root, "DailiesToKeep"), config.DailiesToKeep);
                config.WeekliesToKeep = Json.Int(Json.Get(root, "WeekliesToKeep"), config.WeekliesToKeep);
                config.MonthlyToKeep = Json.Int(Json.Get(root, "MonthlyToKeep"), config.MonthlyToKeep);
                config.YearliesToKeep = Json.Int(Json.Get(root, "YearliesToKeep"), config.YearliesToKeep);
                config.MasterBrowser = Json.Str(Json.Get(root, "MasterBrowser"), config.MasterBrowser).Trim().ToLowerInvariant();
                config.BrowserPriority = Json.Str(Json.Get(root, "BrowserPriority"), config.BrowserPriority).Trim().ToLowerInvariant();
                config.StartWithWindows = Json.Bool(Json.Get(root, "StartWithWindows"), config.StartWithWindows);
                config.MinimiseToTray = Json.Bool(Json.Get(root, "MinimiseToTray"), config.MinimiseToTray);
                config.BackupFolder = Json.Str(Json.Get(root, "BackupFolder"), config.BackupFolder).Trim();
                config.ExtraExtensionIds = Json.Str(Json.Get(root, "ExtraExtensionIds"), config.ExtraExtensionIds).Trim().ToLowerInvariant();
                config.WindowLeft = Json.Int(Json.Get(root, "WindowLeft"), config.WindowLeft);
                config.WindowTop = Json.Int(Json.Get(root, "WindowTop"), config.WindowTop);
            }
            catch (Exception ex)
            {
                config = new Config();
                config.Problem = "The settings file could not be read, so the defaults are in use. " + ex.Message;
            }
            config.Clamp();
            return config;
        }

        /// <summary>A Chrome extension id is 32 letters from a to p.</summary>
        public static bool IsExtensionId(string id)
        {
            if (id == null || id.Length != 32) return false;
            foreach (char c in id) if (c < 'a' || c > 'p') return false;
            return true;
        }

        /// <summary>The valid ids in ExtraExtensionIds.</summary>
        public List<string> ExtraIds()
        {
            var list = new List<string>();
            foreach (string part in (ExtraExtensionIds ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string id = part.Trim().ToLowerInvariant();
                if (IsExtensionId(id) && id != Env.ChromiumExtensionId && !list.Contains(id)) list.Add(id);
            }
            return list;
        }

        private void Clamp()
        {
            AutoBackupsToKeep = Math.Max(5, Math.Min(500, AutoBackupsToKeep));
            DailiesToKeep = Math.Max(1, Math.Min(365, DailiesToKeep));
            WeekliesToKeep = Math.Max(1, Math.Min(260, WeekliesToKeep));
            MonthlyToKeep = Math.Max(1, Math.Min(600, MonthlyToKeep));
            YearliesToKeep = Math.Max(1, Math.Min(100, YearliesToKeep));
            if (Array.IndexOf(new[] { "off", "auto", "chrome", "edge", "firefox" }, MasterBrowser) < 0) MasterBrowser = "off";
        }

        public void Save()
        {
            Clamp();
            var d = new Dictionary<string, object>();
            d["RemoveDuplicates"] = RemoveDuplicates;
            d["AskAboutDuplicates"] = AskAboutDuplicates;
            d["AutoBackupsToKeep"] = AutoBackupsToKeep;
            d["DailiesToKeep"] = DailiesToKeep;
            d["WeekliesToKeep"] = WeekliesToKeep;
            d["MonthlyToKeep"] = MonthlyToKeep;
            d["YearliesToKeep"] = YearliesToKeep;
            d["MasterBrowser"] = MasterBrowser;
            d["BrowserPriority"] = BrowserPriority;
            d["StartWithWindows"] = StartWithWindows;
            d["MinimiseToTray"] = MinimiseToTray;
            d["BackupFolder"] = BackupFolder;
            d["ExtraExtensionIds"] = ExtraExtensionIds;
            d["WindowLeft"] = WindowLeft;
            d["WindowTop"] = WindowTop;
            Env.WriteFileSafely(Env.ConfigFile, Json.Write(d));
        }
    }
}
