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
            d["WindowLeft"] = WindowLeft;
            d["WindowTop"] = WindowTop;
            Env.WriteFileSafely(Env.ConfigFile, Json.Write(d));
        }
    }
}
