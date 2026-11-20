// Test host: runs the offline server outside Windows PowerShell (for example with Mono on Linux) so the replies
// can be tested with replay.py / call.py. The game's server addresses are not added (no Windows networking).
//
//   mcs -langversion:5 -r:System.Web.Extensions.dll -out:server.exe offline/*.cs tools/dev/Host.cs
//   CDN=<path to cdn/Files> mono server.exe <folder with data/ (a copy of offline/)> 45558 45559 [M_stage.json]
public static class Host { public static void Main(string[] a) { OfflineServer.CdnFilesDir = System.Environment.GetEnvironmentVariable("CDN"); OfflineServer.Run(a[0], int.Parse(a[1]), int.Parse(a[2]), a.Length > 3 ? a[3] : null, false); } }
