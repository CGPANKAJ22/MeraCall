//==CSHARP==
// MeraPrint Client (PC) - Host PC ke saare printer is PC ke HAR software me (Aadhaar client, Word, PDF, browser...)
// Kaise: har Host printer ke liye yahan ek printer banta hai ("... (MeraPrint - Host)") jo "Microsoft Print To PDF"
// se PDF banakar C:\ProgramData\MeraPrint\spool me rakhta hai. Ye program use turant Host par bhej deta hai.
// Windows ka apna C# compiler ise banata hai (MeraPrint-Client-Setup.bat) - kuch install nahi.
using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Drawing;
using System.Diagnostics;
using System.Collections;
using System.Collections.Generic;
using System.Windows.Forms;
using System.Web.Script.Serialization;
using Microsoft.Win32;

static class MP
{
    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeraPrint");
    static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MeraPrint");
    static readonly string Spool = Path.Combine(Root, "spool");
    static readonly string Outbox = Path.Combine(Dir, "outbox");
    static readonly string CfgFile = Path.Combine(Dir, "client.json");
    static readonly JavaScriptSerializer J = new JavaScriptSerializer() { MaxJsonLength = int.MaxValue };
    static Dictionary<string, object> cfg = new Dictionary<string, object>();
    static NotifyIcon tray;
    static Control ui;
    static string baseUrl;
    static readonly object cfgLock = new object();

    // ---------------- Start ----------------
    [STAThread]
    static void Main(string[] args)
    {
        Files.BaseUrl = delegate { return Base(baseUrl == null); };
        Files.Token = delegate { return S("token"); };
        Files.HostName = delegate { return S("hostName"); };
        if (args.Length > 0 && args[0] == "/send")
        {
            // Right-click > Send to > MeraPrint Host
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
            Application.EnableVisualStyles();
            Directory.CreateDirectory(Dir); LoadCfg();
            if (!cfg.ContainsKey("token")) { MessageBox.Show("Pehle MeraPrint Client ko Host se jodiye.", "MeraPrint"); return; }
            string[] fl = new string[args.Length - 1]; Array.Copy(args, 1, fl, 0, fl.Length);
            Files.SendMode(fl);
            return;
        }
        bool first;
        Mutex mx = new Mutex(true, "MeraPrintClient_Pankaj", out first);
        if (!first) { MessageBox.Show("MeraPrint Client pehle se chal raha hai (neeche ghadi ke paas icon).", "MeraPrint"); return; }
        ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;   // TLS 1.2 (internet raasta)
        Application.EnableVisualStyles();
        Directory.CreateDirectory(Dir); Directory.CreateDirectory(Outbox);
        LoadCfg();
        string hosts = "";
        for (int i = 0; i < args.Length - 1; i++) if (args[i] == "/hosts") hosts = args[i + 1];
        if (hosts == "__HOSTS__") hosts = "";
        if (hosts != "" && cfg.ContainsKey("token"))
        {
            string[] hp = hosts.Split('|');
            if (hp.Length > 3 && hp[3] != "" && hp[0] == S("hostId")) { cfg["gh"] = hp[3]; SaveCfg(); }
        }
        if (!cfg.ContainsKey("token") || args.Length > 0 && Array.IndexOf(args, "/reset") >= 0)
        {
            if (!SetupForm(hosts)) return;
            MakePrinters(true);
        }
        AutoStart();
        ui = new Control(); ui.CreateControl();
        MakeTray();
        Thread w = new Thread(WatchLoop); w.IsBackground = true; w.Start();
        Thread r = new Thread(RetryLoop); r.IsBackground = true; r.Start();
        StartScanner();
        Thread up = new Thread(UpdateLoop); up.IsBackground = true; up.Start();
        Application.Run();
        GC.KeepAlive(mx);
    }

    // Token disk par Windows DPAPI se band: file copy karke doosre PC / doosre user par le jao to nahi khulti
    static readonly byte[] Ent = Encoding.UTF8.GetBytes("MeraPrint-Pankaj-v1");
    static void LoadCfg()
    {
        try { if (File.Exists(CfgFile)) cfg = J.Deserialize<Dictionary<string, object>>(File.ReadAllText(CfgFile, Encoding.UTF8)); } catch { }
        if (cfg == null) cfg = new Dictionary<string, object>();
        object enc;
        if (cfg.TryGetValue("tokenEnc", out enc) && enc != null)
        {
            try { cfg["token"] = Encoding.UTF8.GetString(System.Security.Cryptography.ProtectedData.Unprotect(Convert.FromBase64String(enc.ToString()), Ent, System.Security.Cryptography.DataProtectionScope.CurrentUser)); }
            catch { cfg.Remove("token"); }
            cfg.Remove("tokenEnc");
        }
        else if (cfg.ContainsKey("token")) SaveCfg();   // purani khuli file -> turant band karo
    }
    static void SaveCfg()
    {
        lock (cfgLock)
        {
            Dictionary<string, object> c = new Dictionary<string, object>(cfg);
            object t;
            if (c.TryGetValue("token", out t) && t != null)
            {
                c["tokenEnc"] = Convert.ToBase64String(System.Security.Cryptography.ProtectedData.Protect(Encoding.UTF8.GetBytes(t.ToString()), Ent, System.Security.Cryptography.DataProtectionScope.CurrentUser));
                c.Remove("token");
            }
            File.WriteAllText(CfgFile, J.Serialize(c), Encoding.UTF8);
        }
    }
    static string S(string k) { object v; return cfg.TryGetValue(k, out v) && v != null ? v.ToString() : ""; }
    static List<string> L(string k)
    {
        List<string> o = new List<string>(); object v;
        if (cfg.TryGetValue(k, out v) && v is IEnumerable && !(v is string)) foreach (object x in (IEnumerable)v) if (x != null) o.Add(x.ToString());
        return o;
    }
    static Dictionary<string, object> Map()
    {
        object v;
        if (cfg.TryGetValue("map", out v) && v is Dictionary<string, object>) return (Dictionary<string, object>)v;
        Dictionary<string, object> m = new Dictionary<string, object>(); cfg["map"] = m; return m;
    }

    // ---------------- HTTP ----------------
    static string Http(string method, string url, string body, int timeoutMs, out int code)
    {
        code = 0;
        try
        {
            HttpWebRequest r = (HttpWebRequest)WebRequest.Create(url);
            r.Method = method; r.Timeout = timeoutMs; r.ReadWriteTimeout = timeoutMs;
            // token sirf Host ko - GitHub / Firebase ko kabhi nahi
            if (S("token") != "" && url.IndexOf("githubusercontent.com") < 0 && url.IndexOf("github.com") < 0 && url.IndexOf("firebaseio.com") < 0 && url.IndexOf("firebasedatabase.app") < 0) r.Headers["x-token"] = S("token");
            if (body != null)
            {
                byte[] b = Encoding.UTF8.GetBytes(body);
                r.ContentType = "application/json"; r.ContentLength = b.Length;
                using (Stream s = r.GetRequestStream()) s.Write(b, 0, b.Length);
            }
            using (HttpWebResponse resp = (HttpWebResponse)r.GetResponse()) { code = (int)resp.StatusCode; return Read(resp); }
        }
        catch (WebException e)
        {
            if (e.Response != null) { HttpWebResponse resp = (HttpWebResponse)e.Response; code = (int)resp.StatusCode; return Read(resp); }
            return e.Message;
        }
        catch (Exception e) { return e.Message; }
    }
    static string Read(HttpWebResponse r) { using (StreamReader sr = new StreamReader(r.GetResponseStream(), Encoding.UTF8)) return sr.ReadToEnd(); }
    static Dictionary<string, object> Obj(string s) { try { return J.Deserialize<Dictionary<string, object>>(s); } catch { return null; } }
    static string Err(string s, string def) { Dictionary<string, object> o = Obj(s); return o != null && o.ContainsKey("err") ? o["err"].ToString() : def; }

