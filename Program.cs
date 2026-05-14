using System.Collections;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static class Program
{
    private const string DefaultLauncherRoot = @"C:\Users\ban2f\Desktop\Reboot-Launcher-10.0.9\Reboot-Launcher-10.0.9";
    private const string DefaultFortniteRoot = @"C:\Users\ban2f\Downloads\24.20 FreeBuild\24.20 FreeBuild";

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (!OperatingSystem.IsWindows())
        {
            Log.Error("AutoHost est prévu pour Windows x64 uniquement.");
            return 1;
        }

        var configPath = GetArgValue(args, "--config") ?? "autohost.json";
        var once = args.Any(arg => arg.Equals("--once", StringComparison.OrdinalIgnoreCase));
        var initOnly = args.Any(arg => arg.Equals("--init", StringComparison.OrdinalIgnoreCase));
        var checkOnly = args.Any(arg => arg.Equals("--check", StringComparison.OrdinalIgnoreCase));

        try
        {
            var config = await AutoHostConfig.LoadOrCreateAsync(configPath);
            if (initOnly)
            {
                Log.Ok($"Config créée/validée : {Path.GetFullPath(configPath)}");
                return 0;
            }

            WarnIfNotAdmin();

            using var shutdown = new CancellationTokenSource();
            Console.CancelKeyPress += (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                Log.Warn("Arrêt demandé, fermeture propre des process...");
                shutdown.Cancel();
            };

            await using var host = new FortniteAutoHost(config);
            await host.PrepareAsync(shutdown.Token);
            if (checkOnly)
            {
                Log.Ok("Check OK : chemins, backend et DLLs résolus.");
                return 0;
            }

            await host.StartBackendAsync(shutdown.Token);

            var run = 1;
            while (!shutdown.IsCancellationRequested)
            {
                Log.Section($"Session host #{run}");
                var result = await host.RunOneSessionAsync(shutdown.Token);
                if (shutdown.IsCancellationRequested)
                {
                    break;
                }

                var shouldRestart = result switch
                {
                    SessionResult.MatchEnded => config.AutoRestart && !once,
                    SessionResult.Crashed => config.RestartOnCrash && !once,
                    _ => false
                };

                if (!shouldRestart)
                {
                    Log.Warn($"Fin de boucle : {result}");
                    break;
                }

                Log.Warn($"Restart dans {config.RestartDelaySeconds}s ({result})...");
                await Task.Delay(TimeSpan.FromSeconds(config.RestartDelaySeconds), shutdown.Token);
                run++;
            }

            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error(ex.Message);
            return 1;
        }
    }

    private static string? GetArgValue(string[] args, string key)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    [SupportedOSPlatform("windows")]
    private static void WarnIfNotAdmin()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
        {
            Log.Warn("Lance l'exe en administrateur si l'injection DLL échoue.");
        }
    }

    internal static string DefaultLauncherRootPath => DefaultLauncherRoot;
    internal static string DefaultFortniteRootPath => DefaultFortniteRoot;
}

internal sealed class FortniteAutoHost : IAsyncDisposable
{
    private const string ShippingExe = "FortniteClient-Win64-Shipping.exe";
    private const string LauncherExe = "FortniteLauncher.exe";
    private const string EacExe = "FortniteClient-Win64-Shipping_EAC.exe";
    private const string AftermathDll = "GFSDK_Aftermath_Lib.dll";
    private const string DefaultBackendHost = "127.0.0.1";
    private const int DefaultBackendPort = 3551;
    private const int DefaultXmppPort = 80;

    private static readonly string[] LoggedInMarkers =
    [
        "[UOnlineAccountCommon::ContinueLoggingIn]",
        "(Completed)"
    ];

    private static readonly string[] CorruptedBuildErrors =
    [
        "Critical error",
        "when 0 bytes remain",
        "Pak chunk signature verification failed!",
        "LogWindows:Error: Fatal error!"
    ];

    private static readonly string[] CannotConnectErrors =
    [
        "port 3551 failed: Connection refused",
        "Unable to login to Fortnite servers",
        "HTTP 400 response from ",
        "Network failure when attempting to check platform restrictions",
        "UOnlineAccountCommon::ForceLogout"
    ];

