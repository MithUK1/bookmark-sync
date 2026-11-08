using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace BookmarkSync
{
    /// <summary>One bookmark as a browser reports it.</summary>
    public sealed class Bm
    {
        public string Url = "";
        public string Title = "";
        public string[] Path = new string[0];
        public int Index;

        /// <summary>The folder it is in plus its normalised address.</summary>
        public string Key { get { return string.Join(Env.PathSep.ToString(), Path) + Env.KeySep + Rules.NormalUrl(Url); } }

        public string FolderKey { get { return string.Join(Env.PathSep.ToString(), Path); } }

        public Dictionary<string, object> ToJson()
        {
            var d = new Dictionary<string, object>();
            d["url"] = Url;
            d["title"] = Title;
            d["path"] = new List<object>(Path);
            d["index"] = Index;
            return d;
        }

        public static Bm FromJson(object value)
        {
            var o = Json.Obj(value);
            if (o == null) return null;
            string url = Json.Get(o, "url") as string;
            if (url == null) return null;
            List<string> path = Json.StrList(Json.Get(o, "path"));
            if (path.Count == 0) return null;
            return new Bm { Url = url, Title = Json.Str(Json.Get(o, "title")), Index = Json.Int(Json.Get(o, "index")), Path = path.ToArray() };
        }
    }

    /// <summary>The address and location rules. The address rules must match normUrl in the extension.</summary>
    public static class Rules
    {
        private static readonly Regex UrlPattern = new Regex(@"^(https?)://([^/?#]*)([^?#]*)(\?[^#]*)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex Tracking = new Regex(@"^(utm_|fbclid$|gclid$|mc_eid$)", RegexOptions.CultureInvariant);

        /// <summary>
        /// Ignores http/https, host case, www., default ports, trailing slashes, tracking
        /// parameters, the #fragment and any login in the address.
        /// </summary>
        public static string NormalUrl(string url)
        {
            string text = (url ?? "").Trim();
            Match m = UrlPattern.Match(text);
            if (!m.Success) return text;

            string host = m.Groups[2].Value.ToLowerInvariant();
            host = host.Substring(host.LastIndexOf('@') + 1);
            if (host.StartsWith("www.")) host = host.Substring(4);
            host = Regex.Replace(host, ":(80|443)$", "");

            string path = m.Groups[3].Value.TrimEnd('/');

            string query = "";
            if (m.Groups[4].Success)
            {
                var kept = new List<string>();
                foreach (string pair in m.Groups[4].Value.Substring(1).Split('&'))
                {
                    if (pair == "") continue;
                    string name = pair.Split('=')[0].ToLowerInvariant();
                    if (!Tracking.IsMatch(name)) kept.Add(pair);
                }
                if (kept.Count > 0) query = "?" + string.Join("&", kept);
            }
            return "//" + host + path + query;
        }

        public static string BookmarkToken(string url)
        {
            return "b|" + NormalUrl(url);
        }

        public static string FolderToken(string name)
        {
            return "f|" + name;
        }

        /// <summary>Lower is better: bar, bar folder, other, other folder.</summary>
        public static int LocationRank(Bm bm)
        {
            int depth = bm.Path.Length;
            if (bm.Path[0] == "toolbar") return depth == 1 ? 0 : 1;
            return depth == 1 ? 2 : 3;
        }
    }
}