    // LAN (IP / PC naam) = http + :8790,  internet (trycloudflare / koi domain) = https, bina port
    static string Norm(string a)
    {
        a = a.Trim().TrimEnd('/');
        if (a == "") return "";
        string raw = a;
        if (raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) raw = raw.Substring(7);
        else if (raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) raw = raw.Substring(8);
        int sl = raw.IndexOf('/'); if (sl >= 0) raw = raw.Substring(0, sl);
        string host = raw; int col = host.LastIndexOf(':'); if (col > 0) host = host.Substring(0, col);
        IPAddress ip;
        bool lan = IPAddress.TryParse(host, out ip) || host.ToLowerInvariant() == "localhost" || host.IndexOf('.') < 0 || host.ToLowerInvariant().EndsWith(".local");
        if (!lan) return "https://" + host;
        Uri u;
        if (!Uri.TryCreate("http://" + raw, UriKind.Absolute, out u)) return "";
        if (u.IsDefaultPort) return "http://" + u.Host + ":8790";
        return u.GetLeftPart(UriPartial.Authority);
    }
    static bool Ping(string b, string hostId, int ms)
    {
        int code; string t = Http("GET", b + "/api/ping", null, ms, out code);
        Dictionary<string, object> o = Obj(t);
        return code == 200 && o != null && o.ContainsKey("app") && (hostId == "" || (o.ContainsKey("hostId") && o["hostId"].ToString() == hostId));
    }
    /** Host tak pahunchne ka raasta: pehle LAN, phir internet */
    static string Base(bool fresh)
    {
        if (!fresh && baseUrl != null) return baseUrl;
        List<string> c = new List<string>();
        if (S("manual") != "") c.Add(S("manual"));
        c.AddRange(L("lan"));
        foreach (string b in c) if (Ping(b, S("hostId"), 2500)) return baseUrl = b;
        if (S("url") != "" && Ping(S("url"), S("hostId"), 12000)) return baseUrl = S("url");
        // Host ne naya internet pata liya ho to purane raaste se poochho (LAN mila nahi tha) - Firebase ho to wahan se
        string fb = S("fb");
        if (fb != "")
        {
            int code; Dictionary<string, object> o = Obj(Http("GET", fb + "printHosts/" + S("hostId") + "/pub.json", null, 8000, out code));
            if (o != null)
            {
                if (o.ContainsKey("url") && o["url"] != null) cfg["url"] = o["url"].ToString();
                if (o.ContainsKey("lan")) cfg["lan"] = o["lan"];
                SaveCfg();
                foreach (string b in L("lan")) if (Ping(b, S("hostId"), 2500)) return baseUrl = b;
                if (S("url") != "" && Ping(S("url"), S("hostId"), 12000)) return baseUrl = S("url");
            }
        }
        // Host restart hua -> internet ka pata badla: GitHub se naya pata (MeraCall wala repo)
        if (S("gh") != "")
        {
            int code; Dictionary<string, object> g = Obj(Http("GET", "https://raw.githubusercontent.com/" + S("gh") + "/HEAD/meraprint/" + S("hostId") + ".json?t=" + DateTime.Now.Ticks, null, 10000, out code));
            if (code == 200 && g != null)
            {
                if (g.ContainsKey("url") && g["url"] != null && g["url"].ToString() != "") cfg["url"] = g["url"].ToString();
                if (g.ContainsKey("lan")) cfg["lan"] = g["lan"];
                SaveCfg();
                foreach (string b in L("lan")) if (Ping(b, S("hostId"), 2500)) return baseUrl = b;
                if (S("url") != "" && Ping(S("url"), S("hostId"), 12000)) return baseUrl = S("url");
            }
        }
        return baseUrl = null;
    }
    static void RefreshAddrs()
    {
        if (baseUrl == null) return;
        int code; Dictionary<string, object> me = Obj(Http("GET", baseUrl + "/api/me", null, 15000, out code));
        if (code == 200 && me != null)
        {
            if (me.ContainsKey("lan")) cfg["lan"] = me["lan"];
            if (me.ContainsKey("url") && me["url"] != null && me["url"].ToString() != "") cfg["url"] = me["url"].ToString();
            if (me.ContainsKey("host")) cfg["hostName"] = me["host"].ToString();
            if (me.ContainsKey("gh") && me["gh"] != null && me["gh"].ToString() != "") cfg["gh"] = me["gh"].ToString();
            if (me.ContainsKey("fb") && me["fb"] != null && me["fb"].ToString() != "") cfg["fb"] = me["fb"].ToString();
            if (S("manual").Contains("trycloudflare")) cfg["manual"] = "";
            SaveCfg();
        }
    }

    // ---------------- Pehli baar: Host se jodna ----------------
    static bool SetupForm(string hosts)
    {
        Form f = new Form();
        f.Text = "MeraPrint Client - Host PC se jodein";
        f.FormBorderStyle = FormBorderStyle.FixedDialog; f.MaximizeBox = false; f.StartPosition = FormStartPosition.CenterScreen;
        f.ClientSize = new Size(470, 460); f.Font = new Font("Segoe UI", 10.5f);
        Label l1 = new Label(); l1.Text = "Host ID ya Host PC ka pata (Host ke admin panel me):"; l1.SetBounds(18, 14, 440, 22);
        TextBox addr = new TextBox(); addr.SetBounds(18, 38, 432, 28);
        Label lu = new Label(); lu.Text = "Login ID (Host wale ne diya):"; lu.SetBounds(18, 76, 210, 22);
        TextBox usr = new TextBox(); usr.SetBounds(18, 100, 210, 28);
        Label lp = new Label(); lp.Text = "Password:"; lp.SetBounds(240, 76, 210, 22);
        TextBox pwd = new TextBox(); pwd.SetBounds(240, 100, 210, 28); pwd.UseSystemPasswordChar = true;
        Label l2 = new Label(); l2.Text = "Ya PIN (Login ID na ho to, Host par dikh raha 6 ank):"; l2.SetBounds(18, 140, 440, 22);
        TextBox pin = new TextBox(); pin.SetBounds(18, 164, 160, 28); pin.MaxLength = 6;
        Label l3 = new Label(); l3.Text = "Is PC ka naam (Host par yahi dikhega):"; l3.SetBounds(18, 204, 440, 22);
        TextBox nm = new TextBox(); nm.SetBounds(18, 228, 432, 28); nm.Text = Environment.MachineName + " (Printer)"; nm.MaxLength = 40;
        Label msg = new Label(); msg.SetBounds(18, 268, 432, 120); msg.ForeColor = Color.FromArgb(185, 28, 28);
        Button ok = new Button(); ok.Text = "Jodo"; ok.SetBounds(18, 400, 432, 42); ok.BackColor = Color.FromArgb(10, 124, 255); ok.ForeColor = Color.White; ok.FlatStyle = FlatStyle.Flat;
        f.Controls.AddRange(new Control[] { l1, addr, lu, usr, lp, pwd, l2, pin, l3, nm, msg, ok });
        if (S("user") != "") usr.Text = S("user");
        f.AcceptButton = ok;
        // Setup file me Host ke pate pehle se ho sakte hain:  ID|http://192.168.1.5:8790,https://x.trycloudflare.com|FirebaseURL
        string preId = "", preFb = "", preGh = ""; List<string> pre = new List<string>();
        if (hosts != "")
        {
            string[] parts = hosts.Split('|');
            preId = parts[0];
            if (parts.Length > 1) foreach (string u in parts[1].Split(',')) if (u.Trim() != "") pre.Add(u.Trim());
            if (parts.Length > 2) preFb = parts[2];
            if (parts.Length > 3) preGh = parts[3];
            addr.Text = pre.Count > 0 ? pre[0] : "";
        }
        bool done = false;
        ok.Click += delegate
        {
            ok.Enabled = false; msg.ForeColor = Color.Gray; msg.Text = "Jud rahe hain..."; Application.DoEvents();
            try
            {
                List<string> tries = new List<string>();
                string idTxt = addr.Text.Trim().ToUpperInvariant();
                if (System.Text.RegularExpressions.Regex.IsMatch(idTxt, "^[A-Z0-9]{4}-?[A-Z0-9]{4}-?[A-Z0-9]{4}$"))
                {
                    // Host ID se dhoondho (GitHub / Firebase par Host ka abhi ka pata)
                    idTxt = idTxt.Replace("-", ""); idTxt = idTxt.Substring(0, 4) + "-" + idTxt.Substring(4, 4) + "-" + idTxt.Substring(8, 4);
                    cfg["hostId"] = idTxt; cfg["gh"] = preGh != "" ? preGh : "CGPANKAJ22/MeraCall"; if (preFb != "") cfg["fb"] = preFb;
                    cfg["lan"] = new ArrayList(); cfg["url"] = ""; cfg["manual"] = "";
                    msg.Text = "Host ID se dhoondh rahe hain..."; Application.DoEvents();
                    string rb = Base(true);
                    if (rb != null) tries.Add(rb);
                }
                else
                {
                    string typed = Norm(addr.Text);
                    if (typed != "") tries.Add(typed);
                }
                foreach (string p in pre) if (!tries.Contains(p)) tries.Add(p);
                string b = null;
                foreach (string t in tries) { if (Ping(t, "", 4000)) { b = t; break; } }
                if (b == null) throw new Exception("Host nahi mila. Host par START-HOST.bat chal raha hai? Pata sahi hai?");
                bool byLogin = usr.Text.Trim() != "";
                if (byLogin && pwd.Text == "") throw new Exception("Password daaliye");
                if (!byLogin && pin.Text.Trim().Length != 6) throw new Exception("Login ID + Password daaliye (ya PIN 6 ank ka)");
                Dictionary<string, object> body = new Dictionary<string, object>();
                body["name"] = nm.Text.Trim() == "" ? Environment.MachineName : nm.Text.Trim();
                if (byLogin) { body["user"] = usr.Text.Trim(); body["pass"] = pwd.Text; } else body["pin"] = pin.Text.Trim();
                cfg.Remove("token");
                int code; string resp = Http("POST", b + (byLogin ? "/api/login" : "/api/pair"), J.Serialize(body), 20000, out code);
                if (code != 200) throw new Exception(Err(resp, "Nahi jud paya (" + code + ")"));
                Dictionary<string, object> j = Obj(resp);
                cfg = new Dictionary<string, object>();
                cfg["token"] = j["token"]; cfg["hostId"] = j["hostId"]; cfg["hostName"] = j["name"];
                cfg["lan"] = j.ContainsKey("lan") ? j["lan"] : new ArrayList();
                cfg["url"] = j.ContainsKey("url") && j["url"] != null ? j["url"].ToString() : "";
                cfg["manual"] = b.Contains("trycloudflare") ? "" : b;
                cfg["fb"] = preFb;
                cfg["gh"] = j.ContainsKey("gh") && j["gh"] != null && j["gh"].ToString() != "" ? j["gh"].ToString() : preGh;
                cfg["color"] = "color"; cfg["duplex"] = "none";
                if (j.ContainsKey("user") && j["user"] != null) cfg["user"] = j["user"].ToString();
                SaveCfg(); baseUrl = b; done = true; f.Close();
            }
            catch (Exception e) { msg.ForeColor = Color.FromArgb(185, 28, 28); msg.Text = e.Message; ok.Enabled = true; }
        };
        f.ShowDialog();
        return done;
    }