    private readonly AutoHostConfig _config;
    private readonly object _gate = new();
    private readonly List<Process> _ownedProcesses = [];
    private readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    private FileInfo _shipping = null!;
    private FileInfo? _launcher;
    private FileInfo? _eac;
    private FileInfo _backendExe = null!;
    private FileInfo _authDll = null!;
    private FileInfo _gameServerDll = null!;
    private FileInfo? _memoryDll;
    private Process? _backendProcess;
    private Process? _gameProcess;
    private Process? _launcherProcess;
    private Process? _eacProcess;
    private TaskCompletionSource<SessionResult>? _session;
    private int _loggedInHandled;
    private bool _launched;

    public FortniteAutoHost(AutoHostConfig config)
    {
        _config = config;
    }

    public async Task PrepareAsync(CancellationToken token)
    {
        Log.Section("Préparation");

        _config.Normalize();
        await _config.SaveIfChangedAsync(token);

        var fortniteRoot = RequireDirectory(_config.FortniteRoot, "FortniteRoot");
        _shipping = RequireSingleFile(fortniteRoot, ShippingExe);
        _launcher = FindSingleFileOrNull(fortniteRoot, LauncherExe);
        _eac = FindSingleFileOrNull(fortniteRoot, EacExe);

        _backendExe = ResolveBackendExe();
        _authDll = ResolveDll(_config.AuthDll, "cobalt.dll", "AuthDll");
        _memoryDll = ResolveOptionalDll(_config.MemoryDll, "memory.dll");
        _gameServerDll = await ResolveGameServerDllAsync(token);

        if (_config.DeleteAftermathDll)
        {
            DeleteAftermathDlls(fortniteRoot);
        }

        UpdateMatchmakerConfig();

        Log.Info($"Fortnite : {_shipping.FullName}");
        Log.Info($"Backend  : {_backendExe.FullName}");
        Log.Info($"Auth DLL : {_authDll.FullName}");
        Log.Info($"GS DLL   : {_gameServerDll.FullName}");
        await _config.SaveIfChangedAsync(token);
    }

    public async Task StartBackendAsync(CancellationToken token)
    {
        Log.Section("Backend");

        if (await PingBackendAsync(token))
        {
            Log.Ok($"Backend déjà disponible sur {DefaultBackendHost}:{_config.BackendPort}.");
            return;
        }

        if (_config.KillBackendPortsBeforeStart)
        {
            WindowsProcess.KillTcpListeners(_config.BackendPort);
            if (_config.BackendPort == DefaultBackendPort)
            {
                WindowsProcess.KillTcpListeners(DefaultXmppPort);
            }
        }

        _backendProcess = StartProcess(_backendExe, [], _backendExe.DirectoryName, "BACKEND", redirectOutput: true);
        RegisterOwnedProcess(_backendProcess);
        _backendProcess.OutputDataReceived += (_, e) => Log.Line("BACKEND", e.Data);
        _backendProcess.ErrorDataReceived += (_, e) => Log.Line("BACKEND", e.Data, error: true);
        _backendProcess.BeginOutputReadLine();
        _backendProcess.BeginErrorReadLine();

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline && !token.IsCancellationRequested)
        {
            if (await PingBackendAsync(token))
            {
                Log.Ok("Backend prêt.");
                return;
            }

            await Task.Delay(1000, token);
        }

