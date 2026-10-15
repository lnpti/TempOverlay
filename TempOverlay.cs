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
[assembly: AssemblyVersion("1.2.5.0")]

namespace TempOverlay
{
    static class Const
    {
        public const string App = "TempOverlay";
        public const string Version = "1.2.5";
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

        public static readonly string Dir = Environment.GetEnvironmentVariable("TEMPOVERLAY_DIR") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Const.App);
        public static readonly string FilePath = Path.Combine(Dir, "config.json");

        public Config Clone() { return (Config)MemberwiseClone(); }
        public bool IsPanel { get { return Style == "panel" || Style == "panel2"; } }

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
        public void KeepOnTop()
        {
            if (IsHandleCreated) Win.SetWindowPos(Handle, (IntPtr)(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
        }

        int lastScale = 100;
        public void Draw(Config c, string label, string temp)
        {
            lastScale = c.Scale;
            if (c.Style == "panel") { DrawPanel(c, label, temp); return; }
            if (c.Style == "panel2") { DrawPanel2(c, label, temp); return; }
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
            using (Font fClock = Semi(c.FontFamily, F), fSec = Semi(c.FontFamily, F * 0.45f), fTemp = Semi(c.FontFamily, F * 0.85f))
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
                float clockW = mHM.Width + (secs ? gap + mSS.Width : 0);
                float b1W = Math.Max(clockW, mDate.Width), b1H = mHM.Height + mDate.Height;
                float iconS = mTemp.Height * 0.72f;
                float tempW = (icon ? iconS + ig : 0) + mTemp.Width;
                float b2W = Math.Max(tempW, mCity.Width), b2H = mTemp.Height + mCity.Height;
                float contentH = Math.Max(left ? b1H : 0, b2H);
                float logoH = contentH * 0.95f, logoW = hasLogo ? Math.Min(logo.Width * logoH / logo.Height, contentH * 2.4f) : 0;
                float padX = 34 * k, padY = 24 * k, side = 40 * k;
                int sections = 1 + (left ? 1 : 0) + (hasLogo ? 1 : 0);
                float total = padX * 2 + b2W + (left ? b1W : 0) + logoW + (sections - 1) * (side * 2 + 1);
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
            bool right = clock || showDate, icon = c.ShowIcon && (WeatherCode >= 0 || ReserveIcon), hasLogo = logo != null;
            string[] city = label.Length > 0 ? SplitMid(label) : new string[0];
            using (Font fClock = Semi(c.FontFamily, F), fSec = Semi(c.FontFamily, F * 0.45f), fTemp = Semi(c.FontFamily, F * 0.55f))
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
                float rightW = Math.Max(clockW, Math.Max(mD1.Width, mD2.Width));
                float rightH = mHM.Height + mD1.Height + mD2.Height;
                float iconS = mTemp.Height * 0.7f;
                float cityW = Math.Max(mC1.Width, mC2.Width), cityH = mC1.Height + mC2.Height;
                float rowW = (icon ? iconS + ig : 0) + mTemp.Width + (cityW > 0 ? ig + cityW : 0);
                float rowH = Math.Max(mTemp.Height, cityH);
                float Lw = Math.Max(rowW, hasLogo ? Math.Max(mHM.Width * 0.62f, 140 * k) : 0);
                float logoBoxH = hasLogo ? Lw * 0.55f : 0, gapL = hasLogo ? 24 * k : 0;
                float contentH = Math.Max(right ? rightH : 0, logoBoxH + gapL + rowH);
                float padX = 44 * k, padY = 36 * k, side = 40 * k;
                float total = padX * 2 + Lw + (right ? side * 2 + 1 + rightW : 0);
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
            ni.Visible = false; ni.Dispose(); Ov.Dispose();
            base.ExitThreadCore();
        }

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
            bool fast = Cfg.IsPanel && Cfg.ShowClock;
            int want = fast ? 1000 : 3000;
            if (tmr.Interval != want) tmr.Interval = want;
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
            if (Cfg.IsPanel && Cfg.ShowClock) Render(); else Ov.KeepOnTop();
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
            if (settings != null && !settings.IsDisposed) { settings.Activate(); return; }
            settings = new SettingsForm(this);
            settings.FormClosed += delegate { settings = null; Win.Trim(); };
            settings.Show();
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
        RadioButton rbCity, rbTxt, rbTransp, rbCard;
        TextBox tbCity, tbTxt, tbSuffix, tbLabel;
        Button btnSearch, btnBrowse, btnTextColor, btnCardColor;
        ListBox lb; Label lblSel;
        NumericUpDown nudRef, nudDec, nudSize;
        TrackBar tbOpacity;
        CheckBox chkLabel, chkLock, chkAuto, chkUpd, chkClock, chkSec, chkDate, chkIcon;
        RadioButton rbPanel, rbPanel2;
        TextBox tbLogo; Button btnLogo; NumericUpDown nudScale;

        public SettingsForm(AppCtx a)
        {
            app = a; snap = a.Cfg.Clone();
            Config c = a.Cfg;
            Text = Const.App + " - Configurações"; Font = new Font("Segoe UI", 9f);
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; TopMost = true;
            StartPosition = FormStartPosition.CenterScreen; BackColor = Base; ForeColor = Ink;
            AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96f, 96f);
            ClientSize = new Size(460, 540); KeyPreview = true;
            KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) { Cancel(); } };
            FormClosing += delegate(object s, FormClosingEventArgs e) { if (e.CloseReason == CloseReason.UserClosing && !saved) RestoreSnap(); };

