using System;
using System.Diagnostics;
using System.IO;
using System.Text;

class Program {
  static int Main(string[] args) {
    string dir = AppDomain.CurrentDomain.BaseDirectory;
    string real = Path.Combine(dir, "HellForge.LocalBackend.real.exe");
    string spacewar = Path.Combine(dir, "steam_api64.spacewar.dll");
    try {
      Environment.SetEnvironmentVariable("SteamAppId", "480");
      Environment.SetEnvironmentVariable("SteamGameId", "480");

      for (int i = 0; i < args.Length; i++) {
        string linkPath = null;
        if (args[i].StartsWith("steam:", StringComparison.OrdinalIgnoreCase))
          linkPath = args[i].Substring(6);
        else if (args[i].Equals("--link", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length &&
                 args[i + 1].StartsWith("steam:", StringComparison.OrdinalIgnoreCase))
          linkPath = args[i + 1].Substring(6);

        if (linkPath != null && File.Exists(spacewar)) {
          string full = Path.GetFullPath(linkPath);
          string linkDir = Path.GetDirectoryName(full);
          if (!string.IsNullOrEmpty(linkDir)) {
            Directory.CreateDirectory(linkDir);
            // Spacewar (480) steam-link helper for --pipe / peer lobby setups
            File.Copy(spacewar, full, true);
            File.WriteAllText(Path.Combine(linkDir, "steam_appid.txt"), "480", Encoding.ASCII);
            File.WriteAllText(Path.Combine(linkDir, "union-crax.ini"),
              "[Settings]\r\nAppId=480\r\nEmulateTicket=true\r\n", Encoding.ASCII);
          }
        }
      }

      var psi = new ProcessStartInfo();
      psi.FileName = real;
      psi.Arguments = QuoteArgs(args);
      psi.UseShellExecute = false;
      psi.RedirectStandardOutput = true;
      psi.RedirectStandardError = true;
      psi.CreateNoWindow = true;
      psi.WorkingDirectory = Environment.CurrentDirectory;
      try {
        psi.EnvironmentVariables["SteamAppId"] = "480";
        psi.EnvironmentVariables["SteamGameId"] = "480";
      } catch {}

      var p = new Process();
      p.StartInfo = psi;
      p.OutputDataReceived += (s, e) => { if (e.Data != null) { Console.Out.WriteLine(e.Data); Console.Out.Flush(); } };
      p.ErrorDataReceived += (s, e) => { if (e.Data != null) { Console.Error.WriteLine(e.Data); Console.Error.Flush(); } };
      p.Start();
      p.BeginOutputReadLine();
      p.BeginErrorReadLine();
      p.WaitForExit();
      return p.ExitCode;
    } catch (Exception ex) {
      Console.Error.WriteLine(ex.Message);
      return 1;
    }
  }
  static string QuoteArgs(string[] args) {
    var sb = new StringBuilder();
    for (int i = 0; i < args.Length; i++) {
      if (i > 0) sb.Append(' ');
      string a = args[i];
      if (a.IndexOfAny(new[]{' ','\t','"'}) < 0) { sb.Append(a); continue; }
      sb.Append('"');
      foreach (char c in a) { if (c == '"') sb.Append('\\'); sb.Append(c); }
      sb.Append('"');
    }
    return sb.ToString();
  }
}