        throw new InvalidOperationException("Backend introuvable après démarrage. Vérifie lawinserver.exe et le port 3551.");
    }

    public async Task<SessionResult> RunOneSessionAsync(CancellationToken token)
    {
        StopGameProcesses();
        _session = new TaskCompletionSource<SessionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _loggedInHandled = 0;
        _launched = false;

        try
        {
            _launcherProcess = StartPausedOptional(_launcher, "LAUNCHER");
            _eacProcess = StartPausedOptional(_eac, "EAC");

            var gameArgs = BuildGameArgs();
            Log.Info("Args host : " + string.Join(" ", gameArgs));

            _gameProcess = StartProcess(
                _shipping,
                gameArgs,
                _shipping.DirectoryName,
                "GAME",
                redirectOutput: true,
                environment: new Dictionary<string, string> { ["OPENSSL_ia32cap"] = "~0x20000000" });

            RegisterOwnedProcess(_gameProcess);
            _gameProcess.OutputDataReceived += (_, e) => OnGameLine(e.Data, error: false);
            _gameProcess.ErrorDataReceived += (_, e) => OnGameLine(e.Data, error: true);
            _gameProcess.EnableRaisingEvents = true;
            _gameProcess.Exited += (_, _) => OnGameExit();
            _gameProcess.BeginOutputReadLine();
            _gameProcess.BeginErrorReadLine();

            await WindowsProcess.InjectDllAsync(_gameProcess.Id, _authDll, token);
            Log.Ok("cobalt.dll injecté.");

            using var registration = token.Register(() => TryComplete(SessionResult.Stopped));
            var result = await _session.Task;
            StopGameProcesses();
            return result;
        }
        catch
        {
            StopGameProcesses();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        StopGameProcesses();

        if (_config.StopBackendOnExit && _backendProcess is { HasExited: false })
        {
            KillProcess(_backendProcess);
        }

        _http.Dispose();
        await Task.CompletedTask;
    }

    private FileInfo ResolveBackendExe()
    {
        if (!string.IsNullOrWhiteSpace(_config.BackendExe) && File.Exists(_config.BackendExe))
        {
            return new FileInfo(_config.BackendExe);
        }

        var launcherRoot = RequireDirectory(_config.LauncherRoot, "LauncherRoot");
        var candidates = new[]
        {
            Path.Combine(launcherRoot.FullName, "gui", "assets", "backend", "lawinserver.exe"),
            Path.Combine(launcherRoot.FullName, "backend", "lawinserver.exe")
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                _config.BackendExe = candidate;
                return new FileInfo(candidate);
            }
        }

        throw new FileNotFoundException("lawinserver.exe introuvable dans le launcher.", candidates[0]);
    }

    private FileInfo ResolveDll(string? configured, string fallbackName, string label)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return new FileInfo(configured);
        }

        var launcherRoot = RequireDirectory(_config.LauncherRoot, "LauncherRoot");
        var candidate = Path.Combine(launcherRoot.FullName, "gui", "dependencies", "dlls", fallbackName);
        if (!File.Exists(candidate))
        {
            throw new FileNotFoundException($"{label} introuvable.", candidate);
        }

        if (label == "AuthDll")
        {
            _config.AuthDll = candidate;
        }

        return new FileInfo(candidate);
    }

    private FileInfo? ResolveOptionalDll(string? configured, string fallbackName)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return new FileInfo(configured);
        }

        var launcherRoot = RequireDirectory(_config.LauncherRoot, "LauncherRoot");
        var candidate = Path.Combine(launcherRoot.FullName, "gui", "dependencies", "dlls", fallbackName);
        if (File.Exists(candidate))
        {
            _config.MemoryDll = candidate;
            return new FileInfo(candidate);
        }

        return null;
    }

    private async Task<FileInfo> ResolveGameServerDllAsync(CancellationToken token)
    {
        if (!string.IsNullOrWhiteSpace(_config.GameServerDll) && File.Exists(_config.GameServerDll))
        {
            return new FileInfo(_config.GameServerDll);
        }

        var dllName = GetVersionMajor() >= 20 ? "reboot_s20.dll" : "reboot.dll";
        var localDll = Path.Combine(AppContext.BaseDirectory, "dlls", dllName);
        if (File.Exists(localDll))
        {
            _config.GameServerDll = localDll;
            return new FileInfo(localDll);
        }

        if (!_config.AutoExtractFallbackRebootDll)
        {
            throw new FileNotFoundException("GameServerDll non configuré.");
        }

        var launcherRoot = RequireDirectory(_config.LauncherRoot, "LauncherRoot");
        var zipName = GetVersionMajor() >= 20 ? "RebootS20Fallback.zip" : "RebootFallback.zip";
        var zipPath = Path.Combine(launcherRoot.FullName, "gui", "dependencies", "dlls", zipName);
        if (!File.Exists(zipPath))
        {
            throw new FileNotFoundException($"Fallback {zipName} introuvable. Configure GameServerDll manuellement.", zipPath);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(localDll)!);
        await using var zipStream = File.OpenRead(zipPath);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
        var dllEntry = archive.Entries.FirstOrDefault(e => e.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
        if (dllEntry == null)
        {
            throw new InvalidOperationException($"{zipName} ne contient aucun DLL.");
        }

        dllEntry.ExtractToFile(localDll, overwrite: true);
        token.ThrowIfCancellationRequested();

        _config.GameServerDll = localDll;
        Log.Ok($"DLL gameserver extrait depuis {zipName}.");
        return new FileInfo(localDll);
    }

    private static DirectoryInfo RequireDirectory(string? path, string label)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"{label} invalide : {path}");
        }

        return new DirectoryInfo(path);
    }

    private static FileInfo RequireSingleFile(DirectoryInfo root, string fileName)
    {
        var files = EnumerateFilesSafe(root.FullName, fileName).Take(2).ToList();
        return files.Count switch
        {
            1 => new FileInfo(files[0]),
            0 => throw new FileNotFoundException($"{fileName} introuvable dans {root.FullName}."),
            _ => throw new InvalidOperationException($"Plusieurs {fileName} trouvés dans {root.FullName}. Mets FortniteRoot plus précisément.")
        };
    }

    private static FileInfo? FindSingleFileOrNull(DirectoryInfo root, string fileName)
    {
        var files = EnumerateFilesSafe(root.FullName, fileName).Take(2).ToList();
        return files.Count == 1 ? new FileInfo(files[0]) : null;
    }

    private static IEnumerable<string> EnumerateFilesSafe(string root, string fileName)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            IEnumerable<string> files = [];
            IEnumerable<string> dirs = [];

            try
            {
                files = Directory.EnumerateFiles(current, fileName, SearchOption.TopDirectoryOnly);
                dirs = Directory.EnumerateDirectories(current);
            }
            catch
            {
                // Ignore les dossiers protégés ou incomplets.
            }

            foreach (var file in files)
            {
                yield return file;
            }

            foreach (var dir in dirs)
            {
                pending.Push(dir);
            }
        }
    }

    private void DeleteAftermathDlls(DirectoryInfo fortniteRoot)
    {
        foreach (var file in EnumerateFilesSafe(fortniteRoot.FullName, AftermathDll))
        {
            try
            {
                File.Delete(file);
                Log.Info($"Supprimé : {file}");
            }
            catch
            {
                Log.Warn($"Impossible de supprimer {file}");
            }
        }
    }

    private void UpdateMatchmakerConfig()
    {
        var configFile = Path.Combine(_backendExe.DirectoryName!, "Config", "config.ini");
        if (!File.Exists(configFile))
        {
            Log.Warn("Config matchmaker introuvable, skip.");
            return;
        }

        var lines = File.ReadAllLines(configFile).ToList();
        SetIniValue(lines, "GameServer", "ip", _config.GameServerHost);
        SetIniValue(lines, "GameServer", "port", _config.GameServerPort.ToString());
        File.WriteAllLines(configFile, lines);
        Log.Ok($"Matchmaker -> {_config.GameServerHost}:{_config.GameServerPort}");
    }

    private static void SetIniValue(List<string> lines, string section, string key, string value)
    {
        var sectionHeader = $"[{section}]";
        var inSection = false;
        var sectionIndex = -1;

        for (var i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
            {
                inSection = trimmed.Equals(sectionHeader, StringComparison.OrdinalIgnoreCase);
                if (inSection)
                {
                    sectionIndex = i;
                }
                continue;
            }

            if (inSection && trimmed.StartsWith($"{key}=", StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = $"{key}={value}";
                return;
            }
        }

        if (sectionIndex == -1)
        {
            lines.Add("");
            lines.Add(sectionHeader);
            lines.Add($"{key}={value}");
        }
        else
        {
            lines.Insert(sectionIndex + 1, $"{key}={value}");
        }
    }

    private async Task<bool> PingBackendAsync(CancellationToken token)
    {
        try
        {
            using var response = await _http.GetAsync($"http://{DefaultBackendHost}:{_config.BackendPort}/unknown", token);
            return response.StatusCode != 0;
        }
        catch
        {
            return false;
        }
    }

    private Process? StartPausedOptional(FileInfo? executable, string label)
    {
        if (executable == null)
        {
            Log.Warn($"{label} introuvable, skip.");
            return null;
        }

        var process = StartProcess(executable, [], executable.DirectoryName, label, redirectOutput: false);
        RegisterOwnedProcess(process);
        WindowsProcess.Suspend(process.Id);
        Log.Ok($"{label} suspendu (pid {process.Id}).");
        return process;
    }

    private Process StartProcess(
        FileInfo executable,
        IReadOnlyCollection<string> args,
        string? workingDirectory,
        string label,
        bool redirectOutput,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable.FullName,
            WorkingDirectory = workingDirectory ?? executable.DirectoryName ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = redirectOutput,
            StandardOutputEncoding = redirectOutput ? Encoding.UTF8 : null,
            StandardErrorEncoding = redirectOutput ? Encoding.UTF8 : null
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        if (environment != null)
        {
            foreach (var (key, value) in environment)
            {
                startInfo.Environment[key] = value;
            }
        }

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Impossible de lancer {executable.FullName}");
        Log.Ok($"{label} lancé (pid {process.Id}).");
        return process;
    }

    private List<string> BuildGameArgs()
    {
        var username = _config.Username;
        var password = _config.Password;
        if (string.IsNullOrWhiteSpace(password))
        {
            username = $"{ParseUsername(username)}@projectreboot.dev";
            password = "Rebooted";
        }

        var args = new OrderedDictionary(StringComparer.OrdinalIgnoreCase)
        {
            ["-epicapp"] = "Fortnite",
            ["-epicenv"] = "Prod",
            ["-epiclocale"] = "en-us",
            ["-epicportal"] = "",
            ["-skippatchcheck"] = "",
            ["-nobe"] = "",
            ["-fromfl"] = "eac",
            ["-fltoken"] = "3db3ba5dcbd2e16703f3978d",
            ["-caldera"] = "eyJhbGciOiJFUzI1NiIsInR5cCI6IkpXVCJ9.eyJhY2NvdW50X2lkIjoiYmU5ZGE1YzJmYmVhNDQwN2IyZjQwZWJhYWQ4NTlhZDQiLCJnZW5lcmF0ZWQiOjE2Mzg3MTcyNzgsImNhbGRlcmFHdWlkIjoiMzgxMGI4NjMtMmE2NS00NDU3LTliNTgtNGRhYjNiNDgyYTg2IiwiYWNQcm92aWRlciI6IkVhc3lBbnRpQ2hlYXQiLCJub3RlcyI6IiIsImZhbGxiYWNrIjpmYWxzZX0.VAWQB67RTxhiWOxx7DBjnzDnXyyEnX7OljJm-j2d88G_WgwQ9wrE6lwMEHZHjBd1ISJdUO1UVUqkfLdU5nofBQ",
            ["-AUTH_LOGIN"] = username,
            ["-AUTH_PASSWORD"] = password,
            ["-AUTH_TYPE"] = "epic",
            ["-nosplash"] = "",
            ["-nosound"] = ""
        };

        if (_config.Headless)
        {
            args["-nullrhi"] = "";
        }

        if (_config.EnableGameLogArg)
        {
            args["-log"] = "";
        }

        foreach (var additionalArg in SplitCommandLine(_config.AdditionalArgs))
        {
            var separator = additionalArg.IndexOf('=');
            var key = separator == -1 ? additionalArg : additionalArg[..separator];
            var value = separator == -1 || separator + 1 >= additionalArg.Length ? "" : additionalArg[(separator + 1)..];
            if (!string.IsNullOrWhiteSpace(key))
            {
                args[key] = value;
            }
        }

        var result = new List<string>();
        foreach (DictionaryEntry entry in args)
        {
            var key = (string)entry.Key;
            var value = (string)entry.Value!;
            result.Add(string.IsNullOrEmpty(value) ? key : $"{key}={value}");
        }

        return result;
    }

    private static string ParseUsername(string username)
    {
        var parsed = Regex.Replace(username, "[^A-Za-z0-9]", "").Trim();
        return string.IsNullOrWhiteSpace(parsed) ? "Host" : parsed;
    }

    private static IEnumerable<string> SplitCommandLine(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            yield break;
        }

        var builder = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < commandLine.Length; i++)
        {
            var c = commandLine[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (builder.Length > 0)
                {
                    yield return builder.ToString();
                    builder.Clear();
                }

                continue;
            }

            builder.Append(c);
        }

        if (builder.Length > 0)
        {
            yield return builder.ToString();
        }
    }

    private void OnGameLine(string? line, bool error)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        Log.Line(error ? "GAME ERR" : "GAME", line, error);

        if (line.Contains("FOnlineSubsystemGoogleCommon::Shutdown()", StringComparison.OrdinalIgnoreCase))
        {
            TryComplete(SessionResult.Stopped);
            return;
        }

        if (CorruptedBuildErrors.Any(marker => line.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            TryComplete(_launched ? SessionResult.Crashed : SessionResult.Failed);
            return;
        }

        if (CannotConnectErrors.Any(marker => line.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            TryComplete(SessionResult.Failed);
            return;
        }

        if (LoggedInMarkers.All(marker => line.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            _ = Task.Run(HandleLoggedInAsync);
            return;
        }

        if (line.Contains("TeamsLeft: 1", StringComparison.OrdinalIgnoreCase))
        {
            TryComplete(SessionResult.MatchEnded);
        }
    }

    private async Task HandleLoggedInAsync()
    {
        if (Interlocked.Exchange(ref _loggedInHandled, 1) == 1)
        {
            return;
        }

        try
        {
            var process = _gameProcess ?? throw new InvalidOperationException("Process Fortnite absent.");
            _launched = true;
            Log.Ok("Login détecté.");

            if (_config.InjectMemoryDllBeforeSeason10 && GetVersionMajor() < 10 && _memoryDll != null)
            {
                await WindowsProcess.InjectDllAsync(process.Id, _memoryDll, CancellationToken.None);
                Log.Ok("memory.dll injecté.");
            }

            if (_config.KillGameServerPortBeforeInject)
            {
                WindowsProcess.KillTcpListeners(_config.GameServerPort);
            }

            await WindowsProcess.InjectDllAsync(process.Id, _gameServerDll, CancellationToken.None);
            Log.Ok("DLL gameserver injecté.");

            var ready = await PingGameServerUntilReadyAsync(TimeSpan.FromMinutes(2), CancellationToken.None);
            if (ready)
            {
                Log.Ok($"Gameserver prêt sur {_config.GameServerHost}:{_config.GameServerPort}.");
            }
            else
            {
                Log.Warn("DLL injecté, mais le ping UDP local n'a pas répondu dans le délai.");
            }
        }
        catch (Exception ex)
        {
            Log.Error("Erreur injection host : " + ex.Message);
            TryComplete(SessionResult.Failed);
        }
    }

    private void OnGameExit()
    {
        if (_session?.Task.IsCompleted == true)
        {
            return;
        }

        Log.Warn("Fortnite s'est fermé.");
        TryComplete(_launched ? SessionResult.Crashed : SessionResult.Failed);
    }

    private async Task<bool> PingGameServerUntilReadyAsync(TimeSpan timeout, CancellationToken token)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !token.IsCancellationRequested)
        {
            if (await PingGameServerAsync(token))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromSeconds(5), token);
        }

        return false;
    }

    private async Task<bool> PingGameServerAsync(CancellationToken token)
    {
        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork);
            var packet = Convert.FromBase64String("AQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABA==");
            await udp.SendAsync(packet, packet.Length, _config.GameServerHost, _config.GameServerPort);
            var receive = udp.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5), token);
            await receive;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private int GetVersionMajor()
    {
        if (!string.IsNullOrWhiteSpace(_config.GameVersion))
        {
            var match = Regex.Match(_config.GameVersion, @"(?<major>\d+)(?:\.|_)\d+");
            if (match.Success && int.TryParse(match.Groups["major"].Value, out var explicitMajor))
            {
                return explicitMajor;
            }
        }

        var candidates = new[]
        {
            _config.FortniteRoot,
            _shipping?.FullName,
            Path.GetFileName(_config.FortniteRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        };

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var match = Regex.Match(candidate, @"(?<!\d)(?<major>\d{1,2})(?:\.|_)(?<minor>\d{1,2})(?!\d)");
            if (match.Success && int.TryParse(match.Groups["major"].Value, out var major))
            {
                return major;
            }
        }

        return 0;
    }

    private void TryComplete(SessionResult result)
    {
        _session?.TrySetResult(result);
    }

    private void RegisterOwnedProcess(Process process)
    {
        lock (_gate)
        {
            _ownedProcesses.Add(process);
        }
    }

    private void StopGameProcesses()
    {
        KillProcess(_gameProcess);
        KillProcess(_launcherProcess);
        KillProcess(_eacProcess);
        _gameProcess = null;
        _launcherProcess = null;
        _eacProcess = null;
    }

    private static void KillProcess(Process? process)
    {
        if (process == null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch
        {
            // Process déjà mort ou inaccessible.
        }
    }
}