    // ---------------- Windows me printers banana ----------------
    static string Clean(string s) { StringBuilder b = new StringBuilder(); foreach (char c in s) if (c != '\\' && c != ',' && c != '!' && c != '\'' && c >= ' ') b.Append(c); return b.ToString().Trim(); }
    static void MakePrinters(bool quietOk)
    {
        if (Base(true) == null) { Note("Host nahi mila - printers baad me banenge (icon par right-click > Printers dobara banao)", true); return; }
        RefreshAddrs();
        int code; string t = Http("GET", baseUrl + "/api/printers", null, 30000, out code);
        if (code != 200) { MessageBox.Show(Err(t, "Host se printers ki list nahi mili (" + code + ")"), "MeraPrint"); return; }
        ArrayList list; try { list = J.Deserialize<ArrayList>(t); } catch { list = new ArrayList(); }
        if (list.Count == 0) { MessageBox.Show("Host PC par koi printer nahi mila.", "MeraPrint"); return; }
        string host = Clean(S("hostName")); if (host.Length > 30) host = host.Substring(0, 30);
        StringBuilder ps = new StringBuilder();
        ps.AppendLine("$ErrorActionPreference = 'Continue'");
        ps.AppendLine("$root = '" + Root + "'; $spool = '" + Spool + "'");
        ps.AppendLine("New-Item -ItemType Directory -Force $spool | Out-Null");
        ps.AppendLine("Start-Transcript -Path (Join-Path $root 'make-printers.log') -Force | Out-Null");
        ps.AppendLine("Write-Output ('Windows: ' + [Environment]::OSVersion.VersionString)");
        ps.AppendLine("Remove-Item (Join-Path $root 'made.txt') -Force -ErrorAction SilentlyContinue");
        ps.AppendLine("$regPorts = 'HKLM:\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Ports'");
        ps.AppendLine("function MakePort($p) { try { Add-PrinterPort -Name $p -ErrorAction Stop; Write-Output ('Port bana: ' + $p) } catch { Write-Output ('Add-PrinterPort fail: ' + $_.Exception.Message + ' -> registry se') ; New-ItemProperty -Path $regPorts -Name $p -Value '' -PropertyType String -Force | Out-Null; $script:restart = $true } }");
        ps.AppendLine("function MakePrinter($n, $p) { try { Add-Printer -Name $n -DriverName 'Microsoft Print To PDF' -PortName $p -ErrorAction Stop; Write-Output ('Printer bana: ' + $n) } catch { Write-Output ('Add-Printer fail: ' + $n + ' : ' + $_.Exception.Message) } }");
        ps.AppendLine("icacls $root /grant '*S-1-5-32-545:(OI)(CI)M' /T /Q | Out-Null");
        ps.AppendLine("if (-not (Get-PrinterDriver -Name 'Microsoft Print To PDF' -ErrorAction SilentlyContinue)) { Write-Output 'PDF driver nahi tha - chalu kar rahe hain'; Enable-WindowsOptionalFeature -Online -FeatureName Printing-PrintToPDFServices-Features -NoRestart -ErrorAction SilentlyContinue | Out-Null; Start-Sleep 3 }");
        ps.AppendLine("Write-Output ('PDF driver: ' + [bool](Get-PrinterDriver -Name 'Microsoft Print To PDF' -ErrorAction SilentlyContinue))");
        ps.AppendLine("Get-Printer | Where-Object { $_.Name -like '*(MeraPrint - *' } | ForEach-Object { Remove-Printer -Name $_.Name -ErrorAction SilentlyContinue }");
        ps.AppendLine("Get-PrinterPort | Where-Object { $_.Name -like ($spool + '\\*') } | ForEach-Object { Remove-PrinterPort -Name $_.Name -ErrorAction SilentlyContinue }");
        Dictionary<string, object> map = new Dictionary<string, object>();
        StringBuilder mk = new StringBuilder();
        int n = 0;
        foreach (object o in list)
        {
            Dictionary<string, object> p = o as Dictionary<string, object>;
            if (p == null || !p.ContainsKey("name")) continue;
            string real = p["name"].ToString();
            n++;
            string port = Path.Combine(Spool, "p" + n + ".pdf");
            string local = Clean(real); if (local.Length > 120) local = local.Substring(0, 120);
            local = local + " (MeraPrint - " + host + ")";
            map["p" + n + ".pdf"] = real;
            ps.AppendLine("MakePort '" + port.Replace("'", "''") + "'");
            mk.AppendLine("MakePrinter '" + local.Replace("'", "''") + "' '" + port.Replace("'", "''") + "'");
        }
        ps.AppendLine("if ($script:restart) { Write-Output 'Print Spooler restart'; Restart-Service Spooler -Force; Start-Sleep 4 }");
        ps.Append(mk.ToString());
        ps.AppendLine("Get-Printer | Where-Object { $_.Name -like '*(MeraPrint - *' } | Select-Object -ExpandProperty Name | Out-File -Encoding utf8 (Join-Path $root 'made.txt')");
        ps.AppendLine("Stop-Transcript | Out-Null");
        string script = Path.Combine(Dir, "make-printers.ps1");
        File.WriteAllText(script, ps.ToString(), new UTF8Encoding(true));
        MessageBox.Show("Ab Windows ijazat (Haan / Yes) maangega - is PC me " + n + " MeraPrint printer banane ke liye.", "MeraPrint");
        try
        {
            ProcessStartInfo si = new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + script + "\"");
            si.Verb = "runas"; si.UseShellExecute = true; si.WindowStyle = ProcessWindowStyle.Hidden;
            Process pr = Process.Start(si); pr.WaitForExit(180000);
        }
        catch (Exception e) { MessageBox.Show("Printers nahi bane: " + e.Message + "\n(Windows ijazat me Yes dabana zaroori hai)", "MeraPrint"); return; }
        string made = ""; try { made = File.ReadAllText(Path.Combine(Root, "made.txt")).Trim(); } catch { }
        cfg["map"] = map; SaveCfg();
        if (made == "")
        {
            string lg = ""; try { lg = File.ReadAllText(Path.Combine(Root, "make-printers.log")); } catch { lg = "(log nahi bana - Windows ijazat me Yes dabaya tha?)"; }
            try { File.WriteAllText(Path.Combine(Dir, "printer-log.txt"), lg); Process.Start("notepad.exe", "\"" + Path.Combine(Dir, "printer-log.txt") + "\""); } catch { }
            MessageBox.Show("Printers nahi bane. Notepad me log khula hai - uska photo bhejiye.", "MeraPrint");
        }
        else MessageBox.Show("Ho gaya! Ab is PC ke har software me Print dabakar ye printer chuniye:\n\n" + made + "\n\nPrint seedha Host PC (" + S("hostName") + ") par jayega.", "MeraPrint");
    }

    // ---------------- Print pakadna aur bhejna ----------------
    static void WatchLoop()
    {
        while (true)
        {
            try
            {
                if (Directory.Exists(Spool))
                    foreach (string f in Directory.GetFiles(Spool, "p*.pdf"))
                    {
                        FileInfo fi = new FileInfo(f);
                        if (fi.Length == 0 || (DateTime.Now - fi.LastWriteTime).TotalMilliseconds < 1200) continue;
                        try { using (FileStream x = new FileStream(f, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } } catch { continue; }   // abhi likh raha hai
                        string key = Path.GetFileName(f).ToLowerInvariant();
                        object printer; Map().TryGetValue(key, out printer);
                        if (printer == null) { try { File.Delete(f); } catch { } continue; }
                        string job = Path.Combine(Outbox, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "__" + key);
                        File.Move(f, job);
                        ThreadPool.QueueUserWorkItem(delegate { Send(job, true); });
                    }
            }
            catch { }
            Thread.Sleep(600);
        }
    }
    static readonly object sendLock = new object();
    static void Send(string job, bool firstTry)
    {
        lock (sendLock)
        {
            if (!File.Exists(job)) return;
            string key = Path.GetFileName(job); key = key.Substring(key.IndexOf("__") + 2);
            object printer; Map().TryGetValue(key, out printer);
            if (printer == null) { try { File.Delete(job); } catch { } return; }
            if (Base(baseUrl == null) == null)
            {
                if (firstTry) Note("Host PC nahi mil raha - print rakh liya hai, Host milte hi chala jayega.", true);
                return;
            }
            Dictionary<string, object> opts = new Dictionary<string, object>();
            opts["printer"] = printer.ToString(); opts["copies"] = 1;
            opts["color"] = S("color") == "bw" ? "bw" : "color"; opts["duplex"] = S("duplex") == "" ? "none" : S("duplex");
            string name = "PC-Print " + Environment.MachineName + " " + DateTime.Now.ToString("HH-mm-ss") + ".pdf";
            int code = 0; string resp = "";
            try
            {
                HttpWebRequest r = (HttpWebRequest)WebRequest.Create(baseUrl + "/api/print");
                r.Method = "PUT"; r.Timeout = 60000; r.ReadWriteTimeout = 300000; r.AllowWriteStreamBuffering = false;
                r.Headers["x-token"] = S("token");
                r.Headers["x-name"] = Uri.EscapeDataString(name);
                r.Headers["x-opts"] = Uri.EscapeDataString(J.Serialize(opts));
                FileInfo fi = new FileInfo(job);
                r.ContentLength = fi.Length; r.ContentType = "application/pdf";
                using (Stream s = r.GetRequestStream()) using (FileStream fs = File.OpenRead(job)) fs.CopyTo(s);
                using (HttpWebResponse rr = (HttpWebResponse)r.GetResponse()) { code = (int)rr.StatusCode; resp = Read(rr); }
            }
            catch (WebException e)
            {
                if (e.Response != null) { code = (int)((HttpWebResponse)e.Response).StatusCode; resp = Read((HttpWebResponse)e.Response); }
                else { baseUrl = null; if (firstTry) Note("Host tak nahi pahuncha - dobara koshish karenge.", true); return; }
            }
            if (code != 200)
            {
                try { File.Delete(job); } catch { }
                Note("Print nahi gaya: " + Err(resp, "Host ne mana kiya (" + code + ")"), true);
                if (code == 401) { cfg.Remove("token"); SaveCfg(); Note("Host ne is PC ko hata diya hai. Icon > 'Doosre Host se jodo'.", true); }
                return;
            }
            try { File.Delete(job); } catch { }
            Dictionary<string, object> j = Obj(resp);
            string id = j != null && j.ContainsKey("job") ? j["job"].ToString() : "";
            Note("Print Host PC par bheja: " + printer, false);
            if (id != "") ThreadPool.QueueUserWorkItem(delegate { Watch(id); });
        }
    }
    static void Watch(string id)
    {
        bool askShown = false;
        for (int i = 0; i < 200; i++)
        {
            Thread.Sleep(2000);
            if (baseUrl == null) continue;
            int code; Dictionary<string, object> j = Obj(Http("GET", baseUrl + "/api/job/" + id, null, 15000, out code));
            if (j == null || !j.ContainsKey("state")) continue;
            string st = j["state"].ToString(), msg = j.ContainsKey("msg") && j["msg"] != null ? j["msg"].ToString() : "";
            if (st == "approval" && !askShown) { askShown = true; Note("Host PC par 'Allow' ka intezaar...", false); }
            if (st == "done" || st == "error" || st == "denied") { Note(Plain(msg), st != "done"); return; }
        }
    }
    static void RetryLoop()
    {
        while (true)
        {
            Thread.Sleep(20000);
            try { foreach (string f in Directory.GetFiles(Outbox)) Send(f, false); } catch { }
        }
    }
    static string Plain(string s) { StringBuilder b = new StringBuilder(); foreach (char c in s) if (c < 0x2000 || (c >= 0x900 && c < 0x980)) b.Append(c); return b.ToString().Trim(); }

    // ---------------- Apne aap update: Host ke paas naya client ho to chupchaap naya bana lo ----------------
    static string MySourceVer()
    {
        try
        {
            string t = File.ReadAllText(Path.Combine(Dir, "MeraPrintClient.cs"), Encoding.UTF8).Replace("\r\n", "\n");
            using (System.Security.Cryptography.SHA256 h = System.Security.Cryptography.SHA256.Create())
            {
                byte[] d = h.ComputeHash(Encoding.UTF8.GetBytes(t)); StringBuilder b = new StringBuilder();
                foreach (byte x in d) b.Append(x.ToString("x2"));
                return b.ToString();
            }
        }
        catch { return ""; }
    }
    static void UpdateLoop()
    {
        Thread.Sleep(120000);
        while (true)
        {
            try
            {
                if (Base(baseUrl == null) != null)
                {
                    RefreshAddrs();
                    int code; Dictionary<string, object> o = Obj(Http("GET", baseUrl + "/api/client", null, 20000, out code));
                    string ver = o != null && o.ContainsKey("ver") && o["ver"] != null ? o["ver"].ToString() : "";
                    string mine = MySourceVer();
                    if (code == 200 && ver != "" && mine != "" && ver != mine)
                    {
                        string bat = Path.Combine(Path.GetTempPath(), "MeraPrint-Client-Update.bat");
                        using (WebClient wc = new WebClient()) wc.DownloadFile(baseUrl + "/MeraPrint-Client-Setup.bat", bat);
                        Note("Naya MeraPrint Client mila - update ho raha hai...", false);
                        ProcessStartInfo si = new ProcessStartInfo("cmd.exe", "/c \"\"" + bat + "\" /quiet\"");
                        si.CreateNoWindow = true; si.UseShellExecute = false; si.WindowStyle = ProcessWindowStyle.Hidden;
                        Process.Start(si);
                        return;   // setup purane ko band karke naya chalu karega
                    }
                }
            }
            catch { }
            Thread.Sleep(6 * 3600 * 1000);
        }
    }

    // ---------------- NAPS2 scanner (Escl) ko Host se jodna ----------------
    static void StartScanner()
    {
        Escl.Note = Note;
        Escl.HostName = delegate { return S("hostName"); };
        Escl.ScannerId = delegate { return S("scanner"); };
        Escl.Api = delegate (string method, string path, string body)
        {
            if (Base(baseUrl == null) == null) return "{\"err\":\"Host PC nahi mila\"}";
            int code; string t = Http(method, baseUrl + path, body, 30000, out code);
            if (code == 0) { baseUrl = null; return "{\"err\":\"Host tak nahi pahunche\"}"; }
            return t;
        };
        Escl.ApiBytes = delegate (string path)
        {
            try
            {
                HttpWebRequest r = (HttpWebRequest)WebRequest.Create(baseUrl + path);
                r.Timeout = 120000; r.ReadWriteTimeout = 120000; r.Headers["x-token"] = S("token");
                using (HttpWebResponse resp = (HttpWebResponse)r.GetResponse()) using (MemoryStream ms = new MemoryStream()) { resp.GetResponseStream().CopyTo(ms); return ms.ToArray(); }
            }
            catch { return null; }
        };
        Escl.Start();
    }
    static void PickScanner()
    {
        if (Base(true) == null) { Note("Host nahi mila", true); return; }
        int code; string t = Http("GET", baseUrl + "/api/scanners", null, 60000, out code);
        if (code != 200) { MessageBox.Show(Err(t, "Scanner list nahi mili (" + code + ")"), "MeraPrint"); return; }
        ArrayList list; try { list = J.Deserialize<ArrayList>(t); } catch { list = new ArrayList(); }
        if (list.Count == 0) { MessageBox.Show("Host PC par koi scanner nahi mila.", "MeraPrint"); return; }
        Form f = new Form(); f.Text = "NAPS2 ke liye Host ka scanner"; f.ClientSize = new Size(420, 120); f.StartPosition = FormStartPosition.CenterScreen; f.FormBorderStyle = FormBorderStyle.FixedDialog; f.Font = new Font("Segoe UI", 10.5f);
        ComboBox cb = new ComboBox(); cb.DropDownStyle = ComboBoxStyle.DropDownList; cb.SetBounds(15, 15, 390, 30);
        List<string> ids = new List<string>();
        foreach (object o in list) { Dictionary<string, object> d = o as Dictionary<string, object>; if (d == null) continue; ids.Add(d["id"].ToString()); cb.Items.Add(d["name"].ToString()); if (d["id"].ToString() == S("scanner")) cb.SelectedIndex = cb.Items.Count - 1; }
        if (cb.SelectedIndex < 0) cb.SelectedIndex = 0;
        Button ok = new Button(); ok.Text = "Theek hai"; ok.SetBounds(15, 60, 390, 40);
        ok.Click += delegate { cfg["scanner"] = ids[cb.SelectedIndex]; SaveCfg(); f.Close(); };
        f.Controls.Add(cb); f.Controls.Add(ok); f.ShowDialog();
    }

    // ---------------- Tray icon ----------------
    static void Note(string text, bool warn)
    {
        if (ui == null || tray == null) return;
        try { ui.BeginInvoke((MethodInvoker)delegate { tray.ShowBalloonTip(4000, "MeraPrint", text, warn ? ToolTipIcon.Warning : ToolTipIcon.Info); }); } catch { }
    }
    static Icon MakeIcon()
    {
        Bitmap b = new Bitmap(32, 32);
        using (Graphics g = Graphics.FromImage(b))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.FillEllipse(new SolidBrush(Color.FromArgb(10, 124, 255)), 0, 0, 31, 31);
            g.FillRectangle(Brushes.White, 10, 6, 12, 6);
            g.FillRectangle(Brushes.White, 6, 13, 20, 10);
            g.FillRectangle(Brushes.White, 10, 20, 12, 7);
        }
        return Icon.FromHandle(b.GetHicon());
    }
    static void MakeTray()
    {
        tray = new NotifyIcon();
        tray.Icon = MakeIcon();
        tray.Text = "MeraPrint Client - " + (S("hostName").Length > 40 ? S("hostName").Substring(0, 40) : S("hostName"));
        ContextMenuStrip m = new ContextMenuStrip();
        m.Items.Add("Host: " + S("hostName")).Enabled = false;
        ToolStripMenuItem bw = new ToolStripMenuItem("Black & White (rang nahi)"); bw.Checked = S("color") == "bw";
        bw.Click += delegate { bw.Checked = !bw.Checked; cfg["color"] = bw.Checked ? "bw" : "color"; SaveCfg(); };
        ToolStripMenuItem dx = new ToolStripMenuItem("Kagaz ke dono taraf"); dx.Checked = S("duplex") == "long";
        dx.Click += delegate { dx.Checked = !dx.Checked; cfg["duplex"] = dx.Checked ? "long" : "none"; SaveCfg(); };
        m.Items.Add("Files: Host se lo / Host ko bhejo", null, delegate { Files.Show(); });
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(bw); m.Items.Add(dx);
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("Printers dobara banao (Host ke naye printer)", null, delegate { MakePrinters(false); });
        m.Items.Add("NAPS2 ke liye scanner chuno", null, delegate { PickScanner(); });
        m.Items.Add("NAPS2 me scanner kaise jode?", null, delegate { MessageBox.Show("NAPS2 me:\n1. Profile banao / Edit karo\n2. Driver: ESCL\n3. 'MeraPrint Scanner (" + S("hostName") + ")' chuno\n\nNa dikhe to Manual IP:  127.0.0.1   Port: " + Escl.Port + "\n\nScan dabate hi Host PC ke scanner par scan hoga (Host par Scan ki ijazat chahiye).", "MeraPrint - NAPS2"); });
        m.Items.Add("Printer log dikhao", null, delegate { try { Process.Start("notepad.exe", "\"" + Path.Combine(Root, "make-printers.log") + "\""); } catch { } });
        m.Items.Add("Connection jaancho", null, delegate { string b = Base(true); Note(b == null ? "Host nahi mila" : "Host juda hai: " + b, b == null); });
        m.Items.Add("Doosre Host se jodo", null, delegate { if (SetupForm("")) { MakePrinters(true); tray.Text = "MeraPrint Client - " + S("hostName"); } });
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("Band karo", null, delegate { tray.Visible = false; Application.Exit(); Environment.Exit(0); });
        tray.ContextMenuStrip = m;
        tray.DoubleClick += delegate { Files.Show(); };
        tray.Visible = true;
        Note("Chalu hai. Kisi bhi software me '(MeraPrint - " + S("hostName") + ")' printer chuniye.", false);
    }
    static void AutoStart()
    {
        try
        {
            // Right-click > Send to > "MeraPrint Host (...)"
            string sendto = Environment.GetFolderPath(Environment.SpecialFolder.SendTo);
            foreach (string old in Directory.GetFiles(sendto, "MeraPrint Host*.lnk")) try { File.Delete(old); } catch { }
            string lnk = Path.Combine(sendto, "MeraPrint Host (" + Clean(S("hostName")).Replace(":", "") + ").lnk");
            string psc = "$s=(New-Object -ComObject WScript.Shell).CreateShortcut('" + lnk.Replace("'", "''") + "'); $s.TargetPath='" + Application.ExecutablePath.Replace("'", "''") + "'; $s.Arguments='/send'; $s.IconLocation='shell32.dll,146'; $s.Save()";
            ProcessStartInfo si = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -WindowStyle Hidden -Command \"" + psc.Replace("\"", "\\\"") + "\"");
            si.CreateNoWindow = true; si.UseShellExecute = false; si.WindowStyle = ProcessWindowStyle.Hidden;
            Process.Start(si);
        }
        catch { }
        try
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                if (k != null) k.SetValue("MeraPrintClient", "\"" + Application.ExecutablePath + "\"");
        }
        catch { }
    }
}

