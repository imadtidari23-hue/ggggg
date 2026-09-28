// ============================================================================
//  TikArena · DarkRing Studio
//  GTA V Story Mode <-> TikTok LIVE (TikFinity) interactive challenge engine.
//  ScriptHookVDotNet v3 · C# 5 only · every setting is read from TikArena.ini
//  Source: src/TikArena.3.cs  (build.py replaces Hash.<NAME> with numeric hashes)
// ============================================================================
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Media;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using GTA;
using GTA.Math;
using GTA.Native;
using GTA.UI;
using Font = GTA.UI.Font;
using Screen = GTA.UI.Screen;

namespace TikArena
{
    // ------------------------------------------------------------------------
    //  Small helpers
    // ------------------------------------------------------------------------
    static class U
    {
        public static readonly CultureInfo IC = CultureInfo.InvariantCulture;
        public static readonly Random Rng = new Random();
        public static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
        public static string Dir = "";
        public static string DataDir = "";
        public static bool DebugLog = false;
        public static int ShownErrors = 0;

        public static long Now { get { return Clock.ElapsedMilliseconds; } }

        public static uint Joaat(string s)
        {
            if (s == null) return 0;
            s = s.ToLowerInvariant();
            uint h = 0;
            for (int i = 0; i < s.Length; i++)
            {
                h += (byte)s[i];
                h += (h << 10);
                h ^= (h >> 6);
            }
            h += (h << 3);
            h ^= (h >> 11);
            h += (h << 15);
            return h;
        }

        public static int HashInt(string s) { return unchecked((int)Joaat(s)); }

        public static void Log(string msg)
        {
            if (!DebugLog) return;
            try { File.AppendAllText(Path.Combine(DataDir, "log.txt"), DateTime.Now.ToString("HH:mm:ss") + "  " + msg + "\r\n", Encoding.UTF8); }
            catch { }
        }