internal enum SessionResult
{
    MatchEnded,
    Crashed,
    Failed,
    Stopped
}

internal sealed class AutoHostConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private string? _path;
    private string? _serializedAtLoad;

    public string FortniteRoot { get; set; } = Program.DefaultFortniteRootPath;
    public string LauncherRoot { get; set; } = Program.DefaultLauncherRootPath;
    public string? GameVersion { get; set; } = "24.20";
    public string? BackendExe { get; set; }
    public string? AuthDll { get; set; }
    public string? GameServerDll { get; set; }
    public string? MemoryDll { get; set; }
    public string Username { get; set; } = "Host";
    public string Password { get; set; } = "";
    public string GameServerHost { get; set; } = "127.0.0.1";
    public int GameServerPort { get; set; } = 7777;
    public int BackendPort { get; set; } = 3551;
    public bool Headless { get; set; } = true;
    public bool EnableGameLogArg { get; set; } = true;
    public bool AutoRestart { get; set; } = true;
    public bool RestartOnCrash { get; set; } = true;
    public int RestartDelaySeconds { get; set; } = 10;
    public bool StopBackendOnExit { get; set; } = true;
    public bool KillBackendPortsBeforeStart { get; set; } = true;
    public bool KillGameServerPortBeforeInject { get; set; } = true;
    public bool InjectMemoryDllBeforeSeason10 { get; set; } = true;
    public bool DeleteAftermathDll { get; set; } = true;
    public bool AutoExtractFallbackRebootDll { get; set; } = true;
    public string AdditionalArgs { get; set; } = "";

    public static async Task<AutoHostConfig> LoadOrCreateAsync(string path)
    {
        AutoHostConfig config;
        if (File.Exists(path))
        {
            var json = await File.ReadAllTextAsync(path);
            config = JsonSerializer.Deserialize<AutoHostConfig>(json, JsonOptions) ?? new AutoHostConfig();
        }
        else
        {
            config = new AutoHostConfig();
            config.Normalize();
            var json = JsonSerializer.Serialize(config, JsonOptions);
            await File.WriteAllTextAsync(path, json);
            Log.Ok($"Config créée : {Path.GetFullPath(path)}");
        }

        config._path = path;
        config._serializedAtLoad = JsonSerializer.Serialize(config, JsonOptions);
        return config;
    }

    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(FortniteRoot) || !Directory.Exists(FortniteRoot))
        {
            var detected = DetectFortniteRoot();
            if (detected != null)
            {
                FortniteRoot = detected;
            }
        }

        if (string.IsNullOrWhiteSpace(LauncherRoot))
        {
            LauncherRoot = Program.DefaultLauncherRootPath;
        }

        if (string.IsNullOrWhiteSpace(Username))
        {
            Username = "Host";
        }

        if (GameServerPort <= 0)
        {
            GameServerPort = 7777;
        }

        if (BackendPort <= 0)
        {
            BackendPort = 3551;
        }

        if (RestartDelaySeconds < 0)
        {
            RestartDelaySeconds = 10;
        }
    }

    public async Task SaveIfChangedAsync(CancellationToken token)
    {
        if (_path == null)
        {
            return;
        }

        var serialized = JsonSerializer.Serialize(this, JsonOptions);
        if (!string.Equals(serialized, _serializedAtLoad, StringComparison.Ordinal))
        {
            await File.WriteAllTextAsync(_path, serialized, token);
            _serializedAtLoad = serialized;
        }
    }

    private static string? DetectFortniteRoot()
    {
        var user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roots = new[]
        {
            Path.Combine(user, "Downloads"),
            Path.Combine(user, "Desktop")
        };

        foreach (var root in roots.Where(Directory.Exists))
        {
            try
            {
                var file = Directory.EnumerateFiles(root, "FortniteClient-Win64-Shipping.exe", SearchOption.AllDirectories).FirstOrDefault();
                if (file != null)
                {
                    return Path.GetDirectoryName(file);
                }
            }
            catch
            {
                // Ignore.
            }
        }

        return null;
    }
}