// ===================================================================================
// NAPS2 / ESCL: Host PC ka SCANNER is PC par "network scanner" ki tarah dikhta hai.
// NAPS2 -> Profile -> Driver: ESCL -> "MeraPrint Scanner (Host)"  (na dikhe to Manual IP: 127.0.0.1, port 9801)
// Sirf isi PC se chalta hai (127.0.0.1) - LAN ka koi aur is raaste Host ka scanner nahi chala sakta.
// ===================================================================================
static class Escl
{
    public const int Port = 9801;
    static readonly string Uuid = "4d505363-616e-0000-0000-" + Math.Abs(Environment.MachineName.GetHashCode()).ToString("x").PadLeft(12, '0').Substring(0, 12);
    class Job { public string id, hostJob, state = "Processing", err; public byte[] data; public bool served; public DateTime ts = DateTime.Now; }
    static readonly Dictionary<string, Job> jobs = new Dictionary<string, Job>();
    public static Func<string, string, string, string> Api;       // (method, path, body) -> text  (MP deta hai)
    public static Func<string, byte[]> ApiBytes;
    public static Func<string> HostName;
    public static Func<string> ScannerId;
    public static Action<string, bool> Note;

    public static void Start()
    {
        Thread t = new Thread(Serve); t.IsBackground = true; t.Start();
        Thread m = new Thread(Mdns); m.IsBackground = true; m.Start();
    }

