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
using System.Runtime.InteropServices;
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

        // values of another file win (HUD profile over the main INI)
        public void Merge(Ini other)
        {
            foreach (KeyValuePair<string, Dictionary<string, string>> sec in other.data)
            {
                Dictionary<string, string> d;
                if (!data.TryGetValue(sec.Key, out d)) { d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); data[sec.Key] = d; }
                foreach (KeyValuePair<string, string> kv in sec.Value) d[kv.Key] = kv.Value;
            }
        }

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

        // Every http(s) url found under keys containing "profilepicture" or "avatar" (breadth-first order).
        public static List<string> FindAvatars(object root)
        {
            List<string> r = new List<string>();
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
                        if (k.Contains("profilepicture") || k.Contains("avatar")) AllUrls(kv.Value, 0, r);
                    }
                    foreach (object v in d.Values) if (v is Dictionary<string, object> || v is List<object>) q.Enqueue(v);
                    continue;
                }
                List<object> l = o as List<object>;
                if (l != null) foreach (object v in l) if (v is Dictionary<string, object> || v is List<object>) q.Enqueue(v);
            }
            return r;
        }

        static void AllUrls(object o, int depth, List<string> r)
        {
            if (o == null || depth > 6 || r.Count > 12) return;
            string s = o as string;
            if (s != null)
            {
                if (s.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !r.Contains(s)) r.Add(s);
                return;
            }
            List<object> l = o as List<object>;
            if (l != null) { foreach (object v in l) AllUrls(v, depth + 1, r); return; }
            Dictionary<string, object> d = o as Dictionary<string, object>;
            if (d != null) foreach (object v in d.Values) AllUrls(v, depth + 1, r);
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
                p.Show = ini.B(sec, n + "Show", true);
                l.P[n] = p;
            }
            l.HealthWidth = ini.F(sec, "HealthWidth", vertical ? 220 : 260);
            l.Scale = U.Clamp(ini.F(sec, "Scale", vertical ? 0.8f : 1f), 0.3f, 3f);
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
        public float KeepSeconds;
        public int Amount = 5;
        public string Weather = "Random";
        public string Music = "";          // music/events/<file> played while this event is on
        public string Aura = "Default";
        public string AuraColor = "";
        public int GiftCoins;
        public string GiftMatch = "Both";
        public bool Spotlight;
        public string SpotlightText = "";

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
            it.KeepSeconds = Math.Max(0f, ini.F(s, "KeepSeconds", 0));
            it.Amount = Math.Max(1, ini.I(s, "Amount", 5));
            it.Weather = ini.S(s, "Weather", "Random");
            it.Music = ini.S(s, "Music", "");
            it.Aura = ini.S(s, "Aura", "Default");
            it.AuraColor = ini.S(s, "AuraColor", "");
            it.GiftCoins = Math.Max(0, ini.I(s, "GiftCoins", 0));
            it.GiftMatch = ini.S(s, "GiftMatch", "Both");
            it.Spotlight = ini.B(s, "Spotlight", it.Action == "MafiaCar" || it.Action == "MotoHitman");
            it.SpotlightText = ini.S(s, "SpotlightText", "");
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
        public bool RoundReset, RoundClearEnemies, RoundClearAllies, RoundClearEffects, RoundHoldQueue;
        public string RoundVanishEffect;
        public float DurationMinutes, EndScreenSeconds;
        // [Queue]
        public int MaxEnemies, MaxAllies, MaxPerEvent, DelayMs, MaxPerSupporter;
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
        public List<string> StyleCycle, FontCycle, LookCycle;
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
        public bool OverheadEnabled, OverheadAvatar, OverheadName, OverheadHealth;
        public float OverheadHeight;
        public string AvatarShape;
        public bool StylePalette, OverheadRing, OverheadLevel;
        public string TextMode, UnicodeFont, HudDesign, HudDesignColor, DesignCoinsWord;
        public Keys DesignKey, DesignColorKey;
        public bool UnicodeBold, StripEmoji, FancyToAscii;
        public float UnicodeSize;
        public int MaxTextTextures;
        public string ScoreVariant, Top3Variant, HealthVariant, NotifVariant, FeedAnimation, OverheadStyle;
        // [Aura]
        public bool AuraEnabled, AuraKingCrown;
        public string AuraEnemy, AuraAlly, FxFire, FxSmoke, FxElectric, FxSparkles;
        public Color AuraEnemyColor, AuraAllyColor, AuraKingColor;
        public float AuraIntensity, AuraRange, AuraRingSize, AuraMaxDistance, AuraFxScale;
        // [LiveTest]
        public bool LiveTestEnabled, LiveTestBadge;
        public Keys LiveTestKey;
        public List<string> LiveTestNames, LiveTestAvatars, LiveTestComments;
        public int LiveTestMinMs, LiveTestMaxMs, LtGift, LtLike, LtComment, LtFollow, LtShare, LtJoin, LiveTestComboChance, LiveTestMaxCombo;
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
        public string GfxPreset;
        public Keys GfxKey;
        // [Music]
        public bool MusicEnabled, MusicBackground, MusicShuffle;
        public Keys MusicKey;
        public int MusicVolume, MusicEventVolume, MusicDeathVolume;
        // [Camera] close "TikTok" camera
        public bool ZoomOnStart, ZoomNormalWhenAiming;
        public Keys ZoomKey, ZoomInKey, ZoomOutKey;
        public float ZoomDistance, ZoomHeight, ZoomSide, ZoomFov, ZoomVehDistance, ZoomVehHeight;
        // [DamageFx]
        public bool DmgEnabled, DmgNumbers, DmgGhost, NoBlood;
        // [Death] extras
        public string DeathOverlay;
        public int ExtraDancers;
        public bool KillerBanner;
        // [Texts]
        public Dictionary<string, string> T = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // [Layout] / [LayoutVertical]
        public LayoutCfg Normal, Vertical;
        public bool SpotEnabled;
        public float SpotSeconds, SpotOffsetY;
        public long SpotMinCoins;
        public string SpotSound;
        public bool FxEnabled, FxPopup, FxBurst, FxBuffAura;
        public float FxPopupSeconds;
        // [Effects] duration control of timed effects
        public float FxMaxSeconds, QuakeStrength;
        public string FxStack;
        public bool FxBars, BlackoutNight, QuakeRagdollPlayer;
        public string FxBarsPos;
        // [Events] panel of the running events (where the gift guide was)
        public bool EventsEnabled, EventsSpawns, EventsInstant, EventsTitleEnabled;
        public int EventsMax;
        public float EventsInstantSeconds;
        public string EventsTitle;
        public int FxBarsMax;
        public Keys StopEffectsKey;
        public Color FxSpeedColor, FxGodColor, FxJumpColor, FxFreezeColor, FxWeaponColor, FxHealColor;
        public string FxSpeedTrail;
        public bool VAutoArrange;
        public float VGap, VBottomMargin;
        public int VMaxNotif, VMaxFeed, VMaxHype;
        // [KillEffects]
        public bool KillFxEnabled, KillOnlyPlayer, KillSoundEnabled;
        public string KillEffect, KillSound;
        public Color SmokeColor;
        // [Death]
        public bool CelebEnabled, CelebDance, CelebCoffin, DeathSoundEnabled, CustomDeath;
        public float RespawnSeconds;
        public string RespawnMode;
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
            c.StopEffectsKey = ini.K("General", "StopEffectsKey", "End");
            c.FxMaxSeconds = Math.Max(0f, ini.F("Effects", "MaxSeconds", 300));
            c.FxStack = ini.S("Effects", "Stack", "Reset");
            c.FxBars = ini.B("Effects", "ShowBars", true);
            c.FxBarsMax = U.Clamp(ini.I("Effects", "MaxBars", 4), 1, 8);
            // where the timer cards of the effects are drawn: Player (above the head) | Top | Left | Right | Bottom (under the health bar)
            c.FxBarsPos = ini.S("Effects", "BarsPosition", "Events");
            c.EventsEnabled = ini.B("Events", "Enabled", true);
            c.EventsMax = U.Clamp(ini.I("Events", "MaxItems", 6), 1, 12);
            c.EventsSpawns = ini.B("Events", "ShowChases", true);
            c.EventsInstant = ini.B("Events", "ShowInstant", true);
            c.EventsInstantSeconds = U.Clamp(ini.F("Events", "InstantSeconds", 5), 1, 60);
            c.EventsTitleEnabled = ini.B("Events", "TitleEnabled", false);
            c.EventsTitle = ini.S("Events", "Title", "الأحداث");
            c.BlackoutNight = ini.B("Effects", "BlackoutNight", true);
            c.QuakeStrength = U.Clamp(ini.F("Effects", "QuakeStrength", 2.5f), 0.2f, 6f);
            c.QuakeRagdollPlayer = ini.B("Effects", "QuakeRagdollPlayer", true);
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
            c.RoundReset = ini.B("Challenge", "ResetOnNewRound", true);
            c.RoundClearEnemies = ini.B("Challenge", "ClearEnemies", true);
            c.RoundClearAllies = ini.B("Challenge", "ClearAllies", true);
            c.RoundClearEffects = ini.B("Challenge", "ClearEffects", true);
            c.RoundHoldQueue = ini.B("Challenge", "HoldQueue", true);
            c.RoundVanishEffect = ini.S("Challenge", "VanishEffect", "SoftSmoke");

            c.MaxEnemies = U.Clamp(ini.I("Queue", "MaxEnemies", 15), 1, 100);
            c.MaxAllies = U.Clamp(ini.I("Queue", "MaxAllies", 15), 1, 100);
            // fair share: one supporter never has more than this on the field at once (0 = no limit),
            // the rest waits and supporters take turns
            c.MaxPerSupporter = Math.Max(0, ini.I("Queue", "MaxPerSupporter", 10));
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
            c.HudStyle = ini.S("Hud", "Style", "Broadcast");
            c.StyleCycle = ini.L("Hud", "StyleCycle", "Broadcast,Cyber,Royal,Hologram,Gradient,Stream,Carbon,Classic,Glass,Neon,Minimal,Esports,Retro");
            // F11 goes through complete looks (a design or a style) so every panel always matches
            c.LookCycle = ini.L("Hud", "LookCycle", "Duo,Arena,Broadcast,Podium,Cards,Esports,Minimal,Classic,Luxury,Aurora,Ember,Cyber,Royal,Hologram,Gradient,Stream,Carbon,Glass,Neon,Retro");
            c.StylePalette = ini.B("Hud", "StylePalette", true);
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
            c.Top3Order = ini.L("Hud", "Top3Order", "Top3,Status");
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
            c.OverheadEnabled = ini.B("Hud", "OverheadEnabled", true);
            c.OverheadAvatar = ini.B("Hud", "OverheadAvatar", true);
            c.OverheadName = ini.B("Hud", "OverheadName", true);
            c.OverheadHealth = ini.B("Hud", "OverheadHealth", true);
            // extra height above the head (meters); old configs used 1.2 from the body center
            c.OverheadHeight = ini.F("Hud", "OverheadHeight", 0f);
            if (c.OverheadHeight >= 0.8f) c.OverheadHeight = 0f;
            c.AvatarShape = ini.S("Hud", "AvatarShape", "Circle");
            c.ScoreVariant = ini.S("Hud", "ScoreVariant", "Classic");
            c.Top3Variant = ini.S("Hud", "Top3Variant", "List");
            c.HealthVariant = ini.S("Hud", "HealthVariant", "Bar");
            c.NotifVariant = ini.S("Hud", "NotifVariant", "Card");
            c.FeedAnimation = ini.S("Hud", "FeedAnimation", "Slide");
            c.OverheadStyle = ini.S("Hud", "OverheadStyle", "Classic");
            c.OverheadRing = ini.B("Hud", "OverheadRing", true);
            c.OverheadLevel = ini.B("Hud", "OverheadLevel", true);
            c.TextMode = ini.S("Hud", "TextMode", "Auto");
            c.HudDesign = ini.S("Hud", "Design", "Arena");
            c.HudDesignColor = ini.S("Hud", "DesignColor", "GOLD");
            c.DesignCoinsWord = ini.S("Hud", "DesignCoinsWord", "");
            c.DesignKey = ini.K("Hud", "DesignKey", "F10");
            c.DesignColorKey = ini.K("Hud", "DesignColorKey", "F5");
            c.UnicodeFont = ini.S("Hud", "UnicodeFont", "Segoe UI");
            c.UnicodeBold = ini.B("Hud", "UnicodeBold", true);
            c.UnicodeSize = U.Clamp(ini.F("Hud", "UnicodeSize", 1f), 0.4f, 3f);
            c.StripEmoji = ini.B("Hud", "StripEmoji", true);
            c.FancyToAscii = ini.B("Hud", "FancyToAscii", true);
            c.MaxTextTextures = U.Clamp(ini.I("Hud", "MaxTextTextures", 1500), 50, 20000);

            c.AuraEnabled = ini.B("Aura", "Enabled", true);
            c.AuraEnemy = ini.S("Aura", "EnemyAura", "Ring");
            c.AuraAlly = ini.S("Aura", "AllyAura", "Glow");
            c.AuraEnemyColor = ini.C("Aura", "EnemyColor", "#ff3b5c");
            c.AuraAllyColor = ini.C("Aura", "AllyColor", "#00e676");
            c.AuraKingCrown = ini.B("Aura", "KingCrown", true);
            c.AuraKingColor = ini.C("Aura", "KingColor", "#ffc828");
            c.AuraIntensity = U.Clamp(ini.F("Aura", "Intensity", 4f), 0f, 30f);
            c.AuraRange = U.Clamp(ini.F("Aura", "Range", 3f), 0.5f, 20f);
            c.AuraRingSize = U.Clamp(ini.F("Aura", "RingSize", 1.4f), 0.2f, 6f);
            c.AuraMaxDistance = U.Clamp(ini.F("Aura", "MaxDistance", 60f), 5f, 300f);
            c.AuraFxScale = U.Clamp(ini.F("Aura", "FxScale", 1f), 0.1f, 5f);
            c.FxFire = ini.S("Aura", "FireFx", "core|ent_amb_torch_fire");
            c.FxSmoke = ini.S("Aura", "SmokeFx", "core|exp_grd_bzgas_smoke");
            c.FxElectric = ini.S("Aura", "ElectricFx", "core|ent_amb_elec_crackle");
            c.FxSparkles = ini.S("Aura", "SparklesFx", "core|ent_amb_sparking_wires");

            c.LiveTestEnabled = ini.B("LiveTest", "Enabled", true);
            c.LiveTestKey = ini.K("LiveTest", "Key", "F9");
            c.LiveTestBadge = ini.B("LiveTest", "ShowBadge", true);
            c.LiveTestNames = ini.L("LiveTest", "Names", "أحمد,سارة,Yassine,Nora,Karim,Lina,Omar,Hiba,Adam,Salma,Mehdi,Imane,Zakaria,Khadija,Anas,Rania");
            c.LiveTestAvatars = ini.L("LiveTest", "AvatarUrls", "");
            c.LiveTestComments = ini.L("LiveTest", "Comments", "GG,🔥,يلاه,اقتلوه,عاونوه,واو");
            c.LiveTestMinMs = U.Clamp(ini.I("LiveTest", "MinDelayMs", 700), 50, 60000);
            c.LiveTestMaxMs = Math.Max(c.LiveTestMinMs, ini.I("LiveTest", "MaxDelayMs", 2200));
            c.LtGift = Math.Max(0, ini.I("LiveTest", "GiftWeight", 45));
            c.LtLike = Math.Max(0, ini.I("LiveTest", "LikeWeight", 25));
            c.LtComment = Math.Max(0, ini.I("LiveTest", "CommentWeight", 10));
            c.LtFollow = Math.Max(0, ini.I("LiveTest", "FollowWeight", 8));
            c.LtShare = Math.Max(0, ini.I("LiveTest", "ShareWeight", 6));
            c.LtJoin = Math.Max(0, ini.I("LiveTest", "JoinWeight", 6));
            c.LiveTestComboChance = U.Clamp(ini.I("LiveTest", "ComboChance", 25), 0, 100);
            c.LiveTestMaxCombo = U.Clamp(ini.I("LiveTest", "MaxCombo", 15), 2, 200);

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
            c.VipJoinText = ini.S("Hype", "VipJoinText", "⚠️ احذر! لقد دخل {name}");
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
            c.GfxPreset = ini.S("Graphics", "Preset", "Off");
            c.GfxKey = ini.K("Graphics", "Key", "Home");
            c.MusicEnabled = ini.B("Music", "Enabled", true);
            c.MusicKey = ini.K("Music", "Key", "F4");
            c.MusicBackground = ini.B("Music", "Background", true);
            c.MusicShuffle = ini.B("Music", "Shuffle", true);
            c.MusicVolume = U.Clamp(ini.I("Music", "Volume", 60), 0, 100);
            c.MusicEventVolume = U.Clamp(ini.I("Music", "EventVolume", 90), 0, 100);
            c.MusicDeathVolume = U.Clamp(ini.I("Music", "DeathVolume", 90), 0, 100);
            c.ZoomKey = ini.K("Camera", "ZoomKey", "F2");
            c.ZoomOnStart = ini.B("Camera", "ZoomOnStart", true);
            c.ZoomInKey = ini.K("Camera", "ZoomInKey", "PageUp");
            c.ZoomOutKey = ini.K("Camera", "ZoomOutKey", "PageDown");
            c.ZoomDistance = U.Clamp(ini.F("Camera", "ZoomDistance", 2.3f), 0.8f, 12f);
            c.ZoomHeight = U.Clamp(ini.F("Camera", "ZoomHeight", 0.55f), -1f, 3f);
            c.ZoomSide = U.Clamp(ini.F("Camera", "ZoomSide", 0.45f), -3f, 3f);
            c.ZoomFov = U.Clamp(ini.F("Camera", "ZoomFov", 50f), 20f, 90f);
            c.ZoomVehDistance = U.Clamp(ini.F("Camera", "ZoomVehDistance", 5.5f), 1.5f, 20f);
            c.ZoomVehHeight = U.Clamp(ini.F("Camera", "ZoomVehHeight", 1.3f), -1f, 5f);
            c.ZoomNormalWhenAiming = ini.B("Camera", "ZoomNormalWhenAiming", true);
            c.DmgEnabled = ini.B("DamageFx", "Enabled", true);
            c.DmgNumbers = ini.B("DamageFx", "Numbers", true);
            c.DmgGhost = ini.B("DamageFx", "Ghost", true);
            // TikTok rules: no blood on the characters (the game's blood is removed all the time)
            c.NoBlood = ini.B("DamageFx", "NoBlood", true);
            c.DensityEnabled = ini.B("Graphics", "DensityEnabled", false);
            c.PedDensity = U.Clamp(ini.F("Graphics", "PedDensity", 1), 0, 3);
            c.VehicleDensity = U.Clamp(ini.F("Graphics", "VehicleDensity", 1), 0, 3);

            string[] texts = {
                "WinText=فوز", "LossText=خسارة", "WinShort=فوز", "LossShort=خسارة", "MvpText=MVP",
                "MvpWinReason=ساعدك باش تربح", "MvpLossReason=هو السبب ف الخسارة", "KillFeedText={name} قُتل",
                "WinStreakText=انتصارات متتالية", "LossStreakText=خسارات متتالية", "EnemiesShort=أعداء",
                "AlliesShort=مساعدين", "KillsShort=قتلات", "QueueShort=الطابور", "PausedText=إيقاف مؤقت", "RespawnText=الرجوع بعد", "ResumedText=استئناف",
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
            c.SpotEnabled = ini.B("Spotlight", "Enabled", true);
            c.SpotSeconds = U.Clamp(ini.F("Spotlight", "Seconds", 3), 1, 15);
            c.SpotMinCoins = Math.Max(0, ini.I("Spotlight", "MinCoins", 1000));
            c.SpotSound = ini.S("Spotlight", "Sound", "CHALLENGE_UNLOCKED|HUD_AWARDS");
            c.SpotOffsetY = ini.F("Spotlight", "OffsetY", 0);
            c.FxEnabled = ini.B("PlayerFx", "Enabled", true);
            c.FxPopup = ini.B("PlayerFx", "Popup", true);
            c.FxPopupSeconds = U.Clamp(ini.F("PlayerFx", "PopupSeconds", 2.5f), 0.5f, 10f);
            c.FxBurst = ini.B("PlayerFx", "Burst", true);
            c.FxBuffAura = ini.B("PlayerFx", "BuffAura", true);
            c.FxSpeedColor = ini.C("PlayerFx", "SpeedColor", "#29b6ff");
            c.FxGodColor = ini.C("PlayerFx", "GodColor", "#ffc828");
            c.FxJumpColor = ini.C("PlayerFx", "JumpColor", "#39ff14");
            c.FxFreezeColor = ini.C("PlayerFx", "FreezeColor", "#bff4ff");
            c.FxWeaponColor = ini.C("PlayerFx", "WeaponColor", "#ff9f1c");
            c.FxHealColor = ini.C("PlayerFx", "HealColor", "#00e676");
            c.FxSpeedTrail = ini.S("PlayerFx", "SpeedTrail", "core|ent_amb_elec_crackle");
            c.VAutoArrange = ini.B("LayoutVertical", "AutoArrange", true);
            c.VGap = U.Clamp(ini.F("LayoutVertical", "Gap", 6), 0, 60);
            c.VBottomMargin = U.Clamp(ini.F("LayoutVertical", "BottomMargin", 60), 0, 300);
            c.VMaxNotif = U.Clamp(ini.I("LayoutVertical", "MaxNotif", 3), 0, 20);
            c.VMaxFeed = U.Clamp(ini.I("LayoutVertical", "MaxFeed", 2), 0, 20);
            c.VMaxHype = U.Clamp(ini.I("LayoutVertical", "MaxHype", 2), 0, 10);

            c.KillFxEnabled = ini.B("KillEffects", "Enabled", true);
            c.KillEffect = ini.S("KillEffects", "Effect", "SoftSmoke");
            c.SmokeColor = ini.C("KillEffects", "SmokeColor", "#33ccff");
            c.KillOnlyPlayer = ini.B("KillEffects", "OnlyPlayerKills", false);
            c.KillSoundEnabled = ini.B("KillEffects", "SoundEnabled", false);
            c.KillSound = ini.S("KillEffects", "Sound", "");

            c.CelebEnabled = ini.B("Death", "CelebrationEnabled", true);
            c.CelebSeconds = U.Clamp(ini.F("Death", "Seconds", 8), 1, 60);
            c.CustomDeath = ini.B("Death", "CustomDeath", true);
            c.RespawnSeconds = U.Clamp(ini.F("Death", "RespawnSeconds", 6), 1, 60);
            c.RespawnMode = ini.S("Death", "RespawnMode", "Here");
            c.CelebDance = ini.B("Death", "Dance", true);
            c.DanceDict = ini.S("Death", "DanceDict", "missfbi3_sniping");
            c.DanceAnim = ini.S("Death", "DanceAnim", "dance_m_default");
            c.CelebCoffin = ini.B("Death", "Coffin", false);
            c.DeathOverlay = ini.S("Death", "Overlay", "Clear");        // Clear (the game stays visible) | Dim
            c.ExtraDancers = U.Clamp(ini.I("Death", "ExtraDancers", 3), 0, 6);
            c.KillerBanner = ini.B("Death", "KillerBanner", true);
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
        public string UserKey = "", Nick = "";
        public List<string> Avatars = new List<string>();
        public bool Sim;
        public Color SimColor;
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
        public string Key = "", Nick = "";
        public List<string> AvatarUrls = new List<string>();
        public bool Sim;
        public Color SimColor;
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
        public string AuraType = "None";
        public Color AuraCol = Color.White;
        public int LoopFx;
    }

    class FeedItem
    {
        public string Text = "";
        public List<string> Parts;  // template pieces + values (Arabic-safe layout)
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
    //  WebP (and any other format Windows knows) through WIC, for TikTok avatars.
    //  System.Drawing cannot read .webp; Windows 10/11 ship a WebP WIC codec.
    // ------------------------------------------------------------------------
    static class Wic
    {
        [ComImport, Guid("ec5ec8a9-c395-4314-9c77-54d7a935ff70"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IWICImagingFactory
        {
            [PreserveSig]
            int CreateDecoderFromFilename([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr vendor, uint access, int options, out IWICBitmapDecoder decoder);
        }

        [ComImport, Guid("9edde9e7-8dee-47ea-99df-e6faf2ed44bf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IWICBitmapDecoder
        {
            void QueryCapability();
            void Initialize();
            void GetContainerFormat();
            void GetDecoderInfo();
            void CopyPalette();
            void GetMetadataQueryReader();
            void GetPreview();
            void GetColorContexts();
            void GetThumbnail();
            void GetFrameCount();
            [PreserveSig]
            int GetFrame(uint index, out IWICBitmapSource frame);
        }

        [ComImport, Guid("00000120-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IWICBitmapSource
        {
            [PreserveSig]
            int GetSize(out uint width, out uint height);
            void GetPixelFormat();
            void GetResolution();
            void CopyPalette();
            [PreserveSig]
            int CopyPixels(IntPtr rect, uint stride, uint size, [Out] byte[] buffer);
        }

        [DllImport("windowscodecs.dll")]
        static extern int WICConvertBitmapSource(ref Guid dstFormat, IWICBitmapSource src, out IWICBitmapSource dst);

        static readonly Guid FactoryClsid = new Guid("cacaf262-9370-4615-a13b-9f5539da4c0a");
        static Guid Bgra32 = new Guid("6fddc324-4e03-4bfe-b185-3d77768dc90f");

        public static Bitmap Decode(byte[] data)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "tikarena_" + Guid.NewGuid().ToString("N") + ".img");
            object fac = null;
            IWICBitmapDecoder dec = null;
            IWICBitmapSource frame = null, conv = null;
            try
            {
                File.WriteAllBytes(tmp, data);
                fac = Activator.CreateInstance(Type.GetTypeFromCLSID(FactoryClsid));
                if (((IWICImagingFactory)fac).CreateDecoderFromFilename(tmp, IntPtr.Zero, 0x80000000, 0, out dec) != 0 || dec == null) return null;
                if (dec.GetFrame(0, out frame) != 0 || frame == null) return null;
                if (WICConvertBitmapSource(ref Bgra32, frame, out conv) != 0 || conv == null) return null;
                uint w, h;
                conv.GetSize(out w, out h);
                if (w == 0 || h == 0 || w > 4096 || h > 4096) return null;
                byte[] buf = new byte[w * h * 4];
                if (conv.CopyPixels(IntPtr.Zero, w * 4, (uint)buf.Length, buf) != 0) return null;
                Bitmap bmp = new Bitmap((int)w, (int)h, PixelFormat.Format32bppArgb);
                BitmapData bd = bmp.LockBits(new Rectangle(0, 0, (int)w, (int)h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                for (int y = 0; y < h; y++) Marshal.Copy(buf, (int)(y * w * 4), new IntPtr(bd.Scan0.ToInt64() + y * bd.Stride), (int)(w * 4));
                bmp.UnlockBits(bd);
                return bmp;
            }
            catch { return null; }
            finally
            {
                try { if (conv != null) Marshal.ReleaseComObject(conv); } catch { }
                try { if (frame != null) Marshal.ReleaseComObject(frame); } catch { }
                try { if (dec != null) Marshal.ReleaseComObject(dec); } catch { }
                try { if (fac != null) Marshal.ReleaseComObject(fac); } catch { }
                try { File.Delete(tmp); } catch { }
            }
        }
    }

    // ------------------------------------------------------------------------
    //  Profile pictures: download -> round/square 128x128 PNG in cache/
    //  - several candidate urls per user (TikFinity sends webp + jpeg variants)
    //  - jpeg/png first, webp decoded through WIC, retry later on failure
    //  - optional colored ring variants for overhead avatars
    //  - generated avatars (initials) for Live Test viewers
    // ------------------------------------------------------------------------
    class Avatars
    {
        class AJob { public string Id, Target; public List<string> Urls; public Color Ring; public string Source; }

        readonly ConcurrentQueue<AJob> work = new ConcurrentQueue<AJob>();
        readonly ConcurrentDictionary<string, string> ready = new ConcurrentDictionary<string, string>();
        readonly ConcurrentDictionary<string, long> pending = new ConcurrentDictionary<string, long>();
        readonly ConcurrentDictionary<string, int> fails = new ConcurrentDictionary<string, int>();
        readonly AutoResetEvent signal = new AutoResetEvent(false);
        Thread thread;
        volatile bool run;
        int logged;
        public string CacheDir = "";
        public bool Circle = true;
        public string DefaultFile;
        public volatile int Downloaded, Failed;

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

        // Local png for a user (or the default one while downloading).
        public string Get(string key, List<string> urls)
        {
            if (string.IsNullOrEmpty(key)) return DefaultFile;
            string id = U.SafeName(key);
            string file;
            if (ready.TryGetValue(id, out file)) return file;
            string target = Path.Combine(CacheDir, id + Suffix);
            if (urls == null || urls.Count == 0) return DefaultFile;
            long retryAt;
            if (pending.TryGetValue(id, out retryAt) && (retryAt == 0 || U.Now < retryAt)) return DefaultFile;
            pending[id] = 0;
            AJob j = new AJob();
            j.Id = id; j.Target = target; j.Urls = new List<string>(urls);
            work.Enqueue(j);
            signal.Set();
            return DefaultFile;
        }

        // Same picture with a colored ring (made in the background, base picture meanwhile).
        public string Ring(string baseFile, Color ring)
        {
            if (string.IsNullOrEmpty(baseFile)) return baseFile;
            string hex = ring.R.ToString("x2") + ring.G.ToString("x2") + ring.B.ToString("x2");
            string id = "ring|" + baseFile + "|" + hex;
            string file;
            if (ready.TryGetValue(id, out file)) return file;
            if (pending.TryAdd(id, 0))
            {
                AJob j = new AJob();
                j.Id = id; j.Source = baseFile; j.Ring = ring;
                j.Target = baseFile.Substring(0, baseFile.Length - 4) + "_r" + hex + ".png";
                work.Enqueue(j);
                signal.Set();
            }
            return baseFile;
        }

        // Generated avatar (colored gradient + initials) for simulated viewers. Main thread, once per user.
        public string Synthetic(string key, string name, Color color)
        {
            string id = U.SafeName(key);
            string file;
            if (ready.TryGetValue(id, out file)) return file;
            string target = Path.Combine(CacheDir, "sim_" + id + Suffix);
            try
            {
                if (!File.Exists(target))
                {
                    using (Bitmap b = new Bitmap(128, 128, PixelFormat.Format32bppArgb))
                    using (Graphics g = Graphics.FromImage(b))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                        g.Clear(Color.Transparent);
                        Color c2 = Color.FromArgb(255, Math.Max(0, color.R - 90), Math.Max(0, color.G - 90), Math.Max(0, color.B - 90));
                        using (LinearGradientBrush br = new LinearGradientBrush(new Rectangle(0, 0, 128, 128), color, c2, 45f))
                        {
                            if (Circle) g.FillEllipse(br, 0, 0, 127, 127); else g.FillRectangle(br, 0, 0, 128, 128);
                        }
                        string ini = string.IsNullOrEmpty(name) ? "?" : name.Substring(0, 1).ToUpperInvariant();
                        using (System.Drawing.Font f = new System.Drawing.Font("Segoe UI", 56, FontStyle.Bold, GraphicsUnit.Pixel))
                        using (StringFormat sf = new StringFormat())
                        {
                            sf.Alignment = StringAlignment.Center;
                            sf.LineAlignment = StringAlignment.Center;
                            g.DrawString(ini, f, Brushes.White, new RectangleF(0, 4, 128, 128), sf);
                        }
                        b.Save(target, ImageFormat.Png);
                    }
                }
                ready[id] = target;
                return target;
            }
            catch { return DefaultFile; }
        }

        void Log(string msg)
        {
            if (logged > 60) return;
            logged++;
            try { File.AppendAllText(Path.Combine(U.DataDir, "avatars-log.txt"), DateTime.Now.ToString("HH:mm:ss") + "  " + msg + "\r\n", Encoding.UTF8); }
            catch { }
        }

        void Loop()
        {
            while (run)
            {
                AJob job;
                if (!work.TryDequeue(out job)) { signal.WaitOne(1000); continue; }
                try
                {
                    if (job.Source != null) { MakeRing(job); continue; }
                    bool fresh = File.Exists(job.Target) && (DateTime.Now - File.GetLastWriteTime(job.Target)).TotalHours < 24;
                    if (!fresh)
                    {
                        Image img = null;
                        foreach (string u in Candidates(job.Urls))
                        {
                            img = Download(u);
                            if (img != null) break;
                        }
                        if (img == null)
                        {
                            int n = fails.AddOrUpdate(job.Id, 1, delegate(string k, int v) { return v + 1; });
                            pending[job.Id] = U.Now + Math.Min(300000, 20000L * n);  // retry later
                            Failed++;
                            Log("FAILED " + job.Id + " (" + job.Urls.Count + " urls) first=" + (job.Urls.Count > 0 ? job.Urls[0] : ""));
                            continue;
                        }
                        using (img) Convert(img, job.Target, Circle);
                        Downloaded++;
                    }
                    ready[job.Id] = job.Target;
                }
                catch (Exception ex) { Log("ERROR " + job.Id + " " + ex.Message); pending[job.Id] = U.Now + 60000; }
            }
        }

        static List<string> Candidates(List<string> urls)
        {
            List<string> r = new List<string>();
            foreach (string u in urls) if (u.IndexOf(".webp", StringComparison.OrdinalIgnoreCase) < 0 && !r.Contains(u)) r.Add(u);
            foreach (string u in urls) if (!r.Contains(u)) r.Add(u);
            foreach (string u in urls)
            {
                if (u.IndexOf(".webp", StringComparison.OrdinalIgnoreCase) < 0) continue;
                string j = u.Replace(".webp", ".jpeg").Replace(".WEBP", ".jpeg");
                if (!r.Contains(j)) r.Add(j);
            }
            return r;
        }

        static Image Download(string url)
        {
            byte[] data;
            try
            {
                using (WebClient wc = new WebClient())
                {
                    wc.Headers[HttpRequestHeader.UserAgent] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36";
                    wc.Headers[HttpRequestHeader.Referer] = "https://www.tiktok.com/";
                    wc.Headers[HttpRequestHeader.Accept] = "image/jpeg,image/png,image/webp,image/*;q=0.8";
                    data = wc.DownloadData(url);
                }
            }
            catch { return null; }
            if (data == null || data.Length < 16) return null;
            try { return Image.FromStream(new MemoryStream(data)); }
            catch { }
            return Wic.Decode(data);  // webp / heic ...
        }

        void MakeRing(AJob job)
        {
            long dummy;
            if (!File.Exists(job.Source)) { pending.TryRemove(job.Id, out dummy); return; }
            if (!File.Exists(job.Target))
            {
                using (Bitmap src = new Bitmap(job.Source))
                using (Bitmap b = new Bitmap(128, 128, PixelFormat.Format32bppArgb))
                using (Graphics g = Graphics.FromImage(b))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.Clear(Color.Transparent);
                    Color c = Color.FromArgb(255, job.Ring);
                    // soft outer glow + solid ring + picture inside
                    for (int i = 0; i < 4; i++)
                        using (Pen p = new Pen(Color.FromArgb(40 + i * 25, c), 2f))
                        {
                            if (Circle) g.DrawEllipse(p, 1 + i, 1 + i, 125 - 2 * i, 125 - 2 * i);
                            else g.DrawRectangle(p, 1 + i, 1 + i, 125 - 2 * i, 125 - 2 * i);
                        }
                    using (SolidBrush br = new SolidBrush(c))
                    {
                        if (Circle) g.FillEllipse(br, 5, 5, 117, 117); else g.FillRectangle(br, 5, 5, 118, 118);
                    }
                    g.DrawImage(src, new Rectangle(12, 12, 104, 104));
                    Save(b, job.Target);
                }
            }
            ready[job.Id] = job.Target;
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
                Save(b, target);
            }
        }

        static void Save(Bitmap b, string target)
        {
            string tmp = target + ".tmp";
            b.Save(tmp, ImageFormat.Png);
            if (File.Exists(target)) File.Delete(target);
            File.Move(tmp, target);
        }
    }

    // ------------------------------------------------------------------------
    //  Drawing helpers (1280x720 virtual screen)
    // ------------------------------------------------------------------------
    // ------------------------------------------------------------------------
    //  Unicode text (Arabic, other scripts) for the HUD.
    //  GTA's fonts have no Arabic glyphs, so such strings are rendered once with
    //  GDI+ (Windows shapes/joins Arabic letters and handles right-to-left) into
    //  a white PNG with outline/shadow, then drawn as a tinted sprite.
    //  Fallback (budget/limit reached or TextMode=Latin): Latin transliteration.
    // ------------------------------------------------------------------------
    static class Txt
    {
        public class Tex { public string File; public int W, H; }

        // [Hud] TextMode: Auto (image only when needed) | Always | Latin (transliterate) | Off (raw GTA text)
        public static string Mode = "Auto";
        public static string FontName = "Segoe UI";
        public static bool Bold = true;
        public static bool StripEmoji = true;
        public static bool FancyToAscii = true;
        public static float SizeFix = 1f;
        public static int MaxTextures = 1500;
        public const int Pad = 8;
        public const float EmPx = 44f;

        static readonly Dictionary<string, Tex> cache = new Dictionary<string, Tex>();
        static readonly Dictionary<string, string> prep = new Dictionary<string, string>();
        static int budget;
        public static int Count { get { return cache.Count; } }

        public static void NewFrame() { budget = 3; }

        static bool IsArabic(int c)
        {
            return (c >= 0x0600 && c <= 0x06FF) || (c >= 0x0750 && c <= 0x077F) || (c >= 0x08A0 && c <= 0x08FF) || (c >= 0xFB50 && c <= 0xFDFF) || (c >= 0xFE70 && c <= 0xFEFF);
        }

        static bool IsEmoji(int c)
        {
            return (c >= 0x1F000 && c <= 0x1FAFF) || (c >= 0x2600 && c <= 0x27BF) || (c >= 0x2B00 && c <= 0x2BFF) || c == 0xFE0F || c == 0x200D || c == 0x20E3 ||
                   (c >= 0x2190 && c <= 0x21FF) || (c >= 0x2300 && c <= 0x23FF) || (c >= 0xE0000 && c <= 0xE007F) || c == 0x00A9 || c == 0x00AE || c == 0x2122;
        }

        // 𝓐𝓶𝓲𝓷𝓮 / 𝐀𝐦𝐢𝐧𝐞 / Ａｍｉｎｅ -> Amine (TikTok "fancy" names)
        static int Fancy(int c)
        {
            if (c >= 0x1D400 && c <= 0x1D6A3) { int i = (c - 0x1D400) % 52; return i < 26 ? 'A' + i : 'a' + i - 26; }
            if (c >= 0x1D7CE && c <= 0x1D7FF) return '0' + (c - 0x1D7CE) % 10;
            if (c >= 0xFF01 && c <= 0xFF5E) return c - 0xFEE0;
            if (c >= 0x24B6 && c <= 0x24CF) return 'A' + c - 0x24B6;
            if (c >= 0x24D0 && c <= 0x24E9) return 'a' + c - 0x24D0;
            if (c >= 0x1F130 && c <= 0x1F149) return 'A' + c - 0x1F130;
            if (c >= 0x1F150 && c <= 0x1F169) return 'A' + c - 0x1F150;
            if (c >= 0x1F170 && c <= 0x1F189) return 'A' + c - 0x1F170;
            if (c >= 0x1F1E6 && c <= 0x1F1FF) return 'A' + c - 0x1F1E6;
            return c;
        }

        // Cleans a string: fancy letters -> ASCII, emoji removed, ellipsis, trims.
        public static string Prep(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            string r;
            if (prep.TryGetValue(s, out r)) return r;
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                int c = s[i];
                if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { c = char.ConvertToUtf32(s[i], s[i + 1]); i++; }
                if (FancyToAscii) c = Fancy(c);
                if (StripEmoji && IsEmoji(c)) continue;
                if (c == 0x2026) { sb.Append("..."); continue; }
                if (c == 0x0640) continue; // tatweel
                if (c < 0x20) continue;
                sb.Append(char.ConvertFromUtf32(c));
            }
            r = sb.ToString().Trim();
            while (r.Contains("  ")) r = r.Replace("  ", " ");
            if (prep.Count > 5000) prep.Clear();
            prep[s] = r;
            return r;
        }

        // true when GTA's own font cannot show the string
        public static bool NeedsImage(string s)
        {
            if (Mode.Equals("Always", StringComparison.OrdinalIgnoreCase)) return true;
            foreach (char ch in s)
            {
                int c = ch;
                if (c <= 0x24F) continue;
                if (c >= 0x2000 && c <= 0x206F) continue;
                return true;
            }
            return false;
        }

        public static bool IsRtl(string s)
        {
            foreach (char ch in s)
            {
                if (IsArabic(ch) || (ch >= 0x0590 && ch <= 0x05FF)) return true;
                if (char.IsLetter(ch)) return false;
            }
            return false;
        }

        static readonly Dictionary<char, string> Map = new Dictionary<char, string> {
            {'ا',"a"},{'أ',"a"},{'إ',"i"},{'آ',"a"},{'ٱ',"a"},{'ب',"b"},{'ت',"t"},{'ث',"th"},{'ج',"j"},{'ح',"h"},{'خ',"kh"},{'د',"d"},{'ذ',"dh"},{'ر',"r"},{'ز',"z"},
            {'س',"s"},{'ش',"ch"},{'ص',"s"},{'ض',"d"},{'ط',"t"},{'ظ',"z"},{'ع',"3"},{'غ',"gh"},{'ف',"f"},{'ق',"q"},{'ك',"k"},{'ل',"l"},{'م',"m"},{'ن',"n"},{'ه',"h"},
            {'و',"w"},{'ي',"y"},{'ى',"a"},{'ة',"a"},{'ء',"'"},{'ئ',"2"},{'ؤ',"2"},{'پ',"p"},{'چ',"tch"},{'ڤ',"v"},{'گ',"g"},{'ک',"k"},{'ی',"y"},{'ڭ',"g"},{'؟',"?"},{'،',","},{'؛',";"} };

        // Latin fallback so a name always shows
        public static string Translit(string s)
        {
            StringBuilder sb = new StringBuilder();
            bool start = true;
            foreach (char ch in s)
            {
                string m;
                if (ch >= 0x0660 && ch <= 0x0669) { sb.Append((char)('0' + ch - 0x0660)); start = false; continue; }
                if (ch >= 0x06F0 && ch <= 0x06F9) { sb.Append((char)('0' + ch - 0x06F0)); start = false; continue; }
                if (ch >= 0x064B && ch <= 0x065F) continue;
                if (Map.TryGetValue(ch, out m)) { sb.Append(start ? char.ToUpperInvariant(m[0]) + m.Substring(1) : m); start = false; continue; }
                if (ch <= 0x24F || (ch >= 0x2000 && ch <= 0x206F)) { sb.Append(ch); start = ch == ' '; continue; }
            }
            string r = sb.ToString().Trim();
            return r.Length == 0 ? "?" : r;
        }

        // rendered texture for a string (null while not available this frame)
        public static Tex Get(string s, bool outline, bool shadow, bool allowCreate)
        {
            string key = s + "\u0001" + (outline ? "o" : "") + (shadow ? "s" : "");
            Tex t;
            if (cache.TryGetValue(key, out t)) return t;
            if (!allowCreate || budget <= 0 || cache.Count >= MaxTextures) return null;
            budget--;
            try
            {
                string dir = Path.Combine(U.DataDir, "cache", "text");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, U.Joaat(FontName + "|" + Bold + "|" + key).ToString("x8") + "_" + s.Length + ".png");
                if (!File.Exists(file)) Render(s, outline, shadow, file);
                t = new Tex();
                t.File = file;
                using (Image im = Image.FromFile(file)) { t.W = im.Width; t.H = im.Height; }
                cache[key] = t;
                return t;
            }
            catch (Exception ex)
            {
                U.Log("text render failed: " + ex.Message);
                cache[key] = null;
                return null;
            }
        }

        static void Render(string s, bool outline, bool shadow, string file)
        {
            bool rtl = IsRtl(s);
            using (System.Drawing.Font f = new System.Drawing.Font(FontName, EmPx, Bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel))
            using (StringFormat sf = new StringFormat())
            {
                sf.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap;
                if (rtl) sf.FormatFlags |= StringFormatFlags.DirectionRightToLeft;
                SizeF m;
                float lineH;
                using (Bitmap tmp = new Bitmap(4, 4))
                using (Graphics g0 = Graphics.FromImage(tmp))
                {
                    g0.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                    m = g0.MeasureString(s, f, 8000, sf);
                    lineH = f.GetHeight(g0);
                }
                int w = Math.Max(4, (int)Math.Ceiling(m.Width) + Pad * 2 + 4);
                int h = (int)Math.Ceiling(lineH) + Pad * 2;
                using (Bitmap b = new Bitmap(w, h, PixelFormat.Format32bppArgb))
                using (Graphics g = Graphics.FromImage(b))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                    g.Clear(Color.Transparent);
                    RectangleF r = new RectangleF(Pad, Pad, w - Pad * 2, lineH);
                    if (shadow)
                        using (SolidBrush sb = new SolidBrush(Color.FromArgb(140, 0, 0, 0)))
                            g.DrawString(s, f, sb, new RectangleF(r.X + 3f, r.Y + 3f, r.Width, r.Height), sf);
                    if (outline)
                        using (SolidBrush ob = new SolidBrush(Color.FromArgb(235, 0, 0, 0)))
                            for (int k = 0; k < 12; k++)
                            {
                                double an = k * Math.PI / 6;
                                g.DrawString(s, f, ob, new RectangleF(r.X + (float)Math.Cos(an) * 2.6f, r.Y + (float)Math.Sin(an) * 2.6f, r.Width, r.Height), sf);
                            }
                    g.DrawString(s, f, Brushes.White, r, sf);
                    string tmpf = file + ".tmp";
                    b.Save(tmpf, ImageFormat.Png);
                    if (File.Exists(file)) File.Delete(file);
                    File.Move(tmpf, file);
                }
            }
        }

        // splits a template into drawable parts: static text pieces and variable values
        public static List<string> Parts(string tpl, string name, int count, string rival, int level)
        {
            List<string> r = new List<string>();
            if (string.IsNullOrEmpty(tpl)) return r;
            int i = 0;
            StringBuilder cur = new StringBuilder();
            while (i < tpl.Length)
            {
                string val = null;
                int len = 0;
                if (tpl[i] == '{')
                {
                    if (string.Compare(tpl, i, "{name}", 0, 6) == 0) { val = name ?? ""; len = 6; }
                    else if (string.Compare(tpl, i, "{count}", 0, 7) == 0) { val = count.ToString(U.IC); len = 7; }
                    else if (string.Compare(tpl, i, "{rival}", 0, 7) == 0) { val = rival ?? ""; len = 7; }
                    else if (string.Compare(tpl, i, "{level}", 0, 7) == 0) { val = level.ToString(U.IC); len = 7; }
                }
                if (val == null) { cur.Append(tpl[i]); i++; continue; }
                AddPart(r, cur.ToString());
                cur.Length = 0;
                AddPart(r, val);
                i += len;
            }
            AddPart(r, cur.ToString());
            return r;
        }

        static void AddPart(List<string> r, string s)
        {
            string p = Prep(s);
            if (p.Length > 0) r.Add(p);
        }
    }

    // ------------------------------------------------------------------------
    //  White shape pictures (made once with GDI+, drawn tinted) for the HUD designs:
    //  rounded corners, circle, ring, glow, hexagon, card point, parallelogram, crown.
    //  Rule: script pictures are drawn above GTA rectangles/text, so text is never
    //  placed on a shape picture (only on rectangles), except finite labels drawn as text pictures.
    // ------------------------------------------------------------------------
    static class Shapes
    {
        static readonly Dictionary<string, string> files = new Dictionary<string, string>();
        const int N = 128;

        public static string F(string name)
        {
            string f;
            return files.TryGetValue(name, out f) ? f : null;
        }

        static PointF[] Hex(float s, bool pointyTop)
        {
            PointF[] p = new PointF[6];
            float c = s / 2f, r = s / 2f - 1;
            for (int i = 0; i < 6; i++)
            {
                double a = Math.PI / 3 * i + (pointyTop ? Math.PI / 2 : 0);
                p[i] = new PointF(c + (float)Math.Cos(a) * r, c + (float)Math.Sin(a) * r);
            }
            return p;
        }

        public static void Make(string dir)
        {
            try { Directory.CreateDirectory(dir); } catch { }
            Draw(dir, "circle", delegate(Graphics g) { g.FillEllipse(Brushes.White, 1, 1, N - 2, N - 2); });
            Draw(dir, "ring", delegate(Graphics g) { using (Pen p = new Pen(Color.White, N * 0.12f)) g.DrawEllipse(p, N * 0.06f + 1, N * 0.06f + 1, N * 0.88f - 2, N * 0.88f - 2); });
            Draw(dir, "glow", delegate(Graphics g)
            {
                using (GraphicsPath gp = new GraphicsPath())
                {
                    gp.AddEllipse(0, 0, N, N);
                    using (PathGradientBrush pb = new PathGradientBrush(gp))
                    {
                        pb.CenterColor = Color.FromArgb(255, 255, 255, 255);
                        pb.SurroundColors = new Color[] { Color.FromArgb(0, 255, 255, 255) };
                        g.FillEllipse(pb, 0, 0, N, N);
                    }
                }
            });
            Draw(dir, "hex", delegate(Graphics g) { g.FillPolygon(Brushes.White, Hex(N, true)); });
            Draw(dir, "q_tl", delegate(Graphics g) { g.FillEllipse(Brushes.White, 0, 0, N * 2, N * 2); });
            Draw(dir, "q_tr", delegate(Graphics g) { g.FillEllipse(Brushes.White, -N, 0, N * 2, N * 2); });
            Draw(dir, "q_bl", delegate(Graphics g) { g.FillEllipse(Brushes.White, 0, -N, N * 2, N * 2); });
            Draw(dir, "q_br", delegate(Graphics g) { g.FillEllipse(Brushes.White, -N, -N, N * 2, N * 2); });
            Draw(dir, "tri_down", delegate(Graphics g) { g.FillPolygon(Brushes.White, new PointF[] { new PointF(0, 0), new PointF(N, 0), new PointF(N / 2f, N) }); });
            Draw(dir, "slant_l", delegate(Graphics g) { g.FillPolygon(Brushes.White, new PointF[] { new PointF(N, 0), new PointF(N, N), new PointF(0, N) }); });
            Draw(dir, "slant_r", delegate(Graphics g) { g.FillPolygon(Brushes.White, new PointF[] { new PointF(0, 0), new PointF(N, 0), new PointF(0, N) }); });
            Draw(dir, "crown", delegate(Graphics g)
            {
                g.FillPolygon(Brushes.White, new PointF[] {
                    new PointF(N * 0.08f, N * 0.85f), new PointF(N * 0.08f, N * 0.30f), new PointF(N * 0.30f, N * 0.55f), new PointF(N * 0.50f, N * 0.12f),
                    new PointF(N * 0.70f, N * 0.55f), new PointF(N * 0.92f, N * 0.30f), new PointF(N * 0.92f, N * 0.85f) });
                g.FillEllipse(Brushes.White, N * 0.02f, N * 0.20f, N * 0.14f, N * 0.14f);
                g.FillEllipse(Brushes.White, N * 0.43f, N * 0.02f, N * 0.14f, N * 0.14f);
                g.FillEllipse(Brushes.White, N * 0.84f, N * 0.20f, N * 0.14f, N * 0.14f);
            });
        }

        static void Draw(string dir, string name, Action<Graphics> paint)
        {
            string file = Path.Combine(dir, name + ".png");
            files[name] = file;
            if (File.Exists(file)) return;
            try
            {
                using (Bitmap b = new Bitmap(N, N, PixelFormat.Format32bppArgb))
                using (Graphics g = Graphics.FromImage(b))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.Clear(Color.Transparent);
                    paint(g);
                    b.Save(file, ImageFormat.Png);
                }
            }
            catch (Exception ex) { U.Log("shape " + name + ": " + ex.Message); files.Remove(name); }
        }
    }

    // Windows MCI player: mp3 / wav / wma, several tracks at the same time (alias = channel)
    static class Mci
    {
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
        static extern int mciSendString(string command, StringBuilder ret, int retLen, IntPtr callback);

        static int Send(string cmd) { try { return mciSendString(cmd, null, 0, IntPtr.Zero); } catch { return -1; } }

        public static bool Play(string alias, string file, bool loop, int volume)
        {
            Close(alias);
            if (Send("open \"" + file + "\" type mpegvideo alias " + alias) != 0) return false;
            Volume(alias, volume);
            return Send("play " + alias + (loop ? " repeat" : "")) == 0;
        }

        public static void Volume(string alias, int percent) { Send("setaudio " + alias + " volume to " + U.Clamp(percent, 0, 100) * 10); }
        public static void Pause(string alias) { Send("pause " + alias); }
        public static void Resume(string alias) { Send("resume " + alias); }
        public static void Close(string alias) { Send("stop " + alias); Send("close " + alias); }

        public static string Mode(string alias)
        {
            try
            {
                StringBuilder sb = new StringBuilder(64);
                if (mciSendString("status " + alias + " mode", sb, 64, IntPtr.Zero) != 0) return "";
                return sb.ToString().ToLowerInvariant();
            }
            catch { return ""; }
        }
    }

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
            Txt.NewFrame();
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
            string mode = Txt.Mode;
            if (!mode.Equals("Off", StringComparison.OrdinalIgnoreCase))
            {
                s = Txt.Prep(s);
                if (s.Length == 0) return;
                if (Txt.NeedsImage(s))
                {
                    if (!mode.Equals("Latin", StringComparison.OrdinalIgnoreCase))
                    {
                        Txt.Tex t = Txt.Get(s, Outline, Shadow, true);
                        if (t != null) { DrawTex(t, x, y, scale, c, al); return; }
                    }
                    s = Txt.Translit(s);
                }
            }
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

        // text drawn as an image: GDI+ line height mapped to the GTA line height of this scale
        static void TexBox(Txt.Tex t, float scale, out float drawW, out float drawH, out float pad)
        {
            float lineH = scale * 38f * Txt.SizeFix;
            drawH = lineH * t.H / Math.Max(1f, t.H - Txt.Pad * 2);
            drawW = drawH * t.W / Math.Max(1f, t.H);
            pad = Txt.Pad * drawH / Math.Max(1f, t.H);
        }

        static void DrawTex(Txt.Tex t, float x, float y, float scale, Color c, Alignment al)
        {
            float w, h, pad;
            TexBox(t, scale, out w, out h, out pad);
            float left = al == Alignment.Center ? x - w / 2 : (al == Alignment.Right ? x - w + pad : x - pad);
            ImageAbsTint(t.File, left, y - pad + scale * 2f, w, h, c);
        }

        public static float TextW(string s, float size)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            float scale = size * FontScale;
            if (!Txt.Mode.Equals("Off", StringComparison.OrdinalIgnoreCase))
            {
                s = Txt.Prep(s);
                if (s.Length == 0) return 0;
                if (Txt.NeedsImage(s))
                {
                    if (!Txt.Mode.Equals("Latin", StringComparison.OrdinalIgnoreCase))
                    {
                        Txt.Tex t = Txt.Get(s, Outline, Shadow, true);
                        if (t != null) { float w, h, pad; TexBox(t, scale, out w, out h, out pad); return w - pad * 2; }
                    }
                    s = Txt.Translit(s);
                }
            }
            try { return TextElement.GetStringWidth(s, F, scale); }
            catch { return s.Length * scale * 18f; }
        }

        // several parts (template pieces + names) laid out in reading order; right-to-left when any part is Arabic
        public static float PartsW(List<string> parts, float size)
        {
            if (parts == null || parts.Count == 0) return 0;
            float w = 0;
            foreach (string p in parts) w += TextW(p, size);
            return w + (parts.Count - 1) * size * 11f * FontScale;
        }

        public static void Parts(List<string> parts, float x, float y, float size, Color c)
        {
            if (parts == null || parts.Count == 0) return;
            bool rtl = false;
            foreach (string p in parts) if (Txt.IsRtl(p)) { rtl = true; break; }
            float gap = size * 11f * FontScale;
            float cx = x;
            for (int i = 0; i < parts.Count; i++)
            {
                string p = parts[rtl ? parts.Count - 1 - i : i];
                Text(p, cx, y, size, c, Alignment.Left);
                cx += TextW(p, size) + gap;
            }
        }

        public static void PartsCentered(List<string> parts, float cx, float y, float size, Color c)
        {
            Parts(parts, cx - PartsW(parts, size) / 2, y, size, c);
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
            ImageAbsTint(file, x, y, w, h, Color.FromArgb(U.Clamp(alpha, 0, 255), 255, 255, 255));
        }

        public static void ImageAbsTint(string file, float x, float y, float w, float h, Color tint)
        {
            if (!FileOk(file) || tint.A <= 0) return;
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
            sp.Color = tint;
            sp.Draw();
        }

        // tinted white shape picture (see Shapes)
        public static void Shape(string name, float x, float y, float w, float h, Color c)
        {
            string f = Shapes.F(name);
            if (f != null && w > 0 && h > 0) ImageAbsTint(f, Ox + x * S, Oy + y * S, w * S, h * S, c);
        }

        // rounded rectangle: rectangles for the body (text can sit on it) + 4 corner pictures
        public static void RRect(float x, float y, float w, float h, float r, Color c)
        {
            r = Math.Min(r, Math.Min(w, h) / 2);
            if (r < 1.5f || Shapes.F("q_tl") == null) { Rect(x, y, w, h, c); return; }
            Rect(x + r, y, w - 2 * r, h, c);
            Rect(x, y + r, r, h - 2 * r, c);
            Rect(x + w - r, y + r, r, h - 2 * r, c);
            Shape("q_tl", x, y, r, r, c);
            Shape("q_tr", x + w - r, y, r, r, c);
            Shape("q_bl", x, y + h - r, r, r, c);
            Shape("q_br", x + w - r, y + h - r, r, r, c);
        }

        // text forced to a picture (for short labels drawn over a shape picture)
        public static void TextPic(string s, float x, float y, float size, Color c, Alignment al)
        {
            string p = Txt.Prep(s);
            Txt.Tex t = p.Length > 0 ? Txt.Get(p, false, false, true) : null;
            if (t != null) DrawTex(t, Ox + x * S, Oy + y * S, size * S * FontScale, c, al);
            else Text(s, x, y, size, c, al);
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
        bool drunkClip, speedOn, freezeOn, gravOn, blackoutOn, nightSet;
        int savedHour, savedMinute;
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
            Shapes.Make(Path.Combine(U.DataDir, "cache", "ui"));
            Interval = 0;
            Tick += OnTick;
            KeyDown += OnKeyDown;
            KeyUp += OnKeyUp;
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
            // [Hud] Profile = name of a saved HUD: TikArena/huds/<name>.ini is loaded over the main INI
            string prof = HudFile(ini.S("Hud", "Profile", ""));
            if (prof.Length > 0)
            {
                string pp = Path.Combine(U.DataDir, "huds", prof + ".ini");
                if (File.Exists(pp)) { ini.Merge(Ini.Load(pp)); U.Log("hud profile: " + pp); }
                else U.Log("hud profile not found: " + pp);
            }
            cfg = Cfg.Load(ini);
            try { iniTime = File.Exists(iniPath) ? File.GetLastWriteTime(iniPath) : DateTime.MinValue; } catch { }
            U.DebugLog = cfg.DebugLog;
            Txt.Mode = cfg.TextMode;
            Txt.FontName = cfg.UnicodeFont;
            Txt.Bold = cfg.UnicodeBold;
            Txt.SizeFix = cfg.UnicodeSize;
            Txt.StripEmoji = cfg.StripEmoji;
            Txt.FancyToAscii = cfg.FancyToAscii;
            Txt.MaxTextures = cfg.MaxTextTextures;
            style = cfg.HudStyle;
            fontName = cfg.HudFont;
            design = cfg.HudDesign;
            designColor = cfg.HudDesignColor;
            gfxPreset = cfg.GfxPreset;
            vertical = string.Equals(cfg.LayoutMode, "Vertical", StringComparison.OrdinalIgnoreCase);
            appliedWeather = null;
            tcApplied = false;
            tcName = null;
            clockLocked = false;
            U.Log("config loaded: " + iniPath + " interactions=" + cfg.Interactions.Count);
        }

        // file name of a HUD profile (same rule as the page): only the characters Windows does not allow are replaced
        static string HudFile(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            StringBuilder sb = new StringBuilder();
            foreach (char ch in name.Trim())
                sb.Append(ch < 32 || "\\/:*?\"<>|".IndexOf(ch) >= 0 ? '_' : ch);
            return sb.ToString().Trim('.', ' ');
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
            toastParts = new List<string> { "TikArena" };
            toastParts.AddRange(Txt.Parts(msg, "", 0, null, 0));
            toastUntil = U.Now + 2800;
        }

        List<string> toastParts;
        long toastUntil;

        // status / load messages drawn by the HUD text system (GTA subtitles cannot show Arabic)
        void DrawToast(bool begin)
        {
            if (toastParts == null || U.Now >= toastUntil) return;
            if (Function.Call<bool>(Hash.IS_PAUSE_MENU_ACTIVE)) return;
            if (begin)
            {
                Gfx.BeginFrame();
                Gfx.F = FontOf(fontName);
                Gfx.FontScale = cfg.FontScale;
                Gfx.Outline = cfg.TextOutline;
                Gfx.Shadow = cfg.TextShadow;
            }
            Gfx.Origin(0, 0, 1);
            float a = U.Clamp((toastUntil - U.Now) / 400f, 0, 1);
            float w = Gfx.PartsW(toastParts, 0.42f) + 30, x = 640 - w / 2, y = 610;
            Gfx.Rect(x, y, w, 30, Color.FromArgb((int)(210 * a), 10, 14, 22));
            Gfx.Rect(x, y, 4, 30, Color.FromArgb((int)(255 * a), 0, 230, 118));
            Gfx.Parts(toastParts, x + 15, y + 3, 0.42f, Color.FromArgb((int)(255 * a), 255, 255, 255));
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
                try { ClearBlips(true); } catch (Exception ex) { U.Error("Blips", ex); }
                if (cfg.ShowLoadMessage)
                {
                    toastParts = new List<string> { "TikArena" };
                    toastParts.AddRange(Txt.Parts(cfg.Tx("LoadedText", "Ready. Press"), "", 0, null, 0));
                    toastParts.Add(cfg.PowerKey.ToString());
                    toastUntil = U.Now + 7000;
                }
            }
            FileWatch();
            try { if (!started || !cfg.HudEnabled) DrawToast(true); } catch (Exception ex) { U.Error("Toast", ex); }
            if (!powered) return;

            try { ProcessInbox(); } catch (Exception ex) { U.Error("Inbox", ex); }
            try { UpdateLiveTest(); } catch (Exception ex) { U.Error("LiveTest", ex); }
            try { WorldRules(); } catch (Exception ex) { U.Error("World", ex); }
            try { UpdateFakeDeath(); } catch (Exception ex) { U.Error("FakeDeath", ex); }
            try { UpdateRound(dt); } catch (Exception ex) { U.Error("Round", ex); }
            try { UpdateQueues(); } catch (Exception ex) { U.Error("Queue", ex); }
            try { UpdateTracked(); } catch (Exception ex) { U.Error("Tracked", ex); }
            try { UpdateBlips(); } catch (Exception ex) { U.Error("Blips", ex); }
            try { UpdateMusic(); } catch (Exception ex) { U.Error("Music", ex); }
            try { UpdateZoomCam(); } catch (Exception ex) { U.Error("ZoomCam", ex); }
            try { WatchHealth(); } catch (Exception ex) { U.Error("DamageFx", ex); }
            try { ClearBlood(); } catch (Exception ex) { U.Error("NoBlood", ex); }
            try { UpdateEffects(dt); } catch (Exception ex) { U.Error("Effects", ex); }
            try { UpdateCamera(dt); } catch (Exception ex) { U.Error("Camera", ex); }
            try { UpdateAuras(); } catch (Exception ex) { U.Error("Aura", ex); }
            try { UpdatePlayerFx(); } catch (Exception ex) { U.Error("PlayerFx", ex); }
            try { UpdateCelebration(); } catch (Exception ex) { U.Error("Death", ex); }
            try { UpdateHypeChecks(); } catch (Exception ex) { U.Error("Hype", ex); }
            try { DrawHud(); } catch (Exception ex) { U.Error("Hud", ex); }
        }

        // Windows repeats KeyDown while a key is held: one press = one action
        //  (a repeat keeps coming every ~30 ms; if a KeyUp was lost, a new press after 0.6 s still counts)
        readonly Dictionary<Keys, long> keysDown = new Dictionary<Keys, long>();

        void OnKeyUp(object sender, KeyEventArgs e) { keysDown.Remove(e.KeyCode); }

        bool IsRepeat(Keys k)
        {
            long last;
            bool rep = keysDown.TryGetValue(k, out last) && U.Now - last < 600;
            keysDown[k] = U.Now;
            return rep;
        }

        void OnKeyDown(object sender, KeyEventArgs e)
        {
            try
            {
                Keys k = e.KeyCode;
                if (k == Keys.None) return;
                if (IsRepeat(k)) return;
                if (k == cfg.PowerKey) { TogglePower(); return; }
                if (!powered) return;
                if (k == cfg.LiveTestKey && cfg.LiveTestEnabled) { ToggleLiveTest(); return; }
                if (k == cfg.StartKey) { StartSession(); return; }
                if (k == cfg.HudStyleKey) { SetLook(Cycle(cfg.LookCycle, CurrentLook)); Status("HUD: " + CurrentLook); return; }
                if (k == cfg.HudFontKey) { fontName = Cycle(cfg.FontCycle, fontName); Status("Font: " + fontName); return; }
                if (k == cfg.DesignKey) { design = Cycle(DesignCycle, design); Status("Design: " + design); return; }
                if (k == cfg.DesignColorKey) { designColor = Cycle(DesignColors, designColor); Status("Color: " + designColor); return; }
                if (k == cfg.LayoutKey) { vertical = !vertical; Status(vertical ? "TikTok 9:16" : "16:9"); return; }
                if (k == cfg.PauseKey)
                {
                    paused = !paused;
                    Status(paused ? cfg.Tx("PausedText", "Paused") : cfg.Tx("ResumedText", "Resumed"));
                    return;
                }
                if (k == cfg.EmergencyKey) { Emergency(); return; }
                if (k == cfg.MusicKey && cfg.MusicEnabled) { ToggleMusic(); return; }
                if (k == cfg.ZoomKey) { zoomOn = !zoomOn; Status(zoomOn ? "Camera: TikTok" : "Camera: GTA"); return; }
                if (zoomOn && (k == cfg.ZoomInKey || k == cfg.ZoomOutKey))
                {
                    zoomAdjust = U.Clamp(zoomAdjust + (k == cfg.ZoomInKey ? -0.25f : 0.25f), -3f, 8f);
                    Status("Zoom: " + (cfg.ZoomDistance + zoomAdjust).ToString("0.00", U.IC) + " m");
                    return;
                }
                if (k == cfg.GfxKey) { gfxPreset = Cycle(GfxPresetNames, gfxPreset); nextSecond = 0; Status("Graphics: " + gfxPreset); return; }
                if (k == cfg.StopEffectsKey) { StopAllEffects(); Status(cfg.Tx("StopEffectsText", "Effects stopped")); return; }
                if (k == cfg.HypeTestKey)
                {
                    Supporter t = GetSup("test:" + cfg.TestUserName, cfg.TestUserName, null, 25);
                    PushHype(cfg.ComboText, t, cfg.ComboMin, null);
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
            try { ClearBlips(false); } catch { }
            try { StopAllMusic(); StopZoomCam(); } catch { }
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
                simOn = false;
                simQueue.Clear();
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
            LoadPlaylist();
            if (cfg.ZoomOnStart) zoomOn = true;
            StartRound();
            Status(cfg.Tx("StartedText", "Started"));
        }

        // new round = clean arena: the spawned enemies/allies vanish and every effect stops.
        // Wins/losses, the streak and the top supporters stay.
        void ResetArena()
        {
            if (!cfg.RoundReset) return;
            int fx = 0;
            for (int i = tracked.Count - 1; i >= 0; i--)
            {
                Tracked t = tracked[i];
                if (!(t.Enemy ? cfg.RoundClearEnemies : cfg.RoundClearAllies)) continue;
                try
                {
                    if (t.Ped != null && t.Ped.Exists() && fx++ < 12) SpawnFx(cfg.RoundVanishEffect, t.Ped.Position);
                    DeleteTracked(t);
                    SafeDeleteVehicle(t.Veh);   // keeps it if you are driving it
                }
                catch (Exception ex) { U.Error("ResetArena", ex); }
                tracked.RemoveAt(i);
            }
            if (cfg.RoundClearEffects) StopAllEffects();
            ClearEvents();
            UpdateBlips();
            if (cfg.RoundClearEnemies && cfg.RoundClearAllies) ClearBlips(true);
        }

        void StartRound()
        {
            ResetArena();
            roundMs = 0;
            roundEnemies = roundAllies = roundKills = 0;
            roundDurMs = cfg.DurationMinutes * 60000.0;
            mvp = null;
            foreach (Supporter s in sups.Values) s.RoundHelpCoins = 0;
            phase = cfg.ChallengeEnabled ? Phase.Running : Phase.Idle;
            Ped pl = Game.Player.Character;
            EnsureHpBuffer();
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
            int max = points + 100 + hpBuf;
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
            StopAllEffects();
            ClearBlips(true);
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
            ClearBlips(true);
            StopAllMusic();
            StopZoomCam();
            RestoreWorld();
        }

        void RestoreWorld()
        {
            Ped pl = Game.Player.Character;
            fxEnd.Clear();
            fxInfo.Clear();
            keepNext.Clear();
            nightSet = false;
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
            RemoveHpBuffer();
            fakeDead = false;
            StopSpeedFx();
            popups.Clear();
            spots.Clear();
            shaking = drunkClip = speedOn = freezeOn = gravOn = blackoutOn = false;
            radarHidden = clockLocked = tcApplied = wantedApplied = false;
            tcName = null;
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

        // ================================================================ graphics looks for the stream
        //  [Graphics] Preset (Key = next one). Filter names are GTA timecycle modifiers (to test in game).
        class GfxLook { public string Name, Modifier, Weather; public float Strength; public int Hour; }
        static readonly GfxLook[] GfxLooks = {
            G("Cinematic", "NG_filmic01", 0.85f, -1, ""),
            G("Vivid", "rply_saturation", 0.8f, -1, ""),
            G("Action", "rply_contrast", 0.7f, -1, ""),
            G("GoldenHour", "glasses_orange", 0.35f, 19, "EXTRASUNNY"),
            G("NeonNight", "rply_saturation", 0.6f, 23, "CLEAR"),
            G("ColdBlue", "glasses_Darkblue", 0.4f, -1, ""),
            G("Dream", "NG_filmic13", 0.8f, -1, ""),
            G("RainyNight", "NG_filmic04", 0.7f, 21, "RAIN"),
            G("FilmNoir", "NG_filmnoir_BW01", 1f, -1, ""),
            G("Vignette", "rply_vignette", 0.8f, -1, "") };
        static GfxLook G(string n, string m, float s, int h, string w) { GfxLook g = new GfxLook(); g.Name = n; g.Modifier = m; g.Strength = s; g.Hour = h; g.Weather = w; return g; }
        static readonly List<string> GfxPresetNames = new List<string> { "Off", "Cinematic", "Vivid", "Action", "GoldenHour", "NeonNight", "ColdBlue", "Dream", "RainyNight", "FilmNoir", "Vignette" };
        string gfxPreset = "Off", tcName;

        GfxLook CurrentGfx()
        {
            foreach (GfxLook g in GfxLooks) if (Is(g.Name, gfxPreset)) return g;
            return null;
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

                GfxLook gl = CurrentGfx();
                if (nightSet) { }
                else if (cfg.LockTime || (gl != null && gl.Hour >= 0))
                {
                    Function.Call(Hash.SET_CLOCK_TIME, gl != null && gl.Hour >= 0 ? gl.Hour : cfg.Hour, gl != null && gl.Hour >= 0 ? 0 : cfg.Minute, 0);
                    Function.Call(Hash.PAUSE_CLOCK, true);
                    clockLocked = true;
                }
                else if (clockLocked) { Function.Call(Hash.PAUSE_CLOCK, false); clockLocked = false; }

                string want = "";
                if (Active("Storm")) want = "THUNDER";
                else if (Active("Weather")) want = weatherOverride;
                else if (gl != null && gl.Weather.Length > 0) want = gl.Weather;
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

                // graphics look: a preset (filter + time + weather) or the custom timecycle
                string tcWant = null;
                float tcStr = 0;
                if (gl != null) { tcWant = gl.Modifier; tcStr = gl.Strength; }
                else if (cfg.TimecycleEnabled) { tcWant = cfg.Timecycle; tcStr = cfg.TimecycleStrength; }
                string tcKey = tcWant == null ? null : tcWant + "|" + tcStr.ToString(U.IC);
                if (tcKey != tcName)
                {
                    if (tcWant != null)
                    {
                        Function.Call(Hash.SET_TIMECYCLE_MODIFIER, tcWant);
                        Function.Call(Hash.SET_TIMECYCLE_MODIFIER_STRENGTH, tcStr);
                        tcApplied = true;
                    }
                    else if (tcApplied) { Function.Call(Hash.CLEAR_TIMECYCLE_MODIFIER); tcApplied = false; }
                    tcName = tcKey;
                }
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
                bool inv = cfg.PlayerInvincible || fakeDead || Active("GodMode") || (cfg.InvincibleDuringCam && camMode.Length > 0);
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
            e.Avatars = Json.FindAvatars(data);
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

        Supporter GetSup(string key, string nick, List<string> urls, int level)
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
            if (urls != null && urls.Count > 0) s.AvatarUrls = urls;
            if (level > s.Level) s.Level = level;
            return s;
        }

        string Avatar(Supporter s)
        {
            if (s == null) return avatars.DefaultFile;
            if (s.Sim && s.AvatarUrls.Count == 0) return avatars.Synthetic(s.Key, s.Nick, s.SimColor);
            return avatars.Get(s.Key, s.AvatarUrls);
        }

        void HandleEvent(LiveEvent e)
        {
            long now = U.Now;
            Supporter s = GetSup(e.UserKey, e.Nick, e.Avatars, e.Level);
            if (e.Sim) { s.Sim = true; s.SimColor = e.SimColor; }
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
                        PushHype(cfg.ComboText, s, e.RepeatCount, null);
                    }
                    if (e.RepeatEnd) comboFired.Remove(skey);
                }
                else
                {
                    delta = Math.Max(1, e.RepeatCount);
                    if (cfg.HypeEnabled && cfg.ComboEnabled && delta >= cfg.ComboMin)
                        PushHype(cfg.ComboText, s, delta, null);
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
                    string gm = (it.GiftMatch ?? "Both").ToLowerInvariant();
                    if (gm == "name") byId = false;
                    else if (gm == "id") byName = false;
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
            if (IsSpawn(it.Action)) { spawnQ.Add(j); NotifyWaiting(s, it.Action); }
            else instantQ.Add(j);

            if (IsHelp(it.Action)) s.RoundHelpCoins += Math.Max(coins, 1);
            if (IsEnemyAction(it.Action)) { lastEnemySup = s; lastEnemyAt = U.Now; }
            else if (IsRivalHelp(it.Action)) CheckRivalry(s);

            QueueSpotlight(it, s, units, coins);
            if (cfg.NotifEnabled && it.Message.Length > 0)
            {
                FeedItem f = new FeedItem();
                f.Parts = Txt.Parts(it.Message, s.Nick, units, null, s.Level);
                f.Text = string.Join(" ", f.Parts.ToArray());
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

        void PushHype(string tpl, Supporter s, int count, string rival)
        {
            if (!cfg.HypeEnabled || string.IsNullOrEmpty(tpl)) return;
            FeedItem f = new FeedItem();
            int lvl = s != null ? s.Level : 0;
            f.Parts = Txt.Parts(tpl, s != null ? s.Nick : "", count, rival, lvl);
            f.Text = string.Join(" ", f.Parts.ToArray());
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
                    PushHype(cfg.NewKingText, k, 0, null);
            }
        }

        void HypeJoin(Supporter s)
        {
            if (!cfg.HypeEnabled) return;
            if (cfg.KingJoinEnabled && king == s && s.Coins > 0)
            {
                if (HypeReady("join:" + s.Key, 120)) PushHype(cfg.KingJoinText, s, 0, null);
                return;
            }
            if (cfg.VipJoinEnabled && (s.Level >= cfg.VipJoinMinLevel || s.Coins >= cfg.VipJoinMinCoins))
            {
                if (HypeReady("join:" + s.Key, 120)) PushHype(cfg.VipJoinText, s, 0, null);
            }
        }

        void CheckRivalry(Supporter helper)
        {
            if (!cfg.HypeEnabled || !cfg.RivalryEnabled || lastEnemySup == null || lastEnemySup == helper) return;
            if (U.Now - lastEnemyAt > cfg.RivalrySeconds * 1000) return;
            if (!HypeReady("rival:" + helper.Key + ":" + lastEnemySup.Key, 60)) return;
            PushHype(cfg.RivalryText, helper, 0, lastEnemySup.Nick);
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
                PushHype(cfg.ComebackText, s, 0, null);
                break;
            }
        }

        // ================================================================ queues
        //  fixed distance for enemies / allies on foot (cars and motorbikes still drive in from SpawnDistance)
        const float FootSpawnDistance = 8f;
        readonly Dictionary<string, long> waitNotified = new Dictionary<string, long>();

        int AliveBySup(Supporter s, bool enemy)
        {
            int n = 0;
            foreach (Tracked t in tracked) if (t.Sup == s && t.Enemy == enemy && t.DeadAt == 0) n++;
            return n;
        }

        // room of one supporter now (per supporter cap and free places in the arena)
        int RoomFor(Supporter s, bool enemy)
        {
            int free = (enemy ? cfg.MaxEnemies : cfg.MaxAllies) - AliveCount(enemy);
            if (cfg.MaxPerSupporter > 0 && s != null) free = Math.Min(free, cfg.MaxPerSupporter - AliveBySup(s, enemy));
            return Math.Max(0, free);
        }

        // "Amine: 40 waiting" when a big gift can not come all at once
        void NotifyWaiting(Supporter s, string action)
        {
            if (!cfg.NotifEnabled || s == null) return;
            bool enemy = !IsAllySpawn(action);
            int queued = 0;
            foreach (Job q in spawnQ) if (q.Sup == s && IsAllySpawn(ActionOf(q)) != enemy) queued += q.Left * SlotsFor(ActionOf(q));
            int waiting = queued - RoomFor(s, enemy);
            if (waiting <= 0) return;
            long last;
            string key = s.Key + (enemy ? "|e" : "|a");
            if (waitNotified.TryGetValue(key, out last) && U.Now - last < 3000) return;
            waitNotified[key] = U.Now;
            FeedItem f = new FeedItem();
            f.Parts = Txt.Parts(cfg.Tx(enemy ? "QueueWaitText" : "QueueWaitAllyText", "{name}: {count} waiting"), s.Nick, waiting, null, s.Level);
            f.Text = string.Join(" ", f.Parts.ToArray());
            f.Avatar = cfg.NotifAvatar ? Avatar(s) : null;
            f.Start = U.Now;
            f.End = U.Now + (long)(cfg.NotifSeconds * 1000);
            f.Col = cfg.TextColor;
            notifs.Add(f);
            while (notifs.Count > cfg.NotifMax) notifs.RemoveAt(0);
        }

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
            return !pl.Exists() || pl.IsDead || celeb || fakeDead || Function.Call<bool>(Hash.IS_PLAYER_DEAD, Game.Player);
        }

        void UpdateQueues()
        {
            if (paused || PlayerBusy()) return;
            // between two rounds the gifts wait in the queue and come in the next round
            if (cfg.RoundReset && cfg.RoundHoldQueue && phase == Phase.Ended) return;
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
                    // this supporter already has his share on the field: the next supporter goes first
                    if (cfg.MaxPerSupporter > 0 && j.Sup != null && AliveBySup(j.Sup, !ally) + Math.Min(need, cfg.MaxPerSupporter) > cfg.MaxPerSupporter) continue;
                    RunJob(j);
                    j.It.LastFire = now;
                    j.Left--;
                    spawnQ.RemoveAt(i);
                    if (j.Left > 0) spawnQ.Add(j);   // turns: the others come before his next unit
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
                    RunJob(j);
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
                r.Model = "g_m_m_chicold_01"; r.Weapon = "WEAPON_MICROSMG"; r.Health = 150; r.VehicleModel = "schafter2"; r.SpawnDistance = 28;
            }
            else if (action == "MotoHitman")
            {
                r.Model = "g_m_y_lost_01"; r.Weapon = "WEAPON_MICROSMG"; r.Health = 150; r.VehicleModel = "bati"; r.SpawnDistance = 26;
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

        // runs one unit of a job. A failing action is logged and counted as done,
        // so it can never stay at the head of the queue and block every next command.
        void RunJob(Job j)
        {
            try { ExecuteUnit(j); }
            catch (Exception ex) { U.Error("Action " + ActionOf(j), ex); }
            finally { curJob = null; }
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

        // map blips: always on the character (never on the car / motorbike), registered here and
        // marked with alpha 254 so leftovers of a previous run can be found and removed.
        const int BlipMark = 254;
        readonly List<Blip> blips = new List<Blip>();
        long nextBlipCheck;

        void AddBlip(Entity e, bool enemy)
        {
            try
            {
                Blip b = e.AddBlip();
                b.Color = enemy ? BlipColor.Red : BlipColor.Blue;
                b.Scale = 0.75f;
                b.Alpha = BlipMark;
                blips.Add(b);
            }
            catch { }
        }

        // a blip disappears as soon as its character is dead or gone
        void UpdateBlips()
        {
            if (U.Now < nextBlipCheck) return;
            nextBlipCheck = U.Now + 400;
            for (int i = blips.Count - 1; i >= 0; i--)
            {
                Blip b = blips[i];
                bool keep = false;
                try
                {
                    if (b.Exists())
                    {
                        Entity en = b.Entity;
                        keep = en != null && en.Exists() && !en.IsDead;
                        if (!keep) b.Delete();
                    }
                }
                catch { }
                if (!keep) blips.RemoveAt(i);
            }
        }

        // removes every blip of the script; orphans = also the marked ones left by an earlier run
        // (script reloaded / crashed) with their characters and vehicles
        void ClearBlips(bool orphans)
        {
            foreach (Blip b in blips) { try { if (b.Exists()) b.Delete(); } catch { } }
            blips.Clear();
            if (!orphans) return;
            try
            {
                Ped pl = Game.Player.Character;
                foreach (Blip b in World.GetAllBlips())
                {
                    try
                    {
                        if (b == null || !b.Exists() || b.Alpha != BlipMark) continue;
                        Entity en = b.Entity;
                        b.Delete();
                        if (en == null || !en.Exists() || (pl.Exists() && en.Handle == pl.Handle)) continue;
                        if (en is Ped && !IsTracked((Ped)en))
                        {
                            Ped p = (Ped)en;
                            if (p.IsInVehicle()) SafeDeleteVehicle(p.CurrentVehicle);
                            p.Delete();
                        }
                        else if (en is Vehicle) SafeDeleteVehicle((Vehicle)en);
                    }
                    catch { }
                }
            }
            catch (Exception ex) { U.Error("ClearBlips", ex); }
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
            if (phase == Phase.Running) { if (enemy) roundEnemies++; else roundAllies++; }
            SetupAura(t);
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
            try { MusicForEvent(j.It, a); } catch (Exception ex) { U.Error("Music", ex); }
            try { RegisterEvent(j, a); } catch (Exception ex) { U.Error("Events", ex); }
        }

        void SpawnFoot(Job j, bool enemy, bool animal)
        {
            Interaction it = j.It;
            Ped pl = Game.Player.Character;
            bool sky = string.Equals(it.Effect, "SkyDrop", StringComparison.OrdinalIgnoreCase);
            Vector3 pos = AroundPlayer(FootSpawnDistance);
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
            if (it.Blip || enemy) AddBlip(p, enemy);
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
            Vector3 pos = AroundPlayer(FootSpawnDistance);
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
            // cars / motorbikes come from close (25-30 m by default) so they are seen on stream quickly
            float dist = U.Clamp(it.SpawnDistance, 15f, 35f);
            double ang = U.Rng.NextDouble() * Math.PI * 2;
            Vector3 around = pl.Position + new Vector3((float)Math.Cos(ang) * dist, (float)Math.Sin(ang) * dist, 0);
            Vector3 road = World.GetNextPositionOnStreet(around);
            if (road == Vector3.Zero || road.DistanceTo(pl.Position) > dist * 1.6f || road.DistanceTo(pl.Position) < 12f) road = around;
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
                AddBlip(p, true);   // enemy crew: always the red point, on each rider
            }
            pm.MarkAsNoLongerNeeded();
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
            StopAura(t);
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
            StopAura(t);
            Ped pl = Game.Player.Character;
            try { if (t.Ped.AttachedBlip != null && t.Ped.AttachedBlip.Exists()) t.Ped.AttachedBlip.Delete(); } catch { }
            Entity killer = null;
            try { killer = t.Ped.Killer; } catch { }
            bool byPlayer = killer != null && pl.Exists() && (killer.Handle == pl.Handle || (pl.IsInVehicle() && killer.Handle == pl.CurrentVehicle.Handle));
            t.FxOk = !cfg.KillOnlyPlayer || byPlayer;
            if (cfg.KillFxEnabled && t.FxOk && cfg.CorpseCleanupSeconds <= 0.2f && !t.Animal) KillFx(t.Ped.Position);
            if (t.Enemy)
            {
                if (phase == Phase.Running) roundKills++;
                if (cfg.FeedEnabled)
                {
                    FeedItem f = new FeedItem();
                    f.Parts = Txt.Parts(cfg.Tx("KillFeedText", "{name}"), t.Sup != null ? t.Sup.Nick : "?", 1, null, 0);
                    f.Text = string.Join(" ", f.Parts.ToArray());
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

        // [Effects] Stack: Extend (add the time), Reset (restart the time), Max (keep the longest)
        //           MaxSeconds: an effect can never last longer than this (0 = no limit)
        class FxInfo { public Supporter Sup; public string Action, Icon, Title; public long Start; public int Hits; }
        readonly Dictionary<string, FxInfo> fxInfo = new Dictionary<string, FxInfo>();
        readonly Dictionary<string, long> keepNext = new Dictionary<string, long>();
        Job curJob;

        void StartTimed(string key, float seconds)
        {
            long now = U.Now;
            long end;
            long add = (long)(Math.Max(0.5f, seconds) * 1000);
            bool running = fxEnd.TryGetValue(key, out end) && end > now;
            string mode = cfg.FxStack ?? "";
            long ne;
            if (!running || mode.Equals("Reset", StringComparison.OrdinalIgnoreCase)) ne = now + add;
            else if (mode.Equals("Max", StringComparison.OrdinalIgnoreCase)) ne = Math.Max(end, now + add);
            else ne = end + add;
            if (cfg.FxMaxSeconds > 0) ne = Math.Min(ne, now + (long)(cfg.FxMaxSeconds * 1000));
            fxEnd[key] = ne;
            FxInfo fi;
            if (!running || !fxInfo.TryGetValue(key, out fi)) { fi = new FxInfo(); fxInfo[key] = fi; }
            fi.Start = now;   // the bar refills on every new gift
            fi.Hits++;
            fi.Action = key.StartsWith("Keep:") ? key.Substring(5) : key;
            if (curJob != null)
            {
                fi.Sup = curJob.Sup;
                fi.Icon = ImgPath(curJob.It.ActionImage);
                fi.Title = curJob.It.Title;
            }
            if (string.IsNullOrEmpty(fi.Title)) fi.Title = fi.Action;
        }

        // ends every running effect now (Emergency key, StopEffectsKey)
        void StopAllEffects()
        {
            ClearEvents();
            bool fire = Active("Keep:Fire");
            fxEnd.Clear();
            fxInfo.Clear();
            keepNext.Clear();
            carRainAcc = 0;
            nextSecond = 0;
            popups.Clear();
            Ped pl = Game.Player.Character;
            if (fire && pl.Exists()) Function.Call(Hash.STOP_ENTITY_FIRE, pl);
        }

        // instant actions that can be repeated for KeepSeconds (0 = once)
        static readonly string[] KeepActions = { "Fire", "Ragdoll", "RemoveWeapons", "EjectVehicle", "RemoveVehicle", "BurstTires",
            "ClearArea", "Launch", "Heal", "BoostVehicle", "ExplodeNearby", "Teleport" };

        static bool IsKeep(string a) { return Array.IndexOf(KeepActions, a) >= 0; }

        static int KeepInterval(string a)
        {
            switch (a)
            {
                case "Fire": return 2500;
                case "Ragdoll": return 2800;
                case "RemoveWeapons": return 400;
                case "EjectVehicle": return 600;
                case "RemoveVehicle": return 700;
                case "BurstTires": return 1000;
                case "ClearArea": return 2000;
                case "Launch": return 3500;
                case "Heal": return 1000;
                case "BoostVehicle": return 1500;
                case "ExplodeNearby": return 2500;
                case "Teleport": return 6000;
            }
            return 1000;
        }

        // one shot of an instant action (also used by the KeepSeconds repeat)
        void Instant(string a)
        {
            Ped pl = Game.Player.Character;
            if (!pl.Exists()) return;
            Vehicle pv = PlayerVehicle();
            switch (a)
            {
                case "Heal":
                    Function.Call(Hash.SET_ENTITY_HEALTH, pl, Function.Call<int>(Hash.GET_ENTITY_MAX_HEALTH, pl), 0);
                    pl.Armor = 100;
                    if (pv != null) { pv.Repair(); }
                    break;
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
                case "Fire": if (!fakeDead) Function.Call(Hash.START_ENTITY_FIRE, pl); break;
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
                case "Ragdoll": if (!fakeDead) Function.Call(Hash.SET_PED_TO_RAGDOLL, pl, 3000, 3000, 0, false, false, false); break;
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
                case "Teleport":
                    {
                        float[] s = TeleportSpots[U.Rng.Next(TeleportSpots.Length)];
                        Entity e = pv != null ? (Entity)pv : pl;
                        Function.Call(Hash.SET_ENTITY_COORDS, e, s[0], s[1], s[2], false, false, false, false);
                        break;
                    }
            }
        }

        Vehicle PlayerVehicle()
        {
            Ped pl = Game.Player.Character;
            if (pl.Exists() && pl.IsInVehicle()) return pl.CurrentVehicle;
            return null;
        }

        void RunAction(Job j, string a)
        {
            curJob = j;
            try { PlayerFx(j, a); } catch (Exception ex) { U.Error("PlayerFx", ex); }
            Interaction it = j.It;
            Ped pl = Game.Player.Character;
            Vehicle pv = PlayerVehicle();
            if (IsKeep(a))
            {
                Instant(a);
                if (it.KeepSeconds > 0)
                {
                    StartTimed("Keep:" + a, it.KeepSeconds);
                    keepNext[a] = U.Now + KeepInterval(a);
                }
                return;
            }
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
                case "GodMode": StartTimed("GodMode", it.Duration); break;
                case "GiveVehicle":
                    {
                        Model m = LoadModel(PickVehicle(it.VehicleModel), "zentorno");
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
                case "KillPlayer":
                    fxEnd.Remove("GodMode");
                    StopCam();
                    killPlayerSup = j.Sup;
                    killPlayerAt = U.Now;
                    if (cfg.CustomDeath && hpBuf > 0) { if (!fakeDead) FakeDeath(j.Sup); break; }
                    pl.IsInvincible = false;
                    Function.Call(Hash.SET_ENTITY_HEALTH, pl, 0, 0);
                    break;
                case "Airstrike": StartTimed("Airstrike", it.Duration); break;
                case "Freeze": StartTimed("Freeze", it.Duration); break;
                case "ExplodeVehicle":
                    if (pv != null) Function.Call(Hash.EXPLODE_VEHICLE, pv, true, false);
                    break;
                case "Skyfall":
                    {
                        Function.Call(Hash.GIVE_WEAPON_TO_PED, pl, U.HashInt("GADGET_PARACHUTE"), 1, false, false);
                        Vector3 p = pl.Position;
                        Function.Call(Hash.SET_ENTITY_COORDS, pl, p.X, p.Y, p.Z + 350f, false, false, false, false);
                        break;
                    }
                case "Drunk": StartTimed("Drunk", it.Duration); break;
                case "Earthquake": StartTimed("Earthquake", it.Duration); break;
                case "CarRain":
                    carRainPerSec = Math.Max(0.2f, Math.Min(20f, it.Amount));
                    StartTimed("CarRain", it.Duration);
                    break;
                case "Storm": StartTimed("Storm", it.Duration); break;
                case "Blackout": StartTimed("Blackout", it.Duration); break;
                case "SlowMotion": StartTimed("SlowMotion", it.Duration); break;
                case "LowGravity": StartTimed("LowGravity", it.Duration); break;
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

        // GiveVehicle: a model name, "Random" (a different car every time) or "Cycle" (all cars one after the other)
        static readonly string[] GiveCars = {
            "zentorno", "adder", "t20", "osiris", "turismor", "entityxf", "cheetah", "infernus", "vacca", "bullet", "voltic",
            "reaper", "fmj", "pfister811", "tyrus", "vagner", "xa21", "italigtb", "nero", "tempesta", "visione", "cyclone",
            "krieger", "emerus", "thrax", "deveste", "furia", "tezeract", "sultanrs", "elegy", "jester", "massacro", "comet2",
            "banshee", "carbonizzare", "coquette", "feltzer2", "ninef", "rapidgt", "schafter3", "kuruma", "dominator", "gauntlet",
            "sabregt", "dukes", "buffalo", "kamacho", "trophytruck", "sandking", "bati", "akuma", "hakuchou", "shotaro", "insurgent2" };
        int giveCarIndex = -1;

        string PickVehicle(string model)
        {
            if (Is(model, "Random")) return GiveCars[U.Rng.Next(GiveCars.Length)];
            if (Is(model, "Cycle")) { giveCarIndex = (giveCarIndex + 1) % GiveCars.Length; return GiveCars[giveCarIndex]; }
            return model;
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

            // blackout: city lights off; BlackoutNight makes it night so it is visible in daytime too
            bool black = Active("Blackout");
            if (black != blackoutOn)
            {
                Function.Call(Hash.SET_ARTIFICIAL_LIGHTS_STATE, black);
                if (black && cfg.BlackoutNight && !nightSet)
                {
                    savedHour = Function.Call<int>(Hash.GET_CLOCK_HOURS);
                    savedMinute = Function.Call<int>(Hash.GET_CLOCK_MINUTES);
                    nightSet = true;
                }
                else if (!black && nightSet)
                {
                    Function.Call(Hash.SET_CLOCK_TIME, savedHour, savedMinute, 0);
                    Function.Call(Hash.PAUSE_CLOCK, cfg.LockTime);
                    nightSet = false;
                }
                blackoutOn = black;
            }
            if (black)
            {
                Function.Call(Hash.SET_ARTIFICIAL_LIGHTS_STATE, true);
                if (nightSet) { Function.Call(Hash.SET_CLOCK_TIME, 0, 30, 0); Function.Call(Hash.PAUSE_CLOCK, true); }
            }

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
            if (shake && (!shaking || (now >= nextQuake && !Function.Call<bool>(Hash.IS_GAMEPLAY_CAM_SHAKING))))
            {
                Function.Call(Hash.SHAKE_GAMEPLAY_CAM, quake ? "ROAD_VIBRATION_SHAKE" : "DRUNK_SHAKE", quake ? cfg.QuakeStrength : 1.5f);
                shaking = true;
            }
            else if (!shake && shaking) { Function.Call(Hash.STOP_GAMEPLAY_CAM_SHAKING, true); shaking = false; }

            if (quake && now >= nextQuake)
            try
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
                if (cfg.QuakeRagdollPlayer && !fakeDead && !pl.IsInVehicle() && U.Rng.Next(4) == 0) Function.Call(Hash.SET_PED_TO_RAGDOLL, pl, 1000, 1500, 0, false, false, false);
            }
            catch (Exception ex) { U.Error("Earthquake", ex); }

            if (!Active("CarRain")) carRainAcc = 0;
            else try
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
            catch (Exception ex) { U.Error("CarRain", ex); }

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

            // repeating instant actions (KeepSeconds)
            foreach (string ka in KeepActions)
            {
                if (!Active("Keep:" + ka)) continue;
                long nx;
                if (keepNext.TryGetValue(ka, out nx) && now < nx) continue;
                keepNext[ka] = now + KeepInterval(ka);
                try { Instant(ka); } catch (Exception ex) { U.Error("Keep " + ka, ex); }
            }

            // tidy finished timers (the bar above the player is empty -> the effect stops)
            List<string> done = null;
            foreach (KeyValuePair<string, long> kv in fxEnd) if (kv.Value <= now) { if (done == null) done = new List<string>(); done.Add(kv.Key); }
            if (done != null) foreach (string k in done)
            {
                fxEnd.Remove(k);
                fxInfo.Remove(k);
                if (k == "Weather" || k == "Storm") nextSecond = 0;
                if (k == "Keep:Fire") Function.Call(Hash.STOP_ENTITY_FIRE, pl);
                if (k.StartsWith("Keep:")) keepNext.Remove(k.Substring(5));
            }
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
                hpBuf = 0;          // the game resets the health on respawn
                fakeDead = false;
                OnPlayerDeath();
            }
            else if (!dead && deathHandled && !celeb && killerCamEnd == 0)
            {
                deathHandled = false;
            }

            if (phase == Phase.Running && cfg.ChallengeEnabled)
            {
                if (!paused && camMode.Length == 0 && !dead && !fakeDead && !celeb) roundMs += dt;
                if (roundMs >= roundDurMs) EndRound(true, null);
            }
            else if (phase == Phase.Ended)
            {
                bool screenDone = !cfg.EndScreenEnabled || now >= endScreenUntil;
                if (screenDone && !dead && !fakeDead && !celeb && camMode.Length == 0 && !Screen.IsFadedOut && !Screen.IsFadingIn)
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
            // a win clears the arena now; a loss waits for the killer camera / celebration (StartRound clears it)
            if (win) ResetArena();
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
            SetDeathKiller(killer, null);
            if (cfg.CelebEnabled) StartCelebration();
        }

        // ================================================================ damage effects on the health bar
        //  [DamageFx] Numbers: "-5" floats up from the bar for every hit (+N for heals),
        //  Ghost: the lost part stays white a moment then drains. No blood anywhere (NoBlood).
        class DmgPop { public int Amount; public long Start; }
        readonly List<DmgPop> dmgPops = new List<DmgPop>();
        int lastHp = -1;
        float ghostFrac;
        long ghostHold, dmgFlash, lastWatch;

        long nextBloodClear;

        // removes the blood the game puts on the player and on the spawned characters
        void ClearBlood()
        {
            if (!cfg.NoBlood || U.Now < nextBloodClear) return;
            nextBloodClear = U.Now + 200;
            Ped pl = Game.Player.Character;
            if (pl.Exists()) { Function.Call(Hash.CLEAR_PED_BLOOD_DAMAGE, pl); Function.Call(Hash.RESET_PED_VISIBLE_DAMAGE, pl); }
            foreach (Tracked t in tracked)
                if (t.Ped != null && t.Ped.Exists()) { Function.Call(Hash.CLEAR_PED_BLOOD_DAMAGE, t.Ped); Function.Call(Hash.RESET_PED_VISIBLE_DAMAGE, t.Ped); }
            foreach (Ped p in dancers)
                if (p != null && p.Exists()) Function.Call(Hash.CLEAR_PED_BLOOD_DAMAGE, p);
        }

        void WatchHealth()
        {
            long now = U.Now;
            float dt = Math.Min(0.25f, Math.Max(0, (now - lastWatch) / 1000f));
            lastWatch = now;
            Ped pl = Game.Player.Character;
            if (!cfg.DmgEnabled || !started || !pl.Exists() || pl.IsDead) { lastHp = -1; dmgPops.Clear(); return; }
            int hp = PlayerHpNow(), max = PlayerHpMax();
            float frac = U.Clamp(hp / (float)max, 0, 1);
            if (lastHp >= 0 && hp != lastHp)
            {
                int diff = hp - lastHp;
                bool show = diff < 0 || diff < max * 0.6f;   // no "+1000" when the round refills the health
                if (show && cfg.DmgNumbers)
                {
                    DmgPop last = dmgPops.Count > 0 ? dmgPops[dmgPops.Count - 1] : null;
                    if (last != null && now - last.Start < 250 && Math.Sign(last.Amount) == Math.Sign(diff)) last.Amount += diff;
                    else { DmgPop p = new DmgPop(); p.Amount = diff; p.Start = now; dmgPops.Add(p); }
                    while (dmgPops.Count > 6) dmgPops.RemoveAt(0);
                }
                if (diff < 0)
                {
                    float before = U.Clamp(lastHp / (float)max, 0, 1);
                    if (ghostFrac < before) ghostFrac = before;
                    ghostHold = now + 450;
                    dmgFlash = now;
                }
            }
            lastHp = hp;
            if (now > ghostHold) ghostFrac = Math.Max(frac, ghostFrac - 0.8f * dt);
            if (ghostFrac < frac) ghostFrac = frac;
            dmgPops.RemoveAll(delegate(DmgPop p) { return now - p.Start > 1200; });
        }

        // drawn over a health bar (bx, by, bw, bh = the bar, frac = health now)
        void DamageFx(float bx, float by, float bw, float bh, float frac)
        {
            if (!cfg.DmgEnabled) return;
            long now = U.Now;
            if (cfg.DmgGhost && ghostFrac > frac + 0.001f)
                Gfx.Rect(bx + bw * frac, by, bw * (ghostFrac - frac), bh, Color.FromArgb(235, 255, 236, 236));
            if (now - dmgFlash < 260)
                Gfx.Rect(bx, by, Math.Max(2, bw * frac), bh, Color.FromArgb((int)(170 * (1 - (now - dmgFlash) / 260f)), 255, 40, 40));
            int i = 0;
            foreach (DmgPop p in dmgPops)
            {
                float t = (now - p.Start) / 1200f;
                int a = (int)(255 * (t < 0.75f ? 1 : (1 - t) / 0.25f));
                float pop = t < 0.12f ? 1.35f - t * 3 : 1f;
                string s = (p.Amount > 0 ? "+" : "") + p.Amount.ToString(U.IC);
                Color c = p.Amount < 0 ? Color.FromArgb(a, 255, 70, 70) : Color.FromArgb(a, 80, 255, 140);
                float x = bx + bw * frac + (i % 3) * 14 - 6;
                Gfx.Text(s, x, by - 18 - t * 30, TXT * 1.15f * pop, c, Alignment.Center);
                i++;
            }
        }

        // ================================================================ close "TikTok" camera
        //  [Camera] ZoomKey on/off (ZoomOnStart = on with the start key), closer over-the-shoulder view
        //  that follows the normal camera rotation (mouse / stick). Only the view changes, not the HUD.
        //  ZoomInKey / ZoomOutKey move it closer / farther while playing.
        bool zoomOn;
        float zoomAdjust;
        Camera zcam;
        bool zoomRendering;
        float zoomSmoothDist = -1;

        void StopZoomCam()
        {
            if (zoomRendering && camMode.Length == 0) Function.Call(Hash.RENDER_SCRIPT_CAMS, false, true, 400, true, false, 0);
            zoomRendering = false;
            try { if (zcam != null && zcam.Exists()) { zcam.IsActive = false; zcam.Delete(); } } catch { }
            zcam = null;
            zoomSmoothDist = -1;
        }

        void UpdateZoomCam()
        {
            Ped pl = Game.Player.Character;
            bool aiming = cfg.ZoomNormalWhenAiming && (GameplayCamera.IsAimCamActive || GameplayCamera.IsFirstPersonAimCamActive);
            bool want = zoomOn && started && pl.Exists() && camMode.Length == 0 && !Function.Call<bool>(Hash.IS_PAUSE_MENU_ACTIVE) && !aiming;
            if (!want)
            {
                if (zcam != null && camMode.Length == 0) StopZoomCam();
                else if (camMode.Length > 0) { zoomRendering = false; if (zcam != null && zcam.Exists()) zcam.IsActive = false; }
                return;
            }
            if (zcam == null || !zcam.Exists()) zcam = World.CreateCamera(GameplayCamera.Position, GameplayCamera.Rotation, cfg.ZoomFov);
            bool inVeh = pl.IsInVehicle();
            float dist = Math.Max(0.6f, (inVeh ? cfg.ZoomVehDistance : cfg.ZoomDistance) + zoomAdjust);
            float height = inVeh ? cfg.ZoomVehHeight : cfg.ZoomHeight;
            Vector3 rot = GameplayCamera.Rotation;
            Vector3 dir = GameplayCamera.Direction;
            double yaw = rot.Z * Math.PI / 180.0;
            Vector3 right = new Vector3((float)Math.Cos(yaw), (float)Math.Sin(yaw), 0);
            Vector3 anchor = (inVeh ? pl.CurrentVehicle.Position : pl.Position) + new Vector3(0, 0, height) + right * (inVeh ? 0 : cfg.ZoomSide);
            // keep the camera out of walls: stop in front of what is between the character and the camera
            float d = dist;
            RaycastResult hit = World.Raycast(anchor, anchor - dir * dist, IntersectFlags.Map, inVeh ? (Entity)pl.CurrentVehicle : pl);
            if (hit.DidHit) d = Math.Max(0.3f, hit.HitPosition.DistanceTo(anchor) - 0.25f);
            zoomSmoothDist = zoomSmoothDist < 0 ? d : (d < zoomSmoothDist ? d : zoomSmoothDist + (d - zoomSmoothDist) * 0.15f);
            zcam.Position = anchor - dir * zoomSmoothDist;
            zcam.Rotation = rot;
            zcam.FieldOfView = cfg.ZoomFov;
            if (!zoomRendering)
            {
                zcam.IsActive = true;
                Function.Call(Hash.RENDER_SCRIPT_CAMS, true, true, 400, true, false, 0);
                zoomRendering = true;
            }
        }

        // ================================================================ music
        //  [Music] background playlist (TikArena/music/background) + one track per event
        //  (TikArena/music/events/<Interaction Music>) played while the event is on;
        //  priority: death music > event music > background. MusicKey = on / off.
        bool musicOn = true, bgPaused;
        List<string> bgList = new List<string>();
        int bgIndex = -1;
        string evtFile;
        Interaction evtIt;
        string evtKey;           // timed effect key, or null for spawns
        bool deathMusic;
        long nextMusicCheck;

        static readonly string[] MusicExt = { ".mp3", ".wav", ".wma", ".m4a" };

        string MusicPath(string sub, string file)
        {
            if (string.IsNullOrEmpty(file)) return null;
            string p = Path.IsPathRooted(file) ? file : Path.Combine(U.DataDir, "music", sub, file);
            if (File.Exists(p)) return p;
            if (Path.GetExtension(p).Length == 0) foreach (string e in MusicExt) if (File.Exists(p + e)) return p + e;
            return null;
        }

        void LoadPlaylist()
        {
            bgList.Clear();
            try
            {
                string dir = Path.Combine(U.DataDir, "music", "background");
                Directory.CreateDirectory(dir);
                Directory.CreateDirectory(Path.Combine(U.DataDir, "music", "events"));
                foreach (string f in Directory.GetFiles(dir))
                    if (Array.IndexOf(MusicExt, Path.GetExtension(f).ToLowerInvariant()) >= 0) bgList.Add(f);
                bgList.Sort(StringComparer.OrdinalIgnoreCase);
                if (cfg.MusicShuffle) for (int i = bgList.Count - 1; i > 0; i--) { int k = U.Rng.Next(i + 1); string t = bgList[i]; bgList[i] = bgList[k]; bgList[k] = t; }
            }
            catch (Exception ex) { U.Error("Playlist", ex); }
            bgIndex = -1;
        }

        // an event with its own music started (called for every executed unit)
        void MusicForEvent(Interaction it, string action)
        {
            if (!cfg.MusicEnabled || !musicOn || it == null || string.IsNullOrEmpty(it.Music)) return;
            string file = MusicPath("events", it.Music);
            if (file == null) { U.Log("music not found: " + it.Music); return; }
            evtIt = it;
            evtKey = IsSpawn(action) ? null : (IsKeep(action) && it.KeepSeconds > 0 ? "Keep:" + action : action);
            if (evtFile == file && Mci.Mode("tka_evt") == "playing") return;
            evtFile = file;
            if (!deathMusic) StartEventTrack();
        }

        void StartEventTrack()
        {
            if (evtFile == null) return;
            PauseBackground();
            if (!Mci.Play("tka_evt", evtFile, true, cfg.MusicEventVolume)) { U.Log("music: can not play " + evtFile); evtFile = null; }
        }

        bool EventStillOn()
        {
            if (evtIt == null || fakeDead || phase == Phase.Ended) return false;
            if (evtKey != null) return Active(evtKey);
            foreach (Tracked t in tracked) if (t.It == evtIt && t.DeadAt == 0) return true;
            foreach (Job j in spawnQ) if (j.It == evtIt) return true;
            return false;
        }

        void StopEventMusic()
        {
            Mci.Close("tka_evt");
            evtFile = null;
            evtIt = null;
            evtKey = null;
        }

        void PauseBackground()
        {
            if (bgIndex >= 0 && !bgPaused) { Mci.Pause("tka_bg"); bgPaused = true; }
        }

        void NextBackground()
        {
            if (bgList.Count == 0) return;
            for (int tries = 0; tries < bgList.Count; tries++)
            {
                bgIndex = (bgIndex + 1) % bgList.Count;
                if (Mci.Play("tka_bg", bgList[bgIndex], false, cfg.MusicVolume)) { bgPaused = false; return; }
            }
            bgIndex = -1;
        }

        // the death / celebration music (instead of the old WAV player)
        bool PlayDeathMusic(string file)
        {
            if (!cfg.MusicEnabled) return false;
            string p = Path.IsPathRooted(file) ? file : Path.Combine(U.DataDir, "sounds", file);
            if (!File.Exists(p)) p = MusicPath("events", file);
            if (p == null || !File.Exists(p)) return false;
            PauseBackground();
            if (evtFile != null) Mci.Pause("tka_evt");
            deathMusic = Mci.Play("tka_death", p, true, cfg.MusicDeathVolume);
            return deathMusic;
        }

        void StopDeathMusic()
        {
            if (!deathMusic) return;
            Mci.Close("tka_death");
            deathMusic = false;
            if (evtFile != null) Mci.Resume("tka_evt");
        }

        void UpdateMusic()
        {
            if (!cfg.MusicEnabled || U.Now < nextMusicCheck) return;
            nextMusicCheck = U.Now + 500;
            if (evtFile != null && !EventStillOn()) StopEventMusic();
            bool bgWanted = musicOn && cfg.MusicBackground && started && evtFile == null && !deathMusic;
            if (!bgWanted) { PauseBackground(); return; }
            if (bgIndex >= 0 && bgPaused) { Mci.Resume("tka_bg"); bgPaused = false; return; }
            string mode = bgIndex >= 0 ? Mci.Mode("tka_bg") : "";
            if (bgIndex < 0 || mode == "stopped" || mode.Length == 0) NextBackground();
        }

        void ToggleMusic()
        {
            musicOn = !musicOn;
            if (!musicOn) { StopEventMusic(); PauseBackground(); }
            else LoadPlaylist();
            Status("Music: " + (musicOn ? "ON" : "OFF"));
        }

        void StopAllMusic()
        {
            Mci.Close("tka_evt"); Mci.Close("tka_bg"); Mci.Close("tka_death");
            evtFile = null; evtIt = null; evtKey = null; deathMusic = false; bgIndex = -1; bgPaused = false;
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
            AddExtraDancers(n);
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
            if (cfg.DeathSoundEnabled && cfg.DeathSound.Length > 0 && !PlayDeathMusic(cfg.DeathSound)) deathPlayer = Wav(cfg.DeathSound, true);
            if (camMode.Length == 0) StartCam("celeb", pl, cfg.CelebSeconds);
        }

        // who killed me: shown on the side of the screen while dead, and his characters dance
        Supporter deathKiller;
        Interaction deathKillerIt;
        long deathKillerAt;
        readonly List<Ped> extraDancers = new List<Ped>();

        void SetDeathKiller(Supporter s, Interaction it)
        {
            deathKiller = s;
            deathKillerIt = it;
            deathKillerAt = U.Now;
        }

        string DancerModel()
        {
            if (deathKillerIt != null && deathKillerIt.Model.Length > 0 && !IsAllySpawn(deathKillerIt.Action)) return deathKillerIt.Model;
            foreach (Interaction it in cfg.Interactions) if (it.Enabled && it.Action == "SpawnEnemy" && it.Model.Length > 0) return it.Model;
            return "s_m_y_clown_01";
        }

        // not enough enemies alive to dance: add a few (deleted after the celebration)
        void AddExtraDancers(int have)
        {
            int need = Math.Min(cfg.ExtraDancers, 6) - have;
            if (need <= 0) return;
            Ped pl = Game.Player.Character;
            Model m = LoadModel(DancerModel(), "s_m_y_clown_01");
            if (!m.IsLoaded) return;
            for (int i = 0; i < need; i++)
            {
                double a = (have + i) * Math.PI * 2 / Math.Max(1, have + need);
                Vector3 pos = pl.Position + new Vector3((float)Math.Cos(a) * 2.8f, (float)Math.Sin(a) * 2.8f, 0f);
                Ped p = World.CreatePed(m, pos, HeadingTo(pos, pl.Position));
                if (p == null || !p.Exists()) continue;
                p.IsInvincible = true;
                p.BlockPermanentEvents = true;
                if (cfg.CelebDance) Function.Call(Hash.TASK_PLAY_ANIM, p, cfg.DanceDict, cfg.DanceAnim, 8f, -8f, -1, 1, 0f, false, false, false);
                extraDancers.Add(p);
                dancers.Add(p);
            }
            m.MarkAsNoLongerNeeded();
        }

        // side banner "killed by" with the supporter picture
        void DrawKillerBanner()
        {
            if (!cfg.KillerBanner || deathKiller == null || !(fakeDead || celeb)) return;
            if (U.Now - deathKillerAt > 60000) return;
            float t = U.Clamp((U.Now - deathKillerAt) / 350f, 0, 1);
            float w = 230, h = 84;
            float x0 = frameX + 14 - (1 - t) * (w + 20), y0 = frameY + (vertical ? 250 : 300);
            Gfx.Origin(x0, y0, vertical ? Math.Min(1f, (frameW - 20) / w) : 1f);
            DBox(0, 0, w, h);
            Gfx.Rect(0, 0, 4, h, cLoss);
            Gfx.Text(cfg.Tx("KilledByText", "Killed by"), 16, 6, SMALL * 1.1f, cLoss, Alignment.Left);
            AvRing(deathKiller, 44, 52, 44, cLoss, true, false);
            Gfx.Text(U.Trunc(deathKiller.Nick, 14), 76, 36, TXT * 1.1f, Color.White, Alignment.Left);
            if (deathKiller.Coins > 0) Gfx.Text(U.Coins(deathKiller.Coins), 76, 58, SMALL, U.WithAlpha(cfg.HypeColor, 230), Alignment.Left);
            Gfx.Origin(0, 0, 1);
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
            StopDeathMusic();
            try { if (coffin != null && coffin.Exists()) coffin.Delete(); } catch { }
            coffin = null;
            foreach (Ped p in dancers)
            {
                if (p == null || !p.Exists()) continue;
                p.IsInvincible = false;
                Function.Call(Hash.CLEAR_PED_TASKS, p);
            }
            dancers.Clear();
            foreach (Ped p in extraDancers) { try { if (p != null && p.Exists()) p.Delete(); } catch { } }
            extraDancers.Clear();
            if (camMode == "celeb" || camMode == "killer") StopCam();
            killerCamEnd = 0;
            if (deathPaused) { Function.Call(Hash.PAUSE_DEATH_ARREST_RESTART, false); deathPaused = false; }
        }

        // ================================================================ auras (effects around spawned characters)

        void SetupAura(Tracked t)
        {
            if (!cfg.AuraEnabled || t.Ped == null || !t.Leader && t.Veh != null) { t.AuraType = "None"; return; }
            string type = t.It != null ? t.It.Aura : "Default";
            if (string.IsNullOrEmpty(type) || type.Equals("Default", StringComparison.OrdinalIgnoreCase)) type = t.Enemy ? cfg.AuraEnemy : cfg.AuraAlly;
            t.AuraType = string.IsNullOrEmpty(type) ? "None" : type;
            Color side = t.Enemy ? cfg.AuraEnemyColor : cfg.AuraAllyColor;
            t.AuraCol = t.It != null && t.It.AuraColor.Length > 0 ? U.ParseColor(t.It.AuraColor, side) : side;
            string fx = null;
            switch (t.AuraType.ToLowerInvariant())
            {
                case "fire": fx = cfg.FxFire; break;
                case "smoke": fx = cfg.FxSmoke; break;
                case "electric": fx = cfg.FxElectric; break;
                case "sparkles": fx = cfg.FxSparkles; break;
            }
            if (fx == null) return;
            try
            {
                string[] p = fx.Split('|');
                if (p.Length < 2) return;
                string asset = p[0].Trim(), name = p[1].Trim();
                Function.Call(Hash.REQUEST_NAMED_PTFX_ASSET, asset);
                int guard = 0;
                while (!Function.Call<bool>(Hash.HAS_NAMED_PTFX_ASSET_LOADED, asset) && guard++ < 30) Script.Yield();
                Function.Call(Hash.USE_PARTICLE_FX_ASSET, asset);
                t.LoopFx = Function.Call<int>(Hash.START_PARTICLE_FX_LOOPED_ON_ENTITY, name, t.Ped, 0f, 0f, -0.2f, 0f, 0f, 0f, cfg.AuraFxScale, false, false, false);
                if (t.LoopFx != 0 && t.AuraType.ToLowerInvariant() != "fire")
                    Function.Call(Hash.SET_PARTICLE_FX_LOOPED_COLOUR, t.LoopFx, t.AuraCol.R / 255f, t.AuraCol.G / 255f, t.AuraCol.B / 255f, false);
            }
            catch (Exception ex) { U.Error("Aura", ex); }
        }

        void StopAura(Tracked t)
        {
            if (t.LoopFx == 0) return;
            try { Function.Call(Hash.STOP_PARTICLE_FX_LOOPED, t.LoopFx, false); } catch { }
            t.LoopFx = 0;
        }

        static Color Rainbow(float phase, int alpha)
        {
            double h = (phase % 1f) * 6.0;
            int i = (int)h;
            float f = (float)(h - i);
            float q = 1 - f;
            float r, g, b;
            switch (i)
            {
                case 0: r = 1; g = f; b = 0; break;
                case 1: r = q; g = 1; b = 0; break;
                case 2: r = 0; g = 1; b = f; break;
                case 3: r = 0; g = q; b = 1; break;
                case 4: r = f; g = 0; b = 1; break;
                default: r = 1; g = 0; b = q; break;
            }
            return Color.FromArgb(alpha, (int)(r * 255), (int)(g * 255), (int)(b * 255));
        }

        void Marker(int type, Vector3 pos, Vector3 rot, Vector3 scale, Color c, bool faceCam, bool bob)
        {
            Function.Call(Hash.DRAW_MARKER, type, pos.X, pos.Y, pos.Z, 0f, 0f, 0f, rot.X, rot.Y, rot.Z, scale.X, scale.Y, scale.Z,
                (int)c.R, (int)c.G, (int)c.B, (int)c.A, bob, faceCam, 2, false, 0, 0, false);
        }

        void Light(Vector3 pos, Color c, float range, float intensity)
        {
            Function.Call(Hash.DRAW_LIGHT_WITH_RANGE, pos.X, pos.Y, pos.Z, (int)c.R, (int)c.G, (int)c.B, range, intensity);
        }

        void UpdateAuras()
        {
            if (!cfg.AuraEnabled || tracked.Count == 0) return;
            Ped pl = Game.Player.Character;
            if (!pl.Exists()) return;
            Vector3 me = pl.Position;
            float time = U.Now / 1000f;
            foreach (Tracked t in tracked)
            {
                if (t.DeadAt != 0 || t.Ped == null || !t.Ped.Exists()) continue;
                bool crown = cfg.AuraKingCrown && king != null && t.Sup == king && t.Leader;
                if (t.AuraType == "None" && !crown) continue;
                bool inVeh = t.Veh != null && t.Veh.Exists() && t.Ped.IsInVehicle();
                if (inVeh && !t.Leader) continue;
                Vector3 p = inVeh ? t.Veh.Position : t.Ped.Position;
                if (p.DistanceTo(me) > cfg.AuraMaxDistance) continue;
                Vector3 feet = p - new Vector3(0, 0, inVeh ? 0.4f : 0.95f);
                Color c = t.AuraCol;
                string a = t.AuraType.ToLowerInvariant();
                if (a == "rainbow") c = Rainbow(time * 0.35f + (t.SpawnAt % 1000) / 1000f, 255);
                float rs = cfg.AuraRingSize * (inVeh ? 2.6f : 1f);
                switch (a)
                {
                    case "glow":
                        Light(p + new Vector3(0, 0, 0.4f), c, cfg.AuraRange, cfg.AuraIntensity);
                        break;
                    case "ring":
                        Marker(25, feet, new Vector3(0, 0, time * 60f % 360f), new Vector3(rs, rs, rs), U.WithAlpha(c, 210), false, false);
                        Light(p, c, cfg.AuraRange * 0.6f, cfg.AuraIntensity * 0.5f);
                        break;
                    case "pulse":
                    case "rainbow":
                        for (int k = 0; k < 2; k++)
                        {
                            float ph = (time * 0.8f + k * 0.5f) % 1f;
                            float sc = rs * (0.5f + ph * 1.4f);
                            Marker(25, feet, Vector3.Zero, new Vector3(sc, sc, sc), U.WithAlpha(c, (int)(230 * (1 - ph))), false, false);
                        }
                        Light(p, c, cfg.AuraRange, cfg.AuraIntensity * (0.6f + 0.4f * (float)Math.Sin(time * 5)));
                        break;
                    case "beam":
                        Marker(1, feet, Vector3.Zero, new Vector3(rs * 0.7f, rs * 0.7f, 9f), U.WithAlpha(c, 60), false, false);
                        Marker(25, feet, new Vector3(0, 0, time * 90f % 360f), new Vector3(rs, rs, rs), U.WithAlpha(c, 200), false, false);
                        Light(p + new Vector3(0, 0, 1f), c, cfg.AuraRange * 1.4f, cfg.AuraIntensity);
                        break;
                    case "crown":
                        crown = true;
                        break;
                    case "fire":
                    case "smoke":
                    case "electric":
                    case "sparkles":
                        Light(p, c, cfg.AuraRange * 0.8f, cfg.AuraIntensity * 0.6f);
                        break;
                }
                if (crown)
                {
                    Color kc = a == "crown" ? c : cfg.AuraKingColor;
                    float bob = (float)Math.Sin(time * 3) * 0.08f;
                    Marker(0, p + new Vector3(0, 0, (inVeh ? 2.2f : 1.25f) + bob), new Vector3(0, 0, time * 120f % 360f), new Vector3(0.35f, 0.35f, 0.3f), U.WithAlpha(kc, 230), false, false);
                    Light(p + new Vector3(0, 0, 1.4f), kc, 2.2f, cfg.AuraIntensity);
                }
            }
        }

        // ================================================================ Live Test (simulated viewers)
        bool simOn;
        long simNext;
        readonly List<KeyValuePair<long, LiveEvent>> simQueue = new List<KeyValuePair<long, LiveEvent>>();

        void ToggleLiveTest()
        {
            if (!cfg.LiveTestEnabled) return;
            simOn = !simOn;
            if (simOn)
            {
                if (!started) StartSession();
                simNext = U.Now + 300;
                Status("LIVE TEST ● ON");
            }
            else
            {
                simQueue.Clear();
                Status("LIVE TEST ■ OFF");
            }
        }

        LiveEvent SimBase(string name)
        {
            LiveEvent e = new LiveEvent();
            uint h = U.Joaat(name);
            e.UserKey = "sim:" + name;
            e.Nick = name;
            e.Sim = true;
            e.SimColor = Rainbow((h % 360) / 360f, 255);
            e.Level = (int)(h % 48) + 1;
            if (cfg.LiveTestAvatars.Count > 0) e.Avatars.Add(cfg.LiveTestAvatars[(int)(h % (uint)cfg.LiveTestAvatars.Count)]);
            return e;
        }

        void UpdateLiveTest()
        {
            long now = U.Now;
            for (int i = 0; i < simQueue.Count; i++)
            {
                if (simQueue[i].Key > now) continue;
                LiveEvent q = simQueue[i].Value;
                simQueue.RemoveAt(i--);
                HandleEvent(q);
            }
            if (!simOn || paused || now < simNext) return;
            simNext = now + U.Rng.Next(cfg.LiveTestMinMs, cfg.LiveTestMaxMs + 1);
            if (cfg.LiveTestNames.Count == 0) return;
            string name = U.Pick(cfg.LiveTestNames);
            int total = cfg.LtGift + cfg.LtLike + cfg.LtComment + cfg.LtFollow + cfg.LtShare + cfg.LtJoin;
            if (total <= 0) return;
            int r = U.Rng.Next(total);
            LiveEvent e = SimBase(name);
            if ((r -= cfg.LtGift) < 0)
            {
                List<Interaction> gifts = new List<Interaction>();
                foreach (Interaction it in cfg.Interactions)
                    if (it.Enabled && it.Trigger.Equals("Gift", StringComparison.OrdinalIgnoreCase) && (it.GiftName.Length > 0 || it.GiftId.Length > 0)) gifts.Add(it);
                Interaction g = U.Pick(gifts);
                e.Type = "gift";
                e.GiftName = g != null ? g.GiftName : "Rose";
                e.GiftId = g != null ? g.GiftId : "";
                e.Diamonds = g != null && g.GiftCoins > 0 ? g.GiftCoins : 1;
                if (U.Rng.Next(100) < cfg.LiveTestComboChance)
                {
                    int n = U.Rng.Next(2, cfg.LiveTestMaxCombo + 1);
                    for (int k = 1; k <= n; k++)
                    {
                        LiveEvent c = SimBase(name);
                        c.Type = "gift"; c.GiftName = e.GiftName; c.GiftId = e.GiftId; c.Diamonds = e.Diamonds;
                        c.GiftType = 1; c.RepeatCount = k; c.RepeatEnd = k == n;
                        simQueue.Add(new KeyValuePair<long, LiveEvent>(now + k * 180, c));
                    }
                    return;
                }
                e.GiftType = 0;
                e.RepeatCount = 1;
            }
            else if ((r -= cfg.LtLike) < 0) { e.Type = "like"; e.Likes = U.Rng.Next(5, 45); }
            else if ((r -= cfg.LtComment) < 0)
            {
                e.Type = "chat";
                List<string> words = new List<string>(cfg.LiveTestComments);
                foreach (Interaction it in cfg.Interactions)
                    if (it.Enabled && it.Trigger.Equals("Comment", StringComparison.OrdinalIgnoreCase) && it.CommentText.Length > 0) words.Add(it.CommentText);
                e.Comment = words.Count > 0 ? U.Pick(words) : "GG";
            }
            else if ((r -= cfg.LtFollow) < 0) e.Type = "follow";
            else if ((r -= cfg.LtShare) < 0) e.Type = "share";
            else e.Type = "join";
            HandleEvent(e);
        }

        // ================================================================ HUD designs over the ORIGINAL panels
        //  [Hud] Design = None | Arena | Broadcast | Podium | Cards | Esports | Minimal | Classic
        //  [Hud] DesignColor = GOLD | NEON | FIRE | ICE | CLASSIC  (medal colours)
        //  Restyles the supporters panel (Top3) and the right lists (notifications / kill feed).
        string design = "None", designColor = "GOLD";
        static readonly List<string> DesignCycle = new List<string> { "None", "Arena", "Broadcast", "Podium", "Cards", "Esports", "Minimal", "Classic", "Duo" };
        // Duo: navy panels with soft round corners, blue (you / allies / wins) against red (enemies / losses)
        static readonly Color DuoBlue = Color.FromArgb(255, 47, 125, 255), DuoRed = Color.FromArgb(255, 255, 51, 85), DuoCyan = Color.FromArgb(255, 111, 211, 255);
        bool IsDuo { get { return Dz == "duo"; } }
        Color DBg(int a) { return IsDuo ? Color.FromArgb(a, 11, 18, 40) : Color.FromArgb(a, 14, 16, 24); }
        static readonly List<string> DesignColors = new List<string> { "GOLD", "NEON", "FIRE", "ICE", "CLASSIC" };

        string Dz { get { return (design ?? "None").ToLowerInvariant(); } }

        // one look = a design (Arena, Broadcast...) or a style with no design (Cyber, Royal...)
        string CurrentLook { get { return DesignOn ? design : style; } }

        void SetLook(string look)
        {
            if (string.IsNullOrEmpty(look)) return;
            foreach (string d in DesignCycle)
            {
                if (d == "None" || !string.Equals(d, look, StringComparison.OrdinalIgnoreCase)) continue;
                design = d;
                return;
            }
            design = "None";
            style = look;
        }
        bool DesignOn { get { string d = Dz; return d.Length > 0 && d != "none"; } }

        Color[] MedalsD
        {
            get
            {
                if (IsDuo) return new Color[] { DuoBlue, DuoRed, DuoCyan };
                switch ((designColor ?? "GOLD").ToUpperInvariant())
                {
                    case "NEON": return new Color[] { Color.FromArgb(255, 215, 60, 255), Color.FromArgb(255, 0, 215, 255), Color.FromArgb(255, 40, 225, 120) };
                    case "FIRE": return new Color[] { Color.FromArgb(255, 255, 140, 0), Color.FromArgb(255, 240, 95, 30), Color.FromArgb(255, 215, 40, 40) };
                    case "ICE": return new Color[] { Color.FromArgb(255, 150, 215, 255), Color.FromArgb(255, 175, 228, 242), Color.FromArgb(255, 125, 165, 225) };
                    default: return new Color[] { Color.FromArgb(255, 245, 190, 40), Color.FromArgb(255, 192, 198, 206), Color.FromArgb(255, 205, 127, 50) };
                }
            }
        }

        Color DAcc
        {
            get
            {
                if (IsDuo) return DuoBlue;
                switch ((designColor ?? "GOLD").ToUpperInvariant())
                {
                    case "NEON": return Color.FromArgb(255, 0, 229, 255);
                    case "FIRE": return Color.FromArgb(255, 255, 106, 0);
                    case "ICE": return Color.FromArgb(255, 127, 216, 255);
                    case "CLASSIC": return cAcc;
                    default: return Color.FromArgb(255, 245, 179, 1);
                }
            }
        }

        static readonly Color Dark = Color.FromArgb(225, 12, 14, 20);
        static readonly Color Ink = Color.FromArgb(255, 28, 24, 18);

        // coins under the name: only the number (DesignCoinsWord adds a word when set)
        List<string> CoinParts(Supporter s)
        {
            List<string> l = new List<string> { U.Coins(s.Coins) };
            if (!string.IsNullOrEmpty(cfg.DesignCoinsWord)) l.Add(cfg.DesignCoinsWord);
            return l;
        }

        void AvRing(Supporter s, float cx, float cy, float d, Color ring, bool glow, bool hex)
        {
            if (glow) Gfx.Shape("glow", cx - d * 0.95f, cy - d * 0.95f, d * 1.9f, d * 1.9f, U.WithAlpha(ring, 150));
            if (hex)
            {
                Gfx.Shape("hex", cx - d * 0.66f, cy - d * 0.66f, d * 1.32f, d * 1.32f, ring);
                Gfx.Shape("hex", cx - d * 0.57f, cy - d * 0.57f, d * 1.14f, d * 1.14f, Color.FromArgb(255, 20, 22, 30));
                Gfx.Image(Avatar(s), cx - d * 0.45f, cy - d * 0.45f, d * 0.9f, d * 0.9f, 255);
            }
            else
            {
                Gfx.Shape("circle", cx - d * 0.57f, cy - d * 0.57f, d * 1.14f, d * 1.14f, ring);
                Gfx.Image(Avatar(s), cx - d / 2, cy - d / 2, d, d, 255);
            }
        }

        void NameTag(string name, float cx, float y, Color bg, Color fg, bool slanted)
        {
            float nw = Gfx.TextW(name, SMALL) + 14;
            if (slanted)
            {
                Gfx.Rect(cx - nw / 2, y, nw, 16, bg);
                Gfx.Shape("slant_l", cx - nw / 2 - 7, y, 7, 16, bg);
                Gfx.Shape("slant_r", cx + nw / 2, y, 7, 16, bg);
            }
            else Gfx.RRect(cx - nw / 2, y, nw, 16, 8, bg);
            Gfx.Text(name, cx, y + 1, SMALL, fg, Alignment.Center);
        }

        // ---------------------------------------------------------------- supporters panel
        SizeF DesignTop(bool draw)
        {
            string dz = Dz;
            List<string> rows = TopRows();
            bool showTop = rows.Contains("top3");
            List<Supporter> top = TopList(dz == "classic" ? cfg.TopCount : Math.Min(3, cfg.TopCount));
            int n = Math.Max(1, top.Count);
            float w, h;
            switch (dz)
            {
                case "arena": case "duo": w = 300; h = 120; break;
                case "esports": w = 300; h = 140; break;
                case "broadcast": w = Math.Max(1, Math.Min(3, n)) * 147; h = 58; break;
                case "podium": w = 272; h = 152; break;
                case "cards": w = 234; h = 116; break;
                case "minimal": w = 36 + Math.Min(3, n) * 104; h = 40; break;
                default: w = 240; h = 30 + n * 28 + 4; break;
            }
            if (!showTop) { w = 236; h = 0; }
            float extra = (rows.Count - (showTop ? 1 : 0)) * 20;
            float total = h + (extra > 0 ? extra + 4 : 0);
            if (total <= 0) return SizeF.Empty;
            if (!draw) return new SizeF(w, total);

            Color acc = DAcc;
            Color[] md = MedalsD;
            int[] order = { 1, 0, 2 };
            if (showTop)
            {
                if (top.Count == 0 && dz != "classic") Gfx.Text("—", w / 2, h / 2 - 8, TXT, TextCol(160), Alignment.Center);
                switch (dz)
                {
                    case "duo":
                    case "arena":
                    case "esports":
                        {
                            float oy = 0;
                            bool es = dz == "esports";
                            if (es)
                            {
                                Gfx.PartsCentered(new List<string> { "//", cfg.Top3Title, "//" }, w / 2, 0, TXT, acc);
                                oy = 20;
                            }
                            for (int c = 0; c < 3; c++)
                            {
                                int ix = order[c];
                                if (ix >= top.Count) continue;
                                Supporter s = top[ix];
                                bool big = ix == 0;
                                float cx = 50 + c * 100, d = big ? 56 : 44, cy = oy + (big ? 50 : 56);
                                if (big && !es) Gfx.Shape("crown", cx - 13, cy - d / 2 - 24, 26, 19, md[0]);
                                AvRing(s, cx, cy, d, md[ix], big, es);
                                if (!es)
                                {
                                    Gfx.Shape("circle", cx - 8, cy + d / 2 - 10, 16, 16, md[ix]);
                                    Gfx.TextPic((ix + 1).ToString(U.IC), cx, cy + d / 2 - 10, SMALL * 0.85f, Ink, Alignment.Center);
                                }
                                float py = oy + 84;
                                NameTag((es ? "#" + (ix + 1) + " " : "") + U.Trunc(s.Nick, 12), cx, py, es ? Color.FromArgb(235, 10, 12, 18) : Color.FromArgb(220, 8, 10, 14), Color.White, es);
                                if (cfg.ShowCoins) Gfx.PartsCentered(CoinParts(s), cx, py + 18, SMALL, md[ix]);
                            }
                            break;
                        }
                    case "broadcast":
                        {
                            float tw = Gfx.TextW(cfg.Top3Title, SMALL) + 26;
                            Gfx.Shape("slant_l", w / 2 - tw / 2 - 8, 0, 8, 17, acc);
                            Gfx.Rect(w / 2 - tw / 2, 0, tw, 17, acc);
                            Gfx.Shape("slant_r", w / 2 + tw / 2, 0, 8, 17, acc);
                            Gfx.Text(cfg.Top3Title, w / 2, 0, SMALL, Ink, Alignment.Center);
                            for (int i = 0; i < top.Count && i < 3; i++)
                            {
                                Supporter s = top[i];
                                float x = i * 147, y = 21;
                                Gfx.Rect(x, y, 138, 34, Dark);
                                Gfx.Shape("slant_r", x + 138, y, 8, 34, Dark);
                                Gfx.Rect(x, y + 34, 138, 2, md[i]);
                                Gfx.Rect(x, y, 22, 34, md[i]);
                                Gfx.Text((i + 1).ToString(U.IC), x + 11, y + 4, 0.5f, Ink, Alignment.Center);
                                Gfx.Image(Avatar(s), x + 26, y + 4, 26, 26, 255);
                                Gfx.Text(U.Trunc(s.Nick, 11), x + 56, y + 2, SMALL, Color.White, Alignment.Left);
                                if (cfg.ShowCoins) Gfx.Parts(CoinParts(s), x + 56, y + 17, SMALL * 0.95f, md[i]);
                            }
                            break;
                        }
                    case "podium":
                        {
                            float[] ph = { 44, 32, 24 };
                            const float colW = 86, baseY = 150;
                            for (int c = 0; c < 3; c++)
                            {
                                int ix = order[c];
                                if (ix >= top.Count) continue;
                                Supporter s = top[ix];
                                float x0 = 6 + c * (colW + 3), cx = x0 + colW / 2, hgt = ph[ix];
                                Gfx.Rect(x0, baseY - hgt, colW, hgt, U.WithAlpha(md[ix], 235));
                                Gfx.Rect(x0, baseY - hgt, colW, 3, Color.FromArgb(130, 255, 255, 255));
                                Gfx.Text((ix + 1).ToString(U.IC), cx, baseY - hgt + 1, 0.5f, Ink, Alignment.Center);
                                if (cfg.ShowCoins && hgt >= 30) Gfx.PartsCentered(CoinParts(s), cx, baseY - hgt + 20, SMALL * 0.8f, Ink);
                                float d = ix == 0 ? 46 : 38, acy = baseY - hgt - 26 - d / 2;
                                if (ix == 0) Gfx.Shape("crown", cx - 11, acy - d / 2 - 18, 22, 16, md[0]);
                                AvRing(s, cx, acy, d, md[ix], ix == 0, false);
                                NameTag(U.Trunc(s.Nick, 10), cx, baseY - hgt - 21, Color.FromArgb(110, 255, 255, 255), Color.White, false);
                            }
                            break;
                        }
                    case "cards":
                        {
                            float[] xs = { 0, 77, 166 };
                            for (int c = 0; c < 3; c++)
                            {
                                int ix = order[c];
                                if (ix >= top.Count) continue;
                                Supporter s = top[ix];
                                bool big = ix == 0;
                                float cw = big ? 82 : 68, bh = big ? 100 : 86, x = xs[c], y = big ? 0 : 10;
                                for (int k = 0; k < 6; k++)
                                    Gfx.Rect(x, y + k * bh / 6, cw, bh / 6 + 0.6f, U.Mix(Color.White, md[ix], 0.3f + k * 0.13f));
                                Gfx.Shape("tri_down", x, y + bh - 0.5f, cw, 14, md[ix]);
                                Gfx.Text(U.Coins(s.Coins), x + 5, y + 2, big ? 0.5f : 0.44f, Ink, Alignment.Left);
                                Gfx.Text("#" + (ix + 1), x + cw - 5, y + 4, SMALL, Ink, Alignment.Right);
                                if (!string.IsNullOrEmpty(cfg.DesignCoinsWord)) Gfx.Text(cfg.DesignCoinsWord, x + 6, y + (big ? 22 : 20), SMALL * 0.75f, Ink, Alignment.Left);
                                AvRing(s, x + cw / 2, y + bh * 0.55f, big ? 32 : 26, Color.White, false, false);
                                Gfx.Text(U.Trunc(s.Nick, 9), x + cw / 2, y + bh - 17, SMALL, Ink, Alignment.Center);
                            }
                            break;
                        }
                    case "minimal":
                        {
                            Gfx.RRect(0, 0, w, 38, 19, Color.FromArgb(75, 255, 255, 255));
                            Gfx.Shape("crown", 10, 12, 18, 14, md[0]);
                            for (int i = 0; i < top.Count && i < 3; i++)
                            {
                                Supporter s = top[i];
                                float x = 36 + i * 104;
                                AvRing(s, x + 13, 19, 24, md[i], false, false);
                                Gfx.Text(U.Trunc(s.Nick, 10), x + 30, 4, SMALL * 0.95f, Color.White, Alignment.Left);
                                if (cfg.ShowCoins) Gfx.Parts(CoinParts(s), x + 30, 19, SMALL * 0.85f, md[i]);
                            }
                            break;
                        }
                    default: // classic
                        {
                            Gfx.RRect(0, 0, w, h, 10, Color.FromArgb(Math.Max(170, cfg.Opacity), 14, 16, 24));
                            Gfx.Shape("crown", 10, 7, 16, 12, acc);
                            Gfx.Text(cfg.Top3Title, 32, 3, TXT, acc, Alignment.Left);
                            if (top.Count == 0) Gfx.Text("—", w / 2, 32, TXT, TextCol(160), Alignment.Center);
                            for (int i = 0; i < top.Count; i++)
                            {
                                Supporter s = top[i];
                                float y = 28 + i * 28;
                                Color mc = i < 3 ? md[i] : Color.FromArgb(255, 120, 126, 136);
                                Gfx.Shape("circle", 10, y + 5, 16, 16, mc);
                                Gfx.TextPic((i + 1).ToString(U.IC), 18, y + 5, SMALL * 0.85f, Ink, Alignment.Center);
                                Gfx.Image(Avatar(s), 32, y + 2, 22, 22, 255);
                                Gfx.Text(U.Trunc(s.Nick, 14), 60, y + 4, TXT, Color.White, Alignment.Left);
                                if (cfg.ShowCoins)
                                {
                                    List<string> cp = CoinParts(s);
                                    Gfx.Parts(cp, w - 10 - Gfx.PartsW(cp, SMALL), y + 6, SMALL, mc);
                                }
                            }
                            break;
                        }
                }
            }
            float ry = h + (showTop ? 4 : 0);
            foreach (string r in rows)
            {
                if (r == "top3") continue;
                Gfx.RRect(w / 2 - 112, ry, 224, 18, 9, Color.FromArgb(160, 10, 12, 18));
                if (r == "counters") CountersRow(w, ry - 2);
                else StatusRow(w, ry - 2);
                ry += 20;
            }
            return new SizeF(w, total);
        }

        // ---------------------------------------------------------------- right lists (notifications / kill feed) rows
        float DesignRowPad(float rowH)
        {
            string dz = Dz;
            return dz == "broadcast" || dz == "cards" || dz == "esports" ? rowH - 2 : 0;
        }

        // draws a row background in the current design, returns the left padding for the content
        float DesignRowBg(float x, float y, float w, float h, float a, int index, bool newest)
        {
            Color acc = DAcc;
            int A = (int)(255 * a);
            switch (Dz)
            {
                case "duo":
                    {
                        // pill row, blue accent (red for the newest one)
                        float r = Math.Min(12, h / 2);
                        Gfx.RRect(x, y, w, h, r, DBg((int)(225 * a)));
                        Color side = newest ? DuoRed : DuoBlue;
                        Gfx.RRect(x + 3, y + 4, 4, h - 8, 2, U.WithAlpha(side, A));
                        if (newest) Gfx.Rect(x + r, y + h - 2, w - r * 2, 2, U.WithAlpha(DuoRed, (int)(170 * a)));
                        return 4;
                    }
                case "arena":
                    Gfx.RRect(x, y, w, h, 8, Color.FromArgb((int)(215 * a), 14, 16, 24));
                    if (newest)
                    {
                        for (int k = 0; k < 8; k++) Gfx.Rect(x + 3 + k * w * 0.07f, y, w * 0.07f + 0.5f, h, U.WithAlpha(acc, (int)(90 * a * (1 - k / 8f))));
                        Gfx.Rect(x, y + 3, 3, h - 6, U.WithAlpha(acc, A));
                    }
                    return 0;
                case "broadcast":
                    Gfx.Rect(x, y, w, h, Color.FromArgb((int)(230 * a), 0, 0, 0));
                    Gfx.Rect(x, y, h - 4, h, Color.FromArgb(A, 255, 255, 255));
                    Gfx.Text(index.ToString(U.IC), x + (h - 4) / 2, y + (h - Gfx.LineH(TXT)) / 2, TXT, U.WithAlpha(Ink, A), Alignment.Center);
                    if (newest) Gfx.Rect(x + h - 4, y + h - 2, w - h + 4, 2, U.WithAlpha(acc, A));
                    return h - 2;
                case "podium":
                    Gfx.RRect(x, y, w, h, 10, Color.FromArgb((int)(80 * a), 255, 255, 255));
                    if (newest) Gfx.Rect(x + 10, y + h - 2, w - 20, 2, U.WithAlpha(acc, A));
                    return 0;
                case "cards":
                    Gfx.RRect(x, y, w, h, 6, Color.FromArgb((int)(215 * a), 14, 16, 24));
                    Gfx.Rect(x + 4, y + 4, h - 8, h - 8, U.WithAlpha(newest ? acc : Color.FromArgb(255, 245, 190, 40), A));
                    Gfx.Text(index.ToString(U.IC), x + h / 2, y + (h - Gfx.LineH(SMALL)) / 2, SMALL, U.WithAlpha(Ink, A), Alignment.Center);
                    return h - 2;
                case "esports":
                    Gfx.Rect(x, y, w, h, Color.FromArgb((int)(225 * a), 10, 12, 20));
                    Gfx.Shape("slant_r", x + w, y, 8, h, Color.FromArgb((int)(225 * a), 10, 12, 20));
                    Gfx.Rect(x, y, 3, h, U.WithAlpha(acc, A));
                    Gfx.Text("#" + index, x + 7, y + (h - Gfx.LineH(SMALL)) / 2, SMALL, U.WithAlpha(acc, A), Alignment.Left);
                    return h - 2;
                case "minimal":
                    Gfx.RRect(x, y, w, h, h / 2, Color.FromArgb((int)(80 * a), 255, 255, 255));
                    if (newest) Gfx.Rect(x + h / 2, y + h - 2, w - h, 2, U.WithAlpha(acc, A));
                    return 4;
                default:
                    PanelBg(x, y, w, h, a);
                    return 0;
            }
        }

        // ---------------------------------------------------------------- score panel (win · timer · loss) in the current design
        SizeF DesignScore(bool draw)
        {
            string dz = Dz;
            bool title = cfg.TitleEnabled && cfg.Title.Length > 0;
            bool timer = cfg.ShowTimer && cfg.ChallengeEnabled;
            bool score = cfg.ShowScore;
            bool stk = cfg.ShowStreak && Math.Abs(streak) >= 2;
            if (!title && !timer && !score && !stk) return SizeF.Empty;
            float w = dz == "minimal" ? 220 : (dz == "broadcast" ? 320 : 290);
            float th = title ? 20 : 0, body = dz == "minimal" ? 44 : 40, sh = stk ? 18 : 0;
            float h = th + body + sh + (dz == "minimal" ? 0 : 6);
            if (!draw) return new SizeF(w, h);

            Color acc = DAcc, win = cWin, loss = cLoss;
            string ws = wins.ToString(U.IC), ls = losses.ToString(U.IC);
            float y = 0;
            switch (dz)
            {
                case "duo":
                case "arena":
                    Gfx.RRect(0, 0, w, h, 14, DBg(242));
                    if (IsDuo) DuoLine(14, 0, w - 28);
                    if (title) { Gfx.Text(cfg.Title, w / 2, 2, SMALL, acc, Alignment.Center); y = th; }
                    if (score)
                    {
                        Gfx.RRect(10, y + 4, 62, 32, 10, U.WithAlpha(win, 230));
                        Gfx.Text(ws, 41, y + 5, 0.5f, Color.White, Alignment.Center);
                        Gfx.RRect(w - 72, y + 4, 62, 32, 10, U.WithAlpha(loss, 230));
                        Gfx.Text(ls, w - 41, y + 5, 0.5f, Color.White, Alignment.Center);
                    }
                    if (timer) Gfx.Text(TimerText(), w / 2, y + 4, 0.56f, TimerColor(), Alignment.Center);
                    break;
                case "broadcast":
                    {
                        if (title)
                        {
                            float tw = Gfx.TextW(cfg.Title, SMALL) + 26;
                            Gfx.Shape("slant_l", w / 2 - tw / 2 - 8, 0, 8, 17, acc);
                            Gfx.Rect(w / 2 - tw / 2, 0, tw, 17, acc);
                            Gfx.Shape("slant_r", w / 2 + tw / 2, 0, 8, 17, acc);
                            Gfx.Text(cfg.Title, w / 2, 0, SMALL, Ink, Alignment.Center);
                            y = th;
                        }
                        Gfx.Rect(8, y + 2, w - 16, 36, Dark);
                        Gfx.Shape("slant_l", 0, y + 2, 8, 36, score ? win : Dark);
                        Gfx.Shape("slant_r", w - 8, y + 2, 8, 36, score ? loss : Dark);
                        if (score)
                        {
                            Gfx.Rect(8, y + 2, 78, 36, win);
                            Gfx.Text(cfg.Tx("WinShort", "W"), 14, y + 10, SMALL, Color.White, Alignment.Left);
                            Gfx.Text(ws, 80, y + 4, 0.52f, Color.White, Alignment.Right);
                            Gfx.Rect(w - 86, y + 2, 78, 36, loss);
                            Gfx.Text(ls, w - 80, y + 4, 0.52f, Color.White, Alignment.Left);
                            Gfx.Text(cfg.Tx("LossShort", "L"), w - 14, y + 10, SMALL, Color.White, Alignment.Right);
                        }
                        Gfx.Rect(86, y + 36, w - 172, 2, acc);
                        if (timer) Gfx.Text(TimerText(), w / 2, y + 5, 0.54f, TimerColor(), Alignment.Center);
                        break;
                    }
                case "podium":
                    Gfx.RRect(0, 0, w, h, Math.Min(22, h / 2), Color.FromArgb(225, 34, 38, 52));
                    Gfx.Rect(22, 0, w - 44, 2, Color.FromArgb(150, 255, 255, 255));
                    if (title) { Gfx.Text(cfg.Title, w / 2, 3, SMALL, Color.White, Alignment.Center); y = th; }
                    if (score)
                    {
                        Gfx.RRect(10, y + 5, 64, 30, 15, U.WithAlpha(win, 235));
                        Gfx.Text(ws, 42, y + 5, 0.5f, Color.White, Alignment.Center);
                        Gfx.RRect(w - 74, y + 5, 64, 30, 15, U.WithAlpha(loss, 235));
                        Gfx.Text(ls, w - 42, y + 5, 0.5f, Color.White, Alignment.Center);
                    }
                    if (timer) Gfx.Text(TimerText(), w / 2, y + 4, 0.56f, TimerColor(), Alignment.Center);
                    break;
                case "cards":
                    {
                        if (title) { Gfx.Text(cfg.Title, w / 2, 1, SMALL, acc, Alignment.Center); y = th; }
                        float cw = 88;
                        Color[] cols = { win, Color.White, loss };
                        for (int i = 0; i < 3; i++)
                        {
                            if ((i != 1 && !score) || (i == 1 && !timer)) continue;
                            float x = 4 + i * (cw + 9);
                            for (int k = 0; k < 4; k++) Gfx.Rect(x, y + 2 + k * 9, cw, 9.6f, U.Mix(Color.White, cols[i], 0.25f + k * 0.2f));
                            Gfx.Shape("tri_down", x, y + 37.5f, cw, 8, i == 1 ? Color.FromArgb(255, 200, 204, 210) : cols[i]);
                        }
                        if (score)
                        {
                            Gfx.Text(ws, 4 + cw / 2, y + 4, 0.52f, Ink, Alignment.Center);
                            Gfx.Text(ls, 4 + 2 * (cw + 9) + cw / 2, y + 4, 0.52f, Ink, Alignment.Center);
                        }
                        if (timer) Gfx.Text(TimerText(), 4 + (cw + 9) + cw / 2, y + 5, 0.5f, Ink, Alignment.Center);
                        break;
                    }
                case "esports":
                    if (title) { Gfx.PartsCentered(new List<string> { "//", cfg.Title, "//" }, w / 2, 0, SMALL, acc); y = th; }
                    Gfx.Rect(10, y + 2, w - 20, 36, Color.FromArgb(230, 10, 12, 20));
                    Gfx.Shape("slant_l", 2, y + 2, 8, 36, Color.FromArgb(230, 10, 12, 20));
                    Gfx.Shape("slant_r", w - 10, y + 2, 8, 36, Color.FromArgb(230, 10, 12, 20));
                    Gfx.Rect(10, y + 2, w - 20, 2, acc);
                    if (score)
                    {
                        Gfx.Text("W " + ws, 20, y + 8, TXT * 1.15f, win, Alignment.Left);
                        Gfx.Text(ls + " L", w - 20, y + 8, TXT * 1.15f, loss, Alignment.Right);
                    }
                    if (timer) Gfx.Text(TimerText(), w / 2, y + 5, 0.54f, TimerColor(), Alignment.Center);
                    break;
                case "minimal":
                    Gfx.RRect(10, 0, w - 20, h, Math.Min(20, h / 2), Color.FromArgb(200, 10, 12, 18));
                    if (title) { Gfx.Text(cfg.Title, w / 2, 0, SMALL, Color.FromArgb(220, 255, 255, 255), Alignment.Center); y = th; }
                    if (timer) Gfx.Text(TimerText(), w / 2, y - 2, 0.7f, TimerColor(), Alignment.Center);
                    if (score)
                    {
                        Gfx.Text(ws, w / 2 - 14, y + 28, TXT, win, Alignment.Right);
                        Gfx.Rect(w / 2 - 6, y + 34, 12, 2, Color.FromArgb(180, 255, 255, 255));
                        Gfx.Text(ls, w / 2 + 14, y + 28, TXT, loss, Alignment.Left);
                    }
                    break;
                default: // classic
                    Gfx.RRect(0, 0, w, h, 10, Color.FromArgb(242, 14, 16, 24));
                    if (title) { Gfx.Shape("crown", 10, 4, 14, 10, acc); Gfx.Text(cfg.Title, 30, 1, SMALL, acc, Alignment.Left); y = th; }
                    if (score)
                    {
                        Gfx.Shape("circle", 12, y + 6, 28, 28, win);
                        Gfx.TextPic(ws, 26, y + 8, 0.42f, Color.White, Alignment.Center);
                        Gfx.Shape("circle", w - 40, y + 6, 28, 28, loss);
                        Gfx.TextPic(ls, w - 26, y + 8, 0.42f, Color.White, Alignment.Center);
                    }
                    if (timer) Gfx.Text(TimerText(), w / 2, y + 4, 0.56f, TimerColor(), Alignment.Center);
                    break;
            }
            if (stk) Gfx.PartsCentered(StreakParts(), w / 2, h - sh - (dz == "minimal" ? 0 : 3), SMALL, streak > 0 ? win : loss);
            return new SizeF(w, h);
        }

        // ---------------------------------------------------------------- health bar in the current design
        SizeF DesignHealth(bool draw)
        {
            string dz = Dz;
            float w = Math.Max(80, L.HealthWidth);
            bool armor = cfg.ShowArmor;
            float h = dz == "minimal" ? 30 : 44 + (armor ? 4 : 0);
            if (!draw) return new SizeF(w, h);
            Ped pl = Game.Player.Character;
            float frac = 0, af = 0;
            int hpNow = 0, hpMax = 0;
            if (pl.Exists())
            {
                hpMax = PlayerHpMax();
                hpNow = PlayerHpNow();
                frac = U.Clamp(hpNow / (float)hpMax, 0, 1);
                af = U.Clamp(pl.Armor / 100f, 0, 1);
            }
            Color hc = frac < 0.25f ? cLoss : (IsDuo ? DuoBlue : cfg.HealthColor);
            if (frac < 0.25f && (U.Now / 300) % 2 == 0) hc = U.Mix(hc, Color.White, 0.35f);
            Color acc = DAcc, back = Color.FromArgb(150, 0, 0, 0), armorC = Color.FromArgb(255, 90, 170, 255);
            string pct = (frac * 100).ToString("0.0", U.IC) + "%";
            List<string> label = new List<string> { cfg.HealthLabel, pct };
            string pts = hpNow + " / " + hpMax;
            float bx = 10, bw = w - 20, by = 22;
            switch (dz)
            {
                case "duo":
                case "arena":
                    Gfx.RRect(0, 0, w, h, 14, DBg(Math.Max(190, cfg.Opacity)));
                    if (IsDuo) DuoLine(14, 0, w - 28);
                    Gfx.Parts(label, 12, 3, SMALL, Color.White);
                    if (cfg.ShowHealthPoints) Gfx.Text(pts, w - 12, 3, SMALL, Color.FromArgb(200, 255, 255, 255), Alignment.Right);
                    Gfx.RRect(bx, by, bw, 12, 6, back);
                    if (frac > 0) Gfx.RRect(bx, by, Math.Max(12, bw * frac), 12, 6, hc);
                    if (armor) Gfx.RRect(bx, by + 15, Math.Max(6, bw * af), 5, 2.5f, armorC);
                    break;
                case "broadcast":
                    Gfx.Rect(0, 0, w, h, Dark);
                    Gfx.Shape("slant_r", w, 0, 9, h, Dark);
                    Gfx.Rect(0, 0, 38, h, acc);
                    Gfx.Text("HP", 19, h / 2 - 10, TXT, Ink, Alignment.Center);
                    Gfx.Parts(label, 46, 3, SMALL, Color.White);
                    if (cfg.ShowHealthPoints) Gfx.Text(pts, w - 6, 3, SMALL, Color.FromArgb(200, 255, 255, 255), Alignment.Right);
                    Gfx.Bar(46, by, w - 52, 12, frac, hc, back);
                    if (armor) Gfx.Bar(46, by + 15, w - 52, 4, af, armorC, back);
                    break;
                case "podium":
                    Gfx.RRect(0, 0, w, h, 14, Color.FromArgb(80, 255, 255, 255));
                    Gfx.Parts(label, 14, 3, SMALL, Color.White);
                    if (cfg.ShowHealthPoints) Gfx.Text(pts, w - 14, 3, SMALL, Color.White, Alignment.Right);
                    Gfx.RRect(bx, by, bw, 13, 6.5f, Color.FromArgb(90, 0, 0, 0));
                    if (frac > 0) Gfx.RRect(bx, by, Math.Max(13, bw * frac), 13, 6.5f, hc);
                    if (armor) Gfx.RRect(bx, by + 16, Math.Max(6, bw * af), 5, 2.5f, armorC);
                    break;
                case "cards":
                    for (int k = 0; k < 4; k++) Gfx.Rect(0, k * h / 4, w, h / 4 + 0.6f, U.Mix(Color.White, Color.FromArgb(255, 200, 204, 212), k / 3f));
                    Gfx.Rect(0, 0, 4, h, hc);
                    Gfx.Parts(label, 12, 3, SMALL, Ink);
                    if (cfg.ShowHealthPoints) Gfx.Text(pts, w - 10, 3, SMALL, Ink, Alignment.Right);
                    Gfx.Bar(bx, by, bw, 12, frac, hc, Color.FromArgb(120, 0, 0, 0));
                    if (armor) Gfx.Bar(bx, by + 15, bw, 4, af, armorC, Color.FromArgb(120, 0, 0, 0));
                    break;
                case "esports":
                    {
                        Gfx.Rect(8, 0, w - 16, h, Color.FromArgb(230, 10, 12, 20));
                        Gfx.Shape("slant_l", 0, 0, 8, h, Color.FromArgb(230, 10, 12, 20));
                        Gfx.Shape("slant_r", w - 8, 0, 8, h, Color.FromArgb(230, 10, 12, 20));
                        Gfx.Rect(8, 0, w - 16, 2, acc);
                        Gfx.Parts(label, 14, 3, SMALL, Color.White);
                        if (cfg.ShowHealthPoints) Gfx.Text(pts, w - 14, 3, SMALL, acc, Alignment.Right);
                        const int segs = 12;
                        float sw = (bw - (segs - 1) * 2) / segs;
                        for (int i = 0; i < segs; i++)
                        {
                            float f = U.Clamp(frac * segs - i, 0, 1);
                            Gfx.Rect(bx + i * (sw + 2), by, sw, 12, back);
                            if (f > 0) Gfx.Rect(bx + i * (sw + 2), by, sw * f, 12, hc);
                        }
                        if (armor) Gfx.Bar(bx, by + 15, bw, 4, af, armorC, back);
                        break;
                    }
                case "minimal":
                    Gfx.Parts(label, 2, 0, SMALL, Color.FromArgb(230, 255, 255, 255));
                    if (cfg.ShowHealthPoints) Gfx.Text(pts, w - 2, 0, SMALL, Color.FromArgb(230, 255, 255, 255), Alignment.Right);
                    Gfx.RRect(0, 18, w, 7, 3.5f, Color.FromArgb(110, 255, 255, 255));
                    if (frac > 0) Gfx.RRect(0, 18, Math.Max(7, w * frac), 7, 3.5f, hc);
                    if (armor && af > 0) Gfx.RRect(0, 27, Math.Max(3, w * af), 3, 1.5f, armorC);
                    break;
                default:
                    Gfx.RRect(0, 0, w, h, 10, Color.FromArgb(Math.Max(170, cfg.Opacity), 14, 16, 24));
                    Gfx.Parts(label, 10, 3, SMALL, Color.White);
                    if (cfg.ShowHealthPoints) Gfx.Text(pts, w - 10, 3, SMALL, Color.White, Alignment.Right);
                    Gfx.Bar(bx, by, bw, 12, frac, hc, back);
                    if (armor) Gfx.Bar(bx, by + 15, bw, 4, af, armorC, back);
                    break;
            }
            if (dz == "minimal") DamageFx(0, 18, w, 7, frac);
            else if (dz == "broadcast") DamageFx(46, by, w - 52, 12, frac);
            else DamageFx(bx, by, bw, dz == "podium" ? 13 : 12, frac);
            return new SizeF(w, h);
        }

        // ---------------------------------------------------------------- 9:16 (TikTok) automatic arrangement
        //  everything is stacked in the visible 405px column: top = score, supporters, hype;
        //  bottom = health, notifications, kill feed. Panels too wide are scaled down to fit.
        float FitScale(float width, float s)
        {
            float room = frameW - 20;
            return width * s > room ? room / width : s;
        }

        float stackScale = 1;

        float StackMeasure(string name, bool enabled, PanelFn fn)
        {
            PanelPos p;
            if (!enabled || !L.P.TryGetValue(name, out p) || !p.Show) return 0;
            Gfx.Origin(0, 0, L.Scale);
            SizeF sz = fn(false);
            if (sz.Width <= 0 || sz.Height <= 0) return 0;
            return sz.Height * FitScale(sz.Width, L.Scale) + cfg.VGap;
        }

        void StackTop(string name, bool enabled, PanelFn fn, ref float y)
        {
            PanelPos p;
            if (!enabled || !L.P.TryGetValue(name, out p) || !p.Show) return;
            Gfx.Origin(0, 0, L.Scale);
            SizeF sz = fn(false);
            if (sz.Width <= 0 || sz.Height <= 0) return;
            float s = FitScale(sz.Width, L.Scale) * stackScale;
            Gfx.Origin(frameX + (frameW - sz.Width * s) / 2, y, s);
            fn(true);
            panelRects[name] = new RectangleF(frameX + (frameW - sz.Width * s) / 2, y, sz.Width * s, sz.Height * s);
            y += sz.Height * s + cfg.VGap * stackScale;
        }

        void StackBottom(string name, bool enabled, PanelFn fn, ref float y)
        {
            PanelPos p;
            if (!enabled || !L.P.TryGetValue(name, out p) || !p.Show) return;
            Gfx.Origin(0, 0, L.Scale);
            SizeF sz = fn(false);
            if (sz.Width <= 0 || sz.Height <= 0) return;
            float s = FitScale(sz.Width, L.Scale) * stackScale;
            y -= sz.Height * s;
            Gfx.Origin(frameX + (frameW - sz.Width * s) / 2, y, s);
            fn(true);
            panelRects[name] = new RectangleF(frameX + (frameW - sz.Width * s) / 2, y, sz.Width * s, sz.Height * s);
            y -= cfg.VGap * stackScale;
        }

        bool VAuto { get { return vertical && cfg.VAutoArrange; } }

        static List<FeedItem> Tail(List<FeedItem> l, int n)
        {
            if (l.Count <= n) return l;
            return l.GetRange(l.Count - n, n);
        }

        // ================================================================ solid design box (score counters, end screen)
        void DBox(float x, float y, float w, float h) { DPlate(x, y, w, h, 1f); }

        // Duo top line: blue half + red half
        void DuoLine(float x, float y, float w)
        {
            if (w <= 0) return;
            Gfx.Rect(x, y, w / 2, 2, DuoBlue);
            Gfx.Rect(x + w / 2, y, w / 2, 2, DuoRed);
        }

        // plate of the current look (every panel uses it, so everything matches)
        void DPlate(float x, float y, float w, float h, float alpha)
        {
            Color acc = U.WithAlpha(DesignOn ? DAcc : cAcc, (int)(255 * alpha));
            Color bg = Color.FromArgb((int)(240 * alpha), 14, 16, 24);
            switch (DesignOn ? Dz : "")
            {
                case "duo":
                    {
                        float r = Math.Min(14, h / 2);
                        Gfx.RRect(x, y, w, h, r, DBg((int)(238 * alpha)));
                        DuoLine(x + r, y, w - r * 2);
                        break;
                    }
                case "arena":
                case "classic":
                    Gfx.RRect(x, y, w, h, Math.Min(14, h / 2), bg);
                    Gfx.Rect(x + 14, y, w - 28, 2, acc);
                    break;
                case "broadcast":
                    Gfx.Rect(x, y, w, h, bg);
                    Gfx.Shape("slant_r", x + w, y, Math.Min(10, h / 3), h, bg);
                    Gfx.Rect(x, y, 5, h, acc);
                    break;
                case "podium":
                    Gfx.RRect(x, y, w, h, Math.Min(16, h / 2), Color.FromArgb((int)(225 * alpha), 34, 38, 52));
                    Gfx.Rect(x + 16, y, w - 32, 2, Color.FromArgb((int)(150 * alpha), 255, 255, 255));
                    break;
                case "cards":
                    Gfx.RRect(x, y, w, h, Math.Min(8, h / 2), bg);
                    Gfx.Rect(x, y + h - 3, w, 3, acc);
                    break;
                case "esports":
                    Gfx.Rect(x + 8, y, w - 16, h, bg);
                    Gfx.Shape("slant_l", x, y, 8, h, bg);
                    Gfx.Shape("slant_r", x + w - 8, y, 8, h, bg);
                    Gfx.Rect(x + 8, y, w - 16, 2, acc);
                    break;
                case "minimal":
                    Gfx.RRect(x, y, w, h, Math.Min(18, h / 2), Color.FromArgb((int)(215 * alpha), 10, 12, 18));
                    break;
                default:
                    if (DesignOn) { Gfx.RRect(x, y, w, h, Math.Min(10, h / 2), bg); Gfx.Rect(x + 10, y, w - 20, 2, acc); }
                    else PanelBg(x, y, w, h, 1.3f * alpha);
                    break;
            }
        }

        // enemies / allies alive now, drawn at the bottom of the score panel
        void CountStrip(float w, float y)
        {
            DBox(0, y, w, 22);
            List<string> pe = new List<string> { cfg.Tx("EnemiesShort", "E"), AliveCount(true).ToString(U.IC) };
            List<string> pa = new List<string> { cfg.Tx("AlliesShort", "A"), AliveCount(false).ToString(U.IC) };
            float we = Gfx.PartsW(pe, SMALL), wa = Gfx.PartsW(pa, SMALL);
            float x = (w - (12 + we + 22 + 12 + wa)) / 2;
            Gfx.Shape("circle", x, y + 7, 8, 8, cLoss);
            Gfx.Parts(pe, x + 12, y + 3, SMALL, Color.White);
            x += 12 + we + 22;
            Gfx.Rect(x - 12, y + 6, 1, 10, Color.FromArgb(120, 255, 255, 255));
            Gfx.Shape("circle", x, y + 7, 8, 8, cWin);
            Gfx.Parts(pa, x + 12, y + 3, SMALL, Color.White);
        }

        SizeF ScorePanel(bool draw)
        {
            SizeF a = DesignOn ? DesignScore(false) : ScoreInner(false);
            bool counts = cfg.ShowCounters;
            if (a.Width <= 0 && !counts) return SizeF.Empty;
            float w = Math.Max(a.Width, counts ? 220 : 0);
            float h = a.Height + (counts ? (a.Height > 0 ? 25 : 22) : 0);
            if (!draw) return new SizeF(w, h);
            if (a.Width > 0)
            {
                float ox = Gfx.Ox, s = Gfx.S;
                Gfx.Origin(Gfx.Ox + (w - a.Width) / 2 * s, Gfx.Oy, s);
                if (DesignOn) DesignScore(true); else ScoreInner(true);
                Gfx.Origin(ox, Gfx.Oy, s);
            }
            if (counts) CountStrip(w, a.Height > 0 ? a.Height + 3 : 0);
            return new SizeF(w, h);
        }

        // ================================================================ end screen (win / loss) in the HUD design
        int roundEnemies, roundAllies, roundKills;

        void EndScreen()
        {
            if (phase != Phase.Ended || !cfg.EndScreenEnabled || U.Now >= endScreenUntil) return;
            float bw = Math.Min(frameW - 24, 470);
            bool mvpOn = cfg.MvpEnabled && mvp != null;
            float bh = 118 + (mvpOn ? 86 : 0);
            float t = U.Clamp((U.Now - (endScreenUntil - (long)(cfg.EndScreenSeconds * 1000))) / 300f, 0, 1);
            float s = (0.85f + 0.15f * t) * (vertical ? Math.Min(1f, L.Scale + 0.1f) : 1f);
            float x0 = frameX + (frameW - bw * s) / 2, y0 = frameY + (vertical ? 250 : 170);
            Gfx.Origin(x0, y0, s);
            Color c = lastWin ? cWin : cLoss;
            Color acc = DesignOn ? DAcc : cAcc;
            DBox(0, 0, bw, bh);
            // title band
            Gfx.Rect(0, 0, bw, 52, U.WithAlpha(c, 235));
            Gfx.Rect(0, 52, bw, 2, Color.FromArgb(120, 255, 255, 255));
            float pulse = 1f + 0.04f * (float)Math.Sin(U.Now / 180.0);
            Gfx.Text(lastWin ? cfg.Tx("WinText", "WIN") : cfg.Tx("LossText", "LOSS"), bw / 2, 3, 0.95f * pulse, Color.White, Alignment.Center);
            // round stats: enemies / allies / kills
            string[] labels = { cfg.Tx("EnemiesShort", "E"), cfg.Tx("AlliesShort", "A"), cfg.Tx("KillsShort", "K") };
            int[] vals = { roundEnemies, roundAllies, roundKills };
            Color[] cols = { cLoss, cWin, acc };
            float cw = bw / 3;
            for (int i = 0; i < 3; i++)
            {
                float cx = cw * i + cw / 2;
                Gfx.Text(vals[i].ToString(U.IC), cx, 60, 0.62f, cols[i], Alignment.Center);
                Gfx.Text(labels[i], cx, 92, SMALL, Color.FromArgb(220, 255, 255, 255), Alignment.Center);
                if (i > 0) Gfx.Rect(cw * i, 64, 1, 40, Color.FromArgb(70, 255, 255, 255));
            }
            if (mvpOn)
            {
                float y = 118;
                Gfx.Rect(14, y, bw - 28, 1, Color.FromArgb(70, 255, 255, 255));
                AvRing(mvp, 52, y + 42, 56, cfg.HypeColor, true, DesignOn && Dz == "esports");
                Gfx.Text(cfg.Tx("MvpText", "MVP"), 92, y + 10, 0.42f, cfg.HypeColor, Alignment.Left);
                Gfx.Text(U.Trunc(mvp.Nick, 22), 92, y + 32, 0.4f, Color.White, Alignment.Left);
                Gfx.Text(mvpReason, 92, y + 56, SMALL, Color.FromArgb(200, 255, 255, 255), Alignment.Left);
            }
        }

        // ================================================================ effects on the player (what a supporter just did to you)
        //  [PlayerFx]: popup above your head (supporter picture + action picture + text), a burst ring + light,
        //  and a glow while a timed effect is active (speed, god mode, jump, low gravity, freeze, drunk).
        class Popup { public Supporter Sup; public string Icon; public List<string> Parts; public Color Col; public long Start, End; }
        readonly List<Popup> popups = new List<Popup>();
        long burstStart = -99999;
        Color burstCol = Color.White;
        int speedFx;

        Color FxColor(string action)
        {
            switch (action)
            {
                case "SuperSpeed": case "BoostVehicle": return cfg.FxSpeedColor;
                case "GodMode": return cfg.FxGodColor;
                case "SuperJump": case "LowGravity": case "Skyfall": case "Launch": return cfg.FxJumpColor;
                case "Freeze": return cfg.FxFreezeColor;
                case "GiveWeapon": case "GiveAllWeapons": return cfg.FxWeaponColor;
                case "Heal": case "AddHealth": return cfg.FxHealColor;
            }
            return IsHelp(action) ? cfg.FxHealColor : cLoss;
        }

        static string Human(string code)
        {
            if (string.IsNullOrEmpty(code)) return "";
            string s = code.ToUpperInvariant().Replace("WEAPON_", "").Replace("GADGET_", "").Replace("_", " ");
            return s.Length > 1 ? s.Substring(0, 1) + s.Substring(1).ToLowerInvariant() : s;
        }

        void PlayerFx(Job j, string action)
        {
            if (!cfg.FxEnabled || IsSpawn(action)) return;
            Interaction it = j.It;
            Color col = FxColor(action);
            if (cfg.FxBurst)
            {
                burstStart = U.Now;
                burstCol = col;
                Ped pl = Game.Player.Character;
                if (pl.Exists() && (action == "GiveWeapon" || action == "GiveAllWeapons" || action == "Heal" || action == "AddHealth" || action == "GodMode"))
                    SpawnFx(action == "GiveWeapon" || action == "GiveAllWeapons" ? "Flash" : "SoftSmoke", pl.Position - new Vector3(0, 0, 0.6f));
            }
            if (!cfg.FxPopup) return;
            // timed effects get a live bar above the player instead (DrawFxBars)
            if (cfg.FxBars && (IsTimed(action) || (it.KeepSeconds > 0 && IsKeep(action)))) return;
            string detail;
            switch (action)
            {
                case "GiveWeapon": detail = Human(it.Weapon); break;
                case "AddHealth": detail = "+" + it.Amount + " HP"; break;
                case "GiveVehicle": detail = Human(it.VehicleModel); break;
                default: detail = IsTimed(action) ? Math.Round(it.Duration).ToString(U.IC) + "s" : ""; break;
            }
            Popup p = new Popup();
            p.Sup = j.Sup;
            p.Icon = ImgPath(it.ActionImage);
            p.Parts = new List<string>();
            if (j.Sup != null) p.Parts.Add(U.Trunc(j.Sup.Nick, 16));
            p.Parts.Add(Txt.Prep(it.Title));
            if (detail.Length > 0) p.Parts.Add(detail);
            p.Col = col;
            p.Start = U.Now;
            p.End = U.Now + (long)(cfg.FxPopupSeconds * 1000);
            popups.Add(p);
            while (popups.Count > 3) popups.RemoveAt(0);
        }

        static bool IsTimed(string a)
        {
            return a == "GodMode" || a == "SuperSpeed" || a == "SuperJump" || a == "Freeze" || a == "Drunk" || a == "LowGravity" || a == "SlowMotion"
                || a == "Airstrike" || a == "Earthquake" || a == "Storm" || a == "Blackout" || a == "CarRain" || a == "Weather";
        }

        void UpdatePlayerFx()
        {
            Ped pl = Game.Player.Character;
            if (!cfg.FxEnabled || !pl.Exists()) { StopSpeedFx(); return; }
            float time = U.Now / 1000f;
            Vector3 p = pl.IsInVehicle() ? pl.CurrentVehicle.Position : pl.Position;
            Vector3 feet = p - new Vector3(0, 0, pl.IsInVehicle() ? 0.4f : 0.95f);
            // burst: expanding ring + light flash
            float bt = (U.Now - burstStart) / 900f;
            if (bt >= 0 && bt < 1)
            {
                float sc = 0.6f + bt * 3.2f;
                Marker(25, feet, Vector3.Zero, new Vector3(sc, sc, sc), U.WithAlpha(burstCol, (int)(230 * (1 - bt))), false, false);
                Light(p + new Vector3(0, 0, 0.8f), burstCol, 5f, 12f * (1 - bt));
            }
            if (!cfg.FxBuffAura) { StopSpeedFx(); return; }
            // glow while a timed effect is active
            string[] buffs = { "SuperSpeed", "GodMode", "SuperJump", "LowGravity", "Freeze", "Drunk" };
            int k = 0;
            foreach (string b in buffs)
            {
                if (!Active(b)) continue;
                Color c = b == "Drunk" ? Color.FromArgb(255, 255, 150, 40) : FxColor(b);
                Light(p + new Vector3(0, 0, 0.6f), c, 3.2f, 6f + 2f * (float)Math.Sin(time * 6));
                float ph = (time * 1.3f + k * 0.33f) % 1f, rs = 0.8f + ph * 1.6f;
                Marker(25, feet + new Vector3(0, 0, k * 0.05f), Vector3.Zero, new Vector3(rs, rs, rs), U.WithAlpha(c, (int)(200 * (1 - ph))), false, false);
                if (b == "GodMode") Marker(0, p + new Vector3(0, 0, 1.2f + (float)Math.Sin(time * 3) * 0.06f), new Vector3(0, 0, time * 90 % 360), new Vector3(0.3f, 0.3f, 0.25f), U.WithAlpha(c, 220), false, false);
                k++;
            }
            if (Active("SuperSpeed") && !pl.IsInVehicle()) StartSpeedFx(pl); else StopSpeedFx();
        }

        void StartSpeedFx(Ped pl)
        {
            if (speedFx != 0 || string.IsNullOrEmpty(cfg.FxSpeedTrail) || cfg.FxSpeedTrail.Equals("None", StringComparison.OrdinalIgnoreCase)) return;
            try
            {
                string[] p = cfg.FxSpeedTrail.Split('|');
                if (p.Length < 2) return;
                Function.Call(Hash.REQUEST_NAMED_PTFX_ASSET, p[0].Trim());
                if (!Function.Call<bool>(Hash.HAS_NAMED_PTFX_ASSET_LOADED, p[0].Trim())) return;
                Function.Call(Hash.USE_PARTICLE_FX_ASSET, p[0].Trim());
                speedFx = Function.Call<int>(Hash.START_PARTICLE_FX_LOOPED_ON_ENTITY, p[1].Trim(), pl, 0f, 0f, -0.3f, 0f, 0f, 0f, 1f, false, false, false);
                if (speedFx != 0)
                {
                    Color c = cfg.FxSpeedColor;
                    Function.Call(Hash.SET_PARTICLE_FX_LOOPED_COLOUR, speedFx, c.R / 255f, c.G / 255f, c.B / 255f, false);
                }
            }
            catch (Exception ex) { U.Error("SpeedFx", ex); speedFx = 0; }
        }

        void StopSpeedFx()
        {
            if (speedFx == 0) return;
            try { Function.Call(Hash.STOP_PARTICLE_FX_LOOPED, speedFx, false); } catch { }
            speedFx = 0;
        }

        // live bars above the player's head: one per running timed effect.
        // The bar drains with the remaining time; when it is empty the effect stops.
        int fxBarsShown;

        bool FxOnPlayer { get { return Is(cfg.FxBarsPos, "Player"); } }

        List<KeyValuePair<string, long>> RunningFx()
        {
            long now = U.Now;
            List<KeyValuePair<string, long>> list = new List<KeyValuePair<string, long>>();
            foreach (KeyValuePair<string, long> kv in fxEnd) if (kv.Value > now && fxInfo.ContainsKey(kv.Key)) list.Add(kv);
            list.Sort(delegate(KeyValuePair<string, long> x, KeyValuePair<string, long> y) { return x.Value.CompareTo(y.Value); });
            return list;
        }

        static string TimeLeft(float ms)
        {
            int s = (int)Math.Ceiling(ms / 1000f);
            return (s / 60).ToString(U.IC) + ":" + (s % 60).ToString("00", U.IC);
        }

        // timer cards in the HUD (not over the character): supporter + action + time left + draining bar
        void DrawFxCards()
        {
            if (!cfg.FxEnabled || !cfg.FxBars || FxOnPlayer || Is(cfg.FxBarsPos, "Events") || fxEnd.Count == 0) return;
            List<KeyValuePair<string, long>> list = RunningFx();
            if (list.Count == 0) return;
            long now = U.Now;
            int count = Math.Min(list.Count, cfg.FxBarsMax);
            const float w = 250, h = 42, gap = 6;
            string pos = (cfg.FxBarsPos ?? "Bottom").ToLowerInvariant();
            float s = vertical ? Math.Min(L.Scale, (frameW - 20) / w) : L.Scale;
            float total = count * h + (count - 1) * gap;
            float x0, y0;
            bool up = false;
            RectangleF r;
            switch (pos)
            {
                case "top":
                    x0 = frameX + (frameW - w * s) / 2;
                    y0 = panelRects.TryGetValue("Score", out r) ? r.Bottom + 8 : frameY + 90;
                    break;
                case "left":
                    x0 = frameX + 14;
                    y0 = frameY + (frameH - total * s) / 2;
                    break;
                case "right":
                    x0 = frameX + frameW - w * s - 14;
                    y0 = frameY + (frameH - total * s) / 2;
                    break;
                default: // bottom: under the health bar (above it when there is no room)
                    if (VAuto && fxCardsY >= 0) { x0 = frameX + (frameW - w * s) / 2; y0 = fxCardsY; s *= stackScale; }
                    else if (panelRects.TryGetValue("Health", out r))
                    {
                        x0 = r.X + (r.Width - w * s) / 2;
                        y0 = r.Bottom + 6;
                        if (y0 + total * s > frameY + frameH - 4) { y0 = r.Y - 6; up = true; }
                    }
                    else { x0 = frameX + (frameW - w * s) / 2; y0 = frameY + frameH - 12; up = true; }
                    break;
            }
            for (int i = 0; i < count; i++)
            {
                FxInfo fi = fxInfo[list[i].Key];
                float span = Math.Max(1, list[i].Value - fi.Start);
                float left = Math.Max(0, list[i].Value - now);
                float ratio = U.Clamp(left / span, 0, 1);
                float tin = U.Clamp((now - fi.Start) / 250f, 0, 1);
                int a = (int)(255 * Math.Min(1f, Math.Min(tin + 0.2f, left / 400f)));
                Color col = FxColor(fi.Action);
                float yy = up ? y0 - (i + 1) * h * s - i * gap * s : y0 + i * (h + gap) * s;
                Gfx.Origin(x0 + (1 - tin) * (pos == "right" ? 30 : -30) * (pos == "left" || pos == "right" ? 1 : 0), yy, s);
                DBox(0, 0, w, h);
                Gfx.Rect(0, 0, 4, h, U.WithAlpha(col, a));
                Gfx.Image(Avatar(fi.Sup), 10, 5, 28, 28, a);
                float x = 44;
                if (Gfx.FileOk(fi.Icon)) { Gfx.Image(fi.Icon, x, 5, 28, 28, a); x += 32; }
                List<string> parts = new List<string> { Txt.Prep(U.Trunc(fi.Title, 16)) };
                if (fi.Hits > 1) parts.Add("x" + fi.Hits);
                Gfx.Parts(parts, x + 2, 4, SMALL * 1.1f, U.WithAlpha(Color.White, a));
                if (fi.Sup != null) Gfx.Text(U.Trunc(fi.Sup.Nick, 16), x + 2, 21, SMALL * 0.9f, U.WithAlpha(cfg.HypeColor, (int)(a * 0.9f)), Alignment.Left);
                Gfx.Text(TimeLeft(left), w - 10, 6, 0.5f, U.WithAlpha(col, a), Alignment.Right);
                Gfx.Rect(8, h - 5, w - 16, 3, Color.FromArgb(a / 3, 0, 0, 0));
                Gfx.Rect(8, h - 5, (w - 16) * ratio, 3, U.WithAlpha(col, a));
            }
            Gfx.Origin(0, 0, 1);
        }

        float fxCardsY = -1;

        // height the cards need under the health bar in the 9:16 automatic layout
        float FxCardsHeight()
        {
            if (!cfg.FxEnabled || !cfg.FxBars || !Is(cfg.FxBarsPos, "Bottom")) return 0;
            int n = Math.Min(RunningFx().Count, cfg.FxBarsMax);
            if (n == 0) return 0;
            float s = Math.Min(L.Scale, (frameW - 20) / 250f);
            return (n * 42 + (n - 1) * 6) * s;
        }

        // ================================================================ running events panel (in the place of the old gift guide)
        //  timed effects (countdown + draining bar), chases that last while the characters are alive
        //  (mafia car, moto hitman, animals) and short actions shown a few seconds (weapon, heal...).
        class EvItem { public Supporter Sup; public string Title, Icon, Action; public long Start, End; public int Alive, Hits; public bool Live; public Interaction It; }
        readonly List<EvItem> liveEvents = new List<EvItem>();
        readonly List<EvItem> instEvents = new List<EvItem>();
        static readonly string[] ChaseActions = { "MafiaCar", "MotoHitman", "Animals" };

        void RegisterEvent(Job j, string a)
        {
            if (!cfg.EventsEnabled || j == null || j.It == null) return;
            long now = U.Now;
            if (Array.IndexOf(ChaseActions, a) >= 0)
            {
                if (!cfg.EventsSpawns) return;
                foreach (EvItem e in liveEvents)
                    if (e.It == j.It && e.Sup == j.Sup) { e.Hits++; e.Start = now; return; }
                liveEvents.Add(NewEv(j, a, now, 0, true));
                return;
            }
            if (IsSpawn(a) || IsTimed(a) || (IsKeep(a) && j.It.KeepSeconds > 0) || !cfg.EventsInstant) return;
            long end = now + (long)(cfg.EventsInstantSeconds * 1000);
            foreach (EvItem e in instEvents)
                if (e.It == j.It && e.Sup == j.Sup) { e.Hits++; e.End = end; return; }
            instEvents.Add(NewEv(j, a, now, end, false));
            while (instEvents.Count > 8) instEvents.RemoveAt(0);
        }

        EvItem NewEv(Job j, string a, long start, long end, bool live)
        {
            EvItem e = new EvItem();
            e.It = j.It; e.Sup = j.Sup; e.Action = a; e.Start = start; e.End = end; e.Live = live; e.Hits = 1;
            e.Title = j.It.Title; e.Icon = ImgPath(j.It.ActionImage);
            return e;
        }

        void ClearEvents() { liveEvents.Clear(); instEvents.Clear(); }

        List<EvItem> BuildEvents()
        {
            List<EvItem> list = new List<EvItem>();
            long now = U.Now;
            // chases: alive characters of that gift (+ the ones still waiting in the queue)
            for (int i = liveEvents.Count - 1; i >= 0; i--)
            {
                EvItem e = liveEvents[i];
                int alive = 0;
                foreach (Tracked t in tracked) if (t.It == e.It && t.Sup == e.Sup && t.DeadAt == 0) alive++;
                foreach (Job q in spawnQ) if (q.It == e.It && q.Sup == e.Sup) alive += q.Left;
                e.Alive = alive;
                if (alive == 0 && now - e.Start > 4000) liveEvents.RemoveAt(i);
            }
            foreach (EvItem e in liveEvents) if (e.Alive > 0 || now - e.Start <= 4000) list.Add(e);
            if (Is(cfg.FxBarsPos, "Events"))
                foreach (KeyValuePair<string, long> kv in RunningFx())
                {
                    FxInfo fi = fxInfo[kv.Key];
                    EvItem e = new EvItem();
                    e.Sup = fi.Sup; e.Title = fi.Title; e.Icon = fi.Icon; e.Action = fi.Action; e.Start = fi.Start; e.End = kv.Value; e.Hits = fi.Hits;
                    list.Add(e);
                }
            instEvents.RemoveAll(delegate(EvItem e) { return now >= e.End; });
            for (int i = instEvents.Count - 1; i >= 0; i--) list.Add(instEvents[i]);
            if (list.Count > cfg.EventsMax) list.RemoveRange(cfg.EventsMax, list.Count - cfg.EventsMax);
            return list;
        }

        const float EvW = 250, EvH = 42, EvGap = 6;

        // one card at the current origin (0,0): supporter, action, name, time left / alive count, bar
        void DrawEvCard(EvItem e, float y)
        {
            long now = U.Now;
            float tin = U.Clamp((now - e.Start) / 250f, 0, 1);
            float left = e.End > 0 ? Math.Max(0, e.End - now) : 0;
            int a = (int)(255 * (e.End > 0 ? Math.Min(1f, Math.Min(tin + 0.2f, left / 400f)) : Math.Min(1f, tin + 0.2f)));
            Color col = e.Live ? cLoss : FxColor(e.Action);
            if (IsDuo) col = e.Live || !IsHelp(e.Action) ? DuoRed : DuoBlue;   // attacks red, help blue
            DBox(0, y, EvW, EvH);
            Gfx.RRect(4, y + 6, 4, EvH - 12, 2, U.WithAlpha(col, a));   // rounded accent, inside the round corners
            Gfx.Image(Avatar(e.Sup), 10, y + 5, 28, 28, a);
            float x = 44;
            if (Gfx.FileOk(e.Icon)) { Gfx.Image(e.Icon, x, y + 5, 28, 28, a); x += 32; }
            List<string> parts = new List<string> { Txt.Prep(U.Trunc(e.Title, 16)) };
            if (e.Hits > 1) parts.Add("x" + e.Hits);
            Gfx.Parts(parts, x + 2, y + 4, SMALL * 1.1f, U.WithAlpha(Color.White, a));
            if (e.Sup != null) Gfx.Text(U.Trunc(e.Sup.Nick, 16), x + 2, y + 21, SMALL * 0.9f, U.WithAlpha(cfg.HypeColor, (int)(a * 0.9f)), Alignment.Left);
            Gfx.Rect(8, y + EvH - 5, EvW - 16, 3, Color.FromArgb(a / 3, 0, 0, 0));
            if (e.Live)
            {
                // chase: alive count + pulsing bar (ends when they are all dead or the round resets)
                Gfx.Text(e.Alive.ToString(U.IC), EvW - 10, y + 6, 0.5f, U.WithAlpha(col, a), Alignment.Right);
                float pulse = 0.55f + 0.45f * (float)Math.Sin(now / 180.0);
                Gfx.Rect(8, y + EvH - 5, EvW - 16, 3, U.WithAlpha(col, (int)(a * pulse)));
            }
            else
            {
                float span = Math.Max(1, e.End - e.Start);
                Gfx.Text(TimeLeft(left), EvW - 10, y + 6, 0.5f, U.WithAlpha(col, a), Alignment.Right);
                Gfx.Rect(8, y + EvH - 5, (EvW - 16) * U.Clamp(left / span, 0, 1), 3, U.WithAlpha(col, a));
            }
        }

        // HUD panel in the "Guide" slot of the layout
        SizeF EventsPanel(bool draw)
        {
            List<EvItem> items = BuildEvents();
            if (items.Count == 0) return SizeF.Empty;
            float th = cfg.EventsTitleEnabled && cfg.EventsTitle.Length > 0 ? 26 : 0;
            float h = th + items.Count * EvH + (items.Count - 1) * EvGap;
            if (!draw) return new SizeF(EvW, h);
            if (th > 0) { PanelBg(0, 0, EvW, th - 2, 1); Header(cfg.EventsTitle, 0, 2, EvW, 22); }
            for (int i = 0; i < items.Count; i++) DrawEvCard(items[i], th + i * (EvH + EvGap));
            return new SizeF(EvW, h);
        }

        void DrawFxBars()
        {
            fxBarsShown = 0;
            if (!cfg.FxEnabled || !cfg.FxBars || !FxOnPlayer || fxEnd.Count == 0) return;
            Ped pl = Game.Player.Character;
            if (!pl.Exists()) return;
            long now = U.Now;
            List<KeyValuePair<string, long>> list = RunningFx();
            if (list.Count == 0) return;
            Vector3 head = HeadTop(pl, 0f) - new Vector3(0, 0, 0.35f);
            PointF sp = Screen.WorldToScreen(head + new Vector3(0, 0, 0.35f));
            if (sp.X == 0 && sp.Y == 0) return;
            int count = Math.Min(list.Count, cfg.FxBarsMax);
            const float w = 210, h = 30;
            for (int i = 0; i < count; i++)
            {
                FxInfo fi = fxInfo[list[i].Key];
                float total = Math.Max(1, list[i].Value - fi.Start);
                float left = Math.Max(0, list[i].Value - now);
                float ratio = U.Clamp(left / total, 0, 1);
                float tin = U.Clamp((now - fi.Start) / 200f, 0, 1);
                int a = (int)(255 * Math.Min(1f, Math.Min(tin + 0.3f, left / 400f)));
                Color col = FxColor(fi.Action);
                Gfx.Origin(sp.X - w / 2, sp.Y - 15 - i * 34, 1);
                DBox(0, 0, w, h);
                Gfx.Rect(0, h - 4, w, 4, Color.FromArgb(a / 3, 0, 0, 0));
                Gfx.Rect(0, h - 4, w * ratio, 4, U.WithAlpha(col, a));
                Gfx.Image(Avatar(fi.Sup), 4, 3, 22, 22, a);
                float x = 30;
                if (Gfx.FileOk(fi.Icon)) { Gfx.Image(fi.Icon, x, 3, 22, 22, a); x += 26; }
                string sec = (left >= 60000 ? ((int)(left / 60000)) + ":" + ((int)(left / 1000) % 60).ToString("00", U.IC) : Math.Ceiling(left / 1000f).ToString(U.IC) + "s");
                Gfx.Text(sec, w - 6, 5, SMALL * 1.1f, U.WithAlpha(col, a), Alignment.Right);
                List<string> parts = new List<string> { Txt.Prep(U.Trunc(fi.Title, 16)) };
                if (fi.Hits > 1) parts.Add("x" + fi.Hits);
                Gfx.Parts(parts, x + 2, 6, SMALL * 1.05f, U.WithAlpha(Color.White, a));
            }
            fxBarsShown = count;
            Gfx.Origin(0, 0, 1);
        }

        // popups above the player's head (world -> screen), rising and fading
        void DrawPopups()
        {
            if (popups.Count == 0) return;
            long now = U.Now;
            popups.RemoveAll(delegate(Popup x) { return now >= x.End; });
            Ped pl = Game.Player.Character;
            if (!pl.Exists()) return;
            Vector3 head = HeadTop(pl, 0f) - new Vector3(0, 0, 0.35f);
            Gfx.Origin(0, 0, 1);
            int n = fxBarsShown;
            for (int i = popups.Count - 1; i >= 0; i--, n++)
            {
                Popup pp = popups[i];
                float life = (now - pp.Start) / (float)Math.Max(1, pp.End - pp.Start);
                float a = life < 0.1f ? life / 0.1f : (life > 0.8f ? (1 - life) / 0.2f : 1f);
                PointF sp = Screen.WorldToScreen(head + new Vector3(0, 0, 0.35f + life * 0.35f));
                if (sp.X == 0 && sp.Y == 0) continue;
                float y = sp.Y - n * 34;
                float tw = Gfx.PartsW(pp.Parts, SMALL * 1.1f);
                float icon = Gfx.FileOk(pp.Icon) ? 26 : 0;
                float w = 36 + icon + tw + 12, x = sp.X - w / 2;
                Gfx.Origin(x, y - 15, 1);
                DBox(0, 0, w, 30);
                Gfx.Rect(0, 27, w * (1 - life), 3, U.WithAlpha(pp.Col, (int)(255 * a)));
                Gfx.Image(Avatar(pp.Sup), 4, 3, 24, 24, (int)(255 * a));
                if (icon > 0) Gfx.Image(pp.Icon, 32, 3, 24, 24, (int)(255 * a));
                Gfx.Parts(pp.Parts, 34 + icon, 6, SMALL * 1.1f, U.WithAlpha(Color.White, (int)(255 * a)));
                Gfx.Origin(0, 0, 1);
            }
        }

        // ================================================================ custom death (no GTA "WASTED" screen)
        //  [Death] CustomDeath=true: the player keeps a hidden health buffer, so GTA never kills him.
        //  When the visible health reaches 0 -> fake death: ragdoll, slow motion, killer camera,
        //  celebration, our own death screen in the HUD design, then he gets up again (here or at a hospital).
        const int HpBufferSize = 5000;
        int hpBuf;
        bool fakeDead;
        long fakeUntil, fakeStart, nextRagdoll, nextDamageScan;
        Tracked lastDamager;
        long lastDamageAt;
        static readonly float[][] Hospitals = {
            new float[] { 357.4f, -593.4f, 28.8f }, new float[] { -449.7f, -340.8f, 34.5f }, new float[] { 1151.2f, -1529.6f, 35.4f },
            new float[] { 1839.6f, 3672.9f, 34.3f }, new float[] { -247.8f, 6331.6f, 32.4f } };

        int PlayerHpNow()
        {
            Ped pl = Game.Player.Character;
            return pl.Exists() ? Math.Max(0, Function.Call<int>(Hash.GET_ENTITY_HEALTH, pl) - 100 - hpBuf) : 0;
        }

        int PlayerHpMax()
        {
            Ped pl = Game.Player.Character;
            return pl.Exists() ? Math.Max(1, Function.Call<int>(Hash.GET_ENTITY_MAX_HEALTH, pl) - 100 - hpBuf) : 1;
        }

        // gives the player the hidden buffer (keeps the visible health as it is)
        void EnsureHpBuffer()
        {
            Ped pl = Game.Player.Character;
            if (!cfg.CustomDeath || hpBuf > 0 || !pl.Exists() || pl.IsDead) return;
            int cur = Function.Call<int>(Hash.GET_ENTITY_HEALTH, pl);
            int max = Function.Call<int>(Hash.GET_ENTITY_MAX_HEALTH, pl);
            hpBuf = HpBufferSize;
            Function.Call(Hash.SET_PED_MAX_HEALTH, pl, max + hpBuf);
            Function.Call(Hash.SET_ENTITY_MAX_HEALTH, pl, max + hpBuf);
            Function.Call(Hash.SET_ENTITY_HEALTH, pl, cur + hpBuf, 0);
        }

        void RemoveHpBuffer()
        {
            Ped pl = Game.Player.Character;
            if (hpBuf == 0) return;
            if (pl.Exists() && !pl.IsDead)
            {
                int vis = PlayerHpNow();
                Function.Call(Hash.SET_PED_MAX_HEALTH, pl, 200);
                Function.Call(Hash.SET_ENTITY_MAX_HEALTH, pl, 200);
                Function.Call(Hash.SET_ENTITY_HEALTH, pl, Math.Max(101, Math.Min(200, vis + 100)), 0);
            }
            hpBuf = 0;
        }

        // remembers which spawned enemy hurt the player last (the killer of a fake death)
        void ScanDamage()
        {
            if (U.Now < nextDamageScan) return;
            nextDamageScan = U.Now + 200;
            Ped pl = Game.Player.Character;
            if (!pl.Exists()) return;
            foreach (Tracked t in tracked)
            {
                if (!t.Enemy || t.Ped == null || !t.Ped.Exists()) continue;
                bool hit = Function.Call<bool>(Hash.HAS_ENTITY_BEEN_DAMAGED_BY_ENTITY, pl, t.Ped, true)
                    || (t.Veh != null && t.Veh.Exists() && Function.Call<bool>(Hash.HAS_ENTITY_BEEN_DAMAGED_BY_ENTITY, pl, t.Veh, true));
                if (!hit) continue;
                lastDamager = t;
                lastDamageAt = U.Now;
                Function.Call(Hash.CLEAR_ENTITY_LAST_DAMAGE_ENTITY, pl);
                break;
            }
        }

        void UpdateFakeDeath()
        {
            Ped pl = Game.Player.Character;
            if (!cfg.CustomDeath || !pl.Exists()) return;
            if (!fakeDead)
            {
                if (pl.IsDead) return;
                EnsureHpBuffer();
                ScanDamage();
                if (hpBuf > 0 && PlayerHpNow() <= 0) FakeDeath(null);
                return;
            }
            long now = U.Now;
            if (now >= nextRagdoll && !pl.IsInVehicle())
            {
                nextRagdoll = now + 900;
                Function.Call(Hash.SET_PED_TO_RAGDOLL, pl, 2000, 2000, 0, false, false, false);
            }
            Function.Call(Hash.DISABLE_ALL_CONTROL_ACTIONS, 0);
            if (now >= fakeUntil) FakeRevive();
        }

        void FakeDeath(Supporter forced)
        {
            Ped pl = Game.Player.Character;
            fakeDead = true;
            fakeStart = U.Now;
            float secs = Math.Max(cfg.RespawnSeconds, Math.Max(cfg.CelebEnabled ? cfg.CelebSeconds : 0, cfg.KillerEnabled ? cfg.KillerSeconds : 0));
            fakeUntil = U.Now + (long)(secs * 1000);
            pl.IsInvincible = true;
            Function.Call(Hash.SET_ENTITY_HEALTH, pl, 100 + hpBuf + 1, 0);
            if (pl.IsInVehicle())
            {
                Vector3 at = pl.CurrentVehicle.Position + pl.CurrentVehicle.RightVector * -2.2f;
                Function.Call(Hash.SET_ENTITY_COORDS, pl, at.X, at.Y, at.Z, false, false, false, false);
            }
            Function.Call(Hash.SET_PED_TO_RAGDOLL, pl, 3000, 3000, 0, false, false, false);
            nextRagdoll = U.Now + 900;
            killSlowEnd = U.Now + 1400;

            Supporter killer = forced;
            Entity killerEnt = null;
            if (killer == null && U.Now - killPlayerAt < 5000) killer = killPlayerSup;
            if (killer == null && lastDamager != null && U.Now - lastDamageAt < 15000)
            {
                killer = lastDamager.Sup;
                if (lastDamager.Ped != null && lastDamager.Ped.Exists()) killerEnt = lastDamager.Ped;
            }
            if (phase == Phase.Running && cfg.ChallengeEnabled) EndRound(false, killer);
            SetDeathKiller(killer, lastDamager != null && U.Now - lastDamageAt < 15000 ? lastDamager.It : null);
            endScreenUntil = Math.Max(endScreenUntil, fakeUntil);
            if (cfg.KillerEnabled && killerEnt != null && killerEnt.Exists())
            {
                StartCam("killer", killerEnt, cfg.KillerSeconds);
                killerCamEnd = U.Now + (long)(cfg.KillerSeconds * 1000);
            }
            if (cfg.CelebEnabled) StartCelebration();
        }

        void FakeRevive()
        {
            Ped pl = Game.Player.Character;
            fakeDead = false;
            EndCelebration();
            StopCam();
            killerCamEnd = 0;
            Function.Call(Hash.CLEAR_PED_TASKS_IMMEDIATELY, pl);
            Function.Call(Hash.STOP_ENTITY_FIRE, pl);
            if (string.Equals(cfg.RespawnMode, "Hospital", StringComparison.OrdinalIgnoreCase))
            {
                float[] best = Hospitals[0];
                float bd = float.MaxValue;
                foreach (float[] h in Hospitals)
                {
                    float d = pl.Position.DistanceTo(new Vector3(h[0], h[1], h[2]));
                    if (d < bd) { bd = d; best = h; }
                }
                Function.Call(Hash.SET_ENTITY_COORDS, pl, best[0], best[1], best[2], false, false, false, false);
            }
            SetPlayerHealth(cfg.PlayerHealth, true);
            pl.Armor = cfg.PlayerArmor;
            pl.IsInvincible = cfg.PlayerInvincible;
            lastDamager = null;
            Function.Call(Hash.CLEAR_ENTITY_LAST_DAMAGE_ENTITY, pl);
        }

        // dark screen + respawn countdown while fake-dead
        void DrawDeathOverlay()
        {
            if (!fakeDead) return;
            Gfx.Origin(0, 0, 1);
            float t = U.Clamp((U.Now - fakeStart) / 500f, 0, 1);
            if (Is(cfg.DeathOverlay, "Dim"))
            {
                Gfx.RectAbs(0, 0, 1280, 720, Color.FromArgb((int)(110 * t), 0, 0, 0));
                for (int i = 0; i < 6; i++)
                {
                    int a = (int)((90 - i * 15) * t);
                    Gfx.RectAbs(frameX, i * 8, frameW, 8, Color.FromArgb(a, 180, 0, 20));
                    Gfx.RectAbs(frameX, 720 - (i + 1) * 8, frameW, 8, Color.FromArgb(a, 180, 0, 20));
                }
            }
            float left = Math.Max(0, (fakeUntil - U.Now) / 1000f);
            float total = Math.Max(0.1f, (fakeUntil - fakeStart) / 1000f);
            float bw = Math.Min(frameW - 40, 300), x = frameX + (frameW - bw) / 2, y = 720 - 70;
            Gfx.Origin(x, y, 1);
            DBox(0, 0, bw, 30);
            Gfx.Rect(8, 24, (bw - 16) * (1 - left / total), 3, cLoss);
            Gfx.PartsCentered(new List<string> { cfg.Tx("RespawnText", "Respawn"), Math.Ceiling(left).ToString(U.IC) }, bw / 2, 4, SMALL * 1.15f, Color.White);
        }

        // ================================================================ Spotlight: big 3-second notification (mafia car, moto hitman, expensive gifts)
        //  [Spotlight] Enabled, Seconds, MinCoins (0 = only interactions with Spotlight=true), Sound, OffsetY
        //  shows: supporter picture + action picture + interaction title + text, in the HUD design
        class Spot { public Supporter Sup; public string Title, Icon, Gift; public List<string> Parts; public int Count; public long Start, End; }
        readonly List<Spot> spots = new List<Spot>();

        void QueueSpotlight(Interaction it, Supporter s, int units, long coins)
        {
            if (!cfg.SpotEnabled) return;
            bool on = it.Spotlight || (cfg.SpotMinCoins > 0 && coins >= cfg.SpotMinCoins);
            if (!on) return;
            Spot sp = new Spot();
            sp.Sup = s;
            sp.Title = it.Title;
            sp.Icon = ImgPath(it.ActionImage);
            sp.Gift = ImgPath(it.GiftImage);
            string tpl = it.SpotlightText.Length > 0 ? it.SpotlightText : it.Message;
            sp.Parts = Txt.Parts(tpl, s != null ? s.Nick : "", units, null, s != null ? s.Level : 0);
            sp.Count = units;
            spots.Add(sp);
            while (spots.Count > 6) spots.RemoveAt(1);
        }

        void DrawSpotlight()
        {
            if (spots.Count == 0) return;
            long now = U.Now;
            Spot sp = spots[0];
            if (sp.Start == 0)
            {
                sp.Start = now;
                sp.End = now + (long)(cfg.SpotSeconds * 1000);
                Snd(cfg.SpotSound);
            }
            if (now >= sp.End) { spots.RemoveAt(0); return; }
            float life = (now - sp.Start) / (float)Math.Max(1, sp.End - sp.Start);
            float tin = U.Clamp((now - sp.Start) / 260f, 0, 1);
            float a = life > 0.9f ? (1 - life) / 0.1f : 1f;
            float pop = 0.8f + 0.2f * tin + (tin < 1 ? (float)Math.Sin(tin * Math.PI) * 0.06f : 0f);
            float w = 480, h = 118;
            float s = pop * (vertical ? Math.Min(1f, (frameW - 16) / w) : 1f);
            float x0 = frameX + (frameW - w * s) / 2, y0 = frameY + (vertical ? 300 : 150) + cfg.SpotOffsetY;
            Gfx.Origin(x0, y0, s);
            Color acc = DesignOn ? DAcc : cAcc;
            DBox(0, 0, w, h);
            // light sweep
            float sweep = (life * 1.6f) % 1f;
            for (int k = 0; k < 6; k++) Gfx.Rect(w * sweep - 40 + k * 8, 3, 8, h - 6, Color.FromArgb((int)(18 * a * (1 - Math.Abs(k - 2.5f) / 3f)), 255, 255, 255));
            Gfx.Rect(0, 0, w, 3, U.WithAlpha(acc, (int)(255 * a)));
            // action picture (big, with glow)
            Gfx.Shape("glow", 4, 4, 110, 110, U.WithAlpha(acc, (int)(120 * a)));
            if (Gfx.FileOk(sp.Icon)) Gfx.Image(sp.Icon, 17, 17, 84, 84, (int)(255 * a));
            else if (Gfx.FileOk(sp.Gift)) Gfx.Image(sp.Gift, 17, 17, 84, 84, (int)(255 * a));
            // texts
            Gfx.Text(U.Trunc(sp.Title, 26), 118, 10, 0.56f, U.WithAlpha(acc, (int)(255 * a)), Alignment.Left);
            if (sp.Count > 1) Gfx.Text("x" + sp.Count, w - 14, 10, 0.56f, U.WithAlpha(Color.White, (int)(255 * a)), Alignment.Right);
            Gfx.Parts(sp.Parts, 118, 46, TXT * 1.05f, U.WithAlpha(Color.White, (int)(235 * a)));
            // supporter
            AvRing(sp.Sup, 132, 90, 30, cfg.HypeColor, false, false);
            if (sp.Sup != null) Gfx.Text(U.Trunc(sp.Sup.Nick, 20), 154, 81, TXT, U.WithAlpha(cfg.HypeColor, (int)(255 * a)), Alignment.Left);
            if (Gfx.FileOk(sp.Gift) && Gfx.FileOk(sp.Icon)) Gfx.Image(sp.Gift, w - 46, h - 46, 36, 36, (int)(255 * a));
            // time bar
            Gfx.Rect(10, h - 5, (w - 20) * (1 - life), 3, U.WithAlpha(acc, (int)(230 * a)));
        }

        // ================================================================ HUD
        const float TXT = 0.34f, SMALL = 0.28f, BIG = 0.62f;
        float frameX, frameY, frameW = 1280, frameH = 720;
        LayoutCfg L;
        Color cAcc, cPan, cTxt, cWin, cLoss;

        // Own palettes of the pro styles: accent, panel, text, win, loss (used when [Hud] StylePalette=true)
        static readonly Dictionary<string, string[]> Palettes = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "Broadcast", new string[] { "#e10600", "#111418", "#ffffff", "#1fc16b", "#e10600" } },
            { "Cyber",     new string[] { "#00f0ff", "#05060d", "#e0fbff", "#39ff14", "#ff2a6d" } },
            { "Royal",     new string[] { "#ffc828", "#1a0f2e", "#fff4d6", "#ffd54a", "#ff4d6d" } },
            { "Hologram",  new string[] { "#7df9ff", "#041a24", "#d9fbff", "#7dffb3", "#ff7d9b" } },
            { "Gradient",  new string[] { "#ff4fd8", "#1b1036", "#ffffff", "#4dffb5", "#ff5c7a" } },
            { "Stream",    new string[] { "#fe2c55", "#000000", "#ffffff", "#25f4ee", "#fe2c55" } },
            { "Carbon",    new string[] { "#ff7a00", "#121212", "#f2f2f2", "#7cff6b", "#ff3b3b" } },
            { "Luxury",    new string[] { "#d4af37", "#0b0b0d", "#f7f1e1", "#e8c766", "#c0392b" } },
            { "Aurora",    new string[] { "#7cf5d6", "#0a0f22", "#eafffb", "#6effa8", "#ff6b9a" } },
            { "Ember",     new string[] { "#ff5a1f", "#160807", "#fff1e8", "#ffb347", "#ff2e2e" } }
        };

        void ApplyPalette()
        {
            cAcc = cfg.Accent; cPan = cfg.PanelColor; cTxt = cfg.TextColor; cWin = cfg.WinColor; cLoss = cfg.LossColor;
            string[] p;
            if (DesignOn)
            {
                // a design owns the whole HUD: its accent everywhere, no palette of another style mixed in
                cAcc = DAcc;
                cPan = DBg(255);
                if (IsDuo) { cWin = DuoBlue; cLoss = DuoRed; }
                return;
            }
            if (cfg.StylePalette && Palettes.TryGetValue(style ?? "", out p))
            {
                cAcc = U.ParseColor(p[0], cAcc); cPan = U.ParseColor(p[1], cPan); cTxt = U.ParseColor(p[2], cTxt);
                cWin = U.ParseColor(p[3], cWin); cLoss = U.ParseColor(p[4], cLoss);
            }
        }

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

        Color PanelCol(float mul) { return U.WithAlpha(cPan, (int)(cfg.Opacity * mul)); }
        Color TextCol(int a) { return U.WithAlpha(cTxt, a); }
        string St { get { return (style ?? "Classic").ToLowerInvariant(); } }
        static bool Is(string a, string b) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }

        void Brackets(float x, float y, float w, float h, float len, float t, Color c)
        {
            Gfx.Rect(x, y, len, t, c); Gfx.Rect(x, y, t, len, c);
            Gfx.Rect(x + w - len, y, len, t, c); Gfx.Rect(x + w - t, y, t, len, c);
            Gfx.Rect(x, y + h - t, len, t, c); Gfx.Rect(x, y + h - len, t, len, c);
            Gfx.Rect(x + w - len, y + h - t, len, t, c); Gfx.Rect(x + w - t, y + h - len, t, len, c);
        }

        void PanelBg(float x, float y, float w, float h, float alpha)
        {
            if (DesignOn) { DPlate(x, y, w, h, Math.Min(1f, alpha)); return; }
            Color acc = cAcc;
            int A = (int)(255 * alpha);
            long now = U.Now;
            switch (St)
            {
                case "glass":
                    Gfx.Rect(x, y, w, h, U.WithAlpha(U.Mix(cPan, Color.White, 0.12f), (int)(cfg.Opacity * 0.55f * alpha)));
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
                    Gfx.Rect(x, y, 4, h, U.WithAlpha(acc, A));
                    Gfx.Rect(x, y + h - 2, w, 2, U.WithAlpha(acc, (int)(120 * alpha)));
                    break;
                case "retro":
                    Gfx.Rect(x, y, w, h, PanelCol(alpha));
                    Gfx.Border(x, y, w, h, 2, U.WithAlpha(cTxt, (int)(170 * alpha)));
                    break;
                case "broadcast":
                    // TV sports graphics: solid plate, colored side block, white hairline
                    Gfx.Rect(x, y, w, h, PanelCol(alpha * 1.1f));
                    Gfx.Rect(x, y, 6, h, U.WithAlpha(acc, A));
                    Gfx.Rect(x + 6, y, w - 6, 1, U.WithAlpha(Color.White, (int)(90 * alpha)));
                    Gfx.Rect(x, y + h - 3, w, 3, U.WithAlpha(acc, (int)(200 * alpha)));
                    break;
                case "cyber":
                    // cut corners + brackets + scanlines
                    {
                        const float cut = 7;
                        Color bg = PanelCol(alpha);
                        Gfx.Rect(x + cut, y, w - cut * 2, h, bg);
                        Gfx.Rect(x, y + cut, cut, h - cut * 2, bg);
                        Gfx.Rect(x + w - cut, y + cut, cut, h - cut * 2, bg);
                        for (int i = 1; i < cut; i += 2) { Gfx.Rect(x + cut - i, y + i, i, 2, bg); Gfx.Rect(x + w - cut, y + h - i - 2, i, 2, bg); }
                        for (float sy = y + 3; sy < y + h - 2; sy += 6) Gfx.Rect(x + 2, sy, w - 4, 1, U.WithAlpha(acc, (int)(14 * alpha)));
                        Brackets(x - 2, y - 2, w + 4, h + 4, 12, 2, U.WithAlpha(acc, A));
                        Gfx.Rect(x + w * 0.3f, y + h - 1, w * 0.4f, 1, U.WithAlpha(acc, (int)(160 * alpha)));
                    }
                    break;
                case "royal":
                    Gfx.Rect(x, y, w, h, PanelCol(alpha));
                    Gfx.Border(x, y, w, h, 2, U.WithAlpha(acc, A));
                    Gfx.Border(x + 4, y + 4, w - 8, h - 8, 1, U.WithAlpha(acc, (int)(110 * alpha)));
                    Gfx.Rect(x - 2, y - 2, 6, 6, U.WithAlpha(acc, A)); Gfx.Rect(x + w - 4, y - 2, 6, 6, U.WithAlpha(acc, A));
                    Gfx.Rect(x - 2, y + h - 4, 6, 6, U.WithAlpha(acc, A)); Gfx.Rect(x + w - 4, y + h - 4, 6, 6, U.WithAlpha(acc, A));
                    break;
                case "hologram":
                    {
                        float fl = 0.85f + 0.15f * (float)Math.Sin(now / 90.0);
                        Gfx.Rect(x, y, w, h, U.WithAlpha(U.Mix(cPan, acc, 0.18f), (int)(cfg.Opacity * 0.45f * alpha * fl)));
                        Gfx.Border(x, y, w, h, 1, U.WithAlpha(acc, (int)(90 * alpha)));
                        Brackets(x, y, w, h, 10, 2, U.WithAlpha(acc, (int)(230 * alpha * fl)));
                        float band = (now / 12) % Math.Max(1, (int)h);
                        Gfx.Rect(x + 1, y + band, w - 2, 2, U.WithAlpha(acc, (int)(70 * alpha)));
                    }
                    break;
                case "gradient":
                    {
                        const int n = 14;
                        Color from = U.Mix(cPan, acc, 0.45f), to = cPan;
                        for (int i = 0; i < n; i++)
                            Gfx.Rect(x + w * i / n, y, w / n + 0.6f, h, U.WithAlpha(U.Mix(from, to, i / (float)(n - 1)), (int)(cfg.Opacity * alpha)));
                        for (int i = 0; i < n; i++)
                            Gfx.Rect(x + w * i / n, y, w / n + 0.6f, 2, U.WithAlpha(U.Mix(acc, cWin, i / (float)(n - 1)), A));
                    }
                    break;
                case "stream":
                    // TikTok glitch: cyan / red offset plates behind a black plate
                    Gfx.Rect(x - 2, y - 1, w, h, U.WithAlpha(cWin, (int)(120 * alpha)));
                    Gfx.Rect(x + 2, y + 1, w, h, U.WithAlpha(acc, (int)(120 * alpha)));
                    Gfx.Rect(x, y, w, h, U.WithAlpha(cPan, (int)(Math.Max(cfg.Opacity, 190) * alpha)));
                    break;
                case "luxury":
                    // black plate, gold hairlines and corner marks
                    Gfx.Rect(x, y, w, h, U.WithAlpha(cPan, (int)(Math.Max(cfg.Opacity, 215) * Math.Min(1f, alpha))));
                    Gfx.Rect(x, y, w, 2, U.WithAlpha(acc, A));
                    Gfx.Rect(x, y + h - 1, w, 1, U.WithAlpha(acc, (int)(170 * alpha)));
                    Gfx.Rect(x + 3, y + 5, w - 6, 1, U.WithAlpha(acc, (int)(45 * alpha)));
                    Gfx.Rect(x, y, 1, h, U.WithAlpha(acc, (int)(110 * alpha)));
                    Gfx.Rect(x + w - 1, y, 1, h, U.WithAlpha(acc, (int)(110 * alpha)));
                    Gfx.Rect(x - 1, y - 1, 5, 5, U.WithAlpha(acc, A)); Gfx.Rect(x + w - 4, y - 1, 5, 5, U.WithAlpha(acc, A));
                    break;
                case "aurora":
                    {
                        // northern-lights gradient over a deep blue plate
                        Gfx.Rect(x, y, w, h, PanelCol(alpha));
                        const int n = 12;
                        Color c1 = Color.FromArgb(255, 60, 240, 200), c2 = Color.FromArgb(255, 140, 90, 255);
                        float wave = (float)(0.5 + 0.5 * Math.Sin(now / 900.0));
                        for (int i = 0; i < n; i++)
                        {
                            Color c = U.Mix(c1, c2, U.Clamp(i / (float)(n - 1) * 0.8f + wave * 0.2f, 0, 1));
                            Gfx.Rect(x + w * i / n, y, w / n + 0.6f, h, U.WithAlpha(c, (int)(34 * alpha)));
                            Gfx.Rect(x + w * i / n, y, w / n + 0.6f, 2, U.WithAlpha(c, A));
                        }
                    }
                    break;
                case "ember":
                    {
                        // dark plate with a warm glow rising from the bottom
                        Gfx.Rect(x, y, w, h, PanelCol(alpha));
                        for (int i = 0; i < 5; i++)
                            Gfx.Rect(x, y + h - (i + 1) * Math.Max(2, h / 10), w, Math.Max(2, h / 10), U.WithAlpha(acc, (int)((60 - i * 11) * alpha)));
                        Gfx.Rect(x, y, 3, h, U.WithAlpha(acc, A));
                        Gfx.Rect(x, y + h - 2, w, 2, U.WithAlpha(U.Mix(acc, Color.Yellow, 0.3f), A));
                    }
                    break;
                case "carbon":
                    Gfx.Rect(x, y, w, h, PanelCol(alpha));
                    for (float sx = x + 2; sx < x + w - 2; sx += 4) Gfx.Rect(sx, y, 2, h, U.WithAlpha(Color.White, (int)(7 * alpha)));
                    Gfx.Rect(x, y, 3, h, U.WithAlpha(acc, A));
                    Gfx.Rect(x, y + h - 2, w, 2, U.WithAlpha(acc, (int)(170 * alpha)));
                    Gfx.Rect(x + w - 14, y, 14, 3, U.WithAlpha(acc, A));
                    break;
                default:
                    Gfx.Rect(x, y, w, h, PanelCol(alpha));
                    Gfx.Rect(x, y, w, 2, U.WithAlpha(acc, A));
                    break;
            }
        }

        void Header(string text, float x, float y, float w, float h)
        {
            if (DesignOn)
            {
                Color acc = DAcc;
                switch (Dz)
                {
                    case "duo":
                        Gfx.Text(text, x + w / 2, y + 2, TXT, Color.White, Alignment.Center);
                        DuoLine(x + w / 2 - 26, y + h - 2, 52);
                        break;
                    case "broadcast":
                        {
                            float tw = Math.Min(w - 16, Gfx.TextW(text, TXT) + 26);
                            Gfx.Shape("slant_l", x + w / 2 - tw / 2 - 8, y, 8, h, acc);
                            Gfx.Rect(x + w / 2 - tw / 2, y, tw, h, acc);
                            Gfx.Shape("slant_r", x + w / 2 + tw / 2, y, 8, h, acc);
                            Gfx.Text(text, x + w / 2, y + 2, TXT, Ink, Alignment.Center);
                            break;
                        }
                    case "esports": Gfx.PartsCentered(new List<string> { "//", text, "//" }, x + w / 2, y + 2, TXT, acc); break;
                    case "podium":
                    case "minimal": Gfx.Text(text, x + w / 2, y + 2, TXT, Color.White, Alignment.Center); break;
                    case "cards":
                        Gfx.Text(text, x + w / 2, y + 2, TXT, acc, Alignment.Center);
                        Gfx.Rect(x + w / 2 - 20, y + h - 2, 40, 2, acc);
                        break;
                    default: Gfx.Text(text, x + w / 2, y + 2, TXT, acc, Alignment.Center); break;
                }
                return;
            }
            switch (St)
            {
                case "esports":
                case "broadcast":
                    Gfx.Rect(x, y, w, h, cAcc);
                    Gfx.Text(text, x + w / 2, y + 2, TXT, St == "broadcast" ? Color.White : cPan, Alignment.Center);
                    break;
                case "luxury":
                case "royal":
                    {
                        float tw = Gfx.TextW(text, TXT);
                        Gfx.Text(text, x + w / 2, y + 2, TXT, cAcc, Alignment.Center);
                        Gfx.Rect(x + 10, y + h / 2, Math.Max(0, (w - tw) / 2 - 18), 1, U.WithAlpha(cAcc, 180));
                        Gfx.Rect(x + (w + tw) / 2 + 8, y + h / 2, Math.Max(0, (w - tw) / 2 - 18), 1, U.WithAlpha(cAcc, 180));
                    }
                    break;
                case "cyber":
                case "hologram":
                    Gfx.Text("[ " + text + " ]", x + w / 2, y + 2, TXT, cAcc, Alignment.Center);
                    break;
                case "stream":
                    Gfx.Text(text, x + w / 2, y + 2, TXT, cTxt, Alignment.Center);
                    Gfx.Rect(x + w / 2 - 22, y + h - 2, 22, 2, cWin);
                    Gfx.Rect(x + w / 2, y + h - 2, 22, 2, cAcc);
                    break;
                default:
                    Gfx.Text(text, x + w / 2, y + 2, TXT, cAcc, Alignment.Center);
                    break;
            }
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
            if (VAuto) return "center";
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
            if (vertical) s = FitScale(sz.Width, s);
            PointF a = Anchor(p, sz.Width * s, sz.Height * s);
            Gfx.Origin(a.X, a.Y, s);
            fn(true);
            panelRects[name] = new RectangleF(a.X, a.Y, sz.Width * s, sz.Height * s);
        }

        PanelFn fScore, fTop, fHealth, fGuide, fNotif, fFeed, fHype;
        readonly Dictionary<string, RectangleF> panelRects = new Dictionary<string, RectangleF>();

        void DrawHud()
        {
            if (!started || !cfg.HudEnabled) return;
            if (Function.Call<bool>(Hash.IS_PAUSE_MENU_ACTIVE)) return;
            Gfx.BeginFrame();
            ApplyPalette();
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
                fScore = ScorePanel; fTop = TopPanel; fHealth = HealthPanel; fGuide = EventsPanel;
                fNotif = NotifPanel; fFeed = FeedPanel; fHype = HypePanel;
            }

            PruneFeeds();
            panelRects.Clear();
            Overheads();
            DrawFxBars();
            DrawPopups();
            if (VAuto)
            {
                float top = frameY + 8, bottom = frameY + frameH - cfg.VBottomMargin;
                // everything must fit between the top and the bottom margin: shrink all together if not
                stackScale = 1;
                float need = StackMeasure("Score", cfg.ScoreEnabled, fScore) + StackMeasure("Top3", cfg.Top3Enabled, fTop)
                           + StackMeasure("Hype", cfg.HypeEnabled && hypes.Count > 0, fHype) + StackMeasure("Guide", cfg.EventsEnabled, fGuide) + StackMeasure("Health", cfg.HealthEnabled, fHealth)
                           + StackMeasure("Notif", cfg.NotifEnabled && notifs.Count > 0, fNotif) + StackMeasure("Feed", cfg.FeedEnabled && feed.Count > 0, fFeed);
                float room = bottom - top - 150;   // keep the middle free for the game
                if (need > room && need > 0) stackScale = Math.Max(0.55f, room / need);
                StackTop("Score", cfg.ScoreEnabled, fScore, ref top);
                StackTop("Top3", cfg.Top3Enabled, fTop, ref top);
                StackTop("Hype", cfg.HypeEnabled && hypes.Count > 0, fHype, ref top);
                StackTop("Guide", cfg.EventsEnabled, fGuide, ref top);
                fxCardsY = -1;
                float fxH = FxCardsHeight();
                if (fxH > 0) { bottom -= fxH * stackScale; fxCardsY = bottom; bottom -= cfg.VGap * stackScale; }
                StackBottom("Health", cfg.HealthEnabled, fHealth, ref bottom);
                StackBottom("Notif", cfg.NotifEnabled && notifs.Count > 0, fNotif, ref bottom);
                StackBottom("Feed", cfg.FeedEnabled && feed.Count > 0, fFeed, ref bottom);
            }
            else
            {
                Place("Score", cfg.ScoreEnabled, fScore);
                Place("Top3", cfg.Top3Enabled, fTop);
                Place("Health", cfg.HealthEnabled, fHealth);
                Place("Guide", cfg.EventsEnabled, fGuide);
                Place("Notif", cfg.NotifEnabled && notifs.Count > 0, fNotif);
                Place("Feed", cfg.FeedEnabled && feed.Count > 0, fFeed);
                Place("Hype", cfg.HypeEnabled && hypes.Count > 0, fHype);
            }
            DrawFxCards();
            DrawSpotlight();
            DrawDeathOverlay();
            DrawKillerBanner();
            EndScreen();
            LiveBadge();
            DrawToast(false);
            if (paused)
            {
                Gfx.Origin(0, 0, 1);
                Gfx.Text(cfg.Tx("PausedText", "PAUSED"), frameX + frameW / 2, 300, 0.7f, cAcc, Alignment.Center);
            }
        }

        void LiveBadge()
        {
            if (!simOn || !cfg.LiveTestBadge) return;
            Gfx.Origin(0, 0, 1);
            string t = "LIVE TEST";
            float w = Gfx.TextW(t, SMALL) + 26, x = frameX + frameW - w - 8, y = frameY + 4;
            bool blink = (U.Now / 500) % 2 == 0;
            Gfx.Rect(x, y, w, 18, Color.FromArgb(220, 254, 44, 85));
            Gfx.Rect(x + 6, y + 6, 6, 6, blink ? Color.White : Color.FromArgb(120, 255, 255, 255));
            Gfx.Text(t, x + 16, y + 1, SMALL, Color.White, Alignment.Left);
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

        Color TimerColor()
        {
            double left = roundDurMs - roundMs;
            return phase == Phase.Running && left < 30000 && (U.Now / 500) % 2 == 0 ? cLoss : cTxt;
        }

        List<string> StreakParts()
        {
            bool win = streak > 0;
            return new List<string> { Math.Abs(streak).ToString(U.IC), win ? cfg.Tx("WinStreakText", "") : cfg.Tx("LossStreakText", "") };
        }

        SizeF ScoreInner(bool draw)
        {
            string v = cfg.ScoreVariant ?? "Classic";
            bool title = cfg.TitleEnabled && cfg.Title.Length > 0;
            bool timer = cfg.ShowTimer && cfg.ChallengeEnabled;
            bool showStreak = cfg.ShowStreak && Math.Abs(streak) >= 2;
            if (Is(v, "Bar"))
            {
                float w = 380, h = 38 + (title ? 22 : 0) + (showStreak ? 18 : 0);
                if (!draw) return new SizeF(w, h);
                PanelBg(0, 0, w, h, 1.3f);
                float y = 0;
                if (title) { Header(cfg.Title, 0, 0, w, 20); y += 22; }
                if (cfg.ShowScore)
                {
                    Gfx.Rect(8, y + 4, 110, 30, U.WithAlpha(cWin, 200));
                    Gfx.Text(cfg.Tx("WinShort", "W"), 16, y + 9, TXT, Color.White, Alignment.Left);
                    Gfx.Text(wins.ToString(U.IC), 110, y + 4, 0.5f, Color.White, Alignment.Right);
                    Gfx.Rect(w - 118, y + 4, 110, 30, U.WithAlpha(cLoss, 200));
                    Gfx.Text(losses.ToString(U.IC), w - 110, y + 4, 0.5f, Color.White, Alignment.Left);
                    Gfx.Text(cfg.Tx("LossShort", "L"), w - 16, y + 9, TXT, Color.White, Alignment.Right);
                }
                if (timer) Gfx.Text(TimerText(), w / 2, y + 5, 0.52f, TimerColor(), Alignment.Center);
                y += 38;
                if (showStreak) Gfx.PartsCentered(StreakParts(), w / 2, y - 2, SMALL, streak > 0 ? cWin : cLoss);
                return new SizeF(w, h);
            }
            if (Is(v, "BigTimer"))
            {
                float w = 250, h = 6 + (title ? 24 : 0) + (timer ? 60 : 0) + (cfg.ShowScore ? 24 : 0) + (showStreak ? 18 : 0) + 4;
                if (!draw) return new SizeF(w, h);
                PanelBg(0, 0, w, h, 1.3f);
                float y = 6;
                if (title) { Header(cfg.Title, 0, y - 4, w, 22); y += 24; }
                if (timer)
                {
                    Gfx.Text(TimerText(), w / 2, y - 4, 1.05f, TimerColor(), Alignment.Center);
                    float frac = roundDurMs > 0 ? (float)U.Clamp((float)(roundMs / roundDurMs), 0, 1) : 0;
                    Gfx.Bar(14, y + 50, w - 28, 4, 1 - frac, cAcc, U.WithAlpha(Color.Black, 150));
                    y += 60;
                }
                if (cfg.ShowScore)
                {
                    Gfx.Text(wins.ToString(U.IC) + " " + cfg.Tx("WinShort", "W"), w / 2 - 12, y, TXT, cWin, Alignment.Right);
                    Gfx.Text("—", w / 2, y, TXT, TextCol(160), Alignment.Center);
                    Gfx.Text(losses.ToString(U.IC) + " " + cfg.Tx("LossShort", "L"), w / 2 + 12, y, TXT, cLoss, Alignment.Left);
                    y += 24;
                }
                if (showStreak) Gfx.PartsCentered(StreakParts(), w / 2, y - 2, SMALL, streak > 0 ? cWin : cLoss);
                return new SizeF(w, h);
            }

            // Classic
            {
                float w = 290, y = 6;
                List<string> rows = new List<string>();
                foreach (string r in cfg.ScoreOrder)
                {
                    string k = r.ToLowerInvariant();
                    if (k == "title" && title) rows.Add(k);
                    else if (k == "score" && (cfg.ShowScore || timer)) rows.Add(k);
                    else if (k == "streak" && showStreak) rows.Add(k);
                }
                if (rows.Count == 0) return SizeF.Empty;
                float h = 6;
                foreach (string r in rows) h += r == "title" ? 26 : (r == "score" ? 50 : 22);
                h += 4;
                if (!draw) return new SizeF(w, h);
                PanelBg(0, 0, w, h, 1.3f);
                foreach (string r in rows)
                {
                    if (r == "title") { Header(cfg.Title, 0, y, w, 24); y += 26; }
                    else if (r == "score")
                    {
                        if (cfg.ShowScore)
                        {
                            Gfx.Rect(10, y + 3, 72, 44, U.WithAlpha(cWin, 60));
                            Gfx.Rect(10, y + 3, 72, 3, cWin);
                            Gfx.Text(wins.ToString(U.IC), 46, y + 4, 0.55f, cWin, Alignment.Center);
                            Gfx.Text(cfg.Tx("WinShort", "W"), 46, y + 29, SMALL, TextCol(230), Alignment.Center);
                            Gfx.Rect(w - 82, y + 3, 72, 44, U.WithAlpha(cLoss, 60));
                            Gfx.Rect(w - 82, y + 3, 72, 3, cLoss);
                            Gfx.Text(losses.ToString(U.IC), w - 46, y + 4, 0.55f, cLoss, Alignment.Center);
                            Gfx.Text(cfg.Tx("LossShort", "L"), w - 46, y + 29, SMALL, TextCol(230), Alignment.Center);
                        }
                        if (timer) Gfx.Text(TimerText(), w / 2, y + 8, BIG, TimerColor(), Alignment.Center);
                        y += 50;
                    }
                    else if (r == "streak")
                    {
                        Gfx.PartsCentered(StreakParts(), w / 2, y + 2, TXT, streak > 0 ? cWin : cLoss);
                        y += 22;
                    }
                }
                return new SizeF(w, h);
            }
        }

        // ---------------------------------------------------------------- top supporters
        static readonly Color[] Medal = { Color.FromArgb(255, 255, 200, 40), Color.FromArgb(255, 200, 210, 220), Color.FromArgb(255, 215, 140, 80) };

        List<string> TopRows()
        {
            List<string> rows = new List<string>();
            foreach (string r in cfg.Top3Order)
            {
                string k = r.ToLowerInvariant();
                if (k == "top3" && cfg.ShowTop3) rows.Add(k);
                else if (k == "status" && cfg.ShowStatus && (cfg.LiveEnabled || simOn)) rows.Add(k);
            }
            return rows;
        }

        void CountersRow(float w, float y)
        {
            List<string> c = new List<string> {
                cfg.Tx("EnemiesShort", "E"), AliveCount(true).ToString(U.IC), "·",
                cfg.Tx("AlliesShort", "A"), AliveCount(false).ToString(U.IC), "·",
                cfg.Tx("QueueShort", "Q"), QueueCount().ToString(U.IC) };
            Gfx.PartsCentered(c, w / 2, y + 3, SMALL, TextCol(230));
        }

        void StatusRow(float w, float y)
        {
            bool ok = live.Connected;
            string t = simOn ? "LIVE TEST" : "TikFinity";
            Gfx.Rect(w / 2 - 44, y + 8, 7, 7, simOn ? Color.FromArgb(255, 254, 44, 85) : (ok ? cWin : cLoss));
            Gfx.Text(t, w / 2 - 32, y + 3, SMALL, TextCol(220), Alignment.Left);
        }

        SizeF TopPanel(bool draw)
        {
            if (DesignOn) return DesignTop(draw);
            string v = cfg.Top3Variant ?? "List";
            List<Supporter> top = TopList(cfg.TopCount);
            List<string> rows = TopRows();
            if (rows.Count == 0) return SizeF.Empty;
            bool podium = Is(v, "Podium"), compact = Is(v, "Compact");
            float w = podium ? 256 : (compact ? Math.Max(230, Math.Max(1, top.Count) * 44 + 16) : 236);
            float h = 6;
            foreach (string r in rows)
            {
                if (r != "top3") { h += 22; continue; }
                h += 24;
                if (podium) h += 118 + Math.Max(0, top.Count - 3) * 24;
                else if (compact) h += 62;
                else h += Math.Max(1, top.Count) * 30;
            }
            h += 4;
            if (!draw) return new SizeF(w, h);

            PanelBg(0, 0, w, h, 1);
            float y = 6;
            foreach (string r in rows)
            {
                if (r == "counters") { CountersRow(w, y); y += 22; continue; }
                if (r == "status") { StatusRow(w, y); y += 22; continue; }
                Header(cfg.Top3Title, 0, y, w, 22);
                y += 24;
                if (podium)
                {
                    // 2 - 1 - 3 columns on pedestals
                    int[] order = { 1, 0, 2 };
                    float[] ped = { 30, 44, 22 };
                    float colW = (w - 16) / 3;
                    for (int c = 0; c < 3; c++)
                    {
                        int i = order[c];
                        float cx = 8 + colW * c + colW / 2;
                        float baseY = y + 118;
                        float av = i == 0 ? 44 : 34;
                        Gfx.Rect(cx - colW / 2 + 3, baseY - ped[c], colW - 6, ped[c], U.WithAlpha(Medal[i], 170));
                        Gfx.Text((i + 1).ToString(U.IC), cx, baseY - ped[c] + 2, 0.42f, Color.FromArgb(230, 20, 20, 20), Alignment.Center);
                        if (i < top.Count)
                        {
                            Supporter s = top[i];
                            float ay = baseY - ped[c] - av - 30;
                            Gfx.Image(avatars.Ring(Avatar(s), Medal[i]), cx - av / 2, ay, av, av, 255);
                            Gfx.Text(U.Trunc(s.Nick, 9), cx, ay + av + 1, SMALL, TextCol(255), Alignment.Center);
                            if (cfg.ShowCoins) Gfx.Text(U.Coins(s.Coins), cx, ay + av + 14, SMALL * 0.9f, cAcc, Alignment.Center);
                        }
                    }
                    y += 118;
                    for (int i = 3; i < top.Count; i++)
                    {
                        Supporter s = top[i];
                        Gfx.Text((i + 1).ToString(U.IC), 16, y + 4, SMALL, TextCol(180), Alignment.Center);
                        Gfx.Image(Avatar(s), 28, y + 2, 20, 20, 255);
                        Gfx.Text(U.Trunc(s.Nick, 16), 54, y + 4, SMALL, TextCol(240), Alignment.Left);
                        if (cfg.ShowCoins) Gfx.Text(U.Coins(s.Coins), w - 12, y + 4, SMALL, cAcc, Alignment.Right);
                        y += 24;
                    }
                }
                else if (compact)
                {
                    if (top.Count == 0) Gfx.Text("—", w / 2, y + 20, TXT, TextCol(150), Alignment.Center);
                    float x0 = (w - top.Count * 44) / 2;
                    for (int i = 0; i < top.Count; i++)
                    {
                        Supporter s = top[i];
                        float cx = x0 + i * 44 + 22;
                        Color mc = i < 3 ? Medal[i] : TextCol(200);
                        Gfx.Image(avatars.Ring(Avatar(s), mc), cx - 18, y + 2, 36, 36, 255);
                        Gfx.Rect(cx + 8, y, 14, 13, mc);
                        Gfx.Text((i + 1).ToString(U.IC), cx + 15, y - 1, SMALL * 0.85f, Color.Black, Alignment.Center);
                        if (cfg.ShowCoins) Gfx.Text(U.Coins(s.Coins), cx, y + 40, SMALL * 0.9f, cAcc, Alignment.Center);
                    }
                    y += 62;
                }
                else
                {
                    if (top.Count == 0) { Gfx.Text("—", w / 2, y + 6, TXT, TextCol(150), Alignment.Center); y += 30; }
                    for (int i = 0; i < top.Count; i++)
                    {
                        Supporter s = top[i];
                        Color mc = i < 3 ? Medal[i] : TextCol(200);
                        Gfx.Rect(6, y + 2, w - 12, 26, U.WithAlpha(mc, 28));
                        Gfx.Text((i + 1).ToString(U.IC), 16, y + 6, TXT, mc, Alignment.Center);
                        Gfx.Image(Avatar(s), 28, y + 3, 24, 24, 255);
                        Gfx.Text(U.Trunc(s.Nick, 16), 58, y + 6, TXT, TextCol(255), Alignment.Left);
                        if (cfg.ShowCoins) Gfx.Text(U.Coins(s.Coins), w - 12, y + 6, TXT, cAcc, Alignment.Right);
                        y += 30;
                    }
                }
            }
            return new SizeF(w, h);
        }

        // ---------------------------------------------------------------- health
        SizeF HealthPanel(bool draw)
        {
            if (DesignOn) return DesignHealth(draw);
            string v = cfg.HealthVariant ?? "Bar";
            float w = Math.Max(80, L.HealthWidth);
            bool armor = cfg.ShowArmor;
            float h;
            if (Is(v, "Numbers")) h = 46 + (armor ? 5 : 0);
            else if (Is(v, "Slim")) h = 30;
            else h = 22 + 12 + (armor ? 8 : 0) + 8;
            if (!draw) return new SizeF(w, h);

            Ped pl = Game.Player.Character;
            float frac = 0, af = 0;
            int hpNow = 0, hpMax = 0;
            if (pl.Exists())
            {
                hpMax = PlayerHpMax();
                hpNow = PlayerHpNow();
                frac = U.Clamp(hpNow / (float)hpMax, 0, 1);
                af = U.Clamp(pl.Armor / 100f, 0, 1);
            }
            bool low = frac < 0.25f;
            Color hc = low ? cLoss : cfg.HealthColor;
            if (low && (U.Now / 300) % 2 == 0) hc = U.Mix(hc, Color.White, 0.35f);
            string pct = (frac * 100).ToString("0.0", U.IC) + "%";
            Color back = U.WithAlpha(Color.Black, 150);
            Color armorC = Color.FromArgb(255, 90, 170, 255);

            if (Is(v, "Segmented"))
            {
                PanelBg(0, 0, w, h, 0.85f);
                Gfx.Parts(new List<string> { cfg.HealthLabel, pct }, 8, 3, TXT, TextCol(255));
                if (cfg.ShowHealthPoints) Gfx.Text(hpNow + " / " + hpMax, w - 8, 3, TXT, TextCol(220), Alignment.Right);
                const int segs = 10;
                float sw = (w - 16 - (segs - 1) * 3) / segs;
                for (int i = 0; i < segs; i++)
                {
                    float f = U.Clamp(frac * segs - i, 0, 1);
                    float sx = 8 + i * (sw + 3);
                    Gfx.Rect(sx, 22, sw, 12, back);
                    if (f > 0) Gfx.Rect(sx, 22, sw * f, 12, hc);
                }
                if (armor) Gfx.Bar(8, 37, w - 16, 5, af, armorC, back);
            }
            else if (Is(v, "Numbers"))
            {
                PanelBg(0, 0, w, h, 0.85f);
                Gfx.Text(hpNow.ToString(U.IC), 10, 0, 0.72f, hc, Alignment.Left);
                float nw = Gfx.TextW(hpNow.ToString(U.IC), 0.72f);
                Gfx.Text("/ " + hpMax, 14 + nw, 14, TXT, TextCol(200), Alignment.Left);
                Gfx.Parts(new List<string> { cfg.HealthLabel, pct }, w - 10 - Gfx.PartsW(new List<string> { cfg.HealthLabel, pct }, SMALL), 14, SMALL, TextCol(220));
                Gfx.Bar(10, 38, w - 20, 4, frac, hc, back);
                if (armor) Gfx.Bar(10, 44, w - 20, 3, af, armorC, back);
            }
            else if (Is(v, "Slim"))
            {
                Gfx.Text(cfg.HealthLabel, 2, 0, SMALL, TextCol(230), Alignment.Left);
                Gfx.Text(cfg.ShowHealthPoints ? hpNow + " / " + hpMax : pct, w - 2, 0, SMALL, TextCol(230), Alignment.Right);
                Gfx.Rect(0, 18, w, 6, back);
                Gfx.Rect(0, 18, w * frac, 6, hc);
                Gfx.Rect(0, 24, w * frac, 2, U.WithAlpha(hc, 90));
                if (armor) Gfx.Rect(0, 27, w * af, 2, armorC);
            }
            else
            {
                PanelBg(0, 0, w, h, 0.85f);
                Gfx.Parts(new List<string> { cfg.HealthLabel, pct }, 8, 3, TXT, TextCol(255));
                if (cfg.ShowHealthPoints) Gfx.Text(hpNow + " / " + hpMax, w - 8, 3, TXT, TextCol(220), Alignment.Right);
                Gfx.Bar(8, 22, w - 16, 12, frac, hc, back);
                if (armor) Gfx.Bar(8, 37, w - 16, 5, af, armorC, back);
            }
            if (Is(v, "Numbers")) DamageFx(10, 38, w - 20, 4, frac);
            else if (Is(v, "Slim")) DamageFx(0, 18, w, 6, frac);
            else DamageFx(8, 22, w - 16, 12, frac);
            return new SizeF(w, h);
        }

        // ---------------------------------------------------------------- feeds (notifications, kill feed, hype)
        float RowWidth(FeedItem f, float img, float size, bool hype)
        {
            float rw = 12 + (f.Avatar != null ? img + 6 : 0) + FeedTextW(f, size) + (f.Icon != null && Gfx.FileOk(f.Icon) ? img + 6 : 0);
            if (hype && cfg.HypeShowLevel && f.Level > 0) rw += Gfx.TextW("Lv " + f.Level, SMALL) + 12;
            return rw;
        }

        static float FeedTextW(FeedItem f, float size) { return f.Parts != null ? Gfx.PartsW(f.Parts, size) : Gfx.TextW(f.Text, size); }

        SizeF FeedList(bool draw, List<FeedItem> items, string panel, float rowH, float img, float size, bool hype)
        {
            bool des = DesignOn;
            string variant = hype || des ? "Card" : (cfg.NotifVariant ?? "Card");
            bool banner = Is(variant, "Banner"), pill = Is(variant, "Pill");
            float dpad = des ? DesignRowPad(rowH) + (Dz == "minimal" ? 8 : 0) : 0;
            float w = 0;
            foreach (FeedItem f in items) w = Math.Max(w, RowWidth(f, img, size, hype) + (pill ? 10 : 0) + dpad);
            w = Math.Min(Math.Max(w, banner ? 260 : 150), 520);
            float gap = banner ? 3 : 4;
            float h = items.Count * (rowH + gap);
            if (!draw) return new SizeF(w, h);
            string al = HAlign(panel);
            string anim = (cfg.FeedAnimation ?? "Slide").ToLowerInvariant();
            float y = 0;
            int idx = 0;
            foreach (FeedItem f in items)
            {
                idx++;
                float a = FeedAlpha(f);
                float rw = banner ? w : Math.Min(w, RowWidth(f, img, size, hype) + (pill ? 10 : 0) + dpad);
                string lv = hype && cfg.HypeShowLevel && f.Level > 0 ? "Lv " + f.Level : null;
                float x = al == "right" ? w - rw : (al == "center" ? (w - rw) / 2 : 0);
                float t = Math.Min(1f, (U.Now - f.Start) / 250f);
                float dy = 0;
                if (anim == "slide") x += (1 - t) * 30 * (al == "right" ? 1 : -1);
                else if (anim == "pop") { float e = 1 - t; dy = e * 14 - (float)Math.Sin(t * Math.PI) * 3; }
                float ry = y + dy;
                Color tc = hype ? f.Col : cTxt;
                float cpad = 0;
                if (des) cpad = DesignRowBg(x, ry, rw, rowH, a, idx, idx == items.Count);
                else if (banner)
                {
                    PanelBg(x, ry, rw, rowH, a);
                    Gfx.Rect(x, ry, rw, 2, U.WithAlpha(cAcc, (int)(255 * a)));
                }
                else if (pill)
                {
                    Gfx.Rect(x + rowH / 2, ry, rw - rowH, rowH, U.WithAlpha(cPan, (int)(Math.Max(cfg.Opacity, 180) * a)));
                    for (int k = 0; k < 4; k++)
                    {
                        float inset = (4 - k) * 1.6f;
                        Gfx.Rect(x + k * rowH / 8f, ry + inset, rowH / 8f + 0.5f, rowH - inset * 2, U.WithAlpha(cAcc, (int)(230 * a)));
                        Gfx.Rect(x + rw - (k + 1) * rowH / 8f, ry + inset, rowH / 8f + 0.5f, rowH - inset * 2, U.WithAlpha(cPan, (int)(Math.Max(cfg.Opacity, 180) * a)));
                    }
                    x += 6;
                }
                else PanelBg(x, ry, rw, rowH, a);
                if (hype)
                {
                    if (!des) Gfx.Border(x, ry, rw, rowH, 1, U.WithAlpha(cfg.HypeColor, (int)(220 * a)));
                    Gfx.Rect(x + cpad, ry + (des ? 4 : 0), 3, rowH - (des ? 8 : 0), U.WithAlpha(cfg.HypeColor, (int)(255 * a)));
                }
                float cx = x + 6 + cpad;
                if (banner) cx = x + (rw - RowWidth(f, img, size, hype)) / 2 + 6;
                if (f.Avatar != null) { Gfx.Image(f.Avatar, cx, ry + (rowH - img) / 2, img, img, (int)(255 * a)); cx += img + 6; }
                if (lv != null)
                {
                    float lw = Gfx.TextW(lv, SMALL) + 8;
                    Gfx.Rect(cx, ry + (rowH - 14) / 2, lw, 14, U.WithAlpha(cfg.HypeColor, (int)(230 * a)));
                    Gfx.Text(lv, cx + lw / 2, ry + (rowH - 14) / 2, SMALL * 0.9f, U.WithAlpha(Color.Black, (int)(255 * a)), Alignment.Center);
                    cx += lw + 4;
                }
                if (f.Parts != null) Gfx.Parts(f.Parts, cx, ry + (rowH - Gfx.LineH(size)) / 2, size, U.WithAlpha(tc, (int)(255 * a)));
                else Gfx.Text(f.Text, cx, ry + (rowH - Gfx.LineH(size)) / 2, size, U.WithAlpha(tc, (int)(255 * a)), Alignment.Left);
                cx += FeedTextW(f, size) + 6;
                if (f.Icon != null && Gfx.FileOk(f.Icon)) Gfx.Image(f.Icon, cx, ry + (rowH - img) / 2, img, img, (int)(255 * a));
                y += rowH + gap;
            }
            return new SizeF(w, h);
        }

        SizeF NotifPanel(bool draw) { return FeedList(draw, VAuto ? Tail(notifs, cfg.VMaxNotif) : notifs, "Notif", 30, 24, TXT, false); }
        SizeF FeedPanel(bool draw) { return FeedList(draw, VAuto ? Tail(feed, cfg.VMaxFeed) : feed, "Feed", 26, 20, TXT, false); }
        SizeF HypePanel(bool draw) { return FeedList(draw, VAuto ? Tail(hypes, cfg.VMaxHype) : hypes, "Hype", 38, 30, 0.38f, true); }

        // ---------------------------------------------------------------- overhead (real profile picture above the character)
        // point just above the head (follows the head when he falls / ragdolls); vehicles: above the roof
        Vector3 HeadTop(Ped p, float extra)
        {
            if (p.IsInVehicle()) return p.CurrentVehicle.Position + new Vector3(0, 0, 1.25f + extra);
            Vector3 h = Function.Call<Vector3>(Hash.GET_PED_BONE_COORDS, p, 31086, 0f, 0f, 0f);
            if (h == Vector3.Zero || h.DistanceTo(p.Position) > 3f) h = p.Position + new Vector3(0, 0, 0.7f);
            return h + new Vector3(0, 0, 0.18f + extra);
        }

        // the camera that is really rendering (story cams, TikTok zoom cam, or the game camera)
        Vector3 ViewPos()
        {
            if (cam != null && cam.Exists() && camMode.Length > 0) return cam.Position;
            if (zoomRendering && zcam != null && zcam.Exists()) return zcam.Position;
            return GameplayCamera.Position;
        }

        void Overheads()
        {
            if (!cfg.OverheadEnabled) return;
            Ped pl = Game.Player.Character;
            if (!pl.Exists()) return;
            Vector3 camPos = ViewPos();
            string ostyle = (cfg.OverheadStyle ?? "Classic").ToLowerInvariant();
            Gfx.Origin(0, 0, 1);
            foreach (Tracked t in tracked)
            {
                if (!t.Leader || t.DeadAt != 0 || t.Ped == null || !t.Ped.Exists()) continue;
                Vector3 wp = HeadTop(t.Ped, cfg.OverheadHeight);
                float dist = wp.DistanceTo(camPos);
                if (dist > 90f) continue;
                PointF sp = Screen.WorldToScreen(wp);
                if (sp.X == 0 && sp.Y == 0) continue;
                float s = U.Clamp(1.35f - dist / 70f, 0.5f, 1.25f);
                Color side = t.Enemy ? cLoss : cWin;
                bool isKing = king != null && t.Sup == king;
                Color ringC = isKing ? cfg.AuraKingColor : side;
                string av = Avatar(t.Sup);
                if (cfg.OverheadRing) av = avatars.Ring(av, ringC);
                int hp = Math.Max(0, t.Ped.Health - 100);
                float fr = U.Clamp(hp / (float)t.MaxHp, 0, 1);
                string name = t.Sup != null ? U.Trunc(t.Sup.Nick, 18) : "";
                float ts = TXT * s * cfg.FontScale;
                float lh = 40 * ts;

                if (ostyle == "card")
                {
                    float nw = Math.Max(60 * s, Gfx.TextW(name, TXT * s) + 10 * s);
                    float ch = lh + 12 * s, sz = 34 * s;
                    float cx = sp.X - (nw + sz) / 2 + sz, cy = sp.Y - ch;
                    Gfx.RectAbs(cx, cy, nw, ch, U.WithAlpha(cPan, 200));
                    Gfx.RectAbs(cx, cy, nw, 2 * s, side);
                    if (cfg.OverheadName) Gfx.TextAbs(name, cx + 5 * s, cy + 2 * s, ts, cTxt, Alignment.Left, Gfx.F);
                    if (cfg.OverheadHealth)
                    {
                        Gfx.RectAbs(cx + 5 * s, cy + ch - 7 * s, nw - 10 * s, 4 * s, U.WithAlpha(Color.Black, 170));
                        Gfx.RectAbs(cx + 5 * s, cy + ch - 7 * s, (nw - 10 * s) * fr, 4 * s, side);
                    }
                    if (cfg.OverheadAvatar) Gfx.ImageAbs(av, cx - sz - 2, cy + ch / 2 - sz / 2, sz, sz, 255);
                    continue;
                }
                float y = sp.Y;
                if (ostyle != "minimal" && cfg.OverheadHealth)
                {
                    float bw = 56 * s;
                    Gfx.RectAbs(sp.X - bw / 2 - 1, y - 7 * s, bw + 2, 5 * s + 2, U.WithAlpha(Color.Black, 170));
                    Gfx.RectAbs(sp.X - bw / 2, y - 6 * s, bw * fr, 5 * s, side);
                    y -= 9 * s;
                }
                if (ostyle != "minimal" && cfg.OverheadName && name.Length > 0)
                {
                    y -= lh + 2;
                    Gfx.TextAbs(name, sp.X, y, ts, side, Alignment.Center, Gfx.F);
                }
                if (cfg.OverheadAvatar)
                {
                    float sz = (ostyle == "minimal" ? 42 : (ostyle == "badge" ? 38 : 32)) * s;
                    y -= sz + 2;
                    Gfx.ImageAbs(av, sp.X - sz / 2, y, sz, sz, 255);
                    if (ostyle == "badge" && cfg.OverheadLevel && t.Sup != null && t.Sup.Level > 0)
                    {
                        string lv = t.Sup.Level.ToString(U.IC);
                        float bw2 = TextElement.GetStringWidth(lv, Gfx.F, SMALL * s) + 8 * s;
                        Gfx.RectAbs(sp.X + sz / 2 - bw2 + 4 * s, y + sz - 12 * s, bw2, 13 * s, cfg.HypeColor);
                        Gfx.TextAbs(lv, sp.X + sz / 2 - bw2 / 2 + 4 * s, y + sz - 13 * s, SMALL * s, Color.Black, Alignment.Center, Gfx.F);
                    }
                }
            }
        }

    }
}
