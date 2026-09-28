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
                : new string[] { "TopCenter", "TopLeft", "BottomCenter", "MiddleLeft", "BottomRight", "BottomLeft", "TopRight" };
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
        public string Aura = "Default";
        public string AuraColor = "";
        public int GiftCoins;

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
            it.Aura = ini.S(s, "Aura", "Default");
            it.AuraColor = ini.S(s, "AuraColor", "");
            it.GiftCoins = Math.Max(0, ini.I(s, "GiftCoins", 0));
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
        public bool StylePalette, OverheadRing, OverheadLevel;
        public string TextMode, UnicodeFont;
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
            c.HudStyle = ini.S("Hud", "Style", "Broadcast");
            c.StyleCycle = ini.L("Hud", "StyleCycle", "Broadcast,Cyber,Royal,Hologram,Gradient,Stream,Carbon,Classic,Glass,Neon,Minimal,Esports,Retro");
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
            c.ScoreVariant = ini.S("Hud", "ScoreVariant", "Classic");
            c.Top3Variant = ini.S("Hud", "Top3Variant", "List");
            c.HealthVariant = ini.S("Hud", "HealthVariant", "Bar");
            c.NotifVariant = ini.S("Hud", "NotifVariant", "Card");
            c.FeedAnimation = ini.S("Hud", "FeedAnimation", "Slide");
            c.OverheadStyle = ini.S("Hud", "OverheadStyle", "Classic");
            c.OverheadRing = ini.B("Hud", "OverheadRing", true);
            c.OverheadLevel = ini.B("Hud", "OverheadLevel", true);
            c.TextMode = ini.S("Hud", "TextMode", "Auto");
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
            Txt.Mode = cfg.TextMode;
            Txt.FontName = cfg.UnicodeFont;
            Txt.Bold = cfg.UnicodeBold;
            Txt.SizeFix = cfg.UnicodeSize;
            Txt.StripEmoji = cfg.StripEmoji;
            Txt.FancyToAscii = cfg.FancyToAscii;
            Txt.MaxTextures = cfg.MaxTextTextures;
            style = cfg.HudStyle;
            fontName = cfg.HudFont;
            vertical = string.Equals(cfg.LayoutMode, "Vertical", StringComparison.OrdinalIgnoreCase);
            ApplyKit(ini);
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
            try { UpdateRound(dt); } catch (Exception ex) { U.Error("Round", ex); }
            try { UpdateQueues(); } catch (Exception ex) { U.Error("Queue", ex); }
            try { UpdateTracked(); } catch (Exception ex) { U.Error("Tracked", ex); }
            try { UpdateEffects(dt); } catch (Exception ex) { U.Error("Effects", ex); }
            try { UpdateCamera(dt); } catch (Exception ex) { U.Error("Camera", ex); }
            try { UpdateAuras(); } catch (Exception ex) { U.Error("Aura", ex); }
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
                if (k == cfg.LiveTestKey && cfg.LiveTestEnabled) { ToggleLiveTest(); return; }
                if (k == cfg.StartKey) { StartSession(); return; }
                if (k == cfg.HudStyleKey) { style = Cycle(cfg.StyleCycle, style); Status("HUD: " + style); return; }
                if (k == cfg.HudFontKey) { fontName = Cycle(cfg.FontCycle, fontName); Status("Font: " + fontName); return; }
                if (KitActive && k == kitDesignKey) { kit.NextLayout(); return; }
                if (KitActive && k == kitStyleKey) { kit.NextStyle(); return; }
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
                KitGift(s, e);
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
            if (KitActive && it.Index == kitSwapIndex) kit.SwapUsed();

            if (IsHelp(it.Action)) s.RoundHelpCoins += Math.Max(coins, 1);
            if (IsEnemyAction(it.Action)) { lastEnemySup = s; lastEnemyAt = U.Now; }
            else if (IsRivalHelp(it.Action)) CheckRivalry(s);

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
            KitResult(win);
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

        // ================================================================ LiveHud designs (embedded kit, section [LiveHud])
        //  arena broadcast podium cards esports minimal classic  x  GOLD NEON FIRE ICE CLASSIC
        //  top supporters (coins) -> TOP board, queue -> NEXT board, round end -> result banner,
        //  big gifts -> MEGA GIFT, one interaction -> "gift spot" bottom-left.
        LiveHudKit.LiveHud kit;
        bool kitOn, kitReplaceTop, kitReplaceEnd, kitShowResult, kitMega;
        long kitMegaMin;
        int kitSwapIndex;
        string kitUnitIcon = "action", kitWinTitle = "", kitLossTitle = "";
        Keys kitDesignKey = Keys.None, kitStyleKey = Keys.None;
        long kitNextBoard;
        static readonly string[] KitStyles = { "GOLD", "NEON", "FIRE", "ICE", "CLASSIC" };

        void ApplyKit(Ini ini)
        {
            const string S = "LiveHud";
            kitOn = ini.B(S, "Enabled", true);
            kitReplaceTop = ini.B(S, "ReplaceTop3", true);
            kitReplaceEnd = ini.B(S, "ReplaceEndScreen", true);
            kitShowResult = ini.B(S, "ShowResult", true);
            kitMega = ini.B(S, "MegaEnabled", true);
            kitMegaMin = Math.Max(1, ini.I(S, "MegaMinCoins", 1000));
            kitUnitIcon = ini.S(S, "UnitIcon", "action");
            kitWinTitle = ini.S(S, "WinTitle", "فوز!");
            kitLossTitle = ini.S(S, "LossTitle", "خسارة!");
            kitDesignKey = ini.K(S, "DesignKey", "F10");
            kitStyleKey = ini.K(S, "StyleKey", "F5");
            kitSwapIndex = ini.I(S, "GiftSpotInteraction", 1);
            if (!kitOn) return;
            try
            {
                if (kit == null)
                {
                    kit = new LiveHudKit.LiveHud(Path.Combine(U.DataDir, "livehud"));
                    kit.OnLog = delegate(string t) { U.Log("LiveHud: " + t); };
                }
                LiveHudKit.LiveHud.Config k = kit.cfg;
                k.Design = ini.S(S, "Design", "arena").ToLowerInvariant();
                int st = Array.IndexOf(KitStyles, ini.S(S, "Style", "GOLD").ToUpperInvariant());
                k.HudStyle = st < 0 ? 0 : st;
                k.HudFont = ini.S(S, "Font", "default").ToLowerInvariant();
                k.HudFontCustom = ini.S(S, "FontCustom", "");
                k.AvShape = ini.S(S, "AvatarShape", "auto").ToLowerInvariant();
                k.PanelKind = ini.S(S, "Panel", "auto").ToLowerInvariant();
                k.HudScale = U.Clamp(ini.F(S, "Scale", 1f), 0.3f, 3f);
                k.TikTokScale = U.Clamp(ini.F(S, "TikTokScale", 0.9f), 0.3f, 3f);
                k.PanelAlpha = U.Clamp(ini.I(S, "PanelAlpha", 190), 0, 255);
                k.Accent = ini.C(S, "Accent", "#f5b301");
                k.ShowTop = ini.B(S, "ShowTop", true);
                k.PodiumPulse = ini.B(S, "PodiumPulse", true);
                k.TopCount = U.Clamp(ini.I(S, "TopCount", 3), 1, 3);
                k.TopY = ini.F(S, "TopY", 170f);
                k.TopTitle = ini.S(S, "TopTitle", "أفضل الداعمين");
                k.GoalsWord = ini.S(S, "CoinsWord", "كوينز");
                k.GoalWord = ini.S(S, "CoinWord", "كوين");
                k.ShowBoard = ini.B(S, "ShowBoard", true);
                k.BoardSide = ini.S(S, "BoardSide", "right").ToLowerInvariant();
                k.BoardTitle = ini.S(S, "BoardTitle", "الطابور");
                k.EmptyText = ini.S(S, "EmptyText", "صيفط هدية باش تدخل!");
                k.BoardRows = U.Clamp(ini.I(S, "BoardRows", 5), 1, 12);
                k.ListY = ini.F(S, "ListY", 300f);
                k.ListW = ini.F(S, "ListW", 420f);
                k.ShowSwap = ini.B(S, "ShowGiftSpot", false);
                k.SwapWord = ini.S(S, "GiftSpotWord", "GIFT");
                k.SwapTextPos = "below";
                k.ShowSwapName = true;
                k.SwapX = ini.F(S, "GiftSpotX", 130f);
                k.SwapBottom = ini.F(S, "GiftSpotBottom", 150f);
                k.SwapSize = ini.F(S, "GiftSpotSize", 110f);
                k.ShowResult = kitShowResult;
                k.ResultStyle = ini.S(S, "ResultStyle", "design").ToLowerInvariant();
                k.ResultY = ini.F(S, "ResultY", 360f);
                k.BigTitle = ini.S(S, "MegaTitle", "MEGA GIFT!");
                k.BigColor = ini.C(S, "MegaColor", "#ffd23c");
                k.BigMs = U.Clamp(ini.I(S, "MegaMs", 6500), 1000, 30000);
                k.HudMode = vertical ? "tiktok" : "wide";
                kit.SwapIcon = null;
                k.SwapGiftName = "";
                foreach (Interaction it in cfg.Interactions)
                    if (it.Index == kitSwapIndex) { kit.SwapIcon = ImgPath(it.GiftImage); k.SwapGiftName = it.Trigger.Equals("Gift", StringComparison.OrdinalIgnoreCase) ? it.GiftName : it.Title; }
                kit.Apply();
            }
            catch (Exception ex) { U.Error("LiveHud", ex); kitOn = false; }
        }

        bool KitActive { get { return kitOn && kit != null; } }

        LiveHudKit.LiveHud.AvatarInfo KitAvatar(Supporter s)
        {
            if (!KitActive || s == null) return null;
            return kit.Avatar(s.Key, string.Join("\n", s.AvatarUrls.ToArray()));
        }

        string KitAlt(Supporter s) { return s.Sim || s.Key.StartsWith("test:") ? s.Nick : "@" + s.Key; }

        void KitGift(Supporter s, LiveEvent e)
        {
            if (!KitActive || e.Coins <= 0) return;
            try
            {
                kit.AddGoal(s.Key, s.Nick, KitAlt(s), string.Join("\n", s.AvatarUrls.ToArray()), (int)Math.Min(e.Coins, 1000000));
                if (kitMega && e.Coins >= kitMegaMin) kit.ShowMegaGift(s.Nick, KitAlt(s), KitAvatar(s), e.GiftName, e.Count, e.Coins);
            }
            catch (Exception ex) { U.Error("LiveHud", ex); }
        }

        void KitResult(bool win)
        {
            if (!KitActive || !kitShowResult) return;
            try { kit.ShowResult(win ? "goal" : "miss", win ? kitWinTitle : kitLossTitle, mvp != null ? mvpReason : "", mvp != null ? mvp.Nick : "", KitAvatar(mvp)); }
            catch (Exception ex) { U.Error("LiveHud", ex); }
        }

        string KitIcon(Interaction it)
        {
            if (it == null) return null;
            string m = (kitUnitIcon ?? "action").ToLowerInvariant();
            if (m == "ball") return null;
            if (m == "gift") return ImgPath(it.GiftImage);
            if (m == "action") return ImgPath(it.ActionImage);
            return ImgPath(kitUnitIcon);
        }

        // queue -> "NEXT" board: one row per supporter (in order), one unit per ball
        void KitBoard()
        {
            List<LiveHudKit.LiveHud.Supporter> list = new List<LiveHudKit.LiveHud.Supporter>();
            LiveHudKit.LiveHud.Supporter cur = null;
            Job first = null;
            foreach (List<Job> q in new List<Job>[] { spawnQ, instantQ })
            {
                foreach (Job j in q)
                {
                    if (list.Count >= 30) break;
                    if (first == null) first = j;
                    string key = j.Sup != null ? j.Sup.Key : "?";
                    if (cur == null || cur.Key != key)
                    {
                        cur = new LiveHudKit.LiveHud.Supporter();
                        cur.Key = key;
                        cur.DisplayName = j.Sup != null ? j.Sup.Nick : "?";
                        cur.Alt = j.Sup != null ? KitAlt(j.Sup) : "";
                        cur.PicUrl = j.Sup != null ? string.Join("\n", j.Sup.AvatarUrls.ToArray()) : "";
                        cur.Avatar = KitAvatar(j.Sup);
                        list.Add(cur);
                    }
                    for (int n = 0; n < j.Left && cur.Balls.Count < 40; n++) cur.Balls.Add(j.It.Action);
                }
            }
            kit.Waiting = list;
            kit.ShootingKey = list.Count > 0 ? list[0].Key : null;
            kit.QueueOverride = QueueCount();
            kit.BallIcon = first != null ? KitIcon(first.It) : null;
        }

        void DrawKit()
        {
            if (!KitActive) return;
            string mode = vertical ? "tiktok" : "wide";
            if (kit.cfg.HudMode != mode) { kit.cfg.HudMode = mode; kit.Apply(); }
            if (U.Now >= kitNextBoard) { kitNextBoard = U.Now + 250; KitBoard(); }
            kit.Draw();
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
            { "Carbon",    new string[] { "#ff7a00", "#121212", "#f2f2f2", "#7cff6b", "#ff3b3b" } }
        };

        void ApplyPalette()
        {
            cAcc = cfg.Accent; cPan = cfg.PanelColor; cTxt = cfg.TextColor; cWin = cfg.WinColor; cLoss = cfg.LossColor;
            string[] p;
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
            switch (St)
            {
                case "esports":
                case "broadcast":
                    Gfx.Rect(x, y, w, h, cAcc);
                    Gfx.Text(text, x + w / 2, y + 2, TXT, St == "broadcast" ? Color.White : cPan, Alignment.Center);
                    break;
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
                fScore = ScorePanel; fTop = TopPanel; fHealth = HealthPanel; fGuide = GuidePanel;
                fNotif = NotifPanel; fFeed = FeedPanel; fHype = HypePanel;
            }

            PruneFeeds();
            Overheads();
            Place("Score", cfg.ScoreEnabled, fScore);
            Place("Top3", cfg.Top3Enabled && !(KitActive && kitReplaceTop && kit.cfg.ShowTop), fTop);
            Place("Health", cfg.HealthEnabled, fHealth);
            Place("Guide", cfg.GuideEnabled, fGuide);
            Place("Notif", cfg.NotifEnabled && notifs.Count > 0, fNotif);
            Place("Feed", cfg.FeedEnabled && feed.Count > 0, fFeed);
            Place("Hype", cfg.HypeEnabled && hypes.Count > 0, fHype);
            DrawKit();
            if (!(KitActive && kitReplaceEnd && kitShowResult)) EndScreen();
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

        SizeF ScorePanel(bool draw)
        {
            string v = cfg.ScoreVariant ?? "Classic";
            bool title = cfg.TitleEnabled && cfg.Title.Length > 0;
            bool timer = cfg.ShowTimer && cfg.ChallengeEnabled;
            bool showStreak = cfg.ShowStreak && Math.Abs(streak) >= 2;
            if (Is(v, "Bar"))
            {
                float w = 380, h = 38 + (title ? 22 : 0) + (showStreak ? 18 : 0);
                if (!draw) return new SizeF(w, h);
                PanelBg(0, 0, w, h, 1);
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
                PanelBg(0, 0, w, h, 1);
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
                PanelBg(0, 0, w, h, 1);
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
                else if (k == "counters" && cfg.ShowCounters) rows.Add(k);
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
                hpMax = Math.Max(1, Function.Call<int>(Hash.GET_ENTITY_MAX_HEALTH, pl) - 100);
                hpNow = Math.Max(0, Function.Call<int>(Hash.GET_ENTITY_HEALTH, pl) - 100);
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
                    Gfx.Rect(gw, y, w - gw, rowH, U.WithAlpha(U.Mix(cPan, cfg.GuideActionColor, 0.25f), cfg.Opacity));
                    Gfx.Rect(gw, y, 3, rowH, cfg.GuideActionColor);
                    if (St == "cyber" || St == "hologram") Brackets(0, y, w, rowH, 6, 1, U.WithAlpha(cAcc, 200));
                    if (St == "royal") Gfx.Border(0, y, w, rowH, 1, U.WithAlpha(cAcc, 200));
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
            string variant = hype ? "Card" : (cfg.NotifVariant ?? "Card");
            bool banner = Is(variant, "Banner"), pill = Is(variant, "Pill");
            float w = 0;
            foreach (FeedItem f in items) w = Math.Max(w, RowWidth(f, img, size, hype) + (pill ? 10 : 0));
            w = Math.Min(Math.Max(w, banner ? 260 : 150), 520);
            float gap = banner ? 3 : 4;
            float h = items.Count * (rowH + gap);
            if (!draw) return new SizeF(w, h);
            string al = HAlign(panel);
            string anim = (cfg.FeedAnimation ?? "Slide").ToLowerInvariant();
            float y = 0;
            foreach (FeedItem f in items)
            {
                float a = FeedAlpha(f);
                float rw = banner ? w : Math.Min(w, RowWidth(f, img, size, hype) + (pill ? 10 : 0));
                string lv = hype && cfg.HypeShowLevel && f.Level > 0 ? "Lv " + f.Level : null;
                float x = al == "right" ? w - rw : (al == "center" ? (w - rw) / 2 : 0);
                float t = Math.Min(1f, (U.Now - f.Start) / 250f);
                float dy = 0;
                if (anim == "slide") x += (1 - t) * 30 * (al == "right" ? 1 : -1);
                else if (anim == "pop") { float e = 1 - t; dy = e * 14 - (float)Math.Sin(t * Math.PI) * 3; }
                float ry = y + dy;
                Color tc = hype ? f.Col : cTxt;
                if (banner)
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
                    Gfx.Border(x, ry, rw, rowH, 1, U.WithAlpha(cfg.HypeColor, (int)(220 * a)));
                    Gfx.Rect(x, ry, 3, rowH, U.WithAlpha(cfg.HypeColor, (int)(255 * a)));
                }
                float cx = x + 6;
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

        SizeF NotifPanel(bool draw) { return FeedList(draw, notifs, "Notif", 30, 24, TXT, false); }
        SizeF FeedPanel(bool draw) { return FeedList(draw, feed, "Feed", 26, 20, TXT, false); }
        SizeF HypePanel(bool draw) { return FeedList(draw, hypes, "Hype", 38, 30, 0.38f, true); }

        // ---------------------------------------------------------------- overhead (real profile picture above the character)
        void Overheads()
        {
            if (!cfg.OverheadEnabled) return;
            Ped pl = Game.Player.Character;
            if (!pl.Exists()) return;
            Vector3 camPos = GameplayCamera.Position;
            if (cam != null && cam.Exists() && camMode.Length > 0) camPos = cam.Position;
            string ostyle = (cfg.OverheadStyle ?? "Classic").ToLowerInvariant();
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

        // ---------------------------------------------------------------- end screen
        void EndScreen()
        {
            if (phase != Phase.Ended || !cfg.EndScreenEnabled || U.Now >= endScreenUntil) return;
            Gfx.Origin(0, 0, 1);
            float cx = frameX + frameW / 2;
            Color c = lastWin ? cWin : cLoss;
            float pulse = 1f + 0.04f * (float)Math.Sin(U.Now / 180.0);
            Gfx.RectAbs(frameX, 190, frameW, 110, U.WithAlpha(cPan, 170));
            Gfx.RectAbs(frameX, 190, frameW, 3, c);
            Gfx.RectAbs(frameX, 297, frameW, 3, c);
            Gfx.TextAbs(lastWin ? cfg.Tx("WinText", "WIN") : cfg.Tx("LossText", "LOSS"), cx, 205, 1.5f * pulse * cfg.FontScale, c, Alignment.Center, Gfx.F);
            if (cfg.MvpEnabled && mvp != null)
            {
                float y = 320;
                float bw = Math.Min(frameW - 20, 360);
                Gfx.Origin(cx - bw / 2, y, 1);
                PanelBg(0, 0, bw, 96, 1);
                Gfx.Origin(0, 0, 1);
                Gfx.ImageAbs(avatars.Ring(Avatar(mvp), cfg.HypeColor), cx - bw / 2 + 12, y + 12, 72, 72, 255);
                float tx = cx - bw / 2 + 96;
                Gfx.TextAbs(cfg.Tx("MvpText", "MVP"), tx, y + 8, 0.5f * cfg.FontScale, cfg.HypeColor, Alignment.Left, Gfx.F);
                Gfx.TextAbs(U.Trunc(mvp.Nick, 20), tx, y + 36, 0.42f * cfg.FontScale, cTxt, Alignment.Left, Gfx.F);
                Gfx.TextAbs(mvpReason, tx, y + 62, 0.3f * cfg.FontScale, U.WithAlpha(cTxt, 200), Alignment.Left, Gfx.F);
            }
        }
    }
}

// =====================================================================
//  LiveHud kit (designs: arena broadcast podium cards esports minimal classic)
//  embedded in TikArena. Changes vs the original kit:
//    - avatar urls may be a list separated by '\n' (tried in order), Referer header,
//      TikArena.Wic fallback decoder, letter avatars retried when a real url arrives
//    - BallIcon: optional picture drawn instead of the soccer ball (queue units)
// =====================================================================
namespace LiveHudKit
{
using System.Drawing.Text;
using System.Reflection;
using System.Text.RegularExpressions;

public class LiveHud
{
    // ------------------------------------------------------------------
    //  Data types
    // ------------------------------------------------------------------
    /// <summary>A profile picture (downloaded + cut in circle / rounded / square / hex on a worker thread).</summary>
    public class AvatarInfo
    {
        public volatile bool Ready;
        public volatile string File;
        public volatile string Base;       // Base + "_circle.png" / "_rounded.png" / "_square.png" / "_hex.png"
        public volatile bool Letter;       // no picture yet (coloured letter)
    }

    /// <summary>A text rendered to PNG (Arabic names, titles...).</summary>
    public class NameTag
    {
        public volatile bool Ready;
        public volatile string File;
        public float Aspect = 1f;
    }

    /// <summary>One line of the TOP SCORERS board.</summary>
    public class Scorer
    {
        public string Key, Name, Alt, PicUrl;     // Key = unique id, Name = nickname (any language), Alt = "@username"
        public AvatarInfo Avatar;
        public int Goals, ReachedAt, FlashUntil;
    }

    /// <summary>One line of the WAITING LIST ("NEXT SHOTS"). Balls = one entry per ball (any text).</summary>
    public class Supporter
    {
        public string Key, DisplayName, Alt, PicUrl;
        public AvatarInfo Avatar;
        public List<string> Balls = new List<string>();
        public int Arrival;
    }

    // ------------------------------------------------------------------
    //  Settings (change them any time, e.g. from your own .ini)
    // ------------------------------------------------------------------
    public class Config
    {
        public string Design = "arena";              // arena broadcast podium cards esports minimal classic
        public int HudStyle = 0;                     // 0 GOLD 1 NEON 2 FIRE 3 ICE 4 CLASSIC
        public string AvShape = "auto";              // auto circle rounded square hex
        public string PanelKind = "auto";            // auto dark glass light
        public float HudScale = 1f;
        public int PanelAlpha = 190;
        public Color Accent = Color.FromArgb(245, 179, 1);
        public string HudMode = "wide";              // wide / tiktok
        public bool Vertical = false;                // set by the mode
        public float TikTokScale = 0.9f;
        public bool TikTokGuide = false;             // white lines on the edges of the TikTok area
        public string HudFont = "default";           // default sport gta modern elegant mono fun custom
        public string HudFontCustom = "";            // Windows font names for "custom", e.g. "Montserrat Black"
        // top scorers
        public bool ShowTop = true, PodiumPulse = false;
        public int TopCount = 3;
        public float TopY = 30f;
        public string TopTitle = "TOP SCORERS", GoalsWord = "GOALS", GoalWord = "GOAL";
        // waiting list
        public bool ShowBoard = true;
        public string BoardSide = "right", BoardTitle = "NEXT SHOTS", EmptyText = "SEND A GIFT TO SHOOT!";
        public int BoardRows = 6;
        public float ListY = 230f, ListW = 420f;
        // swap gift (bottom left)
        public bool ShowSwap = true, ShowSwapName = true;
        public float SwapX = 130f, SwapBottom = 150f, SwapSize = 110f, TargetSpeed = 2.2f;
        public string SwapWord = "SWAP", SwapTextPos = "below", SwapGiftName = "";
        // result banner
        public bool ShowResult = true;
        public string ResultStyle = "design";        // design / gta (GTA's big "wasted" style message)
        public float ResultY = 360f;
        public int RapidSummaryMs = 4000;            // how long a "series" banner stays
        // mega gift
        public int BigMs = 6500;
        public string BigTitle = "MEGA GIFT!";
        public Color BigColor = Color.FromArgb(255, 210, 60);
        public float BigY = 470f;
    }

    public Config cfg = new Config();

    // ------------------------------------------------------------------
    //  Public data + API
    // ------------------------------------------------------------------
    /// <summary>Waiting list shown on the side board (first = shooting now). Set it when it changes.</summary>
    public List<Supporter> Waiting = new List<Supporter>();
    /// <summary>Key of the supporter shooting now (highlighted on the board). null = nobody.</summary>
    public string ShootingKey;
    /// <summary>Picture (png file path) of the SWAP gift drawn bottom-left. null = a blue circle.</summary>
    public string SwapIcon;
    /// <summary>Balls waiting in total (shown on the board header). Default = sum of Waiting balls.</summary>
    public int QueueOverride = -1;
    int QueueCount { get { if (QueueOverride >= 0) return QueueOverride; int n = 0; List<Supporter> w = Waiting; if (w != null) foreach (Supporter s in w) n += s.Balls.Count; return n; } }
    /// <summary>Goals of the last "series" (colours the series banner: >0 gold, 0 red).</summary>
    public int seriesLastGoals;
    /// <summary>Optional: called when the player changes design/style/font/mode with your keys -> save it in your ini.</summary>
    public Action<string, string, string> SaveSetting = delegate { };
    /// <summary>Optional: errors (write them to your log file).</summary>
    public Action<string> OnLog = delegate { };

    readonly Random rng = new Random();
    readonly string dataDir, cacheDir, tagDir, iconCacheDir;
    string ballFile;
    readonly Dictionary<string, CustomSprite> sprites = new Dictionary<string, CustomSprite>();
    readonly ConcurrentDictionary<string, AvatarInfo> avatars = new ConcurrentDictionary<string, AvatarInfo>();
    readonly ConcurrentDictionary<string, NameTag> tags = new ConcurrentDictionary<string, NameTag>();
    readonly Dictionary<string, Scorer> scorers = new Dictionary<string, Scorer>();
    List<Scorer> topList = new List<Scorer>();
    volatile int hudStyle;
    Scaleform bigMsg;
    string bigTitle = "", bigSub = "";
    bool bigPending;
    int bigUntil;

    /// <param name="folder">a folder for the cache (photos, rendered names, shapes), e.g. scripts\MyGame</param>
    public LiveHud(string folder)
    {
        dataDir = folder;
        cacheDir = Path.Combine(folder, "avatars");
        tagDir = Path.Combine(folder, "names");
        iconCacheDir = Path.Combine(folder, "cache");
        foreach (string d in new[] { dataDir, cacheDir, tagDir, iconCacheDir }) { try { Directory.CreateDirectory(d); } catch { } }
        try { foreach (string f in Directory.GetFiles(tagDir, "*.png")) File.Delete(f); } catch { }   // names are rendered again each session
        try { MakeUiKit(); } catch (Exception ex) { Log(ex); }
        try { ballFile = Path.Combine(iconCacheDir, "ball.png"); RenderBall(ballFile); } catch (Exception ex) { Log(ex); ballFile = null; }
        Apply();
    }

    /// <summary>Call after you change cfg (design, style, mode, font...).</summary>
    public void Apply()
    {
        hudStyle = Math.Max(0, Math.Min(StyleNames.Length - 1, cfg.HudStyle));
        cfg.HudMode = (cfg.HudMode ?? "wide").ToLowerInvariant() == "tiktok" ? "tiktok" : "wide";
        cfg.Vertical = cfg.HudMode == "tiktok";
        ApplyHudMode();
        ApplyHudFont();
    }

    /// <summary>Downloads + prepares a profile picture (TikTok webp/jpg/png). Safe to call from any thread.</summary>
    public AvatarInfo Avatar(string userId, string pictureUrl) { return GetAvatar(userId, pictureUrl); }

    /// <summary>Adds goals to a supporter and updates the TOP SCORERS board (call on the game thread).</summary>
    public void AddGoal(string userId, string displayName, string username, string pictureUrl) { AddGoal(userId, displayName, username, pictureUrl, 1); }
    public void AddGoal(string userId, string displayName, string username, string pictureUrl, int goals)
    {
        if (string.IsNullOrEmpty(userId)) return;
        Scorer sc;
        if (!scorers.TryGetValue(userId, out sc))
        {
            sc = new Scorer();
            sc.Key = userId;
            scorers[userId] = sc;
        }
        sc.Name = CleanName(displayName);
        sc.Alt = CleanName(string.IsNullOrEmpty(username) ? displayName : username);
        if (!string.IsNullOrEmpty(pictureUrl)) sc.PicUrl = pictureUrl;
        sc.Avatar = GetAvatar(userId, sc.PicUrl ?? "");
        if (!Renderable(sc.Name)) GetTag(sc.Name);
        sc.Goals += goals;
        sc.ReachedAt = Game.GameTime;
        sc.FlashUntil = Game.GameTime + 1600;
        RebuildTop();
    }
    /// <summary>The whole ranking at once (e.g. loaded from a file).</summary>
    public void SetScorers(IEnumerable<Scorer> list)
    {
        scorers.Clear();
        foreach (Scorer s in list) if (s != null && !string.IsNullOrEmpty(s.Key)) scorers[s.Key] = s;
        RebuildTop();
    }
    public void ResetScores() { scorers.Clear(); RebuildTop(); }
    /// <summary>Current top 5 (best first).</summary>
    public List<Scorer> Top { get { return topList; } }

    void RebuildTop()
    {
        List<Scorer> all = new List<Scorer>(scorers.Values);
        all.Sort(delegate (Scorer a, Scorer b)
        {
            int c = b.Goals.CompareTo(a.Goals);
            return c != 0 ? c : a.ReachedAt.CompareTo(b.ReachedAt);
        });
        if (all.Count > 5) all.RemoveRange(5, all.Count - 5);
        topList = all;
    }

    /// <summary>Banner in the middle. kind = goal / save / miss / swap / series.</summary>
    public void ShowResult(string kind, string title, string sub, string who, AvatarInfo avatar) { SetResult(kind, title, sub, who, avatar); }
    /// <summary>The SWAP gift was used: it jumps.</summary>
    public void SwapUsed() { swapFlash = Game.GameTime; }
    /// <summary>Big gift celebration (queued, one after the other).</summary>
    public void ShowMegaGift(string name, string username, AvatarInfo avatar, string giftName, int count, long coins)
    {
        BigGiftEv e = new BigGiftEv();
        e.Name = CleanName(name); e.Alt = CleanName(username); e.Av = avatar; e.Gift = CleanName(giftName); e.Count = Math.Max(1, count); e.Coins = coins;
        if (!Renderable(e.Name)) GetTag(e.Name);
        e.Seed = (int)(DateTime.Now.Ticks & 0x7FFFFFFF);
        if (bigIn.Count < 10) bigIn.Enqueue(e);
    }
    public void SetDesign(string name) { if (Array.IndexOf(DesignNames, (name ?? "").ToLowerInvariant()) >= 0) { cfg.Design = name.ToLowerInvariant(); SaveSetting("HUD", "Design", cfg.Design); } }
    public void NextStyle() { SetHudStyle(hudStyle + 1); }
    public void NextFont() { NextHudFont(); }
    public void ToggleMode() { ToggleHudMode(); }
    public void NextLayout() { NextDesign(); }

    void Log(Exception ex) { try { OnLog(ex.ToString()); } catch { } }
    string SwapLabel() { return cfg.SwapGiftName ?? ""; }

    // ------------------------------------------------------------------
    //  Colour styles
    // ------------------------------------------------------------------
    static readonly string[] StyleNames = { "GOLD", "NEON", "FIRE", "ICE", "CLASSIC" };
    static Color C3(int r, int g, int b) { return Color.FromArgb(r, g, b); }
    static readonly Color[][][] StyleMedals =
    {
        new[] { new[] { C3(255,244,170), C3(245,190,40), C3(140,95,0) },  new[] { C3(252,253,255), C3(192,198,206), C3(100,108,118) }, new[] { C3(255,205,160), C3(205,127,50), C3(105,55,18) } },
        new[] { new[] { C3(255,170,255), C3(215,60,255), C3(80,0,140) },  new[] { C3(170,255,255), C3(0,215,255), C3(0,80,130) },     new[] { C3(180,255,200), C3(40,225,120), C3(0,100,50) } },
        new[] { new[] { C3(255,245,150), C3(255,140,0), C3(150,20,0) },   new[] { C3(255,205,130), C3(240,95,30), C3(120,30,0) },     new[] { C3(255,160,140), C3(215,40,40), C3(90,0,0) } },
        new[] { new[] { C3(255,255,255), C3(150,215,255), C3(40,110,190) }, new[] { C3(240,250,255), C3(175,228,242), C3(70,130,160) }, new[] { C3(225,238,255), C3(125,165,225), C3(40,70,130) } },
        new[] { new[] { C3(255,244,170), C3(245,190,40), C3(140,95,0) },  new[] { C3(252,253,255), C3(192,198,206), C3(100,108,118) }, new[] { C3(255,205,160), C3(205,127,50), C3(105,55,18) } }
    };
    static readonly Color[][] StyleText =
    {
        new[] { C3(255,205,60), C3(215,220,228), C3(225,150,80) },
        new[] { C3(240,120,255), C3(80,230,255), C3(90,240,150) },
        new[] { C3(255,200,60), C3(255,140,60), C3(255,90,80) },
        new[] { C3(210,240,255), C3(170,225,245), C3(140,180,235) },
        new[] { C3(255,205,60), C3(215,220,228), C3(225,150,80) }
    };
    Color Accent
    {
        get
        {
            switch (hudStyle)
            {
                case 1: return C3(0, 229, 255);
                case 2: return C3(255, 106, 0);
                case 3: return C3(127, 216, 255);
                default: return cfg.Accent;
            }
        }
    }


    // =====================================================================
    //  HUD  -  drawn inside GTA, wide screen 1920 x 1080 (16:9)
    //  7 designs, each with its own shapes and arrangement:
    //    arena  broadcast  podium  cards  esports  minimal  classic
    //  Sizes and positions below are 1920x1080 pixels (converted to GTA's 1280x720 HUD space).
    //  Shapes come from a small kit of white pictures made once (corners, triangles, circle,
    //  hexagon, shield, glow) tinted with the colours, so everything stays sharp and fixed.
    // =====================================================================
    const float U = 1280f / 1920f;
    static readonly string[] DesignNames = { "arena", "broadcast", "podium", "cards", "esports", "minimal", "classic" };
    static readonly string[] DesignShape = { "circle", "square", "rounded", "rounded", "hex", "circle", "circle" };
    static readonly string[] DesignPanel = { "dark", "dark", "glass", "dark", "dark", "glass", "dark" };
    static readonly Color KGoal = Color.FromArgb(39, 217, 107), KSave = Color.FromArgb(255, 179, 0), KMiss = Color.FromArgb(255, 59, 59), KSwap = Color.FromArgb(31, 143, 255);
    static readonly Color Ink = Color.FromArgb(28, 22, 8);
    const GTA.UI.Alignment AL = GTA.UI.Alignment.Left, AC = GTA.UI.Alignment.Center, AR = GTA.UI.Alignment.Right;
    static GTA.UI.Font FL = GTA.UI.Font.ChaletLondon, FC = GTA.UI.Font.ChaletComprimeCologne, FP = GTA.UI.Font.Pricedown;

    // ---- HUD FONTS ([HUD] Font, F12 in the game = next one) ----
    // only the letters of the HUD change: GTA's own text + the text pictures (titles, names, numbers)
    static readonly string[] FontNames = { "default", "sport", "gta", "modern", "elegant", "mono", "fun", "custom" };
    static string[] FontTitleFams = { "Impact", "Arial Black", "Segoe UI Black", "Segoe UI" };
    static string[] FontTextFams = { "Segoe UI Black", "Arial Black", "Segoe UI", "Tahoma", "Arial" };
    static volatile int hudFont;
    void ApplyHudFont()
    {
        int i = Array.IndexOf(FontNames, (cfg.HudFont ?? "default").Trim().ToLowerInvariant());
        if (i < 0) i = 0;
        GTA.UI.Font fc = GTA.UI.Font.ChaletComprimeCologne, fl = GTA.UI.Font.ChaletLondon, fp = GTA.UI.Font.Pricedown;
        string[] tt, tx;
        switch (i)
        {
            case 1: tt = new[] { "Bahnschrift", "Impact" }; tx = new[] { "Bahnschrift", "Segoe UI" }; fl = GTA.UI.Font.ChaletComprimeCologne; break;
            case 2: tt = new[] { "Impact" }; tx = new[] { "Arial Black", "Impact" }; fc = GTA.UI.Font.Pricedown; break;
            case 3: tt = new[] { "Segoe UI Black", "Segoe UI" }; tx = new[] { "Segoe UI Semibold", "Segoe UI" }; fc = GTA.UI.Font.ChaletLondon; break;
            case 4: tt = new[] { "Georgia", "Times New Roman" }; tx = new[] { "Georgia", "Times New Roman" }; fc = GTA.UI.Font.HouseScript; fl = GTA.UI.Font.HouseScript; fp = GTA.UI.Font.HouseScript; break;
            case 5: tt = new[] { "Consolas", "Courier New" }; tx = new[] { "Consolas", "Courier New" }; fc = GTA.UI.Font.Monospace; fl = GTA.UI.Font.Monospace; fp = GTA.UI.Font.Monospace; break;
            case 6: tt = new[] { "Comic Sans MS", "Segoe Print" }; tx = new[] { "Comic Sans MS", "Segoe UI" }; fc = GTA.UI.Font.ChaletLondon; break;
            case 7:
            {
                List<string> fams = SplitList(cfg.HudFontCustom);
                fams.Add("Segoe UI");
                tt = fams.ToArray(); tx = fams.ToArray();
                break;
            }
            default: tt = new[] { "Impact", "Arial Black", "Segoe UI Black", "Segoe UI" }; tx = new[] { "Segoe UI Black", "Arial Black", "Segoe UI", "Tahoma", "Arial" }; break;
        }
        FC = fc; FL = fl; FP = fp;
        FontTitleFams = tt; FontTextFams = tx;
        hudFont = i;
    }
    void NextHudFont()
    {
        int i = (hudFont + 1) % FontNames.Length;
        if (FontNames[i] == "custom" && string.IsNullOrEmpty(cfg.HudFontCustom)) i = 0;
        cfg.HudFont = FontNames[i];
        ApplyHudFont();
        try { SaveSetting("HUD", "Font", cfg.HudFont); } catch (Exception ex) { Log(ex); }
        Notification.Show("~b~HUD font~s~: " + cfg.HudFont.ToUpperInvariant());
    }

    string uiDir;
    readonly Dictionary<string, string> gradImgs = new Dictionary<string, string>();
    readonly Dictionary<string, float> textWidths = new Dictionary<string, float>();

    int DesignIndex { get { int i = Array.IndexOf(DesignNames, (cfg.Design ?? "").ToLowerInvariant()); return i < 0 ? 0 : i; } }
    string Design { get { return DesignNames[DesignIndex]; } }
    string AvShape
    {
        get { string s = cfg.AvShape; return (s == "circle" || s == "rounded" || s == "square" || s == "hex") ? s : DesignShape[DesignIndex]; }
    }
    string PanelKind
    {
        get { string p = cfg.PanelKind; return (p == "dark" || p == "glass" || p == "light") ? p : DesignPanel[DesignIndex]; }
    }
    float Sc { get { return cfg.HudScale * (cfg.HudMode == "tiktok" ? cfg.TikTokScale : 1f); } }

    // ---------- palette of the boxes (dark / glass / light) ----------
    Color PBg, PRow, PText, PMuted;
    bool PLight;
    void UpdatePalette()
    {
        int a = cfg.PanelAlpha;
        switch (PanelKind)
        {
            case "glass":
                PBg = Color.FromArgb(Math.Max(40, Math.Min(140, a / 2 + 20)), 255, 255, 255); PRow = Color.FromArgb(60, 255, 255, 255);
                PText = Color.White; PMuted = Color.FromArgb(235, 238, 244); PLight = false; break;
            case "light":
                PBg = Color.FromArgb(Math.Max(210, a), 248, 249, 252); PRow = Color.FromArgb(Math.Max(210, a), 232, 235, 242);
                PText = Color.FromArgb(22, 24, 30); PMuted = Color.FromArgb(96, 102, 114); PLight = true; break;
            default:
                PBg = Color.FromArgb(a, 14, 16, 24); PRow = Color.FromArgb(Math.Min(255, a + 25), 32, 35, 45);
                PText = Color.White; PMuted = Color.FromArgb(172, 178, 190); PLight = false; break;
        }
    }
    static Color A(Color c, int alpha) { return Color.FromArgb(Math.Max(0, Math.Min(255, alpha)), c.R, c.G, c.B); }
    static Color A(Color c, float mul) { return Color.FromArgb(Math.Max(0, Math.Min(255, (int)(c.A * mul))), c.R, c.G, c.B); }
    Color[] Med(int rank) { return StyleMedals[Math.Max(0, Math.Min(StyleMedals.Length - 1, hudStyle))][Math.Max(0, Math.Min(2, rank - 1))]; }
    Color TxtCol(int rank) { return StyleText[Math.Max(0, Math.Min(StyleText.Length - 1, hudStyle))][Math.Max(0, Math.Min(2, rank - 1))]; }

    // ---------- drawing in 1920x1080 pixels ----------
    static void R(float x, float y, float w, float h, Color c) { if (w > 0.2f && h > 0.2f && c.A > 0) Rect((VOX + x * VK) * U, (VOY + y * VK) * U, w * VK * U, h * VK * U, c); }
    bool Im(string file, float cx, float cy, float w, float h, Color tint) { return tint.A > 0 && Img(file, (VOX + cx * VK) * U, (VOY + cy * VK) * U, w * VK * U, h * VK * U, tint); }

    // ---- HUD MODE ----  wide: the HUD uses the whole 1920x1080 screen
    //  tiktok: the HUD is laid out on a 1080x1920 (9:16) page drawn in the MIDDLE of the screen,
    //  exactly the part TikTok LIVE Studio keeps in portrait -> everything stays visible on the phone
    static float VK = 1f, VOX = 0f, VOY = 0f, VWv = 1920f, VHv = 1080f;
    float VW { get { return VWv; } }
    float VH { get { return VHv; } }
    float VCX { get { return VWv / 2f; } }
    float VY(float y) { return y * VHv / 1080f; }
    bool TikTokMode { get { return cfg.HudMode == "tiktok"; } }
    void ApplyHudMode()
    {
        if (cfg.HudMode == "tiktok") { VWv = 1080f; VHv = 1920f; VK = 1080f / 1920f; VOX = (1920f - 1080f * VK) / 2f; VOY = 0f; }
        else { VWv = 1920f; VHv = 1080f; VK = 1f; VOX = 0f; VOY = 0f; }
    }
    void ToggleHudMode()
    {
        string m = TikTokMode ? "wide" : "tiktok";
        cfg.HudMode = m;
        cfg.Vertical = m == "tiktok";
        ApplyHudMode();
        try { SaveSetting("HUD", "Mode", m); } catch (Exception ex) { Log(ex); }
        Notification.Show("~b~HUD~s~: " + (m == "tiktok" ? "TIKTOK LIVE (vertical, middle of the screen)" : "WIDE 16:9"));
    }
    void DrawTikTokGuide()
    {
        if (!TikTokMode || !cfg.TikTokGuide) return;
        Color g = Color.FromArgb(160, 255, 255, 255);
        R(-3f, 0f, 3f, VH, g);
        R(VW, 0f, 3f, VH, g);
    }
    string Ui(string n) { return Path.Combine(uiDir, n + ".png"); }

    // text centred on the line cy; px = size in 1080p pixels
    static void T(string text, float x, float cy, float px, Color c, GTA.UI.Alignment al, GTA.UI.Font f, bool outline)
    {
        if (string.IsNullOrEmpty(text) || c.A == 0) return;
        new TextElement(text, new PointF((VOX + x * VK) * U, (VOY + (cy - px * 0.6f) * VK) * U), px * VK / 75f, c, f, al, outline, outline).Draw();
    }
    // text on a box: no outline on light boxes
    void TB(string text, float x, float cy, float px, Color c, GTA.UI.Alignment al, GTA.UI.Font f) { T(text, x, cy, px, c, al, f, !PLight); }

    float TextW(string text, GTA.UI.Font f, float px)
    {
        if (string.IsNullOrEmpty(text)) return 0f;
        string key = (int)f + "|" + px.ToString("0.0", CultureInfo.InvariantCulture) + "|" + text;
        float w;
        if (textWidths.TryGetValue(key, out w)) return w;
        try
        {
            Function.Call((Hash)0x54CE8AC98E120CAB, "STRING");                        // BEGIN_TEXT_COMMAND_GET_SCREEN_WIDTH_OF_DISPLAY_TEXT
            Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, text);
            Function.Call(Hash.SET_TEXT_FONT, (int)f);
            Function.Call(Hash.SET_TEXT_SCALE, px / 75f, px / 75f);
            w = Function.Call<float>((Hash)0x85F061DA64ED2F67, true) * 1920f;       // END_TEXT_COMMAND_GET_SCREEN_WIDTH_OF_DISPLAY_TEXT
            if (w <= 0f || w > 5000f) w = text.Length * px * 0.5f;
        }
        catch { w = text.Length * px * 0.5f; }
        if (textWidths.Count > 3000) textWidths.Clear();
        textWidths[key] = w;
        return w;
    }
    string Fit(string text, GTA.UI.Font f, float px, float maxW)
    {
        if (maxW <= 0f || TextW(text, f, px) <= maxW) return text;
        string s = text;
        for (int i = 0; i < 40 && s.Length > 2; i++)
        {
            s = s.Substring(0, s.Length - 1);
            if (TextW(s + ".", f, px) <= maxW) return s + ".";
        }
        return s + ".";
    }

    // a supporter name in any language (Arabic etc. = picture). Returns the width it takes.
    float NameW(string name, string alt, float px, float maxW)
    {
        if (string.IsNullOrEmpty(name)) name = alt ?? "";
        if (Renderable(name)) return Math.Min(maxW, TextW(Fit(name, FL, px, maxW), FL, px));
        NameTag t = GetTag(name);
        if (t.Ready && t.File != null) return Math.Min(maxW, px * 1.05f * t.Aspect);
        return Math.Min(maxW, TextW(alt ?? "...", FL, px));
    }
    float N(string name, string alt, float x, float cy, float px, Color c, GTA.UI.Alignment al, float maxW, bool outline)
    {
        if (string.IsNullOrEmpty(name)) name = alt ?? "";
        if (Renderable(name))
        {
            string s = Fit(name, FL, px, maxW);
            T(s, x, cy, px, c, al, FL, outline);
            return Math.Min(maxW, TextW(s, FL, px));
        }
        NameTag t = GetTag(name);
        if (t.Ready && t.File != null)
        {
            float h = px * 1.05f, w = h * t.Aspect;
            if (maxW > 0f && w > maxW) { h *= maxW / w; w = maxW; }
            float cx = al == AC ? x : (al == AR ? x - w / 2f : x + w / 2f);
            if (Im(t.File, cx, cy, w, h, c)) return w;
        }
        string fb = Fit(string.IsNullOrEmpty(alt) ? "..." : alt, FL, px, maxW);
        T(fb, x, cy, px, c, al, FL, outline);
        return Math.Min(maxW, TextW(fb, FL, px));
    }

    // ---------- text as a picture ----------
    // GTA draws script pictures on top of GTA's own text and boxes, so any text that sits
    // ON a picture (a shield, a podium step, a medal badge) is drawn as a picture too.
    readonly ConcurrentDictionary<string, NameTag> labels = new ConcurrentDictionary<string, NameTag>();
    NameTag GetLabel(string text, Color title)
    {
        string key = "f" + hudFont + (title.A > 0 ? "T" + title.ToArgb() + "|" : "L|") + text;
        NameTag t;
        if (labels.TryGetValue(key, out t)) return t;
        t = new NameTag();
        if (!labels.TryAdd(key, t)) return labels[key];
        NameTag tag = t;
        string file = Path.Combine(tagDir, (title.A > 0 ? "t" : "l") + ((uint)key.GetHashCode()).ToString("x8") + "_" + labels.Count + ".png");
        ThreadPool.QueueUserWorkItem(delegate
        {
            try
            {
                float aspect;
                if (RenderLabel(text, file, title, out aspect)) { tag.Aspect = aspect; tag.File = file; tag.Ready = true; }
            }
            catch (Exception ex) { Log(ex); }
        });
        return t;
    }
    // text picture: px = text size in 1080p pixels
    float TI(string text, float x, float cy, float px, Color c, GTA.UI.Alignment al) { return TI(text, x, cy, px, c, al, 0f); }
    float TI(string text, float x, float cy, float px, Color c, GTA.UI.Alignment al, float maxW)
    {
        if (string.IsNullOrEmpty(text)) return 0f;
        NameTag t = GetLabel(text, Color.Empty);
        if (!t.Ready || t.File == null) return 0f;
        float h = px * 1.34f, w = h * t.Aspect;
        if (maxW > 0f && w > maxW) { h *= maxW / w; w = maxW; }
        float cx = al == AC ? x : (al == AR ? x - w / 2f : x + w / 2f);
        Im(t.File, cx, cy, w, h, c);
        return w;
    }
    // a supporter name as a picture
    float NI(string name, string alt, float x, float cy, float px, Color c, GTA.UI.Alignment al, float maxW)
    {
        return TI(string.IsNullOrEmpty(name) ? (alt ?? "") : name, x, cy, px, c, al, maxW);
    }
    // the big GOAL! / SAVED! title: heavy letters, colour gradient, dark outline
    void Title(string text, float cx, float cy, float px, Color kind, float fade)
    {
        NameTag t = GetLabel(text, kind);
        if (t.Ready && t.File != null)
        {
            float h = px * 1.3f, w = h * t.Aspect;
            if (w > 1500f) { h *= 1500f / w; w = 1500f; }
            Im(t.File, cx, cy, w, h, A(Color.White, fade));
        }
        else T(text, cx, cy, px, A(kind, fade), AC, FP, true);
    }
    static System.Drawing.Font HeavyFont(float px, bool title)
    {
        string[] fams = title ? FontTitleFams : FontTextFams;
        foreach (string fam in fams)
        {
            try
            {
                FontStyle st = (fam.Contains("Black") || fam == "Impact" || fam.Contains("Semibold")) ? FontStyle.Regular : FontStyle.Bold;
                System.Drawing.Font f = new System.Drawing.Font(fam, px, st, GraphicsUnit.Pixel);
                if (f.Name == fam) return f;
                f.Dispose();
            }
            catch { }
        }
        return MakeFont(px);
    }
    static bool RenderLabel(string text, string file, Color title, out float aspect)
    {
        aspect = 1f;
        StringBuilder sb = new StringBuilder();
        foreach (char ch in text)
        {
            if (char.IsSurrogate(ch) || ch == '\u200D' || ch == '\uFE0F' || (ch >= '\u2600' && ch <= '\u27BF')) continue;
            sb.Append(ch);
        }
        string s = sb.ToString().Trim();
        if (s.Length == 0) return false;
        bool rtl = false;
        foreach (char ch in s)
        {
            if ((ch >= '\u0590' && ch <= '\u08FF') || (ch >= '\uFB1D' && ch <= '\uFEFC')) { rtl = true; break; }
            if (char.IsLetter(ch)) break;
        }
        bool big = title.A > 0;
        float em = big ? 150f : 60f;
        int H = big ? 200 : 80, pad = big ? 30 : 6;
        bool nonLatin = false;
        foreach (char ch in s) if (ch > 0x24F) { nonLatin = true; break; }
        using (System.Drawing.Font font = nonLatin ? MakeFont(em) : HeavyFont(em, big))     // Arabic... -> a font that has the letters
        using (StringFormat sf = new StringFormat(StringFormat.GenericTypographic))
        {
            sf.FormatFlags |= StringFormatFlags.NoWrap;
            if (rtl) sf.FormatFlags |= StringFormatFlags.DirectionRightToLeft;
            sf.LineAlignment = StringAlignment.Center;
            int w;
            using (Bitmap probe = new Bitmap(1, 1))
            using (Graphics pg = Graphics.FromImage(probe))
            {
                pg.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                w = (int)Math.Ceiling(pg.MeasureString(s, font, 6000, sf).Width) + pad * 2;
            }
            w = Math.Max(12, Math.Min(big ? 2400 : 1200, w));
            using (Bitmap bmp = new Bitmap(w, H, PixelFormat.Format32bppArgb))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                RectangleF box = new RectangleF(pad, 0, w - pad * 2, H);
                if (!big) g.DrawString(s, font, Brushes.White, box, sf);
                else
                {
                    using (GraphicsPath gp = new GraphicsPath())
                    {
                        gp.AddString(s, font.FontFamily, (int)font.Style, em, box, sf);
                        using (System.Drawing.Drawing2D.Matrix m = new System.Drawing.Drawing2D.Matrix()) { m.Translate(6f, 9f); gp.Transform(m); }
                        using (SolidBrush sh = new SolidBrush(Color.FromArgb(150, 0, 0, 0))) g.FillPath(sh, gp);
                        using (System.Drawing.Drawing2D.Matrix m = new System.Drawing.Drawing2D.Matrix()) { m.Translate(-6f, -9f); gp.Transform(m); }
                        using (Pen pen = new Pen(Color.FromArgb(235, 12, 12, 16), 14f)) { pen.LineJoin = LineJoin.Round; g.DrawPath(pen, gp); }
                        Color light = Color.FromArgb(255, Math.Min(255, title.R + 150), Math.Min(255, title.G + 150), Math.Min(255, title.B + 150));
                        Color dark = Color.FromArgb(255, title.R * 55 / 100, title.G * 55 / 100, title.B * 55 / 100);
                        using (LinearGradientBrush b = new LinearGradientBrush(new RectangleF(0, 0, w, H), Color.White, dark, 90f))
                        {
                            ColorBlend cb = new ColorBlend(4);
                            cb.Colors = new[] { Color.White, light, title, dark };
                            cb.Positions = new[] { 0f, 0.44f, 0.46f, 1f };
                            b.InterpolationColors = cb;
                            g.FillPath(b, gp);
                        }
                    }
                }
                bmp.Save(file, ImageFormat.Png);
            }
            aspect = w / (float)H;
        }
        return true;
    }

    // ---------- shapes ----------
    void RR(float x, float y, float w, float h, float r, Color c)
    {
        r = Math.Min(r, Math.Min(w, h) / 2f);
        if (r < 1f) { R(x, y, w, h, c); return; }
        R(x + r, y, w - 2f * r, h, c);
        R(x, y + r, r, h - 2f * r, c);
        R(x + w - r, y + r, r, h - 2f * r, c);
        Im(Ui("c_tl"), x + r / 2f, y + r / 2f, r, r, c);
        Im(Ui("c_tr"), x + w - r / 2f, y + r / 2f, r, r, c);
        Im(Ui("c_bl"), x + r / 2f, y + h - r / 2f, r, r, c);
        Im(Ui("c_br"), x + w - r / 2f, y + h - r / 2f, r, r, c);
    }
    void Pill(float x, float y, float w, float h, Color c) { RR(x, y, w, h, h / 2f, c); }
    // parallelogram leaning right  / /
    void Para(float x, float y, float w, float h, float sk, Color c)
    {
        R(x + sk, y, w - 2f * sk, h, c);
        Im(Ui("tri_l"), x + sk / 2f, y + h / 2f, sk, h, c);
        Im(Ui("tri_r"), x + w - sk / 2f, y + h / 2f, sk, h, c);
    }
    // box with the bottom-right corner cut
    void Cut(float x, float y, float w, float h, float cut, Color c)
    {
        R(x, y, w - cut, h, c);
        R(x + w - cut, y, cut, h - cut, c);
        Im(Ui("tri_r"), x + w - cut / 2f, y + h - cut / 2f, cut, cut, c);
    }
    void Circle(float cx, float cy, float d, Color c) { Im(Ui("circle"), cx, cy, d, d, c); }
    void Glow(float cx, float cy, float d, Color c) { Im(Ui("glow"), cx, cy, d, d, c); }
    void ShapeFill(string shape, float cx, float cy, float d, Color c)
    {
        switch (shape)
        {
            case "hex": Im(Ui("hex"), cx, cy, d, d, c); break;
            case "rounded": RR(cx - d / 2f, cy - d / 2f, d, d, d * 0.26f, c); break;
            case "square": RR(cx - d / 2f, cy - d / 2f, d, d, d * 0.09f, c); break;
            default: Circle(cx, cy, d, c); break;
        }
    }
    // a metal ring / frame in the medal colours of this place, in the photo shape
    void Ring(string shape, int rank, float cx, float cy, float d)
    {
        string f = GradImg("ring_" + shape, rank);
        if (f == null || !Im(f, cx, cy, d, d, Color.White)) ShapeFill(shape, cx, cy, d, Med(rank)[1]);
    }
    string AvatarFile(AvatarInfo a, string shape)
    {
        if (a == null || !a.Ready) return null;
        if (a.Base != null) return a.Base + "_" + shape + ".png";
        return shape == "circle" ? a.File : null;
    }
    void Av(AvatarInfo a, string who, float cx, float cy, float d, string shape)
    {
        string f = AvatarFile(a, shape);
        if (f != null && Im(f, cx, cy, d, d, Color.White)) return;
        ShapeFill(shape, cx, cy, d, Color.FromArgb(235, 70, 76, 90));
        TI(InitialOf(who), cx, cy, d * 0.34f, Color.White, AC);
    }
    /// <summary>Optional png drawn instead of the soccer ball (e.g. the action picture of the queue).</summary>
    public string BallIcon;
    void Ball(float cx, float cy, float d, Color tint) { string f = !string.IsNullOrEmpty(BallIcon) && File.Exists(BallIcon) ? BallIcon : ballFile; if (f != null) Im(f, cx, cy, d, d, f == ballFile ? tint : Color.White); }

    // ---------- the kit of white shapes (made once) ----------
    void MakeUiKit()
    {
        uiDir = Path.Combine(dataDir, "ui16");
        Directory.CreateDirectory(uiDir);
        MakeShape("c_tl", 64, 64, delegate (Graphics g) { g.FillEllipse(Brushes.White, 0, 0, 128, 128); });
        MakeShape("c_tr", 64, 64, delegate (Graphics g) { g.FillEllipse(Brushes.White, -64, 0, 128, 128); });
        MakeShape("c_bl", 64, 64, delegate (Graphics g) { g.FillEllipse(Brushes.White, 0, -64, 128, 128); });
        MakeShape("c_br", 64, 64, delegate (Graphics g) { g.FillEllipse(Brushes.White, -64, -64, 128, 128); });
        MakeShape("tri_l", 128, 128, delegate (Graphics g) { g.FillPolygon(Brushes.White, new[] { new PointF(128, 0), new PointF(128, 128), new PointF(0, 128) }); });
        MakeShape("tri_r", 128, 128, delegate (Graphics g) { g.FillPolygon(Brushes.White, new[] { new PointF(0, 0), new PointF(128, 0), new PointF(0, 128) }); });
        MakeShape("circle", 128, 128, delegate (Graphics g) { g.FillEllipse(Brushes.White, 1, 1, 126, 126); });
        MakeShape("dot", 32, 32, delegate (Graphics g) { g.FillEllipse(Brushes.White, 1, 1, 30, 30); });
        MakeShape("px", 8, 8, delegate (Graphics g) { g.Clear(Color.White); });
        MakeShape("ring", 128, 128, delegate (Graphics g) { using (Pen pen = new Pen(Color.White, 7f)) g.DrawEllipse(pen, 5, 5, 118, 118); });
        MakeShape("hex", 128, 128, delegate (Graphics g) { g.FillPolygon(Brushes.White, HexPts(128)); });
        MakeShape("glow", 128, 128, delegate (Graphics g)
        {
            using (GraphicsPath gp = new GraphicsPath())
            {
                gp.AddEllipse(0, 0, 128, 128);
                using (PathGradientBrush pb = new PathGradientBrush(gp))
                {
                    pb.CenterColor = Color.FromArgb(255, 255, 255, 255);
                    pb.SurroundColors = new[] { Color.FromArgb(0, 255, 255, 255) };
                    g.FillEllipse(pb, 0, 0, 128, 128);
                }
            }
        });
        MakeShape("shield", 120, 170, delegate (Graphics g) { g.FillPolygon(Brushes.White, ShieldPts(120, 170)); });
        MakeShape("shieldw", 240, 340, delegate (Graphics g)
        {
            PointF[] p = ShieldPts(240, 340);
            using (LinearGradientBrush b = new LinearGradientBrush(new RectangleF(0, 0, 240, 340), Color.White, Color.FromArgb(150, 150, 150), 65f)) g.FillPolygon(b, p);
            using (Pen pen = new Pen(Color.FromArgb(120, 255, 255, 255), 4f)) g.DrawPolygon(pen, ShieldPts(240, 340, 10f));
        });
    }
    static PointF[] HexPts(float s)
    {
        float r = s / 2f - 1f, c = s / 2f;
        PointF[] p = new PointF[6];
        for (int i = 0; i < 6; i++) { double a = (-90 + 60 * i) * Math.PI / 180.0; p[i] = new PointF(c + (float)Math.Cos(a) * r, c + (float)Math.Sin(a) * r); }
        return p;
    }
    static PointF[] ShieldPts(float w, float h) { return ShieldPts(w, h, 0f); }
    static PointF[] ShieldPts(float w, float h, float inset)
    {
        float i = inset;
        return new[]
        {
            new PointF(w * 0.12f + i, i), new PointF(w * 0.88f - i, i), new PointF(w - i, h * 0.07f + i), new PointF(w - i, h * 0.83f - i * 0.5f),
            new PointF(w * 0.5f, h - i * 1.6f), new PointF(i, h * 0.83f - i * 0.5f), new PointF(i, h * 0.07f + i)
        };
    }
    delegate void Painter(Graphics g);
    void MakeShape(string name, int w, int h, Painter paint)
    {
        string f = Ui(name);
        if (File.Exists(f)) return;
        try
        {
            using (Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);
                paint(g);
                bmp.Save(f, ImageFormat.Png);
            }
        }
        catch (Exception ex) { Log(ex); }
    }

    // metal pictures in the medal colours (gold / silver / bronze of the current colour style)
    string GradImg(string kind, int rank)
    {
        int st = Math.Max(0, Math.Min(StyleMedals.Length - 1, hudStyle));
        string key = kind + "_" + st + "_" + rank;
        string f;
        if (gradImgs.TryGetValue(key, out f)) return f;
        f = Ui(key);
        try { if (!File.Exists(f)) RenderGrad(kind, StyleMedals[st][Math.Max(0, Math.Min(2, rank - 1))], f); }
        catch (Exception ex) { Log(ex); f = null; }
        gradImgs[key] = f;
        return f;
    }
    static void RenderGrad(string kind, Color[] mc, string file)
    {
        int w = 160, h = 160;
        if (kind == "shield") { w = 240; h = 340; }
        else if (kind == "step" || kind == "plate") { w = 128; h = 128; }
        else if (kind == "crown") { w = 112; h = 68; }
        using (Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            RectangleF all = new RectangleF(0, 0, w, h);
            if (kind == "crown") { DrawCrown(g, w / 2f, h - 5f, mc); bmp.Save(file, ImageFormat.Png); return; }
            using (GraphicsPath gp = new GraphicsPath())
            {
                float angle = 60f;
                switch (kind)
                {
                    case "shield": gp.AddPolygon(ShieldPts(w, h)); angle = 65f; break;
                    case "ring_hex": gp.AddPolygon(HexPts(w)); break;
                    case "ring_rounded": AddRound(gp, 1f, 1f, w - 2f, h - 2f, w * 0.26f); break;
                    case "ring_square": AddRound(gp, 1f, 1f, w - 2f, h - 2f, w * 0.09f); break;
                    case "step": case "plate": gp.AddRectangle(all); angle = 90f; break;
                    default: gp.AddEllipse(1f, 1f, w - 2f, h - 2f); break;
                }
                using (LinearGradientBrush b = new LinearGradientBrush(all, mc[0], mc[2], angle))
                {
                    ColorBlend cb = new ColorBlend(3);
                    cb.Colors = new[] { mc[0], mc[1], mc[2] };
                    cb.Positions = new[] { 0f, kind == "step" || kind == "plate" ? 0.55f : 0.48f, 1f };
                    b.InterpolationColors = cb;
                    g.FillPath(b, gp);
                }
                g.SetClip(gp);
                if (kind == "shield")
                {
                    using (Pen line = new Pen(Color.FromArgb(28, 255, 255, 255), 2f))
                        for (int x = -h; x < w; x += 14) g.DrawLine(line, x, 0, x + h, h);
                    using (Pen pen = new Pen(Color.FromArgb(120, 255, 255, 255), 4f)) g.DrawPolygon(pen, ShieldPts(w, h, 10f));
                }
                if (kind == "step")
                {
                    using (SolidBrush hl = new SolidBrush(Color.FromArgb(170, 255, 255, 255))) g.FillRectangle(hl, 0, 0, w, 7);
                    using (LinearGradientBrush sh = new LinearGradientBrush(new RectangleF(0, h * 0.6f, w, h * 0.4f + 1), Color.FromArgb(0, 0, 0, 0), Color.FromArgb(60, 0, 0, 0), 90f))
                        g.FillRectangle(sh, 0, h * 0.6f, w, h * 0.4f);
                }
                if (kind.StartsWith("ring"))
                    using (Pen pen = new Pen(Color.FromArgb(200, mc[0]), 3f)) g.DrawPath(pen, gp);
                g.ResetClip();
            }
            bmp.Save(file, ImageFormat.Png);
        }
    }
    static void AddRound(GraphicsPath gp, float x, float y, float w, float h, float r)
    {
        float d = r * 2f;
        gp.AddArc(x, y, d, d, 180, 90);
        gp.AddArc(x + w - d, y, d, d, 270, 90);
        gp.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        gp.AddArc(x, y + h - d, d, d, 90, 90);
        gp.CloseFigure();
    }

    // =====================================================================
    //  HUD main
    // =====================================================================
    /// <summary>Call EVERY FRAME (in your Tick) after your own drawing.</summary>
    public void Draw()
    {
        int now = Game.GameTime;
        UpdatePalette();
        try { if (cfg.ShowTop) DrawTop(now); } catch (Exception ex) { Log(ex); }
        try { if (cfg.ShowBoard) DrawList(now); } catch (Exception ex) { Log(ex); }
        try { if (cfg.ShowSwap) DrawSwap(now); } catch (Exception ex) { Log(ex); }
        try { DrawResult(now); } catch (Exception ex) { Log(ex); }
        try { UpdateMegaGift(now); DrawMegaGift(now); } catch (Exception ex) { Log(ex); }
        DrawTikTokGuide();
    }

    // ---------------- TOP SCORERS (top centre) ----------------
    void DrawTop(int now)
    {
        List<Scorer> list = topList;
        int n = Math.Max(1, Math.Min(3, cfg.TopCount));
        switch (Design)
        {
            case "broadcast": TopBroadcast(list, n); break;
            case "podium": TopPodium(list, n); break;
            case "cards": TopCards(list, n); break;
            case "esports": TopEsports(list, n); break;
            case "minimal": TopMinimal(list, n); break;
            case "classic": TopClassic(list, n); break;
            default: TopArena(list, n, now); break;
        }
    }
    static Scorer At(List<Scorer> l, int i) { return i < l.Count ? l[i] : null; }
    string GoalsText(Scorer sc) { return sc == null ? "" : sc.Goals + " " + (sc.Goals == 1 ? cfg.GoalWord : cfg.GoalsWord); }
    static int[] Order213(int n) { return n >= 3 ? new[] { 2, 1, 3 } : (n == 2 ? new[] { 2, 1 } : new[] { 1 }); }

    // ARENA: metal rings, crown on the 1st, gold / silver / bronze
    void TopArena(List<Scorer> list, int n, int now)
    {
        float s = Sc, oy = VY(cfg.TopY);
        string sh = AvShape;
        foreach (int r in Order213(n))
        {
            Scorer sc = At(list, r - 1);
            float d = (r == 1 ? 168f : 136f) * s;
            if (cfg.PodiumPulse && sc != null && now < sc.FlashUntil) d *= 1f + 0.06f * Math.Abs((float)Math.Sin(now / 90.0));
            float cx = VCX + (r == 1 ? 0f : (r == 2 ? -225f : 225f)) * s;
            float cy = oy + (60f + 84f) * s + (r == 1 ? 0f : 34f * s);
            float fade = sc == null ? 0.45f : 1f;
            Color[] mc = Med(r);
            Glow(cx, cy, d * 1.45f, A(mc[1], (int)(150 * fade)));
            if (r == 1) Im(GradImg("crown", 1), cx, cy - d / 2f - 20f * s, 96f * s, 55f * s, A(Color.White, fade));
            Ring(sh, r, cx, cy, d);
            Av(sc != null ? sc.Avatar : null, sc != null ? sc.Alt : "?", cx, cy, d * 0.86f, sh);
            // place badge
            float bd = 40f * s, by = cy + d / 2f - 4f * s;
            Circle(cx, by, bd + 5f * s, A(Color.Black, 140));
            Im(GradImg("ring_circle", r), cx, by, bd, bd, Color.White);
            TI(r.ToString(), cx, by, 24f * s, Ink, AC);
            // name pill + goals
            float ny = by + 44f * s;
            float nw = sc != null ? NameW(sc.Name, sc.Alt, 28f * s, 230f * s) : 20f * s;
            Pill(cx - nw / 2f - 25f * s, ny - 21f * s, nw + 50f * s, 42f * s, A(PBg, fade));
            if (sc != null) N(sc.Name, sc.Alt, cx, ny, 28f * s, PText, AC, 230f * s, !PLight);
            else TB("-", cx, ny, 28f * s, PMuted, AC, FL);
            if (sc != null) T(GoalsText(sc), cx, ny + 42f * s, 34f * s, TxtCol(r), AC, FC, true);
        }
    }

    // BROADCAST: three slanted TV plates side by side, rank block in metal colour
    void TopBroadcast(List<Scorer> list, int n)
    {
        float s = Sc, oy = VY(cfg.TopY), pw = 372f * s, ph = 92f * s, gap = 14f * s, sk = 22f * s;
        float total = n * pw + (n - 1) * gap, x0 = VCX - total / 2f;
        float tw = TextW(cfg.TopTitle, FC, 26f * s) + 60f * s;
        Para(VCX - tw / 2f, oy, tw, 38f * s, 12f * s, Accent);
        T(cfg.TopTitle, VCX, oy + 19f * s, 26f * s, Color.FromArgb(16, 16, 16), AC, FC, false);
        float y = oy + 50f * s;
        for (int r = 1; r <= n; r++)
        {
            Scorer sc = At(list, r - 1);
            Color[] mc = Med(r);
            float x = x0 + (r - 1) * (pw + gap);
            float fade = sc == null ? 0.5f : 1f;
            Para(x, y, pw, ph, sk, A(Color.FromArgb(Math.Max(200, (int)PBg.A), PLight ? 250 : 12, PLight ? 250 : 14, PLight ? 252 : 20), fade));
            Para(x + sk * 0.2f, y + ph - 6f * s, pw - sk * 0.4f, 6f * s, sk * 0.07f, A(mc[1], fade));
            Para(x, y, 78f * s, ph, sk, A(mc[1], fade));
            R(x + sk, y, 78f * s - 2f * sk, 6f * s, A(mc[0], fade));
            T(r.ToString(), x + 39f * s, y + ph / 2f, 58f * s, Color.FromArgb(18, 18, 18), AC, FC, false);
            float ax = x + 78f * s + 12f * s + 34f * s;
            Av(sc != null ? sc.Avatar : null, sc != null ? sc.Alt : "?", ax, y + ph / 2f, 66f * s, AvShape);
            float tx = ax + 34f * s + 14f * s, maxW = x + pw - sk - tx - 8f * s;
            if (sc != null)
            {
                N(sc.Name, sc.Alt, tx, y + 32f * s, 27f * s, PLight ? PText : Color.White, AL, maxW, !PLight);
                T(GoalsText(sc), tx, y + 64f * s, 26f * s, TxtCol(r), AL, FC, !PLight);
            }
            else T("-", tx, y + ph / 2f, 28f * s, PMuted, AL, FL, !PLight);
        }
    }

    // PODIUM: real podium steps 2 - 1 - 3 with the photos standing on them
    void TopPodium(List<Scorer> list, int n)
    {
        float s = Sc, sw = 250f * s, bottom = VY(cfg.TopY) + 400f * s;
        int[] order = Order213(n);
        float x0 = VCX - order.Length * sw / 2f;
        string sh = AvShape;
        for (int i = 0; i < order.Length; i++)
        {
            int r = order[i];
            Scorer sc = At(list, r - 1);
            float h = (r == 1 ? 150f : (r == 2 ? 112f : 84f)) * s, cx = x0 + (i + 0.5f) * sw, top = bottom - h;
            float fade = sc == null ? 0.5f : 1f;
            if (!Im(GradImg("step", r), cx, bottom - h / 2f, sw, h, A(Color.White, fade))) R(cx - sw / 2f, top, sw, h, Med(r)[1]);
            TI(r.ToString(), cx, top + 44f * s, (r == 3 ? 42f : 52f) * s, Ink, AC);
            if (sc != null) TI(GoalsText(sc), cx, top + (r == 3 ? 72f : 88f) * s, 19f * s, Ink, AC, sw - 20f * s);
            // name pill and photo above the step
            float ny = top - 30f * s;
            float nw = sc != null ? NameW(sc.Name, sc.Alt, 25f * s, 210f * s) : 20f * s;
            Pill(cx - nw / 2f - 23f * s, ny - 19f * s, nw + 46f * s, 38f * s, A(PBg, fade));
            if (sc != null) N(sc.Name, sc.Alt, cx, ny, 25f * s, PText, AC, 210f * s, !PLight);
            float d = (r == 1 ? 124f : 100f) * s, ay = ny - 26f * s - d / 2f;
            ShapeFill(sh, cx, ay, d + 12f * s, A(Color.White, fade));
            Av(sc != null ? sc.Avatar : null, sc != null ? sc.Alt : "?", cx, ay, d, sh);
            if (r == 1) Im(GradImg("crown", 1), cx, ay - d / 2f - 30f * s, 92f * s, 53f * s, A(Color.White, fade));
        }
    }

    // CARDS: player cards in the shape of a shield, goals like a rating
    void TopCards(List<Scorer> list, int n)
    {
        float s = Sc, oy = VY(cfg.TopY);
        int[] order = Order213(n);
        string sh = AvShape;
        for (int i = 0; i < order.Length; i++)
        {
            int r = order[i];
            Scorer sc = At(list, r - 1);
            float w = (r == 1 ? 236f : 200f) * s, h = (r == 1 ? 334f : 284f) * s;
            float cx = VCX + (r == 1 ? 0f : (r == 2 ? -1f : 1f) * (118f + 26f + 100f) * s);
            if (n == 2 && r == 2) cx = VCX - (118f + 13f) * s;
            float top = oy + (r == 1 ? 0f : 42f * s), cy = top + h / 2f;
            float fade = sc == null ? 0.5f : 1f;
            Im(Ui("shield"), cx + 6f * s, cy + 10f * s, w, h, A(Color.Black, (int)(90 * fade)));
            if (!Im(GradImg("shield", r), cx, cy, w, h, A(Color.White, fade))) Im(Ui("shield"), cx, cy, w, h, Med(r)[1]);
            float lx = cx - w / 2f + 36f * s;
            TI(sc != null ? sc.Goals.ToString() : "0", lx, top + 56f * s, (r == 1 ? 50f : 42f) * s, A(Ink, fade), AC);
            TI((sc != null && sc.Goals == 1 ? cfg.GoalWord : cfg.GoalsWord).ToUpperInvariant(), lx, top + (r == 1 ? 96f : 88f) * s, 12f * s, A(Ink, fade), AC);
            TI("#" + r, cx + w / 2f - 30f * s, top + 52f * s, 24f * s, A(Ink, 0.7f * fade), AR);
            float d = w * 0.5f, ay = top + h * 0.47f;
            Av(sc != null ? sc.Avatar : null, sc != null ? sc.Alt : "?", cx, ay, d, sh);
            if (sc != null) NI(sc.Name, sc.Alt, cx, top + h * 0.755f, 19f * s, Ink, AC, w * 0.76f);
        }
    }

    // ESPORTS: hexagon frames, angled name plates, neon glow
    void TopEsports(List<Scorer> list, int n)
    {
        float s = Sc, oy = VY(cfg.TopY);
        T("//  " + cfg.TopTitle + "  //", VCX, oy + 16f * s, 30f * s, Accent, AC, FC, true);
        string sh = AvShape;
        foreach (int r in Order213(n))
        {
            Scorer sc = At(list, r - 1);
            float d = (r == 1 ? 172f : 142f) * s;
            float cx = VCX + (r == 1 ? 0f : (r == 2 ? -250f : 250f)) * s;
            float cy = oy + 48f * s + d / 2f + (r == 1 ? 0f : 38f * s);
            float fade = sc == null ? 0.45f : 1f;
            Color[] mc = Med(r);
            Glow(cx, cy, d * 1.3f, A(mc[1], (int)(170 * fade)));
            Ring(sh, r, cx, cy, d);
            Av(sc != null ? sc.Avatar : null, sc != null ? sc.Alt : "?", cx, cy, d * 0.84f, sh);
            float py = cy + d / 2f + 12f * s, pw = 250f * s;
            Para(cx - pw / 2f, py, pw, 40f * s, 12f * s, A(PBg.A < 200 && !PLight ? Color.FromArgb(215, 12, 14, 22) : PBg, fade));
            R(cx - pw / 2f + 12f * s, py, 6f * s, 40f * s, A(mc[1], fade));
            float tw = TextW("#" + r, FC, 26f * s);
            T("#" + r, cx - pw / 2f + 26f * s, py + 20f * s, 26f * s, A(mc[1], fade), AL, FC, !PLight);
            if (sc != null) N(sc.Name, sc.Alt, cx - pw / 2f + 34f * s + tw, py + 20f * s, 24f * s, PText, AL, pw - 60f * s - tw, !PLight);
            if (sc != null) T(GoalsText(sc), cx, py + 64f * s, 30f * s, TxtCol(r), AC, FC, true);
        }
    }

    // MINIMAL: one clean pill with the three
    void TopMinimal(List<Scorer> list, int n)
    {
        float s = Sc, oy = VY(cfg.TopY), slot = 280f * s, h = 96f * s;
        float total = n * slot + 84f * s, x0 = VCX - total / 2f;
        Pill(x0, oy, total, h, PBg);
        Im(GradImg("crown", 1), x0 + 46f * s, oy + h / 2f, 50f * s, 29f * s, Color.White);
        string sh = AvShape;
        for (int r = 1; r <= n; r++)
        {
            Scorer sc = At(list, r - 1);
            float x = x0 + 80f * s + (r - 1) * slot, cy = oy + h / 2f;
            if (r > 1) R(x - 12f * s, oy + 20f * s, 2f * s, h - 40f * s, A(PMuted, 90));
            float fade = sc == null ? 0.45f : 1f;
            ShapeFill(sh, x + 34f * s, cy, 70f * s, A(Med(r)[1], fade));
            Av(sc != null ? sc.Avatar : null, sc != null ? sc.Alt : "?", x + 34f * s, cy, 62f * s, sh);
            Circle(x + 60f * s, cy + 24f * s, 26f * s, A(Med(r)[1], fade));
            TI(r.ToString(), x + 60f * s, cy + 24f * s, 13f * s, Ink, AC);
            if (sc != null)
            {
                N(sc.Name, sc.Alt, x + 80f * s, cy - 13f * s, 24f * s, PText, AL, slot - 100f * s, !PLight);
                T(GoalsText(sc), x + 80f * s, cy + 17f * s, 22f * s, PLight ? A(TxtCol(r), 255) : TxtCol(r), AL, FC, !PLight);
            }
            else TB("-", x + 80f * s, cy, 24f * s, PMuted, AL, FL);
        }
    }

    // CLASSIC: a table of the top scorers
    void TopClassic(List<Scorer> list, int n)
    {
        float s = Sc, w = 640f * s, x = VCX - w / 2f, y = VY(cfg.TopY), rowH = 76f * s, head = 62f * s;
        RR(x, y, w, head + n * rowH + 10f * s, 24f * s, PBg);
        Im(GradImg("crown", 1), x + 50f * s, y + head / 2f, 54f * s, 31f * s, Color.White);
        T(cfg.TopTitle, x + 86f * s, y + head / 2f, 34f * s, Accent, AL, FC, !PLight);
        R(x + 20f * s, y + head - 2f * s, w - 40f * s, 2f * s, A(PMuted, 70));
        for (int r = 1; r <= n; r++)
        {
            Scorer sc = At(list, r - 1);
            float cy = y + head + (r - 0.5f) * rowH;
            Im(GradImg("ring_circle", r), x + 46f * s, cy, 44f * s, 44f * s, Color.White);
            TI(r.ToString(), x + 46f * s, cy, 22f * s, Ink, AC);
            ShapeFill(AvShape, x + 108f * s, cy, 64f * s, Med(r)[1]);
            Av(sc != null ? sc.Avatar : null, sc != null ? sc.Alt : "?", x + 108f * s, cy, 58f * s, AvShape);
            if (sc != null)
            {
                N(sc.Name, sc.Alt, x + 154f * s, cy, 30f * s, PText, AL, w - 330f * s, !PLight);
                T(GoalsText(sc), x + w - 26f * s, cy, 36f * s, TxtCol(r), AR, FC, !PLight);
            }
            else TB("-", x + 154f * s, cy, 30f * s, PMuted, AL, FL);
        }
    }

    // ---------------- WAITING LIST (side) ----------------
    void DrawList(int now)
    {
        List<Supporter> list = Waiting ?? new List<Supporter>();
        int rows = Math.Min(list.Count, cfg.BoardRows);
        float s = Sc, w = cfg.ListW * s;
        float x = cfg.BoardSide == "left" ? 30f : VW - 30f - w, y = VY(cfg.ListY);
        switch (Design)
        {
            case "broadcast": ListBroadcast(list, rows, x, y, w, s); break;
            case "podium": ListPodium(list, rows, x, y, w, s); break;
            case "cards": ListCards(list, rows, x, y, w, s); break;
            case "esports": ListEsports(list, rows, x, y, w, s); break;
            case "minimal": ListMinimal(list, rows, x, y, w, s); break;
            default: ListArena(list, rows, x, y, w, s); break;
        }
    }
    bool Shooting(Supporter p) { return p != null && !string.IsNullOrEmpty(ShootingKey) && p.Key == ShootingKey; }
    string More(List<Supporter> list, int rows) { return list.Count > rows ? "+" + (list.Count - rows) + " ..." : null; }

    void ListArena(List<Supporter> list, int rows, float x, float y, float w, float s)
    {
        float head = 62f * s, rowH = 78f * s;
        string more = More(list, rows);
        float h = head + Math.Max(1, rows) * rowH + (more != null ? 34f * s : 0f) + 12f * s;
        RR(x, y, w, h, 24f * s, PBg);
        Ball(x + 34f * s, y + head / 2f, 30f * s, Color.White);
        T(cfg.BoardTitle, x + 58f * s, y + head / 2f, 32f * s, Accent, AL, FC, !PLight);
        string qc = QueueCount.ToString();
        float cw = TextW(qc, FC, 28f * s) + 28f * s;
        Pill(x + w - 18f * s - cw, y + head / 2f - 17f * s, cw, 34f * s, Accent);
        T(qc, x + w - 18f * s - cw / 2f, y + head / 2f, 28f * s, Color.FromArgb(17, 17, 17), AC, FC, false);
        R(x + 18f * s, y + head - 3f * s, w - 36f * s, 3f * s, Accent);
        float ry = y + head + 4f * s;
        if (rows == 0) TB(cfg.EmptyText, x + w / 2f, ry + rowH / 2f, 24f * s, PMuted, AC, FL);
        for (int i = 0; i < rows; i++)
        {
            Supporter p = list[i];
            float cy = ry + rowH / 2f;
            if (Shooting(p)) { R(x, ry + 4f * s, w, rowH - 8f * s, A(Accent, 85)); R(x, ry + 10f * s, 6f * s, rowH - 20f * s, Accent); }
            ShapeFill(AvShape, x + 50f * s, cy, 62f * s, Shooting(p) ? Accent : PRow);
            Av(p.Avatar, p.Alt, x + 50f * s, cy, 56f * s, AvShape);
            N(p.DisplayName, p.Alt, x + 94f * s, cy, 27f * s, PText, AL, w - 94f * s - 118f * s, !PLight);
            Pill(x + w - 16f * s - 96f * s, cy - 18f * s, 96f * s, 36f * s, PRow);
            Ball(x + w - 16f * s - 74f * s, cy, 26f * s, Color.White);
            TB("x" + p.Balls.Count, x + w - 16f * s - 22f * s, cy, 28f * s, PText, AR, FC);
            ry += rowH;
        }
        if (more != null) TB(more, x + 24f * s, ry + 14f * s, 24f * s, PMuted, AL, FL);
    }

    void ListBroadcast(List<Supporter> list, int rows, float x, float y, float w, float s)
    {
        float head = 50f * s, rowH = 60f * s, gap = 5f * s;
        R(x, y, w, head, PLight ? Color.FromArgb(245, 255, 255, 255) : Color.FromArgb(235, 10, 10, 14));
        R(x, y + head, w, 4f * s, Accent);
        T(cfg.BoardTitle.ToUpperInvariant(), x + 18f * s, y + head / 2f, 28f * s, PLight ? PText : Color.White, AL, FC, !PLight);
        string qc = QueueCount.ToString();
        float cw = TextW(qc, FC, 26f * s) + 24f * s;
        R(x + w - 12f * s - cw, y + 10f * s, cw, head - 20f * s, Accent);
        T(qc, x + w - 12f * s - cw / 2f, y + head / 2f, 26f * s, Color.FromArgb(17, 17, 17), AC, FC, false);
        float ry = y + head + 10f * s;
        Color rowBg = PLight ? Color.FromArgb(235, 255, 255, 255) : Color.FromArgb(Math.Max(200, (int)PBg.A), 20, 22, 30);
        if (rows == 0) { R(x, ry, w, rowH, rowBg); TB(cfg.EmptyText, x + w / 2f, ry + rowH / 2f, 22f * s, PMuted, AC, FL); }
        for (int i = 0; i < rows; i++)
        {
            Supporter p = list[i];
            bool now = Shooting(p);
            float cy = ry + rowH / 2f;
            R(x, ry, w, rowH, now ? A(Accent, 235) : rowBg);
            R(x, ry, 52f * s, rowH, now ? Color.FromArgb(17, 17, 17) : Color.FromArgb(245, 245, 245));
            T((i + 1).ToString(), x + 26f * s, cy, 30f * s, now ? Accent : Color.FromArgb(17, 17, 17), AC, FC, false);
            R(x + 52f * s, ry, 6f * s, rowH, now ? Color.White : Accent);
            Av(p.Avatar, p.Alt, x + 58f * s + 12f * s + 23f * s, cy, 46f * s, AvShape);
            Color tc = now ? Color.FromArgb(17, 17, 17) : (PLight ? PText : Color.White);
            N(p.DisplayName, p.Alt, x + 58f * s + 12f * s + 46f * s + 12f * s, cy, 25f * s, tc, AL, w - 250f * s, !now && !PLight);
            Ball(x + w - 24f * s, cy, 24f * s, Color.White);
            T(p.Balls.Count.ToString(), x + w - 42f * s, cy, 28f * s, tc, AR, FC, !now && !PLight);
            ry += rowH + gap;
        }
        string more = More(list, rows);
        if (more != null) T(more, x + 12f * s, ry + 12f * s, 22f * s, Color.White, AL, FL, true);
    }

    void ListPodium(List<Supporter> list, int rows, float x, float y, float w, float s)
    {
        T(cfg.BoardTitle, x + 8f * s, y + 20f * s, 34f * s, Color.White, AL, FC, true);
        string qc = QueueCount.ToString();
        float cw = TextW(qc, FC, 28f * s) + 30f * s;
        Pill(x + w - cw, y + 3f * s, cw, 34f * s, Accent);
        T(qc, x + w - cw / 2f, y + 20f * s, 28f * s, Color.FromArgb(17, 17, 17), AC, FC, false);
        float ry = y + 50f * s, rowH = 86f * s;
        if (rows == 0) { RR(x, ry, w, 64f * s, 22f * s, PBg); TB(cfg.EmptyText, x + w / 2f, ry + 32f * s, 22f * s, PMuted, AC, FL); }
        for (int i = 0; i < rows; i++)
        {
            Supporter p = list[i];
            float cy = ry + rowH / 2f;
            if (Shooting(p)) RR(x - 4f * s, ry - 4f * s, w + 8f * s, rowH + 8f * s, 26f * s, Accent);
            RR(x, ry, w, rowH, 22f * s, Shooting(p) ? Color.FromArgb(235, 20, 22, 30) : PBg);
            Av(p.Avatar, p.Alt, x + 46f * s, cy, 60f * s, AvShape);
            Color tc = Shooting(p) ? Color.White : PText;
            N(p.DisplayName, p.Alt, x + 88f * s, cy - 14f * s, 25f * s, tc, AL, w - 170f * s, !PLight || Shooting(p));
            int nb = Math.Min(6, p.Balls.Count);
            for (int k = 0; k < nb; k++) Ball(x + 100f * s + k * 26f * s, cy + 18f * s, 22f * s, Color.White);
            if (p.Balls.Count > 6) TB("+" + (p.Balls.Count - 6), x + 100f * s + 6 * 26f * s, cy + 18f * s, 20f * s, PMuted, AL, FL);
            T(p.Balls.Count.ToString(), x + w - 34f * s, cy, 44f * s, Accent, AC, FC, true);
            ry += rowH + 10f * s;
        }
        string more = More(list, rows);
        if (more != null) T(more, x + 12f * s, ry + 8f * s, 24f * s, Color.White, AL, FL, true);
    }

    void ListCards(List<Supporter> list, int rows, float x, float y, float w, float s)
    {
        float head = 58f * s, rowH = 82f * s;
        string more = More(list, rows);
        float h = head + Math.Max(1, rows) * rowH + (more != null ? 34f * s : 0f) + 12f * s;
        RR(x, y, w, h, 22f * s, PBg);
        R(x + 12f * s, y + 8f * s, w - 24f * s, head - 16f * s, A(Accent, 60));
        T(cfg.BoardTitle, x + 24f * s, y + head / 2f, 32f * s, Accent, AL, FC, !PLight);
        TB(QueueCount.ToString(), x + w - 24f * s, y + head / 2f, 30f * s, PText, AR, FC);
        float ry = y + head + 4f * s;
        if (rows == 0) TB(cfg.EmptyText, x + w / 2f, ry + rowH / 2f, 23f * s, PMuted, AC, FL);
        for (int i = 0; i < rows; i++)
        {
            Supporter p = list[i];
            float cy = ry + rowH / 2f;
            if (Shooting(p)) RR(x + 8f * s, ry + 4f * s, w - 16f * s, rowH - 8f * s, 14f * s, A(Accent, 80));
            Im(Ui("shield"), x + 42f * s, cy, 50f * s, 62f * s, Accent);
            TI(p.Balls.Count.ToString(), x + 42f * s, cy - 4f * s, 22f * s, Color.FromArgb(35, 24, 0), AC);
            Av(p.Avatar, p.Alt, x + 104f * s, cy, 56f * s, AvShape);
            N(p.DisplayName, p.Alt, x + 144f * s, cy, 26f * s, PText, AL, w - 220f * s, !PLight);
            TB("#" + (i + 1), x + w - 22f * s, cy, 26f * s, PMuted, AR, FC);
            if (i < rows - 1) R(x + 18f * s, ry + rowH - 1f * s, w - 36f * s, 1.5f * s, A(PMuted, 50));
            ry += rowH;
        }
        if (more != null) TB(more, x + 24f * s, ry + 14f * s, 24f * s, PMuted, AL, FL);
    }

    void ListEsports(List<Supporter> list, int rows, float x, float y, float w, float s)
    {
        float head = 46f * s, rowH = 70f * s;
        Color box = PLight ? Color.FromArgb(240, 250, 250, 252) : Color.FromArgb(Math.Max(205, (int)PBg.A), 12, 14, 22);
        Cut(x, y, w, head, 14f * s, box);
        R(x, y + head, w, 3f * s, Accent);
        T(cfg.BoardTitle.ToUpperInvariant(), x + 18f * s, y + head / 2f, 28f * s, Accent, AL, FC, !PLight);
        TB(QueueCount.ToString(), x + w - 30f * s, y + head / 2f, 28f * s, PText, AR, FC);
        float ry = y + head + 10f * s;
        if (rows == 0) { Cut(x, ry, w, rowH, 16f * s, box); TB(cfg.EmptyText, x + w / 2f, ry + rowH / 2f, 22f * s, PMuted, AC, FL); }
        for (int i = 0; i < rows; i++)
        {
            Supporter p = list[i];
            bool now = Shooting(p);
            float cy = ry + rowH / 2f;
            Cut(x, ry, w, rowH, 18f * s, now ? A(Accent, 150) : box);
            R(x, ry, 5f * s, rowH, now ? Color.White : Accent);
            T("#" + (i + 1), x + 18f * s, cy, 26f * s, now ? Color.White : Accent, AL, FC, !PLight);
            Av(p.Avatar, p.Alt, x + 92f * s, cy, 50f * s, AvShape);
            N(p.DisplayName, p.Alt, x + 128f * s, cy, 25f * s, now ? Color.White : PText, AL, w - 220f * s, !PLight || now);
            T("x" + p.Balls.Count, x + w - 34f * s, cy, 28f * s, now ? Color.White : PText, AR, FC, !PLight || now);
            ry += rowH + 6f * s;
        }
        string more = More(list, rows);
        if (more != null) T(more, x + 12f * s, ry + 10f * s, 22f * s, Color.White, AL, FL, true);
    }

    void ListMinimal(List<Supporter> list, int rows, float x, float y, float w, float s)
    {
        bool right = cfg.BoardSide != "left";
        float edge = right ? x + w : x;
        string title = cfg.BoardTitle.ToUpperInvariant() + "  " + QueueCount;
        T(title, edge, y + 14f * s, 24f * s, Color.White, right ? AR : AL, FC, true);
        float ry = y + 34f * s, rowH = 62f * s;
        if (rows == 0)
        {
            float ew = TextW(cfg.EmptyText, FL, 22f * s) + 44f * s;
            Pill(right ? edge - ew : edge, ry, ew, rowH, PRow);
            TB(cfg.EmptyText, (right ? edge - ew : edge) + ew / 2f, ry + rowH / 2f, 22f * s, PMuted, AC, FL);
        }
        for (int i = 0; i < rows; i++)
        {
            Supporter p = list[i];
            int nb = Math.Min(5, p.Balls.Count);
            string extra = p.Balls.Count > 5 ? "+" + (p.Balls.Count - 5) : "";
            float nw = NameW(p.DisplayName, p.Alt, 24f * s, 230f * s);
            float cw = nb * 24f * s + (extra.Length > 0 ? TextW(extra, FC, 22f * s) + 6f * s : 0f);
            float pw = Math.Min(w, 8f * s + 50f * s + 12f * s + nw + 16f * s + cw + 34f * s);
            float px = right ? edge - pw : edge, cy = ry + rowH / 2f;
            if (Shooting(p)) Pill(px - 3f * s, ry - 3f * s, pw + 6f * s, rowH + 6f * s, Accent);
            Pill(px, ry, pw, rowH, Shooting(p) ? Color.FromArgb(235, 20, 22, 30) : PRow);
            Av(p.Avatar, p.Alt, px + 8f * s + 25f * s, cy, 50f * s, AvShape);
            N(p.DisplayName, p.Alt, px + 70f * s, cy, 24f * s, Shooting(p) ? Color.White : PText, AL, 230f * s, !PLight || Shooting(p));
            // the balls he still has: one ball picture per ball (5 max, then +N)
            float bx = px + pw - 32f * s - cw + 11f * s;
            for (int k = 0; k < nb; k++) Ball(bx + k * 24f * s, cy, 21f * s, Color.White);
            if (extra.Length > 0) T(extra, px + pw - 32f * s, cy, 22f * s, Accent, AR, FC, true);
            ry += rowH + 8f * s;
        }
        string more = More(list, rows);
        if (more != null) T(more, edge, ry + 10f * s, 22f * s, Color.White, right ? AR : AL, FL, true);
    }

    // ---------------- SWAP gift (bottom left) : moves like the gifts in the goal ----------------
    void DrawSwap(int now)
    {
        float s = Sc, t = now / 1000f, size = cfg.SwapSize * s;
        float cx = cfg.SwapX, cy = VH - VY(cfg.SwapBottom) - size / 2f;
        float bob = (float)Math.Sin(t * cfg.TargetSpeed) * 14f * s;
        float pulse = 1f + (float)Math.Sin(t * cfg.TargetSpeed * 1.7f) * 0.07f;
        // it was just used: jump
        int since = now - swapFlash;
        if (since >= 0 && since < 1100) { float u = since / 1100f; pulse *= 1f + 0.45f * (float)Math.Sin(u * Math.PI) * (1f - u); }
        float icy = cy + bob;
        Im(Ui("glow"), cx, icy, size * 1.6f, size * 1.15f, A(Accent, 140));
        string icon = SwapIcon;
        bool drew = icon != null && Im(icon, cx, icy, size * pulse, size * pulse, Color.FromArgb(245, 255, 255, 255));
        if (!drew) { Circle(cx, icy, size * 0.9f * pulse, KSwap); TI("<>", cx, icy, size * 0.3f, Color.White, AC); }

        string word = string.IsNullOrEmpty(cfg.SwapWord) ? "SWAP" : cfg.SwapWord.ToUpperInvariant();
        bool above = cfg.SwapTextPos == "above";
        float lh = 42f * s, lw = TextW(word, FC, 34f * s) + 40f * s;
        float ly = above ? icy - size / 2f - 10f * s - lh : icy + size / 2f + 8f * s;
        float lx = cx - lw / 2f, lc = ly + lh / 2f;
        switch (Design)
        {
            case "broadcast": Para(lx - 8f * s, ly, lw + 16f * s, lh, 12f * s, Accent); T(word, cx, lc, 32f * s, Color.FromArgb(17, 17, 17), AC, FC, false); break;
            case "podium": Pill(lx, ly, lw, lh, Color.FromArgb(90, 255, 255, 255)); T(word, cx, lc, 32f * s, Color.White, AC, FC, true); break;
            case "cards": RR(lx, ly, lw, lh, 7f * s, Med(1)[1]); T(word, cx, lc, 32f * s, Ink, AC, FC, false); break;
            case "esports":
                Cut(lx, ly, lw, lh, 12f * s, Color.FromArgb(225, 12, 14, 22)); R(lx, ly, 5f * s, lh, Accent);
                T(word, cx + 2f * s, lc, 32f * s, Accent, AC, FC, false); break;
            case "minimal": T(word, cx, lc, 34f * s, Color.White, AC, FC, true); R(cx - lw / 2f + 10f * s, ly + lh - 2f * s, lw - 20f * s, 4f * s, Accent); break;
            default: Pill(lx, ly, lw, lh, Accent); T(word, cx, lc, 34f * s, Color.FromArgb(17, 17, 17), AC, FC, false); break;
        }
        if (cfg.ShowSwapName)
        {
            string g = SwapLabel();
            if (!string.IsNullOrEmpty(g)) T(g, cx, above ? icy + size / 2f + 20f * s : ly + lh + 18f * s, 22f * s, Color.White, AC, FL, true);
        }
    }
    int swapFlash = -100000;

    // ---------------- RESULT  GOAL! / SAVED! / MISSED! / ROLE SWAP! ----------------
    string resKind = "", resTitle = "", resSub = "", resWho = "";
    AvatarInfo resAv;
    int resStart = -100000, resSeed;
    const int ResMs = 3200;
    int resMs = ResMs;

    void SetResult(string kind, string title, string sub, string who, AvatarInfo av)
    {
        resKind = kind;
        resTitle = Regex.Replace(title ?? "", "~[a-zA-Z]~", "").Trim();
        resSub = sub ?? "";
        resWho = who ?? "";
        if (!string.IsNullOrEmpty(resSub) && resSub.TrimStart('@').Equals(resWho.TrimStart('@'), StringComparison.OrdinalIgnoreCase)) resSub = "";
        resAv = av;
        resMs = kind == "series" ? Math.Max(ResMs, cfg.RapidSummaryMs) : ResMs;
        resStart = Game.GameTime;
        resSeed = rng.Next();
        if (kind == "swap") swapFlash = Game.GameTime;
        int bigMs = kind == "series" ? Math.Max(1500, cfg.RapidSummaryMs - 300) : 2700;
        if (cfg.ShowResult && cfg.ResultStyle == "gta") ShowBig(title, string.IsNullOrEmpty(sub) ? who : sub, bigMs);
    }
    Color KindColor { get { return resKind == "save" ? KSave : resKind == "miss" ? KMiss : resKind == "swap" ? KSwap : resKind == "series" ? (seriesLastGoals > 0 ? Accent : KMiss) : KGoal; } }

    void DrawResult(int now)
    {
        if (!cfg.ShowResult) return;
        if (cfg.ResultStyle == "gta") { DrawBig(); return; }
        int el = now - resStart;
        if (el < 0 || el > resMs || string.IsNullOrEmpty(resTitle)) return;
        float t = el / (float)resMs;
        float fade = t < 0.08f ? t / 0.08f : (t > 0.85f ? Math.Max(0f, (1f - t) / 0.15f) : 1f);
        switch (Design)
        {
            case "broadcast": ResBroadcast(t, fade); break;
            case "podium": ResPodium(t, fade); break;
            case "cards": ResCards(t, fade); break;
            case "esports": ResEsports(t, fade, el); break;
            case "minimal": ResMinimal(t, fade); break;
            default: ResArena(t, fade); break;
        }
        if (resKind == "goal" || resKind == "swap" || (resKind == "series" && seriesLastGoals > 0)) Confetti(el, fade);
    }
    static float PopScale(float t)
    {
        if (t < 0.1f) return 0.3f + 0.85f * (t / 0.1f);
        if (t < 0.18f) return 1.15f - 0.15f * ((t - 0.1f) / 0.08f);
        return 1f;
    }
    void WhoPill(float cy, float fade, float s, Color bg, Color fg)
    {
        if (string.IsNullOrEmpty(resWho)) return;
        float nw = NameW(resWho, resWho, 32f * s, 600f * s), w = 64f * s + 16f * s + nw + 44f * s, x = VCX - w / 2f;
        Pill(x, cy - 34f * s, w, 68f * s, A(bg, fade));
        Av(resAv, resWho, x + 8f * s + 26f * s, cy, 52f * s, AvShape);
        N(resWho, resWho, x + 76f * s, cy, 32f * s, A(fg, fade), AL, 600f * s, fg.R > 128);
    }
    void SubLine(float cy, float fade, float s)
    {
        if (!string.IsNullOrEmpty(resSub)) T(resSub, VCX, cy, 28f * s, A(Color.White, fade), AC, FL, true);
    }

    void ResArena(float t, float fade)
    {
        float s = Sc, cy = VY(cfg.ResultY) + 80f * s, p = PopScale(t);
        Glow(VCX, cy, 760f * s * p, A(KindColor, (int)(110 * fade)));
        Title(resTitle, VCX, cy, 150f * s * p, KindColor, fade);
        WhoPill(cy + 118f * s, fade, s, PBg.A < 150 ? Color.FromArgb(220, 12, 14, 22) : PBg, PLight ? PText : Color.White);
        SubLine(cy + 180f * s, fade, s);
    }

    void ResBroadcast(float t, float fade)
    {
        float s = Sc, h = 130f * s, y = VY(cfg.ResultY) + 20f * s;
        float slide = t < 0.12f ? (1f - (float)Math.Pow(1f - t / 0.12f, 3)) - 1f : (t > 0.84f ? (t - 0.84f) / 0.16f : 0f);
        float tw = TextW(resTitle.ToUpperInvariant(), FC, 104f * s) + 90f * s;
        float nw = string.IsNullOrEmpty(resWho) ? 0f : NameW(resWho, resWho, 34f * s, 420f * s);
        float bw = string.IsNullOrEmpty(resWho) ? 0f : 90f * s + 30f * s + Math.Max(nw, string.IsNullOrEmpty(resSub) ? 0f : TextW(resSub.ToUpperInvariant(), FC, 24f * s)) + 60f * s;
        float x = VCX - (tw + bw) / 2f + slide * 1300f * s;
        Para(x, y, tw, h, 30f * s, A(KindColor, fade));
        R(x + 30f * s, y, tw - 60f * s, 10f * s, A(Color.White, (int)(70 * fade)));
        T(resTitle.ToUpperInvariant(), x + tw / 2f, y + h / 2f, 104f * s, A(Color.White, fade), AC, FC, true);
        if (bw > 0f)
        {
            float bx = x + tw - 12f * s;
            Para(bx, y + 12f * s, bw, h - 24f * s, 26f * s, A(Color.FromArgb(235, 14, 16, 22), fade));
            Av(resAv, resWho, bx + 40f * s + 45f * s, y + h / 2f, 86f * s, AvShape);
            N(resWho, resWho, bx + 40f * s + 90f * s + 20f * s, y + h / 2f - (string.IsNullOrEmpty(resSub) ? 0f : 16f * s), 34f * s, A(Color.White, fade), AL, 420f * s, true);
            if (!string.IsNullOrEmpty(resSub)) T(resSub.ToUpperInvariant(), bx + 40f * s + 90f * s + 20f * s, y + h / 2f + 24f * s, 24f * s, A(Color.FromArgb(190, 196, 206), fade), AL, FC, false);
        }
    }

    void ResPodium(float t, float fade)
    {
        float s = Sc, rise = t < 0.14f ? (1f - t / 0.14f) : 0f;
        float w = 660f * s, h = 290f * s, x = VCX - w / 2f, y = VY(cfg.ResultY) + 150f * s + rise * 120f * s;
        RR(x, y, w, h, 40f * s, A(PBg.A < 120 ? Color.FromArgb(150, 255, 255, 255) : PBg, fade));
        ShapeFill(AvShape, VCX, y, 150f * s, A(KindColor, fade));
        Av(resAv, resWho, VCX, y, 134f * s, AvShape);
        T(resTitle, VCX, y + 130f * s, 118f * s, A(KindColor, fade), AC, FC, true);
        if (!string.IsNullOrEmpty(resWho)) N(resWho, resWho, VCX, y + 206f * s, 34f * s, A(PLight ? PText : Color.White, fade), AC, 600f * s, !PLight);
        if (!string.IsNullOrEmpty(resSub)) T(resSub, VCX, y + 250f * s, 24f * s, A(PLight ? PMuted : Color.White, fade), AC, FL, !PLight);
    }

    void ResCards(float t, float fade)
    {
        float s = Sc, flip = t < 0.16f ? Math.Min(1.05f, t / 0.14f) : (t > 0.86f ? Math.Max(0f, (1f - t) / 0.14f) : 1f);
        float w = 430f * s * flip, h = 500f * s, cy = VY(cfg.ResultY) + 280f * s;
        Im(Ui("shield"), VCX + 10f * s, cy + 14f * s, w, h, A(Color.Black, (int)(100 * fade)));
        Im(Ui("shieldw"), VCX, cy, w, h, A(KindColor, fade));
        if (flip < 0.7f) return;
        float top = cy - h / 2f;
        Title(resTitle, VCX, top + 92f * s, 84f * s, KindColor, fade);
        Circle(VCX, top + 236f * s, 150f * s, A(Color.White, (int)(210 * fade)));
        Av(resAv, resWho, VCX, top + 236f * s, 136f * s, AvShape);
        if (!string.IsNullOrEmpty(resWho)) NI(resWho, resWho, VCX, top + 342f * s, 26f * s, A(Color.White, fade), AC, 380f * s);
        if (!string.IsNullOrEmpty(resSub)) TI(resSub, VCX, top + 386f * s, 15f * s, A(Color.White, fade), AC, 360f * s);
    }

    void ResEsports(float t, float fade, int el)
    {
        float s = Sc, cy = VY(cfg.ResultY) + 80f * s;
        float jit = el < 420 ? (float)Math.Sin(el * 0.21) * 26f * s * (1f - el / 420f) : 0f;
        string title = resTitle.ToUpperInvariant();
        T(title, VCX - 7f * s + jit, cy, 150f * s, A(Color.FromArgb(0, 240, 255), (int)(170 * fade)), AC, FC, false);
        T(title, VCX + 7f * s - jit, cy, 150f * s, A(Color.FromArgb(255, 0, 200), (int)(160 * fade)), AC, FC, false);
        T(title, VCX + jit * 0.3f, cy, 150f * s, A(KindColor, fade), AC, FC, true);
        if (string.IsNullOrEmpty(resWho)) return;
        float nw = NameW(resWho, resWho, 32f * s, 520f * s), sw = string.IsNullOrEmpty(resSub) ? 0f : TextW(resSub.ToUpperInvariant(), FC, 22f * s) + 20f * s;
        float w = 80f * s + nw + sw + 50f * s, x = VCX - w / 2f, y = cy + 90f * s;
        Para(x, y, w, 68f * s, 16f * s, A(Color.FromArgb(230, 12, 14, 22), fade));
        R(x + 16f * s, y, 6f * s, 68f * s, A(KindColor, fade));
        Av(resAv, resWho, x + 58f * s, y + 34f * s, 54f * s, AvShape);
        N(resWho, resWho, x + 96f * s, y + 34f * s, 32f * s, A(Color.White, fade), AL, 520f * s, true);
        if (sw > 0f) T(resSub.ToUpperInvariant(), x + 96f * s + nw + 16f * s, y + 36f * s, 22f * s, A(Color.FromArgb(185, 190, 200), fade), AL, FC, false);
    }

    void ResMinimal(float t, float fade)
    {
        float s = Sc, cy = VY(cfg.ResultY) + 80f * s;
        T(resTitle.ToUpperInvariant(), VCX, cy, 146f * s, A(Color.White, fade), AC, FC, true);
        float grow = t < 0.18f ? t / 0.18f : (t > 0.84f ? Math.Max(0f, (1f - t) / 0.16f) : 1f);
        float lw = 440f * s * grow;
        RR(VCX - lw / 2f, cy + 70f * s, lw, 9f * s, 4f * s, A(KindColor, fade));
        Im(Ui("glow"), VCX, cy + 74f * s, lw * 0.9f, 44f * s, A(KindColor, (int)(90 * fade)));
        WhoPill(cy + 140f * s, fade, s, PRow.A < 120 ? Color.FromArgb(150, 255, 255, 255) : PRow, PLight ? PText : Color.White);
        SubLine(cy + 200f * s, fade, s);
    }

    // small paper confetti for goals and swaps
    void Confetti(int el, float fade)
    {
        if (el > 2600) return;
        float s = Sc, t = el / 1000f, cy = VY(cfg.ResultY) + 80f * s;
        Random r = new Random(resSeed);
        Color[] cols = resKind == "swap" ? new[] { Color.FromArgb(143, 227, 255), KSwap, Color.White }
                                         : new[] { Color.FromArgb(255, 216, 74), KGoal, Color.White, Color.FromArgb(255, 77, 109), Color.FromArgb(63, 169, 255) };
        for (int i = 0; i < 60; i++)
        {
            double a = r.NextDouble() * Math.PI * 2, v = (260 + r.NextDouble() * 520) * s;
            float delay = (float)r.NextDouble() * 0.2f, tt = Math.Max(0f, t - delay);
            if (tt <= 0f) continue;
            float x = VCX + (float)(Math.Cos(a) * v) * Math.Min(1f, tt * 1.6f);
            float y = cy + (float)(Math.Sin(a) * v * 0.55) * Math.Min(1f, tt * 1.6f) - 120f * s * Math.Min(1f, tt * 2f) + 520f * s * tt * tt;
            float sz = (10f + (float)r.NextDouble() * 8f) * s;
            float wob = (float)Math.Abs(Math.Sin(tt * 9 + i));
            R(x, y, sz * (0.35f + 0.65f * wob), sz * 1.5f, A(cols[i % cols.Length], (int)(255 * fade * Math.Max(0f, 1f - tt / 2.6f))));
        }
    }

    void NextDesign()
    {
        cfg.Design = DesignNames[(DesignIndex + 1) % DesignNames.Length];
        SaveSetting("HUD", "Design", cfg.Design);
        Notification.Show("~b~HUD design~s~: " + cfg.Design.ToUpperInvariant());
    }


    // ------------------------------------------------------------------
    //  Helpers: sprites, avatars, name pictures (Arabic...), shapes
    // ------------------------------------------------------------------
    // ------------------------------------------------------------------
    //  Drawing helpers  (all coordinates in the 1280 x 720 HUD space)
    // ------------------------------------------------------------------
    static void Rect(float x, float y, float w, float h, Color c)
    {
        Function.Call(Hash.DRAW_RECT, (x + w / 2f) / 1280f, (y + h / 2f) / 720f, w / 1280f, h / 720f, (int)c.R, (int)c.G, (int)c.B, (int)c.A, false);
    }

    CustomSprite GetSprite(string file)
    {
        CustomSprite sp;
        if (sprites.TryGetValue(file, out sp)) return sp;
        try { sp = new CustomSprite(file, new SizeF(64f, 64f), PointF.Empty, Color.White, 0f, true); }
        catch (Exception ex) { Log(ex); sp = null; }
        sprites[file] = sp;
        return sp;
    }

    // draw an image centred on (cx, cy)
    bool Img(string file, float cx, float cy, float w, float h, Color tint)
    {
        if (string.IsNullOrEmpty(file)) return false;
        CustomSprite sp = GetSprite(file);
        if (sp == null) return false;
        sp.Size = new SizeF(w, h);
        sp.Position = new PointF(cx, cy);
        sp.Color = tint;
        sp.Draw();
        return true;
    }

    // a small soccer ball image, drawn once
    static void RenderBall(string file)
    {
        const int S = 64;
        using (Bitmap bmp = new Bitmap(S, S, PixelFormat.Format32bppArgb))
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            RectangleF r = new RectangleF(3f, 3f, S - 6f, S - 6f);
            float cx = S / 2f, cy = S / 2f;
            using (GraphicsPath clip = new GraphicsPath())
            {
                clip.AddEllipse(r);
                using (PathGradientBrush pb = new PathGradientBrush(clip))
                {
                    pb.CenterPoint = new PointF(S * 0.38f, S * 0.33f);
                    pb.CenterColor = Color.White;
                    pb.SurroundColors = new[] { Color.FromArgb(190, 196, 204) };
                    g.FillEllipse(pb, r);
                }
                g.SetClip(clip);
                using (Pen seam = new Pen(Color.FromArgb(70, 70, 75), 1.6f))
                    for (int i = 0; i < 5; i++)
                    {
                        double a = (-90 + 72 * i) * Math.PI / 180.0;
                        g.DrawLine(seam, cx + (float)Math.Cos(a) * 10f, cy + (float)Math.Sin(a) * 10f, cx + (float)Math.Cos(a) * 30f, cy + (float)Math.Sin(a) * 30f);
                    }
                Pentagon(g, cx, cy, 11f, -90f);
                for (int i = 0; i < 5; i++)
                {
                    double a = (-90 + 72 * i + 36) * Math.PI / 180.0;
                    Pentagon(g, cx + (float)Math.Cos(a) * 28f, cy + (float)Math.Sin(a) * 28f, 10f, -90f + 72 * i + 36 + 180);
                }
                g.ResetClip();
            }
            using (Pen o = new Pen(Color.FromArgb(40, 40, 45), 2f)) g.DrawEllipse(o, r);
            bmp.Save(file, ImageFormat.Png);
        }
    }

    static void Pentagon(Graphics g, float x, float y, float rad, float rotDeg)
    {
        PointF[] p = new PointF[5];
        for (int i = 0; i < 5; i++)
        {
            double a = (rotDeg + 72 * i) * Math.PI / 180.0;
            p[i] = new PointF(x + (float)Math.Cos(a) * rad, y + (float)Math.Sin(a) * rad);
        }
        using (SolidBrush b = new SolidBrush(Color.FromArgb(28, 28, 32))) g.FillPolygon(b, p);
    }
    static void Jewel(Graphics g, float x, float y, float r, Color c)
    {
        using (SolidBrush b = new SolidBrush(c)) g.FillEllipse(b, x - r, y - r, r * 2f, r * 2f);
        using (SolidBrush hl = new SolidBrush(Color.FromArgb(210, 255, 255, 255))) g.FillEllipse(hl, x - r * 0.5f, y - r * 0.6f, r * 0.65f, r * 0.65f);
        using (Pen p = new Pen(Color.FromArgb(210, 90, 60, 0), 1.5f)) g.DrawEllipse(p, x - r, y - r, r * 2f, r * 2f);
    }

    static void DrawCrown(Graphics g, float cx, float baseY, Color[] mc)
    {
        float w = 96f, h = 54f, x0 = cx - w / 2f, y0 = baseY - h;
        PointF[] pts =
        {
            new PointF(x0, baseY), new PointF(x0 - 3f, y0 + 14f), new PointF(x0 + w * 0.28f, y0 + 30f),
            new PointF(cx, y0), new PointF(x0 + w * 0.72f, y0 + 30f), new PointF(x0 + w + 3f, y0 + 14f),
            new PointF(x0 + w, baseY)
        };
        RectangleF bounds = new RectangleF(x0 - 4f, y0, w + 8f, h);
        using (LinearGradientBrush b = new LinearGradientBrush(bounds, mc[0], mc[2], 90f))
        {
            b.InterpolationColors = Blend3(mc);
            g.FillPolygon(b, pts);
        }
        using (Pen p = new Pen(Color.FromArgb(230, 110, 70, 0), 2.5f))
        {
            p.LineJoin = LineJoin.Round;
            g.DrawPolygon(p, pts);
        }
        RectangleF band = new RectangleF(x0, baseY - 13f, w, 13f);
        using (LinearGradientBrush b2 = new LinearGradientBrush(band, mc[0], mc[2], 90f))
        {
            b2.InterpolationColors = Blend3(mc);
            g.FillRectangle(b2, band);
        }
        using (Pen p = new Pen(Color.FromArgb(230, 110, 70, 0), 2f)) g.DrawRectangle(p, band.X, band.Y, band.Width, band.Height);
        Jewel(g, x0 - 3f, y0 + 14f, 6f, Color.FromArgb(225, 40, 60));
        Jewel(g, cx, y0, 7.5f, Color.FromArgb(40, 130, 235));
        Jewel(g, x0 + w + 3f, y0 + 14f, 6f, Color.FromArgb(225, 40, 60));
        Jewel(g, cx - 24f, baseY - 6.5f, 4f, Color.FromArgb(40, 190, 110));
        Jewel(g, cx, baseY - 6.5f, 4.5f, Color.FromArgb(225, 40, 60));
        Jewel(g, cx + 24f, baseY - 6.5f, 4f, Color.FromArgb(40, 190, 110));
    }

    static ColorBlend Blend3(Color[] c)
    {
        ColorBlend cb = new ColorBlend(3);
        cb.Colors = new[] { c[0], c[1], c[2] };
        cb.Positions = new[] { 0f, 0.5f, 1f };
        return cb;
    }

    static List<string> SplitList(string s)
    {
        List<string> l = new List<string>();
        foreach (string part in (s ?? "").Split(','))
        {
            string t = part.Trim();
            if (t.Length > 0) l.Add(t);
        }
        return l;
    }

    // ------------------------------------------------------------------
    //  Names in any language -> PNG (GTA fonts only have Latin letters)
    // ------------------------------------------------------------------
    NameTag GetTag(string text)
    {
        NameTag t;
        if (tags.TryGetValue(text, out t)) return t;
        t = new NameTag();
        if (!tags.TryAdd(text, t)) return tags[text];
        NameTag tag = t;
        string file = Path.Combine(tagDir, "n" + ((uint)text.GetHashCode()).ToString("x8") + "_" + tags.Count + ".png");
        ThreadPool.QueueUserWorkItem(delegate
        {
            try
            {
                float aspect;
                if (RenderTag(text, file, out aspect)) { tag.Aspect = aspect; tag.File = file; tag.Ready = true; }
            }
            catch (Exception ex) { Log(ex); }
        });
        return t;
    }

    static bool RenderTag(string text, string file, out float aspect)
    {
        aspect = 1f;
        // drop emoji / joiners that GDI+ would draw as boxes
        StringBuilder sb = new StringBuilder();
        foreach (char ch in text)
        {
            if (char.IsSurrogate(ch) || ch == '‍' || ch == '️' || (ch >= '☀' && ch <= '➿')) continue;
            sb.Append(ch);
        }
        string s = sb.ToString().Trim();
        if (s.Length == 0) return false;

        bool rtl = false;
        foreach (char ch in s)
        {
            if ((ch >= '֐' && ch <= 'ࣿ') || (ch >= 'יִ' && ch <= 'ﻼ')) { rtl = true; break; }
            if (char.IsLetter(ch)) break;
        }

        const int H = 48;
        using (System.Drawing.Font font = MakeFont(30f))
        using (StringFormat sf = new StringFormat(StringFormat.GenericTypographic))
        {
            sf.FormatFlags |= StringFormatFlags.NoWrap;
            if (rtl) sf.FormatFlags |= StringFormatFlags.DirectionRightToLeft;
            int w;
            using (Bitmap probe = new Bitmap(1, 1))
            using (Graphics pg = Graphics.FromImage(probe))
            {
                pg.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                w = (int)Math.Ceiling(pg.MeasureString(s, font, 4000, sf).Width) + 10;
            }
            w = Math.Max(10, Math.Min(900, w));
            using (Bitmap bmp = new Bitmap(w, H, PixelFormat.Format32bppArgb))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                RectangleF box = new RectangleF(3, 4, w - 6, H - 4);
                using (SolidBrush sh = new SolidBrush(Color.FromArgb(200, 0, 0, 0)))
                    g.DrawString(s, font, sh, new RectangleF(box.X + 2, box.Y + 2, box.Width, box.Height), sf);
                g.DrawString(s, font, Brushes.White, box, sf);
                bmp.Save(file, ImageFormat.Png);
            }
            aspect = w / (float)H;
        }
        return true;
    }

    // true when GTA's own font can draw the text (Latin letters only)
    static bool Renderable(string s)
    {
        if (s == null) return true;
        foreach (char ch in s) if (ch > 0x24F) return false;
        return true;
    }

    static string CleanName(string s)
    {
        s = (s ?? "").Replace("~", "-").Replace("\r", " ").Replace("\n", " ").Trim();
        if (s.Length > 24) s = s.Substring(0, 24);
        return s;
    }

    static string InitialOf(string alt)
    {
        string a = (alt ?? "").TrimStart('@');
        return a.Length > 0 ? a.Substring(0, 1).ToUpperInvariant() : "?";
    }

    static string Short(string s, int n) { return s.Length > n ? s.Substring(0, n - 1) + "." : s; }

    static System.Drawing.Font MakeFont(float px)
    {
        foreach (string fam in new[] { "Segoe UI", "Tahoma", "Arial" })
        {
            try
            {
                System.Drawing.Font f = new System.Drawing.Font(fam, px, FontStyle.Bold, GraphicsUnit.Pixel);
                if (f.Name == fam) return f;
                f.Dispose();
            }
            catch { }
        }
        return new System.Drawing.Font(FontFamily.GenericSansSerif, px, FontStyle.Bold, GraphicsUnit.Pixel);
    }

    static Bitmap LoadBitmap(string file)
    {
        using (MemoryStream ms = new MemoryStream(File.ReadAllBytes(file)))
        using (Image im = Image.FromStream(ms))
            return new Bitmap(im);
    }

    // ------------------------------------------------------------------
    //  Avatars (downloaded on a worker thread, cropped to a circle PNG)
    // ------------------------------------------------------------------
    AvatarInfo GetAvatar(string uid, string url)
    {
        if (string.IsNullOrEmpty(uid)) return null;
        if (url == null) url = "";
        AvatarInfo existing;
        if (avatars.TryGetValue(uid, out existing))
        {
            if (!(existing.Letter && existing.Ready && url.Length > 0)) return existing;
            AvatarInfo dropped;
            avatars.TryRemove(uid, out dropped);   // a real picture arrived after a letter avatar
        }

        AvatarInfo a = new AvatarInfo();
        if (!avatars.TryAdd(uid, a)) return avatars[uid];
        string safe = Regex.Replace(uid, "[^A-Za-z0-9_.-]", "_");
        string baseName = Path.Combine(cacheDir, safe);
        string file = baseName + ".png";
        ThreadPool.QueueUserWorkItem(delegate
        {
            try
            {
                if (url.Length > 0 && (!File.Exists(file) || !File.Exists(baseName + "_hex.png") || (DateTime.Now - File.GetLastWriteTime(file)).TotalDays > 3))
                {
                    using (Image img = DownloadImage(url))
                    {
                        if (img != null) { SaveCircle(img, file); SaveShapes(img, baseName); }
                    }
                }
                if (File.Exists(file))
                {
                    if (!File.Exists(baseName + "_hex.png")) { using (Bitmap old = LoadBitmap(file)) SaveShapes(old, baseName); }
                    a.Base = baseName; a.File = file; a.Ready = true; return;
                }
                // picture not available -> letter avatar (tried again next session)
                string initBase = Path.Combine(cacheDir, "letter_" + safe);
                RenderInitialAvatar(uid, initBase + ".png");
                a.Base = initBase; a.File = initBase + ".png"; a.Letter = url.Length > 0 ? false : true; a.Ready = true;
            }
            catch (Exception ex) { Log(ex); }
        });
        return a;
    }

    static Image DownloadImage(string url)
    {
        List<string> urls = new List<string>();
        foreach (string one in url.Split('\n'))
        {
            string u0 = one.Trim();
            if (u0.Length == 0) continue;
            urls.Add(u0);
            if (u0.Contains(".webp"))               // unsigned CDN pictures (gifts) also exist as png / jpeg
            {
                urls.Add(u0.Replace(".webp", ".png"));
                urls.Add(u0.Replace(".webp", ".jpeg"));
            }
        }
        foreach (string u in urls)
        {
            byte[] data;
            try
            {
                using (WebClient wc = new WebClient())
                {
                    wc.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120 Safari/537.36");
                    wc.Headers.Add("Referer", "https://www.tiktok.com/");
                    data = wc.DownloadData(u);
                }
            }
            catch { continue; }
            Image img = DecodeImage(data);
            if (img != null) return img;
        }
        return null;
    }

    // PNG/JPG/GIF/BMP with GDI+; WebP (TikTok profile pictures) with the Windows image codecs (WIC)
    static Image DecodeImage(byte[] data)
    {
        try
        {
            using (MemoryStream ms = new MemoryStream(data))
            using (Image img = Image.FromStream(ms))
                return new Bitmap(img);
        }
        catch { }
        Image wic = DecodeWithWindowsCodecs(data);
        if (wic != null) return wic;
        return TikArena.Wic.Decode(data);
    }

    static Image DecodeWithWindowsCodecs(byte[] data)
    {
        Image result = null;
        Thread t = new Thread(delegate ()
        {
            try
            {
                Assembly pc = Assembly.Load("PresentationCore, Version=4.0.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35");
                Type decT = pc.GetType("System.Windows.Media.Imaging.BitmapDecoder");
                Type createOpt = pc.GetType("System.Windows.Media.Imaging.BitmapCreateOptions");
                Type cacheOpt = pc.GetType("System.Windows.Media.Imaging.BitmapCacheOption");
                MethodInfo create = decT.GetMethod("Create", new Type[] { typeof(Stream), createOpt, cacheOpt });
                object dec = create.Invoke(null, new object[] { new MemoryStream(data), Enum.Parse(createOpt, "None"), Enum.Parse(cacheOpt, "OnLoad") });
                System.Collections.IList frames = (System.Collections.IList)decT.GetProperty("Frames").GetValue(dec, null);
                if (frames == null || frames.Count == 0) return;
                Type encT = pc.GetType("System.Windows.Media.Imaging.PngBitmapEncoder");
                object enc = Activator.CreateInstance(encT);
                System.Collections.IList encFrames = (System.Collections.IList)encT.GetProperty("Frames").GetValue(enc, null);
                encFrames.Add(frames[0]);
                using (MemoryStream outMs = new MemoryStream())
                {
                    encT.GetMethod("Save", new Type[] { typeof(Stream) }).Invoke(enc, new object[] { outMs });
                    outMs.Position = 0;
                    using (Image im = Image.FromStream(outMs)) result = new Bitmap(im);
                }
            }
            catch { result = null; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true;
        t.Start();
        t.Join(8000);
        return result;
    }

    // no picture could be loaded -> a coloured circle with the first letter
    static void RenderInitialAvatar(string name, string file)
    {
        string n = (name ?? "?").TrimStart('@').Trim();
        string letter = n.Length > 0 ? n.Substring(0, 1).ToUpperInvariant() : "?";
        int h = 0;
        foreach (char ch in n) h = h * 31 + ch;
        Color[] pal = { Color.FromArgb(233, 30, 99), Color.FromArgb(156, 39, 176), Color.FromArgb(63, 81, 181), Color.FromArgb(3, 169, 244),
                        Color.FromArgb(0, 150, 136), Color.FromArgb(76, 175, 80), Color.FromArgb(255, 152, 0), Color.FromArgb(244, 67, 54) };
        Color c = pal[(h & 0x7FFFFFFF) % pal.Length];
        using (Bitmap bmp = new Bitmap(128, 128, PixelFormat.Format32bppArgb))
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(c);
            using (System.Drawing.Font f = MakeFont(64f))
            using (StringFormat sf = new StringFormat())
            {
                sf.Alignment = StringAlignment.Center;
                sf.LineAlignment = StringAlignment.Center;
                g.DrawString(letter, f, Brushes.White, new RectangleF(0, 4, 128, 128), sf);
            }
            using (Image copy = new Bitmap(bmp))
            {
                SaveCircle(copy, file);
                SaveShapes(copy, file.EndsWith(".png") ? file.Substring(0, file.Length - 4) : file);
            }
        }
    }

    // the photo cut in every shape the HUD designs use (no border: the designs draw their own frames)
    static void SaveShapes(Image src, string baseName)
    {
        string[] shapes = { "circle", "rounded", "square", "hex" };
        foreach (string sh in shapes)
        {
            const int SZ = 128;
            using (Bitmap bmp = new Bitmap(SZ, SZ, PixelFormat.Format32bppArgb))
            using (Graphics g = Graphics.FromImage(bmp))
            using (GraphicsPath gp = new GraphicsPath())
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);
                switch (sh)
                {
                    case "hex": gp.AddPolygon(HexPts(SZ)); break;
                    case "rounded": AddRound(gp, 1f, 1f, SZ - 2f, SZ - 2f, SZ * 0.26f); break;
                    case "square": AddRound(gp, 1f, 1f, SZ - 2f, SZ - 2f, SZ * 0.09f); break;
                    default: gp.AddEllipse(1, 1, SZ - 2, SZ - 2); break;
                }
                using (TextureBrush tb = new TextureBrush(src, WrapMode.Clamp))
                {
                    float k = Math.Max(SZ / (float)src.Width, SZ / (float)src.Height);
                    tb.ScaleTransform(k, k);
                    tb.TranslateTransform((SZ - src.Width * k) / 2f / k, (SZ - src.Height * k) / 2f / k, MatrixOrder.Prepend);
                    g.FillPath(tb, gp);
                }
                bmp.Save(baseName + "_" + sh + ".png", ImageFormat.Png);
            }
        }
    }

    static void SaveSquare(Image src, string file, int size)
    {
        using (Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.Clear(Color.Transparent);
            float k = Math.Min(size / (float)src.Width, size / (float)src.Height);
            float w = src.Width * k, h = src.Height * k;
            g.DrawImage(src, (size - w) / 2f, (size - h) / 2f, w, h);
            bmp.Save(file, ImageFormat.Png);
        }
    }

    static void SaveCircle(Image src, string file)
    {
        const int SZ = 128;
        using (Bitmap bmp = new Bitmap(SZ, SZ, PixelFormat.Format32bppArgb))
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.Clear(Color.Transparent);
            using (GraphicsPath gp = new GraphicsPath())
            {
                gp.AddEllipse(4, 4, SZ - 8, SZ - 8);
                g.SetClip(gp);
                g.DrawImage(src, new Rectangle(4, 4, SZ - 8, SZ - 8));
                g.ResetClip();
            }
            using (Pen pen = new Pen(Color.White, 5f)) g.DrawEllipse(pen, 4, 4, SZ - 8, SZ - 8);
            bmp.Save(file, ImageFormat.Png);
        }
    }

    void ShowBig(string title, string sub, int ms)
    {
        if (bigMsg == null) bigMsg = new Scaleform("MP_BIG_MESSAGE_FREEMODE");
        bigTitle = title;
        bigSub = sub ?? "";
        bigPending = true;
        bigUntil = Game.GameTime + ms;
    }

    void DrawBig()
    {
        if (bigMsg == null || Game.GameTime > bigUntil || !bigMsg.IsLoaded) return;
        if (bigPending)
        {
            bigMsg.CallFunction("SHOW_SHARD_WASTED_MP_MESSAGE", bigTitle, bigSub);
            bigPending = false;
        }
        bigMsg.Render2D();
    }



    void SetHudStyle(int st)
    {
        hudStyle = ((st % StyleNames.Length) + StyleNames.Length) % StyleNames.Length;
        cfg.HudStyle = hudStyle;
        SaveSetting("HUD", "Style", hudStyle.ToString());
        Notification.Show("~b~HUD style~s~: " + StyleNames[hudStyle]);
    }

    // ------------------------------------------------------------------
    //  MEGA GIFT (big gift celebration)
    // ------------------------------------------------------------------
    class BigGiftEv { public string Name, Alt, Gift; public AvatarInfo Av; public int Count; public long Coins; public int Seed; }
    readonly ConcurrentQueue<BigGiftEv> bigIn = new ConcurrentQueue<BigGiftEv>();
    BigGiftEv bigCur;
    int bigStart;
    /// <summary>Called when a mega gift starts on screen (play your sound / fireworks here).</summary>
    public Action<string> OnMegaGiftStart = delegate { };

    void UpdateMegaGift(int now)
    {
        if (bigCur != null && now - bigStart > cfg.BigMs) bigCur = null;
        if (bigCur == null)
        {
            BigGiftEv e;
            if (!bigIn.TryDequeue(out e)) return;
            bigCur = e; bigStart = now;
            try { OnMegaGiftStart(e.Alt); } catch { }
        }
    }

    void DrawMegaGift(int now)
    {
        BigGiftEv e = bigCur;
        if (e == null) return;
        int el = now - bigStart;
        float T = Math.Max(1f, cfg.BigMs), t = el / T;
        if (t > 1f) return;
        float fade = t < 0.06f ? t / 0.06f : (t > 0.86f ? Math.Max(0f, (1f - t) / 0.14f) : 1f);
        float s = Sc, cx = VCX, cy = VY(cfg.BigY), sec = el / 1000f;
        Color col = cfg.BigColor;
        float pop = el < 350 ? 0.4f + 0.75f * (el / 350f) : (el < 520 ? 1.15f - 0.15f * ((el - 350) / 170f) : 1f);
        float D = 300f * s * pop;

        // light behind + rotating rays of glow
        Glow(cx, cy, (820f + 60f * (float)Math.Sin(sec * 3f)) * s, A(col, (int)(150 * fade)));
        Glow(cx, cy, 420f * s, A(Color.White, (int)(90 * fade)));

        // ripples: rings of light that go out from the photo like water
        for (int k = 0; k < 5; k++)
        {
            float ph = (sec * 0.8f + k / 5f) % 1f;
            float d = D * (1.05f + ph * 2.3f);
            Im(Ui("ring"), cx, cy, d, d, A(k % 2 == 0 ? col : Color.White, (int)((1f - ph) * 210 * fade)));
        }

        // the photo ripples (jelly wave) and floats
        float wave = (float)Math.Sin(sec * 11f), wave2 = (float)Math.Sin(sec * 7f + 1.3f);
        float w = D * (1f + 0.07f * wave), h = D * (1f - 0.07f * wave);
        float py = cy + 10f * s * wave2;
        ShapeFill("circle", cx, py, Math.Max(w, h) * 1.12f, A(col, (int)(255 * fade)));
        ShapeFill("circle", cx, py, Math.Max(w, h) * 1.045f, A(Color.FromArgb(20, 20, 26), (int)(255 * fade)));
        string f = AvatarFile(e.Av, "circle");
        if (f == null || !Im(f, cx, py, w, h, A(Color.White, (int)(255 * fade))))
        {
            ShapeFill("circle", cx, py, w, A(Color.FromArgb(70, 76, 90), (int)(255 * fade)));
            TI(InitialOf(e.Alt), cx, py, w * 0.34f, A(Color.White, (int)(255 * fade)), AC);
        }

        // sparkles turning around the photo
        for (int k = 0; k < 14; k++)
        {
            double a = sec * (k % 2 == 0 ? 1.3 : -0.9) + k * Math.PI * 2 / 14;
            float rr = D * (0.72f + 0.08f * (float)Math.Sin(sec * 4 + k));
            float sz = (10f + 8f * (float)Math.Abs(Math.Sin(sec * 6 + k))) * s;
            Glow(cx + (float)Math.Cos(a) * rr, py + (float)Math.Sin(a) * rr, sz * 3f, A(k % 3 == 0 ? Color.White : col, (int)(230 * fade)));
        }

        // falling gold confetti (pictures, so they are above everything)
        Random r = new Random(e.Seed);
        for (int i = 0; i < 56; i++)
        {
            float x = (float)r.NextDouble() * VW, spd = (200f + (float)r.NextDouble() * 260f) * s, delay = (float)r.NextDouble() * 1.2f;
            float tt = sec - delay;
            if (tt <= 0f) continue;
            float y = -40f + tt * spd;
            if (y > 1120f) continue;
            float sz = (8f + (float)r.NextDouble() * 10f) * s * (0.4f + 0.6f * (float)Math.Abs(Math.Sin(tt * 7 + i)));
            Color cc = i % 4 == 0 ? Color.White : (i % 4 == 1 ? col : (i % 4 == 2 ? Color.FromArgb(255, 77, 109) : Color.FromArgb(63, 169, 255)));
            Im(Ui("dot"), x + 30f * (float)Math.Sin(tt * 2 + i), y, sz, sz * 1.4f, A(cc, (int)(235 * fade)));
        }

        // texts (pictures)
        Title(cfg.BigTitle, cx, cy - D * 0.5f - 95f * s, 110f * s * pop, col, fade);
        float nw = 560f * s;
        {
            // name box made of pictures (so it stays above the light)
            float bh = 70f * s, by = py + h * 0.5f + 26f * s + bh / 2f;
            Color bc = A(Color.FromArgb(225, 12, 14, 22), fade);
            Im(Ui("px"), cx, by, nw - bh, bh, bc);
            ShapeFill("circle", cx - nw / 2f + bh / 2f, by, bh, bc);
            ShapeFill("circle", cx + nw / 2f - bh / 2f, by, bh, bc);
        }
        NI(e.Name, e.Alt, cx, py + h * 0.5f + 61f * s, 36f * s, A(Color.White, fade), AC, nw - 40f * s);
        string line = e.Gift + "  x" + e.Count + (e.Coins > 0 ? "   -   " + e.Coins + " COINS" : "");
        TI(line, cx, py + h * 0.5f + 128f * s, 30f * s, A(col, fade), AC);
    }
}
}