        public static void Error(string where, Exception ex)
        {
            try
            {
                File.AppendAllText(Path.Combine(DataDir, "errors.txt"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  [" + where + "] " + ex.ToString() + "\r\n\r\n", Encoding.UTF8);
            }
            catch { }
            if (ShownErrors < 3)
            {
                ShownErrors++;
                try { Notification.Show("~r~TikArena error~s~ [" + where + "]~n~" + ex.Message); } catch { }
            }
        }

        public static float Clamp(float v, float a, float b) { return v < a ? a : (v > b ? b : v); }
        public static int Clamp(int v, int a, int b) { return v < a ? a : (v > b ? b : v); }

        public static Color WithAlpha(Color c, int a) { return Color.FromArgb(Clamp(a, 0, 255), c.R, c.G, c.B); }

        public static Color Mix(Color a, Color b, float t)
        {
            return Color.FromArgb(
                (int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        public static Color ParseColor(string s, Color def)
        {
            if (string.IsNullOrEmpty(s)) return def;
            s = s.Trim().TrimStart('#');
            try
            {
                if (s.Length == 6) return Color.FromArgb(255, Convert.ToInt32(s.Substring(0, 2), 16), Convert.ToInt32(s.Substring(2, 2), 16), Convert.ToInt32(s.Substring(4, 2), 16));
                if (s.Length == 8) return Color.FromArgb(Convert.ToInt32(s.Substring(0, 2), 16), Convert.ToInt32(s.Substring(2, 2), 16), Convert.ToInt32(s.Substring(4, 2), 16), Convert.ToInt32(s.Substring(6, 2), 16));
            }
            catch { }
            return def;
        }

        public static string Fill(string tpl, string name, int count)
        {
            if (tpl == null) return "";
            return tpl.Replace("{name}", name ?? "").Replace("{count}", count.ToString(IC));
        }

        public static string Coins(long c)
        {
            if (c >= 1000000) return (c / 1000000.0).ToString("0.#", IC) + "M";
            if (c >= 10000) return (c / 1000.0).ToString("0.#", IC) + "K";
            return c.ToString(IC);
        }

        public static string SafeName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "user";
            StringBuilder sb = new StringBuilder();
            foreach (char ch in s)
            {
                if ((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch == '_' || ch == '-') sb.Append(ch);
                else sb.Append('_');
                if (sb.Length > 40) break;
            }
            return sb.ToString() + "_" + Joaat(s).ToString("x8");
        }

        public static T Pick<T>(IList<T> list)
        {
            if (list == null || list.Count == 0) return default(T);
            return list[Rng.Next(list.Count)];
        }

        public static float RandF(float a, float b) { return a + (float)Rng.NextDouble() * (b - a); }

        public static Keys ParseKey(string s)
        {
            if (string.IsNullOrEmpty(s)) return Keys.None;
            try { return (Keys)Enum.Parse(typeof(Keys), s.Trim(), true); }
            catch { return Keys.None; }
        }

        public static string Trunc(string s, int max)
        {
            if (s == null) return "";
            if (s.Length <= max) return s;
            return s.Substring(0, Math.Max(1, max - 1)) + "…";
        }
    }

    // ------------------------------------------------------------------------
    //  INI (UTF-8, hand written)
    // ------------------------------------------------------------------------
    class Ini
    {
        readonly Dictionary<string, Dictionary<string, string>> data =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        public static Ini Load(string path)
        {
            Ini ini = new Ini();
            if (path == null || !File.Exists(path)) return ini;
            string[] lines = File.ReadAllLines(path, Encoding.UTF8);
            Dictionary<string, string> cur = null;
            foreach (string raw in lines)
            {
                string line = raw.Trim().TrimStart('﻿');
                if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#")) continue;
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    string name = line.Substring(1, line.Length - 2).Trim();
                    if (!ini.data.TryGetValue(name, out cur))
                    {
                        cur = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        ini.data[name] = cur;
                    }
                    continue;
                }
                int eq = line.IndexOf('=');
                if (eq <= 0 || cur == null) continue;
                cur[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
            return ini;
        }

        public bool Has(string sec) { return data.ContainsKey(sec); }

        public string S(string sec, string key, string def)
        {
            Dictionary<string, string> d;
            string v;
            if (data.TryGetValue(sec, out d) && d.TryGetValue(key, out v)) return v;
            return def;
        }

        public int I(string sec, string key, int def)
        {
            string v = S(sec, key, null);
            int r;
            if (v != null && int.TryParse(v, NumberStyles.Integer, U.IC, out r)) return r;
            float f;
            if (v != null && float.TryParse(v, NumberStyles.Float, U.IC, out f)) return (int)f;
            return def;
        }

        public float F(string sec, string key, float def)
        {
            string v = S(sec, key, null);
            float r;
            if (v != null && float.TryParse(v.Replace(',', '.'), NumberStyles.Float, U.IC, out r)) return r;
            return def;
        }

        public bool B(string sec, string key, bool def)
        {
            string v = S(sec, key, null);
            if (v == null) return def;
            v = v.Trim().ToLowerInvariant();
            if (v == "true" || v == "1" || v == "yes" || v == "on") return true;
            if (v == "false" || v == "0" || v == "no" || v == "off") return false;
            return def;
        }

        public Color C(string sec, string key, string def)
        {
            return U.ParseColor(S(sec, key, def), U.ParseColor(def, Color.White));
        }

        public Keys K(string sec, string key, string def) { return U.ParseKey(S(sec, key, def)); }

        public List<string> L(string sec, string key, string def)
        {
            List<string> r = new List<string>();
            foreach (string p in S(sec, key, def).Split(','))
            {
                string t = p.Trim();
                if (t.Length > 0) r.Add(t);
            }
            return r;
        }
    }

    // ------------------------------------------------------------------------
    //  Tiny JSON parser (objects -> Dictionary, arrays -> List)
    // ------------------------------------------------------------------------
    static class Json
    {
        public static object Parse(string s)
        {
            int i = 0;
            try { return Val(s, ref i); }
            catch { return null; }
        }

        static void Ws(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

        static object Val(string s, ref int i)
        {
            Ws(s, ref i);
            if (i >= s.Length) return null;
            char c = s[i];
            if (c == '{')
            {
                Dictionary<string, object> d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                i++;
                while (true)
                {
                    Ws(s, ref i);
                    if (i < s.Length && s[i] == '}') { i++; break; }
                    string k = Str(s, ref i);
                    Ws(s, ref i);
                    i++; // ':'
                    object v = Val(s, ref i);
                    d[k] = v;
                    Ws(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == '}') { i++; break; }
                    break;
                }
                return d;
            }
            if (c == '[')
            {
                List<object> l = new List<object>();
                i++;
                while (true)
                {
                    Ws(s, ref i);
                    if (i < s.Length && s[i] == ']') { i++; break; }
                    l.Add(Val(s, ref i));
                    Ws(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == ']') { i++; break; }
                    break;
                }
                return l;
            }
            if (c == '"') return Str(s, ref i);
            if (s.Length - i >= 4 && s.Substring(i, 4) == "true") { i += 4; return true; }
            if (s.Length - i >= 5 && s.Substring(i, 5) == "false") { i += 5; return false; }
            if (s.Length - i >= 4 && s.Substring(i, 4) == "null") { i += 4; return null; }
            int st = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            double num;
            if (double.TryParse(s.Substring(st, i - st), NumberStyles.Float, U.IC, out num)) return num;
            i++;
            return null;
        }

        static string Str(string s, ref int i)
        {
            StringBuilder sb = new StringBuilder();
            i++; // opening quote
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') break;
                if (c == '\\' && i < s.Length)
                {
                    char e = s[i++];
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (i + 4 <= s.Length)
                            {
                                sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16));
                                i += 4;
                            }
                            break;
                        default: sb.Append(e); break;
                    }
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        // Breadth-first search for the first key (any level) matching one of names.
        static object Find(object root, string[] names, Func<object, bool> accept)
        {
            Queue<object> q = new Queue<object>();
            q.Enqueue(root);
            int guard = 0;
            while (q.Count > 0 && guard++ < 2000)
            {
                object o = q.Dequeue();
                Dictionary<string, object> d = o as Dictionary<string, object>;
                if (d != null)
                {
                    foreach (string n in names)
                    {
                        object v;
                        if (d.TryGetValue(n, out v) && v != null && accept(v)) return v;
                    }
                    foreach (object v in d.Values) if (v is Dictionary<string, object> || v is List<object>) q.Enqueue(v);
                    continue;
                }
                List<object> l = o as List<object>;
                if (l != null) foreach (object v in l) if (v is Dictionary<string, object> || v is List<object>) q.Enqueue(v);
            }
            return null;
        }

        public static string FindStr(object root, params string[] names)
        {
            object v = Find(root, names, delegate(object x) { return x is string || x is double; });
            if (v == null) return null;
            if (v is double) return ((double)v).ToString("0.################", U.IC);
            return (string)v;
        }

        public static double FindNum(object root, params string[] names)
        {
            object v = Find(root, names, delegate(object x)
            {
                if (x is double) return true;
                string s = x as string;
                double t;
                return s != null && double.TryParse(s, NumberStyles.Float, U.IC, out t);
            });
            if (v == null) return double.NaN;
            if (v is double) return (double)v;
            return double.Parse((string)v, NumberStyles.Float, U.IC);
        }

        public static bool FindBool(object root, bool def, params string[] names)
        {
            object v = Find(root, names, delegate(object x) { return x is bool || x is double || x is string; });
            if (v == null) return def;
            if (v is bool) return (bool)v;
            if (v is double) return (double)v != 0;
            string s = ((string)v).ToLowerInvariant();
            return s == "true" || s == "1";
        }

        // First http(s) url found under any key containing "profilepicture" or "avatar".
        public static string FindAvatar(object root)
        {
            Queue<object> q = new Queue<object>();
            q.Enqueue(root);
            int guard = 0;
            while (q.Count > 0 && guard++ < 2000)
            {
                object o = q.Dequeue();
                Dictionary<string, object> d = o as Dictionary<string, object>;
                if (d != null)
                {
                    foreach (KeyValuePair<string, object> kv in d)
                    {
                        string k = kv.Key.ToLowerInvariant();
                        if (k.Contains("profilepicture") || k.Contains("avatar"))
                        {
                            string u = FirstUrl(kv.Value, 0);
                            if (u != null) return u;
                        }
                    }
                    foreach (object v in d.Values) if (v is Dictionary<string, object> || v is List<object>) q.Enqueue(v);
                    continue;
                }
                List<object> l = o as List<object>;
                if (l != null) foreach (object v in l) if (v is Dictionary<string, object> || v is List<object>) q.Enqueue(v);
            }
            return null;
        }

        static string FirstUrl(object o, int depth)
        {
            if (o == null || depth > 6) return null;
            string s = o as string;
            if (s != null) return s.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? s : null;
            List<object> l = o as List<object>;
            if (l != null)
            {
                foreach (object v in l) { string r = FirstUrl(v, depth + 1); if (r != null) return r; }
                return null;
            }
            Dictionary<string, object> d = o as Dictionary<string, object>;
            if (d != null)
            {
                foreach (object v in d.Values) { string r = FirstUrl(v, depth + 1); if (r != null) return r; }
            }
            return null;
        }
    }

    // ------------------------------------------------------------------------
    //  Configuration model (everything comes from TikArena.ini)
    // ------------------------------------------------------------------------
    class PanelPos
    {
        public string Pos = "TopLeft";
        public float Ox, Oy;
        public bool Show = true;
    }

    class LayoutCfg
    {
        public Dictionary<string, PanelPos> P = new Dictionary<string, PanelPos>(StringComparer.OrdinalIgnoreCase);
        public float HealthWidth = 260;
        public float Scale = 1;

        public static readonly string[] Panels = { "Score", "Top3", "Health", "Guide", "Notif", "Feed", "Hype" };

        public static LayoutCfg Read(Ini ini, string sec, bool vertical)
        {
            LayoutCfg l = new LayoutCfg();
            string[] defPos = vertical
                ? new string[] { "TopCenter", "TopCenter", "BottomCenter", "MiddleLeft", "BottomCenter", "MiddleCenter", "TopCenter" }
                : new string[] { "TopCenter", "TopLeft", "BottomCenter", "MiddleLeft", "BottomRight", "MiddleRight", "TopRight" };
            float[] defOy = vertical ? new float[] { 0, 100, 0, 0, -70, 130, 262 } : new float[] { 0, 0, 0, 0, 0, 0, 70 };
            for (int i = 0; i < Panels.Length; i++)
            {
                string n = Panels[i];
                PanelPos p = new PanelPos();
                p.Pos = ini.S(sec, n + "Position", defPos[i]);
                p.Ox = ini.F(sec, n + "OffsetX", 0);
                p.Oy = ini.F(sec, n + "OffsetY", defOy[i]);
                p.Show = ini.B(sec, n + "Show", !(vertical && n == "Guide"));
                l.P[n] = p;
            }
            l.HealthWidth = ini.F(sec, "HealthWidth", vertical ? 220 : 260);
            l.Scale = U.Clamp(ini.F(sec, "Scale", vertical ? 0.9f : 1f), 0.3f, 3f);
            return l;
        }
    }

    class Interaction
    {
        public int Index;
        public bool Enabled = true;
        public string Name = "";
        public string Action = "SpawnEnemy";
        public string Trigger = "Gift";
        public string GiftName = "";
        public string GiftId = "";
        public int LikesCount = 100;
        public string CommentText = "";
        public bool OncePerUser;
        public Keys TestKey = Keys.None;
        public int Units = 1;
        public bool MultiplyByCombo = true;
        public float Cooldown;
        public string Effect = "None";
        public string Message = "";
        public string GiftImage = "";
        public string ActionImage = "";
        public bool EntryCam;
        public bool ShowInGuide = true;
        public string Model = "";
        public int Health = 150;
        public int Armor;
        public string Weapon = "WEAPON_PISTOL";
        public int Ammo = 9999;
        public int Accuracy = 25;
        public int CombatAbility = 1;
        public int CombatMovement = 2;
        public int ShootRate = 100;
        public bool Blip = true;
        public float SpawnDistance = 20;
        public string VehicleModel = "";
        public bool WarpIntoVehicle = true;
        public float Duration = 15;
        public int Amount = 5;
        public string Weather = "Random";

        // runtime
        public long LastFire = -999999;
        public HashSet<string> OnceUsers = new HashSet<string>();
        public Dictionary<string, int> LikeAcc = new Dictionary<string, int>();

        public static Interaction Read(Ini ini, int n)
        {
            string s = "Interaction" + n;
            Interaction it = new Interaction();
            it.Index = n;
            it.Enabled = ini.B(s, "Enabled", true);
            it.Name = ini.S(s, "Name", "");
            it.Action = ini.S(s, "Action", "SpawnEnemy");
            it.Trigger = ini.S(s, "Trigger", "Gift");
            it.GiftName = ini.S(s, "GiftName", "");
            it.GiftId = ini.S(s, "GiftId", "");
            it.LikesCount = Math.Max(1, ini.I(s, "LikesCount", 100));
            it.CommentText = ini.S(s, "CommentText", "");
            it.OncePerUser = ini.B(s, "OncePerUser", false);
            it.TestKey = ini.K(s, "TestKey", "None");
            it.Units = U.Clamp(ini.I(s, "Units", 1), 1, 999);
            it.MultiplyByCombo = ini.B(s, "MultiplyByCombo", true);
            it.Cooldown = Math.Max(0, ini.F(s, "Cooldown", 0));
            it.Effect = ini.S(s, "Effect", "None");
            it.Message = ini.S(s, "Message", "");
            it.GiftImage = ini.S(s, "GiftImage", "");
            it.ActionImage = ini.S(s, "ActionImage", "");
            it.EntryCam = ini.B(s, "EntryCam", false);
            it.ShowInGuide = ini.B(s, "ShowInGuide", true);
            it.Model = ini.S(s, "Model", "");
            it.Health = Math.Max(1, ini.I(s, "Health", 150));
            it.Armor = Math.Max(0, ini.I(s, "Armor", 0));
            it.Weapon = ini.S(s, "Weapon", "WEAPON_PISTOL");
            it.Ammo = Math.Max(1, ini.I(s, "Ammo", 9999));
            it.Accuracy = U.Clamp(ini.I(s, "Accuracy", 25), 0, 100);
            it.CombatAbility = U.Clamp(ini.I(s, "CombatAbility", 1), 0, 2);
            it.CombatMovement = U.Clamp(ini.I(s, "CombatMovement", 2), 0, 3);
            it.ShootRate = U.Clamp(ini.I(s, "ShootRate", 100), 10, 1000);
            it.Blip = ini.B(s, "Blip", true);
            it.SpawnDistance = U.Clamp(ini.F(s, "SpawnDistance", 20), 2, 500);
            it.VehicleModel = ini.S(s, "VehicleModel", "");
            it.WarpIntoVehicle = ini.B(s, "WarpIntoVehicle", true);
            it.Duration = Math.Max(0.5f, ini.F(s, "Duration", 15));
            it.Amount = Math.Max(1, ini.I(s, "Amount", 5));
            it.Weather = ini.S(s, "Weather", "Random");
            return it;
        }

        public string Title { get { return string.IsNullOrEmpty(Name) ? Action : Name; } }
    }

    class RandomEntry
    {
        public string Action;
        public int Weight;
    }

    class Cfg
    {
        // [General]
        public Keys PowerKey, StartKey, HudStyleKey, HudFontKey, EmergencyKey, PauseKey, LayoutKey, HypeTestKey;
        public bool AutoReload, AutoImport, ShowLoadMessage, CleanupOnPowerOff, ShowStatusMessages, TestKeysEnabled, DebugLog;
        // [TikFinity]
        public bool LiveEnabled, LearnMode;
        public string LiveUrl, TestUserName;
        public int ReconnectSeconds;
        // [Challenge]
        public bool ChallengeEnabled, AutoRestart, EndScreenEnabled, MvpEnabled;
        public float DurationMinutes, EndScreenSeconds;
        // [Queue]
        public int MaxEnemies, MaxAllies, MaxPerEvent, DelayMs;
        public float CorpseCleanupSeconds, VehicleCleanupSeconds;
        public bool ClearQueueOnRoundEnd;
        // [World]
        public bool PoliceIgnore, GangsIgnore;
        // [Player]
        public bool ApplyOnRoundStart, PlayerInvincible;
        public int PlayerHealth, PlayerArmor;
        // [Random]
        public float RndDuration;
        public string RndEffect, RndEnemyModel, RndEnemyWeapon, RndAllyModel, RndAllyWeapon, RndVehicle, RndWeapon;
        public int RndEnemyHealth, RndAllyHealth, RndAnimals, RndHealth;
        public List<RandomEntry> RandomList = new List<RandomEntry>();
        // [Hud]
        public bool HudEnabled, TextOutline, TextShadow;
        public string HudStyle, HudFont, LayoutMode;
        public List<string> StyleCycle, FontCycle;
        public float FontScale;
        public int Opacity;
        public Color Accent, TextColor, PanelColor, WinColor, LossColor;
        public bool ScoreEnabled, TitleEnabled, ShowScore, ShowTimer, ShowStreak;
        public List<string> ScoreOrder;
        public string Title;
        public bool Top3Enabled, ShowTop3, ShowCoins, ShowCounters, ShowStatus;
        public List<string> Top3Order;
        public int TopCount;
        public string Top3Title;
        public bool HealthEnabled, ShowArmor, ShowHealthPoints, HideRadar;
        public string HealthLabel;
        public Color HealthColor;
        public bool GuideEnabled, GuideRandom, GuideShowGiftImage, GuideShowActionImage, GuideTitleEnabled;
        public int GuideMax, GuideRotateCount;
        public float GuideRotateSeconds;
        public string GuideMode, GuideTitle;
        public Color GuideGiftColor, GuideActionColor;
        public bool OverheadEnabled, OverheadAvatar, OverheadName, OverheadHealth;
        public float OverheadHeight;
        public string AvatarShape;
        // [Notifications] / [KillFeed]
        public bool NotifEnabled, NotifAvatar, NotifGiftIcon, FeedEnabled;
        public float NotifSeconds, FeedSeconds;
        public int NotifMax, FeedMax;
        // [Hype]
        public bool HypeEnabled, HypeShowLevel;
        public float HypeSeconds;
        public int HypeMax;
        public Color HypeColor;
        public bool KingJoinEnabled, VipJoinEnabled, ComebackEnabled, RivalryEnabled, NewKingEnabled, ComboEnabled;
        public string KingJoinText, VipJoinText, ComebackText, RivalryText, NewKingText, ComboText;
        public int VipJoinMinLevel, VipJoinMinCoins, ComebackMinCoins, NewKingMinCoins, ComboMin;
        public float ComebackMinutes, RivalrySeconds;
        // [Sounds]
        public string SndEnemy, SndAlly, SndHelp, SndKill, SndHype;
        // [Camera]
        public bool InvincibleDuringCam, EntryEnabled, KillerEnabled, WinCamEnabled;
        public int EntryMinCoins;
        public float EntrySeconds, KillerSeconds, SlowMoScale, SlowMoSeconds, WinSeconds;
        public string SlowMoMode;
        // [Graphics]
        public bool LockTime, LockWeather, TimecycleEnabled, DensityEnabled;
        public int Hour, Minute;
        public string Weather, Timecycle;
        public float TimecycleStrength, PedDensity, VehicleDensity;
        // [Texts]
        public Dictionary<string, string> T = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // [Layout] / [LayoutVertical]
        public LayoutCfg Normal, Vertical;
        // [KillEffects]
        public bool KillFxEnabled, KillOnlyPlayer, KillSoundEnabled;
        public string KillEffect, KillSound;
        public Color SmokeColor;
        // [Death]
        public bool CelebEnabled, CelebDance, CelebCoffin, DeathSoundEnabled;
        public float CelebSeconds;
        public string DanceDict, DanceAnim, CoffinModel, DeathSound;
        // [Interactions]
        public List<Interaction> Interactions = new List<Interaction>();

        public static readonly string[] RandomActions = {
            "Earthquake", "CarRain", "ExplodeNearby", "KillPlayer", "SpawnEnemy", "SpawnAlly", "GiveVehicle", "RemoveVehicle",
            "GiveAllWeapons", "RemoveWeapons", "Fire", "Launch", "Ragdoll", "Drunk", "SlowMotion", "LowGravity", "SuperJump",
            "Blackout", "Animals", "Teleport", "Weather", "Heal", "MafiaCar", "MotoHitman", "AddHealth", "Freeze", "GodMode",
            "SuperSpeed", "Skyfall", "ClearArea", "EjectVehicle", "ExplodeVehicle", "BurstTires", "BoostVehicle", "Airstrike", "Storm" };

        public string Tx(string key, string def)
        {
            string v;
            if (T.TryGetValue(key, out v) && v != null) return v;
            return def;
        }

        public static Cfg Load(Ini ini)
        {
            Cfg c = new Cfg();
            c.PowerKey = ini.K("General", "PowerKey", "K");
            c.StartKey = ini.K("General", "StartKey", "F3");
            c.HudStyleKey = ini.K("General", "HudStyleKey", "F11");
            c.HudFontKey = ini.K("General", "HudFontKey", "F8");
            c.EmergencyKey = ini.K("General", "EmergencyKey", "Delete");
            c.PauseKey = ini.K("General", "PauseKey", "Pause");
            c.LayoutKey = ini.K("General", "LayoutKey", "F7");
            c.HypeTestKey = ini.K("General", "HypeTestKey", "F6");
            c.AutoReload = ini.B("General", "AutoReload", true);
            c.AutoImport = ini.B("General", "AutoImport", true);
            c.ShowLoadMessage = ini.B("General", "ShowLoadMessage", true);
            c.CleanupOnPowerOff = ini.B("General", "CleanupOnPowerOff", true);
            c.ShowStatusMessages = ini.B("General", "ShowStatusMessages", true);
            c.TestKeysEnabled = ini.B("General", "TestKeysEnabled", true);
            c.DebugLog = ini.B("General", "DebugLog", false);

            c.LiveEnabled = ini.B("TikFinity", "Enabled", true);
            c.LiveUrl = ini.S("TikFinity", "Url", "ws://localhost:21213/");
            c.ReconnectSeconds = Math.Max(1, ini.I("TikFinity", "ReconnectSeconds", 5));
            c.LearnMode = ini.B("TikFinity", "LearnMode", true);
            c.TestUserName = ini.S("TikFinity", "TestUserName", "Test");

            c.ChallengeEnabled = ini.B("Challenge", "Enabled", true);
            c.DurationMinutes = Math.Max(0.1f, ini.F("Challenge", "DurationMinutes", 10));
            c.AutoRestart = ini.B("Challenge", "AutoRestart", true);
            c.EndScreenEnabled = ini.B("Challenge", "EndScreenEnabled", true);
            c.EndScreenSeconds = Math.Max(1, ini.F("Challenge", "EndScreenSeconds", 6));
            c.MvpEnabled = ini.B("Challenge", "MvpEnabled", true);

            c.MaxEnemies = U.Clamp(ini.I("Queue", "MaxEnemies", 15), 1, 100);
            c.MaxAllies = U.Clamp(ini.I("Queue", "MaxAllies", 10), 1, 100);
            c.MaxPerEvent = Math.Max(0, ini.I("Queue", "MaxPerEvent", 0));
            c.DelayMs = U.Clamp(ini.I("Queue", "DelayMs", 400), 0, 60000);
            c.CorpseCleanupSeconds = Math.Max(0, ini.F("Queue", "CorpseCleanupSeconds", 2));
            c.VehicleCleanupSeconds = Math.Max(1, ini.F("Queue", "VehicleCleanupSeconds", 60));
            c.ClearQueueOnRoundEnd = ini.B("Queue", "ClearQueueOnRoundEnd", false);

            c.PoliceIgnore = ini.B("World", "PoliceIgnore", true);
            c.GangsIgnore = ini.B("World", "GangsIgnore", true);

            c.ApplyOnRoundStart = ini.B("Player", "ApplyOnRoundStart", true);
            c.PlayerHealth = Math.Max(1, ini.I("Player", "Health", 1000));
            c.PlayerArmor = U.Clamp(ini.I("Player", "Armor", 100), 0, 100);
            c.PlayerInvincible = ini.B("Player", "Invincible", false);

            c.RndDuration = Math.Max(1, ini.F("Random", "Duration", 15));
            c.RndEffect = ini.S("Random", "Effect", "SoftSmoke");
            c.RndEnemyModel = ini.S("Random", "EnemyModel", "s_m_y_clown_01");
            c.RndEnemyWeapon = ini.S("Random", "EnemyWeapon", "WEAPON_BAT");
            c.RndEnemyHealth = Math.Max(1, ini.I("Random", "EnemyHealth", 150));
            c.RndAllyModel = ini.S("Random", "AllyModel", "s_m_y_swat_01");
            c.RndAllyWeapon = ini.S("Random", "AllyWeapon", "WEAPON_CARBINERIFLE");
            c.RndAllyHealth = Math.Max(1, ini.I("Random", "AllyHealth", 300));
            c.RndAnimals = Math.Max(1, ini.I("Random", "AnimalsAmount", 3));
            c.RndHealth = Math.Max(1, ini.I("Random", "HealthAmount", 250));
            c.RndVehicle = ini.S("Random", "VehicleModel", "zentorno");
            c.RndWeapon = ini.S("Random", "GiveWeapon", "WEAPON_CARBINERIFLE");
            foreach (string a in RandomActions)
            {
                bool def = a != "KillPlayer" && a != "Airstrike";
                if (!ini.B("Random", a + "Enabled", def)) continue;
                RandomEntry e = new RandomEntry();
                e.Action = a;
                e.Weight = U.Clamp(ini.I("Random", a + "Weight", 10), 1, 50);
                c.RandomList.Add(e);
            }

            c.HudEnabled = ini.B("Hud", "Enabled", true);
            c.HudStyle = ini.S("Hud", "Style", "Classic");
            c.StyleCycle = ini.L("Hud", "StyleCycle", "Classic,Glass,Neon,Minimal,Esports,Retro");
            c.HudFont = ini.S("Hud", "Font", "ChaletLondon");
            c.FontCycle = ini.L("Hud", "FontCycle", "ChaletLondon,ChaletComprimeCologne,Pricedown");
            c.FontScale = U.Clamp(ini.F("Hud", "FontScale", 1), 0.3f, 3f);
            c.TextOutline = ini.B("Hud", "TextOutline", true);
            c.TextShadow = ini.B("Hud", "TextShadow", true);
            c.Opacity = U.Clamp(ini.I("Hud", "Opacity", 200), 0, 255);
            c.Accent = ini.C("Hud", "AccentColor", "#00e676");
            c.TextColor = ini.C("Hud", "TextColor", "#ffffff");
            c.PanelColor = ini.C("Hud", "PanelColor", "#0a0e16");
            c.WinColor = ini.C("Hud", "WinColor", "#00e676");
            c.LossColor = ini.C("Hud", "LossColor", "#ff3b5c");
            c.LayoutMode = ini.S("Hud", "LayoutMode", "Normal");
            c.ScoreEnabled = ini.B("Hud", "ScoreEnabled", true);
            c.ScoreOrder = ini.L("Hud", "ScoreOrder", "Title,Score,Streak");
            c.TitleEnabled = ini.B("Hud", "TitleEnabled", true);
            c.Title = ini.S("Hud", "Title", "حاول تقتلني");
            c.ShowScore = ini.B("Hud", "ShowScore", true);
            c.ShowTimer = ini.B("Hud", "ShowTimer", true);
            c.ShowStreak = ini.B("Hud", "ShowStreak", true);
            c.Top3Enabled = ini.B("Hud", "Top3Enabled", true);
            c.Top3Order = ini.L("Hud", "Top3Order", "Top3,Counters,Status");
            c.ShowTop3 = ini.B("Hud", "ShowTop3", true);
            c.TopCount = U.Clamp(ini.I("Hud", "TopCount", 3), 1, 10);
            c.Top3Title = ini.S("Hud", "Top3Title", "أفضل الداعمين");
            c.ShowCoins = ini.B("Hud", "ShowCoins", true);
            c.ShowCounters = ini.B("Hud", "ShowCounters", true);
            c.ShowStatus = ini.B("Hud", "ShowStatus", true);
            c.HealthEnabled = ini.B("Hud", "HealthEnabled", true);
            c.HealthLabel = ini.S("Hud", "HealthLabel", "health");
            c.HealthColor = ini.C("Hud", "HealthColor", "#1ed760");
            c.ShowArmor = ini.B("Hud", "ShowArmor", true);
            c.ShowHealthPoints = ini.B("Hud", "ShowHealthPoints", false);
            c.HideRadar = ini.B("Hud", "HideRadar", false);
            c.GuideEnabled = ini.B("Hud", "GuideEnabled", true);
            c.GuideMax = U.Clamp(ini.I("Hud", "GuideMax", 8), 1, 30);
            c.GuideMode = ini.S("Hud", "GuideMode", "Cards");
            c.GuideRotateCount = U.Clamp(ini.I("Hud", "GuideRotateCount", 3), 1, 30);
            c.GuideRotateSeconds = Math.Max(1, ini.F("Hud", "GuideRotateSeconds", 5));
            c.GuideRandom = ini.B("Hud", "GuideRandom", true);
            c.GuideShowGiftImage = ini.B("Hud", "GuideShowGiftImage", true);
            c.GuideShowActionImage = ini.B("Hud", "GuideShowActionImage", true);
            c.GuideTitleEnabled = ini.B("Hud", "GuideTitleEnabled", false);
            c.GuideTitle = ini.S("Hud", "GuideTitle", "الهدايا");
            c.GuideGiftColor = ini.C("Hud", "GuideGiftColor", "#2979ff");
            c.GuideActionColor = ini.C("Hud", "GuideActionColor", "#00e676");
            c.OverheadEnabled = ini.B("Hud", "OverheadEnabled", true);
            c.OverheadAvatar = ini.B("Hud", "OverheadAvatar", true);
            c.OverheadName = ini.B("Hud", "OverheadName", true);
            c.OverheadHealth = ini.B("Hud", "OverheadHealth", true);
            c.OverheadHeight = ini.F("Hud", "OverheadHeight", 1.2f);
            c.AvatarShape = ini.S("Hud", "AvatarShape", "Circle");

            c.NotifEnabled = ini.B("Notifications", "Enabled", true);
            c.NotifSeconds = Math.Max(1, ini.F("Notifications", "Seconds", 5));
            c.NotifMax = U.Clamp(ini.I("Notifications", "MaxVisible", 5), 1, 20);
            c.NotifAvatar = ini.B("Notifications", "ShowAvatar", true);
            c.NotifGiftIcon = ini.B("Notifications", "ShowGiftIcon", true);
            c.FeedEnabled = ini.B("KillFeed", "Enabled", true);
            c.FeedSeconds = Math.Max(1, ini.F("KillFeed", "Seconds", 4));
            c.FeedMax = U.Clamp(ini.I("KillFeed", "MaxVisible", 4), 1, 20);

            c.HypeEnabled = ini.B("Hype", "Enabled", true);
            c.HypeSeconds = Math.Max(1, ini.F("Hype", "Seconds", 6));
            c.HypeMax = U.Clamp(ini.I("Hype", "MaxVisible", 3), 1, 10);
            c.HypeShowLevel = ini.B("Hype", "ShowLevel", true);
            c.HypeColor = ini.C("Hype", "Color", "#ffc828");
            c.KingJoinEnabled = ini.B("Hype", "KingJoinEnabled", true);
            c.KingJoinText = ini.S("Hype", "KingJoinText", "👑 احذر! لقد دخل الملك {name}");
            c.VipJoinEnabled = ini.B("Hype", "VipJoinEnabled", true);
            c.VipJoinMinLevel = ini.I("Hype", "VipJoinMinLevel", 20);
            c.VipJoinMinCoins = ini.I("Hype", "VipJoinMinCoins", 100);
            c.VipJoinText = ini.S("Hype", "VipJoinText", "⚠️ احذر! لقد دخل {name} (Lv {level})");
            c.ComebackEnabled = ini.B("Hype", "ComebackEnabled", true);
            c.ComebackMinCoins = ini.I("Hype", "ComebackMinCoins", 100);
            c.ComebackMinutes = Math.Max(0.1f, ini.F("Hype", "ComebackMinutes", 5));
            c.ComebackText = ini.S("Hype", "ComebackText", "👀 أين أنت يا {name}؟ اللايف كيتسناك");
            c.RivalryEnabled = ini.B("Hype", "RivalryEnabled", true);
            c.RivalrySeconds = Math.Max(1, ini.F("Hype", "RivalrySeconds", 60));
            c.RivalryText = ini.S("Hype", "RivalryText", "⚔️ {name} كيتحدى {rival}!");
            c.NewKingEnabled = ini.B("Hype", "NewKingEnabled", true);
            c.NewKingMinCoins = ini.I("Hype", "NewKingMinCoins", 50);
            c.NewKingText = ini.S("Hype", "NewKingText", "👑 {name} ولى هو الملك ديال اللايف!");
            c.ComboEnabled = ini.B("Hype", "ComboEnabled", true);
            c.ComboMin = Math.Max(2, ini.I("Hype", "ComboMin", 10));
            c.ComboText = ini.S("Hype", "ComboText", "🔥 كومبو x{count} من {name}!");

            c.SndEnemy = ini.S("Sounds", "EnemySpawn", "CHECKPOINT_MISSED|HUD_MINI_GAME_SOUNDSET");
            c.SndAlly = ini.S("Sounds", "AllySpawn", "CHALLENGE_UNLOCKED|HUD_AWARDS");
            c.SndHelp = ini.S("Sounds", "Help", "PICK_UP|HUD_FRONTEND_DEFAULT_SOUNDSET");
            c.SndKill = ini.S("Sounds", "Kill", "CHECKPOINT_PERFECT|HUD_MINI_GAME_SOUNDSET");
            c.SndHype = ini.S("Sounds", "Hype", "MEDAL_UP|HUD_MINI_GAME_SOUNDSET");

            c.InvincibleDuringCam = ini.B("Camera", "InvincibleDuringCam", true);
            c.EntryEnabled = ini.B("Camera", "EntryEnabled", true);
            c.EntryMinCoins = ini.I("Camera", "EntryMinCoins", 1000);
            c.EntrySeconds = Math.Max(0.5f, ini.F("Camera", "EntrySeconds", 3));
            c.KillerEnabled = ini.B("Camera", "KillerEnabled", true);
            c.KillerSeconds = Math.Max(0.5f, ini.F("Camera", "KillerSeconds", 3));
            c.SlowMoMode = ini.S("Camera", "SlowMoMode", "LastInWave");
            c.SlowMoScale = U.Clamp(ini.F("Camera", "SlowMoScale", 0.3f), 0.05f, 1f);
            c.SlowMoSeconds = Math.Max(0.1f, ini.F("Camera", "SlowMoSeconds", 1.5f));
            c.WinCamEnabled = ini.B("Camera", "WinEnabled", true);
            c.WinSeconds = Math.Max(0.5f, ini.F("Camera", "WinSeconds", 4));

            c.LockTime = ini.B("Graphics", "LockTime", false);
            c.Hour = U.Clamp(ini.I("Graphics", "Hour", 21), 0, 23);
            c.Minute = U.Clamp(ini.I("Graphics", "Minute", 0), 0, 59);
            c.LockWeather = ini.B("Graphics", "LockWeather", false);
            c.Weather = ini.S("Graphics", "Weather", "Clear");
            c.TimecycleEnabled = ini.B("Graphics", "TimecycleEnabled", false);
            c.Timecycle = ini.S("Graphics", "Timecycle", "rply_saturation");
            c.TimecycleStrength = U.Clamp(ini.F("Graphics", "TimecycleStrength", 0.6f), 0, 1);
            c.DensityEnabled = ini.B("Graphics", "DensityEnabled", false);
            c.PedDensity = U.Clamp(ini.F("Graphics", "PedDensity", 1), 0, 3);
            c.VehicleDensity = U.Clamp(ini.F("Graphics", "VehicleDensity", 1), 0, 3);

            string[] texts = {
                "WinText=فوز", "LossText=خسارة", "WinShort=فوز", "LossShort=خسارة", "MvpText=MVP",
                "MvpWinReason=ساعدك باش تربح", "MvpLossReason=هو السبب ف الخسارة", "KillFeedText={name} قُتل",
                "WinStreakText=انتصارات متتالية", "LossStreakText=خسارات متتالية", "EnemiesShort=أعداء",
                "AlliesShort=مساعدين", "QueueShort=الطابور", "PausedText=إيقاف مؤقت", "ResumedText=استئناف",
                "StartedText=بدأ التحدي", "EmergencyText=تم مسح كل شيء", "LoadedText=جاهز. ضغط",
                "ReloadedText=تحدثات الإعدادات", "ImportedText=تجابو الإعدادات الجداد من Downloads" };
            foreach (string t in texts)
            {
                int eq = t.IndexOf('=');
                string k = t.Substring(0, eq);
                c.T[k] = ini.S("Texts", k, t.Substring(eq + 1));
            }

            c.Normal = LayoutCfg.Read(ini, "Layout", false);
            c.Vertical = LayoutCfg.Read(ini, "LayoutVertical", true);

            c.KillFxEnabled = ini.B("KillEffects", "Enabled", true);
            c.KillEffect = ini.S("KillEffects", "Effect", "SoftSmoke");
            c.SmokeColor = ini.C("KillEffects", "SmokeColor", "#33ccff");
            c.KillOnlyPlayer = ini.B("KillEffects", "OnlyPlayerKills", false);
            c.KillSoundEnabled = ini.B("KillEffects", "SoundEnabled", false);
            c.KillSound = ini.S("KillEffects", "Sound", "");

            c.CelebEnabled = ini.B("Death", "CelebrationEnabled", true);
            c.CelebSeconds = U.Clamp(ini.F("Death", "Seconds", 8), 1, 60);
            c.CelebDance = ini.B("Death", "Dance", true);
            c.DanceDict = ini.S("Death", "DanceDict", "missfbi3_sniping");
            c.DanceAnim = ini.S("Death", "DanceAnim", "dance_m_default");
            c.CelebCoffin = ini.B("Death", "Coffin", true);
            c.CoffinModel = ini.S("Death", "CoffinModel", "prop_coffin_02b");
            c.DeathSoundEnabled = ini.B("Death", "SoundEnabled", false);
            c.DeathSound = ini.S("Death", "Sound", "");

            int count = U.Clamp(ini.I("Interactions", "Count", 0), 0, 500);
            for (int i = 1; i <= count; i++)
            {
                if (!ini.Has("Interaction" + i)) continue;
                c.Interactions.Add(Interaction.Read(ini, i));
            }
            return c;
        }
    }

    // ------------------------------------------------------------------------
    //  Runtime data
    // ------------------------------------------------------------------------
    class LiveEvent
    {
        public string Type = "";
        public string UserKey = "", Nick = "", Avatar;
        public int Level;
        public string GiftName = "", GiftId = "";
        public int Count = 1;
        public long Coins;
        public int Diamonds;
        public int RepeatCount = 1;
        public bool RepeatEnd = true;
        public int GiftType;
        public string Comment = "";
        public int Likes;
    }

    class Supporter
    {
        public string Key = "", Nick = "", AvatarUrl;
        public int Level;
        public long Coins;
        public long RoundHelpCoins;
        public long LastActive = -999999999, LastGift = -999999999;
        public bool ComebackSent;
    }

    class Job
    {
        public Interaction It;
        public Supporter Sup;
        public int Left;
        public long Coins;         // coins of the triggering event (for entry cam)
        public bool Random;        // runs with [Random] settings
        public string RandomAction;
        public bool CamDone;
    }

    class Tracked
    {
        public Ped Ped;
        public bool Enemy;
        public Supporter Sup;
        public Interaction It;
        public Vehicle Veh;
        public bool Leader = true;
        public int MaxHp = 200;
        public long DeadAt;
        public long SpawnAt;
        public long NextTask;
        public bool Animal;
        public bool Parachuting;
        public bool FxOk = true;
    }

    class FeedItem
    {
        public string Text = "";
        public string Avatar;      // local png path or null
        public string Icon;        // gift icon png path or null
        public int Level;
        public long Start, End;
        public Color Col;
    }

    class TempVehicle
    {
        public Vehicle Veh;
        public long DeleteAt;
    }

    // ------------------------------------------------------------------------
    //  TikFinity WebSocket client (background thread -> Inbox)
    // ------------------------------------------------------------------------
    class Live
    {
        public readonly ConcurrentQueue<string> Inbox = new ConcurrentQueue<string>();
        public volatile bool Connected;
        volatile bool run;
        Thread thread;
        ClientWebSocket cur;
        string url = "ws://localhost:21213/";
        int reconnect = 5;

        public bool Running { get { return run; } }

        public void Start(string u, int rec)
        {
            url = u; reconnect = rec;
            if (run) return;
            run = true;
            thread = new Thread(Loop);
            thread.IsBackground = true;
            thread.Start();
        }

        public void Stop()
        {
            run = false;
            Connected = false;
            try { ClientWebSocket w = cur; if (w != null) w.Abort(); } catch { }
        }

        void Loop()
        {
            byte[] buf = new byte[1 << 16];
            while (run)
            {
                try
                {
                    using (ClientWebSocket ws = new ClientWebSocket())
                    {
                        cur = ws;
                        ws.ConnectAsync(new Uri(url), CancellationToken.None).Wait(8000);
                        if (ws.State == WebSocketState.Open)
                        {
                            Connected = true;
                            MemoryStream ms = new MemoryStream();
                            while (run && ws.State == WebSocketState.Open)
                            {
                                WebSocketReceiveResult r = ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None).Result;
                                if (r.MessageType == WebSocketMessageType.Close) break;
                                ms.Write(buf, 0, r.Count);
                                if (r.EndOfMessage)
                                {
                                    if (Inbox.Count < 5000) Inbox.Enqueue(Encoding.UTF8.GetString(ms.ToArray()));
                                    ms.SetLength(0);
                                }
                            }
                        }
                    }
                }
                catch { }
                cur = null;
                Connected = false;
                for (int i = 0; i < reconnect * 10 && run; i++) Thread.Sleep(100);
            }
        }
    }

    // ------------------------------------------------------------------------
    //  Profile pictures: download -> round/square 128x128 PNG in cache/
    // ------------------------------------------------------------------------
    class Avatars
    {
        readonly ConcurrentQueue<string[]> work = new ConcurrentQueue<string[]>();
        readonly ConcurrentDictionary<string, string> ready = new ConcurrentDictionary<string, string>();
        readonly ConcurrentDictionary<string, bool> pending = new ConcurrentDictionary<string, bool>();
        readonly AutoResetEvent signal = new AutoResetEvent(false);
        Thread thread;
        volatile bool run;
        public string CacheDir = "";
        public bool Circle = true;
        public string DefaultFile;

        public void Start(string cacheDir)
        {
            CacheDir = cacheDir;
            try { Directory.CreateDirectory(cacheDir); } catch { }
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { }
            MakeDefault();
            if (run) return;
            run = true;
            thread = new Thread(Loop);
            thread.IsBackground = true;
            thread.Start();
        }

        public void Stop() { run = false; signal.Set(); }

        string Suffix { get { return Circle ? "_c.png" : "_s.png"; } }

        void MakeDefault()
        {
            DefaultFile = Path.Combine(CacheDir, "_default" + Suffix);
            if (File.Exists(DefaultFile)) return;
            try
            {
                using (Bitmap b = new Bitmap(128, 128, PixelFormat.Format32bppArgb))
                using (Graphics g = Graphics.FromImage(b))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (SolidBrush bg = new SolidBrush(Color.FromArgb(255, 40, 48, 64)))
                    {
                        if (Circle) g.FillEllipse(bg, 0, 0, 127, 127); else g.FillRectangle(bg, 0, 0, 128, 128);
                    }
                    using (SolidBrush fg = new SolidBrush(Color.FromArgb(255, 150, 160, 180)))
                    {
                        g.FillEllipse(fg, 40, 22, 48, 48);
                        g.FillEllipse(fg, 18, 78, 92, 80);
                    }
                    b.Save(DefaultFile, ImageFormat.Png);
                }
            }
            catch { DefaultFile = null; }
        }

        // Returns local png path (or default) for a user; queues a download on first call.
        public string Get(string key, string url)
        {
            if (string.IsNullOrEmpty(key)) return DefaultFile;
            string file;
            string id = U.SafeName(key);
            if (ready.TryGetValue(id, out file)) return file;
            if (string.IsNullOrEmpty(url)) return DefaultFile;
            if (pending.TryAdd(id, true))
            {
                string target = Path.Combine(CacheDir, id + Suffix);
                work.Enqueue(new string[] { id, url, target });
                signal.Set();
            }
            return DefaultFile;
        }

        void Loop()
        {
            while (run)
            {
                string[] job;
                if (!work.TryDequeue(out job)) { signal.WaitOne(1000); continue; }
                try
                {
                    string target = job[2];
                    if (!File.Exists(target) || (DateTime.Now - File.GetLastWriteTime(target)).TotalHours > 24)
                    {
                        Image img = Download(job[1]);
                        if (img == null && job[1].IndexOf(".webp", StringComparison.OrdinalIgnoreCase) >= 0)
                            img = Download(job[1].Replace(".webp", ".jpeg").Replace(".WEBP", ".jpeg"));
                        if (img == null) continue;
                        using (img) Convert(img, target, Circle);
                    }
                    ready[job[0]] = target;
                }
                catch { }
            }
        }

        static Image Download(string url)
        {
            try
            {
                using (WebClient wc = new WebClient())
                {
                    wc.Headers[HttpRequestHeader.UserAgent] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";
                    byte[] data = wc.DownloadData(url);
                    MemoryStream ms = new MemoryStream(data);
                    return Image.FromStream(ms);
                }
            }
            catch { return null; }
        }

        public static void Convert(Image src, string target, bool circle)
        {
            using (Bitmap b = new Bitmap(128, 128, PixelFormat.Format32bppArgb))
            using (Graphics g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.Clear(Color.Transparent);
                int side = Math.Min(src.Width, src.Height);
                Rectangle srcRect = new Rectangle((src.Width - side) / 2, (src.Height - side) / 2, side, side);
                using (Bitmap sq = new Bitmap(128, 128, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g2 = Graphics.FromImage(sq))
                    {
                        g2.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g2.DrawImage(src, new Rectangle(0, 0, 128, 128), srcRect, GraphicsUnit.Pixel);
                    }
                    if (circle)
                    {
                        using (TextureBrush tb = new TextureBrush(sq)) g.FillEllipse(tb, 0, 0, 127, 127);
                    }
                    else g.DrawImage(sq, 0, 0);
                }
                string tmp = target + ".tmp";
                b.Save(tmp, ImageFormat.Png);
                if (File.Exists(target)) File.Delete(target);
                File.Move(tmp, target);
            }
        }
    }

    // ------------------------------------------------------------------------
    //  Drawing helpers (1280x720 virtual screen)
    // ------------------------------------------------------------------------
    static class Gfx
    {
        static readonly TextElement te = new TextElement("", PointF.Empty, 0.35f);
        static readonly Dictionary<string, List<CustomSprite>> pool = new Dictionary<string, List<CustomSprite>>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, int> used = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, bool> exists = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        static long existsReset;

        // current transform
        public static float Ox, Oy, S = 1;
        public static Font F = Font.ChaletLondon;
        public static bool Outline = true, Shadow = true;
        public static float FontScale = 1;

        public static void BeginFrame()
        {
            used.Clear();
            if (U.Now > existsReset) { exists.Clear(); existsReset = U.Now + 2000; }
        }

        public static void Origin(float x, float y, float s) { Ox = x; Oy = y; S = s; }

        public static void RectAbs(float x, float y, float w, float h, Color c)
        {
            if (c.A == 0 || w <= 0 || h <= 0) return;
            Function.Call(Hash.DRAW_RECT, (x + w / 2f) / 1280f, (y + h / 2f) / 720f, w / 1280f, h / 720f, (int)c.R, (int)c.G, (int)c.B, (int)c.A, false);
        }

        public static void Rect(float x, float y, float w, float h, Color c)
        {
            RectAbs(Ox + x * S, Oy + y * S, w * S, h * S, c);
        }

        public static void Border(float x, float y, float w, float h, float t, Color c)
        {
            Rect(x, y, w, t, c);
            Rect(x, y + h - t, w, t, c);
            Rect(x, y + t, t, h - 2 * t, c);
            Rect(x + w - t, y + t, t, h - 2 * t, c);
        }

        public static float LineH(float size) { return size * 40f * FontScale; }

        public static void TextAbs(string s, float x, float y, float scale, Color c, Alignment al, Font f)
        {
            if (string.IsNullOrEmpty(s) || c.A == 0) return;
            te.Caption = s;
            te.Position = new PointF(x, y);
            te.Scale = scale;
            te.Color = c;
            te.Alignment = al;
            te.Font = f;
            te.Outline = Outline;
            te.Shadow = Shadow;
            te.WrapWidth = 0f;
            te.Draw();
        }

        // size: nominal text scale (before global scale/font scale)
        public static void Text(string s, float x, float y, float size, Color c, Alignment al)
        {
            TextAbs(s, Ox + x * S, Oy + y * S, size * S * FontScale, c, al, F);
        }

        public static float TextW(string s, float size)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            try { return TextElement.GetStringWidth(s, F, size * FontScale); }
            catch { return s.Length * size * 18f * FontScale; }
        }

        public static bool FileOk(string file)
        {
            if (string.IsNullOrEmpty(file)) return false;
            bool ok;
            if (exists.TryGetValue(file, out ok)) return ok;
            ok = File.Exists(file);
            exists[file] = ok;
            return ok;
        }

        public static void ImageAbs(string file, float x, float y, float w, float h, int alpha)
        {
            if (!FileOk(file) || alpha <= 0) return;
            List<CustomSprite> list;
            if (!pool.TryGetValue(file, out list)) { list = new List<CustomSprite>(); pool[file] = list; }
            int n;
            used.TryGetValue(file, out n);
            CustomSprite sp;
            if (n < list.Count) sp = list[n];
            else
            {
                if (list.Count > 60) return;
                try { sp = new CustomSprite(file, new SizeF(w, h), new PointF(x, y)); }
                catch { exists[file] = false; return; }
                list.Add(sp);
            }
            used[file] = n + 1;
            sp.Position = new PointF(x, y);
            sp.Size = new SizeF(w, h);
            sp.Color = Color.FromArgb(U.Clamp(alpha, 0, 255), 255, 255, 255);
            sp.Draw();
        }

        public static void Image(string file, float x, float y, float w, float h, int alpha)
        {
            ImageAbs(file, Ox + x * S, Oy + y * S, w * S, h * S, alpha);
        }

        public static void Bar(float x, float y, float w, float h, float frac, Color fg, Color bg)
        {
            frac = U.Clamp(frac, 0, 1);
            Rect(x, y, w, h, bg);
            if (frac > 0) Rect(x, y, w * frac, h, fg);
        }
    }

    // ------------------------------------------------------------------------
    //  Main script
    // ------------------------------------------------------------------------
    public class TikArenaMain : Script
    {
        Cfg cfg;
        string iniPath;
        DateTime iniTime = DateTime.MinValue;
        DateTime lastImport = DateTime.MinValue;
        long nextFileCheck;
        bool first = true;

        bool powered, started, paused;
        readonly Live live = new Live();
        readonly Avatars avatars = new Avatars();

        int relEnemy, relAlly, relPlayer;
        static readonly string[] OtherGroups = { "COP", "ARMY", "SECURITY_GUARD", "PRIVATE_SECURITY", "FIREMAN", "MEDIC" };
        static readonly string[] GangGroups = { "AMBIENT_GANG_BALLAS", "AMBIENT_GANG_FAMILY", "AMBIENT_GANG_LOST", "AMBIENT_GANG_MARABUNTE",
            "AMBIENT_GANG_MEXICAN", "AMBIENT_GANG_SALVA", "AMBIENT_GANG_WEICHENG", "AMBIENT_GANG_HILLBILLY", "AMBIENT_GANG_CULT", "GANG_1", "GANG_2", "GANG_9", "GANG_10" };

        // supporters & live-state
        readonly Dictionary<string, Supporter> sups = new Dictionary<string, Supporter>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, int> streaks = new Dictionary<string, int>();
        readonly HashSet<string> comboFired = new HashSet<string>();
        readonly HashSet<string> learned = new HashSet<string>();
        readonly Dictionary<string, long> hypeLast = new Dictionary<string, long>();
        Supporter king;
        Supporter lastEnemySup;
        long lastEnemyAt = -999999;
        long nextComebackCheck;

        // queues & world
        readonly List<Job> spawnQ = new List<Job>();
        readonly List<Job> instantQ = new List<Job>();
        long nextSpawn, nextInstant;
        readonly List<Tracked> tracked = new List<Tracked>();
        readonly List<TempVehicle> tempVehicles = new List<TempVehicle>();

        // hud feeds
        readonly List<FeedItem> notifs = new List<FeedItem>();
        readonly List<FeedItem> feed = new List<FeedItem>();
        readonly List<FeedItem> hypes = new List<FeedItem>();
        string style = "Classic";
        string fontName = "ChaletLondon";
        bool vertical;
        List<int> guideOrder = new List<int>();
        long guideNextRotate;
        int guideOffset;

        // round
        enum Phase { Idle, Running, Ended }
        Phase phase = Phase.Idle;
        double roundMs;
        double roundDurMs = 600000;
        long lastTickNow;
        int wins, losses, streak;
        long endScreenUntil;
        bool lastWin;
        Supporter mvp;
        string mvpReason = "";
        bool deathHandled;
        Supporter killPlayerSup;
        long killPlayerAt = -999999;

        // effects
        readonly Dictionary<string, long> fxEnd = new Dictionary<string, long>();
        string weatherOverride = "";
        string appliedWeather = null;
        float carRainPerSec = 2, carRainAcc;
        long nextQuake, nextStrike, nextStorm, nextSecond;
        long killSlowEnd;
        float lastTimeScale = 1;
        bool shaking;
        bool drunkClip, speedOn, freezeOn, gravOn, blackoutOn;
        bool radarHidden, clockLocked, tcApplied, wantedApplied;

        // camera
        Camera cam;
        string camMode = "";
        long camEnd;
        Entity camTarget;
        float camOrbit;

        // celebration
        bool celeb;
        long celebEnd;
        readonly List<Ped> dancers = new List<Ped>();
        Prop coffin;
        SoundPlayer deathPlayer;
        bool deathPaused;
        long killerCamEnd;

        static readonly string[] HelpActions = { "SpawnAlly", "Heal", "AddHealth", "GiveWeapon", "GiveAllWeapons", "GiveVehicle", "GodMode", "Clone", "SuperSpeed", "BoostVehicle", "ClearArea" };
        static readonly string[] SpawnActions = { "SpawnEnemy", "SpawnAlly", "MafiaCar", "MotoHitman", "Animals", "Clone" };
        static readonly string[] Weathers = { "CLEAR", "EXTRASUNNY", "CLOUDS", "OVERCAST", "RAIN", "CLEARING", "THUNDER", "SMOG", "FOGGY", "XMAS", "SNOWLIGHT", "BLIZZARD", "HALLOWEEN" };
        static readonly string[] AllWeapons = {
            "WEAPON_KNIFE", "WEAPON_BAT", "WEAPON_CROWBAR", "WEAPON_MACHETE", "WEAPON_PISTOL", "WEAPON_COMBATPISTOL", "WEAPON_APPISTOL",
            "WEAPON_PISTOL50", "WEAPON_REVOLVER", "WEAPON_MICROSMG", "WEAPON_SMG", "WEAPON_ASSAULTSMG", "WEAPON_COMBATPDW", "WEAPON_MG",
            "WEAPON_COMBATMG", "WEAPON_ASSAULTRIFLE", "WEAPON_CARBINERIFLE", "WEAPON_ADVANCEDRIFLE", "WEAPON_SPECIALCARBINE",
            "WEAPON_BULLPUPRIFLE", "WEAPON_PUMPSHOTGUN", "WEAPON_SAWNOFFSHOTGUN", "WEAPON_ASSAULTSHOTGUN", "WEAPON_HEAVYSHOTGUN",
            "WEAPON_SNIPERRIFLE", "WEAPON_HEAVYSNIPER", "WEAPON_MARKSMANRIFLE", "WEAPON_GRENADELAUNCHER", "WEAPON_RPG", "WEAPON_MINIGUN",
            "WEAPON_HOMINGLAUNCHER", "WEAPON_GRENADE", "WEAPON_STICKYBOMB", "WEAPON_MOLOTOV", "WEAPON_PROXMINE", "WEAPON_PETROLCAN", "GADGET_PARACHUTE" };
        static readonly string[] RainCars = { "panto", "blista", "issi2", "asea", "emperor", "buccaneer", "voodoo2", "sultan", "futo", "baller", "bison",
            "mule", "taxi", "police", "ambulance", "bus", "trash", "tornado", "zentorno", "adder", "infernus", "dune", "bfinjection", "rhapsody" };
        static readonly float[][] TeleportSpots = {
            new float[] { 501.6f, 5604.5f, 797.9f }, new float[] { -75.0f, -818.0f, 326.2f }, new float[] { -1336.0f, -3044.0f, 13.9f },
            new float[] { 711.0f, 1198.0f, 348.5f }, new float[] { 1747.0f, 3273.0f, 41.1f }, new float[] { -275.5f, 6635.5f, 7.4f },
            new float[] { -1850.0f, -1231.0f, 13.0f }, new float[] { 195.0f, -934.0f, 30.7f }, new float[] { 105.0f, -1940.0f, 20.8f },
            new float[] { -1600.0f, -1040.0f, 13.0f }, new float[] { -425.0f, 1123.0f, 325.9f }, new float[] { 1110.0f, -640.0f, 56.8f },
            new float[] { 2747.0f, 1522.0f, 24.5f }, new float[] { -2046.0f, 3131.0f, 32.8f } };

        public TikArenaMain()
        {
            string baseDir = BaseDirectory;
            if (string.IsNullOrEmpty(baseDir)) baseDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scripts");
            U.Dir = baseDir;
            U.DataDir = Path.Combine(baseDir, "TikArena");
            try
            {
                Directory.CreateDirectory(U.DataDir);
                Directory.CreateDirectory(Path.Combine(U.DataDir, "images"));
                Directory.CreateDirectory(Path.Combine(U.DataDir, "sounds"));
                Directory.CreateDirectory(Path.Combine(U.DataDir, "cache"));
            }
            catch { }
            LoadConfig();
            avatars.Circle = !string.Equals(cfg.AvatarShape, "Square", StringComparison.OrdinalIgnoreCase);
            avatars.Start(Path.Combine(U.DataDir, "cache"));
            Interval = 0;
            Tick += OnTick;
            KeyDown += OnKeyDown;
            Aborted += OnAborted;
        }

        // ================================================================ config
        string FindIni()
        {
            string a = Path.Combine(U.Dir, "TikArena.ini");
            if (File.Exists(a)) return a;
            string b = Path.Combine(U.Dir, "TikArena.ini.txt");
            if (File.Exists(b)) return b;
            return a;
        }

        void LoadConfig()
        {
            iniPath = FindIni();
            Ini ini = Ini.Load(iniPath);
            cfg = Cfg.Load(ini);
            try { iniTime = File.Exists(iniPath) ? File.GetLastWriteTime(iniPath) : DateTime.MinValue; } catch { }
            U.DebugLog = cfg.DebugLog;
            style = cfg.HudStyle;
            fontName = cfg.HudFont;
            vertical = string.Equals(cfg.LayoutMode, "Vertical", StringComparison.OrdinalIgnoreCase);
            guideOrder.Clear();
            appliedWeather = null;
            tcApplied = false;
            clockLocked = false;
            U.Log("config loaded: " + iniPath + " interactions=" + cfg.Interactions.Count);
        }

        void FileWatch()
        {
            if (U.Now < nextFileCheck) return;
            nextFileCheck = U.Now + 2000;
            try
            {
                if (cfg.AutoImport)
                {
                    string dl = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                    if (Directory.Exists(dl))
                    {
                        string best = null;
                        DateTime bestT = DateTime.MinValue;
                        foreach (string f in Directory.GetFiles(dl, "TikArena*"))
                        {
                            string lf = f.ToLowerInvariant();
                            if (!(lf.EndsWith(".ini") || lf.EndsWith(".ini.txt"))) continue;
                            DateTime t = File.GetLastWriteTime(f);
                            if (t > bestT) { bestT = t; best = f; }
                        }
                        if (best != null && bestT > lastImport && bestT > iniTime)
                        {
                            lastImport = bestT;
                            string target = Path.Combine(U.Dir, "TikArena.ini");
                            File.Copy(best, target, true);
                            File.SetLastWriteTime(target, DateTime.Now);
                            LoadConfig();
                            Status(cfg.Tx("ImportedText", "Imported"));
                            return;
                        }
                    }
                }
                if (cfg.AutoReload)
                {
                    string p = FindIni();
                    DateTime t = File.Exists(p) ? File.GetLastWriteTime(p) : DateTime.MinValue;
                    if (p != iniPath || t != iniTime)
                    {
                        LoadConfig();
                        Status(cfg.Tx("ReloadedText", "Reloaded"));
                    }
                }
            }
            catch (Exception ex) { U.Error("FileWatch", ex); }
        }

        void Status(string msg)
        {
            if (!cfg.ShowStatusMessages || string.IsNullOrEmpty(msg)) return;
            try { Screen.ShowSubtitle("~g~TikArena~s~  " + msg, 2500); } catch { }
        }

        // ================================================================ main loop
        void OnTick(object sender, EventArgs e)
        {
            long now = U.Now;
            float dt = Math.Min(250f, Math.Max(0f, now - lastTickNow));
            lastTickNow = now;
            if (first)
            {
                first = false;
                if (cfg.ShowLoadMessage)
                {
                    try { Notification.Show("~g~TikArena~s~ " + cfg.Tx("LoadedText", "Ready. Press") + " ~y~" + cfg.PowerKey); } catch { }
                }
            }
            FileWatch();
            if (!powered) return;

            try { ProcessInbox(); } catch (Exception ex) { U.Error("Inbox", ex); }
            try { WorldRules(); } catch (Exception ex) { U.Error("World", ex); }
            try { UpdateRound(dt); } catch (Exception ex) { U.Error("Round", ex); }
            try { UpdateQueues(); } catch (Exception ex) { U.Error("Queue", ex); }
            try { UpdateTracked(); } catch (Exception ex) { U.Error("Tracked", ex); }
            try { UpdateEffects(dt); } catch (Exception ex) { U.Error("Effects", ex); }
            try { UpdateCamera(dt); } catch (Exception ex) { U.Error("Camera", ex); }
            try { UpdateCelebration(); } catch (Exception ex) { U.Error("Death", ex); }
            try { UpdateHypeChecks(); } catch (Exception ex) { U.Error("Hype", ex); }
            try { DrawHud(); } catch (Exception ex) { U.Error("Hud", ex); }
        }

        void OnKeyDown(object sender, KeyEventArgs e)
        {
            try
            {
                Keys k = e.KeyCode;
                if (k == Keys.None) return;
                if (k == cfg.PowerKey) { TogglePower(); return; }
                if (!powered) return;
                if (k == cfg.StartKey) { StartSession(); return; }
                if (k == cfg.HudStyleKey) { style = Cycle(cfg.StyleCycle, style); Status("HUD: " + style); return; }
                if (k == cfg.HudFontKey) { fontName = Cycle(cfg.FontCycle, fontName); Status("Font: " + fontName); return; }
                if (k == cfg.LayoutKey) { vertical = !vertical; Status(vertical ? "TikTok 9:16" : "16:9"); return; }
                if (k == cfg.PauseKey)
                {
                    paused = !paused;
                    Status(paused ? cfg.Tx("PausedText", "Paused") : cfg.Tx("ResumedText", "Resumed"));
                    return;
                }
                if (k == cfg.EmergencyKey) { Emergency(); return; }
                if (k == cfg.HypeTestKey)
                {
                    Supporter t = GetSup("test:" + cfg.TestUserName, cfg.TestUserName, null, 25);
                    PushHype(U.Fill(cfg.ComboText, t.Nick, cfg.ComboMin), t);
                    return;
                }
                if (cfg.TestKeysEnabled)
                {
                    foreach (Interaction it in cfg.Interactions)
                    {
                        if (it.TestKey == k && it.Enabled) FireTest(it);
                    }
                }
            }
            catch (Exception ex) { U.Error("Key", ex); }
        }

        void OnAborted(object sender, EventArgs e)
        {
            try { live.Stop(); avatars.Stop(); if (powered) Cleanup(); } catch { }
        }

        static string Cycle(List<string> list, string cur)
        {
            if (list == null || list.Count == 0) return cur;
            int i = -1;
            for (int j = 0; j < list.Count; j++) if (string.Equals(list[j], cur, StringComparison.OrdinalIgnoreCase)) i = j;
            return list[(i + 1) % list.Count];
        }

        // ================================================================ power / session
        void TogglePower()
        {
            if (!powered)
            {
                powered = true;
                SetupRelationships();
                PreloadFx();
                Status("ON");
            }
            else
            {
                if (cfg.CleanupOnPowerOff) Cleanup();
                else RestoreWorld();
                live.Stop();
                powered = false;
                started = false;
                phase = Phase.Idle;
                Status("OFF");
            }
        }

        void StartSession()
        {
            started = true;
            if (cfg.LiveEnabled) live.Start(cfg.LiveUrl, cfg.ReconnectSeconds);
            StartRound();
            Status(cfg.Tx("StartedText", "Started"));
        }

        void StartRound()
        {
            roundMs = 0;
            roundDurMs = cfg.DurationMinutes * 60000.0;
            mvp = null;
            foreach (Supporter s in sups.Values) s.RoundHelpCoins = 0;
            phase = cfg.ChallengeEnabled ? Phase.Running : Phase.Idle;
            Ped pl = Game.Player.Character;
            if (cfg.ApplyOnRoundStart && pl.Exists() && !pl.IsDead)
            {
                SetPlayerHealth(cfg.PlayerHealth, true);
                pl.Armor = cfg.PlayerArmor;
            }
            if (pl.Exists()) Function.Call(Hash.CLEAR_ENTITY_LAST_DAMAGE_ENTITY, pl);
        }

        void SetPlayerHealth(int points, bool fill)
        {
            Ped pl = Game.Player.Character;
            int max = points + 100;
            Function.Call(Hash.SET_PED_MAX_HEALTH, pl, max);
            Function.Call(Hash.SET_ENTITY_MAX_HEALTH, pl, max);
            if (fill) Function.Call(Hash.SET_ENTITY_HEALTH, pl, max, 0);
        }

        void Emergency()
        {
            foreach (Tracked t in tracked) DeleteTracked(t);
            tracked.Clear();
            foreach (TempVehicle v in tempVehicles) SafeDeleteVehicle(v.Veh);
            tempVehicles.Clear();
            spawnQ.Clear();
            instantQ.Clear();
            Status(cfg.Tx("EmergencyText", "Cleared"));
        }

        void Cleanup()
        {
            EndCelebration();
            StopCam();
            foreach (Tracked t in tracked) DeleteTracked(t);
            tracked.Clear();
            foreach (TempVehicle v in tempVehicles) SafeDeleteVehicle(v.Veh);
            tempVehicles.Clear();
            spawnQ.Clear();
            instantQ.Clear();
            notifs.Clear(); feed.Clear(); hypes.Clear();
            RestoreWorld();
        }

        void RestoreWorld()
        {
            Ped pl = Game.Player.Character;
            fxEnd.Clear();
            Function.Call(Hash.SET_TIME_SCALE, 1f);
            lastTimeScale = 1;
            Function.Call(Hash.SET_GRAVITY_LEVEL, 0);
            Function.Call(Hash.SET_ARTIFICIAL_LIGHTS_STATE, false);
            Function.Call(Hash.CLEAR_OVERRIDE_WEATHER);
            Function.Call(Hash.CLEAR_WEATHER_TYPE_PERSIST);
            Function.Call(Hash.CLEAR_TIMECYCLE_MODIFIER);
            Function.Call(Hash.PAUSE_CLOCK, false);
            Function.Call(Hash.DISPLAY_RADAR, true);
            Function.Call(Hash.SET_MAX_WANTED_LEVEL, 5);
            Function.Call(Hash.SET_POLICE_IGNORE_PLAYER, Game.Player, false);
            Function.Call(Hash.SET_RUN_SPRINT_MULTIPLIER_FOR_PLAYER, Game.Player, 1f);
            Function.Call(Hash.SET_SWIM_MULTIPLIER_FOR_PLAYER, Game.Player, 1f);
            Function.Call(Hash.STOP_GAMEPLAY_CAM_SHAKING, true);
            Function.Call(Hash.PAUSE_DEATH_ARREST_RESTART, false);
            if (pl.Exists())
            {
                pl.IsInvincible = false;
                Function.Call(Hash.FREEZE_ENTITY_POSITION, pl, false);
                Function.Call(Hash.RESET_PED_MOVEMENT_CLIPSET, pl, 0f);
                Function.Call(Hash.SET_PED_MOVE_RATE_OVERRIDE, pl, 1f);
                if (pl.IsInVehicle()) Function.Call(Hash.FREEZE_ENTITY_POSITION, pl.CurrentVehicle, false);
            }
            ClearRelationships();
            shaking = drunkClip = speedOn = freezeOn = gravOn = blackoutOn = false;
            radarHidden = clockLocked = tcApplied = wantedApplied = false;
            appliedWeather = null;
            weatherOverride = "";
            killSlowEnd = 0;
            paused = false;
        }

        // ================================================================ relationships / world rules
        void SetupRelationships()
        {
            relEnemy = World.AddRelationshipGroup("TIKARENA_ENEMY").Hash;
            relAlly = World.AddRelationshipGroup("TIKARENA_ALLY").Hash;
            relPlayer = U.HashInt("PLAYER");
            ApplyRelationships();
        }

        void Rel(int r, int a, int b)
        {
            Function.Call(Hash.SET_RELATIONSHIP_BETWEEN_GROUPS, r, a, b);
            Function.Call(Hash.SET_RELATIONSHIP_BETWEEN_GROUPS, r, b, a);
        }

        void ApplyRelationships()
        {
            Rel(5, relEnemy, relPlayer);
            Rel(5, relEnemy, relAlly);
            Rel(0, relAlly, relPlayer);
            Rel(0, relEnemy, relEnemy);
            Rel(0, relAlly, relAlly);
            if (cfg.PoliceIgnore)
                foreach (string g in OtherGroups) { int h = U.HashInt(g); Rel(1, h, relEnemy); Rel(1, h, relAlly); Rel(1, h, relPlayer); }
            if (cfg.GangsIgnore)
                foreach (string g in GangGroups) { int h = U.HashInt(g); Rel(1, h, relEnemy); Rel(1, h, relAlly); Rel(1, h, relPlayer); }
        }

        void ClearRelationships()
        {
            if (relEnemy == 0) return;
            List<string> all = new List<string>(OtherGroups);
            all.AddRange(GangGroups);
            foreach (string g in all)
            {
                int h = U.HashInt(g);
                foreach (int o in new int[] { relEnemy, relAlly, relPlayer })
                {
                    Function.Call(Hash.CLEAR_RELATIONSHIP_BETWEEN_GROUPS, 1, h, o);
                    Function.Call(Hash.CLEAR_RELATIONSHIP_BETWEEN_GROUPS, 1, o, h);
                }
            }
        }

        void WorldRules()
        {
            Ped pl = Game.Player.Character;
            long now = U.Now;
            bool second = now >= nextSecond;
            if (second) nextSecond = now + 1000;

            if (second)
            {
                if (cfg.PoliceIgnore)
                {
                    Function.Call(Hash.SET_MAX_WANTED_LEVEL, 0);
                    if (Game.Player.WantedLevel > 0) Function.Call(Hash.CLEAR_PLAYER_WANTED_LEVEL, Game.Player);
                    Function.Call(Hash.SET_POLICE_IGNORE_PLAYER, Game.Player, true);
                    wantedApplied = true;
                }
                else if (wantedApplied)
                {
                    Function.Call(Hash.SET_MAX_WANTED_LEVEL, 5);
                    Function.Call(Hash.SET_POLICE_IGNORE_PLAYER, Game.Player, false);
                    wantedApplied = false;
                }
                ApplyRelationships();

                if (cfg.LockTime)
                {
                    Function.Call(Hash.SET_CLOCK_TIME, cfg.Hour, cfg.Minute, 0);
                    Function.Call(Hash.PAUSE_CLOCK, true);
                    clockLocked = true;
                }
                else if (clockLocked) { Function.Call(Hash.PAUSE_CLOCK, false); clockLocked = false; }

                string want = "";
                if (Active("Storm")) want = "THUNDER";
                else if (Active("Weather")) want = weatherOverride;
                else if (cfg.LockWeather) want = cfg.Weather.ToUpperInvariant();
                if (want != appliedWeather)
                {
                    if (want.Length > 0)
                    {
                        Function.Call(Hash.SET_WEATHER_TYPE_NOW_PERSIST, want);
                        Function.Call(Hash.SET_OVERRIDE_WEATHER, want);
                    }
                    else
                    {
                        Function.Call(Hash.CLEAR_OVERRIDE_WEATHER);
                        Function.Call(Hash.CLEAR_WEATHER_TYPE_PERSIST);
                    }
                    appliedWeather = want;
                }

                if (cfg.TimecycleEnabled)
                {
                    if (!tcApplied)
                    {
                        Function.Call(Hash.SET_TIMECYCLE_MODIFIER, cfg.Timecycle);
                        Function.Call(Hash.SET_TIMECYCLE_MODIFIER_STRENGTH, cfg.TimecycleStrength);
                        tcApplied = true;
                    }
                }
                else if (tcApplied) { Function.Call(Hash.CLEAR_TIMECYCLE_MODIFIER); tcApplied = false; }
            }

            if (cfg.HideRadar) { Function.Call(Hash.DISPLAY_RADAR, false); radarHidden = true; }
            else if (radarHidden) { Function.Call(Hash.DISPLAY_RADAR, true); radarHidden = false; }

            if (cfg.DensityEnabled)
            {
                Function.Call(Hash.SET_PED_DENSITY_MULTIPLIER_THIS_FRAME, cfg.PedDensity);
                Function.Call(Hash.SET_SCENARIO_PED_DENSITY_MULTIPLIER_THIS_FRAME, cfg.PedDensity, cfg.PedDensity);
                Function.Call(Hash.SET_VEHICLE_DENSITY_MULTIPLIER_THIS_FRAME, cfg.VehicleDensity);
                Function.Call(Hash.SET_RANDOM_VEHICLE_DENSITY_MULTIPLIER_THIS_FRAME, cfg.VehicleDensity);
                Function.Call(Hash.SET_PARKED_VEHICLE_DENSITY_MULTIPLIER_THIS_FRAME, cfg.VehicleDensity);
            }

            // invincibility: settings, god mode, cinematic cameras
            if (pl.Exists() && !pl.IsDead)
            {
                bool inv = cfg.PlayerInvincible || Active("GodMode") || (cfg.InvincibleDuringCam && camMode.Length > 0);
                if (pl.IsInvincible != inv) pl.IsInvincible = inv;
            }
        }

        // ================================================================ TikFinity events
        void ProcessInbox()
        {
            string raw;
            int n = 0;
            while (n++ < 60 && live.Inbox.TryDequeue(out raw))
            {
                try { HandleMessage(raw); }
                catch (Exception ex) { U.Error("Message", ex); }
            }
        }

        void HandleMessage(string raw)
        {
            object root = Json.Parse(raw);
            if (root == null) return;
            Dictionary<string, object> d = root as Dictionary<string, object>;
            string ev = null;
            object data = root;
            if (d != null)
            {
                object v;
                if (d.TryGetValue("event", out v) && v is string) ev = (string)v;
                else if (d.TryGetValue("type", out v) && v is string) ev = (string)v;
                if (d.TryGetValue("data", out v) && v != null) data = v;
            }
            if (ev == null) return;
            ev = ev.ToLowerInvariant();
            U.Log("event " + ev);

            LiveEvent e = new LiveEvent();
            e.UserKey = Json.FindStr(data, "uniqueId", "userId", "username", "secUid") ?? "";
            e.Nick = Json.FindStr(data, "nickname", "displayName", "uniqueId") ?? e.UserKey;
            e.Avatar = Json.FindAvatar(data);
            double lv = Json.FindNum(data, "gifterLevel", "level", "payGrade", "userLevel");
            e.Level = double.IsNaN(lv) ? 0 : (int)lv;
            if (e.UserKey.Length == 0) e.UserKey = e.Nick;
            if (e.UserKey.Length == 0) e.UserKey = "unknown";

            switch (ev)
            {
                case "gift":
                    e.Type = "gift";
                    e.GiftName = Json.FindStr(data, "giftName", "name") ?? "";
                    e.GiftId = Json.FindStr(data, "giftId", "id") ?? "";
                    double rc = Json.FindNum(data, "repeatCount", "count");
                    e.RepeatCount = double.IsNaN(rc) ? 1 : Math.Max(1, (int)rc);
                    e.RepeatEnd = Json.FindBool(data, true, "repeatEnd");
                    double gt = Json.FindNum(data, "giftType");
                    e.GiftType = double.IsNaN(gt) ? 0 : (int)gt;
                    double dc = Json.FindNum(data, "diamondCount", "diamonds", "coins");
                    e.Diamonds = double.IsNaN(dc) ? 0 : (int)dc;
                    break;
                case "like":
                    e.Type = "like";
                    double lc = Json.FindNum(data, "likeCount", "count");
                    e.Likes = double.IsNaN(lc) ? 1 : Math.Max(1, (int)lc);
                    break;
                case "chat":
                case "comment":
                    e.Type = "chat";
                    e.Comment = Json.FindStr(data, "comment", "text", "message") ?? "";
                    break;
                case "follow":
                    e.Type = "follow"; break;
                case "share":
                    e.Type = "share"; break;
                case "subscribe":
                    e.Type = "subscribe"; break;
                case "social":
                    string dt = (Json.FindStr(data, "displayType", "label") ?? "").ToLowerInvariant();
                    if (dt.Contains("follow")) e.Type = "follow";
                    else if (dt.Contains("share")) e.Type = "share";
                    else return;
                    break;
                case "member":
                case "join":
                    e.Type = "join"; break;
                default:
                    return;
            }
            HandleEvent(e);
        }

        Supporter GetSup(string key, string nick, string avatar, int level)
        {
            Supporter s;
            if (!sups.TryGetValue(key, out s))
            {
                s = new Supporter();
                s.Key = key;
                sups[key] = s;
            }
            if (!string.IsNullOrEmpty(nick)) s.Nick = nick;
            if (string.IsNullOrEmpty(s.Nick)) s.Nick = key;
            if (!string.IsNullOrEmpty(avatar)) s.AvatarUrl = avatar;
            if (level > s.Level) s.Level = level;
            return s;
        }

        string Avatar(Supporter s)
        {
            if (s == null) return avatars.DefaultFile;
            return avatars.Get(s.Key, s.AvatarUrl);
        }

        void HandleEvent(LiveEvent e)
        {
            long now = U.Now;
            Supporter s = GetSup(e.UserKey, e.Nick, e.Avatar, e.Level);
            s.LastActive = now;
            Avatar(s); // start download early

            if (e.Type == "join") { HypeJoin(s); return; }

            if (e.Type == "gift")
            {
                string skey = e.UserKey + "|" + (e.GiftId.Length > 0 ? e.GiftId : e.GiftName);
                int delta;
                if (e.GiftType == 1)
                {
                    int last;
                    streaks.TryGetValue(skey, out last);
                    delta = e.RepeatCount >= last ? e.RepeatCount - last : e.RepeatCount;
                    if (e.RepeatEnd) streaks.Remove(skey); else streaks[skey] = e.RepeatCount;
                    if (cfg.HypeEnabled && cfg.ComboEnabled && e.RepeatCount >= cfg.ComboMin && !comboFired.Contains(skey))
                    {
                        comboFired.Add(skey);
                        PushHype(U.Fill(cfg.ComboText, s.Nick, e.RepeatCount), s);
                    }
                    if (e.RepeatEnd) comboFired.Remove(skey);
                }
                else
                {
                    delta = Math.Max(1, e.RepeatCount);
                    if (cfg.HypeEnabled && cfg.ComboEnabled && delta >= cfg.ComboMin)
                        PushHype(U.Fill(cfg.ComboText, s.Nick, delta), s);
                }
                if (delta <= 0) return;
                e.Count = delta;
                e.Coins = (long)e.Diamonds * delta;
                s.Coins += e.Coins;
                s.LastGift = now;
                s.ComebackSent = false;
                LearnLog(e);
                CheckNewKing();
            }

            foreach (Interaction it in cfg.Interactions)
            {
                int units = Match(it, e, s);
                if (units > 0) Trigger(it, s, units, e.Coins, e.Count, false);
            }
        }

        int Match(Interaction it, LiveEvent e, Supporter s)
        {
            if (!it.Enabled) return 0;
            string tr = it.Trigger.ToLowerInvariant();
            int units = 0;
            switch (tr)
            {
                case "gift":
                    if (e.Type != "gift") return 0;
                    bool byName = it.GiftName.Length > 0 && string.Equals(it.GiftName.Trim(), e.GiftName.Trim(), StringComparison.OrdinalIgnoreCase);
                    bool byId = it.GiftId.Length > 0 && it.GiftId.Trim() == e.GiftId.Trim();
                    if (!byName && !byId) return 0;
                    units = it.Units * (it.MultiplyByCombo ? e.Count : 1);
                    break;
                case "likes":
                case "like":
                    if (e.Type != "like") return 0;
                    int acc;
                    it.LikeAcc.TryGetValue(s.Key, out acc);
                    acc += e.Likes;
                    int times = acc / it.LikesCount;
                    it.LikeAcc[s.Key] = acc % it.LikesCount;
                    if (times <= 0) return 0;
                    units = it.Units * (it.MultiplyByCombo ? times : 1);
                    break;
                case "comment":
                case "chat":
                    if (e.Type != "chat") return 0;
                    if (it.CommentText.Length > 0 && e.Comment.IndexOf(it.CommentText, StringComparison.OrdinalIgnoreCase) < 0) return 0;
                    units = it.Units;
                    break;
                case "follow":
                case "share":
                case "subscribe":
                    if (e.Type != tr) return 0;
                    units = it.Units;
                    break;
                default:
                    return 0;
            }
            if (it.OncePerUser)
            {
                if (it.OnceUsers.Contains(s.Key)) return 0;
                it.OnceUsers.Add(s.Key);
            }
            if (cfg.MaxPerEvent > 0 && units > cfg.MaxPerEvent) units = cfg.MaxPerEvent;
            return units;
        }

        void FireTest(Interaction it)
        {
            Supporter t = GetSup("test:" + cfg.TestUserName, cfg.TestUserName, null, 0);
            t.LastActive = U.Now;
            Trigger(it, t, it.Units, 0, 1, true);
        }

        void Trigger(Interaction it, Supporter s, int units, long coins, int count, bool test)
        {
            if (units <= 0) return;
            Job j = new Job();
            j.It = it;
            j.Sup = s;
            j.Left = units;
            j.Coins = coins;
            if (IsSpawn(it.Action)) spawnQ.Add(j); else instantQ.Add(j);

            if (IsHelp(it.Action)) s.RoundHelpCoins += Math.Max(coins, 1);
            if (IsEnemyAction(it.Action)) { lastEnemySup = s; lastEnemyAt = U.Now; }
            else if (IsRivalHelp(it.Action)) CheckRivalry(s);

            if (cfg.NotifEnabled && it.Message.Length > 0)
            {
                FeedItem f = new FeedItem();
                f.Text = U.Fill(it.Message, s.Nick, units);
                f.Avatar = cfg.NotifAvatar ? Avatar(s) : null;
                f.Icon = cfg.NotifGiftIcon ? ImgPath(it.GiftImage) : null;
                f.Start = U.Now;
                f.End = U.Now + (long)(cfg.NotifSeconds * 1000);
                f.Col = cfg.TextColor;
                notifs.Add(f);
                while (notifs.Count > cfg.NotifMax) notifs.RemoveAt(0);
            }
            if (IsHelp(it.Action) && !IsSpawn(it.Action)) Snd(cfg.SndHelp);
        }

        void LearnLog(LiveEvent e)
        {
            if (!cfg.LearnMode) return;
            string k = e.GiftName + "|" + e.GiftId;
            if (learned.Contains(k)) return;
            learned.Add(k);
            try
            {
                File.AppendAllText(Path.Combine(U.DataDir, "gifts-log.txt"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm") + " | " + e.GiftName + " | id=" + e.GiftId + " | coins=" + e.Diamonds + " | user=" + e.Nick + "\r\n",
                    Encoding.UTF8);
            }
            catch { }
        }

        static bool In(string[] arr, string a)
        {
            foreach (string x in arr) if (string.Equals(x, a, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static bool IsHelp(string a) { return In(HelpActions, a); }
        static bool IsSpawn(string a) { return In(SpawnActions, a); }
        static bool IsAllySpawn(string a) { return a == "SpawnAlly" || a == "Clone"; }
        static bool IsEnemyAction(string a) { return a == "SpawnEnemy" || a == "MafiaCar" || a == "MotoHitman" || a == "Animals"; }
        static bool IsRivalHelp(string a) { return a == "SpawnAlly" || a == "Clone" || a == "Heal" || a == "AddHealth"; }

        string ImgPath(string rel)
        {
            if (string.IsNullOrEmpty(rel)) return null;
            if (Path.IsPathRooted(rel)) return rel;
            return Path.Combine(U.DataDir, rel.Replace('/', Path.DirectorySeparatorChar));
        }

        // ================================================================ hype
        bool HypeReady(string key, float seconds)
        {
            long last;
            if (hypeLast.TryGetValue(key, out last) && U.Now - last < seconds * 1000) return false;
            hypeLast[key] = U.Now;
            return true;
        }

        void PushHype(string text, Supporter s)
        {
            if (!cfg.HypeEnabled || string.IsNullOrEmpty(text)) return;
            FeedItem f = new FeedItem();
            f.Text = text.Replace("{level}", s != null ? s.Level.ToString(U.IC) : "0");
            f.Avatar = Avatar(s);
            f.Level = s != null ? s.Level : 0;
            f.Start = U.Now;
            f.End = U.Now + (long)(cfg.HypeSeconds * 1000);
            f.Col = cfg.HypeColor;
            hypes.Add(f);
            while (hypes.Count > cfg.HypeMax) hypes.RemoveAt(0);
            Snd(cfg.SndHype);
        }

        List<Supporter> TopList(int n)
        {
            List<Supporter> l = new List<Supporter>();
            foreach (Supporter s in sups.Values) if (s.Coins > 0) l.Add(s);
            l.Sort(delegate(Supporter a, Supporter b) { return b.Coins.CompareTo(a.Coins); });
            if (l.Count > n) l.RemoveRange(n, l.Count - n);
            return l;
        }

        void CheckNewKing()
        {
            List<Supporter> top = TopList(1);
            if (top.Count == 0) return;
            Supporter k = top[0];
            if (k != king)
            {
                Supporter prev = king;
                king = k;
                if (prev != null && cfg.NewKingEnabled && k.Coins >= cfg.NewKingMinCoins && HypeReady("newking:" + k.Key, 30))
                    PushHype(U.Fill(cfg.NewKingText, k.Nick, 0), k);
            }
        }

        void HypeJoin(Supporter s)
        {
            if (!cfg.HypeEnabled) return;
            if (cfg.KingJoinEnabled && king == s && s.Coins > 0)
            {
                if (HypeReady("join:" + s.Key, 120)) PushHype(U.Fill(cfg.KingJoinText, s.Nick, 0), s);
                return;
            }
            if (cfg.VipJoinEnabled && (s.Level >= cfg.VipJoinMinLevel || s.Coins >= cfg.VipJoinMinCoins))
            {
                if (HypeReady("join:" + s.Key, 120)) PushHype(U.Fill(cfg.VipJoinText, s.Nick, 0), s);
            }
        }

        void CheckRivalry(Supporter helper)
        {
            if (!cfg.HypeEnabled || !cfg.RivalryEnabled || lastEnemySup == null || lastEnemySup == helper) return;
            if (U.Now - lastEnemyAt > cfg.RivalrySeconds * 1000) return;
            if (!HypeReady("rival:" + helper.Key + ":" + lastEnemySup.Key, 60)) return;
            PushHype(U.Fill(cfg.RivalryText, helper.Nick, 0).Replace("{rival}", lastEnemySup.Nick), helper);
        }

        void UpdateHypeChecks()
        {
            long now = U.Now;
            if (now < nextComebackCheck) return;
            nextComebackCheck = now + 5000;
            if (!cfg.HypeEnabled || !cfg.ComebackEnabled) return;
            foreach (Supporter s in sups.Values)
            {
                if (s.ComebackSent || s.Coins < cfg.ComebackMinCoins) continue;
                if (now - s.LastGift < cfg.ComebackMinutes * 60000) continue;
                if (now - s.LastActive > 120000) continue;
                s.ComebackSent = true;
                PushHype(U.Fill(cfg.ComebackText, s.Nick, 0), s);
                break;
            }
        }

        // ================================================================ queues
        int AliveCount(bool enemy)
        {
            int n = 0;
            foreach (Tracked t in tracked) if (t.Enemy == enemy && t.DeadAt == 0) n++;
            return n;
        }

        int QueueCount()
        {
            int n = 0;
            foreach (Job j in spawnQ) n += j.Left;
            foreach (Job j in instantQ) n += j.Left;
            return n;
        }

        string ActionOf(Job j) { return j.Random ? j.RandomAction : j.It.Action; }

        int SlotsFor(string action)
        {
            if (action == "MafiaCar") return 4;
            if (action == "MotoHitman") return 2;
            return 1;
        }

        bool CooldownReady(Interaction it)
        {
            return it.Cooldown <= 0 || U.Now - it.LastFire >= it.Cooldown * 1000;
        }

        bool PlayerBusy()
        {
            Ped pl = Game.Player.Character;
            return !pl.Exists() || pl.IsDead || celeb || Function.Call<bool>(Hash.IS_PLAYER_DEAD, Game.Player);
        }

        void UpdateQueues()
        {
            if (paused || PlayerBusy()) return;
            long now = U.Now;

            if (now >= nextSpawn && spawnQ.Count > 0)
            {
                bool enemyBlocked = false, allyBlocked = false;
                for (int i = 0; i < spawnQ.Count; i++)
                {
                    Job j = spawnQ[i];
                    string a = ActionOf(j);
                    bool ally = IsAllySpawn(a);
                    if (ally ? allyBlocked : enemyBlocked) continue;
                    if (!j.Random && !CooldownReady(j.It)) continue;
                    int alive = AliveCount(!ally);
                    int max = ally ? cfg.MaxAllies : cfg.MaxEnemies;
                    int need = Math.Min(SlotsFor(a), max);
                    if (alive + need > max)
                    {
                        if (ally) allyBlocked = true; else enemyBlocked = true;
                        continue;
                    }
                    ExecuteUnit(j);
                    j.It.LastFire = now;
                    j.Left--;
                    if (j.Left <= 0) spawnQ.RemoveAt(i);
                    nextSpawn = now + cfg.DelayMs;
                    break;
                }
            }

            if (now >= nextInstant && instantQ.Count > 0)
            {
                for (int i = 0; i < instantQ.Count; i++)
                {
                    Job j = instantQ[i];
                    if (!CooldownReady(j.It)) continue;
                    ExecuteUnit(j);
                    j.It.LastFire = now;
                    j.Left--;
                    if (j.Left <= 0) instantQ.Remove(j);
                    nextInstant = now + cfg.DelayMs;
                    break;
                }
            }
        }

        // Interaction built from the [Random] section for a given action.
        Interaction RandomIt(string action, Interaction src)
        {
            Interaction r = new Interaction();
            r.Index = src.Index;
            r.Name = src.Name;
            r.Action = action;
            r.Effect = cfg.RndEffect;
            r.Duration = cfg.RndDuration;
            r.Amount = cfg.RndHealth;
            r.VehicleModel = cfg.RndVehicle;
            r.Weapon = cfg.RndWeapon;
            r.Blip = true;
            r.SpawnDistance = 20;
            r.Weather = "Random";
            r.GiftImage = src.GiftImage;
            r.ActionImage = src.ActionImage;
            r.EntryCam = src.EntryCam;
            if (action == "SpawnAlly")
            {
                r.Model = cfg.RndAllyModel; r.Weapon = cfg.RndAllyWeapon; r.Health = cfg.RndAllyHealth;
                r.Accuracy = 50; r.CombatAbility = 2; r.CombatMovement = 2;
            }
            else if (action == "SpawnEnemy")
            {
                r.Model = cfg.RndEnemyModel; r.Weapon = cfg.RndEnemyWeapon; r.Health = cfg.RndEnemyHealth;
            }
            else if (action == "Animals")
            {
                r.Model = "a_c_mtlion"; r.Health = 200;
            }
            else if (action == "MafiaCar")
            {
                r.Model = "g_m_m_chicold_01"; r.Weapon = "WEAPON_MICROSMG"; r.Health = 150; r.VehicleModel = "schafter2"; r.SpawnDistance = 80;
            }
            else if (action == "MotoHitman")
            {
                r.Model = "g_m_y_lost_01"; r.Weapon = "WEAPON_MICROSMG"; r.Health = 150; r.VehicleModel = "bati"; r.SpawnDistance = 70;
            }
            else if (action == "Clone")
            {
                r.Weapon = cfg.RndAllyWeapon; r.Health = cfg.RndAllyHealth; r.Accuracy = 50; r.CombatAbility = 2;
            }
            return r;
        }

        string PickRandomAction()
        {
            int total = 0;
            foreach (RandomEntry e in cfg.RandomList) total += e.Weight;
            if (total <= 0) return null;
            int r = U.Rng.Next(total);
            foreach (RandomEntry e in cfg.RandomList)
            {
                if (r < e.Weight) return e.Action;
                r -= e.Weight;
            }
            return null;
        }

        void ExecuteUnit(Job j)
        {
            Interaction it = j.It;
            string a = it.Action;
            if (j.Random) { a = j.RandomAction; }
            if (a == "Random" && !j.Random)
            {
                string pick = PickRandomAction();
                if (pick == null) return;
                if (IsSpawn(pick))
                {
                    Job n = new Job();
                    n.It = RandomIt(pick, it);
                    n.Sup = j.Sup;
                    n.Left = pick == "Animals" ? cfg.RndAnimals : 1;
                    n.Coins = j.Coins;
                    n.Random = true;
                    n.RandomAction = pick;
                    spawnQ.Add(n);
                    return;
                }
                Job tmp = new Job();
                tmp.It = RandomIt(pick, it);
                tmp.Sup = j.Sup;
                tmp.Coins = j.Coins;
                tmp.Random = true;
                tmp.RandomAction = pick;
                Execute(tmp, pick);
                return;
            }
            Execute(j, a);
        }

        // ================================================================ spawning
        static float HeadingTo(Vector3 from, Vector3 to)
        {
            Vector3 d = to - from;
            return (float)(Math.Atan2(-d.X, d.Y) * 180.0 / Math.PI);
        }

        Vector3 AroundPlayer(float dist)
        {
            Ped pl = Game.Player.Character;
            Vector3 p = pl.Position;
            double ang = U.Rng.NextDouble() * Math.PI * 2;
            Vector3 t = new Vector3(p.X + (float)Math.Cos(ang) * dist, p.Y + (float)Math.Sin(ang) * dist, p.Z);
            OutputArgument oz = new OutputArgument();
            bool ok = Function.Call<bool>(Hash.GET_GROUND_Z_FOR_3D_COORD, t.X, t.Y, p.Z + 40f, oz, false, false);
            if (ok)
            {
                float z = oz.GetResult<float>();
                if (Math.Abs(z - p.Z) < 25f) t.Z = z + 0.5f;
            }
            return t;
        }

        Model LoadModel(string name, string fallback)
        {
            Model m = new Model(string.IsNullOrEmpty(name) ? fallback : name);
            if (!m.IsInCdImage || !m.IsValid) m = new Model(fallback);
            m.Request(2500);
            return m;
        }

        static string DefaultModel(string action)
        {
            switch (action)
            {
                case "SpawnAlly": return "s_m_y_swat_01";
                case "Animals": return "a_c_mtlion";
                case "MafiaCar": return "g_m_m_chicold_01";
                case "MotoHitman": return "g_m_y_lost_01";
                default: return "s_m_y_clown_01";
            }
        }

        void SetupPed(Ped p, Interaction it, bool enemy, bool animal)
        {
            int hp = Math.Max(1, it.Health);
            Function.Call(Hash.SET_PED_MAX_HEALTH, p, hp + 100);
            Function.Call(Hash.SET_ENTITY_MAX_HEALTH, p, hp + 100);
            Function.Call(Hash.SET_ENTITY_HEALTH, p, hp + 100, 0);
            if (it.Armor > 0) Function.Call(Hash.SET_PED_ARMOUR, p, it.Armor);
            Function.Call(Hash.SET_PED_RELATIONSHIP_GROUP_HASH, p, enemy ? relEnemy : relAlly);
            if (!animal && !string.IsNullOrEmpty(it.Weapon) && !it.Weapon.Equals("None", StringComparison.OrdinalIgnoreCase))
            {
                int wh = U.HashInt(it.Weapon);
                Function.Call(Hash.GIVE_WEAPON_TO_PED, p, wh, it.Ammo, false, true);
                Function.Call(Hash.SET_CURRENT_PED_WEAPON, p, wh, true);
            }
            Function.Call(Hash.SET_PED_ACCURACY, p, it.Accuracy);
            Function.Call(Hash.SET_PED_COMBAT_ABILITY, p, it.CombatAbility);
            Function.Call(Hash.SET_PED_COMBAT_MOVEMENT, p, it.CombatMovement);
            Function.Call(Hash.SET_PED_COMBAT_RANGE, p, it.CombatMovement >= 3 ? 0 : 1);
            Function.Call(Hash.SET_PED_SHOOT_RATE, p, it.ShootRate);
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, p, 46, true);
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, p, 5, true);
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, p, 2, true);
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, p, 1, true);
            Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, p, 0, it.CombatMovement < 3);
            Function.Call(Hash.SET_PED_FLEE_ATTRIBUTES, p, 0, false);
            Function.Call(Hash.SET_PED_KEEP_TASK, p, true);
            Function.Call(Hash.SET_PED_DROPS_WEAPONS_WHEN_DEAD, p, false);
            Function.Call(Hash.SET_PED_SUFFERS_CRITICAL_HITS, p, false);
            Function.Call(Hash.SET_PED_SEEING_RANGE, p, 150f);
            Function.Call(Hash.SET_PED_HEARING_RANGE, p, 150f);
            if (enemy) Function.Call(Hash.SET_BLOCKING_OF_NON_TEMPORARY_EVENTS, p, true);
            else Function.Call(Hash.SET_PED_CAN_BE_TARGETTED, p, false);
        }

        void AddBlip(Entity e, bool enemy)
        {
            try
            {
                Blip b = e.AddBlip();
                b.Color = enemy ? BlipColor.Red : BlipColor.Blue;
                b.Scale = 0.75f;
            }
            catch { }
        }

        void TaskPed(Tracked t)
        {
            Ped p = t.Ped;
            Ped pl = Game.Player.Character;
            if (!p.Exists() || p.IsDead || !pl.Exists()) return;
            if (t.Enemy)
            {
                if (p.IsInVehicle() && t.Veh != null && t.Veh.Exists() && t.Veh.Driver == p && t.Veh.IsDriveable)
                {
                    Function.Call(Hash.TASK_VEHICLE_CHASE, p, pl);
                    Function.Call(Hash.SET_DRIVER_ABILITY, p, 1f);
                    Function.Call(Hash.SET_DRIVER_AGGRESSIVENESS, p, 1f);
                    return;
                }
                if (!Function.Call<bool>(Hash.IS_PED_IN_COMBAT, p, pl) || U.Now - t.SpawnAt < 500)
                    Function.Call(Hash.TASK_COMBAT_PED, p, pl, 0, 16);
            }
            else
            {
                if (AliveCount(true) > 0)
                {
                    if (!Function.Call<bool>(Hash.IS_PED_IN_COMBAT, p, 0))
                        Function.Call(Hash.TASK_COMBAT_HATED_TARGETS_AROUND_PED, p, 120f, 0);
                }
                else if (!Function.Call<bool>(Hash.IS_PED_GROUP_MEMBER, p, Function.Call<int>(Hash.GET_PED_GROUP_INDEX, pl)))
                {
                    if (p.Position.DistanceTo(pl.Position) > 8f)
                        Function.Call(Hash.TASK_FOLLOW_TO_OFFSET_OF_ENTITY, p, pl, U.RandF(-3, 3), U.RandF(-4, -1), 0f, 3f, -1, 3f, true);
                }
            }
        }

        Tracked Track(Ped p, Job j, bool enemy, bool animal, Vehicle v, bool leader)
        {
            Tracked t = new Tracked();
            t.Ped = p;
            t.Enemy = enemy;
            t.Sup = j.Sup;
            t.It = j.It;
            t.Veh = v;
            t.Leader = leader;
            t.MaxHp = Math.Max(1, j.It.Health);
            t.SpawnAt = U.Now;
            t.NextTask = U.Now + 300;
            t.Animal = animal;
            tracked.Add(t);
            return t;
        }

        void Execute(Job j, string a)
        {
            Ped pl = Game.Player.Character;
            if (!pl.Exists()) return;
            U.Log("execute " + a + " for " + (j.Sup != null ? j.Sup.Nick : "?"));
            switch (a)
            {
                case "SpawnEnemy": SpawnFoot(j, true, false); break;
                case "SpawnAlly": SpawnFoot(j, false, false); break;
                case "Animals": SpawnFoot(j, true, true); break;
                case "Clone": SpawnClone(j); break;
                case "MafiaCar": SpawnCrew(j, "schafter2", 4); break;
                case "MotoHitman": SpawnCrew(j, "bati", 2); break;
                default:
                    RunAction(j, a);
                    break;
            }
        }

        void SpawnFoot(Job j, bool enemy, bool animal)
        {
            Interaction it = j.It;
            Ped pl = Game.Player.Character;
            bool sky = string.Equals(it.Effect, "SkyDrop", StringComparison.OrdinalIgnoreCase);
            Vector3 pos = AroundPlayer(it.SpawnDistance);
            if (sky) pos.Z += 45f;
            Model m = LoadModel(it.Model, DefaultModel(it.Action == "Random" ? (animal ? "Animals" : (enemy ? "SpawnEnemy" : "SpawnAlly")) : it.Action));
            if (!m.IsLoaded) return;
            Ped p = World.CreatePed(m, pos, HeadingTo(pos, pl.Position));
            m.MarkAsNoLongerNeeded();
            if (p == null || !p.Exists()) return;
            SetupPed(p, it, enemy, animal);
            if (!enemy)
            {
                int grp = Function.Call<int>(Hash.GET_PED_GROUP_INDEX, pl);
                Function.Call(Hash.SET_PED_AS_GROUP_MEMBER, p, grp);
                Function.Call(Hash.SET_PED_NEVER_LEAVES_GROUP, p, true);
                Function.Call(Hash.SET_PED_RELATIONSHIP_GROUP_HASH, p, relAlly);
            }
            if (it.Blip) AddBlip(p, enemy);
            Tracked t = Track(p, j, enemy, animal, null, true);
            if (sky && !animal)
            {
                Function.Call(Hash.GIVE_WEAPON_TO_PED, p, U.HashInt("GADGET_PARACHUTE"), 1, false, false);
                Function.Call(Hash.TASK_PARACHUTE_TO_TARGET, p, pl.Position.X, pl.Position.Y, pl.Position.Z);
                t.Parachuting = true;
                t.NextTask = U.Now + 9000;
            }
            else
            {
                SpawnFx(it.Effect, pos);
                TaskPed(t);
            }
            Snd(enemy ? cfg.SndEnemy : cfg.SndAlly);
            MaybeEntryCam(j, p);
        }

        void SpawnClone(Job j)
        {
            Ped pl = Game.Player.Character;
            int h = Function.Call<int>(Hash.CLONE_PED, pl, false, false, true);
            Ped p = Entity.FromHandle(h) as Ped;
            if (p == null || !p.Exists()) return;
            Vector3 pos = AroundPlayer(Math.Min(j.It.SpawnDistance, 6f));
            p.Position = pos;
            p.Heading = pl.Heading;
            SetupPed(p, j.It, false, false);
            if (string.IsNullOrEmpty(j.It.Weapon) || j.It.Weapon == "None")
                Function.Call(Hash.GIVE_WEAPON_TO_PED, p, U.HashInt("WEAPON_CARBINERIFLE"), 9999, false, true);
            int grp = Function.Call<int>(Hash.GET_PED_GROUP_INDEX, pl);
            Function.Call(Hash.SET_PED_AS_GROUP_MEMBER, p, grp);
            Function.Call(Hash.SET_PED_NEVER_LEAVES_GROUP, p, true);
            Function.Call(Hash.SET_PED_RELATIONSHIP_GROUP_HASH, p, relAlly);
            if (j.It.Blip) AddBlip(p, false);
            Tracked t = Track(p, j, false, false, null, true);
            SpawnFx(j.It.Effect, pos);
            TaskPed(t);
            Snd(cfg.SndAlly);
            MaybeEntryCam(j, p);
        }

        void SpawnCrew(Job j, string defVeh, int crew)
        {
            Interaction it = j.It;
            Ped pl = Game.Player.Character;
            float dist = Math.Max(40f, it.SpawnDistance);
            double ang = U.Rng.NextDouble() * Math.PI * 2;
            Vector3 around = pl.Position + new Vector3((float)Math.Cos(ang) * dist, (float)Math.Sin(ang) * dist, 0);
            Vector3 road = World.GetNextPositionOnStreet(around);
            if (road == Vector3.Zero || road.DistanceTo(pl.Position) > dist * 2.5f) road = around;
            Model vm = LoadModel(it.VehicleModel, defVeh);
            if (!vm.IsLoaded) return;
            Vehicle v = World.CreateVehicle(vm, road, HeadingTo(road, pl.Position));
            vm.MarkAsNoLongerNeeded();
            if (v == null || !v.Exists()) return;
            v.IsPersistent = true;
            Model pm = LoadModel(it.Model, DefaultModel(it.Action == "MotoHitman" || defVeh == "bati" ? "MotoHitman" : "MafiaCar"));
            if (!pm.IsLoaded) { v.Delete(); return; }
            int seats = Function.Call<int>(Hash.GET_VEHICLE_MAX_NUMBER_OF_PASSENGERS, v) + 1;
            int n = Math.Max(1, Math.Min(crew, seats));
            for (int i = 0; i < n; i++)
            {
                Ped p = v.CreatePedOnSeat((VehicleSeat)(i - 1), pm);
                if (p == null || !p.Exists()) continue;
                SetupPed(p, it, true, false);
                Function.Call(Hash.SET_PED_COMBAT_ATTRIBUTES, p, 3, i != 0);
                Tracked t = Track(p, j, true, false, v, i == 0);
                TaskPed(t);
            }
            pm.MarkAsNoLongerNeeded();
            if (it.Blip) AddBlip(v, true);
            Function.Call(Hash.SET_VEHICLE_ENGINE_ON, v, true, true, false);
            SpawnFx(it.Effect, road);
            Snd(cfg.SndEnemy);
            MaybeEntryCam(j, v);
        }

        void MaybeEntryCam(Job j, Entity target)
        {
            if (!cfg.EntryEnabled || j.CamDone) return;
            if (!(j.It.EntryCam || (j.Coins > 0 && j.Coins >= cfg.EntryMinCoins))) return;
            j.CamDone = true;
            if (camMode.Length > 0) return;
            StartCam("entry", target, cfg.EntrySeconds);
        }

        // ================================================================ tracked peds
        void UpdateTracked()
        {
            long now = U.Now;
            Ped pl = Game.Player.Character;
            for (int i = tracked.Count - 1; i >= 0; i--)
            {
                Tracked t = tracked[i];
                if (t.Ped == null || !t.Ped.Exists())
                {
                    if (t.DeadAt == 0) Requeue(t);
                    tracked.RemoveAt(i);
                    ReleaseVehicle(t);
                    continue;
                }
                if (t.DeadAt == 0 && (t.Ped.IsDead || t.Ped.Health <= 0))
                {
                    t.DeadAt = now;
                    OnPedDeath(t);
                }
                if (t.DeadAt != 0)
                {
                    if (celeb && dancers.Contains(t.Ped)) continue;
                    if (now - t.DeadAt >= cfg.CorpseCleanupSeconds * 1000)
                    {
                        if (cfg.CorpseCleanupSeconds > 0.2f && cfg.KillFxEnabled && t.FxOk && !t.Animal) KillFx(t.Ped.Position);
                        DeleteTracked(t);
                        tracked.RemoveAt(i);
                        ReleaseVehicle(t);
                    }
                    continue;
                }
                if (celeb) continue;
                // far away (e.g. player respawned at hospital): bring back later, nothing is lost
                if (pl.Exists() && !t.Ped.IsInVehicle() && t.Ped.Position.DistanceTo(pl.Position) > 300f)
                {
                    Requeue(t);
                    DeleteTracked(t);
                    tracked.RemoveAt(i);
                    ReleaseVehicle(t);
                    continue;
                }
                if (now >= t.NextTask)
                {
                    t.NextTask = now + 4000;
                    t.Parachuting = false;
                    TaskPed(t);
                }
            }

            for (int i = tempVehicles.Count - 1; i >= 0; i--)
            {
                TempVehicle v = tempVehicles[i];
                if (v.Veh == null || !v.Veh.Exists()) { tempVehicles.RemoveAt(i); continue; }
                if (now >= v.DeleteAt)
                {
                    if (pl.Exists() && pl.IsInVehicle() && pl.CurrentVehicle.Handle == v.Veh.Handle) { v.DeleteAt = now + 10000; continue; }
                    SafeDeleteVehicle(v.Veh);
                    tempVehicles.RemoveAt(i);
                }
            }
        }

        void Requeue(Tracked t)
        {
            if (t.It == null) return;
            if (t.Veh != null && !t.Leader) return; // crews come back as one unit (via leader)
            Job j = new Job();
            j.It = t.It;
            j.Sup = t.Sup;
            j.Left = 1;
            j.CamDone = true;
            if (t.It.Action != "SpawnEnemy" && t.It.Action != "SpawnAlly" && !IsSpawn(t.It.Action))
            {
                j.Random = true;
                j.RandomAction = t.Animal ? "Animals" : (t.Enemy ? "SpawnEnemy" : "SpawnAlly");
            }
            spawnQ.Insert(0, j);
        }

        void ReleaseVehicle(Tracked t)
        {
            if (t.Veh == null) return;
            foreach (Tracked o in tracked) if (o.Veh != null && o.Veh.Handle == t.Veh.Handle) return;
            foreach (TempVehicle tv in tempVehicles) if (tv.Veh.Handle == t.Veh.Handle) return;
            if (!t.Veh.Exists()) return;
            if (t.Veh.AttachedBlip != null && t.Veh.AttachedBlip.Exists()) t.Veh.AttachedBlip.Delete();
            TempVehicle v = new TempVehicle();
            v.Veh = t.Veh;
            v.DeleteAt = U.Now + (long)(cfg.VehicleCleanupSeconds * 1000);
            tempVehicles.Add(v);
        }

        void DeleteTracked(Tracked t)
        {
            try
            {
                if (t.Ped != null && t.Ped.Exists())
                {
                    if (t.Ped.AttachedBlip != null && t.Ped.AttachedBlip.Exists()) t.Ped.AttachedBlip.Delete();
                    t.Ped.Delete();
                }
            }
            catch { }
        }

        void SafeDeleteVehicle(Vehicle v)
        {
            try
            {
                if (v == null || !v.Exists()) return;
                Ped pl = Game.Player.Character;
                if (pl.Exists() && pl.IsInVehicle() && pl.CurrentVehicle.Handle == v.Handle) { v.IsPersistent = false; return; }
                if (v.AttachedBlip != null && v.AttachedBlip.Exists()) v.AttachedBlip.Delete();
                v.Delete();
            }
            catch { }
        }

        void OnPedDeath(Tracked t)
        {
            Ped pl = Game.Player.Character;
            try { if (t.Ped.AttachedBlip != null && t.Ped.AttachedBlip.Exists()) t.Ped.AttachedBlip.Delete(); } catch { }
            Entity killer = null;
            try { killer = t.Ped.Killer; } catch { }
            bool byPlayer = killer != null && pl.Exists() && (killer.Handle == pl.Handle || (pl.IsInVehicle() && killer.Handle == pl.CurrentVehicle.Handle));
            t.FxOk = !cfg.KillOnlyPlayer || byPlayer;
            if (cfg.KillFxEnabled && t.FxOk && cfg.CorpseCleanupSeconds <= 0.2f && !t.Animal) KillFx(t.Ped.Position);
            if (t.Enemy)
            {
                if (cfg.FeedEnabled)
                {
                    FeedItem f = new FeedItem();
                    f.Text = U.Fill(cfg.Tx("KillFeedText", "{name}"), t.Sup != null ? t.Sup.Nick : "?", 1);
                    f.Avatar = Avatar(t.Sup);
                    f.Start = U.Now;
                    f.End = U.Now + (long)(cfg.FeedSeconds * 1000);
                    f.Col = cfg.TextColor;
                    feed.Add(f);
                    while (feed.Count > cfg.FeedMax) feed.RemoveAt(0);
                }
                Snd(cfg.SndKill);
                if (cfg.KillSoundEnabled && cfg.KillSound.Length > 0) Wav(cfg.KillSound, false);
                string mode = cfg.SlowMoMode.ToLowerInvariant();
                bool slow = mode == "everykill" || (mode == "lastinwave" && AliveCount(true) == 0 && !HasQueued(true));
                if (slow && !PlayerBusy()) killSlowEnd = U.Now + (long)(cfg.SlowMoSeconds * 1000);
            }
        }

        bool HasQueued(bool enemy)
        {
            foreach (Job j in spawnQ) if (IsAllySpawn(ActionOf(j)) != enemy) return true;
            return false;
        }

        // ================================================================ visual & audio effects
        static readonly string[][] FxTable = {
            new string[] { "SoftSmoke", "core", "veh_respray_smoke", "1.0", "color" },
            new string[] { "Smoke", "core", "exp_grd_grenade_smoke", "1.0", "" },
            new string[] { "Flare", "core", "exp_grd_flare", "1.2", "" },
            new string[] { "Flash", "scr_rcbarry2", "scr_clown_appears", "1.2", "" },
            new string[] { "Lightning", "scr_rcbarry2", "scr_clown_death", "1.0", "" } };

        void PreloadFx()
        {
            Function.Call(Hash.REQUEST_NAMED_PTFX_ASSET, "core");
            Function.Call(Hash.REQUEST_NAMED_PTFX_ASSET, "scr_rcbarry2");
        }

        void Ptfx(string asset, string name, Vector3 pos, float scale, bool colored)
        {
            Function.Call(Hash.REQUEST_NAMED_PTFX_ASSET, asset);
            int guard = 0;
            while (!Function.Call<bool>(Hash.HAS_NAMED_PTFX_ASSET_LOADED, asset) && guard++ < 30) Script.Yield();
            Function.Call(Hash.USE_PARTICLE_FX_ASSET, asset);
            if (colored)
            {
                Color c = cfg.SmokeColor;
                Function.Call(Hash.SET_PARTICLE_FX_NON_LOOPED_COLOUR, c.R / 255f, c.G / 255f, c.B / 255f);
            }
            Function.Call(Hash.START_PARTICLE_FX_NON_LOOPED_AT_COORD, name, pos.X, pos.Y, pos.Z, 0f, 0f, 0f, scale, false, false, false);
        }

        void Effect(string effect, Vector3 pos)
        {
            if (string.IsNullOrEmpty(effect)) return;
            string e = effect.Trim();
            if (e.Equals("None", StringComparison.OrdinalIgnoreCase) || e.Equals("SkyDrop", StringComparison.OrdinalIgnoreCase)) return;
            if (e.Equals("Explosion", StringComparison.OrdinalIgnoreCase))
            {
                Function.Call(Hash.ADD_EXPLOSION, pos.X, pos.Y, pos.Z, 0, 0f, true, false, 0.4f, true);
                return;
            }
            if (e.Equals("Lightning", StringComparison.OrdinalIgnoreCase)) Function.Call(Hash.FORCE_LIGHTNING_FLASH);
            foreach (string[] row in FxTable)
            {
                if (!row[0].Equals(e, StringComparison.OrdinalIgnoreCase)) continue;
                Ptfx(row[1], row[2], pos, float.Parse(row[3], U.IC), row[4] == "color");
                return;
            }
        }

        void SpawnFx(string effect, Vector3 pos) { try { Effect(effect, pos); } catch (Exception ex) { U.Error("SpawnFx", ex); } }
        void KillFx(Vector3 pos) { try { Effect(cfg.KillEffect, pos); } catch (Exception ex) { U.Error("KillFx", ex); } }

        void Snd(string spec)
        {
            if (string.IsNullOrEmpty(spec) || spec.Equals("None", StringComparison.OrdinalIgnoreCase)) return;
            string[] p = spec.Split('|');
            string set = p.Length > 1 ? p[1].Trim() : "HUD_FRONTEND_DEFAULT_SOUNDSET";
            Function.Call(Hash.PLAY_SOUND_FRONTEND, -1, p[0].Trim(), set, true);
        }

        SoundPlayer Wav(string file, bool keep)
        {
            try
            {
                string path = Path.IsPathRooted(file) ? file : Path.Combine(U.DataDir, "sounds", file);
                if (!File.Exists(path)) return null;
                SoundPlayer sp = new SoundPlayer(path);
                sp.Play();
                return sp;
            }
            catch (Exception ex) { U.Error("Wav", ex); return null; }
        }

        // ================================================================ actions
        bool Active(string key)
        {
            long end;
            return fxEnd.TryGetValue(key, out end) && U.Now < end;
        }

        void StartTimed(string key, float seconds)
        {
            long now = U.Now;
            long end;
            long add = (long)(Math.Max(0.5f, seconds) * 1000);
            if (fxEnd.TryGetValue(key, out end) && end > now) fxEnd[key] = end + add;
            else fxEnd[key] = now + add;
        }

        Vehicle PlayerVehicle()
        {
            Ped pl = Game.Player.Character;
            if (pl.Exists() && pl.IsInVehicle()) return pl.CurrentVehicle;
            return null;
        }

        void RunAction(Job j, string a)
        {
            Interaction it = j.It;
            Ped pl = Game.Player.Character;
            Vehicle pv = PlayerVehicle();
            switch (a)
            {
                case "AddHealth":
                    {
                        int max = Function.Call<int>(Hash.GET_ENTITY_MAX_HEALTH, pl) + it.Amount;
                        int cur = Function.Call<int>(Hash.GET_ENTITY_HEALTH, pl) + it.Amount;
                        Function.Call(Hash.SET_PED_MAX_HEALTH, pl, max);
                        Function.Call(Hash.SET_ENTITY_MAX_HEALTH, pl, max);
                        Function.Call(Hash.SET_ENTITY_HEALTH, pl, Math.Min(cur, max), 0);
                        break;
                    }
                case "Heal":
                    Function.Call(Hash.SET_ENTITY_HEALTH, pl, Function.Call<int>(Hash.GET_ENTITY_MAX_HEALTH, pl), 0);
                    pl.Armor = 100;
                    if (pv != null) { pv.Repair(); }
                    break;
                case "GodMode": StartTimed("GodMode", it.Duration); break;
                case "GiveVehicle":
                    {
                        Model m = LoadModel(it.VehicleModel, "zentorno");
                        if (!m.IsLoaded) break;
                        bool sky = string.Equals(it.Effect, "SkyDrop", StringComparison.OrdinalIgnoreCase);
                        Vector3 pos = pl.Position + pl.ForwardVector * (sky ? 8f : 4f) + pl.RightVector * (sky ? 0f : 3f);
                        if (sky) pos.Z += 35f;
                        Vehicle v = World.CreateVehicle(m, pos, pl.Heading);
                        m.MarkAsNoLongerNeeded();
                        if (v == null || !v.Exists()) break;
                        if (!sky) SpawnFx(it.Effect, pos);
                        if (it.WarpIntoVehicle && !sky) Function.Call(Hash.SET_PED_INTO_VEHICLE, pl, v, -1);
                        TempVehicle tv = new TempVehicle();
                        tv.Veh = v;
                        tv.DeleteAt = long.MaxValue;
                        tempVehicles.Add(tv);
                        break;
                    }
                case "GiveWeapon":
                    {
                        int wh = U.HashInt(string.IsNullOrEmpty(it.Weapon) ? "WEAPON_CARBINERIFLE" : it.Weapon);
                        Function.Call(Hash.GIVE_WEAPON_TO_PED, pl, wh, it.Ammo, false, true);
                        break;
                    }
                case "GiveAllWeapons":
                    foreach (string w in AllWeapons) Function.Call(Hash.GIVE_WEAPON_TO_PED, pl, U.HashInt(w), 9999, false, false);
                    break;
                case "SuperSpeed": StartTimed("SuperSpeed", it.Duration); break;
                case "SuperJump": StartTimed("SuperJump", it.Duration); break;
                case "BoostVehicle":
                    if (pv != null) Function.Call(Hash.SET_VEHICLE_FORWARD_SPEED, pv, pv.Speed + 40f);
                    break;
                case "ClearArea":
                    foreach (Ped p in World.GetNearbyPeds(pl, 80f))
                    {
                        if (p == null || !p.Exists() || p.IsPlayer || IsTracked(p)) continue;
                        if (pv != null && p.IsInVehicle() && p.CurrentVehicle.Handle == pv.Handle) continue;
                        p.Delete();
                    }
                    break;
                case "KillPlayer":
                    fxEnd.Remove("GodMode");
                    StopCam();
                    killPlayerSup = j.Sup;
                    killPlayerAt = U.Now;
                    pl.IsInvincible = false;
                    Function.Call(Hash.SET_ENTITY_HEALTH, pl, 0, 0);
                    break;
                case "Airstrike": StartTimed("Airstrike", it.Duration); break;
                case "Fire": Function.Call(Hash.START_ENTITY_FIRE, pl); break;
                case "Freeze": StartTimed("Freeze", it.Duration); break;
                case "RemoveWeapons": Function.Call(Hash.REMOVE_ALL_PED_WEAPONS, pl, true); break;
                case "RemoveVehicle":
                    if (pv != null)
                    {
                        Vector3 at = pv.Position + new Vector3(0, 0, 1.5f);
                        Function.Call(Hash.SET_ENTITY_COORDS, pl, at.X, at.Y, at.Z, false, false, false, false);
                        pv.Delete();
                    }
                    break;
                case "EjectVehicle":
                    if (pv != null) Function.Call(Hash.TASK_LEAVE_VEHICLE, pl, pv, 4160);
                    break;
                case "ExplodeVehicle":
                    if (pv != null) Function.Call(Hash.EXPLODE_VEHICLE, pv, true, false);
                    break;
                case "BurstTires":
                    if (pv != null) for (int w = 0; w < 8; w++) Function.Call(Hash.SET_VEHICLE_TYRE_BURST, pv, w, true, 1000f);
                    break;
                case "Launch":
                    if (pv != null) pv.Velocity = pv.Velocity + new Vector3(0, 0, 35f);
                    else
                    {
                        Function.Call(Hash.SET_PED_TO_RAGDOLL, pl, 4000, 5000, 0, false, false, false);
                        pl.Velocity = new Vector3(U.RandF(-3, 3), U.RandF(-3, 3), 40f);
                    }
                    break;
                case "Skyfall":
                    {
                        Function.Call(Hash.GIVE_WEAPON_TO_PED, pl, U.HashInt("GADGET_PARACHUTE"), 1, false, false);
                        Vector3 p = pl.Position;
                        Function.Call(Hash.SET_ENTITY_COORDS, pl, p.X, p.Y, p.Z + 350f, false, false, false, false);
                        break;
                    }
                case "Ragdoll": Function.Call(Hash.SET_PED_TO_RAGDOLL, pl, 3000, 3000, 0, false, false, false); break;
                case "Drunk": StartTimed("Drunk", it.Duration); break;
                case "Earthquake": StartTimed("Earthquake", it.Duration); break;
                case "CarRain":
                    carRainPerSec = Math.Max(0.2f, Math.Min(20f, it.Amount));
                    StartTimed("CarRain", it.Duration);
                    break;
                case "ExplodeNearby":
                    {
                        int n = 0;
                        foreach (Vehicle v in World.GetNearbyVehicles(pl, 60f))
                        {
                            if (v == null || !v.Exists() || (pv != null && v.Handle == pv.Handle)) continue;
                            Function.Call(Hash.EXPLODE_VEHICLE, v, true, false);
                            if (++n >= 12) break;
                        }
                        break;
                    }
                case "Storm": StartTimed("Storm", it.Duration); break;
                case "Blackout": StartTimed("Blackout", it.Duration); break;
                case "SlowMotion": StartTimed("SlowMotion", it.Duration); break;
                case "LowGravity": StartTimed("LowGravity", it.Duration); break;
                case "Teleport":
                    {
                        float[] s = TeleportSpots[U.Rng.Next(TeleportSpots.Length)];
                        Entity e = pv != null ? (Entity)pv : pl;
                        Function.Call(Hash.SET_ENTITY_COORDS, e, s[0], s[1], s[2], false, false, false, false);
                        break;
                    }
                case "Weather":
                    {
                        string w = it.Weather;
                        if (string.IsNullOrEmpty(w) || w.Equals("Random", StringComparison.OrdinalIgnoreCase)) w = Weathers[U.Rng.Next(Weathers.Length)];
                        weatherOverride = w.ToUpperInvariant();
                        StartTimed("Weather", it.Duration);
                        nextSecond = 0;
                        break;
                    }
            }
        }

        bool IsTracked(Ped p)
        {
            foreach (Tracked t in tracked) if (t.Ped != null && t.Ped.Handle == p.Handle) return true;
            return false;
        }

        Vector3 GroundNear(Vector3 c, float rmin, float rmax)
        {
            double ang = U.Rng.NextDouble() * Math.PI * 2;
            float r = U.RandF(rmin, rmax);
            Vector3 t = new Vector3(c.X + (float)Math.Cos(ang) * r, c.Y + (float)Math.Sin(ang) * r, c.Z);
            OutputArgument oz = new OutputArgument();
            if (Function.Call<bool>(Hash.GET_GROUND_Z_FOR_3D_COORD, t.X, t.Y, c.Z + 50f, oz, false, false)) t.Z = oz.GetResult<float>();
            return t;
        }

        void UpdateEffects(float dt)
        {
            long now = U.Now;
            Ped pl = Game.Player.Character;
            if (!pl.Exists()) return;
            Vehicle pv = PlayerVehicle();

            // time scale: kill slow-mo > SlowMotion action
            float ts = 1f;
            if (now < killSlowEnd) ts = cfg.SlowMoScale;
            else if (Active("SlowMotion")) ts = 0.4f;
            if (ts != 1f || lastTimeScale != 1f) { Function.Call(Hash.SET_TIME_SCALE, ts); lastTimeScale = ts; }

            bool grav = Active("LowGravity");
            if (grav != gravOn) { Function.Call(Hash.SET_GRAVITY_LEVEL, grav ? 2 : 0); gravOn = grav; }

            bool black = Active("Blackout");
            if (black != blackoutOn) { Function.Call(Hash.SET_ARTIFICIAL_LIGHTS_STATE, black); blackoutOn = black; }

            if (Active("SuperJump")) { Function.Call(Hash.SET_SUPER_JUMP_THIS_FRAME, Game.Player); }

            bool speed = Active("SuperSpeed");
            if (speed)
            {
                Function.Call(Hash.SET_RUN_SPRINT_MULTIPLIER_FOR_PLAYER, Game.Player, 1.49f);
                Function.Call(Hash.SET_SWIM_MULTIPLIER_FOR_PLAYER, Game.Player, 1.49f);
                Function.Call(Hash.SET_PED_MOVE_RATE_OVERRIDE, pl, 1.5f);
                speedOn = true;
            }
            else if (speedOn)
            {
                Function.Call(Hash.SET_RUN_SPRINT_MULTIPLIER_FOR_PLAYER, Game.Player, 1f);
                Function.Call(Hash.SET_SWIM_MULTIPLIER_FOR_PLAYER, Game.Player, 1f);
                Function.Call(Hash.SET_PED_MOVE_RATE_OVERRIDE, pl, 1f);
                speedOn = false;
            }

            bool freeze = Active("Freeze");
            if (freeze != freezeOn)
            {
                Function.Call(Hash.FREEZE_ENTITY_POSITION, pl, freeze);
                if (pv != null) Function.Call(Hash.FREEZE_ENTITY_POSITION, pv, freeze);
                freezeOn = freeze;
            }

            bool drunk = Active("Drunk");
            if (drunk)
            {
                Function.Call(Hash.REQUEST_CLIP_SET, "move_m@drunk@verydrunk");
                if (!drunkClip && Function.Call<bool>(Hash.HAS_CLIP_SET_LOADED, "move_m@drunk@verydrunk"))
                {
                    Function.Call(Hash.SET_PED_MOVEMENT_CLIPSET, pl, "move_m@drunk@verydrunk", 1f);
                    drunkClip = true;
                }
            }
            else if (drunkClip) { Function.Call(Hash.RESET_PED_MOVEMENT_CLIPSET, pl, 1f); drunkClip = false; }

            bool quake = Active("Earthquake");
            bool shake = quake || drunk;
            if (shake && !shaking)
            {
                Function.Call(Hash.SHAKE_GAMEPLAY_CAM, quake ? "ROAD_VIBRATION_SHAKE" : "DRUNK_SHAKE", quake ? 2.5f : 1.5f);
                shaking = true;
            }
            else if (!shake && shaking) { Function.Call(Hash.STOP_GAMEPLAY_CAM_SHAKING, true); shaking = false; }

            if (quake && now >= nextQuake)
            {
                nextQuake = now + 1200;
                foreach (Ped p in World.GetNearbyPeds(pl, 45f))
                {
                    if (p == null || !p.Exists() || p.IsPlayer || p.IsInVehicle()) continue;
                    if (U.Rng.Next(3) == 0) Function.Call(Hash.SET_PED_TO_RAGDOLL, p, 1500, 2500, 0, false, false, false);
                }
                foreach (Vehicle v in World.GetNearbyVehicles(pl, 50f))
                {
                    if (v == null || !v.Exists()) continue;
                    v.Velocity = v.Velocity + new Vector3(U.RandF(-3, 3), U.RandF(-3, 3), U.RandF(1, 3.5f));
                }
                if (!pl.IsInVehicle() && U.Rng.Next(4) == 0) Function.Call(Hash.SET_PED_TO_RAGDOLL, pl, 1000, 1500, 0, false, false, false);
            }

            if (Active("CarRain"))
            {
                carRainAcc += dt / 1000f * carRainPerSec;
                int spawned = 0;
                while (carRainAcc >= 1f && spawned < 3)
                {
                    carRainAcc -= 1f;
                    spawned++;
                    Model m = new Model(RainCars[U.Rng.Next(RainCars.Length)]);
                    m.Request(1000);
                    if (!m.IsLoaded) continue;
                    Vector3 p = pl.Position + new Vector3(U.RandF(-22, 22), U.RandF(-22, 22), U.RandF(30, 45));
                    Vehicle v = World.CreateVehicle(m, p, U.RandF(0, 360));
                    m.MarkAsNoLongerNeeded();
                    if (v == null || !v.Exists()) continue;
                    v.Rotation = new Vector3(U.RandF(-40, 40), U.RandF(-40, 40), U.RandF(0, 360));
                    v.Velocity = new Vector3(0, 0, -12f);
                    TempVehicle tv = new TempVehicle();
                    tv.Veh = v;
                    tv.DeleteAt = now + (long)(cfg.VehicleCleanupSeconds * 1000);
                    tempVehicles.Add(tv);
                }
                while (tempVehicles.Count > 80)
                {
                    SafeDeleteVehicle(tempVehicles[0].Veh);
                    tempVehicles.RemoveAt(0);
                }
            }
            else carRainAcc = 0;

            if (Active("Airstrike") && now >= nextStrike)
            {
                nextStrike = now + 650;
                Vector3 t = GroundNear(pl.Position, 6f, 26f);
                Function.Call(Hash.ADD_EXPLOSION, t.X, t.Y, t.Z, 4, 1f, true, false, 0.8f, false);
            }

            if (Active("Storm") && now >= nextStorm)
            {
                nextStorm = now + U.Rng.Next(1500, 3500);
                Function.Call(Hash.FORCE_LIGHTNING_FLASH);
                Vector3 t = GroundNear(pl.Position, 10f, 40f);
                Function.Call(Hash.ADD_EXPLOSION, t.X, t.Y, t.Z, 0, 0f, true, false, 0.3f, true);
            }

            // tidy finished timers
            List<string> done = null;
            foreach (KeyValuePair<string, long> kv in fxEnd) if (kv.Value <= now) { if (done == null) done = new List<string>(); done.Add(kv.Key); }
            if (done != null) foreach (string k in done) { fxEnd.Remove(k); if (k == "Weather" || k == "Storm") nextSecond = 0; }
        }

        // ================================================================ round / challenge
        void UpdateRound(float dt)
        {
            Ped pl = Game.Player.Character;
            long now = U.Now;
            bool dead = !pl.Exists() || pl.IsDead || Function.Call<bool>(Hash.IS_PLAYER_DEAD, Game.Player);

            if (dead && !deathHandled)
            {
                deathHandled = true;
                OnPlayerDeath();
            }
            else if (!dead && deathHandled && !celeb && killerCamEnd == 0)
            {
                deathHandled = false;
            }

            if (phase == Phase.Running && cfg.ChallengeEnabled)
            {
                if (!paused && camMode.Length == 0 && !dead && !celeb) roundMs += dt;
                if (roundMs >= roundDurMs) EndRound(true, null);
            }
            else if (phase == Phase.Ended)
            {
                bool screenDone = !cfg.EndScreenEnabled || now >= endScreenUntil;
                if (screenDone && !dead && !celeb && camMode.Length == 0 && !Screen.IsFadedOut && !Screen.IsFadingIn)
                {
                    if (cfg.AutoRestart) StartRound();
                    else phase = Phase.Idle;
                }
            }
        }

        void EndRound(bool win, Supporter killerSup)
        {
            phase = Phase.Ended;
            lastWin = win;
            endScreenUntil = U.Now + (long)(cfg.EndScreenSeconds * 1000);
            if (win)
            {
                wins++;
                streak = streak > 0 ? streak + 1 : 1;
                mvp = null;
                foreach (Supporter s in sups.Values)
                    if (s.RoundHelpCoins > 0 && (mvp == null || s.RoundHelpCoins > mvp.RoundHelpCoins)) mvp = s;
                mvpReason = cfg.Tx("MvpWinReason", "");
                if (cfg.WinCamEnabled) StartCam("win", Game.Player.Character, cfg.WinSeconds);
                Function.Call(Hash.PLAY_SOUND_FRONTEND, -1, "RACE_PLACED", "HUD_AWARDS", true);
            }
            else
            {
                losses++;
                streak = streak < 0 ? streak - 1 : -1;
                mvp = killerSup;
                mvpReason = cfg.Tx("MvpLossReason", "");
                Function.Call(Hash.PLAY_SOUND_FRONTEND, -1, "LOSER", "HUD_AWARDS", true);
            }
            if (cfg.ClearQueueOnRoundEnd) { spawnQ.Clear(); instantQ.Clear(); }
        }

        void OnPlayerDeath()
        {
            Ped pl = Game.Player.Character;
            Supporter killer = null;
            Entity killerEnt = null;
            if (U.Now - killPlayerAt < 5000) killer = killPlayerSup;
            else
            {
                Entity k = null;
                try { k = pl.Killer; } catch { }
                if (k != null)
                {
                    foreach (Tracked t in tracked)
                    {
                        if ((t.Ped != null && t.Ped.Handle == k.Handle) || (t.Veh != null && t.Veh.Handle == k.Handle))
                        {
                            killer = t.Sup; killerEnt = t.Ped; break;
                        }
                    }
                }
                if (killer == null)
                {
                    foreach (Tracked t in tracked)
                    {
                        if (!t.Enemy || t.Ped == null || !t.Ped.Exists()) continue;
                        if (Function.Call<bool>(Hash.HAS_ENTITY_BEEN_DAMAGED_BY_ENTITY, pl, t.Ped, true))
                        {
                            killer = t.Sup; killerEnt = t.Ped; break;
                        }
                    }
                }
            }
            if (phase == Phase.Running && cfg.ChallengeEnabled) EndRound(false, killer);

            bool wantCam = cfg.KillerEnabled && killerEnt != null && killerEnt.Exists();
            if (wantCam || cfg.CelebEnabled)
            {
                Function.Call(Hash.PAUSE_DEATH_ARREST_RESTART, true);
                deathPaused = true;
            }
            if (wantCam)
            {
                StartCam("killer", killerEnt, cfg.KillerSeconds);
                killerCamEnd = U.Now + (long)(cfg.KillerSeconds * 1000);
            }
            else killerCamEnd = 0;
            if (cfg.CelebEnabled) StartCelebration();
        }

        // ================================================================ camera
        void StartCam(string mode, Entity target, float seconds)
        {
            if (target == null || !target.Exists()) return;
            if (cam == null || !cam.Exists())
            {
                cam = World.CreateCamera(target.Position + new Vector3(0, -4, 2), Vector3.Zero, 55f);
            }
            camMode = mode;
            camTarget = target;
            camEnd = U.Now + (long)(seconds * 1000);
            camOrbit = U.RandF(0, 360);
            PlaceCam();
            cam.IsActive = true;
            Function.Call(Hash.RENDER_SCRIPT_CAMS, true, true, 600, true, false, 0);
        }

        void PlaceCam()
        {
            if (cam == null || camTarget == null || !camTarget.Exists()) return;
            Vector3 c = camTarget.Position;
            float r = camMode == "win" ? 5.5f : (camMode == "celeb" ? 7f : 4.5f);
            float h = camMode == "celeb" ? 3.5f : 1.8f;
            double a = camOrbit * Math.PI / 180.0;
            cam.Position = new Vector3(c.X + (float)Math.Cos(a) * r, c.Y + (float)Math.Sin(a) * r, c.Z + h);
            cam.PointAt(camTarget);
        }

        void StopCam()
        {
            if (camMode.Length == 0 && cam == null) return;
            camMode = "";
            camTarget = null;
            Function.Call(Hash.RENDER_SCRIPT_CAMS, false, true, 600, true, false, 0);
            if (cam != null && cam.Exists()) { cam.IsActive = false; cam.Delete(); }
            cam = null;
        }

        void UpdateCamera(float dt)
        {
            if (camMode.Length == 0) return;
            long now = U.Now;
            if (camTarget == null || !camTarget.Exists() || now >= camEnd)
            {
                if (camMode == "killer" && celeb)
                {
                    killerCamEnd = 0;
                    StartCam("celeb", Game.Player.Character, Math.Max(0.5f, (celebEnd - now) / 1000f));
                    return;
                }
                if (camMode == "killer") killerCamEnd = 0;
                StopCam();
                if (!celeb && deathPaused) { Function.Call(Hash.PAUSE_DEATH_ARREST_RESTART, false); deathPaused = false; }
                return;
            }
            float speed = camMode == "entry" ? 20f : 28f;
            camOrbit += dt / 1000f * speed;
            PlaceCam();
        }

        // ================================================================ death celebration
        void StartCelebration()
        {
            Ped pl = Game.Player.Character;
            celeb = true;
            celebEnd = U.Now + (long)(cfg.CelebSeconds * 1000);
            dancers.Clear();
            List<Tracked> cands = new List<Tracked>();
            foreach (Tracked t in tracked)
                if (t.Enemy && !t.Animal && t.DeadAt == 0 && t.Ped.Exists() && !t.Ped.IsDead) cands.Add(t);
            cands.Sort(delegate(Tracked a, Tracked b)
            {
                return a.Ped.Position.DistanceTo(pl.Position).CompareTo(b.Ped.Position.DistanceTo(pl.Position));
            });
            if (cfg.CelebDance)
            {
                Function.Call(Hash.REQUEST_ANIM_DICT, cfg.DanceDict);
                int guard = 0;
                while (!Function.Call<bool>(Hash.HAS_ANIM_DICT_LOADED, cfg.DanceDict) && guard++ < 60) Script.Yield();
            }
            int n = Math.Min(6, cands.Count);
            for (int i = 0; i < n; i++)
            {
                Ped p = cands[i].Ped;
                double a = i * Math.PI * 2 / Math.Max(1, n);
                Vector3 pos = pl.Position + new Vector3((float)Math.Cos(a) * 3f, (float)Math.Sin(a) * 3f, 0f);
                p.IsInvincible = true;
                Function.Call(Hash.CLEAR_PED_TASKS_IMMEDIATELY, p);
                Function.Call(Hash.SET_ENTITY_COORDS, p, pos.X, pos.Y, pos.Z, false, false, false, false);
                p.Heading = HeadingTo(pos, pl.Position);
                if (cfg.CelebDance)
                    Function.Call(Hash.TASK_PLAY_ANIM, p, cfg.DanceDict, cfg.DanceAnim, 8f, -8f, -1, 1, 0f, false, false, false);
                dancers.Add(p);
            }
            if (cfg.CelebCoffin)
            {
                Model m = new Model(cfg.CoffinModel);
                m.Request(2000);
                if (m.IsLoaded)
                {
                    coffin = World.CreateProp(m, pl.Position + new Vector3(0, 0, 2f), false, false);
                    m.MarkAsNoLongerNeeded();
                    if (coffin != null && coffin.Exists())
                    {
                        if (dancers.Count > 0)
                        {
                            Function.Call(Hash.ATTACH_ENTITY_TO_ENTITY, coffin, dancers[0], 0, 0f, 0f, 1.25f, 0f, 0f, 90f, false, false, false, false, 2, true, 0);
                        }
                        else
                        {
                            Vector3 p = pl.Position + new Vector3(1.5f, 0, 0);
                            coffin.Position = p;
                            Function.Call(Hash.PLACE_OBJECT_ON_GROUND_PROPERLY, coffin);
                        }
                    }
                }
            }
            if (cfg.DeathSoundEnabled && cfg.DeathSound.Length > 0) deathPlayer = Wav(cfg.DeathSound, true);
            if (camMode.Length == 0) StartCam("celeb", pl, cfg.CelebSeconds);
        }

        void UpdateCelebration()
        {
            if (!celeb) return;
            if (U.Now >= celebEnd) EndCelebration();
        }

        void EndCelebration()
        {
            if (!celeb) return;
            celeb = false;
            try { if (deathPlayer != null) deathPlayer.Stop(); } catch { }
            deathPlayer = null;
            try { if (coffin != null && coffin.Exists()) coffin.Delete(); } catch { }
            coffin = null;
            foreach (Ped p in dancers)
            {
                if (p == null || !p.Exists()) continue;
                p.IsInvincible = false;
                Function.Call(Hash.CLEAR_PED_TASKS, p);
            }
            dancers.Clear();
            if (camMode == "celeb" || camMode == "killer") StopCam();
            killerCamEnd = 0;
            if (deathPaused) { Function.Call(Hash.PAUSE_DEATH_ARREST_RESTART, false); deathPaused = false; }
        }

        // ================================================================ HUD
        const float TXT = 0.34f, SMALL = 0.28f, BIG = 0.62f;
        float frameX, frameY, frameW = 1280, frameH = 720;
        LayoutCfg L;

        static Font FontOf(string n)
        {
            switch ((n ?? "").ToLowerInvariant())
            {
                case "housescript": return Font.HouseScript;
                case "monospace": return Font.Monospace;
                case "chaletcomprimecologne": return Font.ChaletComprimeCologne;
                case "pricedown": return Font.Pricedown;
                default: return Font.ChaletLondon;
            }
        }

        Color PanelCol(float mul) { return U.WithAlpha(cfg.PanelColor, (int)(cfg.Opacity * mul)); }
        Color TextCol(int a) { return U.WithAlpha(cfg.TextColor, a); }
        string St { get { return (style ?? "Classic").ToLowerInvariant(); } }

        void PanelBg(float x, float y, float w, float h, float alpha)
        {
            Color acc = cfg.Accent;
            switch (St)
            {
                case "glass":
                    Gfx.Rect(x, y, w, h, U.WithAlpha(U.Mix(cfg.PanelColor, Color.White, 0.12f), (int)(cfg.Opacity * 0.55f * alpha)));
                    Gfx.Border(x, y, w, h, 1, U.WithAlpha(Color.White, (int)(55 * alpha)));
                    break;
                case "neon":
                    Gfx.Rect(x, y, w, h, PanelCol(alpha));
                    Gfx.Border(x - 2, y - 2, w + 4, h + 4, 2, U.WithAlpha(acc, (int)(60 * alpha)));
                    Gfx.Border(x, y, w, h, 1, U.WithAlpha(acc, (int)(230 * alpha)));
                    break;
                case "minimal":
                    Gfx.Rect(x, y, w, h, PanelCol(0.3f * alpha));
                    break;
                case "esports":
                    Gfx.Rect(x, y, w, h, PanelCol(alpha));
                    Gfx.Rect(x, y, 4, h, U.WithAlpha(acc, (int)(255 * alpha)));
                    Gfx.Rect(x, y + h - 2, w, 2, U.WithAlpha(acc, (int)(120 * alpha)));
                    break;
                case "retro":
                    Gfx.Rect(x, y, w, h, PanelCol(alpha));
                    Gfx.Border(x, y, w, h, 2, U.WithAlpha(cfg.TextColor, (int)(170 * alpha)));
                    break;
                default:
                    Gfx.Rect(x, y, w, h, PanelCol(alpha));
                    Gfx.Rect(x, y, w, 2, U.WithAlpha(acc, (int)(255 * alpha)));
                    break;
            }
        }

        void Header(string text, float x, float y, float w, float h)
        {
            if (St == "esports")
            {
                Gfx.Rect(x, y, w, h, cfg.Accent);
                Gfx.Text(text, x + w / 2, y + 2, TXT, cfg.PanelColor, Alignment.Center);
            }
            else Gfx.Text(text, x + w / 2, y + 2, TXT, cfg.Accent, Alignment.Center);
        }

        PointF Anchor(PanelPos p, float w, float h)
        {
            string pos = (p.Pos ?? "TopLeft").ToLowerInvariant();
            float x, y;
            const float m = 14;
            if (pos.EndsWith("left")) x = frameX + m;
            else if (pos.EndsWith("right")) x = frameX + frameW - w - m;
            else x = frameX + (frameW - w) / 2;
            if (pos.StartsWith("top")) y = frameY + m;
            else if (pos.StartsWith("bottom")) y = frameY + frameH - h - m - 40;
            else y = frameY + (frameH - h) / 2;
            return new PointF(x + p.Ox, y + p.Oy);
        }

        string HAlign(string panel)
        {
            PanelPos p;
            if (!L.P.TryGetValue(panel, out p)) return "left";
            string pos = (p.Pos ?? "").ToLowerInvariant();
            if (pos.EndsWith("right")) return "right";
            if (pos.EndsWith("left")) return "left";
            return "center";
        }

        delegate SizeF PanelFn(bool draw);

        void Place(string name, bool enabled, PanelFn fn)
        {
            if (!enabled) return;
            PanelPos p;
            if (!L.P.TryGetValue(name, out p) || !p.Show) return;
            float s = L.Scale;
            Gfx.Origin(0, 0, s);
            SizeF sz = fn(false);
            if (sz.Width <= 0 || sz.Height <= 0) return;
            PointF a = Anchor(p, sz.Width * s, sz.Height * s);
            Gfx.Origin(a.X, a.Y, s);
            fn(true);
        }

        PanelFn fScore, fTop, fHealth, fGuide, fNotif, fFeed, fHype;

        void DrawHud()
        {
            if (!started || !cfg.HudEnabled) return;
            if (Function.Call<bool>(Hash.IS_PAUSE_MENU_ACTIVE)) return;
            Gfx.BeginFrame();
            Gfx.F = FontOf(fontName);
            Gfx.FontScale = cfg.FontScale;
            Gfx.Outline = cfg.TextOutline;
            Gfx.Shadow = cfg.TextShadow;
            L = vertical ? cfg.Vertical : cfg.Normal;
            if (vertical) { frameW = 405; frameX = (1280 - 405) / 2f; }
            else { frameW = 1280; frameX = 0; }
            frameY = 0; frameH = 720;

            if (fScore == null)
            {
                fScore = ScorePanel; fTop = TopPanel; fHealth = HealthPanel; fGuide = GuidePanel;
                fNotif = NotifPanel; fFeed = FeedPanel; fHype = HypePanel;
            }

            PruneFeeds();
            Overheads();
            Place("Score", cfg.ScoreEnabled, fScore);
            Place("Top3", cfg.Top3Enabled, fTop);
            Place("Health", cfg.HealthEnabled, fHealth);
            Place("Guide", cfg.GuideEnabled, fGuide);
            Place("Notif", cfg.NotifEnabled && notifs.Count > 0, fNotif);
            Place("Feed", cfg.FeedEnabled && feed.Count > 0, fFeed);
            Place("Hype", cfg.HypeEnabled && hypes.Count > 0, fHype);
            EndScreen();
            if (paused)
            {
                Gfx.Origin(0, 0, 1);
                Gfx.Text(cfg.Tx("PausedText", "PAUSED"), frameX + frameW / 2, 300, 0.7f, cfg.Accent, Alignment.Center);
            }
        }

        void PruneFeeds()
        {
            long now = U.Now;
            notifs.RemoveAll(delegate(FeedItem f) { return now >= f.End; });
            feed.RemoveAll(delegate(FeedItem f) { return now >= f.End; });
            hypes.RemoveAll(delegate(FeedItem f) { return now >= f.End; });
        }

        static float FeedAlpha(FeedItem f)
        {
            long now = U.Now;
            float a = 1f;
            if (now - f.Start < 250) a = (now - f.Start) / 250f;
            if (f.End - now < 500) a = Math.Min(a, (f.End - now) / 500f);
            return U.Clamp(a, 0, 1);
        }

        // ---------------------------------------------------------------- score
        string TimerText()
        {
            double left = roundDurMs;
            if (phase == Phase.Running) left = roundDurMs - roundMs;
            else if (phase == Phase.Ended) left = lastWin ? 0 : Math.Max(0, roundDurMs - roundMs);
            if (left < 0) left = 0;
            int s = (int)Math.Ceiling(left / 1000.0);
            return (s / 60).ToString("00", U.IC) + ":" + (s % 60).ToString("00", U.IC);
        }

        SizeF ScorePanel(bool draw)
        {
            float w = 290, y = 6;
            List<string> rows = new List<string>();
            foreach (string r in cfg.ScoreOrder)
            {
                string k = r.ToLowerInvariant();
                if (k == "title" && cfg.TitleEnabled && cfg.Title.Length > 0) rows.Add(k);
                else if (k == "score" && (cfg.ShowScore || (cfg.ShowTimer && cfg.ChallengeEnabled))) rows.Add(k);
                else if (k == "streak" && cfg.ShowStreak && Math.Abs(streak) >= 2) rows.Add(k);
            }
            if (rows.Count == 0) return SizeF.Empty;
            float h = 6;
            foreach (string r in rows) h += r == "title" ? 26 : (r == "score" ? 50 : 22);
            h += 4;
            if (!draw) return new SizeF(w, h);

            PanelBg(0, 0, w, h, 1);
            foreach (string r in rows)
            {
                if (r == "title")
                {
                    Header(cfg.Title, 0, y, w, 24);
                    y += 26;
                }
                else if (r == "score")
                {
                    if (cfg.ShowScore)
                    {
                        Gfx.Rect(8, y + 3, 72, 44, U.WithAlpha(cfg.WinColor, 60));
                        Gfx.Rect(8, y + 3, 72, 3, cfg.WinColor);
                        Gfx.Text(wins.ToString(U.IC), 44, y + 4, 0.55f, cfg.WinColor, Alignment.Center);
                        Gfx.Text(cfg.Tx("WinShort", "W"), 44, y + 29, SMALL, TextCol(230), Alignment.Center);
                        Gfx.Rect(w - 80, y + 3, 72, 44, U.WithAlpha(cfg.LossColor, 60));
                        Gfx.Rect(w - 80, y + 3, 72, 3, cfg.LossColor);
                        Gfx.Text(losses.ToString(U.IC), w - 44, y + 4, 0.55f, cfg.LossColor, Alignment.Center);
                        Gfx.Text(cfg.Tx("LossShort", "L"), w - 44, y + 29, SMALL, TextCol(230), Alignment.Center);
                    }
                    if (cfg.ShowTimer && cfg.ChallengeEnabled)
                    {
                        double left = roundDurMs - roundMs;
                        Color tc = phase == Phase.Running && left < 30000 && (U.Now / 500) % 2 == 0 ? cfg.LossColor : cfg.TextColor;
                        Gfx.Text(TimerText(), w / 2, y + 8, BIG, tc, Alignment.Center);
                    }
                    y += 50;
                }
                else if (r == "streak")
                {
                    bool win = streak > 0;
                    string txt = Math.Abs(streak).ToString(U.IC) + " " + (win ? cfg.Tx("WinStreakText", "") : cfg.Tx("LossStreakText", ""));
                    Gfx.Text(txt, w / 2, y + 2, TXT, win ? cfg.WinColor : cfg.LossColor, Alignment.Center);
                    y += 22;
                }
            }
            return new SizeF(w, h);
        }

        // ---------------------------------------------------------------- top supporters
        SizeF TopPanel(bool draw)
        {
            float w = 236;
            List<Supporter> top = TopList(cfg.TopCount);
            List<string> rows = new List<string>();
            foreach (string r in cfg.Top3Order)
            {
                string k = r.ToLowerInvariant();
                if (k == "top3" && cfg.ShowTop3) rows.Add(k);
                else if (k == "counters" && cfg.ShowCounters) rows.Add(k);
                else if (k == "status" && cfg.ShowStatus && cfg.LiveEnabled) rows.Add(k);
            }
            if (rows.Count == 0) return SizeF.Empty;
            float h = 6;
            foreach (string r in rows)
            {
                if (r == "top3") h += 24 + Math.Max(1, top.Count) * 30;
                else h += 22;
            }
            h += 4;
            if (!draw) return new SizeF(w, h);

            PanelBg(0, 0, w, h, 1);
            float y = 6;
            Color[] medal = { Color.FromArgb(255, 255, 200, 40), Color.FromArgb(255, 200, 210, 220), Color.FromArgb(255, 215, 140, 80) };
            foreach (string r in rows)
            {
                if (r == "top3")
                {
                    Header(cfg.Top3Title, 0, y, w, 22);
                    y += 24;
                    if (top.Count == 0)
                    {
                        Gfx.Text("—", w / 2, y + 6, TXT, TextCol(150), Alignment.Center);
                        y += 30;
                    }
                    for (int i = 0; i < top.Count; i++)
                    {
                        Supporter s = top[i];
                        Color mc = i < 3 ? medal[i] : TextCol(200);
                        Gfx.Rect(6, y + 2, w - 12, 26, U.WithAlpha(mc, 28));
                        Gfx.Text((i + 1).ToString(U.IC), 16, y + 6, TXT, mc, Alignment.Center);
                        Gfx.Image(Avatar(s), 28, y + 3, 24, 24, 255);
                        Gfx.Text(U.Trunc(s.Nick, 16), 58, y + 6, TXT, TextCol(255), Alignment.Left);
                        if (cfg.ShowCoins) Gfx.Text(U.Coins(s.Coins), w - 12, y + 6, TXT, cfg.Accent, Alignment.Right);
                        y += 30;
                    }
                }
                else if (r == "counters")
                {
                    string c = cfg.Tx("EnemiesShort", "E") + " " + AliveCount(true) + "  ·  " +
                               cfg.Tx("AlliesShort", "A") + " " + AliveCount(false) + "  ·  " +
                               cfg.Tx("QueueShort", "Q") + " " + QueueCount();
                    Gfx.Text(c, w / 2, y + 3, SMALL, TextCol(230), Alignment.Center);
                    y += 22;
                }
                else if (r == "status")
                {
                    bool ok = live.Connected;
                    Gfx.Rect(w / 2 - 44, y + 8, 7, 7, ok ? cfg.WinColor : cfg.LossColor);
                    Gfx.Text("TikFinity", w / 2 - 32, y + 3, SMALL, TextCol(220), Alignment.Left);
                    y += 22;
                }
            }
            return new SizeF(w, h);
        }

        // ---------------------------------------------------------------- health
        SizeF HealthPanel(bool draw)
        {
            float w = Math.Max(80, L.HealthWidth);
            bool armor = cfg.ShowArmor;
            float h = 22 + 12 + (armor ? 8 : 0) + 8;
            if (!draw) return new SizeF(w, h);
            Ped pl = Game.Player.Character;
            float frac = 0, af = 0;
            int hpNow = 0, hpMax = 0;
            if (pl.Exists())
            {
                hpMax = Math.Max(1, Function.Call<int>(Hash.GET_ENTITY_MAX_HEALTH, pl) - 100);
                hpNow = Math.Max(0, Function.Call<int>(Hash.GET_ENTITY_HEALTH, pl) - 100);
                frac = U.Clamp(hpNow / (float)hpMax, 0, 1);
                af = U.Clamp(pl.Armor / 100f, 0, 1);
            }
            PanelBg(0, 0, w, h, 0.85f);
            Color hc = frac < 0.25f ? cfg.LossColor : cfg.HealthColor;
            Gfx.Text(cfg.HealthLabel + " " + (frac * 100).ToString("0.0", U.IC) + "%", 8, 3, TXT, TextCol(255), Alignment.Left);
            if (cfg.ShowHealthPoints) Gfx.Text(hpNow + " / " + hpMax, w - 8, 3, TXT, TextCol(220), Alignment.Right);
            Gfx.Bar(8, 22, w - 16, 12, frac, hc, U.WithAlpha(Color.Black, 150));
            if (armor) Gfx.Bar(8, 37, w - 16, 5, af, Color.FromArgb(255, 90, 170, 255), U.WithAlpha(Color.Black, 150));
            return new SizeF(w, h);
        }

        // ---------------------------------------------------------------- gift guide
        string TriggerLabel(Interaction it)
        {
            switch (it.Trigger.ToLowerInvariant())
            {
                case "gift": return it.GiftName.Length > 0 ? it.GiftName : ("#" + it.GiftId);
                case "likes": case "like": return it.LikesCount + " Likes";
                case "follow": return "Follow";
                case "share": return "Share";
                case "subscribe": return "Subscribe";
                case "comment": case "chat": return it.CommentText.Length > 0 ? "\"" + it.CommentText + "\"" : "Comment";
                default: return "";
            }
        }

        List<Interaction> GuideItems()
        {
            List<Interaction> l = new List<Interaction>();
            foreach (Interaction it in cfg.Interactions)
            {
                if (!it.Enabled || !it.ShowInGuide) continue;
                if (it.Trigger.Equals("Key", StringComparison.OrdinalIgnoreCase)) continue;
                l.Add(it);
                if (l.Count >= 60) break;
            }
            return l;
        }

        List<Interaction> GuideVisible()
        {
            List<Interaction> all = GuideItems();
            string mode = cfg.GuideMode.ToLowerInvariant();
            if (mode != "rotate")
            {
                if (all.Count > cfg.GuideMax) all.RemoveRange(cfg.GuideMax, all.Count - cfg.GuideMax);
                return all;
            }
            if (guideOrder.Count != all.Count)
            {
                guideOrder.Clear();
                for (int i = 0; i < all.Count; i++) guideOrder.Add(i);
                guideOffset = 0;
            }
            if (U.Now >= guideNextRotate)
            {
                guideNextRotate = U.Now + (long)(cfg.GuideRotateSeconds * 1000);
                guideOffset += cfg.GuideRotateCount;
                if (guideOffset >= guideOrder.Count)
                {
                    guideOffset = 0;
                    if (cfg.GuideRandom)
                        for (int i = guideOrder.Count - 1; i > 0; i--) { int k = U.Rng.Next(i + 1); int t = guideOrder[i]; guideOrder[i] = guideOrder[k]; guideOrder[k] = t; }
                }
            }
            List<Interaction> r = new List<Interaction>();
            for (int i = 0; i < cfg.GuideRotateCount && i < guideOrder.Count; i++)
                r.Add(all[guideOrder[(guideOffset + i) % guideOrder.Count]]);
            return r;
        }

        SizeF GuidePanel(bool draw)
        {
            List<Interaction> items = GuideVisible();
            if (items.Count == 0) return SizeF.Empty;
            bool list = cfg.GuideMode.Equals("List", StringComparison.OrdinalIgnoreCase);
            float w = list ? 270 : 290;
            float rowH = list ? 26 : 36;
            float titleH = cfg.GuideTitleEnabled && cfg.GuideTitle.Length > 0 ? 26 : 0;
            float h = titleH + items.Count * (rowH + (list ? 0 : 4)) + (list ? 10 : 2);
            if (!draw) return new SizeF(w, h);

            float y = 0;
            if (list) PanelBg(0, 0, w, h, 1);
            if (titleH > 0)
            {
                if (!list) PanelBg(0, 0, w, titleH - 2, 1);
                Header(cfg.GuideTitle, 0, y + 2, w, 22);
                y += titleH;
            }
            if (list) y += 5;
            foreach (Interaction it in items)
            {
                string gift = U.Trunc(TriggerLabel(it), 14);
                string act = U.Trunc(it.Title, 20);
                string gi = ImgPath(it.GiftImage), ai = ImgPath(it.ActionImage);
                if (list)
                {
                    float x = 8;
                    if (cfg.GuideShowGiftImage && Gfx.FileOk(gi)) { Gfx.Image(gi, x, y + 2, 22, 22, 255); x += 26; }
                    Gfx.Text(gift, x, y + 4, TXT, cfg.GuideGiftColor, Alignment.Left);
                    x += Gfx.TextW(gift, TXT) + 6;
                    Gfx.Text(">", x, y + 4, TXT, TextCol(160), Alignment.Left);
                    x += 14;
                    if (cfg.GuideShowActionImage && Gfx.FileOk(ai)) { Gfx.Image(ai, x, y + 2, 22, 22, 255); x += 26; }
                    Gfx.Text(act, x, y + 4, TXT, TextCol(255), Alignment.Left);
                    y += rowH;
                }
                else
                {
                    float gw = w * 0.42f;
                    Gfx.Rect(0, y, gw, rowH, U.WithAlpha(cfg.GuideGiftColor, (int)(cfg.Opacity * 0.9f)));
                    Gfx.Rect(gw, y, w - gw, rowH, U.WithAlpha(U.Mix(cfg.PanelColor, cfg.GuideActionColor, 0.25f), cfg.Opacity));
                    Gfx.Rect(gw, y, 3, rowH, cfg.GuideActionColor);
                    float x = 6;
                    if (cfg.GuideShowGiftImage && Gfx.FileOk(gi)) { Gfx.Image(gi, x, y + 4, 28, 28, 255); x += 32; }
                    Gfx.Text(gift, x, y + 9, TXT, Color.White, Alignment.Left);
                    x = gw + 8;
                    if (cfg.GuideShowActionImage && Gfx.FileOk(ai)) { Gfx.Image(ai, x, y + 4, 28, 28, 255); x += 32; }
                    Gfx.Text(act, x, y + 9, TXT, TextCol(255), Alignment.Left);
                    y += rowH + 4;
                }
            }
            return new SizeF(w, h);
        }

        // ---------------------------------------------------------------- feeds
        SizeF FeedList(bool draw, List<FeedItem> items, string panel, float rowH, float img, float size, bool hype)
        {
            float w = 0;
            foreach (FeedItem f in items)
            {
                float rw = 12 + (f.Avatar != null ? img + 6 : 0) + Gfx.TextW(f.Text, size) + (f.Icon != null && Gfx.FileOk(f.Icon) ? img + 6 : 0);
                if (hype && cfg.HypeShowLevel && f.Level > 0) rw += Gfx.TextW("Lv " + f.Level, SMALL) + 12;
                if (rw > w) w = rw;
            }
            w = Math.Min(Math.Max(w, 150), 520);
            float h = items.Count * (rowH + 4);
            if (!draw) return new SizeF(w, h);
            string al = HAlign(panel);
            float y = 0;
            foreach (FeedItem f in items)
            {
                float a = FeedAlpha(f);
                float rw = 12 + (f.Avatar != null ? img + 6 : 0) + Gfx.TextW(f.Text, size) + (f.Icon != null && Gfx.FileOk(f.Icon) ? img + 6 : 0);
                string lv = hype && cfg.HypeShowLevel && f.Level > 0 ? "Lv " + f.Level : null;
                if (lv != null) rw += Gfx.TextW(lv, SMALL) + 12;
                rw = Math.Min(rw, w);
                float x = al == "right" ? w - rw : (al == "center" ? (w - rw) / 2 : 0);
                float slide = (1 - Math.Min(1f, (U.Now - f.Start) / 250f)) * 30 * (al == "right" ? 1 : -1);
                x += slide;
                PanelBg(x, y, rw, rowH, a);
                if (hype) Gfx.Border(x, y, rw, rowH, 1, U.WithAlpha(cfg.HypeColor, (int)(220 * a)));
                float cx = x + 6;
                if (f.Avatar != null) { Gfx.Image(f.Avatar, cx, y + (rowH - img) / 2, img, img, (int)(255 * a)); cx += img + 6; }
                if (lv != null)
                {
                    float lw = Gfx.TextW(lv, SMALL) + 8;
                    Gfx.Rect(cx, y + (rowH - 14) / 2, lw, 14, U.WithAlpha(cfg.HypeColor, (int)(230 * a)));
                    Gfx.Text(lv, cx + lw / 2, y + (rowH - 14) / 2, SMALL * 0.9f, U.WithAlpha(Color.Black, (int)(255 * a)), Alignment.Center);
                    cx += lw + 4;
                }
                Gfx.Text(f.Text, cx, y + (rowH - Gfx.LineH(size)) / 2, size, U.WithAlpha(f.Col, (int)(255 * a)), Alignment.Left);
                cx += Gfx.TextW(f.Text, size) + 6;
                if (f.Icon != null && Gfx.FileOk(f.Icon)) Gfx.Image(f.Icon, cx, y + (rowH - img) / 2, img, img, (int)(255 * a));
                y += rowH + 4;
            }
            return new SizeF(w, h);
        }

        SizeF NotifPanel(bool draw) { return FeedList(draw, notifs, "Notif", 30, 24, TXT, false); }
        SizeF FeedPanel(bool draw) { return FeedList(draw, feed, "Feed", 26, 20, TXT, false); }
        SizeF HypePanel(bool draw) { return FeedList(draw, hypes, "Hype", 38, 30, 0.38f, true); }

        // ---------------------------------------------------------------- overhead
        void Overheads()
        {
            if (!cfg.OverheadEnabled) return;
            Ped pl = Game.Player.Character;
            if (!pl.Exists()) return;
            Vector3 camPos = GameplayCamera.Position;
            Gfx.Origin(0, 0, 1);
            foreach (Tracked t in tracked)
            {
                if (!t.Leader || t.DeadAt != 0 || t.Ped == null || !t.Ped.Exists()) continue;
                Entity e = t.Veh != null && t.Veh.Exists() && t.Ped.IsInVehicle() ? (Entity)t.Veh : t.Ped;
                Vector3 wp = e.Position + new Vector3(0, 0, cfg.OverheadHeight + (e is Vehicle ? 0.6f : 0f));
                float dist = wp.DistanceTo(camPos);
                if (dist > 90f) continue;
                PointF sp = Screen.WorldToScreen(wp);
                if (sp.X == 0 && sp.Y == 0) continue;
                float s = U.Clamp(1.35f - dist / 70f, 0.5f, 1.25f);
                float y = sp.Y;
                if (cfg.OverheadHealth)
                {
                    int hp = Math.Max(0, t.Ped.Health - 100);
                    float fr = U.Clamp(hp / (float)t.MaxHp, 0, 1);
                    float bw = 56 * s;
                    Gfx.RectAbs(sp.X - bw / 2, y - 6 * s, bw, 5 * s, U.WithAlpha(Color.Black, 170));
                    Gfx.RectAbs(sp.X - bw / 2, y - 6 * s, bw * fr, 5 * s, t.Enemy ? cfg.LossColor : cfg.WinColor);
                    y -= 8 * s;
                }
                if (cfg.OverheadName && t.Sup != null)
                {
                    float ts = TXT * s * cfg.FontScale;
                    y -= 40 * ts + 2;
                    Gfx.TextAbs(U.Trunc(t.Sup.Nick, 18), sp.X, y, ts, t.Enemy ? cfg.LossColor : cfg.WinColor, Alignment.Center, Gfx.F);
                }
                if (cfg.OverheadAvatar)
                {
                    float sz = 30 * s;
                    y -= sz + 2;
                    Gfx.ImageAbs(Avatar(t.Sup), sp.X - sz / 2, y, sz, sz, 255);
                }
            }
        }

        // ---------------------------------------------------------------- end screen
        void EndScreen()
        {
            if (phase != Phase.Ended || !cfg.EndScreenEnabled || U.Now >= endScreenUntil) return;
            Gfx.Origin(0, 0, 1);
            float cx = frameX + frameW / 2;
            Color c = lastWin ? cfg.WinColor : cfg.LossColor;
            float pulse = 1f + 0.04f * (float)Math.Sin(U.Now / 180.0);
            Gfx.RectAbs(frameX, 190, frameW, 110, U.WithAlpha(cfg.PanelColor, 170));
            Gfx.RectAbs(frameX, 190, frameW, 3, c);
            Gfx.RectAbs(frameX, 297, frameW, 3, c);
            Gfx.TextAbs(lastWin ? cfg.Tx("WinText", "WIN") : cfg.Tx("LossText", "LOSS"), cx, 205, 1.5f * pulse * cfg.FontScale, c, Alignment.Center, Gfx.F);
            if (cfg.MvpEnabled && mvp != null)
            {
                float y = 320;
                float bw = Math.Min(frameW - 20, 360);
                Gfx.RectAbs(cx - bw / 2, y, bw, 96, U.WithAlpha(cfg.PanelColor, 200));
                Gfx.RectAbs(cx - bw / 2, y, bw, 2, cfg.HypeColor);
                Gfx.ImageAbs(Avatar(mvp), cx - bw / 2 + 12, y + 12, 72, 72, 255);
                float tx = cx - bw / 2 + 96;
                Gfx.TextAbs(cfg.Tx("MvpText", "MVP"), tx, y + 8, 0.5f * cfg.FontScale, cfg.HypeColor, Alignment.Left, Gfx.F);
                Gfx.TextAbs(U.Trunc(mvp.Nick, 20), tx, y + 36, 0.42f * cfg.FontScale, cfg.TextColor, Alignment.Left, Gfx.F);
                Gfx.TextAbs(mvpReason, tx, y + 62, 0.3f * cfg.FontScale, U.WithAlpha(cfg.TextColor, 200), Alignment.Left, Gfx.F);
            }
        }
    }
}