internal static partial class WindowsProcess
{
    private const uint ProcessCreateThread = 0x0002;
    private const uint ProcessVmOperation = 0x0008;
    private const uint ProcessVmWrite = 0x0020;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessSuspendResume = 0x0800;
    private const uint MemCommitReserve = 0x3000;
    private const uint PageReadWrite = 0x04;

    public static void Suspend(int pid)
    {
        var handle = OpenProcess(ProcessSuspendResume, false, pid);
        if (handle == IntPtr.Zero)
        {
            Log.Warn($"Suspend impossible pour pid {pid}.");
            return;
        }

        try
        {
            _ = NtSuspendProcess(handle);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public static async Task InjectDllAsync(int pid, FileInfo dll, CancellationToken token)
    {
        if (!dll.Exists)
        {
            throw new FileNotFoundException("DLL introuvable.", dll.FullName);
        }

        await Task.Run(() => InjectDll(pid, dll.FullName), token);
    }

    public static void KillTcpListeners(int port)
    {
        foreach (var pid in NetstatTcpListeners(port).Distinct())
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                Log.Warn($"Kill port TCP {port} -> {process.ProcessName} ({pid})");
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Ignore.
            }
        }
    }

    private static IEnumerable<int> NetstatTcpListeners(int port)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "netstat.exe",
                Arguments = "-ano -p tcp",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                yield break;
            }

            while (!process.StandardOutput.EndOfStream)
            {
                var line = process.StandardOutput.ReadLine();
                if (line == null ||
                    !line.Contains("LISTENING", StringComparison.OrdinalIgnoreCase) ||
                    !Regex.IsMatch(line, $@":{port}\s"))
                {
                    continue;
                }

                var parts = Regex.Split(line.Trim(), @"\s+");
                if (parts.Length > 0 && int.TryParse(parts[^1], out var pid))
                {
                    yield return pid;
                }
            }

            process.WaitForExit(2000);
        }
        finally
        {
            // Iterator cleanup only.
        }
    }

    private static void InjectDll(int pid, string dllPath)
    {
        var access = ProcessCreateThread | ProcessVmOperation | ProcessVmWrite | ProcessQueryInformation;
        var process = OpenProcess(access, false, pid);
        if (process == IntPtr.Zero)
        {
            throw new InvalidOperationException($"OpenProcess échoue pour pid {pid}: {Marshal.GetLastWin32Error()}");
        }

        try
        {
            var kernel32 = GetModuleHandle("kernel32.dll");
            var loadLibrary = GetProcAddress(kernel32, "LoadLibraryW");
            if (loadLibrary == IntPtr.Zero)
            {
                throw new InvalidOperationException("LoadLibraryW introuvable.");
            }

            var bytes = Encoding.Unicode.GetBytes(dllPath + "\0");
            var remote = VirtualAllocEx(process, IntPtr.Zero, (nuint)bytes.Length, MemCommitReserve, PageReadWrite);
            if (remote == IntPtr.Zero)
            {
                throw new InvalidOperationException($"VirtualAllocEx échoue: {Marshal.GetLastWin32Error()}");
            }

            if (!WriteProcessMemory(process, remote, bytes, bytes.Length, out _))
            {
                throw new InvalidOperationException($"WriteProcessMemory échoue: {Marshal.GetLastWin32Error()}");
            }

            var thread = CreateRemoteThread(process, IntPtr.Zero, 0, loadLibrary, remote, 0, IntPtr.Zero);
            if (thread == IntPtr.Zero)
            {
                throw new InvalidOperationException($"CreateRemoteThread échoue: {Marshal.GetLastWin32Error()}");
            }

            WaitForSingleObject(thread, 10000);
            CloseHandle(thread);
        }
        finally
        {
            CloseHandle(process);
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr hObject);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial IntPtr GetModuleHandle(string lpModuleName);

    [LibraryImport("kernel32.dll", EntryPoint = "GetProcAddress", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr lpAddress, nuint dwSize, uint flAllocationType, uint flProtect);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int nSize, out nuint lpNumberOfBytesWritten);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr CreateRemoteThread(
        IntPtr hProcess,
        IntPtr lpThreadAttributes,
        nuint dwStackSize,
        IntPtr lpStartAddress,
        IntPtr lpParameter,
        uint dwCreationFlags,
        IntPtr lpThreadId);

    [LibraryImport("kernel32.dll")]
    private static partial uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [LibraryImport("ntdll.dll")]
    private static partial int NtSuspendProcess(IntPtr processHandle);
}

internal static class Log
{
    public static void Section(string message)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine();
        Console.WriteLine("== " + message);
        Console.ResetColor();
    }

    public static void Info(string message)
    {
        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine("[INFO] " + message);
        Console.ResetColor();
    }

    public static void Ok(string message)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("[OK] " + message);
        Console.ResetColor();
    }

    public static void Warn(string message)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("[WARN] " + message);
        Console.ResetColor();
    }

    public static void Error(string message)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine("[ERR] " + message);
        Console.ResetColor();
    }

    public static void Line(string label, string? line, bool error = false)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        Console.ForegroundColor = error ? ConsoleColor.DarkYellow : ConsoleColor.DarkGray;
        Console.WriteLine($"[{label}] {line}");
        Console.ResetColor();
    }
}
