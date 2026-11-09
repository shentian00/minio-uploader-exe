// minio-uploader.exe：本地照片自动上传 MinIO
// - 用 Windows 自带的 .NET Framework 4.x 编译器 csc.exe 编译（见 build.cmd），只使用 C# 5 语法
// - 不依赖 PowerShell，不需要安装任何组件
// - MinIO 的接口就是 S3 协议，请求按 S3 标准的 Signature V4 签名
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace MinioPhotoUploader
{
    sealed class Config
    {
        public string FilePath;
        public string Endpoint, AccessKey, SecretKey, Region, Bucket, ObjectPrefix;
        public string SourceDir, FailedDir, DataDir, HostHeader;
        public string[] Extensions;
        public int UploadThreads, ScanIntervalSeconds, StableSeconds, MaxFailures, RetryDelaySeconds;
        public int RequestTimeoutSeconds, EmptyDirMinAgeHours, LogRetentionDays;
        public int KeepLocalDays;   // 0=上传成功立即删除本地；1=保留当天（0 点后核对 MinIO 再删）；2=保留今天和昨天……
        public bool CleanEmptyDirs, IsHttps;
        public DateTime? UploadFromDate;   // 只处理修改日期 >= 这一天的照片；更早的不上传、不删除、不移动。为空表示全部处理
    }

    // 已上传但本地暂时保留的照片（保存在 data\uploaded.idx，重启后不会重复上传）
    sealed class UploadedEntry
    {
        public long Size;
        public long MtimeTicks;     // 本地文件修改时间（UTC Ticks），用来判断上传后文件有没有被改过
        public string Md5 = "";
    }

    // 一张照片本轮要做的事：Indexed 为空 = 上传；Indexed 不为空 = 已上传、过了保留期，核对 MinIO 后删除本地
    sealed class Job
    {
        public FileInfo File;
        public string Rel;
        public UploadedEntry Indexed;
        public bool Keep;           // 上传后是否保留本地（照片日期还在保留期内）
    }

    sealed class S3Result
    {
        public bool Ok;
        public int Status;          // 0 表示网络层失败（连不上、超时等）
        public string ETag = "";
        public long Length = -1;
        public string Error = "";
    }

    // Done=本地已删除  Kept=已上传、本地按规则保留  Skip=本轮跳过  Fail=计一次失败  Pause=MinIO 整体不可用，暂停
    enum Outcome { Done, Kept, Skip, Fail, Pause }

    sealed class UploadResult
    {
        public Outcome Kind;
        public string Error = "";
        public string LogLevel;
        public string LogText;
        public bool Uploaded;           // 本次是否真的发了 PUT
        public bool Verified;           // 本次是核对 MinIO 后删除（没有重新上传）
        public UploadedEntry Entry;     // Kept 时写入已上传清单

        public static UploadResult Make(Outcome kind, string error, string level, string text)
        {
            UploadResult r = new UploadResult();
            r.Kind = kind; r.Error = error ?? ""; r.LogLevel = level; r.LogText = text;
            return r;
        }
    }

    sealed class FailState
    {
        public int Failures;
        public string LastError = "";
        public DateTime NextRetry = DateTime.MinValue;
    }

    static class Program
    {
        const string TaskName = "MinioPhotoUploader";
        const string WatchdogTaskName = "MinioPhotoUploaderWatchdog";
        const string MutexName = @"Global\MinioPhotoUploader";
        const string MutexNameLocal = "MinioPhotoUploader";
        const string EmptySha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

        static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
        static readonly Encoding Utf8Bom = new UTF8Encoding(true);
        static readonly object LogLock = new object();
        static readonly object StateLock = new object();

        static string ExePath, ExeDir, LogDir, StatePath, FailedCsv, UploadedPath;
        static Config Cfg;
        static Dictionary<string, FailState> State = new Dictionary<string, FailState>(StringComparer.OrdinalIgnoreCase);
        static Dictionary<string, UploadedEntry> Uploaded = new Dictionary<string, UploadedEntry>(StringComparer.OrdinalIgnoreCase);
        static bool Paused;
        static string LastPauseMsg = "";

        // ============================================================
        //  入口
        // ============================================================
        static int Main(string[] args)
        {
            ExePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
            ExeDir = Path.GetDirectoryName(ExePath);
            LogDir = Path.Combine(ExeDir, @"data\logs");

            string mode = null, cfgPath = null;
            bool skipCheck = false;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string al = a.ToLowerInvariant();
                if ((al == "--config" || al == "-config" || al == "/config") && i + 1 < args.Length) cfgPath = args[++i];
                else if (al == "--skip-check" || al == "-skipcheck") skipCheck = true;
                else if (al == "-h" || al == "--help" || al == "/?" || al == "help") mode = "help";
                else if (mode == null) mode = al.TrimStart('-', '/');
                else { Console.Error.WriteLine("未知参数: " + a); PrintHelp(); return 2; }
            }
            if (mode == null) mode = "help";   // 双击运行时只显示帮助，不会误启动上传
            if (cfgPath == null) cfgPath = Path.Combine(ExeDir, "config.json");
            cfgPath = Path.GetFullPath(cfgPath);

            try
            {
                switch (mode)
                {
                    case "run": return Start(cfgPath, false);
                    case "once": return Start(cfgPath, true);
                    case "check": return InitConfig(cfgPath, true) ? RunCheck() : 1;
                    case "install": return Install(cfgPath, skipCheck);
                    case "uninstall": return Uninstall();
                    case "watchdog": return Watchdog(cfgPath);
                    case "selftest": return SelfTest();
                    case "help": PrintHelp(); return 0;
                    default: Console.Error.WriteLine("未知命令: " + mode); PrintHelp(); return 2;
                }
            }
            catch (Exception ex)
            {
                Log("ERROR", "程序异常: " + ex.Message);
                return 1;
            }
        }

        static void PrintHelp()
        {
            Console.WriteLine("照片自动上传 MinIO");
            Console.WriteLine();
            Console.WriteLine("用法: minio-uploader.exe <命令> [--config 配置文件路径]");
            Console.WriteLine();
            Console.WriteLine("  install     注册开机自启任务和看门狗任务并立即启动（需要管理员）");
            Console.WriteLine("              加 --skip-check 可跳过安装前的连通性检查（仅测试用）");
            Console.WriteLine("  uninstall   停止并删除两个计划任务（需要管理员，不删除照片和日志）");
            Console.WriteLine("  check       只检查连通性和权限，不上传、不删除照片");
            Console.WriteLine("  once        执行一轮上传后退出（会真实上传并删除本地照片）");
            Console.WriteLine("  run         常驻运行（计划任务使用，一般不用手动执行）");
            Console.WriteLine("  watchdog    看门狗检查一次（计划任务使用）");
            Console.WriteLine();
            Console.WriteLine("默认配置文件: 程序同目录下的 config.json");
        }

        // ============================================================
        //  日志
        // ============================================================
        static void Log(string level, string msg)
        {
            DateTime now = DateTime.Now;
            string line = now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " [" + level + "] " + msg;
            lock (LogLock)
            {
                try
                {
                    Directory.CreateDirectory(LogDir);
                    File.AppendAllText(Path.Combine(LogDir, "uploader-" + now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log"),
                        line + "\r\n", Utf8Bom);
                }
                catch { }
                try { Console.WriteLine(line); } catch { }
            }
        }

        static void WriteRestartLog(string msg)
        {
            DateTime now = DateTime.Now;
            string line = now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " " + msg;
            try
            {
                Directory.CreateDirectory(LogDir);
                File.AppendAllText(Path.Combine(LogDir, "restart-" + now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log"),
                    line + "\r\n", Utf8Bom);
            }
            catch { }
            try { Console.WriteLine(line); } catch { }
        }

        static void Say(string text, ConsoleColor? color)
        {
            try
            {
                if (color.HasValue) Console.ForegroundColor = color.Value;
                Console.WriteLine(text);
            }
            finally { try { Console.ResetColor(); } catch { } }
        }

        // ============================================================
        //  配置
        // ============================================================
        static bool InitConfig(string path, bool toConsole)
        {
            try
            {
                Cfg = LoadConfig(path);
            }
            catch (Exception ex)
            {
                if (toConsole) Say("[失败] 配置错误: " + ex.Message, ConsoleColor.Red);
                else Log("ERROR", "配置错误: " + ex.Message);
                return false;
            }
            LogDir = Path.Combine(Cfg.DataDir, "logs");
            StatePath = Path.Combine(Cfg.DataDir, "state.json");
            UploadedPath = Path.Combine(Cfg.DataDir, "uploaded.idx");
            FailedCsv = Path.Combine(Cfg.FailedDir, "failed-records.csv");
            Directory.CreateDirectory(Cfg.DataDir);
            Directory.CreateDirectory(LogDir);
            Directory.CreateDirectory(Cfg.FailedDir);

            ServicePointManager.Expect100Continue = false;
            ServicePointManager.DefaultConnectionLimit = Math.Max(8, Cfg.UploadThreads + 2);
            // MinIO 用 HTTPS 时要启用 TLS 1.2（.NET Framework 4.x 默认不开，否则握手失败）
            if (Cfg.IsHttps) ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            return true;
        }

        static Config LoadConfig(string path)
        {
            if (!File.Exists(path)) throw new Exception("配置文件不存在: " + path);
            Dictionary<string, object> raw;
            try { raw = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>; }
            catch (Exception ex) { throw new Exception("配置文件不是有效的 JSON（检查引号、逗号、路径里的反斜杠是否写成两个）: " + ex.Message); }
            if (raw == null) throw new Exception("配置文件格式不正确: " + path);

            Dictionary<string, object> d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, object> kv in raw) if (!kv.Key.StartsWith("_")) d[kv.Key] = kv.Value;

            Config c = new Config();
            c.FilePath = path;
            c.Endpoint = Str(d, "Endpoint", "").Trim().TrimEnd('/');
            c.AccessKey = Str(d, "AccessKey", "").Trim();
            c.SecretKey = Str(d, "SecretKey", "");
            c.Region = Str(d, "Region", "us-east-1").Trim();
            c.Bucket = Str(d, "Bucket", "").Trim();
            c.ObjectPrefix = Str(d, "ObjectPrefix", "").Trim().Trim('/');
            string src = Str(d, "SourceDir", "").Trim();
            string failed = Str(d, "FailedDir", "").Trim();

            foreach (string k in new[] { "Endpoint", "AccessKey", "SecretKey", "Bucket", "SourceDir", "FailedDir" })
            {
                if (string.IsNullOrWhiteSpace(Str(d, k, ""))) throw new Exception("配置项 " + k + " 不能为空");
            }

            c.SourceDir = Path.GetFullPath(src).TrimEnd('\\');
            c.FailedDir = Path.GetFullPath(failed).TrimEnd('\\');
            string dataDir = Str(d, "DataDir", "data").Trim();
            if (dataDir.Length == 0) dataDir = "data";
            if (!Path.IsPathRooted(dataDir)) dataDir = Path.Combine(ExeDir, dataDir);
            c.DataDir = Path.GetFullPath(dataDir).TrimEnd('\\');

            foreach (string dir in new[] { c.FailedDir, c.DataDir })
            {
                if ((dir + "\\").StartsWith(c.SourceDir + "\\", StringComparison.OrdinalIgnoreCase))
                    throw new Exception("FailedDir / DataDir 不能位于 SourceDir 之内: " + dir);
            }

            List<string> exts = new List<string>();
            object ev;
            if (d.TryGetValue("Extensions", out ev) && ev is object[])
            {
                foreach (object o in (object[])ev)
                {
                    string e = Convert.ToString(o, CultureInfo.InvariantCulture).Trim().ToLowerInvariant();
                    if (e.Length == 0) continue;
                    if (!e.StartsWith(".")) e = "." + e;
                    exts.Add(e);
                }
            }
            if (exts.Count == 0) exts.AddRange(new[] { ".jpg", ".jpeg", ".bmp" });
            c.Extensions = exts.ToArray();

            c.UploadThreads = Math.Max(1, Math.Min(32, Int(d, "UploadThreads", 4)));
            c.ScanIntervalSeconds = Math.Max(1, Int(d, "ScanIntervalSeconds", 10));
            c.StableSeconds = Math.Max(0, Int(d, "StableSeconds", 30));
            c.MaxFailures = Math.Max(1, Int(d, "MaxFailures", 5));
            c.RetryDelaySeconds = Math.Max(0, Int(d, "RetryDelaySeconds", 60));
            c.RequestTimeoutSeconds = Math.Max(5, Int(d, "RequestTimeoutSeconds", 120));
            c.EmptyDirMinAgeHours = Math.Max(0, Int(d, "EmptyDirMinAgeHours", 48));
            c.LogRetentionDays = Math.Max(0, Int(d, "LogRetentionDays", 30));
            c.KeepLocalDays = Math.Max(0, Math.Min(365, Int(d, "KeepLocalDays", 0)));
            c.CleanEmptyDirs = Bool(d, "CleanEmptyDirs", false);   // 默认不删除任何文件夹，只删除已上传的照片

            string fromDate = Str(d, "UploadFromDate", "").Trim();
            if (fromDate.Length > 0)
            {
                DateTime fd;
                if (!DateTime.TryParseExact(fromDate, new[] { "yyyy-MM-dd", "yyyyMMdd" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out fd))
                    throw new Exception("配置项 UploadFromDate 格式不正确，应类似 2026-10-29（留空表示全部上传）: " + fromDate);
                c.UploadFromDate = fd.Date;
            }

            Uri uri;
            if (!Uri.TryCreate(c.Endpoint, UriKind.Absolute, out uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
                throw new Exception("Endpoint 格式不正确，应类似 http://192.168.1.10:9000 : " + c.Endpoint);
            c.HostHeader = uri.IsDefaultPort ? uri.Host : uri.Host + ":" + uri.Port;
            c.IsHttps = uri.Scheme == "https";
            return c;
        }

        static string Str(Dictionary<string, object> d, string k, string def)
        {
            object v;
            if (d.TryGetValue(k, out v) && v != null) return Convert.ToString(v, CultureInfo.InvariantCulture);
            return def;
        }

        static int Int(Dictionary<string, object> d, string k, int def)
        {
            object v;
            if (!d.TryGetValue(k, out v) || v == null) return def;
            try { return Convert.ToInt32(v, CultureInfo.InvariantCulture); }
            catch { throw new Exception("配置项 " + k + " 必须是整数"); }
        }

        static bool Bool(Dictionary<string, object> d, string k, bool def)
        {
            object v;
            if (!d.TryGetValue(k, out v) || v == null) return def;
            if (v is bool) return (bool)v;
            string s = Convert.ToString(v, CultureInfo.InvariantCulture).Trim().ToLowerInvariant();
            return s == "true" || s == "1";
        }

        static bool KeysNotFilled(Config c)
        {
            return c.AccessKey == "请填写" || c.SecretKey == "请填写";
        }

        // ============================================================
        //  通用工具
        // ============================================================
        static string Hex(byte[] b)
        {
            StringBuilder sb = new StringBuilder(b.Length * 2);
            foreach (byte x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }

        static string Sha256Hex(byte[] data)
        {
            using (SHA256 s = SHA256.Create()) return Hex(s.ComputeHash(data));
        }

        static byte[] Md5(byte[] data)
        {
            using (MD5 m = MD5.Create()) return m.ComputeHash(data);
        }

        static byte[] HmacSha256(byte[] key, string data)
        {
            using (HMACSHA256 h = new HMACSHA256(key)) return h.ComputeHash(Utf8NoBom.GetBytes(data));
        }

        // 按 SigV4 规则对对象路径做 URI 编码，保留 '/'
        static string EncodePath(string key)
        {
            StringBuilder sb = new StringBuilder();
            foreach (byte b in Utf8NoBom.GetBytes(key))
            {
                bool keep = (b >= 0x41 && b <= 0x5A) || (b >= 0x61 && b <= 0x7A) || (b >= 0x30 && b <= 0x39) ||
                            b == 0x2D || b == 0x2E || b == 0x5F || b == 0x7E || b == 0x2F;
                if (keep) sb.Append((char)b); else sb.Append('%').Append(b.ToString("X2"));
            }
            return sb.ToString();
        }

        static string RelPath(string fullName)
        {
            return fullName.Substring(Cfg.SourceDir.Length).TrimStart('\\');
        }

        static string ObjectKey(string rel)
        {
            string k = rel.Replace('\\', '/');
            if (Cfg.ObjectPrefix.Length > 0) k = Cfg.ObjectPrefix + "/" + k;
            return k;
        }

        static string ContentType(string ext)
        {
            switch (ext.ToLowerInvariant())
            {
                case ".jpg":
                case ".jpeg": return "image/jpeg";
                case ".bmp": return "image/bmp";
                case ".png": return "image/png";
                default: return "application/octet-stream";
            }
        }

        static string CsvField(string v)
        {
            return "\"" + (v ?? "").Replace("\"", "\"\"") + "\"";
        }

        // 递归列出文件；某个子目录无权限时跳过，不影响其它目录
        static void CollectFiles(string dir, List<FileInfo> output)
        {
            try
            {
                foreach (string f in Directory.GetFiles(dir)) { try { output.Add(new FileInfo(f)); } catch { } }
                foreach (string sub in Directory.GetDirectories(dir)) CollectFiles(sub, output);
            }
            catch { }
        }

        static void CollectDirs(string dir, List<string> output)
        {
            try
            {
                foreach (string sub in Directory.GetDirectories(dir)) { output.Add(sub); CollectDirs(sub, output); }
            }
            catch { }
        }

        // ============================================================
        //  S3 请求（Signature V4）
        // ============================================================
        static string SigV4(string method, string canonicalUri, string canonicalQuery, string host, string payloadHash,
                            string amzDate, string region, string accessKey, string secretKey)
        {
            string dateStamp = amzDate.Substring(0, 8);
            const string signedHeaders = "host;x-amz-content-sha256;x-amz-date";
            string canonicalHeaders = "host:" + host + "\nx-amz-content-sha256:" + payloadHash + "\nx-amz-date:" + amzDate + "\n";
            string canonicalRequest = method + "\n" + canonicalUri + "\n" + canonicalQuery + "\n" + canonicalHeaders + "\n" +
                                      signedHeaders + "\n" + payloadHash;
            string scope = dateStamp + "/" + region + "/s3/aws4_request";
            string stringToSign = "AWS4-HMAC-SHA256\n" + amzDate + "\n" + scope + "\n" + Sha256Hex(Utf8NoBom.GetBytes(canonicalRequest));

            byte[] kDate = HmacSha256(Utf8NoBom.GetBytes("AWS4" + secretKey), dateStamp);
            byte[] kRegion = HmacSha256(kDate, region);
            byte[] kService = HmacSha256(kRegion, "s3");
            byte[] kSigning = HmacSha256(kService, "aws4_request");
            string signature = Hex(HmacSha256(kSigning, stringToSign));
            return "AWS4-HMAC-SHA256 Credential=" + accessKey + "/" + scope + ", SignedHeaders=" + signedHeaders + ", Signature=" + signature;
        }

        static S3Result S3(string method, string key, byte[] body, string contentType, string contentMd5)
        {
            string path = "/" + EncodePath(Cfg.Bucket);
            if (!string.IsNullOrEmpty(key)) path += "/" + EncodePath(key);
            string amzDate = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
            string payloadHash = (body != null && body.Length > 0) ? Sha256Hex(body) : EmptySha256;
            string auth = SigV4(method, path, "", Cfg.HostHeader, payloadHash, amzDate, Cfg.Region, Cfg.AccessKey, Cfg.SecretKey);

            S3Result r = new S3Result();
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(Cfg.Endpoint + path);
                req.Method = method;
                req.Proxy = null;                 // 内网直连，不走系统代理
                req.KeepAlive = true;
                req.AllowAutoRedirect = false;
                req.Timeout = Cfg.RequestTimeoutSeconds * 1000;
                req.ReadWriteTimeout = Cfg.RequestTimeoutSeconds * 1000;
                req.Headers.Add("x-amz-date", amzDate);
                req.Headers.Add("x-amz-content-sha256", payloadHash);
                req.Headers.Add("Authorization", auth);
                if (contentType != null) req.ContentType = contentType;
                if (contentMd5 != null) req.Headers.Add("Content-MD5", contentMd5);
                if (method == "PUT")
                {
                    int len = body == null ? 0 : body.Length;
                    req.ContentLength = len;
                    if (len > 0) using (Stream s = req.GetRequestStream()) s.Write(body, 0, len);
                }
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    r.Ok = true;
                    r.Status = (int)resp.StatusCode;
                    r.ETag = resp.Headers["ETag"] ?? "";
                    r.Length = resp.ContentLength;
                }
            }
            catch (WebException we)
            {
                HttpWebResponse resp = we.Response as HttpWebResponse;
                if (resp != null)
                {
                    using (resp)
                    {
                        r.Status = (int)resp.StatusCode;
                        string s3Code = "";
                        try
                        {
                            using (StreamReader sr = new StreamReader(resp.GetResponseStream()))
                            {
                                Match m = Regex.Match(sr.ReadToEnd(), "<Code>([^<]+)</Code>");
                                if (m.Success) s3Code = m.Groups[1].Value;
                            }
                        }
                        catch { }
                        r.Error = ("HTTP " + r.Status + " " + s3Code).Trim();
                    }
                }
                else
                {
                    r.Status = 0;
                    r.Error = "网络错误 " + we.Status + ": " + we.Message;
                }
            }
            catch (Exception ex)
            {
                r.Status = 0;
                r.Error = "网络错误 " + ex.Message;
            }
            return r;
        }

        static S3Result TestBucket() { return S3("HEAD", null, null, null, null); }

        // ============================================================
        //  失败计数（持久化到 state.json，重启后不清零）
        // ============================================================
        static void LoadState()
        {
            State.Clear();
            if (!File.Exists(StatePath)) return;
            try
            {
                Dictionary<string, object> obj = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(StatePath, Encoding.UTF8)) as Dictionary<string, object>;
                if (obj == null) return;
                foreach (KeyValuePair<string, object> kv in obj)
                {
                    Dictionary<string, object> v = kv.Value as Dictionary<string, object>;
                    if (v == null) continue;
                    FailState st = new FailState();
                    object o;
                    if (v.TryGetValue("Failures", out o) && o != null) st.Failures = Convert.ToInt32(o, CultureInfo.InvariantCulture);
                    if (v.TryGetValue("LastError", out o) && o != null) st.LastError = Convert.ToString(o, CultureInfo.InvariantCulture);
                    DateTime t;
                    if (v.TryGetValue("NextRetry", out o) && o != null &&
                        DateTime.TryParse(Convert.ToString(o, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out t))
                        st.NextRetry = t.ToLocalTime();
                    State[kv.Key] = st;
                }
            }
            catch (Exception ex)
            {
                Log("WARN", "状态文件读取失败，失败计数从零开始: " + ex.Message);
                State.Clear();
            }
        }

        static void SaveState()
        {
            try
            {
                Dictionary<string, object> outp = new Dictionary<string, object>();
                foreach (KeyValuePair<string, FailState> kv in State)
                {
                    Dictionary<string, object> v = new Dictionary<string, object>();
                    v["Failures"] = kv.Value.Failures;
                    v["LastError"] = kv.Value.LastError;
                    v["NextRetry"] = kv.Value.NextRetry.ToString("o", CultureInfo.InvariantCulture);
                    outp[kv.Key] = v;
                }
                string tmp = StatePath + ".tmp";
                File.WriteAllText(tmp, new JavaScriptSerializer().Serialize(outp), Utf8NoBom);
                if (File.Exists(StatePath)) File.Replace(tmp, StatePath, null);
                else File.Move(tmp, StatePath);
            }
            catch (Exception ex)
            {
                Log("WARN", "保存状态文件失败: " + ex.Message);
            }
        }

        // ============================================================
        //  已上传清单（data\uploaded.idx，每行：相对路径 \t 大小 \t 修改时间Ticks \t MD5）
        //  上传时追加一行；有记录被移除时整体重写一次
        // ============================================================
        static void LoadUploaded()
        {
            Uploaded.Clear();
            if (!File.Exists(UploadedPath)) return;
            try
            {
                foreach (string line in File.ReadAllLines(UploadedPath, Encoding.UTF8))
                {
                    string[] p = line.Split('\t');
                    if (p.Length != 4) continue;
                    UploadedEntry e = new UploadedEntry();
                    if (!long.TryParse(p[1], out e.Size) || !long.TryParse(p[2], out e.MtimeTicks)) continue;
                    e.Md5 = p[3].Trim();
                    Uploaded[p[0]] = e;
                }
            }
            catch (Exception ex)
            {
                Log("WARN", "已上传清单读取失败（保留期内的照片可能会重新上传一次，不影响结果）: " + ex.Message);
                Uploaded.Clear();
            }
        }

        static string UploadedLine(string rel, UploadedEntry e)
        {
            return rel + "\t" + e.Size.ToString(CultureInfo.InvariantCulture) + "\t" +
                   e.MtimeTicks.ToString(CultureInfo.InvariantCulture) + "\t" + e.Md5 + "\r\n";
        }

        static void AppendUploaded(string rel, UploadedEntry e)
        {
            try { File.AppendAllText(UploadedPath, UploadedLine(rel, e), Utf8NoBom); }
            catch (Exception ex) { Log("WARN", "写入已上传清单失败: " + ex.Message); }
        }

        static void RewriteUploaded()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                foreach (KeyValuePair<string, UploadedEntry> kv in Uploaded) sb.Append(UploadedLine(kv.Key, kv.Value));
                string tmp = UploadedPath + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), Utf8NoBom);
                if (File.Exists(UploadedPath)) File.Replace(tmp, UploadedPath, null);
                else File.Move(tmp, UploadedPath);
            }
            catch (Exception ex) { Log("WARN", "重写已上传清单失败: " + ex.Message); }
        }

        // “今天”。环境变量 MINIO_UPLOADER_TEST_TODAY=yyyyMMdd 只用于测试跨天，正常运行不要设置
        static DateTime Today()
        {
            string t = Environment.GetEnvironmentVariable("MINIO_UPLOADER_TEST_TODAY");
            DateTime d;
            if (!string.IsNullOrEmpty(t) && DateTime.TryParseExact(t, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) return d;
            return DateTime.Today;
        }

        // 照片的修改日期还在保留期内（KeepLocalDays=1 表示只保留当天的；0 点一过，前一天的就到期）
        static bool InKeepPeriod(FileInfo f)
        {
            return Cfg.KeepLocalDays > 0 && f.LastWriteTime.Date > Today().AddDays(-Cfg.KeepLocalDays);
        }

        // 照片修改日期早于 UploadFromDate 的，程序完全不处理（不上传、不删除、不移动）
        static bool BeforeUploadFrom(FileInfo f)
        {
            return Cfg.UploadFromDate.HasValue && f.LastWriteTime.Date < Cfg.UploadFromDate.Value;
        }

        static string UploadFromText()
        {
            return Cfg.UploadFromDate.HasValue
                ? "只处理 " + Cfg.UploadFromDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " 及以后的照片（更早的不上传、不删除）"
                : "处理全部照片";
        }

        // 调用方需持有 StateLock
        static void MoveToFailed(FileInfo f, string rel)
        {
            if (Uploaded.Remove(rel)) RewriteUploaded();
            FailState st;
            if (!State.TryGetValue(rel, out st)) st = new FailState();
            string dest = Path.Combine(Cfg.FailedDir, rel);
            string dir = Path.GetDirectoryName(dest);
            long size = 0;
            try
            {
                size = new FileInfo(f.FullName).Length;
                Directory.CreateDirectory(dir);
                if (File.Exists(dest))
                {
                    dest = Path.Combine(dir, Path.GetFileNameWithoutExtension(dest) + "_" +
                        DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + Path.GetExtension(dest));
                }
                File.Move(f.FullName, dest);
            }
            catch (Exception ex)
            {
                Log("ERROR", "移动到失败目录出错（下一轮重试）: " + f.FullName + " -> " + dest + " : " + ex.Message);
                return;
            }

            try
            {
                StringBuilder sb = new StringBuilder();
                if (!File.Exists(FailedCsv)) sb.Append("时间,原路径,失败目录路径,文件大小(字节),失败次数,最后错误\r\n");
                sb.Append(string.Join(",", new[] {
                    CsvField(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)), CsvField(f.FullName), CsvField(dest),
                    CsvField(size.ToString(CultureInfo.InvariantCulture)), CsvField(st.Failures.ToString(CultureInfo.InvariantCulture)), CsvField(st.LastError)
                })).Append("\r\n");
                File.AppendAllText(FailedCsv, sb.ToString(), Utf8Bom);
            }
            catch (Exception ex)
            {
                Log("ERROR", "写入失败记录出错: " + ex.Message);
            }

            State.Remove(rel);
            SaveState();
            Log("ERROR", string.Format("失败 {0} 次，已移到失败目录: {1} -> {2}，最后错误: {3}", st.Failures, f.FullName, dest, st.LastError));
        }

        // 调用方需持有 StateLock
        static void RegisterFailure(FileInfo f, string rel, string message)
        {
            FailState st;
            if (!State.TryGetValue(rel, out st)) { st = new FailState(); State[rel] = st; }
            st.Failures++;
            st.LastError = message;
            st.NextRetry = DateTime.Now.AddSeconds(Cfg.RetryDelaySeconds);
            Log("WARN", string.Format("上传失败（第 {0}/{1} 次）: {2} : {3}", st.Failures, Cfg.MaxFailures, f.FullName, message));
            SaveState();
            if (st.Failures >= Cfg.MaxFailures) MoveToFailed(f, rel);
        }

        // ============================================================
        //  单张照片处理（在上传线程里执行，不直接改状态）
        // ============================================================
        static byte[] ReadFileBytes(string path)
        {
            // FileShare.Read：其它程序正在写这个文件时会打开失败，避免读到半张图
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                byte[] buf = new byte[fs.Length];
                int read = 0;
                while (read < buf.Length)
                {
                    int n = fs.Read(buf, read, buf.Length - read);
                    if (n <= 0) throw new IOException("读取文件不完整: " + path);
                    read += n;
                }
                return buf;
            }
        }

        // 401/403（密钥或权限）、5xx（服务端故障）→ 整体暂停，不计失败次数；
        // 其它错误再检查一次桶：桶也不可用 → 暂停；桶正常 → 算这张照片自己的失败
        static UploadResult ResolveError(S3Result resp, string what)
        {
            string msg = what + ": " + resp.Error;
            if (resp.Status == 401 || resp.Status == 403 || resp.Status >= 500) return UploadResult.Make(Outcome.Pause, msg, null, null);
            S3Result b = TestBucket();
            if (!b.Ok) return UploadResult.Make(Outcome.Pause, msg + "；桶检查: " + b.Error, null, null);
            return UploadResult.Make(Outcome.Fail, msg, null, null);
        }

        static UploadResult CompleteFile(FileInfo f, string key, long size)
        {
            FileInfo now = new FileInfo(f.FullName);
            if (!now.Exists) return UploadResult.Make(Outcome.Done, "", null, null);
            if (now.Length != size || now.LastWriteTimeUtc != f.LastWriteTimeUtc)
                return UploadResult.Make(Outcome.Skip, "", "WARN", "文件在上传期间被修改，暂不删除，下一轮重新处理: " + f.FullName);
            try
            {
                if (now.IsReadOnly) now.IsReadOnly = false;
                File.Delete(f.FullName);
            }
            catch (Exception ex)
            {
                return UploadResult.Make(Outcome.Skip, "", "WARN", "上传成功，但删除本地文件失败（下一轮重试）: " + f.FullName + " : " + ex.Message);
            }
            return UploadResult.Make(Outcome.Done, "", "INFO", string.Format("上传成功，已删除本地: {0} ({1} 字节)", key, size));
        }

        // keep=true：上传成功后保留本地；reason：重新上传的原因（写进日志），普通上传为 null
        static UploadResult DoUpload(FileInfo f, string rel, bool keep, string reason)
        {
            string key = ObjectKey(rel);
            if (f.Length > 1024L * 1024 * 1024)
                return UploadResult.Make(Outcome.Fail, "文件过大（" + f.Length + " 字节），超出单次上传上限 1GB", null, null);

            byte[] bytes;
            try
            {
                bytes = ReadFileBytes(f.FullName);
            }
            catch (FileNotFoundException) { return UploadResult.Make(Outcome.Skip, "文件已不存在", null, null); }
            catch (DirectoryNotFoundException) { return UploadResult.Make(Outcome.Skip, "文件已不存在", null, null); }
            catch (IOException ex)
            {
                int code = Marshal.GetHRForException(ex) & 0xFFFF;
                if (code == 32 || code == 33) return UploadResult.Make(Outcome.Skip, "文件被占用", null, null);
                return UploadResult.Make(Outcome.Fail, "读取文件失败: " + ex.Message, null, null);
            }
            catch (Exception ex)
            {
                return UploadResult.Make(Outcome.Fail, "读取文件失败: " + ex.Message, null, null);
            }

            // 只发一次 PUT。带 Content-MD5，MinIO 收到后自己重新计算比对，不一致直接拒收（HTTP 400 BadDigest）；
            // 返回 200 即表示内容完整写入。同名对象已存在时直接覆盖。
            byte[] md5 = Md5(bytes);
            S3Result put = S3("PUT", key, bytes, ContentType(f.Extension), Convert.ToBase64String(md5));
            if (!put.Ok) return ResolveError(put, "上传失败");

            UploadResult r;
            if (keep)
            {
                // 保留期内：不删本地，记入已上传清单，到期后核对 MinIO 再删
                FileInfo now = new FileInfo(f.FullName);
                if (!now.Exists || now.Length != bytes.Length || now.LastWriteTimeUtc != f.LastWriteTimeUtc)
                    return UploadResult.Make(Outcome.Skip, "", "WARN", "文件在上传期间被修改或删除，下一轮重新处理: " + f.FullName);
                r = UploadResult.Make(Outcome.Kept, "", "INFO", string.Format("{0}（本地保留，{1:MM-dd} 0 点后核对删除）: {2} ({3} 字节)",
                    reason ?? "上传成功", f.LastWriteTime.Date.AddDays(Cfg.KeepLocalDays), key, bytes.Length));
                r.Entry = new UploadedEntry();
                r.Entry.Size = bytes.Length;
                r.Entry.MtimeTicks = f.LastWriteTimeUtc.Ticks;
                r.Entry.Md5 = Hex(md5);
            }
            else
            {
                r = CompleteFile(f, key, bytes.Length);
                if (reason != null && r.Kind == Outcome.Done)
                    r.LogText = string.Format("{0}，已重新上传并删除本地: {1} ({2} 字节)", reason, key, bytes.Length);
            }
            r.Uploaded = true;
            return r;
        }

        // 已上传、过了保留期的照片：先 HEAD 核对 MinIO 上的大小和 MD5，一致才删除本地；
        // MinIO 上没有或不一致 → 重新上传后再删
        static UploadResult VerifyAndDelete(FileInfo f, string rel, UploadedEntry e)
        {
            string key = ObjectKey(rel);
            S3Result h = S3("HEAD", key, null, null, null);
            if (h.Ok && h.Length == e.Size && h.ETag.Trim('"').ToLowerInvariant() == e.Md5)
            {
                UploadResult r = CompleteFile(f, key, e.Size);
                if (r.Kind == Outcome.Done)
                {
                    r.Verified = true;
                    r.LogText = null;   // 核对删除不逐张记日志，只在本轮汇总里记数量
                }
                return r;
            }
            if (!h.Ok && h.Status != 404) return ResolveError(h, "删除前核对 MinIO 失败");
            string why = h.Ok ? "删除前核对发现 MinIO 上的对象与本地不一致" : "删除前核对发现 MinIO 上没有该对象";
            return DoUpload(f, rel, false, why);
        }

        static UploadResult ProcessJob(Job j)
        {
            if (j.Indexed != null) return VerifyAndDelete(j.File, j.Rel, j.Indexed);
            return DoUpload(j.File, j.Rel, j.Keep, null);
        }

        // ============================================================
        //  一轮扫描（多线程上传）
        // ============================================================
        static void ScanRound()
        {
            if (!Directory.Exists(Cfg.SourceDir)) return;
            DateTime cutoff = DateTime.Now.AddSeconds(-Cfg.StableSeconds);
            List<FileInfo> all = new List<FileInfo>();
            CollectFiles(Cfg.SourceDir, all);
            List<FileInfo> files = all
                .Where(x => Cfg.Extensions.Contains(x.Extension.ToLowerInvariant()) && x.LastWriteTime < cutoff && !BeforeUploadFrom(x))
                .OrderBy(x => x.LastWriteTime).ToList();

            // 新照片先传，到期的核对删除排在后面，避免 0 点集中核对时耽误新照片上传
            List<Job> uploads = new List<Job>(), verifies = new List<Job>();
            bool indexChanged = false;
            lock (StateLock)
            {
                // 已上传清单里，本地已经不存在的照片（被人手动删了或挪走了）直接移除
                HashSet<string> existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (FileInfo x in all) existing.Add(RelPath(x.FullName));
                foreach (string k in Uploaded.Keys.ToList())
                    if (!existing.Contains(k)) { Uploaded.Remove(k); indexChanged = true; }

                foreach (FileInfo f in files)
                {
                    string rel = RelPath(f.FullName);
                    bool keep = InKeepPeriod(f);
                    UploadedEntry ue;
                    if (Uploaded.TryGetValue(rel, out ue))
                    {
                        if (ue.Size != f.Length || ue.MtimeTicks != f.LastWriteTimeUtc.Ticks)
                        {
                            Uploaded.Remove(rel); indexChanged = true; ue = null;   // 上传后文件被改过，按新照片重新上传
                        }
                        else if (keep) continue;   // 已上传，还在保留期内，什么都不做
                    }

                    FailState st;
                    if (State.TryGetValue(rel, out st))
                    {
                        if (st.Failures >= Cfg.MaxFailures) { MoveToFailed(f, rel); continue; }
                        if (st.NextRetry > DateTime.Now) continue;
                    }

                    Job j = new Job();
                    j.File = f; j.Rel = rel; j.Keep = keep; j.Indexed = ue;
                    if (ue == null) uploads.Add(j); else verifies.Add(j);
                }
            }
            List<Job> queue = uploads.Concat(verifies).ToList();

            int uploaded = 0, kept = 0, verified = 0, failed = 0;
            if (queue.Count > 0)
            {
                int pauseFlag = 0;
                ParallelOptions po = new ParallelOptions();
                po.MaxDegreeOfParallelism = Cfg.UploadThreads;
                Parallel.ForEach(Partitioner.Create(queue, EnumerablePartitionerOptions.NoBuffering), po, (j, loop) =>
                {
                    if (Thread.VolatileRead(ref pauseFlag) != 0) { loop.Stop(); return; }
                    FileInfo f = j.File;
                    string rel = j.Rel;
                    UploadResult r;
                    try { r = ProcessJob(j); }
                    catch (Exception ex) { r = UploadResult.Make(Outcome.Fail, "处理异常: " + ex.Message, null, null); }

                    lock (StateLock)
                    {
                        if (r.LogText != null) Log(r.LogLevel, r.LogText);
                        if (r.Uploaded) uploaded++;
                        switch (r.Kind)
                        {
                            case Outcome.Done:
                                if (r.Verified) verified++;
                                LastPauseMsg = "";
                                if (State.Remove(rel)) SaveState();
                                if (Uploaded.Remove(rel)) indexChanged = true;
                                break;
                            case Outcome.Kept:
                                kept++;
                                LastPauseMsg = "";
                                if (State.Remove(rel)) SaveState();
                                Uploaded[rel] = r.Entry;
                                AppendUploaded(rel, r.Entry);
                                break;
                            case Outcome.Fail:
                                failed++;
                                RegisterFailure(f, rel, r.Error);
                                break;
                            case Outcome.Pause:
                                // 不再派发新照片，正在传的几张结束后退出本轮；同样的错误只记一次日志
                                Interlocked.Exchange(ref pauseFlag, 1);
                                if (r.Error != LastPauseMsg)
                                {
                                    Log("WARN", "MinIO 不可用或拒绝访问，暂停上传（不计入失败次数），稍后自动重试: " + r.Error);
                                    LastPauseMsg = r.Error;
                                }
                                loop.Stop();
                                break;
                        }
                    }
                });
            }

            // 清理已经不存在的文件的失败计数；已上传清单有变化时整体重写一次
            lock (StateLock)
            {
                bool removed = false;
                foreach (string k in State.Keys.ToList())
                {
                    if (!File.Exists(Path.Combine(Cfg.SourceDir, k))) { State.Remove(k); removed = true; }
                }
                if (removed) SaveState();
                if (indexChanged) RewriteUploaded();
            }

            if (uploaded > 0 || verified > 0 || failed > 0)
            {
                if (Cfg.KeepLocalDays > 0)
                    Log("INFO", string.Format("本轮完成: 上传 {0} 张（其中本地保留 {1} 张），核对 MinIO 后删除本地 {2} 张，失败 {3} 张，扫描到 {4} 张（{5} 线程）",
                        uploaded, kept, verified, failed, files.Count, Cfg.UploadThreads));
                else
                    Log("INFO", string.Format("本轮完成 {0} 张，失败 {1} 张，扫描到 {2} 张（{3} 线程）", uploaded, failed, files.Count, Cfg.UploadThreads));
            }
        }

        // ============================================================
        //  定期维护：删除传空的旧目录、按天压缩日志
        // ============================================================
        static void Maintenance()
        {
            if (Cfg.CleanEmptyDirs && Directory.Exists(Cfg.SourceDir))
            {
                DateTime minCreated = DateTime.Now.AddHours(-Cfg.EmptyDirMinAgeHours);
                List<string> dirs = new List<string>();
                CollectDirs(Cfg.SourceDir, dirs);
                foreach (string d in dirs.OrderByDescending(x => x.Length))
                {
                    try
                    {
                        if (Directory.GetCreationTime(d) < minCreated && !Directory.EnumerateFileSystemEntries(d).Any())
                        {
                            Directory.Delete(d, false);
                            Log("INFO", "已删除空目录: " + d);
                        }
                    }
                    catch { }
                }
            }
            CompressOldLogs();
        }

        // 把今天之前的 *-yyyyMMdd.log 按天打包成 logs-yyyyMMdd.zip，成功后删除原文件；
        // 超过 LogRetentionDays 天的压缩包自动删除
        static void CompressOldLogs()
        {
            if (!Directory.Exists(LogDir)) return;
            string today = DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            Regex re = new Regex(@"-(\d{8})\.log$", RegexOptions.IgnoreCase);
            SortedDictionary<string, List<string>> groups = new SortedDictionary<string, List<string>>();
            foreach (string f in Directory.GetFiles(LogDir, "*.log"))
            {
                Match m = re.Match(Path.GetFileName(f));
                if (!m.Success || string.CompareOrdinal(m.Groups[1].Value, today) >= 0) continue;
                List<string> list;
                if (!groups.TryGetValue(m.Groups[1].Value, out list)) { list = new List<string>(); groups[m.Groups[1].Value] = list; }
                list.Add(f);
            }

            foreach (KeyValuePair<string, List<string>> g in groups)
            {
                string zipPath = Path.Combine(LogDir, "logs-" + g.Key + ".zip");
                try
                {
                    bool exists = File.Exists(zipPath);
                    using (ZipArchive zip = ZipFile.Open(zipPath, exists ? ZipArchiveMode.Update : ZipArchiveMode.Create))
                    {
                        foreach (string f in g.Value)
                        {
                            string name = Path.GetFileName(f);
                            if (exists)
                            {
                                ZipArchiveEntry old = zip.GetEntry(name);
                                if (old != null) old.Delete();
                            }
                            zip.CreateEntryFromFile(f, name, CompressionLevel.Optimal);
                        }
                    }
                    foreach (string f in g.Value) { try { File.Delete(f); } catch { } }
                    Log("INFO", string.Format("已将 {0} 个日志文件压缩到 {1}", g.Value.Count, zipPath));
                }
                catch (Exception ex)
                {
                    Log("WARN", "压缩 " + g.Key + " 的日志失败（原日志保留，下次再试）: " + ex.Message);
                }
            }

            if (Cfg.LogRetentionDays > 0)
            {
                string minDay = DateTime.Now.AddDays(-Cfg.LogRetentionDays).ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                Regex zre = new Regex(@"^logs-(\d{8})\.zip$", RegexOptions.IgnoreCase);
                foreach (string z in Directory.GetFiles(LogDir, "logs-*.zip"))
                {
                    Match m = zre.Match(Path.GetFileName(z));
                    if (m.Success && string.CompareOrdinal(m.Groups[1].Value, minDay) < 0)
                    {
                        try { File.Delete(z); Log("INFO", "已删除过期日志压缩包: " + Path.GetFileName(z)); } catch { }
                    }
                }
            }
        }

        // ============================================================
        //  check：连通性和权限检查
        // ============================================================
        static int RunCheck()
        {
            bool ok = true;
            Console.WriteLine("配置文件    : " + Cfg.FilePath);
            Console.WriteLine("Endpoint    : " + Cfg.Endpoint);
            Console.WriteLine("Bucket      : " + Cfg.Bucket);
            Console.WriteLine("ObjectPrefix: " + Cfg.ObjectPrefix);
            Console.WriteLine("SourceDir   : " + Cfg.SourceDir);
            Console.WriteLine("FailedDir   : " + Cfg.FailedDir);
            Console.WriteLine("DataDir     : " + Cfg.DataDir);
            Console.WriteLine("上传线程    : " + Cfg.UploadThreads);
            Console.WriteLine("本地保留    : " + (Cfg.KeepLocalDays > 0
                ? Cfg.KeepLocalDays + " 天（当天照片上传后保留，0 点后核对 MinIO 再删除）"
                : "不保留（上传成功立即删除）"));
            Console.WriteLine("上传范围    : " + UploadFromText());
            Console.WriteLine();

            if (KeysNotFilled(Cfg)) { Say("[失败] config.json 中的 AccessKey / SecretKey 还没有填写", ConsoleColor.Red); return 1; }

            S3Result b = TestBucket();
            if (b.Ok) Say("[通过] 桶可访问（网络、密钥、时间均正常）", ConsoleColor.Green);
            else
            {
                Say("[失败] 桶不可访问: " + b.Error, ConsoleColor.Red);
                Console.WriteLine("       常见原因: 地址/端口不对（要用 9000 API 端口）、密钥错误、桶名错误、本机时间与服务器相差超过 15 分钟");
                return 1;
            }

            string testKey = ObjectKey(".uploader-check\\" + Environment.MachineName + ".txt");
            byte[] body = Utf8NoBom.GetBytes("uploader check " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            byte[] md5 = Md5(body);
            S3Result put = S3("PUT", testKey, body, "text/plain", Convert.ToBase64String(md5));
            if (put.Ok)
            {
                S3Result v = S3("HEAD", testKey, null, null, null);
                if (v.Ok && v.Length == body.Length && v.ETag.Trim('"').ToLowerInvariant() == Hex(md5))
                    Say("[通过] 上传并校验测试对象: " + Cfg.Bucket + "/" + testKey, ConsoleColor.Green);
                else { Say("[失败] 测试对象上传后校验不通过: " + v.Error, ConsoleColor.Red); ok = false; }
            }
            else
            {
                Say("[失败] 上传测试对象: " + put.Error + "（检查该账号是否有这个目录的上传权限）", ConsoleColor.Red);
                ok = false;
            }

            if (Directory.Exists(Cfg.SourceDir))
            {
                List<FileInfo> all = new List<FileInfo>();
                CollectFiles(Cfg.SourceDir, all);
                List<FileInfo> photos = all.Where(x => Cfg.Extensions.Contains(x.Extension.ToLowerInvariant())).ToList();
                List<FileInfo> pending = photos.Where(x => !BeforeUploadFrom(x)).ToList();
                double gb = pending.Sum(x => (double)x.Length) / (1024.0 * 1024 * 1024);
                Console.WriteLine(string.Format("[信息] 待上传照片 {0} 张，约 {1:N2} GB", pending.Count, gb));
                if (Cfg.UploadFromDate.HasValue)
                {
                    List<FileInfo> skipped = photos.Where(BeforeUploadFrom).ToList();
                    double sgb = skipped.Sum(x => (double)x.Length) / (1024.0 * 1024 * 1024);
                    Console.WriteLine(string.Format("[信息] 早于 {0} 的照片 {1} 张，约 {2:N2} GB，不上传、不删除",
                        Cfg.UploadFromDate.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), skipped.Count, sgb));
                }
            }
            else Say("[警告] SourceDir 不存在: " + Cfg.SourceDir, ConsoleColor.Yellow);

            return ok ? 0 : 1;
        }

        // ============================================================
        //  run / once：主循环
        // ============================================================
        static Mutex CreateMutex()
        {
            try { return new Mutex(false, MutexName); }
            catch { return new Mutex(false, MutexNameLocal); }
        }

        static int Start(string cfgPath, bool once)
        {
            if (!InitConfig(cfgPath, false)) return 1;

            Mutex mutex = CreateMutex();
            bool hasLock = false;
            try { hasLock = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { hasLock = true; }   // 上一个实例异常退出，锁已转给本实例
            if (!hasLock)
            {
                Log("INFO", "已有一个上传程序在运行，本实例退出");
                mutex.Dispose();
                return 0;
            }

            try
            {
                lock (StateLock) { LoadState(); LoadUploaded(); }
                Log("INFO", string.Format("启动: {0} -> {1}/{2}/{3}，失败目录 {4}，扫描间隔 {5} 秒，上传线程 {6} 个，{7}，{8}",
                    Cfg.SourceDir, Cfg.Endpoint, Cfg.Bucket, Cfg.ObjectPrefix, Cfg.FailedDir, Cfg.ScanIntervalSeconds, Cfg.UploadThreads,
                    Cfg.KeepLocalDays > 0
                        ? string.Format("本地保留 {0} 天（已上传待核对 {1} 张）", Cfg.KeepLocalDays, Uploaded.Count)
                        : "上传成功立即删除本地",
                    UploadFromText()));

                DateTime lastMaintenance = DateTime.MinValue;
                while (true)
                {
                    try
                    {
                        S3Result b = TestBucket();
                        if (!b.Ok)
                        {
                            if (!Paused) Log("WARN", "MinIO 不可用，暂停上传: " + b.Error);
                            Paused = true;
                        }
                        else
                        {
                            if (Paused) Log("INFO", "MinIO 已恢复，继续上传");
                            Paused = false;
                            ScanRound();
                        }

                        if ((DateTime.Now - lastMaintenance).TotalHours >= 1)
                        {
                            Maintenance();
                            lastMaintenance = DateTime.Now;
                        }

                    }
                    catch (Exception ex)
                    {
                        Log("ERROR", "本轮异常: " + ex.Message);
                    }

                    if (once) break;
                    Thread.Sleep(Cfg.ScanIntervalSeconds * 1000);
                }
                return 0;
            }
            catch (Exception ex)
            {
                Log("ERROR", "程序异常退出（看门狗会自动重启）: " + ex.Message);
                return 1;
            }
            finally
            {
                try { mutex.ReleaseMutex(); } catch { }
                mutex.Dispose();
            }
        }

        // ============================================================
        //  计划任务（通过系统自带的 Task Scheduler COM 接口，不依赖 PowerShell，也不受系统语言影响）
        // ============================================================
        static dynamic TaskFolder()
        {
            Type t = Type.GetTypeFromProgID("Schedule.Service");
            dynamic svc = Activator.CreateInstance(t);
            svc.Connect();
            return svc.GetFolder("\\");
        }

        static dynamic GetTask(string name)
        {
            try { return TaskFolder().GetTask(name); }
            catch { return null; }
        }

        static string TaskStateText(int state)
        {
            switch (state)
            {
                case 1: return "已禁用";
                case 2: return "排队中";
                case 3: return "就绪";
                case 4: return "正在运行";
                default: return "未知";
            }
        }

        static string X(string s) { return SecurityElement.Escape(s); }

        static string TaskXml(string description, string triggers, string execLimit, string arguments)
        {
            return
"<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
"<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
"  <RegistrationInfo><Description>" + X(description) + "</Description></RegistrationInfo>\r\n" +
"  <Triggers>" + triggers + "</Triggers>\r\n" +
"  <Principals><Principal id=\"Author\"><UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\r\n" +
"  <Settings>\r\n" +
"    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n" +
"    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n" +
"    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n" +
"    <StartWhenAvailable>true</StartWhenAvailable>\r\n" +
"    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>\r\n" +
"    <AllowStartOnDemand>true</AllowStartOnDemand>\r\n" +
"    <Enabled>true</Enabled>\r\n" +
"    <ExecutionTimeLimit>" + execLimit + "</ExecutionTimeLimit>\r\n" +
"    <Priority>5</Priority>\r\n" +
"  </Settings>\r\n" +
"  <Actions Context=\"Author\"><Exec>\r\n" +
"    <Command>" + X(ExePath) + "</Command>\r\n" +
"    <Arguments>" + X(arguments) + "</Arguments>\r\n" +
"    <WorkingDirectory>" + X(ExeDir) + "</WorkingDirectory>\r\n" +
"  </Exec></Actions>\r\n" +
"</Task>";
        }

        static void RegisterTask(string name, string xml)
        {
            // 6 = TASK_CREATE_OR_UPDATE；5 = TASK_LOGON_SERVICE_ACCOUNT
            TaskFolder().RegisterTask(name, xml, 6, "SYSTEM", null, 5, null);
        }

        static void DeleteTask(string name)
        {
            dynamic t = GetTask(name);
            if (t == null) return;
            try { t.Stop(0); } catch { }
            TaskFolder().DeleteTask(name, 0);
            Console.WriteLine("已删除计划任务 " + name);
        }

        static void KillOtherInstances()
        {
            int self = Process.GetCurrentProcess().Id;
            foreach (Process p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExePath)))
            {
                try
                {
                    if (p.Id == self) continue;
                    p.Kill();
                    p.WaitForExit(5000);
                    Console.WriteLine("已结束进程 " + p.Id);
                }
                catch { }
            }
        }

        static bool IsAdmin()
        {
            return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        }

        static int Install(string cfgPath, bool skipCheck)
        {
            if (!IsAdmin()) { Say("[失败] 请用“以管理员身份运行”打开命令行窗口后再执行 install", ConsoleColor.Red); return 1; }
            if (!InitConfig(cfgPath, true)) return 1;
            if (KeysNotFilled(Cfg)) { Say("[失败] " + cfgPath + " 中的 AccessKey / SecretKey 还没有填写", ConsoleColor.Red); return 1; }

            // 1. 先做连通性和权限检查，不通过就不安装
            if (skipCheck) Say("[警告] 已跳过连通性检查（--skip-check），仅适用于测试", ConsoleColor.Yellow);
            else
            {
                Console.WriteLine("===== 连通性检查 =====");
                if (RunCheck() != 0) { Say("检查未通过，请按上面的提示修改配置文件后重新执行 install", ConsoleColor.Red); return 1; }
                Console.WriteLine();
            }

            // 2. 配置文件里有密钥：只允许 SYSTEM 和管理员组读取（用 SID，兼容中文系统）
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("icacls.exe",
                    "\"" + cfgPath + "\" /inheritance:r /grant:r *S-1-5-18:(F) *S-1-5-32-544:(F)");
                psi.UseShellExecute = false; psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); p.WaitForExit();
                    if (p.ExitCode != 0) Say("[警告] 配置文件权限收紧失败，请手动检查", ConsoleColor.Yellow);
                }
            }
            catch (Exception ex) { Say("[警告] 配置文件权限收紧失败: " + ex.Message, ConsoleColor.Yellow); }

            string cfgArg = " --config \"" + cfgPath + "\"";

            // 3. 上传任务：开机 30 秒后启动，常驻。异常退出后的重启交给看门狗，每次重启都有记录
            RegisterTask(TaskName, TaskXml("开机自动把本地照片上传到 MinIO，上传成功后删除本地文件",
                "<BootTrigger><Enabled>true</Enabled><Delay>PT30S</Delay></BootTrigger>", "PT0S", "run" + cfgArg));

            // 4. 看门狗任务：每 1 分钟一次，无限期重复（重启电脑后继续按间隔运行）
            string start = DateTime.Now.AddMinutes(1).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
            RegisterTask(WatchdogTaskName, TaskXml("每 1 分钟检查 " + TaskName + " 是否在运行，未运行则自动重启并记录",
                "<TimeTrigger><Repetition><Interval>PT1M</Interval><StopAtDurationEnd>false</StopAtDurationEnd></Repetition>" +
                "<StartBoundary>" + start + "</StartBoundary><Enabled>true</Enabled></TimeTrigger>", "PT5M", "watchdog" + cfgArg));

            // 5. 结束可能残留的旧进程，立即启动
            KillOtherInstances();
            GetTask(TaskName).Run(null);
            Thread.Sleep(5000);

            Console.WriteLine();
            Console.WriteLine("上传任务 " + TaskName + " 状态: " + TaskStateText((int)GetTask(TaskName).State));
            Console.WriteLine("看门狗任务 " + WatchdogTaskName + " 状态: " + TaskStateText((int)GetTask(WatchdogTaskName).State) +
                "（每 1 分钟运行一次，平时显示“就绪”是正常的）");
            Console.WriteLine("日志目录: " + LogDir);
            return 0;
        }

        static int Uninstall()
        {
            if (!IsAdmin()) { Say("[失败] 请用“以管理员身份运行”打开命令行窗口后再执行 uninstall", ConsoleColor.Red); return 1; }
            DeleteTask(WatchdogTaskName);   // 先删看门狗，否则它会把上传程序重新拉起来
            DeleteTask(TaskName);
            KillOtherInstances();
            Console.WriteLine("卸载完成（照片、日志和配置文件都保留）");
            return 0;
        }

        // ============================================================
        //  watchdog：上传程序不在就重启，并写重启日志
        // ============================================================
        static bool IsUploaderRunning()
        {
            foreach (string name in new[] { MutexName, MutexNameLocal })
            {
                try
                {
                    Mutex m;
                    if (Mutex.TryOpenExisting(name, out m)) { m.Dispose(); return true; }
                }
                catch (UnauthorizedAccessException) { return true; }   // 互斥体存在但无权打开，说明程序在运行
                catch { }
            }
            return false;
        }

        static int Watchdog(string cfgPath)
        {
            // 日志目录与上传程序一致；配置读不出来时用默认目录
            try { Config c = LoadConfig(cfgPath); LogDir = Path.Combine(c.DataDir, "logs"); } catch { }

            if (IsUploaderRunning()) return 0;

            dynamic task = GetTask(TaskName);
            if (task == null)
            {
                WriteRestartLog("[错误] 检测到上传程序未运行，但计划任务 " + TaskName + " 不存在，无法重启。请重新执行 install");
                return 1;
            }
            if (!(bool)task.Enabled) return 0;   // 管理员手动禁用，不干预

            string detail;
            try
            {
                DateTime last = (DateTime)task.LastRunTime;
                detail = string.Format("上次启动 {0:yyyy-MM-dd HH:mm:ss}，上次退出码 0x{1:X8}", last, (int)task.LastTaskResult);
            }
            catch { detail = "无上次运行信息"; }

            // 任务显示“正在运行”但进程已不在（例如卡死后被结束），先停掉，否则新的启动会被忽略
            try { if ((int)task.State == 4) { task.Stop(0); Thread.Sleep(2000); } } catch { }

            try { task.Run(null); }
            catch (Exception ex)
            {
                WriteRestartLog("[重启失败] 检测到上传程序未运行，启动计划任务出错: " + ex.Message + "（" + detail + "）");
                return 1;
            }

            bool ok = false;
            for (int i = 0; i < 15 && !ok; i++) { Thread.Sleep(2000); ok = IsUploaderRunning(); }
            if (ok) WriteRestartLog("[重启成功] 检测到上传程序未运行，已自动重启（" + detail + "）");
            else WriteRestartLog("[重启失败] 检测到上传程序未运行，已尝试重启但 30 秒内未检测到程序（" + detail + "），1 分钟后再试。请查看 uploader 日志");
            return ok ? 0 : 1;
        }

        // ============================================================
        //  selftest：用 AWS 官方文档的示例数据验证签名算法
        // ============================================================
        static int SelfTest()
        {
            const string host = "examplebucket.s3.amazonaws.com", date = "20130524T000000Z", region = "us-east-1";
            const string ak = "AKIAIOSFODNN7EXAMPLE", sk = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";
            bool a = SigV4("GET", "/", "lifecycle=", host, EmptySha256, date, region, ak, sk)
                .EndsWith("Signature=fea454ca298b7da1c68078a5d1bdbfbbe0d65c699e0f91ac7a200a0136783543");
            bool b = SigV4("GET", "/", "max-keys=2&prefix=J", host, EmptySha256, date, region, ak, sk)
                .EndsWith("Signature=34b48302e7b5fa45bde8084f4b7868a86f0a534bc59db6670ed5711ef69dc6f7");
            string enc = EncodePath("photos/202610/图 a+b(1).jpg");
            Console.WriteLine("sigv4 lifecycle : " + a);
            Console.WriteLine("sigv4 listobject: " + b);
            Console.WriteLine("encode          : " + enc);
            return a && b ? 0 : 1;
        }
    }
}