    // ---------- chhota HTTP server (sirf 127.0.0.1 - admin / firewall ki zaroorat nahi) ----------
    static void Serve()
    {
        System.Net.Sockets.TcpListener l = null;
        for (int i = 0; i < 30 && l == null; i++)
        {
            try { l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, Port); l.Start(); }
            catch { l = null; Thread.Sleep(2000); }
        }
        if (l == null) { Note("Scanner (NAPS2) ka port " + Port + " khali nahi hai", true); return; }
        while (true)
        {
            try
            {
                System.Net.Sockets.TcpClient c = l.AcceptTcpClient();
                ThreadPool.QueueUserWorkItem(delegate { try { Handle(c); } catch { } finally { try { c.Close(); } catch { } } });
            }
            catch { Thread.Sleep(200); }
        }
    }
    static string ReadLine(Stream s)
    {
        StringBuilder b = new StringBuilder(); int ch;
        while ((ch = s.ReadByte()) >= 0) { if (ch == '\n') break; if (ch != '\r') b.Append((char)ch); if (b.Length > 8192) break; }
        return ch < 0 && b.Length == 0 ? null : b.ToString();
    }
    static void Handle(System.Net.Sockets.TcpClient c)
    {
        c.ReceiveTimeout = 30000; c.SendTimeout = 60000;
        Stream s = c.GetStream();
        while (true)
        {
            string req = ReadLine(s);
            if (req == null || req == "") return;
            string[] rp = req.Split(' ');
            if (rp.Length < 2) return;
            string method = rp[0], path = rp[1];
            int q = path.IndexOf('?'); if (q >= 0) path = path.Substring(0, q);
            int len = 0; bool keep = true; string hostHdr = "127.0.0.1:" + Port;
            string h;
            while ((h = ReadLine(s)) != null && h != "")
            {
                int k = h.IndexOf(':'); if (k < 0) continue;
                string n = h.Substring(0, k).Trim().ToLowerInvariant(), v = h.Substring(k + 1).Trim();
                if (n == "content-length") int.TryParse(v, out len);
                if (n == "connection" && v.ToLowerInvariant() == "close") keep = false;
                if (n == "host") hostHdr = v;
            }
            byte[] body = new byte[Math.Max(0, Math.Min(len, 1 << 20))];
            int got = 0; while (got < body.Length) { int r = s.Read(body, got, body.Length - got); if (r <= 0) break; got += r; }
            Route(s, method, path, Encoding.UTF8.GetString(body, 0, got), hostHdr, keep);
            if (!keep) return;
        }
    }
    static void Reply(Stream s, int code, string type, byte[] data, string extra, bool keep)
    {
        string st = code == 200 ? "OK" : code == 201 ? "Created" : code == 404 ? "Not Found" : code == 503 ? "Service Unavailable" : code == 409 ? "Conflict" : "Error";
        string head = "HTTP/1.1 " + code + " " + st + "\r\n" + (type != null ? "Content-Type: " + type + "\r\n" : "") +
            "Content-Length: " + (data == null ? 0 : data.Length) + "\r\n" + (extra ?? "") + "Connection: " + (keep ? "keep-alive" : "close") + "\r\n\r\n";
        byte[] hb = Encoding.ASCII.GetBytes(head);
        s.Write(hb, 0, hb.Length);
        if (data != null && data.Length > 0) s.Write(data, 0, data.Length);
        s.Flush();
    }
    static void Xml(Stream s, string xml, bool keep) { Reply(s, 200, "text/xml; charset=utf-8", Encoding.UTF8.GetBytes(xml), null, keep); }

    const string NS = "xmlns:scan=\"http://schemas.hp.com/imaging/escl/2011/05/03\" xmlns:pwg=\"http://www.pwg.org/schemas/2010/12/sm\"";
    static string Esc(string x) { return System.Security.SecurityElement.Escape(x ?? ""); }

    static void Route(Stream s, string method, string path, string body, string hostHdr, bool keep)
    {
        if (path.StartsWith("/eSCL", StringComparison.OrdinalIgnoreCase)) path = path.Substring(5);
        if (method == "GET" && path == "/ScannerCapabilities") { Xml(s, Caps(), keep); return; }
        if (method == "GET" && path == "/ScannerStatus") { Xml(s, Status(), keep); return; }
        if (method == "POST" && path == "/ScanJobs")
        {
            Job j = NewJob(body);
            if (j == null) { Reply(s, 503, null, null, "Retry-After: 5\r\n", keep); return; }
            Reply(s, 201, null, null, "Location: http://" + hostHdr + "/eSCL/ScanJobs/" + j.id + "\r\n", keep);
            return;
        }
        if (path.StartsWith("/ScanJobs/"))
        {
            string[] p = path.Substring(10).Split('/');
            Job j; lock (jobs) jobs.TryGetValue(p[0], out j);
            if (j == null) { Reply(s, 404, null, null, null, keep); return; }
            if (method == "DELETE") { lock (jobs) jobs.Remove(j.id); Reply(s, 200, null, null, null, keep); return; }
            if (method == "GET" && p.Length > 1 && p[1] == "NextDocument")
            {
                // Host par scan khatam hone ka intezaar (20 sec tak), warna "abhi nahi" (503) - NAPS2 dobara poochhta hai
                for (int i = 0; i < 40 && j.state == "Processing"; i++) Thread.Sleep(500);
                if (j.state == "Processing") { Reply(s, 503, null, null, "Retry-After: 2\r\n", keep); return; }
                if (j.data != null && !j.served) { j.served = true; j.state = "Completed"; Reply(s, 200, "image/jpeg", j.data, null, keep); return; }
                Reply(s, 404, null, null, null, keep); return;
            }
            if (method == "GET" && p.Length > 1 && p[1] == "ScanImageInfo")
            {
                Xml(s, "<?xml version=\"1.0\" encoding=\"UTF-8\"?><scan:ScanImageInfo " + NS + "><pwg:JobUri>/eSCL/ScanJobs/" + j.id + "</pwg:JobUri><pwg:JobUuid>" + j.id + "</pwg:JobUuid></scan:ScanImageInfo>", keep);
                return;
            }
        }
        Reply(s, 404, null, null, null, keep);
    }

    static string Caps()
    {
        string res = "";
        foreach (int r in new int[] { 100, 150, 200, 300, 600 }) res += "<scan:DiscreteResolution><scan:XResolution>" + r + "</scan:XResolution><scan:YResolution>" + r + "</scan:YResolution></scan:DiscreteResolution>";
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?><scan:ScannerCapabilities " + NS + ">" +
            "<pwg:Version>2.63</pwg:Version><pwg:MakeAndModel>MeraPrint Scanner (" + Esc(HostName()) + ")</pwg:MakeAndModel><pwg:SerialNumber>MP1</pwg:SerialNumber>" +
            "<scan:UUID>" + Uuid + "</scan:UUID>" +
            "<scan:Platen><scan:PlatenInputCaps>" +
            "<scan:MinWidth>16</scan:MinWidth><scan:MaxWidth>2550</scan:MaxWidth><scan:MinHeight>16</scan:MinHeight><scan:MaxHeight>3508</scan:MaxHeight>" +
            "<scan:MaxScanRegions>1</scan:MaxScanRegions>" +
            "<scan:SettingProfiles><scan:SettingProfile>" +
            "<scan:ColorModes><scan:ColorMode>RGB24</scan:ColorMode><scan:ColorMode>Grayscale8</scan:ColorMode><scan:ColorMode>BlackAndWhite1</scan:ColorMode></scan:ColorModes>" +
            "<scan:DocumentFormats><pwg:DocumentFormat>image/jpeg</pwg:DocumentFormat><scan:DocumentFormatExt>image/jpeg</scan:DocumentFormatExt></scan:DocumentFormats>" +
            "<scan:SupportedResolutions><scan:DiscreteResolutions>" + res + "</scan:DiscreteResolutions></scan:SupportedResolutions>" +
            "<scan:ColorSpaces><scan:ColorSpace>sRGB</scan:ColorSpace></scan:ColorSpaces>" +
            "</scan:SettingProfile></scan:SettingProfiles>" +
            "<scan:SupportedIntents><scan:Intent>Document</scan:Intent><scan:Intent>Photo</scan:Intent><scan:Intent>TextAndGraphic</scan:Intent></scan:SupportedIntents>" +
            "<scan:MaxOpticalXResolution>600</scan:MaxOpticalXResolution><scan:MaxOpticalYResolution>600</scan:MaxOpticalYResolution>" +
            "</scan:PlatenInputCaps></scan:Platen></scan:ScannerCapabilities>";
    }
    static string Status()
    {
        StringBuilder b = new StringBuilder();
        lock (jobs) foreach (Job j in jobs.Values)
                b.Append("<scan:JobInfo><pwg:JobUri>/eSCL/ScanJobs/" + j.id + "</pwg:JobUri><pwg:JobUuid>" + j.id + "</pwg:JobUuid><scan:Age>" + (int)(DateTime.Now - j.ts).TotalSeconds +
                    "</scan:Age><pwg:ImagesCompleted>" + (j.served ? 1 : 0) + "</pwg:ImagesCompleted><pwg:ImagesToTransfer>" + (j.data != null && !j.served ? 1 : 0) +
                    "</pwg:ImagesToTransfer><pwg:JobState>" + (j.state == "Aborted" ? "Aborted" : j.state == "Processing" ? "Processing" : "Completed") + "</pwg:JobState>" +
                    (j.state == "Aborted" ? "<pwg:JobStateReasons><pwg:JobStateReason>JobCanceledBySystem</pwg:JobStateReason></pwg:JobStateReasons>" : "") + "</scan:JobInfo>");
        bool busy = false; lock (jobs) foreach (Job j in jobs.Values) if (j.state == "Processing") busy = true;
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?><scan:ScannerStatus " + NS + "><pwg:Version>2.63</pwg:Version><pwg:State>" + (busy ? "Processing" : "Idle") +
            "</pwg:State><scan:Jobs>" + b + "</scan:Jobs></scan:ScannerStatus>";
    }
    static string Tag(string xml, string name)
    {
        int a = xml.IndexOf(":" + name + ">"); if (a < 0) a = xml.IndexOf("<" + name + ">"); if (a < 0) return "";
        a = xml.IndexOf('>', a) + 1; int e = xml.IndexOf('<', a);
        return e > a ? xml.Substring(a, e - a).Trim() : "";
    }
    static Job NewJob(string settings)
    {
        lock (jobs)
        {
            List<string> old = new List<string>();
            foreach (Job x in jobs.Values) if ((DateTime.Now - x.ts).TotalMinutes > 30) old.Add(x.id);
            foreach (string o in old) jobs.Remove(o);
            foreach (Job x in jobs.Values) if (x.state == "Processing") return null;   // ek waqt me ek scan
        }
        string cm = Tag(settings, "ColorMode");
        string mode = cm.StartsWith("Grayscale") ? "gray" : cm.StartsWith("BlackAndWhite") ? "bw" : "color";
        int dpi = 200; int.TryParse(Tag(settings, "XResolution"), out dpi); if (dpi <= 0) dpi = 200;
        int best = 200; foreach (int r in new int[] { 100, 150, 200, 300, 600 }) if (Math.Abs(r - dpi) < Math.Abs(best - dpi)) best = r;
        Job j = new Job(); j.id = Guid.NewGuid().ToString("N");
        lock (jobs) jobs[j.id] = j;
        ThreadPool.QueueUserWorkItem(delegate { RunScan(j, mode, best); });
        return j;
    }
    static void RunScan(Job j, string mode, int dpi)
    {
        try
        {
            Note("Host PC par scan shuru (" + dpi + " DPI)...", false);
            string body = "{\"scanner\":\"" + (ScannerId() ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\",\"mode\":\"" + mode + "\",\"dpi\":" + dpi + "}";
            string r = Api("POST", "/api/scan", body == null ? "" : body);
            string id = TagJson(r, "job");
            if (id == "") throw new Exception(TagJson(r, "err") != "" ? TagJson(r, "err") : "Host ne scan nahi liya");
            for (int i = 0; i < 150; i++)
            {
                Thread.Sleep(2000);
                string st = Api("GET", "/api/job/" + id, null);
                string state = TagJson(st, "state");
                if (state == "done") { j.data = ApiBytes("/api/job/" + id + "/file"); if (j.data == null || j.data.Length == 0) throw new Exception("Scan file nahi aayi"); j.state = "Ready"; Note("Scan aa gaya - NAPS2 me dekhiye", false); return; }
                if (state == "error" || state == "denied") throw new Exception(TagJson(st, "msg"));
                if (state == "approval" && i == 0) Note("Host PC par 'Allow' ka intezaar...", false);
            }
            throw new Exception("Host se jawab nahi aaya");
        }
        catch (Exception e) { j.err = e.Message; j.state = "Aborted"; Note("Scan nahi hua: " + e.Message, true); }
    }
    static string TagJson(string json, string key)
    {
        try { Dictionary<string, object> o = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json); object v; return o != null && o.TryGetValue(key, out v) && v != null ? v.ToString() : ""; }
        catch { return ""; }
    }

    // ---------- mDNS: NAPS2 khud dhoondh le ("_uscan._tcp") ----------
    static void Name(List<byte> o, string name) { foreach (string p in name.TrimEnd('.').Split('.')) { byte[] b = Encoding.UTF8.GetBytes(p); o.Add((byte)b.Length); o.AddRange(b); } o.Add(0); }
    static void U16(List<byte> o, int v) { o.Add((byte)(v >> 8)); o.Add((byte)v); }
    static void U32(List<byte> o, uint v) { o.Add((byte)(v >> 24)); o.Add((byte)(v >> 16)); o.Add((byte)(v >> 8)); o.Add((byte)v); }
    static void Rec(List<byte> o, string name, int type, bool flush, List<byte> rdata)
    {
        Name(o, name); U16(o, type); U16(o, flush ? 0x8001 : 0x0001); U32(o, 120); U16(o, rdata.Count); o.AddRange(rdata);
    }
    static string ReadName(byte[] b, ref int i)
    {
        StringBuilder s = new StringBuilder(); int jumps = 0; int pos = i; bool jumped = false;
        while (pos < b.Length && jumps < 10)
        {
            int l = b[pos];
            if (l == 0) { pos++; break; }
            if ((l & 0xC0) == 0xC0) { if (pos + 1 >= b.Length) break; int ptr = ((l & 0x3F) << 8) | b[pos + 1]; if (!jumped) i = pos + 2; jumped = true; pos = ptr; jumps++; continue; }
            if (pos + 1 + l > b.Length) break;
            s.Append(Encoding.UTF8.GetString(b, pos + 1, l)).Append('.');
            pos += 1 + l;
        }
        if (!jumped) i = pos;
        return s.ToString().TrimEnd('.').ToLowerInvariant();
    }
    static byte[] Answer()
    {
        string svc = "_uscan._tcp.local", inst = "MeraPrint Scanner (" + HostName() + ")." + svc, host = "meraprint-" + Math.Abs(Environment.MachineName.GetHashCode()) + ".local";
        List<byte> o = new List<byte>();
        U16(o, 0); U16(o, 0x8400); U16(o, 0); U16(o, 1); U16(o, 0); U16(o, 3);
        List<byte> ptr = new List<byte>(); Name(ptr, inst); Rec(o, svc, 12, false, ptr);
        List<byte> srv = new List<byte>(); U16(srv, 0); U16(srv, 0); U16(srv, Port); Name(srv, host); Rec(o, inst, 33, true, srv);
        List<byte> txt = new List<byte>();
        foreach (string kv in new string[] { "txtvers=1", "vers=2.63", "ty=MeraPrint Scanner (" + HostName() + ")", "rs=eSCL", "pdl=image/jpeg", "cs=color,grayscale,binary", "is=platen", "duplex=F", "UUID=" + Uuid, "note=MeraPrint" })
        { byte[] b = Encoding.UTF8.GetBytes(kv); txt.Add((byte)Math.Min(255, b.Length)); txt.AddRange(b); }
        Rec(o, inst, 16, true, txt);
        Rec(o, host, 1, true, new List<byte>(new byte[] { 127, 0, 0, 1 }));
        return o.ToArray();
    }
    static void Mdns()
    {
        try
        {
            System.Net.Sockets.UdpClient u = new System.Net.Sockets.UdpClient();
            u.ExclusiveAddressUse = false;
            u.Client.SetSocketOption(System.Net.Sockets.SocketOptionLevel.Socket, System.Net.Sockets.SocketOptionName.ReuseAddress, true);
            u.Client.Bind(new IPEndPoint(IPAddress.Any, 5353));
            IPAddress grp = IPAddress.Parse("224.0.0.251");
            u.JoinMulticastGroup(grp);
            try { u.JoinMulticastGroup(grp, IPAddress.Loopback); } catch { }
            u.MulticastLoopback = true;
            IPEndPoint mc = new IPEndPoint(grp, 5353);
            try { byte[] a0 = Answer(); u.Send(a0, a0.Length, mc); } catch { }
            while (true)
            {
                IPEndPoint from = new IPEndPoint(IPAddress.Any, 0);
                byte[] b = u.Receive(ref from);
                if (b.Length < 12 || (b[2] & 0x80) != 0) continue;    // sirf sawaal
                int qd = (b[4] << 8) | b[5], i = 12; bool want = false, unicast = false;
                for (int k = 0; k < qd && i < b.Length; k++)
                {
                    string n = ReadName(b, ref i);
                    if (i + 4 > b.Length) break;
                    int qclass = (b[i + 2] << 8) | b[i + 3]; i += 4;
                    if (n == "_uscan._tcp.local" || n.EndsWith("._uscan._tcp.local") || n.StartsWith("meraprint-")) { want = true; if ((qclass & 0x8000) != 0) unicast = true; }
                }
                if (!want) continue;
                byte[] ans = Answer();
                try { if (unicast || from.Port != 5353) u.Send(ans, ans.Length, from); else u.Send(ans, ans.Length, mc); } catch { }
            }
        }
        catch { }   // mDNS na chale to bhi NAPS2 me Manual IP 127.0.0.1 : 9801 se chalega
    }
}

