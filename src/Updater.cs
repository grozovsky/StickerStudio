using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace StickerStudio
{
    // Автообновление с GitHub Releases (grozovsky/StickerStudio).
    // Проверка — тихо в фоне при запуске; скачивание и подмена exe —
    // только после явного согласия пользователя.
    static class UpdateManager
    {
        const string ApiLatest =
            "https://api.github.com/repos/grozovsky/StickerStudio/releases/latest";
        const string AssetName = "StickerStudio-Standalone.exe";

        public static Version CurrentVersion
        {
            get { return Assembly.GetExecutingAssembly().GetName().Version; }
        }

        // onUpdate(новая версия, url exe) дергается ТОЛЬКО если релиз новее
        public static void CheckInBackground(Control invoker,
            Action<Version, string> onUpdate)
        {
            Thread t = new Thread(delegate()
            {
                try
                {
                    ServicePointManager.SecurityProtocol |=
                        SecurityProtocolType.Tls12;
                    string json;
                    using (WebClient wc = new WebClient())
                    {
                        wc.Headers[HttpRequestHeader.UserAgent] =
                            "StickerStudio-Updater";
                        json = wc.DownloadString(ApiLatest);
                    }

                    Match mTag = Regex.Match(json,
                        "\"tag_name\"\\s*:\\s*\"v?([0-9][0-9.]*)\"");
                    Match mUrl = Regex.Match(json,
                        "\"browser_download_url\"\\s*:\\s*\"([^\"]*" +
                        Regex.Escape(AssetName) + ")\"");
                    if (!mTag.Success || !mUrl.Success) return;

                    Version latest = ParseVersion(mTag.Groups[1].Value);
                    if (latest == null) return;
                    Version current = CurrentVersion;
                    if (latest <= new Version(current.Major, current.Minor,
                        Math.Max(0, current.Build))) return;

                    string url = mUrl.Groups[1].Value;
                    if (invoker != null && invoker.IsHandleCreated)
                    {
                        invoker.BeginInvoke((MethodInvoker)delegate
                        {
                            onUpdate(latest, url);
                        });
                    }
                }
                catch { /* нет сети/лимит API — просто молчим */ }
            });
            t.IsBackground = true;
            t.Name = "StickerStudio update check";
            t.Start();
        }

        static Version ParseVersion(string s)
        {
            try
            {
                string[] p = s.Split('.');
                int major = p.Length > 0 ? int.Parse(p[0]) : 0;
                int minor = p.Length > 1 ? int.Parse(p[1]) : 0;
                int build = p.Length > 2 ? int.Parse(p[2]) : 0;
                return new Version(major, minor, build);
            }
            catch { return null; }
        }

        // Качает новый exe и подменяет текущий через вспомогательный
        // PowerShell-скрипт (ждёт выхода процесса, двигает файл, перезапускает)
        public static void DownloadAndApply(string url,
            Action<int> progress, Action<string> onError)
        {
            string target = Application.ExecutablePath;
            string temp = Path.Combine(Path.GetTempPath(),
                "StickerStudio-update-" + Guid.NewGuid().ToString("N") + ".exe");

            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            WebClient wc = new WebClient();
            wc.Headers[HttpRequestHeader.UserAgent] = "StickerStudio-Updater";
            wc.DownloadProgressChanged += delegate(object s,
                DownloadProgressChangedEventArgs e)
            {
                if (progress != null) progress(e.ProgressPercentage);
            };
            wc.DownloadFileCompleted += delegate(object s,
                System.ComponentModel.AsyncCompletedEventArgs e)
            {
                wc.Dispose();
                if (e.Cancelled || e.Error != null)
                {
                    try { File.Delete(temp); } catch { }
                    if (onError != null)
                        onError(e.Error != null ? e.Error.Message : "отменено");
                    return;
                }

                // здравый смысл: exe меньше 5 МБ — это не наш standalone
                try
                {
                    if (new FileInfo(temp).Length < 5L * 1024 * 1024)
                    {
                        File.Delete(temp);
                        if (onError != null) onError("файл обновления неполный");
                        return;
                    }
                }
                catch { }

                try
                {
                    string script =
                        "$ErrorActionPreference='SilentlyContinue';" +
                        "Wait-Process -Id " + Process.GetCurrentProcess().Id + ";" +
                        "Start-Sleep -Milliseconds 400;" +
                        "for($i=0;$i -lt 20;$i++){" +
                        "try{Move-Item -LiteralPath '" + temp.Replace("'", "''") +
                        "' -Destination '" + target.Replace("'", "''") +
                        "' -Force -ErrorAction Stop;break}" +
                        "catch{Start-Sleep -Milliseconds 500}};" +
                        "Start-Process -FilePath '" + target.Replace("'", "''") + "'";
                    string encoded = Convert.ToBase64String(
                        Encoding.Unicode.GetBytes(script));

                    ProcessStartInfo psi = new ProcessStartInfo("powershell.exe",
                        "-NoProfile -WindowStyle Hidden -EncodedCommand " + encoded);
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                    Process.Start(psi);
                    Application.Exit();
                }
                catch (Exception ex)
                {
                    if (onError != null) onError(ex.Message);
                }
            };
            wc.DownloadFileAsync(new Uri(url), temp);
        }
    }
}