            sel = c.Lat.HasValue ? new Place { Name = c.CityName, Desc = c.CityDesc, Lat = c.Lat.Value, Lon = c.Lon.Value } : null;

            TabControl tabs = new TabControl { Left = 12, Top = 12, Width = 436, Height = 470 };
            TabPage t1 = Page("Fonte"), t2 = Page("Aparência"), t3 = Page("Geral");
            tabs.TabPages.AddRange(new TabPage[] { t1, t2, t3 });
            int startTab; if (int.TryParse(Environment.GetEnvironmentVariable("TEMPOVERLAY_TAB"), out startTab) && startTab >= 0 && startTab < 3) tabs.SelectedIndex = startTab; // (teste)
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
            rbTransp = Rad("Transparente", c.Style != "card" && c.Style != "panel" && c.Style != "panel2");
            rbCard = Rad("Cartão", c.Style == "card");
            rbPanel = Rad("Painel 1", c.Style == "panel");
            rbPanel2 = Rad("Painel 2", c.Style == "panel2");
            FlowLayoutPanel flowStyle = new FlowLayoutPanel { Left = 62, Top = 212, Width = 372, Height = 30, WrapContents = false, BackColor = Base };
            flowStyle.Controls.AddRange(new Control[] { rbTransp, rbCard, rbPanel, rbPanel2 });
            EventHandler darker = delegate
            {
                if ((rbPanel.Checked || rbPanel2.Checked) && Hex(btnCardColor.BackColor) == "#101820") btnCardColor.BackColor = Color.FromArgb(13, 13, 13);
            };
            rbPanel.CheckedChanged += darker; rbPanel2.CheckedChanged += darker;
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
                Lbl("Estilo", 14, 218, 48), flowStyle, chkLabel, tbLabel, h2,
                Lbl("Mostrar no painel:", 14, 314, 300), flowPanel, Lbl("Logo", 14, 407, 44), tbLogo, btnLogo });

            // --- geral ---
            chkLock = new CheckBox { Text = "Travar posição (os cliques passam por cima do painel)", Left = 14, Top = 16, Width = 380, Checked = c.Locked };
            chkAuto = new CheckBox { Text = "Iniciar com o Windows", Left = 14, Top = 48, Width = 380, Checked = c.Autostart };
            chkUpd = new CheckBox { Text = "Atualizar sozinho (a cada 24 h e de madrugada)", Left = 14, Top = 80, Width = 380, Checked = c.AutoUpdate };
            Button btnUpd = Btn("Verificar atualização agora (v" + Const.Version + ")", 14, 116, 280); btnUpd.Click += delegate { app.CheckUpdate(true); };
            Label h3 = Lbl("Destravado: arraste o painel (contorno laranja) e, para redimensionar, arraste o canto inferior direito. Para reabrir esta janela, use o ícone perto do relógio.", 14, 160, 368);
            h3.Height = 52; h3.ForeColor = Ink2;
            t3.Controls.AddRange(new Control[] { chkLock, chkAuto, chkUpd, btnUpd, h3 });

            // --- rodape ---
            Button bc = Btn("Cancelar", 150, 492, 90), ba = Btn("Aplicar", 248, 492, 90), bs = Btn("Salvar", 346, 492, 100);
            bs.BackColor = Orange; bs.FlatAppearance.BorderColor = Orange; bs.Font = new Font(Font, FontStyle.Bold);
            bc.Click += delegate { Cancel(); }; ba.Click += delegate { Save(false); }; bs.Click += delegate { Save(true); };
            AcceptButton = bs;
            Controls.AddRange(new Control[] { bc, ba, bs });

            // previa ao vivo da aparencia
            EventHandler live = delegate { Preview(); };
            tbSuffix.TextChanged += live; nudDec.ValueChanged += live; nudSize.ValueChanged += live; tbOpacity.ValueChanged += live;
            rbCard.CheckedChanged += live; rbPanel.CheckedChanged += live; rbPanel2.CheckedChanged += live; chkClock.CheckedChanged += live; chkSec.CheckedChanged += live;
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
            c.Style = rbPanel2.Checked ? "panel2" : (rbPanel.Checked ? "panel" : (rbCard.Checked ? "card" : "transparent"));
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
            c.Save(); AppCtx.SetAutostart(c.Autostart);
            snap = c.Clone(); app.ResetValue(); app.ApplyLock(); app.Refresh(true);
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