// ===================================================================================
// Files: client PC <-> Host PC (Host ka "MeraPrint-Share" folder)
//   Bhejna: Files window ka "File bhejo" / drag-drop / right-click > Send to > MeraPrint Host
//   Lena:   Files window me file chuno > Download  ->  Downloads\MeraPrint
// ===================================================================================
static class Files
{
    public static Func<string> BaseUrl;          // Host ka raasta (null = nahi mila)
    public static Func<string> Token;
    public static Func<string> HostName;
    static readonly JavaScriptSerializer J = new JavaScriptSerializer() { MaxJsonLength = int.MaxValue };
    public static readonly string DownDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "MeraPrint");

    static Dictionary<string, object> Obj(string s) { try { return J.Deserialize<Dictionary<string, object>>(s); } catch { return null; } }
    static string Str(Dictionary<string, object> o, string k) { object v; return o != null && o.TryGetValue(k, out v) && v != null ? v.ToString() : ""; }
    static string Http(string method, string path, string body, int ms, out int code)
    {
        code = 0;
        string b = BaseUrl(); if (b == null) return "{\"err\":\"Host PC nahi mila (Host chalu hai? Internet / WiFi?)\"}";
        try
        {
            HttpWebRequest r = (HttpWebRequest)WebRequest.Create(b + path);
            r.Method = method; r.Timeout = ms; r.ReadWriteTimeout = ms; r.Headers["x-token"] = Token();
            if (body != null) { byte[] d = Encoding.UTF8.GetBytes(body); r.ContentType = "application/json"; r.ContentLength = d.Length; using (Stream s = r.GetRequestStream()) s.Write(d, 0, d.Length); }
            using (HttpWebResponse resp = (HttpWebResponse)r.GetResponse()) using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) { code = (int)resp.StatusCode; return sr.ReadToEnd(); }
        }
        catch (WebException e)
        {
            if (e.Response != null) using (StreamReader sr = new StreamReader(e.Response.GetResponseStream(), Encoding.UTF8)) { code = (int)((HttpWebResponse)e.Response).StatusCode; return sr.ReadToEnd(); }
            return "{\"err\":\"" + e.Message.Replace("\"", "'") + "\"}";
        }
        catch (Exception e) { return "{\"err\":\"" + e.Message.Replace("\"", "'") + "\"}"; }
    }
    /** Host par job ki haalat jab tak khatam na ho (status = har badlaav par) */
    static Dictionary<string, object> WaitJob(string id, Action<string> status)
    {
        bool asked = false;
        for (int i = 0; i < 200; i++)
        {
            int code; Dictionary<string, object> j = Obj(Http("GET", "/api/job/" + id, null, 20000, out code));
            string st = Str(j, "state");
            if (st == "done" || st == "error" || st == "denied") return j;
            if (st == "approval" && !asked) { asked = true; status("Host PC par 'Allow' ka intezaar..."); }
            Thread.Sleep(1500);
        }
        Dictionary<string, object> t = new Dictionary<string, object>(); t["state"] = "error"; t["msg"] = "Host se jawab nahi aaya"; return t;
    }
    static string Plain(string s) { StringBuilder b = new StringBuilder(); foreach (char c in s ?? "") if (c < 0x2000) b.Append(c); return b.ToString().Trim(); }

    /** Ek file Host par bhejo. Lautata hai: "" = ho gaya, warna galti */
    public static string Upload(string file, Action<string> status)
    {
        string name = Path.GetFileName(file);
        try
        {
            string b = BaseUrl(); if (b == null) return "Host PC nahi mila";
            FileInfo fi = new FileInfo(file);
            HttpWebRequest r = (HttpWebRequest)WebRequest.Create(b + "/api/send");
            r.Method = "PUT"; r.Timeout = 60000; r.ReadWriteTimeout = 600000; r.AllowWriteStreamBuffering = false; r.SendChunked = false;
            r.Headers["x-token"] = Token(); r.Headers["x-name"] = Uri.EscapeDataString(name);
            r.ContentType = "application/octet-stream"; r.ContentLength = fi.Length;
            using (Stream s = r.GetRequestStream()) using (FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                byte[] buf = new byte[262144]; long sent = 0; int n; int lastPct = -1;
                while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                {
                    s.Write(buf, 0, n); sent += n;
                    int pct = fi.Length > 0 ? (int)(sent * 100 / fi.Length) : 100;
                    if (pct != lastPct) { lastPct = pct; status(name + " - Host tak bhej rahe hain " + pct + "%"); }
                }
            }
            string resp; int code;
            try { using (HttpWebResponse rr = (HttpWebResponse)r.GetResponse()) using (StreamReader sr = new StreamReader(rr.GetResponseStream())) { code = (int)rr.StatusCode; resp = sr.ReadToEnd(); } }
            catch (WebException e)
            {
                if (e.Response == null) return e.Message;
                using (StreamReader sr = new StreamReader(e.Response.GetResponseStream())) { code = (int)((HttpWebResponse)e.Response).StatusCode; resp = sr.ReadToEnd(); }
            }
            Dictionary<string, object> o = Obj(resp);
            if (code != 200) return Str(o, "err") != "" ? Str(o, "err") : "Host ne mana kiya (" + code + ")";
            Dictionary<string, object> j = WaitJob(Str(o, "job"), status);
            if (Str(j, "state") == "done") { status(name + " - " + Plain(Str(j, "msg"))); return ""; }
            return Plain(Str(j, "msg"));
        }
        catch (Exception e) { return e.Message; }
    }

    /** Host ke share folder se file lao -> Downloads\MeraPrint. Lautata hai: save hui file ka raasta, ya null */
    public static string Download(string relPath, Action<string> status, out string err)
    {
        err = null;
        string name = relPath.Substring(relPath.LastIndexOf('/') + 1);
        Dictionary<string, object> body = new Dictionary<string, object>(); body["path"] = relPath;
        int code; Dictionary<string, object> o = Obj(Http("POST", "/api/download", J.Serialize(body), 30000, out code));
        if (code != 200) { err = Str(o, "err") != "" ? Str(o, "err") : "Host ne mana kiya (" + code + ")"; return null; }
        status(name + " - Host se maang rahe hain...");
        Dictionary<string, object> j = WaitJob(Str(o, "job"), status);
        if (Str(j, "state") != "done" || Str(j, "ticket") == "") { err = Plain(Str(j, "msg")); if (err == "") err = "Download nahi mila"; return null; }
        try
        {
            Directory.CreateDirectory(DownDir);
            string dest = Path.Combine(DownDir, name), ext = Path.GetExtension(name), bn = Path.GetFileNameWithoutExtension(name);
            for (int k = 1; File.Exists(dest); k++) dest = Path.Combine(DownDir, bn + " (" + k + ")" + ext);
            HttpWebRequest r = (HttpWebRequest)WebRequest.Create(BaseUrl() + "/dl/" + Str(j, "ticket"));
            r.Timeout = 60000; r.ReadWriteTimeout = 600000;
            using (HttpWebResponse resp = (HttpWebResponse)r.GetResponse())
            using (Stream s = resp.GetResponseStream())
            using (FileStream fs = new FileStream(dest + ".part", FileMode.Create))
            {
                long total = resp.ContentLength, got = 0; byte[] buf = new byte[262144]; int n, lastPct = -1;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    fs.Write(buf, 0, n); got += n;
                    int pct = total > 0 ? (int)(got * 100 / total) : 0;
                    if (pct != lastPct) { lastPct = pct; status(name + " - download " + pct + "%"); }
                }
            }
            File.Move(dest + ".part", dest);
            return dest;
        }
        catch (Exception e) { err = e.Message; return null; }
    }

    // ---------------- Files window ----------------
    static Form win;
    public static void Show()
    {
        if (win != null && !win.IsDisposed) { win.WindowState = FormWindowState.Normal; win.Activate(); return; }
        Form f = new Form(); win = f;
        f.Text = "MeraPrint Files - Host: " + HostName();
        f.Size = new Size(760, 560); f.MinimumSize = new Size(560, 400); f.StartPosition = FormStartPosition.CenterScreen;
        f.Font = new Font("Segoe UI", 10f); f.AllowDrop = true;
        Panel top = new Panel(); top.Dock = DockStyle.Top; top.Height = 48;
        Button up = new Button(); up.Text = "Upar"; up.SetBounds(8, 8, 70, 32);
        Button refresh = new Button(); refresh.Text = "Taaza"; refresh.SetBounds(84, 8, 70, 32);
        Button send = new Button(); send.Text = "File bhejo (Host par)"; send.SetBounds(160, 8, 180, 32); send.BackColor = Color.FromArgb(10, 124, 255); send.ForeColor = Color.White; send.FlatStyle = FlatStyle.Flat;
        Button dl = new Button(); dl.Text = "Download (is PC par)"; dl.SetBounds(346, 8, 180, 32); dl.BackColor = Color.FromArgb(22, 163, 74); dl.ForeColor = Color.White; dl.FlatStyle = FlatStyle.Flat;
        Button openDl = new Button(); openDl.Text = "Downloads kholo"; openDl.SetBounds(532, 8, 150, 32);
        top.Controls.AddRange(new Control[] { up, refresh, send, dl, openDl });
        Label path = new Label(); path.Dock = DockStyle.Top; path.Height = 24; path.Padding = new Padding(8, 3, 0, 0); path.ForeColor = Color.DimGray;
        ListView lv = new ListView(); lv.Dock = DockStyle.Fill; lv.View = View.Details; lv.FullRowSelect = true; lv.MultiSelect = true; lv.HideSelection = false;
        lv.Columns.Add("Naam", 380); lv.Columns.Add("Size", 100); lv.Columns.Add("Tareekh", 160);
        Label status = new Label(); status.Dock = DockStyle.Bottom; status.Height = 46; status.Padding = new Padding(8, 4, 8, 0);
        status.Text = "Files ko is window par kheench kar chhodo = Host par chali jayengi.  Host par aayi files: 'Aayi hui files' folder.";
        f.Controls.Add(lv); f.Controls.Add(path); f.Controls.Add(top); f.Controls.Add(status);

        string cur = "";
        Action<string> say = delegate (string t) { try { f.BeginInvoke((MethodInvoker)delegate { status.Text = t; }); } catch { } };
        Action<string> load = null;
        load = delegate (string p)
        {
            say("Host se list la rahe hain...");
            ThreadPool.QueueUserWorkItem(delegate
            {
                int code; string t = Http("GET", "/api/files?path=" + Uri.EscapeDataString(p), null, 30000, out code);
                Dictionary<string, object> o = Obj(t);
                try
                {
                    f.BeginInvoke((MethodInvoker)delegate
                    {
                        if (code != 200) { say("Galti: " + (Str(o, "err") != "" ? Str(o, "err") : "(" + code + ")")); return; }
                        cur = Str(o, "path");
                        path.Text = "Host: " + HostName() + "  >  MeraPrint-Share" + (cur == "" ? "" : "  >  " + cur.Replace("/", "  >  "));
                        lv.BeginUpdate(); lv.Items.Clear();
                        object items; o.TryGetValue("items", out items);
                        if (items is IEnumerable)
                            foreach (object x in (IEnumerable)items)
                            {
                                Dictionary<string, object> it = x as Dictionary<string, object>; if (it == null) continue;
                                bool dir = Str(it, "dir") == "True";
                                long size = 0; double mt = 0; object ov;
                                try { if (it.TryGetValue("size", out ov) && ov != null) size = Convert.ToInt64(ov); } catch { }
                                try { if (it.TryGetValue("mtime", out ov) && ov != null) mt = Convert.ToDouble(ov); } catch { }
                                ListViewItem li = new ListViewItem((dir ? "[Folder]  " : "") + Str(it, "name"));
                                li.SubItems.Add(dir ? "" : (size >= 1048576 ? (size / 1048576.0).ToString("0.0") + " MB" : Math.Max(1, size / 1024) + " KB"));
                                li.SubItems.Add(mt > 0 ? new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(mt).ToLocalTime().ToString("dd-MM-yyyy HH:mm") : "");
                                li.Tag = new string[] { Str(it, "name"), dir ? "1" : "0" };
                                lv.Items.Add(li);
                            }
                        lv.EndUpdate();
                        say(lv.Items.Count == 0 ? "Ye folder khaali hai. Host PC par 'MeraPrint-Share' folder me file rakhiye." : lv.Items.Count + " cheezein. Folder par double-click = kholo, file chuno + Download.");
                    });
                }
                catch { }
            });
        };
        Action<string[]> sendFiles = delegate (string[] list)
        {
            List<string> ok = new List<string>(); foreach (string x in list) if (File.Exists(x)) ok.Add(x);
            if (ok.Count == 0) { say("Sirf files bhej sakte hain (folder nahi)"); return; }
            send.Enabled = false;
            ThreadPool.QueueUserWorkItem(delegate
            {
                int good = 0; string last = "";
                foreach (string x in ok) { string e = Upload(x, say); if (e == "") good++; else last = Path.GetFileName(x) + ": " + e; }
                say(good + "/" + ok.Count + " file Host par pahunchi" + (last != "" ? "   |   Galti: " + last : "  (Host: MeraPrint-Share > Aayi hui files)"));
                try { f.BeginInvoke((MethodInvoker)delegate { send.Enabled = true; load(cur); }); } catch { }
            });
        };
        Action open = delegate
        {
            if (lv.SelectedItems.Count == 0) return;
            string[] tg = (string[])lv.SelectedItems[0].Tag;
            if (tg[1] == "1") { load(cur == "" ? tg[0] : cur + "/" + tg[0]); return; }
            dl.PerformClick();
        };
        up.Click += delegate { if (cur == "") return; int k = cur.LastIndexOf('/'); load(k < 0 ? "" : cur.Substring(0, k)); };
        refresh.Click += delegate { load(cur); };
        lv.DoubleClick += delegate { open(); };
        lv.KeyDown += delegate (object s, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) open(); if (e.KeyCode == Keys.Back) up.PerformClick(); };
        send.Click += delegate
        {
            OpenFileDialog d = new OpenFileDialog(); d.Multiselect = true; d.Title = "Host PC par bhejne ke liye file chuniye";
            if (d.ShowDialog(f) == DialogResult.OK) sendFiles(d.FileNames);
        };
        dl.Click += delegate
        {
            List<string> want = new List<string>();
            foreach (ListViewItem li in lv.SelectedItems) { string[] tg = (string[])li.Tag; if (tg[1] == "0") want.Add(cur == "" ? tg[0] : cur + "/" + tg[0]); }
            if (want.Count == 0) { say("Pehle list me file chuniye (folder nahi)"); return; }
            dl.Enabled = false;
            ThreadPool.QueueUserWorkItem(delegate
            {
                int good = 0; string last = "", lastFile = null;
                foreach (string w in want) { string err; string p = Download(w, say, out err); if (p != null) { good++; lastFile = p; } else last = w + ": " + err; }
                say(good + "/" + want.Count + " download hui -> " + DownDir + (last != "" ? "   |   Galti: " + last : ""));
                if (lastFile != null) try { Process.Start("explorer.exe", "/select,\"" + lastFile + "\""); } catch { }
                try { f.BeginInvoke((MethodInvoker)delegate { dl.Enabled = true; }); } catch { }
            });
        };
        openDl.Click += delegate { try { Directory.CreateDirectory(DownDir); Process.Start("explorer.exe", "\"" + DownDir + "\""); } catch { } };
        f.DragEnter += delegate (object s, DragEventArgs e) { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
        f.DragDrop += delegate (object s, DragEventArgs e) { string[] fl = e.Data.GetData(DataFormats.FileDrop) as string[]; if (fl != null) sendFiles(fl); };
        lv.AllowDrop = true;
        lv.DragEnter += delegate (object s, DragEventArgs e) { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
        lv.DragDrop += delegate (object s, DragEventArgs e) { string[] fl = e.Data.GetData(DataFormats.FileDrop) as string[]; if (fl != null) sendFiles(fl); };
        f.Shown += delegate { load(""); };
        f.Show();
    }

    /** Right-click > Send to > MeraPrint Host: chhoti progress window, phir band */
    public static void SendMode(string[] files)
    {
        Form f = new Form(); f.Text = "MeraPrint - Host (" + HostName() + ") par bhej rahe hain";
        f.ClientSize = new Size(520, 110); f.StartPosition = FormStartPosition.CenterScreen; f.FormBorderStyle = FormBorderStyle.FixedDialog; f.Font = new Font("Segoe UI", 10f); f.TopMost = true;
        Label l = new Label(); l.SetBounds(14, 14, 492, 80); l.Text = "Shuru...";
        f.Controls.Add(l);
        Action<string> say = delegate (string t) { try { f.BeginInvoke((MethodInvoker)delegate { l.Text = t; }); } catch { } };
        f.Shown += delegate
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                int good = 0, total = 0; string last = "";
                foreach (string x in files) { if (!File.Exists(x)) continue; total++; string e = Upload(x, say); if (e == "") good++; else last = Path.GetFileName(x) + ": " + e; }
                say(good + "/" + total + " file Host par pahunchi." + (last != "" ? "\nGalti: " + last : "\nHost: MeraPrint-Share > Aayi hui files"));
                Thread.Sleep(last != "" ? 8000 : 2500);
                try { f.BeginInvoke((MethodInvoker)delegate { f.Close(); }); } catch { }
            });
        };
        Application.Run(f);
    }
}
