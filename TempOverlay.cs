// TempOverlay - temperatura sobreposta a qualquer programa (cidade via Open-Meteo ou arquivo TXT).
// Nativo (.NET Framework 4.x, ja vem no Windows): sem runtime extra, poucos MB de RAM.
// Compilar: build.ps1 (csc.exe do proprio Windows, C# 5).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("TempOverlay")]
[assembly: AssemblyVersion("1.5.0.0")]

namespace TempOverlay
{
    static class Const
    {
        public const string App = "TempOverlay";
        public const string Version = "1.5.0";
        public const string GithubRepo = "lnpti/TempOverlay";
        public const string R2Manifest = "https://pub-8060abbe70084968817647c74ce4ffbc.r2.dev/tempoverlay/latest.json";
        public const int UpdateHour = 3; // checagem de madrugada (alem de a cada 24 h)
    }

    static class Win
    {
        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr h, int i, int v);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")]
        public static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dst, ref Point pdst, ref Size sz, IntPtr src,
            ref Point psrc, int key, ref BLEND blend, int flags);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr o);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
        [DllImport("psapi.dll")] public static extern bool EmptyWorkingSet(IntPtr h);

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BLEND { public byte Op, Flags, Alpha, Format; }

        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;

        public static void Trim()
        {
            try { GC.Collect(); GC.WaitForPendingFinalizers(); EmptyWorkingSet(Process.GetCurrentProcess().Handle); }
            catch { }
        }
    }

    // ---------- configuracao (mesmo config.json da versao anterior) ----------
    class Config
    {
        public string Source = "city", CityName = "", CityDesc = "", TxtPath = "", Suffix = "°C", Label = "";
        public string FontFamily = "Segoe UI", TextColor = "#FFFFFF", Style = "transparent", CardColor = "#101820";
        public double? Lat, Lon;
        public int RefreshMin = 10, Decimals = 0, FontSize = 48, Opacity = 100, X = 40, Y = 40;
        public bool ShowLabel = true, Locked = false, Autostart = false, AutoUpdate = true;
        public double LastUpdateCheck = 0;
        public bool ShowClock = true, ShowSeconds = true, ShowDate = true, ShowIcon = true;
        public string LogoPath = "";
        public int Scale = 100;
        public string AirIp = "", AirChannels = "1", AirText = "NO AR";
        public bool AirFader = true;
        public string GraphCh = "1", GraphLabel = "", GraphStyle = "gradient", GraphColorA = "", GraphColorB = "";
        public bool GraphReflect;
        public int GraphGain = 100;

        public static readonly string Dir = Environment.GetEnvironmentVariable("TEMPOVERLAY_DIR") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Const.App);
        public static readonly string FilePath = Path.Combine(Dir, "config.json");

        public Config Clone() { return (Config)MemberwiseClone(); }
        public bool HasAir { get { return Style == "panel_air" || Style == "panel2_air" || Style == "panel2_air_rta" || Style == "panel3_air_graph"; } }
        public bool HasGraph { get { return Style == "panel3_air_graph"; } }
        public bool HasRta { get { return Style == "panel2_air_rta"; } }
        public bool IsPanel { get { return Style == "panel" || Style == "panel2" || HasAir; } }
        public int PanelKind { get { return Style == "panel3_air_graph" ? 3 : ((Style == "panel2" || Style == "panel2_air" || Style == "panel2_air_rta") ? 2 : 1); } }

        public static Config Load()
        {
            Config c = new Config();
            try
            {
                Dictionary<string, string> d = Json.ParseFlat(File.ReadAllText(FilePath, Encoding.UTF8));
                c.Source = Json.S(d, "source", c.Source); c.CityName = Json.S(d, "city_name", ""); c.CityDesc = Json.S(d, "city_desc", "");
                c.TxtPath = Json.S(d, "txt_path", ""); c.Suffix = Json.S(d, "suffix", c.Suffix); c.Label = Json.S(d, "label", "");
                c.FontFamily = Json.S(d, "font_family", c.FontFamily); c.TextColor = Json.S(d, "text_color", c.TextColor);
                c.Style = Json.S(d, "style", c.Style); c.CardColor = Json.S(d, "card_color", c.CardColor);
                c.Lat = Json.D(d, "lat"); c.Lon = Json.D(d, "lon");
                c.RefreshMin = Json.I(d, "refresh_min", c.RefreshMin); c.Decimals = Json.I(d, "decimals", c.Decimals);
                c.FontSize = Json.I(d, "font_size", c.FontSize); c.Opacity = Json.I(d, "opacity", c.Opacity);
                c.X = Json.I(d, "x", c.X); c.Y = Json.I(d, "y", c.Y);
                c.ShowLabel = Json.B(d, "show_label", c.ShowLabel); c.Locked = Json.B(d, "locked", c.Locked);
                c.Autostart = Json.B(d, "autostart", c.Autostart); c.AutoUpdate = Json.B(d, "auto_update", c.AutoUpdate);
                double? l = Json.D(d, "last_update_check"); if (l.HasValue) c.LastUpdateCheck = l.Value;
                c.ShowClock = Json.B(d, "show_clock", c.ShowClock); c.ShowSeconds = Json.B(d, "show_seconds", c.ShowSeconds);
                c.ShowDate = Json.B(d, "show_date", c.ShowDate); c.ShowIcon = Json.B(d, "show_icon", c.ShowIcon);
                c.LogoPath = Json.S(d, "logo_path", "");
                c.Scale = Math.Max(30, Math.Min(400, Json.I(d, "scale", 100)));
                c.AirIp = Json.S(d, "air_ip", ""); c.AirChannels = Json.S(d, "air_channels", "1");
                c.AirText = Json.S(d, "air_text", "NO AR"); c.AirFader = Json.B(d, "air_fader", true);
                c.GraphCh = Json.S(d, "graph_ch", "1"); c.GraphLabel = Json.S(d, "graph_label", "");
                c.GraphStyle = Json.S(d, "graph_style", "gradient"); c.GraphReflect = Json.B(d, "graph_reflect", false);
                c.GraphColorA = Json.S(d, "graph_color_a", ""); c.GraphColorB = Json.S(d, "graph_color_b", ""); c.GraphGain = Math.Max(10, Math.Min(400, Json.I(d, "graph_gain", 100)));
            }
            catch { }
            return c;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                StringBuilder sb = new StringBuilder("{\n");
                Action<string, string> s = delegate(string k, string v) { sb.Append("  \"" + k + "\": " + Json.Str(v) + ",\n"); };
                Action<string, string> r = delegate(string k, string v) { sb.Append("  \"" + k + "\": " + v + ",\n"); };
                Func<bool, string> b = delegate(bool v) { return v ? "true" : "false"; };
                Func<double?, string> n = delegate(double? v) { return v.HasValue ? v.Value.ToString("R", CultureInfo.InvariantCulture) : "null"; };
                s("source", Source); s("city_name", CityName); s("city_desc", CityDesc); r("lat", n(Lat)); r("lon", n(Lon));
                s("txt_path", TxtPath); r("refresh_min", RefreshMin.ToString()); s("suffix", Suffix); r("decimals", Decimals.ToString());
                r("show_label", b(ShowLabel)); s("label", Label); r("font_size", FontSize.ToString()); s("font_family", FontFamily);
                s("text_color", TextColor); s("style", Style); s("card_color", CardColor); r("opacity", Opacity.ToString());
                r("locked", b(Locked)); r("x", X.ToString()); r("y", Y.ToString()); r("autostart", b(Autostart));
                r("auto_update", b(AutoUpdate)); r("show_clock", b(ShowClock)); r("show_seconds", b(ShowSeconds));
                r("show_date", b(ShowDate)); r("show_icon", b(ShowIcon)); s("logo_path", LogoPath); r("scale", Scale.ToString());
                s("air_ip", AirIp); s("air_channels", AirChannels); s("air_text", AirText); r("air_fader", b(AirFader)); s("graph_ch", GraphCh); s("graph_label", GraphLabel); s("graph_style", GraphStyle); r("graph_reflect", b(GraphReflect)); s("graph_color_a", GraphColorA); s("graph_color_b", GraphColorB); r("graph_gain", GraphGain.ToString());
                sb.Append("  \"last_update_check\": " + LastUpdateCheck.ToString("R", CultureInfo.InvariantCulture) + "\n}\n");
                File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }
    }

    // JSON minimo: so o necessario para o config plano e as respostas das APIs.
    static class Json
    {
        static readonly Regex Pair = new Regex("\"([^\"]+)\"\\s*:\\s*(\"(?:[^\"\\\\]|\\\\.)*\"|[-+0-9.eE]+|true|false|null)");

        public static Dictionary<string, string> ParseFlat(string text)
        {
            Dictionary<string, string> d = new Dictionary<string, string>();
            foreach (Match m in Pair.Matches(text)) d[m.Groups[1].Value] = m.Groups[2].Value;
            return d;
        }
        public static string Unescape(string s)
        {
            if (s.Length >= 2 && s[0] == '"') s = s.Substring(1, s.Length - 2);
            return Regex.Replace(s, "\\\\(u[0-9a-fA-F]{4}|.)", delegate(Match m)
            {
                string g = m.Groups[1].Value;
                if (g.Length == 5) return ((char)Convert.ToInt32(g.Substring(1), 16)).ToString();
                switch (g) { case "n": return "\n"; case "t": return "\t"; case "r": return "\r"; default: return g; }
            });
        }
        public static string Str(string s)
        {
            return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t") + "\"";
        }
        public static string S(Dictionary<string, string> d, string k, string def)
        {
            string v; if (!d.TryGetValue(k, out v) || v == "null") return def; return Unescape(v);
        }
        public static double? D(Dictionary<string, string> d, string k)
        {
            string v; double r;
            if (d.TryGetValue(k, out v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out r)) return r;
            return null;
        }
        public static int I(Dictionary<string, string> d, string k, int def)
        {
            double? v = D(d, k); return v.HasValue ? (int)v.Value : def;
        }
        public static bool B(Dictionary<string, string> d, string k, bool def)
        {
            string v; if (!d.TryGetValue(k, out v)) return def; return v == "true" ? true : (v == "false" ? false : def);
        }
    }

    class Place { public string Name, Desc; public double Lat, Lon; }

    // Lista embutida (RS) + coordenadas digitadas: funciona mesmo se a rede bloquear o site de busca.
    static class Offline
    {
        const string Table =
            "Porto Alegre|-30.0346|-51.2177;Erechim|-27.6346|-52.2754;Marau|-28.4498|-52.2007;Sarandi|-27.9437|-52.9227;" +
            "Cacique Doble|-27.7667|-51.6588;Lagoa Vermelha|-28.2086|-51.5258;Passo Fundo|-28.2628|-52.4067;Caxias do Sul|-29.1678|-51.1794;" +
            "Santa Maria|-29.6842|-53.8069;Pelotas|-31.7654|-52.3376;Canoas|-29.9178|-51.1839;Novo Hamburgo|-29.6783|-51.1306;" +
            "São Leopoldo|-29.7604|-51.1472;Gravataí|-29.9444|-50.9919;Viamão|-30.0811|-51.0233;Santa Cruz do Sul|-29.7178|-52.4258;" +
            "Uruguaiana|-29.7547|-57.0883;Bagé|-31.3314|-54.1069;Rio Grande|-32.035|-52.0986;Ijuí|-28.3878|-53.9147;" +
            "Santo Ângelo|-28.2992|-54.2631;Cruz Alta|-28.6392|-53.6064;Carazinho|-28.2839|-52.7861;Vacaria|-28.5122|-50.9339;" +
            "Bento Gonçalves|-29.1661|-51.5189;Farroupilha|-29.2253|-51.3475;Lajeado|-29.4669|-51.9614;Santa Rosa|-27.8708|-54.4814;" +
            "Frederico Westphalen|-27.3592|-53.3942;Palmeira das Missões|-27.8989|-53.3139;Santiago|-29.1917|-54.8667;" +
            "São Borja|-28.6578|-56.0044;Alegrete|-29.7831|-55.7919;Camaquã|-30.8511|-51.8117;Torres|-29.3353|-49.7269;" +
            "Tramandaí|-30.0|-50.1333;Guaíba|-30.1139|-51.325;Cachoeira do Sul|-30.0397|-52.8939;Venâncio Aires|-29.6|-52.1917;" +
            "Montenegro|-29.6889|-51.4608;Estrela|-29.5008|-51.9622;Soledade|-28.8169|-52.5128;Getúlio Vargas|-27.8903|-52.2275;" +
            "Gaurama|-27.5864|-52.0872;Tapejara|-28.0689|-52.0089;Casca|-28.5614|-51.9778;Nova Prata|-28.78|-51.6144;" +
            "Encantado|-29.2392|-51.8706;Espumoso|-28.7258|-52.8508;Ibirubá|-28.6278|-53.0889;Não-Me-Toque|-28.4558|-52.8197;" +
            "Tapera|-28.6247|-52.8644;Sananduva|-27.9544|-51.8092;Ciríaco|-28.3414|-51.8719;David Canabarro|-28.3881|-51.8481;" +
            "Ibiaçá|-28.0561|-51.8517;Paim Filho|-27.7033|-51.7608;Barracão|-27.67|-51.46;Santa Bárbara do Sul|-28.3664|-53.2461;" +
            "Tupanciretã|-29.0817|-53.8381;São Gabriel|-30.3367|-54.32;Sapucaia do Sul|-29.8283|-51.145;Esteio|-29.8614|-51.1789;" +
            "Cachoeirinha|-29.9472|-51.0939;Alvorada|-29.99|-51.0833;Sapiranga|-29.6358|-50.9261;Taquara|-29.65|-50.7778;" +
            "Gramado|-29.3789|-50.8742;Canela|-29.3656|-50.8119;Osório|-29.8886|-50.2694;Capão da Canoa|-29.7458|-50.0114";

        static string Norm(string t)
        {
            string d = t.Normalize(NormalizationForm.FormD); StringBuilder sb = new StringBuilder();
            foreach (char ch in d) if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(char.ToLowerInvariant(ch));
            return sb.ToString().Trim();
        }

        public static List<Place> Search(string q)
        {
            List<Place> list = new List<Place>();
            Match m = Regex.Match(q, "^\\s*(-?\\d+(?:[.,]\\d+)?)\\s*[;, ]\\s*(-?\\d+(?:[.,]\\d+)?)\\s*$");
            if (m.Success)
            {
                Place c = new Place(); c.Name = "Local";
                c.Lat = double.Parse(m.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
                c.Lon = double.Parse(m.Groups[2].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
                c.Desc = "Coordenadas " + c.Lat.ToString("0.####", CultureInfo.InvariantCulture) + ", " + c.Lon.ToString("0.####", CultureInfo.InvariantCulture);
                list.Add(c); return list;
            }
            string n = Norm(q); if (n.Length == 0) return list;
            foreach (string row in Table.Split(';'))
            {
                string[] f = row.Split('|');
                if (Norm(f[0]).Contains(n))
                {
                    Place p = new Place(); p.Name = f[0]; p.Desc = f[0] + ", Rio Grande do Sul, Brasil";
                    p.Lat = double.Parse(f[1], CultureInfo.InvariantCulture); p.Lon = double.Parse(f[2], CultureInfo.InvariantCulture);
                    list.Add(p);
                }
            }
            return list;
        }
    }

    // ---------- rede ----------
    static class Net
    {
        static Net() { try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; } catch { } }

        static HttpWebRequest Req(string url, int timeout)
        {
            HttpWebRequest r = (HttpWebRequest)WebRequest.Create(url);
            r.UserAgent = Const.App + "/" + Const.Version; r.Timeout = timeout; r.ReadWriteTimeout = timeout;
            return r;
        }
        static void Log(string msg)
        {
            try
            {
                Directory.CreateDirectory(Config.Dir);
                string f = Path.Combine(Config.Dir, "rede.log");
                if (File.Exists(f) && new FileInfo(f).Length > 40000) File.Delete(f);
                File.AppendAllText(f, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + msg + Environment.NewLine);
            }
            catch { }
        }

        // GET normal (respeita proxy do Windows); se falhar/demorar, cai para conexao forcada por IPv4
        // (redes com IPv6 quebrado deixam o .NET pendurado em hosts que tem IPv4 e IPv6).
        public static string Get(string url)
        {
            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                using (WebResponse resp = Req(url, 9000).GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) return sr.ReadToEnd();
            }
            catch (Exception ex)
            {
                Log("GET http falhou (" + sw.ElapsedMilliseconds + " ms) " + new Uri(url).Host + ": " + ex.Message + " -> tentando IPv4");
            }
            byte[] data = RawGet(url, 5);
            Log("GET via IPv4 ok " + new Uri(url).Host + " (" + sw.ElapsedMilliseconds + " ms)");
            return Encoding.UTF8.GetString(data);
        }

        public static string Download(string url)
        {
            string dir = Path.Combine(Path.GetTempPath(), "tempoverlay_upd_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            string dest = Path.Combine(dir, "TempOverlay_Setup.exe");
            try
            {
                using (WebResponse resp = Req(url, 20000).GetResponse())
                using (Stream rs = resp.GetResponseStream())
                using (FileStream fs = File.Create(dest)) rs.CopyTo(fs);
            }
            catch (Exception ex)
            {
                Log("download http falhou " + new Uri(url).Host + ": " + ex.Message + " -> tentando IPv4");
                File.WriteAllBytes(dest, RawGet(url, 5));
            }
            FileInfo fi = new FileInfo(dest);
            using (FileStream fs = File.OpenRead(dest))
                if (fi.Length < 100000 || fs.ReadByte() != 'M' || fs.ReadByte() != 'Z') throw new Exception("download invalido");
            return dest;
        }

        static int IndexOf(byte[] d, byte[] pat, int from)
        {
            for (int i = from; i <= d.Length - pat.Length; i++)
            {
                int j = 0; while (j < pat.Length && d[i + j] == pat[j]) j++;
                if (j == pat.Length) return i;
            }
            return -1;
        }

        // HTTP/1.1 minimo sobre IPv4 + TLS 1.2 (segue redirecionamentos, decodifica chunked)
        static byte[] RawGet(string url, int redirects)
        {
            Uri u = new Uri(url);
            List<IPAddress> v4 = new List<IPAddress>();
            foreach (IPAddress ip in Dns.GetHostAddresses(u.Host)) if (ip.AddressFamily == AddressFamily.InterNetwork) v4.Add(ip);
            if (v4.Count == 0) throw new Exception("sem endereco IPv4 para " + u.Host);
            TcpClient cli = null; Exception last = null;
            foreach (IPAddress ip in v4)
            {
                try
                {
                    TcpClient c = new TcpClient(AddressFamily.InterNetwork);
                    IAsyncResult ar = c.BeginConnect(ip, u.Port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(8000)) { c.Close(); throw new TimeoutException("tempo esgotado conectando a " + u.Host); }
                    c.EndConnect(ar); cli = c; break;
                }
                catch (Exception ex) { last = ex; }
            }
            if (cli == null) throw last ?? new Exception("sem conexao");
            byte[] data;
            using (cli)
            {
                cli.ReceiveTimeout = 30000; cli.SendTimeout = 30000;
                Stream st = cli.GetStream();
                if (u.Scheme == "https")
                {
                    SslStream ss = new SslStream(st);
                    ss.AuthenticateAsClient(u.Host, null, System.Security.Authentication.SslProtocols.Tls12, false);
                    st = ss;
                }
                byte[] req = Encoding.ASCII.GetBytes("GET " + u.PathAndQuery + " HTTP/1.1\r\nHost: " + u.Host + "\r\nUser-Agent: " +
                    Const.App + "/" + Const.Version + "\r\nAccept: */*\r\nConnection: close\r\n\r\n");
                st.Write(req, 0, req.Length);
                using (MemoryStream all = new MemoryStream()) { st.CopyTo(all); data = all.ToArray(); }
            }
            int he = IndexOf(data, new byte[] { 13, 10, 13, 10 }, 0);
            if (he < 0) throw new Exception("resposta invalida");
            string head = Encoding.ASCII.GetString(data, 0, he);
            int status = int.Parse(head.Substring(9, 3));
            if (status >= 300 && status < 400 && redirects > 0)
            {
                Match loc = Regex.Match(head, "(?im)^location:\\s*(\\S+)");
                if (loc.Success) return RawGet(new Uri(u, loc.Groups[1].Value).ToString(), redirects - 1);
            }
            if (status >= 400) throw new Exception("HTTP " + status);
            int pos = he + 4;
            if (Regex.IsMatch(head, "(?im)^transfer-encoding:\\s*chunked"))
            {
                MemoryStream body = new MemoryStream();
                while (pos < data.Length)
                {
                    int le = IndexOf(data, new byte[] { 13, 10 }, pos);
                    if (le < 0) break;
                    string hex = Encoding.ASCII.GetString(data, pos, le - pos).Split(';')[0].Trim();
                    int len = Convert.ToInt32(hex, 16);
                    if (len == 0) break;
                    body.Write(data, le + 2, Math.Min(len, data.Length - le - 2));
                    pos = le + 2 + len + 2;
                }
                return body.ToArray();
            }
            byte[] outb = new byte[data.Length - pos];
            Buffer.BlockCopy(data, pos, outb, 0, outb.Length);
            return outb;
        }

        public static List<Place> SearchCity(string name)
        {
            string url = "https://geocoding-api.open-meteo.com/v1/search?name=" + Uri.EscapeDataString(name) + "&count=8&language=pt&format=json";
            string txt = Get(url);
            List<Place> list = new List<Place>();
            foreach (Match m in Regex.Matches(txt, "\\{\"id\":[^{}]*\\}"))
            {
                Dictionary<string, string> d = Json.ParseFlat(m.Value);
                double? la = Json.D(d, "latitude"), lo = Json.D(d, "longitude");
                if (!la.HasValue || !lo.HasValue) continue;
                Place p = new Place(); p.Name = Json.S(d, "name", ""); p.Lat = la.Value; p.Lon = lo.Value;
                List<string> parts = new List<string>();
                foreach (string k in new string[] { "name", "admin1", "country" }) { string v = Json.S(d, k, ""); if (v.Length > 0) parts.Add(v); }
                p.Desc = string.Join(", ", parts.ToArray());
                list.Add(p);
            }
            return list;
        }
        public static double CityTemp(double lat, double lon, out int code)
        {
            string url = "https://api.open-meteo.com/v1/forecast?latitude=" + lat.ToString(CultureInfo.InvariantCulture) +
                         "&longitude=" + lon.ToString(CultureInfo.InvariantCulture) + "&current=temperature_2m,weather_code";
            Match blk = Regex.Match(Get(url), "\"current\"\\s*:\\s*\\{([^}]*)\\}");
            Match m = Regex.Match(blk.Value, "\"temperature_2m\"\\s*:\\s*(-?[0-9.]+)");
            if (!m.Success) throw new Exception("sem temperatura");
            Match wc = Regex.Match(blk.Value, "\"weather_code\"\\s*:\\s*(\\d+)");
            code = wc.Success ? int.Parse(wc.Groups[1].Value) : -1;
            return double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        public static bool LatestRelease(out string version, out string installerUrl)
        {
            try
            {
                string t = Get("https://api.github.com/repos/" + Const.GithubRepo + "/releases/latest");
                Match tag = Regex.Match(t, "\"tag_name\"\\s*:\\s*\"v?([^\"]+)\"");
                Match url = Regex.Match(t, "\"browser_download_url\"\\s*:\\s*\"([^\"]*[Ss]etup[^\"]*\\.exe)\"");
                if (tag.Success && url.Success) { version = tag.Groups[1].Value; installerUrl = url.Groups[1].Value; return true; }
            }
            catch { }
            string r2 = Get(Const.R2Manifest);
            Match v = Regex.Match(r2, "\"version\"\\s*:\\s*\"([^\"]+)\"");
            Match u = Regex.Match(r2, "\"installer_url\"\\s*:\\s*\"([^\"]+)\"");
            version = v.Success ? v.Groups[1].Value : "0"; installerUrl = u.Success ? u.Groups[1].Value : "";
            return v.Success && u.Success;
        }
        public static Version Ver(string s)
        {
            Match m = Regex.Match(s, "(\\d+)(?:\\.(\\d+))?(?:\\.(\\d+))?");
            Func<int, int> g = delegate(int i) { return m.Groups[i].Success ? int.Parse(m.Groups[i].Value) : 0; };
            return new Version(g(1), g(2), g(3));
        }
    }

    // ---------- Behringer X32 (OSC por UDP, porta 10023): canal aberto/mudo + fader ----------
    static class X32
    {
        const int Port = 10023;

        static byte[] Pad(string s)
        {
            byte[] b = Encoding.ASCII.GetBytes(s), o = new byte[(b.Length / 4 + 1) * 4];
            Buffer.BlockCopy(b, 0, o, 0, b.Length);
            return o;
        }
        static int Al(int n) { return (n + 4) & ~3; }   // tamanho de uma string OSC (n chars + NUL) alinhada a 4

        static bool Parse(byte[] d, int len, out string addr, out float val)
        {
            addr = null; val = 0;
            int z = Array.IndexOf(d, (byte)0, 0, len); if (z <= 0) return false;
            addr = Encoding.ASCII.GetString(d, 0, z);
            int p = Al(z);
            if (p + 1 >= len || d[p] != (byte)',') return false;
            char t = (char)d[p + 1];
            int z2 = Array.IndexOf(d, (byte)0, p, len - p); if (z2 < 0) return false;
            p += Al(z2 - p);
            if (p + 4 > len) return false;
            byte[] b = new byte[] { d[p + 3], d[p + 2], d[p + 1], d[p] };   // big-endian
            if (t == 'i') val = BitConverter.ToInt32(b, 0);
            else if (t == 'f') val = BitConverter.ToSingle(b, 0);
            else return false;
            return true;
        }

        public static Dictionary<string, float> Query(string ip, List<string> addrs, int timeoutMs)
        {
            Dictionary<string, float> res = new Dictionary<string, float>();
            IPAddress addr; if (!IPAddress.TryParse(ip, out addr)) return res;
            using (UdpClient u = new UdpClient(AddressFamily.InterNetwork))
            {
                IPEndPoint ep = new IPEndPoint(addr, Port);
                foreach (string a in addrs) { byte[] m = Pad(a); u.Send(m, m.Length, ep); }
                DateTime end = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                while (res.Count < addrs.Count)
                {
                    int left = (int)(end - DateTime.UtcNow).TotalMilliseconds; if (left <= 0) break;
                    u.Client.ReceiveTimeout = left;
                    try
                    {
                        IPEndPoint from = new IPEndPoint(IPAddress.Any, 0);
                        byte[] d = u.Receive(ref from); string a; float v;
                        if (Parse(d, d.Length, out a, out v)) res[a] = v;
                    }
                    catch (SocketException) { break; }
                }
            }
            return res;
        }

        // "1", "ch2", "aux1", "bus3", "mtx2", "dca4", "main", "mono"  ->  enderecos OSC de on/fader
        public static List<string[]> Tokens(string channels)
        {
            List<string[]> list = new List<string[]>();
            foreach (string raw in Regex.Split(channels ?? "", "[,; ]+"))
            {
                string t = raw.Trim().ToLowerInvariant(); if (t.Length == 0) continue;
                string bas = null, name = t;
                Match m = Regex.Match(t, "^(ch|aux|auxin|bus|mtx|dca)?(\\d+)$");
                if (m.Success)
                {
                    int n = int.Parse(m.Groups[2].Value); string kind = m.Groups[1].Value, nn = n.ToString("00");
                    if ((kind == "" || kind == "ch") && n >= 1 && n <= 32) { bas = "/ch/" + nn + "/mix"; name = "Canal " + n; }
                    else if ((kind == "aux" || kind == "auxin") && n >= 1 && n <= 8) { bas = "/auxin/" + nn + "/mix"; name = "Aux " + n; }
                    else if (kind == "bus" && n >= 1 && n <= 16) { bas = "/bus/" + nn + "/mix"; name = "Bus " + n; }
                    else if (kind == "mtx" && n >= 1 && n <= 6) { bas = "/mtx/" + nn + "/mix"; name = "Matrix " + n; }
                    else if (kind == "dca" && n >= 1 && n <= 8) { bas = "/dca/" + n; name = "DCA " + n; }
                }
                else if (t == "main" || t == "st" || t == "lr") { bas = "/main/st/mix"; name = "Main LR"; }
                else if (t == "mono" || t == "m") { bas = "/main/m/mix"; name = "Mono"; }
                if (bas != null) list.Add(new string[] { bas + "/on", bas + "/fader", name });
            }
            return list;
        }

        // ---- RTA: assina o medidor 15 (100 faixas de espectro, int16 little-endian, dB = valor/256) ----
        public static byte[] RtaSubscribePacket(int timeFactor = 4)
        {
            List<byte> b = new List<byte>();
            b.AddRange(Pad("/meters")); b.AddRange(Pad(",siii")); b.AddRange(Pad("/meters/15"));
            foreach (int v in new int[] { 0, 0, timeFactor })   // canal, grupo, fator de tempo (N x 50 ms)
            {
                byte[] i = BitConverter.GetBytes(v); Array.Reverse(i); b.AddRange(i);
            }
            return b.ToArray();
        }

        public static bool ParseRta(byte[] d, int len, out float[] db)
        {
            db = null;
            int z = Array.IndexOf(d, (byte)0, 0, len); if (z <= 0) return false;
            if (Encoding.ASCII.GetString(d, 0, z) != "/meters/15") return false;
            int p = Al(z);
            if (p + 1 >= len || d[p] != (byte)',' || d[p + 1] != (byte)'b') return false;
            int z2 = Array.IndexOf(d, (byte)0, p, len - p); if (z2 < 0) return false;
            p += Al(z2 - p);
            if (p + 8 > len) return false;
            p += 4;                                   // tamanho do blob (big-endian)
            int n = BitConverter.ToInt32(d, p); p += 4;   // quantidade de valores (little-endian)
            int avail = (len - p) / 2;
            if (n <= 0 || n > avail) n = avail;
            int bins = Math.Min(n, 100);
            if (bins < 8) return false;
            db = new float[bins];
            for (int i = 0; i < bins; i++) db[i] = BitConverter.ToInt16(d, p + 2 * i) / 256f;
            return true;
        }

        public static string RtaProbe(string ip)
        {
            IPAddress addr; if (!IPAddress.TryParse(ip, out addr)) return "RTA: IP invalido.";
            using (UdpClient u = new UdpClient(AddressFamily.InterNetwork))
            {
                IPEndPoint ep = new IPEndPoint(addr, Port);
                byte[] sub = RtaSubscribePacket(); u.Send(sub, sub.Length, ep);
                DateTime end = DateTime.UtcNow.AddMilliseconds(2000);
                while (true)
                {
                    int left = (int)(end - DateTime.UtcNow).TotalMilliseconds; if (left <= 0) break;
                    u.Client.ReceiveTimeout = left;
                    try
                    {
                        IPEndPoint from = new IPEndPoint(IPAddress.Any, 0);
                        byte[] d = u.Receive(ref from); float[] db;
                        if (ParseRta(d, d.Length, out db))
                        {
                            float max = -128; foreach (float v in db) max = Math.Max(max, v);
                            return "RTA: " + db.Length + " faixas recebidas, pico " + max.ToString("0.#", CultureInfo.InvariantCulture) + " dB.";
                        }
                    }
                    catch (SocketException) { break; }
                }
            }
            return "RTA: sem dados. Na mesa, ative o RTA e escolha a fonte (canal ou bus).";
        }

        // "1".."32" / "ch5" -> 0..31 ; "aux1".."aux8" -> 32..39 (ordem das fontes do RTA da X32) ; senao -1
        public static int MeterIndex(string token, out string name)
        {
            name = token; string t = (token ?? "").Trim().ToLowerInvariant();
            Match m = Regex.Match(t, "^(ch|aux|auxin)?(\\d+)$");
            if (!m.Success) return -1;
            int n = int.Parse(m.Groups[2].Value); string kind = m.Groups[1].Value;
            if ((kind == "" || kind == "ch") && n >= 1 && n <= 32) { name = "Canal " + n; return n - 1; }
            if ((kind == "aux" || kind == "auxin") && n >= 1 && n <= 8) { name = "Aux " + n; return 32 + n - 1; }
            return -1;
        }

        // /-prefs/rta/source ,i <indice>  : escolhe qual canal o RTA da mesa analisa
        public static byte[] SetRtaSourcePacket(int index)
        {
            List<byte> b = new List<byte>(); b.AddRange(Pad("/-prefs/rta/source")); b.AddRange(Pad(",i"));
            byte[] i = BitConverter.GetBytes(index); Array.Reverse(i); b.AddRange(i);
            return b.ToArray();
        }

        public static string GraphProbe(string ip, string token)
        {
            string name; int idx = MeterIndex(token, out name);
            if (idx < 0) return "Gráfico: use um canal de 1 a 32 (ou aux1 a aux8).";
            IPAddress addr; if (!IPAddress.TryParse(ip, out addr)) return "Gráfico: IP invalido.";
            using (UdpClient u = new UdpClient(AddressFamily.InterNetwork))
            {
                IPEndPoint ep = new IPEndPoint(addr, Port);
                byte[] src = SetRtaSourcePacket(idx); u.Send(src, src.Length, ep);
                byte[] sub = RtaSubscribePacket(); u.Send(sub, sub.Length, ep);
                DateTime end = DateTime.UtcNow.AddMilliseconds(2500);
                while (true)
                {
                    int left = (int)(end - DateTime.UtcNow).TotalMilliseconds; if (left <= 0) break;
                    u.Client.ReceiveTimeout = left;
                    try
                    {
                        IPEndPoint from = new IPEndPoint(IPAddress.Any, 0);
                        byte[] d = u.Receive(ref from); float[] db;
                        if (ParseRta(d, d.Length, out db))
                        {
                            float max = -128; foreach (float v in db) max = Math.Max(max, v);
                            return "Gráfico: RTA da mesa apontado para " + name + " (" + db.Length + " faixas, pico " + max.ToString("0.#", CultureInfo.InvariantCulture) + " dB). Fale no canal para as barras subirem.";
                        }
                    }
                    catch (SocketException) { break; }
                }
            }
            return "Gráfico: sem dados do RTA. Confira o IP e se o RTA da mesa está ativo.";
        }

        // 1 = no ar (algum canal aberto), 0 = fora do ar, -1 = sem resposta da mesa
        public static int State(string ip, string channels, bool useFader)
        {
            List<string[]> tk = Tokens(channels);
            if (tk.Count == 0) return -1;
            List<string> addrs = new List<string>();
            foreach (string[] t in tk) { addrs.Add(t[0]); if (useFader) addrs.Add(t[1]); }
            Dictionary<string, float> r = Query(ip, addrs, 700);
            if (r.Count == 0) return -1;
            foreach (string[] t in tk)
            {
                float on, f;
                if (r.TryGetValue(t[0], out on) && on >= 0.5f)
                    if (!useFader || !r.TryGetValue(t[1], out f) || f > 0.0625f) return 1;   // 0,0625 = -60 dB
            }
            return 0;
        }

        static string Db(float f)
        {
            double d = f >= 0.5f ? f * 40 - 30 : (f >= 0.25f ? f * 80 - 50 : (f >= 0.0625f ? f * 160 - 70 : f * 480 - 90));
            return d <= -89 ? "-inf dB" : d.ToString("0.#", CultureInfo.InvariantCulture) + " dB";
        }
        public static string Describe(string ip, string channels)
        {
            List<string[]> tk = Tokens(channels);
            if (tk.Count == 0) return "Nenhum canal válido. Ex.: 1  ou  1,2,aux1,bus3,main";
            List<string> addrs = new List<string>();
            foreach (string[] t in tk) { addrs.Add(t[0]); addrs.Add(t[1]); }
            Dictionary<string, float> r = Query(ip, addrs, 1500);
            if (r.Count == 0) return "Sem resposta da mesa em " + ip + " (porta 10023). Confira o IP e se o PC está na mesma rede da X32.";
            StringBuilder sb = new StringBuilder("Conectado à X32.\n");
            foreach (string[] t in tk)
            {
                float on, f; bool ho = r.TryGetValue(t[0], out on), hf = r.TryGetValue(t[1], out f);
                sb.Append(t[2] + ": " + (!ho ? "sem resposta" : (on >= 0.5f ? "aberto" : "mudo")) + (hf ? ", fader " + Db(f) : "") + "\n");
            }
            return sb.ToString().TrimEnd();
        }
    }

    // ---------- janela do overlay (camada com alfa por pixel: texto nitido sem franjas) ----------
    class Overlay : Form
    {
        public bool ClickThrough;
        bool dragging, sizing;
        int gripPx = 24, startW, startX, startScale;
        Size curSize;
        public int Scale = 100;
        public event Action<int> ScaleRequested;
        Bitmap logo;
        public int WeatherCode = -1;
        public bool ReserveIcon;
        public float[] RtaCap;       // picos (hold) do RTA, para as tampinhas flutuantes do grafico
        public string GraphName = "";
        public float[] Rta;          // espectro da X32 (dB por faixa) para o estilo com RTA
        public int AirState = -1;   // 1 no ar, 0 fora, -1 sem resposta da X32
        static readonly CultureInfo PtBr = new CultureInfo("pt-BR");
        public void SetLogo(Bitmap b) { if (logo != null) logo.Dispose(); logo = b; }
        public Point Pos = new Point(40, 40); // posicao propria (nao depende de Form.Location)
        public event EventHandler Moved;

        public Overlay()
        {
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
            StartPosition = FormStartPosition.Manual; Text = Const.App; Cursor = Cursors.SizeNWSE;
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && !ClickThrough) { sizing = true; Capture = true; startW = curSize.Width; startX = Control.MousePosition.X; startScale = Scale; }
            base.OnMouseDown(e);
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (sizing && startW > 0)
            {
                int pct = (int)Math.Round(startScale * (startW + Control.MousePosition.X - startX) / (double)startW);
                pct = Math.Max(30, Math.Min(400, pct));
                if (pct != Scale && ScaleRequested != null) ScaleRequested(pct);
            }
            base.OnMouseMove(e);
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (sizing) { sizing = false; Capture = false; if (Moved != null) Moved(this, EventArgs.Empty); }
            base.OnMouseUp(e);
        }
        static void DrawGrip(Graphics g, int w, int h, float k)
        {
            using (Pen p = new Pen(Color.FromArgb(255, 255, 176, 0), Math.Max(2f, 2 * k)))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                for (int i = 1; i <= 3; i++) g.DrawLine(p, w - 6 * k * i - 4 * k, h - 4 * k, w - 4 * k, h - 6 * k * i - 4 * k);
            }
        }
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Win.WS_EX_LAYERED | Win.WS_EX_TOOLWINDOW | Win.WS_EX_NOACTIVATE;
                if (ClickThrough) cp.ExStyle |= Win.WS_EX_TRANSPARENT;
                return cp;
            }
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x84 && !ClickThrough)   // arrastar em qualquer ponto (HTCAPTION); o canto inferior direito redimensiona
            {
                int lp = m.LParam.ToInt32(); int sx = (short)(lp & 0xFFFF), sy = (short)((lp >> 16) & 0xFFFF);
                Win.RECT rc; Win.GetWindowRect(Handle, out rc);
                m.Result = (IntPtr)((sx > rc.R - gripPx && sy > rc.B - gripPx) ? 1 : 2); return;
            }
            if (m.Msg == 0x231) dragging = true;                                      // WM_ENTERSIZEMOVE
            if (m.Msg == 0x232 && dragging)                                           // WM_EXITSIZEMOVE
            {
                dragging = false;
                Win.RECT r; if (Win.GetWindowRect(Handle, out r)) Pos = new Point(r.L, r.T);
                if (Moved != null) Moved(this, EventArgs.Empty);
            }
            base.WndProc(ref m);
        }
        public void SetClickThrough(bool on)
        {
            ClickThrough = on;
            if (!IsHandleCreated) return;
            int ex = Win.GetWindowLong(Handle, Win.GWL_EXSTYLE);
            ex = on ? (ex | Win.WS_EX_TRANSPARENT) : (ex & ~Win.WS_EX_TRANSPARENT);
            Win.SetWindowLong(Handle, Win.GWL_EXSTYLE, ex);
        }
        public bool Suspend;   // true enquanto a janela de configuracoes esta aberta: ela fica por cima do widget
        public void KeepOnTop()
        {
            if (!Suspend && IsHandleCreated) Win.SetWindowPos(Handle, (IntPtr)(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
        }

        int lastScale = 100;
        public void Draw(Config c, string label, string temp)
        {
            lastScale = c.Scale;
            if (c.IsPanel) { if (c.PanelKind == 3) DrawPanel3(c, label, temp); else if (c.PanelKind == 2) DrawPanel2(c, label, temp); else DrawPanel(c, label, temp); return; }
            float dpi; using (Graphics sg = Graphics.FromHwnd(IntPtr.Zero)) dpi = sg.DpiX;
            gripPx = (int)(26 * dpi / 96f);
            float sc = Math.Max(30, Math.Min(400, c.Scale)) / 100f;
            float k = dpi / 96f * sc;
            bool card = c.Style == "card";
            int alpha = Math.Max(30, Math.Min(100, c.Opacity)) * 255 / 100;
            Color fg = Color.FromArgb(alpha, Parse(c.TextColor, Color.White));
            float pt = Math.Max(8, c.FontSize * sc);
            using (Font ft = new Font(c.FontFamily, pt, FontStyle.Bold, GraphicsUnit.Point))
            using (Font fl = new Font(c.FontFamily, Math.Max(8f, pt * 0.34f), FontStyle.Regular, GraphicsUnit.Point))
            using (StringFormat sf = new StringFormat(StringFormat.GenericTypographic))
            {
                sf.FormatFlags |= StringFormatFlags.NoWrap;
                SizeF st, sl = SizeF.Empty;
                using (Bitmap probe = new Bitmap(1, 1))
                using (Graphics pg = Graphics.FromImage(probe))
                {
                    probe.SetResolution(dpi, dpi);
                    pg.PageUnit = GraphicsUnit.Pixel; pg.SetClip(new Rectangle(0, 0, 1, 1));
                    pg.TextRenderingHint = TextRenderingHint.AntiAlias;
                    st = pg.MeasureString(temp, ft, 10000, sf); st = Fix(st, MaxW(pg, ft, sf, Pat(temp), dpi));
                    if (label.Length > 0) sl = pg.MeasureString(label, fl, 10000, sf);
                }
                int padX = (int)((card ? 18 : 8) * k), padY = (int)((card ? 10 : 4) * k), gap = label.Length > 0 ? (int)(2 * k) : 0;
                int w = (int)Math.Ceiling(Math.Max(st.Width, sl.Width)) + padX * 2;
                int h = (int)Math.Ceiling(st.Height + sl.Height) + gap + padY * 2;
                using (Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb))
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    bmp.SetResolution(dpi, dpi);
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = TextRenderingHint.AntiAlias;
                    Rectangle rect = new Rectangle(0, 0, w - 1, h - 1);
                    if (card)
                    {
                        using (GraphicsPath gp = Round(rect, (int)(14 * k)))
                        using (Brush b = new SolidBrush(Color.FromArgb(alpha, Parse(c.CardColor, Color.Black)))) g.FillPath(b, gp);
                    }
                    else if (!c.Locked)
                    {
                        using (Brush b = new SolidBrush(Color.FromArgb(45, 0, 0, 0))) g.FillRectangle(b, rect);
                    }
                    if (!c.Locked)
                        using (Pen p = new Pen(Color.FromArgb(255, 255, 176, 0), Math.Max(2, 2 * k)))
                        using (GraphicsPath gp = Round(rect, card ? (int)(14 * k) : 2)) g.DrawPath(p, gp);
                    float y = padY;
                    if (label.Length > 0)
                    {
                        DrawText(g, label, fl, fg, (w - sl.Width) / 2, y, sf, card, k);
                        y += sl.Height + gap;
                    }
                    DrawText(g, temp, ft, fg, (w - st.Width) / 2, y, sf, card, k);
                    if (!c.Locked) DrawGrip(g, w, h, k);
                    Push(bmp);
                }
            }
        }


        public DateTime AirSince = DateTime.MinValue;   // quando o canal abriu (inicio do cronometro)
        string AirTimerText()
        {
            if (AirState != 1 || AirSince == DateTime.MinValue) return "00:00:00";
            TimeSpan t = DateTime.Now - AirSince; if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            return string.Format("{0:00}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds);
        }

        // ---------- placa "NO AR" (vermelha) ----------
        static string PlateText(Config c, int state) { return state == 1 ? c.AirText : (state == 0 ? "FORA DO AR" : "SEM X32"); }
        static SizeF PlateSize(Graphics pg, Font f, StringFormat sf, Config c, float k, out float dot)
        {
            float w = 0;
            foreach (int st in new int[] { 1, 0, -1 }) w = Math.Max(w, pg.MeasureString(PlateText(c, st), f, 10000, sf).Width);   // largura fixa
            float h = pg.MeasureString("Ag", f, 10000, sf).Height;
            dot = h * 0.42f;
            return new SizeF(w + dot + 12 * k + 44 * k, h + 24 * k);
        }
        static void DrawPlate(Graphics g, Config c, int state, Font f, StringFormat sf, float x, float y, SizeF size, float dot, float k, int alpha)
        {
            Color fill = state == 1 ? Color.FromArgb(alpha, 220, 38, 38) : (state == 0 ? Color.FromArgb(alpha, 52, 52, 52) : Color.FromArgb(alpha, 146, 64, 14));
            Color fg = state == 0 ? Color.FromArgb(alpha, 150, 150, 150) : Color.FromArgb(alpha, 255, 255, 255);
            using (GraphicsPath gp = Round(Rectangle.Round(new RectangleF(x, y, size.Width, size.Height)), (int)size.Height))
            using (Brush b = new SolidBrush(fill)) g.FillPath(b, gp);
            string t = PlateText(c, state);
            SizeF m = g.MeasureString(t, f, 10000, sf);
            float cx = x + (size.Width - (dot + 12 * k + m.Width)) / 2;
            using (Brush bd = new SolidBrush(fg))
            {
                g.FillEllipse(bd, cx, y + (size.Height - dot) / 2, dot, dot);
                g.DrawString(t, f, bd, cx + dot + 12 * k, y + (size.Height - m.Height) / 2, sf);
            }
        }

        // ---------- estilo Painel: [logo] | relogio + data | icone + temperatura + cidade ----------
        // Largura estavel: mede com o digito mais largo (o "1" e mais estreito e fazia a borda tremer a cada segundo)
        static readonly Dictionary<string, float> widthCache = new Dictionary<string, float>();
        static string Pat(string t) { return Regex.Replace(t, "\\d", "#"); }
        static SizeF Fix(SizeF s, float w) { return new SizeF(Math.Max(s.Width, w), s.Height); }
        static float MaxW(Graphics pg, Font f, StringFormat sf, string pattern, float dpi)
        {
            string key = f.Name + "|" + f.Size + "|" + pattern + "|" + dpi;
            float v;
            if (widthCache.TryGetValue(key, out v)) return v;
            for (int d = 0; d < 10; d++)
                v = Math.Max(v, pg.MeasureString(pattern.Replace('#', (char)('0' + d)), f, 10000, sf).Width);
            widthCache[key] = v;
            return v;
        }

        static Font Semi(string fam, float pt)
        {
            if (pt < 10f) return new Font(fam, pt, FontStyle.Bold, GraphicsUnit.Point);   // abaixo de 10 pt a "Semibold" sai com aspecto serifado no GDI+
            Font f = new Font("Segoe UI Semibold", pt, FontStyle.Regular, GraphicsUnit.Point);
            if (f.Name == "Segoe UI Semibold") return f;
            f.Dispose(); return new Font(fam, pt, FontStyle.Bold, GraphicsUnit.Point);
        }
        static float AscentPx(Font f, float dpi)
        {
            FontFamily ff = f.FontFamily;
            return ff.GetCellAscent(f.Style) / (float)ff.GetEmHeight(f.Style) * (f.Size * dpi / 72f);
        }

        void DrawPanel(Config c, string label, string temp)
        {
            float dpi; using (Graphics sg = Graphics.FromHwnd(IntPtr.Zero)) dpi = sg.DpiX;
            gripPx = (int)(26 * dpi / 96f);
            float sc = Math.Max(30, Math.Min(400, c.Scale)) / 100f;
            float k = dpi / 96f * sc;
            int alpha = Math.Max(30, Math.Min(100, c.Opacity)) * 255 / 100;
            Color main = Color.FromArgb(alpha, Parse(c.TextColor, Color.White));
            Color grey = Color.FromArgb(alpha, 138, 138, 138), grey2 = Color.FromArgb(alpha, 181, 181, 181);
            float F = Math.Max(12, c.FontSize) * sc;
            if (c.Suffix == "°C" && temp.EndsWith("°C")) temp = temp.Substring(0, temp.Length - 1); // painel: "24°"
            DateTime now = DateTime.Now;
            string hm = now.ToString("HH:mm"), ss = now.ToString("ss");
            string date = now.ToString("dddd, d 'de' MMMM", PtBr); date = char.ToUpper(date[0]) + date.Substring(1);
            bool clock = c.ShowClock, secs = c.ShowClock && c.ShowSeconds, showDate = c.ShowDate;
            bool left = clock || showDate, icon = c.ShowIcon && (WeatherCode >= 0 || ReserveIcon), hasLogo = logo != null;
            using (Font fClock = Semi(c.FontFamily, F), fSec = Semi(c.FontFamily, F * 0.45f), fTemp = Semi(c.FontFamily, F * 0.85f), fAir = Semi(c.FontFamily, Math.Max(10f, F * 0.42f)))
            using (Font fDate = new Font(c.FontFamily, Math.Max(8f, F * 0.30f), FontStyle.Regular, GraphicsUnit.Point))
            using (Font fCity = new Font(c.FontFamily, Math.Max(8f, F * 0.27f), FontStyle.Regular, GraphicsUnit.Point))
            using (StringFormat sf = new StringFormat(StringFormat.GenericTypographic))
            using (Bitmap probe = new Bitmap(1, 1))
            using (Graphics pg = Graphics.FromImage(probe))
            {
                sf.FormatFlags |= StringFormatFlags.NoWrap;
                probe.SetResolution(dpi, dpi); pg.PageUnit = GraphicsUnit.Pixel; pg.TextRenderingHint = TextRenderingHint.AntiAlias;
                Func<string, Font, SizeF> M = delegate(string t, Font f) { return t.Length == 0 ? SizeF.Empty : pg.MeasureString(t, f, 10000, sf); };
                SizeF mHM = clock ? Fix(M(hm, fClock), MaxW(pg, fClock, sf, "##:##", dpi)) : SizeF.Empty, mSS = secs ? Fix(M(ss, fSec), MaxW(pg, fSec, sf, "##", dpi)) : SizeF.Empty;
                SizeF mDate = showDate ? M(date, fDate) : SizeF.Empty, mTemp = Fix(M(temp, fTemp), MaxW(pg, fTemp, sf, Pat(temp), dpi)), mCity = M(label, fCity);
                float gap = 8 * k, ig = 10 * k;
                float dotA = 0; SizeF plate = c.HasAir ? PlateSize(pg, fAir, sf, c, k, out dotA) : SizeF.Empty;
                float clockW = mHM.Width + (secs ? gap + mSS.Width : 0);
                float b1W = Math.Max(clockW, mDate.Width), b1H = mHM.Height + mDate.Height;
                float iconS = mTemp.Height * 0.72f;
                float tempW = (icon ? iconS + ig : 0) + mTemp.Width;
                float b2W = Math.Max(tempW, mCity.Width), b2H = mTemp.Height + mCity.Height;
                float contentH = Math.Max(Math.Max(left ? b1H : 0, b2H), plate.Height);
                float logoH = contentH * 0.95f, logoW = hasLogo ? Math.Min(logo.Width * logoH / logo.Height, contentH * 2.4f) : 0;
                float padX = 34 * k, padY = 24 * k, side = 40 * k;
                int sections = 1 + (left ? 1 : 0) + (hasLogo ? 1 : 0) + (c.HasAir ? 1 : 0);
                float total = padX * 2 + b2W + (left ? b1W : 0) + logoW + plate.Width + (sections - 1) * (side * 2 + 1);
                int w = (int)Math.Ceiling(total), h = (int)Math.Ceiling(contentH + padY * 2);
                using (Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb))
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    bmp.SetResolution(dpi, dpi);
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = SmoothingMode.AntiAlias; g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.TextRenderingHint = TextRenderingHint.AntiAlias;
                    Rectangle rect = new Rectangle(0, 0, w - 1, h - 1);
                    int rad = (int)(24 * k);
                    using (GraphicsPath gp = Round(rect, rad))
                    {
                        using (Brush b = new SolidBrush(Color.FromArgb(alpha, Parse(c.CardColor, Color.FromArgb(13, 13, 13))))) g.FillPath(b, gp);
                        if (c.Locked) using (Pen p = new Pen(Color.FromArgb(alpha, 38, 38, 38), 1)) g.DrawPath(p, gp);
                        else using (Pen p = new Pen(Color.FromArgb(255, 255, 176, 0), Math.Max(2, 2 * k))) g.DrawPath(p, gp);
                    }
                    float x = padX;
                    using (Pen dv = new Pen(Color.FromArgb(alpha, 42, 42, 42), 1))
                    {
                        Action<float> divider = delegate(float dx) { g.DrawLine(dv, dx, padY + contentH * 0.08f, dx, padY + contentH * 0.92f); };
                        if (hasLogo)
                        {
                            g.DrawImage(logo, new RectangleF(x, padY + (contentH - logoH) / 2, logoW, logoH));
                            x += logoW + side; divider(x); x += 1 + side;
                        }
                        if (c.HasAir)
                        {
                            DrawPlate(g, c, AirState, fAir, sf, x, padY + (contentH - plate.Height) / 2, plate, dotA, k, alpha);
                            x += plate.Width + side; divider(x); x += 1 + side;
                        }
                        if (left)
                        {
                            float y = padY + (contentH - b1H) / 2;
                            float x0 = x + (b1W - clockW) / 2;
                            if (clock)
                            {
                                using (Brush bm = new SolidBrush(main)) g.DrawString(hm, fClock, bm, x0, y, sf);
                                if (secs)
                                    using (Brush bs = new SolidBrush(grey))
                                        g.DrawString(ss, fSec, bs, x0 + pg.MeasureString(hm, fClock, 10000, sf).Width + gap, y + AscentPx(fClock, dpi) - AscentPx(fSec, dpi), sf);
                            }
                            if (showDate)
                                using (Brush bd = new SolidBrush(grey2)) g.DrawString(date, fDate, bd, x + (b1W - mDate.Width) / 2, y + mHM.Height, sf);
                            x += b1W + side; divider(x); x += 1 + side;
                        }
                        float y2 = padY + (contentH - b2H) / 2, x1 = x + (b2W - tempW) / 2;
                        if (icon && WeatherCode >= 0) DrawIcon(g, WeatherCode, x1, y2 + (mTemp.Height - iconS) / 2, iconS, alpha);
                        using (Brush bt = new SolidBrush(main)) g.DrawString(temp, fTemp, bt, x1 + (icon ? iconS + ig : 0), y2, sf);
                        if (label.Length > 0)
                            using (Brush bc = new SolidBrush(grey)) g.DrawString(label, fCity, bc, x + (b2W - mCity.Width) / 2, y2 + mTemp.Height, sf);
                    }
                    if (!c.Locked) DrawGrip(g, w, h, k);
                    Push(bmp);
                }
            }
        }


        // ---------- Painel 3: [logo / icone+temp+cidade] | relogio+data | NO AR + cronometro | grafico de onda do canal ----------
        void DrawPanel3(Config c, string label, string temp)
        {
            float dpi; using (Graphics sg = Graphics.FromHwnd(IntPtr.Zero)) dpi = sg.DpiX;
            gripPx = (int)(26 * dpi / 96f);
            float sc = Math.Max(30, Math.Min(400, c.Scale)) / 100f;
            float k = dpi / 96f * sc;
            int alpha = Math.Max(30, Math.Min(100, c.Opacity)) * 255 / 100;
            Color main = Color.FromArgb(alpha, Parse(c.TextColor, Color.White));
            Color grey = Color.FromArgb(alpha, 138, 138, 138), grey2 = Color.FromArgb(alpha, 181, 181, 181);
            float F = Math.Max(12, c.FontSize) * sc;
            if (c.Suffix == "°C" && temp.EndsWith("°C")) temp = temp.Substring(0, temp.Length - 1);
            DateTime now = DateTime.Now;
            string hm = now.ToString("HH:mm"), ss = now.ToString("ss");
            string wd = now.ToString("dddd", PtBr); wd = char.ToUpper(wd[0]) + wd.Substring(1);
            string dm = now.ToString("d 'de' MMMM", PtBr);
            bool clock = c.ShowClock, secs = c.ShowClock && c.ShowSeconds, showDate = c.ShowDate;
            bool hasClockBlock = clock || showDate, icon = c.ShowIcon && (WeatherCode >= 0 || ReserveIcon), hasLogo = logo != null;
            string[] city = label.Length > 0 ? SplitMid(label) : new string[0];
            using (Font fClock = Semi(c.FontFamily, F), fSec = Semi(c.FontFamily, F * 0.45f), fTemp = Semi(c.FontFamily, F * 0.55f),
                        fAir = Semi(c.FontFamily, Math.Max(10f, F * 0.36f)), fTimer = Semi(c.FontFamily, Math.Max(9f, F * 0.28f)))
            using (Font fDate = new Font(c.FontFamily, Math.Max(8f, F * 0.28f), FontStyle.Regular, GraphicsUnit.Point))
            using (Font fCity = new Font(c.FontFamily, Math.Max(8f, F * 0.22f), FontStyle.Regular, GraphicsUnit.Point))
            using (Font fGl = new Font(c.FontFamily, Math.Max(7f, F * 0.20f), FontStyle.Regular, GraphicsUnit.Point))
            using (StringFormat sf = new StringFormat(StringFormat.GenericTypographic))
            using (Bitmap probe = new Bitmap(1, 1))
            using (Graphics pg = Graphics.FromImage(probe))
            {
                sf.FormatFlags |= StringFormatFlags.NoWrap;
                probe.SetResolution(dpi, dpi); pg.PageUnit = GraphicsUnit.Pixel; pg.TextRenderingHint = TextRenderingHint.AntiAlias;
                Func<string, Font, SizeF> M = delegate(string t, Font f) { return t.Length == 0 ? SizeF.Empty : pg.MeasureString(t, f, 10000, sf); };
                SizeF mHM = clock ? Fix(M(hm, fClock), MaxW(pg, fClock, sf, "##:##", dpi)) : SizeF.Empty, mSS = secs ? Fix(M(ss, fSec), MaxW(pg, fSec, sf, "##", dpi)) : SizeF.Empty;
                SizeF mD1 = showDate ? M(wd, fDate) : SizeF.Empty, mD2 = showDate ? M(dm, fDate) : SizeF.Empty;
                SizeF mTemp = Fix(M(temp, fTemp), MaxW(pg, fTemp, sf, Pat(temp), dpi));
                SizeF mC1 = city.Length > 0 ? M(city[0], fCity) : SizeF.Empty, mC2 = city.Length > 1 ? M(city[1], fCity) : SizeF.Empty;
                float gap = 8 * k, ig = 10 * k, sideGap = 30 * k, tGap = 10 * k;
                float clockW = mHM.Width + (secs ? gap + mSS.Width : 0);
                float clockBlockW = hasClockBlock ? Math.Max(clockW, Math.Max(mD1.Width, mD2.Width)) : 0;
                float clockBlockH = mHM.Height + mD1.Height + mD2.Height;
                float dotA; SizeF plate = PlateSize(pg, fAir, sf, c, k, out dotA);
                string timerText = AirTimerText();
                SizeF mTimer = Fix(M(timerText, fTimer), MaxW(pg, fTimer, sf, "##:##:##", dpi));
                float airW = Math.Max(plate.Width, mTimer.Width), airBlockH = plate.Height + tGap + mTimer.Height;
                float iconS = mTemp.Height * 0.7f;
                float cityW = Math.Max(mC1.Width, mC2.Width), cityH = mC1.Height + mC2.Height;
                float rowW = (icon ? iconS + ig : 0) + mTemp.Width + (cityW > 0 ? ig + cityW : 0), rowH = Math.Max(mTemp.Height, cityH);
                float baseW = hasClockBlock ? clockBlockW : mHM.Width;
                float Lw = Math.Max(rowW, hasLogo ? Math.Max(baseW * 0.62f, 120 * k) : 0);
                float logoBoxH = hasLogo ? Lw * 0.5f : 0, gapL = hasLogo ? 18 * k : 0;
                float contentH = Math.Max(Math.Max(logoBoxH + gapL + rowH, hasClockBlock ? clockBlockH : 0), Math.Max(airBlockH, 110 * k));
                float Gw = Math.Max(300 * k, (hasClockBlock ? clockBlockW : 160 * k) * 1.7f);
                float padX = 30 * k, padY = 26 * k, side = 28 * k;
                float total = padX * 2 + Lw + (side * 2 + 1) + (hasClockBlock ? clockBlockW + sideGap : 0) + airW + (side * 2 + 1) + Gw;
                int w = (int)Math.Ceiling(total), h = (int)Math.Ceiling(contentH + padY * 2);
                using (Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb))
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    bmp.SetResolution(dpi, dpi);
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = SmoothingMode.AntiAlias; g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.TextRenderingHint = TextRenderingHint.AntiAlias;
                    Rectangle rect = new Rectangle(0, 0, w - 1, h - 1);
                    using (GraphicsPath gp = Round(rect, (int)(24 * k)))
                    {
                        using (Brush b = new SolidBrush(Color.FromArgb(alpha, Parse(c.CardColor, Color.FromArgb(13, 13, 13))))) g.FillPath(b, gp);
                        if (c.Locked) using (Pen p = new Pen(Color.FromArgb(alpha, 38, 38, 38), 1)) g.DrawPath(p, gp);
                        else using (Pen p = new Pen(Color.FromArgb(255, 255, 176, 0), Math.Max(2, 2 * k))) g.DrawPath(p, gp);
                    }
                    // coluna esquerda: logo em cima, temperatura embaixo
                    if (hasLogo)
                    {
                        float sc2 = Math.Min(Lw / logo.Width, logoBoxH / logo.Height), dw = logo.Width * sc2, dh = logo.Height * sc2;
                        g.DrawImage(logo, new RectangleF(padX + (Lw - dw) / 2, padY + (logoBoxH - dh) / 2, dw, dh));
                    }
                    float ry = hasLogo ? padY + contentH - rowH : padY + (contentH - rowH) / 2, x = padX;
                    if (icon) { if (WeatherCode >= 0) DrawIcon(g, WeatherCode, x, ry + (rowH - iconS) / 2, iconS, alpha); x += iconS + ig; }
                    using (Brush bt = new SolidBrush(main)) g.DrawString(temp, fTemp, bt, x, ry + (rowH - mTemp.Height) / 2, sf);
                    x += mTemp.Width + ig;
                    using (Brush bc = new SolidBrush(grey))
                    {
                        float cy = ry + (rowH - cityH) / 2;
                        if (city.Length > 0) g.DrawString(city[0], fCity, bc, x, cy, sf);
                        if (city.Length > 1) g.DrawString(city[1], fCity, bc, x, cy + mC1.Height, sf);
                    }
                    using (Pen dv = new Pen(Color.FromArgb(alpha, 42, 42, 42), 1))
                    {
                        float dx1 = padX + Lw + side;
                        g.DrawLine(dv, dx1, padY, dx1, padY + contentH);
                        float cx = dx1 + 1 + side;
                        // relogio + data
                        if (hasClockBlock)
                        {
                            float y = padY + (contentH - clockBlockH) / 2;
                            if (clock)
                            {
                                using (Brush bm = new SolidBrush(main)) g.DrawString(hm, fClock, bm, cx, y, sf);
                                if (secs)
                                    using (Brush bs = new SolidBrush(grey))
                                        g.DrawString(ss, fSec, bs, cx + pg.MeasureString(hm, fClock, 10000, sf).Width + gap, y + AscentPx(fClock, dpi) - AscentPx(fSec, dpi), sf);
                            }
                            if (showDate)
                                using (Brush bd = new SolidBrush(grey2))
                                {
                                    g.DrawString(wd, fDate, bd, cx, y + mHM.Height, sf);
                                    g.DrawString(dm, fDate, bd, cx, y + mHM.Height + mD1.Height, sf);
                                }
                            cx += clockBlockW + sideGap;
                        }
                        // NO AR + cronometro
                        float ay = padY + (contentH - airBlockH) / 2;
                        DrawPlate(g, c, AirState, fAir, sf, cx + (airW - plate.Width) / 2, ay, plate, dotA, k, alpha);
                        using (Brush bt2 = new SolidBrush(AirState == 1 ? main : grey))
                            g.DrawString(timerText, fTimer, bt2, cx + (airW - mTimer.Width) / 2, ay + plate.Height + tGap, sf);
                        cx += airW + side;
                        g.DrawLine(dv, cx, padY, cx, padY + contentH);
                        cx += 1 + side;
                        DrawWave(g, c, cx, padY, Gw, contentH, fGl, sf, k, alpha);
                    }
                    if (!c.Locked) DrawGrip(g, w, h, k);
                    Push(bmp);
                }
            }
        }

        // cores do grafico: degrade entre duas cores pelo matiz (caminho mais curto), sem passar por cinza
        static void ToHsv(Color c, out float h, out float sat, out float v)
        {
            float r = c.R / 255f, g = c.G / 255f, b = c.B / 255f, max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
            v = max; sat = max == 0 ? 0 : d / max;
            if (d == 0) h = 0;
            else if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * ((b - r) / d + 2);
            else h = 60 * ((r - g) / d + 4);
            if (h < 0) h += 360;
        }
        static Color FromHsv(int a, float h, float sat, float v)
        {
            float c = v * sat, x = c * (1 - Math.Abs((h / 60f) % 2 - 1)), m = v - c, r, g, b;
            switch ((int)(h / 60f) % 6)
            {
                case 0: r = c; g = x; b = 0; break;
                case 1: r = x; g = c; b = 0; break;
                case 2: r = 0; g = c; b = x; break;
                case 3: r = 0; g = x; b = c; break;
                case 4: r = x; g = 0; b = c; break;
                default: r = c; g = 0; b = x; break;
            }
            return Color.FromArgb(a, (int)((r + m) * 255), (int)((g + m) * 255), (int)((b + m) * 255));
        }
        static Color Lerp(Color a, Color b, float t, int alpha)
        {
            float ha, sa, va, hb, sb, vb; ToHsv(a, out ha, out sa, out va); ToHsv(b, out hb, out sb, out vb);
            if (sa < 0.08f || sb < 0.08f)   // cinza/branco/preto: mistura simples
                return Color.FromArgb(alpha, (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
            float dh = hb - ha; if (dh > 180) dh -= 360; if (dh < -180) dh += 360;
            return FromHsv(alpha, (ha + dh * t + 360) % 360, sa + (sb - sa) * t, va + (vb - va) * t);
        }

        // grafico azul: barras finas arredondadas (pilulas) sem fundo, com reflexo suave opcional
        void DrawPill(Graphics g, Config c, float gx, float gy, float gw, float gh, Font fLabel, StringFormat sf, float k, int alpha)
        {
            bool refl = c.GraphReflect;
            float padH = 12 * k, baseY = refl ? gy + gh * 0.60f : gy + gh - 16 * k, topY = gy + 26 * k, maxH = baseY - topY;
            float barW = Math.Max(3f, 6 * k), gapB = Math.Max(1.5f, 3.5f * k);
            int N = Math.Max(8, (int)((gw - 2 * padH + gapB) / (barW + gapB)));
            float usedW = N * barW + (N - 1) * gapB, x0 = gx + (gw - usedW) / 2;
            // sem fundo: so as barras (e o reflexo) sobre o fundo do widget
            float[] v = Rta; int bins = v == null ? 0 : v.Length;
            float gain = Math.Max(0.1f, c.GraphGain / 100f);
            Color pTop = Parse(c.GraphColorA, Color.FromArgb(70, 200, 250)), pBot = Parse(c.GraphColorB, Color.FromArgb(12, 140, 210));
            using (LinearGradientBrush body = new LinearGradientBrush(new PointF(0, topY), new PointF(0, baseY), Color.FromArgb(alpha, pTop), Color.FromArgb(alpha, pBot)))
            using (LinearGradientBrush fade = new LinearGradientBrush(new PointF(0, baseY), new PointF(0, baseY + maxH * 0.55f), Color.FromArgb(alpha * 90 / 255, pTop), Color.FromArgb(0, pTop)))
            {
                for (int i = 0; i < N; i++)
                {
                    float db = -90;
                    if (bins > 0)
                    {
                        int a = i * bins / N, b = Math.Max(a + 1, (i + 1) * bins / N);
                        for (int j = a; j < b && j < bins; j++) db = Math.Max(db, v[j]);
                    }
                    float amp = Math.Max(0f, Math.Min(1f, (db + 70f) / 60f * gain));
                    float bh = Math.Max(barW, amp * maxH), bx = x0 + i * (barW + gapB);
                    using (GraphicsPath bar = Round(Rectangle.Round(new RectangleF(bx, baseY - bh, barW, bh)), (int)Math.Max(2f, barW)))
                        g.FillPath(body, bar);
                    if (refl)
                    {
                        float rh = Math.Max(barW, bh * 0.55f);
                        using (GraphicsPath rb = Round(Rectangle.Round(new RectangleF(bx, baseY + 4 * k, barW, rh)), (int)Math.Max(2f, barW)))
                            g.FillPath(fade, rb);
                    }
                }
            }
            using (Brush bt = new SolidBrush(Color.FromArgb(alpha, 120, 160, 190)))
                g.DrawString(GraphName, fLabel, bt, gx + 10 * k, gy + 6 * k, sf);
        }

        // grafico de barras estilo RTA (so para cima): barras fixas que sobem e descem, gradiente indigo -> roxo -> magenta -> laranja,
        // com "picos" flutuando acima de cada barra. Dados: RTA da mesa apontado para o canal escolhido.
        void DrawWave(Graphics g, Config c, float gx, float gy, float gw, float gh, Font fLabel, StringFormat sf, float k, int alpha)
        {
            if (c.GraphStyle == "pill") { DrawPill(g, c, gx, gy, gw, gh, fLabel, sf, k, alpha); return; }
            float padH = 6 * k, baseY = gy + gh - 18 * k, topY = gy + 26 * k, maxH = baseY - topY;
            float barW = Math.Max(4f, 11 * k), gapB = Math.Max(2f, 5 * k);
            int N = Math.Max(6, (int)((gw - 2 * padH + gapB) / (barW + gapB)));
            float usedW = N * barW + (N - 1) * gapB, x0 = gx + (gw - usedW) / 2;
            float[] v = Rta, cp = RtaCap; int bins = v == null ? 0 : v.Length;
            float gain = Math.Max(0.1f, c.GraphGain / 100f), capH = Math.Max(2f, 3 * k);
            Color c1 = Color.FromArgb(alpha, 99, 102, 241), c2 = Color.FromArgb(alpha, 168, 85, 247),
                  c3 = Color.FromArgb(alpha, 236, 72, 153), c4 = Color.FromArgb(alpha, 251, 146, 60);
            if (!string.IsNullOrEmpty(c.GraphColorA) || !string.IsNullOrEmpty(c.GraphColorB))   // cores escolhidas nas configuracoes
            {
                Color ca = Parse(c.GraphColorA, Color.FromArgb(99, 102, 241)), cz = Parse(c.GraphColorB, Color.FromArgb(251, 146, 60));
                c1 = Color.FromArgb(alpha, ca); c4 = Color.FromArgb(alpha, cz);
                c2 = Lerp(ca, cz, 1f / 3, alpha); c3 = Lerp(ca, cz, 2f / 3, alpha);
            }
            using (LinearGradientBrush lg = new LinearGradientBrush(new PointF(x0, 0), new PointF(x0 + usedW, 0), c1, c4))
            {
                ColorBlend cb = new ColorBlend(4);
                cb.Colors = new Color[] { c1, c2, c3, c4 }; cb.Positions = new float[] { 0f, 0.35f, 0.68f, 1f };
                lg.InterpolationColors = cb;
                for (int i = 0; i < N; i++)
                {
                    float db = -90, cdb = -90;
                    if (bins > 0)
                    {
                        int a = i * bins / N, b = Math.Max(a + 1, (i + 1) * bins / N);
                        for (int j = a; j < b && j < bins; j++)
                        {
                            db = Math.Max(db, v[j]);
                            if (cp != null && j < cp.Length) cdb = Math.Max(cdb, cp[j]);
                        }
                    }
                    float amp = Math.Max(0f, Math.Min(1f, (db + 70f) / 60f * gain));      // -70 .. -10 dB
                    float camp = Math.Max(amp, Math.Max(0f, Math.Min(1f, (cdb + 70f) / 60f * gain)));
                    float bh = Math.Max(3 * k, amp * maxH), bx = x0 + i * (barW + gapB);
                    g.FillRectangle(lg, bx, baseY - bh, barW, bh);
                    float capTop = baseY - Math.Max(bh, camp * maxH) - 7 * k - capH;
                    g.FillRectangle(lg, bx, Math.Max(topY - 10 * k, capTop), barW, capH);
                }
                using (Pen bl = new Pen(lg, Math.Max(2f, 3 * k)))
                {
                    bl.StartCap = LineCap.Round; bl.EndCap = LineCap.Round;
                    g.DrawLine(bl, x0 - 4 * k, baseY + 6 * k, x0 + usedW + 4 * k, baseY + 6 * k);
                }
            }
            using (Brush bt = new SolidBrush(Color.FromArgb(alpha, 138, 138, 138)))
                g.DrawString(GraphName, fLabel, bt, gx + 8 * k, gy + 4 * k, sf);
        }

        // ---------- grafico RTA (barras, verde -> amarelo -> vermelho) ----------
        void DrawRta(Graphics g, float x, float y, float w, float h, float k, int alpha)
        {
            const int N = 50;
            float gapB = Math.Max(1.5f, 2 * k), bw = (w - gapB * (N - 1)) / N;
            float[] v = Rta; int bins = v == null ? 0 : v.Length;
            using (LinearGradientBrush lg = new LinearGradientBrush(new PointF(0, y + h), new PointF(0, y), Color.Green, Color.Red))
            using (Brush idle = new SolidBrush(Color.FromArgb(alpha * 40 / 255, 255, 255, 255)))
            {
                ColorBlend cb = new ColorBlend(3);
                cb.Colors = new Color[] { Color.FromArgb(alpha, 34, 197, 94), Color.FromArgb(alpha, 234, 179, 8), Color.FromArgb(alpha, 239, 68, 68) };
                cb.Positions = new float[] { 0f, 0.7f, 1f };
                lg.InterpolationColors = cb;
                for (int i = 0; i < N; i++)
                {
                    float db = -90;
                    if (bins > 0)
                    {
                        int a = i * bins / N, b = Math.Max(a + 1, (i + 1) * bins / N);
                        for (int j = a; j < b && j < bins; j++) db = Math.Max(db, v[j]);
                    }
                    float lv = Math.Max(0f, Math.Min(1f, (db + 80f) / 80f));
                    float bh = Math.Max(2 * k, lv * h);
                    g.FillRectangle(bins > 0 ? (Brush)lg : idle, x + i * (bw + gapB), y + h - bh, bw, bh);
                }
            }
        }

        // ---------- Painel 2 (compacto): [logo / icone+temp+cidade] | relogio + data em duas linhas ----------
        static string[] SplitMid(string t)
        {
            int best = -1, mid = t.Length / 2;
            for (int i = 0; i < t.Length; i++) if (t[i] == ' ' && (best < 0 || Math.Abs(i - mid) < Math.Abs(best - mid))) best = i;
            return best < 0 ? new string[] { t } : new string[] { t.Substring(0, best), t.Substring(best + 1) };
        }

        void DrawPanel2(Config c, string label, string temp)
        {
            float dpi; using (Graphics sg = Graphics.FromHwnd(IntPtr.Zero)) dpi = sg.DpiX;
            gripPx = (int)(26 * dpi / 96f);
            float sc = Math.Max(30, Math.Min(400, c.Scale)) / 100f;
            float k = dpi / 96f * sc;
            int alpha = Math.Max(30, Math.Min(100, c.Opacity)) * 255 / 100;
            Color main = Color.FromArgb(alpha, Parse(c.TextColor, Color.White));
            Color grey = Color.FromArgb(alpha, 138, 138, 138), grey2 = Color.FromArgb(alpha, 181, 181, 181);
            float F = Math.Max(12, c.FontSize) * sc;
            if (c.Suffix == "°C" && temp.EndsWith("°C")) temp = temp.Substring(0, temp.Length - 1);
            DateTime now = DateTime.Now;
            string hm = now.ToString("HH:mm"), ss = now.ToString("ss");
            string wd = now.ToString("dddd", PtBr); wd = char.ToUpper(wd[0]) + wd.Substring(1);
            string dm = now.ToString("d 'de' MMMM", PtBr);
            bool clock = c.ShowClock, secs = c.ShowClock && c.ShowSeconds, showDate = c.ShowDate;
            bool right = clock || showDate || c.HasAir, icon = c.ShowIcon && (WeatherCode >= 0 || ReserveIcon), hasLogo = logo != null;
            string[] city = label.Length > 0 ? SplitMid(label) : new string[0];
            bool rtaOn = c.HasRta;
            using (Font fClock = Semi(c.FontFamily, F), fSec = Semi(c.FontFamily, F * 0.45f), fTemp = Semi(c.FontFamily, F * 0.55f), fAir = Semi(c.FontFamily, Math.Max(10f, F * 0.40f)), fTimer = Semi(c.FontFamily, Math.Max(9f, F * 0.30f)))
            using (Font fDate = new Font(c.FontFamily, Math.Max(8f, F * 0.30f), FontStyle.Regular, GraphicsUnit.Point))
            using (Font fCity = new Font(c.FontFamily, Math.Max(8f, F * 0.24f), FontStyle.Regular, GraphicsUnit.Point))
            using (StringFormat sf = new StringFormat(StringFormat.GenericTypographic))
            using (Bitmap probe = new Bitmap(1, 1))
            using (Graphics pg = Graphics.FromImage(probe))
            {
                sf.FormatFlags |= StringFormatFlags.NoWrap;
                probe.SetResolution(dpi, dpi); pg.PageUnit = GraphicsUnit.Pixel; pg.TextRenderingHint = TextRenderingHint.AntiAlias;
                Func<string, Font, SizeF> M = delegate(string t, Font f) { return t.Length == 0 ? SizeF.Empty : pg.MeasureString(t, f, 10000, sf); };
                SizeF mHM = clock ? Fix(M(hm, fClock), MaxW(pg, fClock, sf, "##:##", dpi)) : SizeF.Empty, mSS = secs ? Fix(M(ss, fSec), MaxW(pg, fSec, sf, "##", dpi)) : SizeF.Empty;
                SizeF mD1 = showDate ? M(wd, fDate) : SizeF.Empty, mD2 = showDate ? M(dm, fDate) : SizeF.Empty;
                SizeF mTemp = Fix(M(temp, fTemp), MaxW(pg, fTemp, sf, Pat(temp), dpi));
                SizeF mC1 = city.Length > 0 ? M(city[0], fCity) : SizeF.Empty, mC2 = city.Length > 1 ? M(city[1], fCity) : SizeF.Empty;
                float gap = 8 * k, ig = 12 * k;
                float clockW = mHM.Width + (secs ? gap + mSS.Width : 0);
                float dotA = 0; SizeF plate = c.HasAir ? PlateSize(pg, fAir, sf, c, k, out dotA) : SizeF.Empty;
                string timerText = AirTimerText();
                SizeF mTimer = c.HasAir ? Fix(M(timerText, fTimer), MaxW(pg, fTimer, sf, "##:##:##", dpi)) : SizeF.Empty;
                float tGap = 10 * k, sideGap = 36 * k;
                float airW = Math.Max(plate.Width, mTimer.Width), airBlockH = c.HasAir ? plate.Height + tGap + mTimer.Height : 0;
                float clockBlockW = Math.Max(clockW, Math.Max(mD1.Width, mD2.Width));
                float rightW = clockBlockW + (c.HasAir && clockBlockW > 0 ? sideGap : 0) + (c.HasAir ? airW : 0);
                float rightH = mHM.Height + mD1.Height + mD2.Height;
                float rightHT = Math.Max(rightH, airBlockH);
                float iconS = mTemp.Height * 0.7f;
                float cityW = Math.Max(mC1.Width, mC2.Width), cityH = mC1.Height + mC2.Height;
                float rowW = (icon ? iconS + ig : 0) + mTemp.Width + (cityW > 0 ? ig + cityW : 0);
                float rowH = Math.Max(mTemp.Height, cityH);
                float Lw = Math.Max(rowW, hasLogo ? Math.Max(mHM.Width * 0.62f, 140 * k) : 0);
                float logoBoxH = hasLogo ? Lw * 0.55f : 0, gapL = hasLogo ? 24 * k : 0;
                float contentH = Math.Max(right ? rightHT : 0, logoBoxH + gapL + rowH);
                float padX = 44 * k, padY = 36 * k, side = 40 * k;
                float rtaGap = rtaOn ? 28 * k : 0, rtaH = rtaOn ? F * dpi / 72f * 0.95f : 0;
                float total = padX * 2 + Lw + (right ? side * 2 + 1 + rightW : 0);
                int w = (int)Math.Ceiling(total), h = (int)Math.Ceiling(contentH + padY * 2 + rtaGap + rtaH);
                using (Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb))
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    bmp.SetResolution(dpi, dpi);
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = SmoothingMode.AntiAlias; g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.TextRenderingHint = TextRenderingHint.AntiAlias;
                    Rectangle rect = new Rectangle(0, 0, w - 1, h - 1);
                    using (GraphicsPath gp = Round(rect, (int)(24 * k)))
                    {
                        using (Brush b = new SolidBrush(Color.FromArgb(alpha, Parse(c.CardColor, Color.FromArgb(13, 13, 13))))) g.FillPath(b, gp);
                        if (c.Locked) using (Pen p = new Pen(Color.FromArgb(alpha, 38, 38, 38), 1)) g.DrawPath(p, gp);
                        else using (Pen p = new Pen(Color.FromArgb(255, 255, 176, 0), Math.Max(2, 2 * k))) g.DrawPath(p, gp);
                    }
                    // coluna esquerda: logo em cima, temperatura embaixo
                    if (hasLogo)
                    {
                        float lh = logoBoxH, lw = Lw;
                        float sc2 = Math.Min(lw / logo.Width, lh / logo.Height);
                        float dw = logo.Width * sc2, dh = logo.Height * sc2;
                        g.DrawImage(logo, new RectangleF(padX + (lw - dw) / 2, padY + (lh - dh) / 2, dw, dh));
                    }
                    float ry = hasLogo ? padY + contentH - rowH : padY + (contentH - rowH) / 2;
                    float x = padX;
                    if (icon) { if (WeatherCode >= 0) DrawIcon(g, WeatherCode, x, ry + (rowH - iconS) / 2, iconS, alpha); x += iconS + ig; }
                    using (Brush bt = new SolidBrush(main)) g.DrawString(temp, fTemp, bt, x, ry + (rowH - mTemp.Height) / 2, sf);
                    x += mTemp.Width + ig;
                    using (Brush bc = new SolidBrush(grey))
                    {
                        float cy = ry + (rowH - cityH) / 2;
                        if (city.Length > 0) g.DrawString(city[0], fCity, bc, x, cy, sf);
                        if (city.Length > 1) g.DrawString(city[1], fCity, bc, x, cy + mC1.Height, sf);
                    }
                    // divisor + coluna direita (alinhada a esquerda, centralizada na vertical)
                    if (right)
                    {
                        float dx = padX + Lw + side;
                        using (Pen dv = new Pen(Color.FromArgb(alpha, 42, 42, 42), 1)) g.DrawLine(dv, dx, padY, dx, padY + contentH);
                        float rx = dx + 1 + side, y = padY + (contentH - rightH) / 2;
                        if (c.HasAir)   // NO AR ao lado do relogio, com o cronometro (tempo no ar) embaixo
                        {
                            float ax = rx + clockBlockW + (clockBlockW > 0 ? sideGap : 0), ay = padY + (contentH - airBlockH) / 2;
                            DrawPlate(g, c, AirState, fAir, sf, ax + (airW - plate.Width) / 2, ay, plate, dotA, k, alpha);
                            using (Brush bt2 = new SolidBrush(AirState == 1 ? main : grey))
                                g.DrawString(timerText, fTimer, bt2, ax + (airW - mTimer.Width) / 2, ay + plate.Height + tGap, sf);
                        }
                        if (clock)
                        {
                            using (Brush bm = new SolidBrush(main)) g.DrawString(hm, fClock, bm, rx, y, sf);
                            if (secs)
                                using (Brush bs = new SolidBrush(grey))
                                    g.DrawString(ss, fSec, bs, rx + pg.MeasureString(hm, fClock, 10000, sf).Width + gap, y + AscentPx(fClock, dpi) - AscentPx(fSec, dpi), sf);
                        }
                        if (showDate)
                            using (Brush bd = new SolidBrush(grey2))
                            {
                                g.DrawString(wd, fDate, bd, rx, y + mHM.Height, sf);
                                g.DrawString(dm, fDate, bd, rx, y + mHM.Height + mD1.Height, sf);
                            }
                    }
                    if (rtaOn) DrawRta(g, padX, padY + contentH + rtaGap, w - 2 * padX, rtaH, k, alpha);
                    if (!c.Locked) DrawGrip(g, w, h, k);
                    Push(bmp);
                }
            }
        }

        static void Cloud(Graphics g, Color col, float x, float y, float w, float h)
        {
            using (Brush b = new SolidBrush(col))
            {
                g.FillEllipse(b, x + w * 0.05f, y + h * 0.35f, w * 0.40f, h * 0.65f);
                g.FillEllipse(b, x + w * 0.25f, y, w * 0.50f, h * 0.85f);
                g.FillEllipse(b, x + w * 0.55f, y + h * 0.30f, w * 0.40f, h * 0.70f);
                g.FillRectangle(b, x + w * 0.22f, y + h * 0.55f, w * 0.56f, h * 0.45f);
            }
        }
        static void Sun(Graphics g, Color col, float cx, float cy, float r)
        {
            using (Pen p = new Pen(col, Math.Max(1.5f, r * 0.2f)))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                g.DrawEllipse(p, cx - r, cy - r, r * 2, r * 2);
                for (int i = 0; i < 8; i++)
                {
                    double a = i * Math.PI / 4;
                    g.DrawLine(p, cx + (float)Math.Cos(a) * r * 1.5f, cy + (float)Math.Sin(a) * r * 1.5f,
                                  cx + (float)Math.Cos(a) * r * 1.95f, cy + (float)Math.Sin(a) * r * 1.95f);
                }
            }
        }
        // icone do clima (codigos WMO do Open-Meteo), desenhado com formas simples
        static void DrawIcon(Graphics g, int code, float x, float y, float s, int alpha)
        {
            Color sun = Color.FromArgb(alpha, 240, 90, 60), cloud = Color.FromArgb(alpha, 203, 213, 225);
            Color rain = Color.FromArgb(alpha, 96, 165, 250), snow = Color.FromArgb(alpha, 241, 245, 249);
            if (code == 0) { Sun(g, sun, x + s / 2, y + s / 2, s * 0.24f); return; }
            if (code == 1 || code == 2)
            {
                Sun(g, sun, x + s * 0.68f, y + s * 0.34f, s * 0.17f);
                Cloud(g, cloud, x, y + s * 0.30f, s * 0.9f, s * 0.58f); return;
            }
            if (code == 45 || code == 48)
            {
                using (Pen p = new Pen(cloud, Math.Max(2f, s * 0.08f)))
                {
                    p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                    for (int i = 0; i < 3; i++) g.DrawLine(p, x + s * 0.15f, y + s * (0.30f + i * 0.2f), x + s * 0.85f, y + s * (0.30f + i * 0.2f));
                }
                return;
            }
            Cloud(g, cloud, x, y + s * 0.08f, s, s * 0.6f);
            bool rainy = (code >= 51 && code <= 67) || (code >= 80 && code <= 82), snowy = (code >= 71 && code <= 77) || code == 85 || code == 86;
            if (rainy)
                using (Pen p = new Pen(rain, Math.Max(2f, s * 0.07f)))
                {
                    p.StartCap = LineCap.Round; p.EndCap = LineCap.Round;
                    for (int i = 0; i < 3; i++) g.DrawLine(p, x + s * (0.28f + i * 0.22f), y + s * 0.74f, x + s * (0.22f + i * 0.22f), y + s * 0.94f);
                }
            else if (snowy)
                using (Brush b = new SolidBrush(snow))
                    for (int i = 0; i < 3; i++) g.FillEllipse(b, x + s * (0.24f + i * 0.22f), y + s * 0.78f, s * 0.12f, s * 0.12f);
            else if (code >= 95)
                using (Brush b = new SolidBrush(sun))
                    g.FillPolygon(b, new PointF[] { new PointF(x + s * 0.52f, y + s * 0.62f), new PointF(x + s * 0.36f, y + s * 0.84f),
                        new PointF(x + s * 0.48f, y + s * 0.84f), new PointF(x + s * 0.40f, y + s * 1.0f), new PointF(x + s * 0.66f, y + s * 0.76f),
                        new PointF(x + s * 0.54f, y + s * 0.76f) });
        }

        static void DrawText(Graphics g, string t, Font f, Color fg, float x, float y, StringFormat sf, bool card, float k)
        {
            if (!card)
                using (Brush sh = new SolidBrush(Color.FromArgb(fg.A * 140 / 255, 0, 0, 0))) g.DrawString(t, f, sh, x + k, y + k, sf);
            using (Brush b = new SolidBrush(fg)) g.DrawString(t, f, b, x, y, sf);
        }
        static GraphicsPath Round(Rectangle r, int d)
        {
            GraphicsPath p = new GraphicsPath();
            if (d < 2) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure(); return p;
        }
        public static Color Parse(string s, Color def)
        {
            try { return ColorTranslator.FromHtml(s); } catch { return def; }
        }
        void Push(Bitmap bmp)
        {
            IntPtr h0 = Handle; // garante o HWND
            IntPtr screen = Win.GetDC(IntPtr.Zero), mem = Win.CreateCompatibleDC(screen);
            IntPtr hb = bmp.GetHbitmap(Color.FromArgb(0)), old = Win.SelectObject(mem, hb);
            Size sz = bmp.Size; curSize = sz; Scale = lastScale; Point src = Point.Empty, dst = Pos;
            Win.BLEND bf = new Win.BLEND(); bf.Op = 0; bf.Flags = 0; bf.Alpha = 255; bf.Format = 1;
            Win.UpdateLayeredWindow(h0, screen, ref dst, ref sz, mem, ref src, 0, ref bf, 2);
            Win.SelectObject(mem, old); Win.DeleteObject(hb); Win.DeleteDC(mem); Win.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    // ---------- aplicativo (icone da bandeja + logica) ----------
    class AppCtx : ApplicationContext
    {
        public Config Cfg = Config.Load();
        public Overlay Ov = new Overlay();
        NotifyIcon ni = new NotifyIcon();
        System.Windows.Forms.Timer tmr = new System.Windows.Forms.Timer();
        ToolStripMenuItem miLock;
        SettingsForm settings;
        double? value;
        int code = -1;
        string logoPath = null;
        volatile bool exiting;
        volatile int airState = -1;
        long airSinceTicks;
        float[] rta; DateTime rtaSeen = DateTime.MinValue;
        float[] rtaCap;
        string lastTip = "";
        readonly AutoResetEvent airWake = new AutoResetEvent(false);
        DateTime t0 = DateTime.Now, nextRetry = DateTime.MinValue, lastFetch = DateTime.MinValue;
        bool fetching, updating;
        readonly bool firstRun = !File.Exists(Config.FilePath);

        public AppCtx()
        {
            Ov.ClickThrough = Cfg.Locked;
            Ov.Pos = new Point(Cfg.X, Cfg.Y);
            Ov.ScaleRequested += delegate(int pct) { Cfg.Scale = pct; Render(); if (settings != null && !settings.IsDisposed) settings.SyncScale(pct); };
            Ov.Moved += delegate { Cfg.X = Ov.Pos.X; Cfg.Y = Ov.Pos.Y; Cfg.Save(); };
            Ov.Show();
            ni.Icon = LoadIcon(); ni.Text = Const.App; ni.Visible = true;
            ni.DoubleClick += delegate { OpenSettings(); };
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("Configurações", null, delegate { OpenSettings(); });
            miLock = new ToolStripMenuItem("Travar posição (cliques passam)");
            miLock.Click += delegate { Cfg.Locked = !Cfg.Locked; Cfg.Save(); ApplyLock(); };
            menu.Items.Add(miLock);
            menu.Items.Add("Atualizar agora", null, delegate { Refresh(true); });
            menu.Items.Add("Verificar atualização do app", null, delegate { CheckUpdate(true); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Sair", null, delegate { ExitThread(); });
            menu.Opening += delegate { miLock.Checked = Cfg.Locked; };
            ni.ContextMenuStrip = menu;
            tmr.Interval = 3000; tmr.Tick += delegate { Tick(); }; tmr.Start();
            Thread airThread = new Thread(AirLoop); airThread.IsBackground = true; airThread.Name = "x32"; airThread.Start();
            Thread rtaThread = new Thread(RtaLoop); rtaThread.IsBackground = true; rtaThread.Name = "x32-rta"; rtaThread.Start();
            Render(); Refresh(true);
            if (firstRun || (Cfg.Source == "city" && !Cfg.Lat.HasValue)) OpenSettings();
            Win.Trim();
        }

        static Icon LoadIcon()
        {
            try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { return SystemIcons.Application; }
        }
        protected override void ExitThreadCore()
        {
            exiting = true; airWake.Set();
            ni.Visible = false; ni.Dispose(); Ov.Dispose();
            base.ExitThreadCore();
        }

        // consulta a X32 em segundo plano; so redesenha quando o estado muda
        void AirLoop()
        {
            int last = -99;
            while (!exiting)
            {
                string ip = Cfg.AirIp == null ? "" : Cfg.AirIp.Trim();
                bool active = Cfg.HasAir && ip.Length > 0;
                int st = -1;
                if (active) { try { st = X32.State(ip, Cfg.AirChannels, Cfg.AirFader); } catch { st = -1; } }
                if (st != last) { last = st; if (st == 1) airSinceTicks = DateTime.Now.Ticks; airState = st; Post(delegate { Render(); }); }
                airWake.WaitOne(active ? 800 : 2000);
            }
        }
        // fluxo do RTA da X32 (so ativo no estilo com RTA); ~5 quadros por segundo, sobe rapido e cai suave
        void RtaLoop()
        {
            UdpClient u = null; IPEndPoint ep = null; string curIp = "", curSrc = null;
            DateTime lastSub = DateTime.MinValue, lastPost = DateTime.MinValue;
            while (!exiting)
            {
                string ip = Cfg.AirIp == null ? "" : Cfg.AirIp.Trim(); IPAddress a;
                if (!((Cfg.HasRta || Cfg.HasGraph) && IPAddress.TryParse(ip, out a)))
                {
                    if (u != null) { u.Close(); u = null; }
                    if (rta != null) { rta = null; Post(delegate { Render(); }); }
                    Thread.Sleep(1000); continue;
                }
                try
                {
                    if (u == null || ip != curIp)
                    {
                        if (u != null) u.Close();
                        u = new UdpClient(AddressFamily.InterNetwork); ep = new IPEndPoint(IPAddress.Parse(ip), 10023); curIp = ip; lastSub = DateTime.MinValue; curSrc = null;
                    }
                    string wantSrc = Cfg.HasGraph ? (Cfg.GraphCh ?? "") : "";
                    if (wantSrc != curSrc)   // Painel 3: aponta o RTA da mesa para o canal escolhido nas configuracoes
                    {
                        curSrc = wantSrc; string nm; int si = X32.MeterIndex(wantSrc, out nm);
                        if (Cfg.HasGraph && si >= 0) { byte[] sp = X32.SetRtaSourcePacket(si); u.Send(sp, sp.Length, ep); }
                    }
                    if ((DateTime.UtcNow - lastSub).TotalSeconds > 8) { byte[] sub = X32.RtaSubscribePacket(Cfg.HasGraph ? 1 : 2); u.Send(sub, sub.Length, ep); lastSub = DateTime.UtcNow; }   // 50 ms (graficos fluidos) / 100 ms
                    u.Client.ReceiveTimeout = 500;
                    IPEndPoint from = new IPEndPoint(IPAddress.Any, 0);
                    byte[] d = u.Receive(ref from); float[] db;
                    if (X32.ParseRta(d, d.Length, out db))
                    {
                        double dt = rtaSeen == DateTime.MinValue ? 0.1 : Math.Max(0.02, Math.Min(0.3, (DateTime.UtcNow - rtaSeen).TotalSeconds));
                        float fall = (float)(28 * dt), capFall = (float)(14 * dt);   // dB por quadro, proporcional ao tempo (queda suave em qualquer taxa)
                        float[] cur = rta;
                        if (cur == null || cur.Length != db.Length) { cur = new float[db.Length]; for (int i = 0; i < cur.Length; i++) cur[i] = -90; }
                        float[] nxt = new float[db.Length];
                        for (int i = 0; i < nxt.Length; i++) nxt[i] = db[i] > cur[i] ? db[i] : Math.Max(db[i], cur[i] - fall);
                        float[] cc = rtaCap;
                        if (cc == null || cc.Length != nxt.Length) { cc = new float[nxt.Length]; for (int i = 0; i < cc.Length; i++) cc[i] = -90; }
                        float[] nc = new float[nxt.Length];
                        for (int i = 0; i < nc.Length; i++) nc[i] = Math.Max(nxt[i], cc[i] - capFall);   // pico segura e cai devagar
                        rtaCap = nc; rta = nxt; rtaSeen = DateTime.UtcNow;
                        if ((DateTime.UtcNow - lastPost).TotalMilliseconds > 55) { lastPost = DateTime.UtcNow; Post(delegate { Render(); }); }
                    }
                }
                catch (SocketException)
                {
                    if (rta != null && (DateTime.UtcNow - rtaSeen).TotalSeconds > 3) { rta = null; Post(delegate { Render(); }); }
                }
                catch (Exception) { Thread.Sleep(500); }
            }
            if (u != null) u.Close();
        }
        public void WakeAir() { airWake.Set(); }

        public void ApplyLock()
        {
            Ov.SetClickThrough(Cfg.Locked); Render();
        }
        string LabelText()
        {
            if (!Cfg.ShowLabel) return "";
            if (Cfg.Label.Trim().Length > 0) return Cfg.Label.Trim();
            return Cfg.Lat.HasValue ? Cfg.CityName : "";
        }
        string TempText()
        {
            if (!value.HasValue) return "--" + Cfg.Suffix;
            string s = value.Value.ToString("F" + Math.Max(0, Math.Min(1, Cfg.Decimals)), CultureInfo.InvariantCulture);
            if (s.TrimStart('-').Trim('0', '.').Length == 0) s = s.TrimStart('-');
            return s.Replace('.', ',') + Cfg.Suffix;
        }
        static Bitmap LoadLogo(string p)
        {
            try
            {
                if (string.IsNullOrEmpty(p) || !File.Exists(p)) return null;
                using (FileStream fs = File.OpenRead(p))
                using (Image im = Image.FromStream(fs))
                {
                    float sc = Math.Min(1f, 240f / im.Height);
                    return new Bitmap(im, Math.Max(1, (int)(im.Width * sc)), Math.Max(1, (int)(im.Height * sc)));
                }
            }
            catch { return null; }
        }
        public void Render()
        {
            if (Cfg.LogoPath != logoPath) { logoPath = Cfg.LogoPath; Ov.SetLogo(LoadLogo(logoPath)); }
            bool fast = Cfg.IsPanel && (Cfg.ShowClock || (Cfg.HasAir && airState == 1));   // 1 s: relogio ou cronometro do NO AR
            int want = fast ? 1000 : 3000;
            if (tmr.Interval != want) tmr.Interval = want;
            Ov.AirState = airState;
            Ov.Rta = rta;
            Ov.RtaCap = rtaCap;
            { string gn; X32.MeterIndex(Cfg.GraphCh, out gn); Ov.GraphName = Cfg.GraphLabel.Trim().Length > 0 ? Cfg.GraphLabel.Trim() : gn; }
            Ov.AirSince = airSinceTicks > 0 ? new DateTime(airSinceTicks) : DateTime.MinValue;
            string tip = (Cfg.HasAir && Cfg.AirIp.Trim().Length > 0 && airState < 0) ? "TempOverlay - X32 sem resposta" : Const.App;
            if (tip != lastTip) { lastTip = tip; ni.Text = tip; }   // so mexe na bandeja quando muda (o grafico redesenha varias vezes por segundo)
            Ov.WeatherCode = Cfg.Lat.HasValue ? code : -1;
            Ov.ReserveIcon = Cfg.Lat.HasValue;   // reserva o espaco do icone para o painel nao mudar de tamanho quando ele chega
            Ov.Draw(Cfg, LabelText(), TempText()); Ov.KeepOnTop();
        }

        public void Refresh(bool force)
        {
            if (Cfg.Source == "txt")
            {
                double? v = null;
                try
                {
                    using (FileStream fs = new FileStream(Cfg.TxtPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    using (StreamReader sr = new StreamReader(fs, Encoding.UTF8))
                    {
                        char[] buf = new char[4096]; int n = sr.Read(buf, 0, buf.Length);
                        Match m = Regex.Match(new string(buf, 0, n), "-?\\d+(?:[.,]\\d+)?");
                        if (m.Success) v = double.Parse(m.Value.Replace(',', '.'), CultureInfo.InvariantCulture);
                    }
                }
                catch { }
                if (v != value) { value = v; Render(); }
            }
            // Site: temperatura (fonte "cidade") ou so o icone do clima (fonte TXT, se houver cidade escolhida)
            if (!Cfg.Lat.HasValue || fetching) return;
            if (!force && (DateTime.Now - lastFetch).TotalMinutes < Math.Max(1, Cfg.RefreshMin)) return;
            fetching = true; lastFetch = DateTime.Now;
            double lat = Cfg.Lat.Value, lon = Cfg.Lon.Value;
            bool takeTemp = Cfg.Source != "txt";
            ThreadPool.QueueUserWorkItem(delegate
            {
                double? v = null; int wc = -1;
                try { v = Net.CityTemp(lat, lon, out wc); } catch { }
                Post(delegate
                {
                    fetching = false;
                    if (v.HasValue) { if (takeTemp) value = v; code = wc; } else if (force) lastFetch = DateTime.MinValue;
                    Render(); Win.Trim();
                });
            });
        }
        void Post(Action a)
        {
            try { if (Ov.IsHandleCreated) Ov.BeginInvoke(a); } catch { }
        }
        public void ResetValue() { value = null; }

        int ticks;
        void Tick()
        {
            if (++ticks % 30 == 0) Win.Trim();
            Refresh(false);
            if (Cfg.IsPanel && (Cfg.ShowClock || (Cfg.HasAir && airState == 1))) Render(); else Ov.KeepOnTop();
            if (UpdateDue()) CheckUpdate(false);
        }
        bool UpdateDue()
        {
            if (!Cfg.AutoUpdate || updating || DateTime.Now < nextRetry || (DateTime.Now - t0).TotalSeconds < 30) return false;
            DateTime last = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(Cfg.LastUpdateCheck).ToLocalTime();
            if ((DateTime.Now - last).TotalHours >= 24) return true;
            return DateTime.Now.Hour == Const.UpdateHour && last.Date != DateTime.Now.Date;
        }

        public void CheckUpdate(bool manual)
        {
            if (updating) return;
            updating = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                string msg = null, installer = null; bool retry = false;
                try
                {
                    string ver, url;
                    if (!Net.LatestRelease(out ver, out url)) throw new Exception("sem versao");
                    if (Net.Ver(ver) <= Net.Ver(Const.Version)) msg = "Você já está na versão mais recente (" + Const.Version + ").";
                    else installer = Net.Download(url);
                }
                catch { msg = "Não foi possível verificar agora (sem internet?)."; retry = true; }
                Post(delegate
                {
                    updating = false;
                    if (retry) nextRetry = DateTime.Now.AddMinutes(30);
                    else { Cfg.LastUpdateCheck = (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds; Cfg.Save(); }
                    if (installer != null)
                    {
                        try
                        {
                            Process.Start(new ProcessStartInfo(installer, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS") { UseShellExecute = true });
                            ExitThread();
                        }
                        catch { }
                    }
                    else if (manual && msg != null) MessageBox.Show(msg, Const.App);
                });
            });
        }

        public void OpenSettings()
        {
            if (settings != null && !settings.IsDisposed) { settings.TopMost = false; settings.TopMost = true; settings.Activate(); return; }
            Ov.Suspend = true;   // o widget para de se recolocar no topo, para a janela de configuracoes ficar por cima
            settings = new SettingsForm(this);
            settings.FormClosed += delegate { settings = null; Ov.Suspend = false; Ov.KeepOnTop(); Win.Trim(); };
            settings.Show();
            settings.TopMost = false; settings.TopMost = true; settings.Activate();   // sobe acima do widget (ambos sao "sempre no topo")
        }

        public static void SetAutostart(bool on)
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run", true))
                {
                    if (on) k.SetValue(Const.App, "\"" + Application.ExecutablePath + "\"");
                    else k.DeleteValue(Const.App, false);
                }
            }
            catch { }
        }
    }

    // ---------- configuracoes (WinForms nativo, claro e simples) ----------
    class SettingsForm : Form
    {
        static readonly Color Base = Color.FromArgb(0xE0, 0xE5, 0xEC), Ink = Color.FromArgb(0x1E, 0x29, 0x3B),
            Ink2 = Color.FromArgb(0x47, 0x55, 0x69), Orange = Color.FromArgb(0xF9, 0x73, 0x16);
        AppCtx app; Config snap; bool loading = true;
        Place sel; List<Place> results = new List<Place>();
        RadioButton rbCity, rbTxt;
        ComboBox cbStyle;
        static readonly string[] StyleKeys = { "transparent", "card", "panel", "panel2", "panel_air", "panel2_air", "panel2_air_rta", "panel3_air_graph" };
        static readonly string[] StyleNames = { "Texto", "Cartão", "Painel 1", "Painel 2", "Painel 1 + NO AR", "Painel 2 + NO AR", "Painel 2 + NO AR + RTA", "Painel 3 + NO AR + Gráfico" };
        TextBox tbCity, tbTxt, tbSuffix, tbLabel;
        Button btnSearch, btnBrowse, btnTextColor, btnCardColor;
        ListBox lb; Label lblSel;
        NumericUpDown nudRef, nudDec, nudSize;
        TrackBar tbOpacity;
        CheckBox chkLabel, chkLock, chkAuto, chkUpd, chkClock, chkSec, chkDate, chkIcon;
        TextBox tbAirIp, tbAirCh, tbAirText, tbGraphCh, tbGraphLabel; ComboBox cbGraphStyle; CheckBox chkReflect;
        Button btnColA, btnColB; string graphColA = "", graphColB = ""; CheckBox chkAirFader; Label lblAirTest; NumericUpDown nudGain;
        TextBox tbLogo; Button btnLogo; NumericUpDown nudScale;

        public SettingsForm(AppCtx a)
        {
            app = a; snap = a.Cfg.Clone();
            Config c = a.Cfg;
            Text = Const.App + " - Configurações"; Font = new Font("Segoe UI", 9f);
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; TopMost = true;
            StartPosition = FormStartPosition.CenterScreen; BackColor = Base; ForeColor = Ink;
            AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96f, 96f);
            ClientSize = new Size(460, 670); KeyPreview = true;
            KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) { Cancel(); } };
            FormClosing += delegate(object s, FormClosingEventArgs e) { if (e.CloseReason == CloseReason.UserClosing && !saved) RestoreSnap(); };

            sel = c.Lat.HasValue ? new Place { Name = c.CityName, Desc = c.CityDesc, Lat = c.Lat.Value, Lon = c.Lon.Value } : null;

            TabControl tabs = new TabControl { Left = 12, Top = 12, Width = 436, Height = 600 };
            TabPage t1 = Page("Fonte"), t2 = Page("Aparência"), t3 = Page("Geral"), t4 = Page("NO AR");
            tabs.TabPages.AddRange(new TabPage[] { t1, t2, t3, t4 });
            int startTab; if (int.TryParse(Environment.GetEnvironmentVariable("TEMPOVERLAY_TAB"), out startTab) && startTab >= 0 && startTab < 4) tabs.SelectedIndex = startTab; // (teste)
            Controls.Add(tabs);

            // --- fonte ---
            rbCity = new RadioButton { Text = "Temperatura do site (cidade)", Left = 14, Top = 14, Width = 380, Checked = c.Source == "city" };
            tbCity = new TextBox { Left = 14, Top = 44, Width = 270, Text = c.CityName };
            tbCity.KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Search(); } };
            btnSearch = Btn("Buscar", 292, 42, 90); btnSearch.Click += delegate { Search(); };
            lb = new ListBox { Left = 14, Top = 76, Width = 368, Height = 80, BorderStyle = BorderStyle.FixedSingle };
            lb.SelectedIndexChanged += delegate
            {
                if (lb.SelectedIndex >= 0 && lb.SelectedIndex < results.Count) { sel = results[lb.SelectedIndex]; lblSel.Text = SelText(); lblSel.ForeColor = Color.FromArgb(0x16, 0x65, 0x34); }
            };
            lblSel = new Label { Left = 14, Top = 162, Width = 368, Height = 18, Text = SelText(), ForeColor = sel != null ? Color.FromArgb(0x16, 0x65, 0x34) : Ink2 };
            Label l1 = Lbl("Atualizar a cada (min)", 14, 192, 170);
            nudRef = Nud(190, 188, 1, 120, c.RefreshMin);
            rbTxt = new RadioButton { Text = "Temperatura do arquivo TXT (só um número, ex.: 27,5)", Left = 14, Top = 236, Width = 400, Checked = c.Source == "txt" };
            tbTxt = new TextBox { Left = 14, Top = 266, Width = 270, Text = c.TxtPath };
            btnBrowse = Btn("Procurar", 292, 264, 90);
            btnBrowse.Click += delegate
            {
                using (OpenFileDialog d = new OpenFileDialog { Filter = "Texto (*.txt)|*.txt|Todos|*.*", Title = "Arquivo com a temperatura" })
                    if (d.ShowDialog(this) == DialogResult.OK) { tbTxt.Text = d.FileName; rbTxt.Checked = true; }
            };
            Label h1 = Lbl("O arquivo é relido a cada poucos segundos. Com o TXT, a cidade escolhida acima continua fornecendo o ícone do clima (e o nome).", 14, 296, 400); h1.ForeColor = Ink2; h1.Height = 52;
            rbCity.CheckedChanged += delegate { SourceState(); };
            t1.Controls.AddRange(new Control[] { rbCity, tbCity, btnSearch, lb, lblSel, l1, nudRef, rbTxt, tbTxt, btnBrowse, h1 });

            // --- aparencia ---
            tbSuffix = new TextBox { Left = 150, Top = 16, Width = 60, Text = c.Suffix };
            nudDec = Nud(150, 48, 0, 1, c.Decimals); nudSize = Nud(150, 80, 12, 300, c.FontSize);
            nudScale = Nud(320, 80, 30, 400, c.Scale);
            nudScale.ValueChanged += delegate { if (!loading) { app.Cfg.Scale = (int)nudScale.Value; app.Render(); } };
            tbOpacity = new TrackBar { Left = 142, Top = 112, Width = 240, Minimum = 30, Maximum = 100, TickFrequency = 10, Value = Math.Max(30, Math.Min(100, c.Opacity)), AutoSize = false, Height = 28 };
            btnTextColor = Btn("", 150, 150, 60); btnTextColor.BackColor = Overlay.Parse(c.TextColor, Color.White);
            btnCardColor = Btn("", 150, 182, 60); btnCardColor.BackColor = Overlay.Parse(c.CardColor, Color.Black);
            cbStyle = new ComboBox { Left = 62, Top = 214, Width = 250, DropDownStyle = ComboBoxStyle.DropDownList };
            cbStyle.Items.AddRange(StyleNames);
            int si = Array.IndexOf(StyleKeys, c.Style); cbStyle.SelectedIndex = si < 0 ? 0 : si;
            cbStyle.SelectedIndexChanged += delegate
            {
                if (cbStyle.SelectedIndex >= 2 && Hex(btnCardColor.BackColor) == "#101820") btnCardColor.BackColor = Color.FromArgb(13, 13, 13);
            };
            chkClock = Chk("Relógio", c.ShowClock); chkSec = Chk("Segundos", c.ShowSeconds);
            chkDate = Chk("Data", c.ShowDate); chkIcon = Chk("Ícone do clima", c.ShowIcon);
            FlowLayoutPanel flowPanel = new FlowLayoutPanel { Left = 14, Top = 338, Width = 410, Height = 56, BackColor = Base };
            flowPanel.Controls.AddRange(new Control[] { chkClock, chkSec, chkDate, chkIcon });
            tbLogo = new TextBox { Left = 60, Top = 404, Width = 252, Text = c.LogoPath };
            btnLogo = Btn("Escolher", 322, 402, 90);
            btnLogo.Click += delegate
            {
                using (OpenFileDialog d = new OpenFileDialog { Filter = "Imagens|*.png;*.jpg;*.jpeg;*.bmp;*.gif|Todos|*.*", Title = "Logo da rádio" })
                    if (d.ShowDialog(this) == DialogResult.OK) tbLogo.Text = d.FileName;
            };
            chkLabel = Chk("Mostrar rótulo", c.ShowLabel); chkLabel.Left = 14; chkLabel.Top = 256;
            tbLabel = new TextBox { Left = 170, Top = 254, Width = 236, Text = c.Label };
            Label h2 = Lbl("Rótulo vazio = nome da cidade.", 170, 282, 250); h2.ForeColor = Ink2;
            btnTextColor.Click += delegate { PickColor(btnTextColor); };
            btnCardColor.Click += delegate { PickColor(btnCardColor); };
            t2.Controls.AddRange(new Control[] { Lbl("Sufixo", 14, 19, 120), tbSuffix, Lbl("Casas decimais", 14, 51, 120), nudDec,
                Lbl("Tamanho da fonte", 14, 83, 130), nudSize, Lbl("Escala (%)", 240, 83, 76), nudScale, Lbl("Opacidade", 14, 117, 120), tbOpacity,
                Lbl("Cor do texto", 14, 155, 120), btnTextColor, Lbl("Cor do cartão", 14, 187, 120), btnCardColor,
                Lbl("Estilo", 14, 218, 48), cbStyle, chkLabel, tbLabel, h2,
                Lbl("Mostrar no painel:", 14, 314, 300), flowPanel, Lbl("Logo", 14, 407, 44), tbLogo, btnLogo });

            // --- geral ---
            chkLock = new CheckBox { Text = "Travar posição (cliques passam por cima)", Left = 14, Top = 16, Width = 380, Checked = c.Locked };
            chkAuto = new CheckBox { Text = "Iniciar com o Windows", Left = 14, Top = 48, Width = 380, Checked = c.Autostart };
            chkUpd = new CheckBox { Text = "Atualizar sozinho (a cada 24 h e de madrugada)", Left = 14, Top = 80, Width = 380, Checked = c.AutoUpdate };
            Button btnUpd = Btn("Verificar atualização agora (v" + Const.Version + ")", 14, 116, 280); btnUpd.Click += delegate { app.CheckUpdate(true); };
            Label h3 = Lbl("Destravado: arraste o painel (contorno laranja) e, para redimensionar, arraste o canto inferior direito. Para reabrir esta janela, use o ícone perto do relógio.", 14, 160, 368);
            h3.Width = 380; h3.Height = 84; h3.ForeColor = Ink2;
            t3.Controls.AddRange(new Control[] { chkLock, chkAuto, chkUpd, btnUpd, h3 });

            // --- NO AR (Behringer X32) ---
            Label hA = Lbl("Usado nos estilos \"+ NO AR\" (aba Aparência).", 14, 14, 380); hA.ForeColor = Ink2;
            tbAirIp = new TextBox { Left = 14, Top = 60, Width = 200, Text = c.AirIp };
            tbAirText = new TextBox { Left = 230, Top = 60, Width = 180, Text = c.AirText };
            tbAirCh = new TextBox { Left = 14, Top = 116, Width = 400, Text = c.AirChannels };
            Label hC = Lbl("Ex.: 1 (microfone), 1,2,5, aux1, bus3, main, dca2. O aviso acende se qualquer um dos canais estiver aberto.", 14, 142, 380);
            hC.ForeColor = Ink2; hC.Height = 40;
            chkAirFader = new CheckBox { Text = "Considerar o fader (aberto se acima de -60 dB)", Left = 14, Top = 186, Width = 380, Checked = c.AirFader };
            tbGraphCh = new TextBox { Left = 14, Top = 254, Width = 80, Text = c.GraphCh };
            tbGraphLabel = new TextBox { Left = 108, Top = 254, Width = 170, Text = c.GraphLabel };
            nudGain = new NumericUpDown { Left = 292, Top = 254, Width = 80, Minimum = 10, Maximum = 400, Value = Math.Max(10, Math.Min(400, c.GraphGain)) };
            cbGraphStyle = new ComboBox { Left = 14, Top = 312, Width = 210, DropDownStyle = ComboBoxStyle.DropDownList };
            cbGraphStyle.Items.AddRange(new object[] { "Barras coloridas com picos", "Barras azuis arredondadas" });
            cbGraphStyle.SelectedIndex = c.GraphStyle == "pill" ? 1 : 0;
            chkReflect = new CheckBox { Text = "Reflexo", Left = 240, Top = 314, Width = 120, Checked = c.GraphReflect };
            graphColA = c.GraphColorA ?? ""; graphColB = c.GraphColorB ?? "";
            btnColA = Btn("", 14, 372, 64); btnColB = Btn("", 86, 372, 64);
            Button btnColReset = Btn("Padrão", 158, 372, 80);
            btnColA.Click += delegate { PickGraphColor(true); };
            btnColB.Click += delegate { PickGraphColor(false); };
            btnColReset.Click += delegate { graphColA = ""; graphColB = ""; RefreshGraphSwatches(); };
            cbGraphStyle.SelectedIndexChanged += delegate { RefreshGraphSwatches(); };
            RefreshGraphSwatches();
            Button btnAirTest = Btn("Testar conexão", 14, 420, 150);
            Label hR = Lbl("Painel 3: o app aponta o RTA da mesa para o canal escolhido. Painel 2 + RTA: usa a fonte do RTA da mesa.", 14, 458, 380);
            hR.ForeColor = Ink2; hR.Height = 40;
            lblAirTest = new Label { Left = 14, Top = 502, Width = 380, Height = 70, Text = "", ForeColor = Ink };
            btnAirTest.Click += delegate
            {
                string ip = tbAirIp.Text.Trim(), ch = tbAirCh.Text, gch = tbGraphCh.Text;
                string styleKey = StyleKeys[Math.Max(0, cbStyle.SelectedIndex)];
                bool wantRta = styleKey == "panel2_air_rta", wantGraph = styleKey == "panel3_air_graph";
                lblAirTest.ForeColor = Ink2; lblAirTest.Text = "Consultando a mesa...";
                ThreadPool.QueueUserWorkItem(delegate
                {
                    string r;
                    try
                    {
                        r = X32.Describe(ip, ch);
                        if (wantRta) r += "\n" + X32.RtaProbe(ip);
                        if (wantGraph) r += "\n" + X32.GraphProbe(ip, gch);
                    }
                    catch (Exception ex) { r = "Erro: " + ex.Message; }
                    try { BeginInvoke((Action)delegate { lblAirTest.ForeColor = r.StartsWith("Conectado") ? Color.FromArgb(0x16, 0x65, 0x34) : Ink; lblAirTest.Text = r; }); } catch { }
                });
            };
            t4.Controls.AddRange(new Control[] { hA, Lbl("IP da mesa X32", 14, 38, 210), Lbl("Texto do aviso no ar", 230, 38, 180), tbAirIp, tbAirText,
                Lbl("Canais do aviso NO AR", 14, 94, 380), tbAirCh, hC, chkAirFader,
                Lbl("Canal (1-32)", 14, 232, 90), Lbl("Rótulo do gráfico", 108, 232, 170), Lbl("Sensib. (%)", 292, 232, 90),
                tbGraphCh, tbGraphLabel, nudGain,
                Lbl("Aparência do gráfico", 14, 290, 200), cbGraphStyle, chkReflect,
                Lbl("Cores do gráfico (degradê da Cor 1 para a Cor 2)", 14, 348, 400), btnColA, btnColB, btnColReset,
                btnAirTest, hR, lblAirTest });

            // --- rodape ---
            Button bc = Btn("Cancelar", 150, 622, 90), ba = Btn("Aplicar", 248, 622, 90), bs = Btn("Salvar", 346, 622, 100);
            bs.BackColor = Orange; bs.FlatAppearance.BorderColor = Orange; bs.Font = new Font(Font, FontStyle.Bold);
            bc.Click += delegate { Cancel(); }; ba.Click += delegate { Save(false); }; bs.Click += delegate { Save(true); };
            AcceptButton = bs;
            Controls.AddRange(new Control[] { bc, ba, bs });

            // previa ao vivo da aparencia
            EventHandler live = delegate { Preview(); };
            tbSuffix.TextChanged += live; nudDec.ValueChanged += live; nudSize.ValueChanged += live; tbOpacity.ValueChanged += live;
            cbStyle.SelectedIndexChanged += live; chkClock.CheckedChanged += live; chkSec.CheckedChanged += live;
            chkDate.CheckedChanged += live; chkIcon.CheckedChanged += live; tbLogo.TextChanged += live; chkLabel.CheckedChanged += live; tbLabel.TextChanged += live; chkLock.CheckedChanged += live;
            SourceState();
            loading = false;
        }

        bool saved;
        public void SyncScale(int pct)
        {
            bool l = loading; loading = true;
            nudScale.Value = Math.Max(nudScale.Minimum, Math.Min(nudScale.Maximum, pct)); loading = l;
        }
        static RadioButton Rad(string t, bool on)
        {
            return new RadioButton { Text = t, AutoSize = true, Checked = on, Margin = new Padding(0, 3, 4, 0) };
        }
        static CheckBox Chk(string t, bool on)
        {
            return new CheckBox { Text = t, AutoSize = true, Checked = on, Margin = new Padding(0, 3, 16, 3) };
        }
        static TabPage Page(string t) { return new TabPage(t) { BackColor = Base, UseVisualStyleBackColor = false }; }
        static Label Lbl(string t, int x, int y, int w) { return new Label { Text = t, Left = x, Top = y, Width = w, Height = 20 }; }
        static NumericUpDown Nud(int x, int y, int min, int max, int v)
        {
            return new NumericUpDown { Left = x, Top = y, Width = 70, Minimum = min, Maximum = max, Value = Math.Max(min, Math.Min(max, v)) };
        }
        static Button Btn(string t, int x, int y, int w)
        {
            Button b = new Button { Text = t, Left = x, Top = y, Width = w, Height = 28, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(0xEE, 0xF1, 0xF5), UseVisualStyleBackColor = false };
            b.FlatAppearance.BorderColor = Color.FromArgb(0xA3, 0xB1, 0xC6); b.FlatAppearance.MouseOverBackColor = Color.White;
            return b;
        }
        string SelText() { return sel != null ? "Selecionada: " + sel.Desc : "Nenhuma cidade selecionada"; }

        void SourceState()
        {
            bool city = rbCity.Checked;   // a cidade fica sempre ativa: no modo TXT ela fornece o icone do clima
            tbTxt.Enabled = btnBrowse.Enabled = !city;
        }
        void RefreshGraphSwatches()
        {
            bool pill = cbGraphStyle.SelectedIndex == 1;
            btnColA.BackColor = graphColA.Length > 0 ? Overlay.Parse(graphColA, Color.White) : (pill ? Color.FromArgb(70, 200, 250) : Color.FromArgb(99, 102, 241));
            btnColB.BackColor = graphColB.Length > 0 ? Overlay.Parse(graphColB, Color.White) : (pill ? Color.FromArgb(12, 140, 210) : Color.FromArgb(251, 146, 60));
        }
        void PickGraphColor(bool first)
        {
            Button b = first ? btnColA : btnColB;
            using (ColorDialog d = new ColorDialog { Color = b.BackColor, FullOpen = true })
                if (d.ShowDialog(this) == DialogResult.OK)
                {
                    if (first) graphColA = Hex(d.Color); else graphColB = Hex(d.Color);
                    RefreshGraphSwatches();
                }
        }
        void PickColor(Button b)
        {
            using (ColorDialog d = new ColorDialog { Color = b.BackColor, FullOpen = true })
                if (d.ShowDialog(this) == DialogResult.OK) { b.BackColor = d.Color; Preview(); }
        }
        void Search()
        {
            string q = tbCity.Text.Trim(); if (q.Length == 0) return;
            lb.Items.Clear(); lb.Items.Add("Buscando...");
            ThreadPool.QueueUserWorkItem(delegate
            {
                List<Place> res = null, off = null; string err = "";
                List<Place> coords = Offline.Search(q);
                bool isCoord = coords.Count == 1 && coords[0].Name == "Local";
                if (isCoord) res = coords;
                for (int i = 0; i < 2 && res == null; i++)
                {
                    try { res = Net.SearchCity(q); }
                    catch (Exception ex) { err = ex.Message; Thread.Sleep(700); }
                }
                if (res == null) off = Offline.Search(q);
                try
                {
                    BeginInvoke((Action)delegate
                    {
                        if (off != null && off.Count > 0)
                        {
                            results = off; lb.Items.Clear();
                            foreach (Place p in off) lb.Items.Add(p.Desc);
                            lblSel.ForeColor = Ink2; lblSel.Text = "Sem acesso ao site de busca: mostrando a lista embutida.";
                            return;
                        }
                        results = res ?? new List<Place>(); lb.Items.Clear();
                        if (res == null) { lb.Items.Add("Sem acesso ao site de busca (" + err + ")."); lb.Items.Add("Digite as coordenadas, ex.: -27.63, -52.27"); }
                        else if (res.Count == 0) lb.Items.Add("Nada encontrado");
                        foreach (Place p in results) lb.Items.Add(p.Desc);
                    });
                }
                catch { }
            });
        }

        static string Hex(Color c) { return string.Format("#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B); }
        void ReadAppearance(Config c)
        {
            c.Suffix = tbSuffix.Text; c.Decimals = (int)nudDec.Value; c.FontSize = (int)nudSize.Value; c.Opacity = tbOpacity.Value;
            c.TextColor = Hex(btnTextColor.BackColor); c.CardColor = Hex(btnCardColor.BackColor);
            c.Style = StyleKeys[Math.Max(0, cbStyle.SelectedIndex)];
            c.ShowClock = chkClock.Checked; c.ShowSeconds = chkSec.Checked; c.ShowDate = chkDate.Checked; c.ShowIcon = chkIcon.Checked;
            c.LogoPath = tbLogo.Text.Trim(); c.ShowLabel = chkLabel.Checked; c.Label = tbLabel.Text;
            c.Locked = chkLock.Checked;
        }
        void Preview()
        {
            if (loading) return;
            ReadAppearance(app.Cfg); app.ApplyLock();
        }
        void RestoreSnap()
        {
            Config c = app.Cfg;
            c.Suffix = snap.Suffix; c.Decimals = snap.Decimals; c.FontSize = snap.FontSize; c.Opacity = snap.Opacity;
            c.TextColor = snap.TextColor; c.CardColor = snap.CardColor; c.Style = snap.Style; c.ShowLabel = snap.ShowLabel;
            c.Label = snap.Label; c.Locked = snap.Locked;
            c.ShowClock = snap.ShowClock; c.ShowSeconds = snap.ShowSeconds; c.ShowDate = snap.ShowDate; c.ShowIcon = snap.ShowIcon;
            c.LogoPath = snap.LogoPath; c.Scale = snap.Scale;
            app.ApplyLock();
        }
        void Cancel() { RestoreSnap(); saved = true; Close(); }

        void Save(bool close)
        {
            Config c = app.Cfg;
            bool city = rbCity.Checked;
            if (city && sel == null) { MessageBox.Show(this, "Busque e selecione uma cidade (ou escolha a fonte TXT).", Const.App); return; }
            if (!city && tbTxt.Text.Trim().Length == 0) { MessageBox.Show(this, "Escolha o arquivo TXT.", Const.App); return; }
            ReadAppearance(c);
            c.Source = city ? "city" : "txt"; c.TxtPath = tbTxt.Text.Trim(); c.RefreshMin = (int)nudRef.Value;
            if (sel != null) { c.CityName = sel.Name; c.CityDesc = sel.Desc; c.Lat = sel.Lat; c.Lon = sel.Lon; }
            c.Autostart = chkAuto.Checked; c.AutoUpdate = chkUpd.Checked;
            c.AirIp = tbAirIp.Text.Trim(); c.AirChannels = tbAirCh.Text.Trim().Length == 0 ? "1" : tbAirCh.Text.Trim();
            c.GraphCh = tbGraphCh.Text.Trim().Length == 0 ? "1" : tbGraphCh.Text.Trim(); c.GraphLabel = tbGraphLabel.Text.Trim(); c.GraphStyle = cbGraphStyle.SelectedIndex == 1 ? "pill" : "gradient"; c.GraphReflect = chkReflect.Checked;
            c.GraphColorA = graphColA; c.GraphColorB = graphColB; c.GraphGain = (int)nudGain.Value;
            c.AirFader = chkAirFader.Checked; c.AirText = tbAirText.Text.Trim().Length == 0 ? "NO AR" : tbAirText.Text.Trim();
            c.Save(); AppCtx.SetAutostart(c.Autostart);
            snap = c.Clone(); app.ResetValue(); app.WakeAir(); app.ApplyLock(); app.Refresh(true);
            if (close) { saved = true; Close(); }
        }
    }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool created;
            using (Mutex mx = new Mutex(true, "Local\\TempOverlay_single", out created))
            {
                if (!created) return;
                Win.SetProcessDPIAware();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new AppCtx());
            }
        }
    }
}
