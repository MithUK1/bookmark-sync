using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace BookmarkSync
{
    /// <summary>
    /// The browser side of the program. A browser starts the program with its extension's request
    /// stream (native messaging: a 4-byte little-endian length, then that many bytes of UTF-8 JSON, in
    /// both directions). The connection stays open while the browser runs, so a change made in one
    /// browser can be pushed to the others at once.
    /// </summary>
    public static class Host
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        // ---------------------------------------------------------------- framing

        public static byte[] ReadExactly(Stream stream, int count)
        {
            var buffer = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = stream.Read(buffer, offset, count - offset);
                if (read <= 0) return null;
                offset += read;
            }
            return buffer;
        }

        /// <summary>Reads one message and returns its JSON text, or null when the stream has ended.</summary>
        public static string ReadMessage(Stream stream)
        {
            byte[] header = ReadExactly(stream, 4);
            if (header == null) return null;
            int length = BitConverter.ToInt32(header, 0);
            if (length <= 0) return null;
            byte[] body = ReadExactly(stream, length);
            return body == null ? null : Utf8.GetString(body);
        }

        public static void WriteMessage(Stream stream, object reply)
        {
            byte[] bytes = Utf8.GetBytes(Json.Write(reply));
            stream.Write(BitConverter.GetBytes(bytes.Length), 0, 4);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }

        /// <summary>Splits text into base64 pieces small enough for one reply each.</summary>
        public static List<string> SplitParts(string text, int partBytes)
        {
            byte[] bytes = Utf8.GetBytes(text);
            var parts = new List<string>();
            for (int offset = 0; offset < bytes.Length; offset += partBytes)
            {
                parts.Add(Convert.ToBase64String(bytes, offset, Math.Min(partBytes, bytes.Length - offset)));
            }
            if (parts.Count == 0) parts.Add("");
            return parts;
        }

        private static string PlanFile(string browser)
        {
            return Path.Combine(Env.PlanDir, browser + ".json");
        }

        // --------------------------------------------------------------- requests

        /// <summary>
        /// Answers one request. Types: status, check (is this browser up to date?), sync (merge, then
        /// reply with the master list in pieces, or unchanged), part (a further piece), ack (the
        /// browser applied the list, record its bookmarks) and open (show the window).
        /// </summary>
        /// <summary>Tells the extension whether to ask before a new duplicate bookmark stays.</summary>
        private static Dictionary<string, object> WithAsk(Dictionary<string, object> reply)
        {
            Config config = Config.Load();
            reply["askDuplicates"] = config.AskAboutDuplicates && !config.RemoveDuplicates;
            return reply;
        }

        public static Dictionary<string, object> Handle(Dictionary<string, object> request)
        {
            string type = Json.Str(Json.Get(request, "type"));
            if (type == "status") return Reply(new Dictionary<string, object> { { "version", Env.AppVersion } });
            if (type == "open")
            {
                OpenWindow();
                return Reply();
            }

            string browser = Json.Str(Json.Get(request, "browser"));
            if (!Env.IsBrowser(browser)) throw new InvalidOperationException("Unknown browser '" + browser + "'.");

            if (type == "hello")
            {
                var hello = WithAsk(Reply());
                // An older extension copy (Chrome or Edge, loaded from our folder) is told to reload
                // so it picks up the files the upgrade wrote. Old copies that send no version are left alone.
                string theirs = Json.Str(Json.Get(request, "version"));
                if (!string.IsNullOrEmpty(theirs) && theirs != Env.AppVersion && browser != "firefox") hello["reload"] = true;
                return hello;
            }

            if (type == "check")
            {
                return WithAsk(Reply(new Dictionary<string, object> { { "current", Status.Load().IsCurrent(browser) } }));
            }

            if (type == "part")
            {
                List<string> parts = SplitParts(Env.ReadText(PlanFile(browser)), Env.PartBytes);
                int index = Json.Int(Json.Get(request, "part"));
                if (index < 0 || index >= parts.Count) throw new InvalidOperationException("That part of the bookmark list does not exist, sync again.");
                return Reply(new Dictionary<string, object> { { "part", index }, { "parts", parts.Count }, { "data", parts[index] } });
            }

            if (type == "sync")
            {
                Config config = Config.Load();
                Snap snap = Snap.FromJson(request);
                return Env.WithStateLock(() =>
                {
                    State state = State.Load();
                    SyncResult result = Engine.Sync(state, browser, snap, config);
                    state.Save();
                    var reply = new Dictionary<string, object>
                    {
                        { "unchanged", result.Unchanged }, { "mirror", result.Mirror }, { "revision", state.Revision }
                    };
                    if (!result.Unchanged)
                    {
                        string text = Json.Write(state.Master.ToJson());
                        Env.WriteFileSafely(PlanFile(browser), text);
                        List<string> parts = SplitParts(text, Env.PartBytes);
                        reply["part"] = 0;
                        reply["parts"] = parts.Count;
                        reply["data"] = parts[0];
                    }
                    return Reply(reply);
                });
            }

            if (type == "ack")
            {
                Snap snap = Snap.FromJson(request);
                var stats = Json.Get(request, "stats");
                int revision = Json.Int(Json.Get(request, "revision"));
                Env.WithStateLock(() =>
                {
                    State state = State.Load();
                    Engine.Complete(state, browser, snap, revision,
                        Json.Int(Json.Get(stats, "created")), Json.Int(Json.Get(stats, "deleted")),
                        Json.Int(Json.Get(stats, "moved")), Json.Int(Json.Get(stats, "errors")));
                    state.Save();
                });
                try { File.Delete(PlanFile(browser)); } catch (IOException) { }
                return Reply();
            }

            throw new InvalidOperationException("Unknown request '" + type + "'.");
        }

        private static Dictionary<string, object> Reply(Dictionary<string, object> fields = null)
        {
            var reply = new Dictionary<string, object>();
            reply["ok"] = true;
            if (fields != null) foreach (var pair in fields) reply[pair.Key] = pair.Value;
            return reply;
        }

        /// <summary>Opens the Bookmark Sync window, through the installed copy if there is one.</summary>
        private static void OpenWindow()
        {
            string exe = File.Exists(Env.InstalledExe) ? Env.InstalledExe : System.Reflection.Assembly.GetExecutingAssembly().Location;
            Process.Start(new ProcessStartInfo(exe, "--show") { UseShellExecute = false });
        }

        // ------------------------------------------------------------------- loop

        /// <summary>
        /// Runs until the browser closes the stream. After a hello the connection also watches for
        /// other browsers' changes and tells this one when it is no longer up to date.
        /// </summary>
        public static void Run()
        {
            Stream input = Console.OpenStandardInput();
            Stream output = Console.OpenStandardOutput();
            var writeLock = new object();
            FileSystemWatcher watcher = null;
            Timer debounce = null;
            string browser = null;
            DateTime lastPush = DateTime.MinValue;

            Action<object> send = reply =>
            {
                lock (writeLock) { WriteMessage(output, reply); }
            };

            try
            {
                while (true)
                {
                    string text = ReadMessage(input);
                    if (text == null) break;
                    object id = null;
                    Dictionary<string, object> reply;
                    try
                    {
                        var request = Json.Obj(Json.Parse(text));
                        id = Json.Get(request, "id");
                        reply = Handle(request);
                        if (Json.Str(Json.Get(request, "type")) == "hello" && watcher == null)
                        {
                            browser = Json.Str(Json.Get(request, "browser"));
                            string watched = browser;
                            debounce = new Timer(_ =>
                            {
                                try
                                {
                                    if (Status.Load().IsCurrent(watched)) return;
                                    if ((DateTime.UtcNow - lastPush).TotalSeconds < 2) return;
                                    lastPush = DateTime.UtcNow;
                                    send(new Dictionary<string, object> { { "ok", true }, { "type", "changed" } });
                                }
                                catch (Exception ex)
                                {
                                    Env.Log("Push failed: " + ex.Message);
                                }
                            }, null, Timeout.Infinite, Timeout.Infinite);
                            Directory.CreateDirectory(Env.DataDir);
                            watcher = new FileSystemWatcher(Env.DataDir, "status.json");
                            watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size;
                            FileSystemEventHandler onChange = (s, e) => debounce.Change(500, Timeout.Infinite);
                            watcher.Changed += onChange;
                            watcher.Created += onChange;
                            watcher.Renamed += (s, e) => debounce.Change(500, Timeout.Infinite);
                            watcher.EnableRaisingEvents = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        Env.Log("Request failed: " + ex.Message);
                        reply = new Dictionary<string, object> { { "ok", false }, { "error", ex.Message } };
                    }
                    if (id != null) reply["id"] = id;
                    send(reply);
                }
            }
            finally
            {
                if (watcher != null) watcher.Dispose();
                if (debounce != null) debounce.Dispose();
            }
        }
    }
}
